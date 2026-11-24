using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    /// <summary>What kind of work a call does: picks the colour on the bar and on the model.</summary>
    internal enum ActionKind { Read, Create, Modify, Delete, File, View }

    internal enum StepState { Running, Ok, Failed, Refused }

    /// <summary>One tool call as the Agent Cursors bar shows it.</summary>
    internal sealed class AgentStep
    {
        public string Agent, Tool, Caption, Error;
        public ActionKind Action;
        public DateTime Started = DateTime.Now;
        public double Seconds;
        public StepState State = StepState.Running;
        public string Document = "";                                   // title of the model it ran in
        public readonly List<long> Named = new List<long>();           // ids from the arguments and the result
        public readonly List<long> Created = new List<long>(), Modified = new List<long>();
        public readonly List<BoundingBoxXYZ> DeletedBoxes = new List<BoundingBoxXYZ>(); // taken before the call ran
        public int DeletedCount;
        public int Changed => Created.Count + Modified.Count + DeletedCount;
    }

    internal sealed class AgentInfo
    {
        public string Key, Name;
        public System.Windows.Media.Color Color;
        public string Goal;
        public List<string> Tasks = new List<string>();
        public int Step;
        public bool PlanDone;
        public AgentStep Current;
        public DateTime LastSeen = DateTime.Now;
        public int Calls, Changes;
    }

    /// <summary>
    /// Agent Cursors for Revit: a live view of what each AI app does in the model through MCP (after the T3Lab pyRevit
    /// feature of the same name). The bar (CursorUi.Hud) lists each agent, its goal and tasks (t3lab_narrate), what runs
    /// now and the last steps, with Pause; "Show on the model" also moves a cursor per agent over the Revit view and frames
    /// what a call created, changed or is about to delete. Changes are taken from Revit's DocumentChanged event.
    /// Off by default (ribbon T3Lab MCP > Agent Cursors). Begin / End run on the socket thread and only record; every UI
    /// update goes to the bar's own thread and can never fail a tool call.
    /// </summary>
    internal static class AgentCursors
    {
        public const string NarrateTool = "t3lab_narrate";
        const int FeedSize = 20, PlanMax = 8, TaskMax = 60, GoalMax = 90, MaxCapture = 200, MaxTouched = 5000, MaxBoxes = 12;

        static readonly object Gate = new object();
        static readonly Dictionary<string, AgentInfo> Agents = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);
        static readonly List<AgentStep> Feed = new List<AgentStep>();
        static readonly Dictionary<string, HashSet<long>> Touched = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        static AgentStep _capturing;     // main thread only

        public static volatile bool Enabled, Paused, ShowOnModel, Follow;

        [ThreadStatic] public static string CurrentAgent;

        static string SettingsFile => Path.Combine(Log.DataDir, "agent_cursors.json");

        // ---------------------------------------------------------------- settings

        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var a = new Args(Json.Parse(File.ReadAllText(SettingsFile)));
                    Enabled = a.Bool("enabled", false);
                    ShowOnModel = a.Bool("show_on_model", true);
                    Follow = a.Bool("follow", false);
                }
                else ShowOnModel = true;
            }
            catch (Exception ex) { Log.Error("agent cursors settings: " + ex.Message); }
            if (Enabled) Refresh(true);
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile, Json.Write(new Dictionary<string, object>
                {
                    ["enabled"] = Enabled,
                    ["show_on_model"] = ShowOnModel,
                    ["follow"] = Follow,
                }));
            }
            catch (Exception ex) { Log.Error("agent cursors settings: " + ex.Message); }
        }

        /// <summary>Ribbon: on; the bar again when it was closed; off.</summary>
        public static string Toggle()
        {
            if (Enabled && !CursorUi.HudVisible)
            {
                Refresh(true);
                return "T3Lab Agent Cursors: bar shown.";
            }
            SetEnabled(!Enabled);
            return Enabled ? "T3Lab Agent Cursors: on." : "T3Lab Agent Cursors: off.";
        }

        public static void SetEnabled(bool on)
        {
            Enabled = on;
            if (!on) Paused = false;
            Save();
            if (on) Refresh(true);
            else CursorUi.HideAll();
        }

        public static void SetOption(string name, bool value)
        {
            if (name == "show_on_model") { ShowOnModel = value; if (!value) CursorUi.ClearModel(); }
            else if (name == "follow") Follow = value;
            Save();
            Refresh(false);
        }

        public static void SetPaused(bool paused)
        {
            Paused = paused;
            Refresh(false);
        }

        public static void Shutdown()
        {
            CursorUi.Shutdown();
            Paused = false;
            lock (Gate)
            {
                Agents.Clear();
                Feed.Clear();
                Touched.Clear();
            }
        }

        public static void ForgetDocument(string title)
        {
            if (string.IsNullOrEmpty(title)) return;
            lock (Gate)
            {
                Touched.Remove(title);
                foreach (AgentStep s in Feed.Where(x => string.Equals(x.Document, title, StringComparison.OrdinalIgnoreCase)))
                {
                    s.Named.Clear(); s.Created.Clear(); s.Modified.Clear(); s.DeletedBoxes.Clear();
                }
            }
        }

        // ---------------------------------------------------------------- agents

        static readonly System.Windows.Media.Color[] Palette =
        {
            Rgb(0xD9, 0x77, 0x57), Rgb(0x0D, 0x99, 0xFF), Rgb(0x14, 0xAE, 0x5C), Rgb(0xA2, 0x59, 0xFF),
            Rgb(0xE8, 0x46, 0x7C), Rgb(0x00, 0xB5, 0xCE), Rgb(0xFF, 0xC7, 0x00), Rgb(0xFF, 0x72, 0x62),
        };

        internal static System.Windows.Media.Color Rgb(byte r, byte g, byte b) => System.Windows.Media.Color.FromRgb(r, g, b);

        /// <summary>Display name and colour of an AI app from its MCP clientInfo.name.</summary>
        static AgentInfo AgentFor(string rawName)
        {
            string key = string.IsNullOrWhiteSpace(rawName) ? "ai" : rawName.Trim();
            if (Agents.TryGetValue(key, out AgentInfo known)) return known;
            string lower = key.ToLowerInvariant();
            string name;
            System.Windows.Media.Color color;
            if (lower.Contains("claude-code") || lower == "claude code") { name = "Claude Code"; color = Palette[0]; }
            else if (lower.Contains("claude")) { name = "Claude"; color = Palette[0]; }
            else if (lower.Contains("cursor")) { name = "Cursor"; color = Palette[3]; }
            else if (lower.Contains("codex") || lower.Contains("openai") || lower.Contains("chatgpt")) { name = "ChatGPT"; color = Palette[2]; }
            else if (lower.Contains("gemini") || lower.Contains("antigravity")) { name = "Gemini"; color = Palette[1]; }
            else if (lower.Contains("copilot") || lower.Contains("vscode") || lower.Contains("visual studio")) { name = "Copilot"; color = Palette[5]; }
            else if (lower.Contains("windsurf") || lower.Contains("cascade")) { name = "Windsurf"; color = Palette[4]; }
            else if (key == "ai") { name = "AI"; color = Palette[1]; }
            else
            {
                name = key.Length > 16 ? key.Substring(0, 16) : key;
                color = Palette[(key.GetHashCode() & 0x7FFFFFFF) % Palette.Length];
            }
            var info = new AgentInfo { Key = key, Name = name, Color = color };
            Agents[key] = info;
            return info;
        }

        public static string ClientName(object raw)
        {
            if (raw is Dictionary<string, object> d) return new Args(d).Str("name");
            return raw as string;
        }

        // ---------------------------------------------------------------- calls (socket thread)

        public static AgentStep Begin(ToolDef def, Args args, string client)
        {
            if (!Enabled || def.Name == NarrateTool) return null;
            var step = new AgentStep
            {
                Tool = def.Name,
                Action = ActionOf(def, args),
                Caption = Describe(def, args),
                Document = Entry.ActiveDocument,
            };
            AddIds(step.Named, args.Raw("element_ids"));
            lock (Gate)
            {
                AgentInfo agent = AgentFor(client);
                step.Agent = agent.Key;
                agent.Current = step;
                agent.LastSeen = DateTime.Now;
                agent.Calls++;
            }
            Refresh(false);
            return step;
        }

        static void AddIds(List<long> into, object raw)
        {
            if (raw is IList list && !(raw is string))
            {
                foreach (object o in list)
                {
                    if (into.Count >= MaxCapture) return;
                    if (TryLong(o, out long v)) into.Add(v);
                }
            }
            else if (raw != null && TryLong(raw, out long one)) into.Add(one);
        }

        static bool TryLong(object o, out long v)
        {
            v = 0;
            try
            {
                if (o is string s) return long.TryParse(s, out v);
                if (o is IConvertible) { v = Convert.ToInt64(o); return true; }
            }
            catch (Exception) { }
            return false;
        }

        public const string PausedMessage =
            "The user paused AI changes in Revit (T3Lab Agent Cursors bar). Read-only tools still work; ask the user to press " +
            "Resume on the bar, then try again.";

        static readonly HashSet<string> SafeWhilePaused = new HashSet<string>
        {
            NarrateTool, "set_active_view", "set_selection", "export_image", "reload_addin",
        };

        /// <summary>While paused: refuse anything that changes the model or writes files. Never throws.</summary>
        public static bool Blocks(ToolDef def, Args args)
        {
            try
            {
                if (!Enabled || !Paused || SafeWhilePaused.Contains(def.Name)) return false;
                if (def.Kind == ToolKind.Read) return def.WritesFiles && !string.IsNullOrEmpty(args.Str("csv_path"));
                if ((def.Name == "delete_elements" || def.Name == "rename_elements" || def.Name == "edit_text_notes") && args.Bool("dry_run", false)) return false;
                if (def.Name == "purge_unused" && args.Bool("dry_run", true)) return false;
                return true;
            }
            catch (Exception) { return def.Kind != ToolKind.Read; }
        }

        public static void End(AgentStep step, bool ok, bool refused, string error, object result)
        {
            if (step == null) return;
            step.Seconds = (DateTime.Now - step.Started).TotalSeconds;
            step.State = refused ? StepState.Refused : ok ? StepState.Ok : StepState.Failed;
            step.Error = error;
            if (ok && result != null) CollectResultIds(result, step.Named, 0);
            AgentInfo agent;
            lock (Gate)
            {
                agent = Agents.TryGetValue(step.Agent, out AgentInfo a) ? a : null;
                if (agent != null)
                {
                    if (agent.Current == step) agent.Current = null;
                    agent.LastSeen = DateTime.Now;
                    agent.Changes += step.Changed;
                }
                Feed.Insert(0, step);
                if (Feed.Count > FeedSize) Feed.RemoveAt(Feed.Count - 1);
                if (step.State == StepState.Ok && !string.IsNullOrEmpty(step.Document) && IsChange(step))
                {
                    if (!Touched.TryGetValue(step.Document, out HashSet<long> set))
                        Touched[step.Document] = set = new HashSet<long>();
                    foreach (long id in step.Created.Concat(step.Modified).Concat(step.Named))
                        if (set.Count < MaxTouched) set.Add(id);
                }
            }
            Refresh(false);
            if (ShowOnModel && agent != null && (step.State == StepState.Ok || step.DeletedBoxes.Count > 0))
                Entry.Main?.Post(app => ShowOnModelNow(app, agent, step));
        }

        static bool IsChange(AgentStep s) => s.Action != ActionKind.Read && s.Action != ActionKind.View;

        public static void Narrate(string client, string goal, List<string> tasks, int step, bool done)
        {
            lock (Gate)
            {
                AgentInfo agent = AgentFor(client);
                if (goal != null) agent.Goal = Clip(goal, GoalMax);
                if (tasks != null && tasks.Count > 0)
                {
                    agent.Tasks = tasks.Take(PlanMax).Select(t => Clip(t, TaskMax)).ToList();
                    agent.Step = 1;
                    agent.PlanDone = false;
                }
                if (step > 0) agent.Step = Math.Min(step, Math.Max(1, agent.Tasks.Count));
                if (done) { agent.PlanDone = true; agent.Step = agent.Tasks.Count; }
                agent.LastSeen = DateTime.Now;
            }
            Refresh(false);
        }

        static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        // ---------------------------------------------------------------- change capture (main thread)

        /// <summary>Before a call runs: arm DocumentChanged capture for writes, remember the boxes of what a delete / move names.</summary>
        public static void StartCapture(AgentStep step, ToolDef def, UIApplication app)
        {
            _capturing = null;
            if (step == null || def.Kind == ToolKind.Read) return;
            _capturing = step;
            try
            {
                Document doc = app.ActiveUIDocument?.Document;
                if (doc == null || (def.Name != "delete_elements" && def.Name != "transform_elements")) return;
                foreach (long id in step.Named.Take(MaxBoxes))
                {
                    BoundingBoxXYZ b = doc.GetElement(Id.Make(id))?.get_BoundingBox(null);
                    if (b != null) step.DeletedBoxes.Add(b);
                }
                if (def.Name == "transform_elements") step.DeletedBoxes.Clear(); // only deletes show "before" boxes
            }
            catch (Exception ex) { Log.Error("agent cursors before: " + ex.Message); }
        }

        public static void EndCapture(AgentStep step)
        {
            if (_capturing == step) _capturing = null;
        }

        /// <summary>Application.DocumentChanged: raised at each commit, on the main thread.</summary>
        public static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            AgentStep step = _capturing;
            if (step == null) return;
            try
            {
                Document doc = e.GetDocument();
                lock (Gate)
                {
                    foreach (ElementId id in e.GetAddedElementIds())
                    {
                        if (step.Created.Count >= MaxCapture) break;
                        if (doc.GetElement(id)?.Category != null) step.Created.Add(Id.V(id));
                    }
                    var created = new HashSet<long>(step.Created);
                    foreach (ElementId id in e.GetModifiedElementIds())
                    {
                        if (step.Modified.Count >= MaxCapture) break;
                        long v = Id.V(id);
                        Element el = doc.GetElement(id);
                        if (!created.Contains(v) && el?.Category != null && !(el is Autodesk.Revit.DB.View) && !step.Modified.Contains(v)) step.Modified.Add(v);
                    }
                    step.DeletedCount += e.GetDeletedElementIds().Count;
                }
            }
            catch (Exception ex) { Log.Error("agent cursors capture: " + ex.Message); }
        }

        // ---------------------------------------------------------------- captions

        static readonly Dictionary<string, ActionKind> Kinds = new Dictionary<string, ActionKind>
        {
            ["create_walls"] = ActionKind.Create, ["create_floor"] = ActionKind.Create, ["create_levels"] = ActionKind.Create,
            ["create_grids"] = ActionKind.Create, ["place_family_instance"] = ActionKind.Create, ["create_rooms"] = ActionKind.Create,
            ["load_family"] = ActionKind.Create, ["create_view"] = ActionKind.Create, ["create_sheets"] = ActionKind.Create,
            ["place_views_on_sheet"] = ActionKind.Create, ["create_annotation"] = ActionKind.Create, ["create_schedule"] = ActionKind.Create,
            ["delete_elements"] = ActionKind.Delete, ["purge_unused"] = ActionKind.Delete,
            ["place_line_based"] = ActionKind.Create, ["transfer_elements"] = ActionKind.Create, ["tag_all_in_view"] = ActionKind.Create,
            ["create_view_filter"] = ActionKind.Create, ["create_project_parameter"] = ActionKind.Create,
            ["manage_links"] = ActionKind.File, ["schedule_roundtrip"] = ActionKind.File, ["export_room_data"] = ActionKind.Read,
            ["manage_document"] = ActionKind.File, ["export_documents"] = ActionKind.File,
            ["set_active_view"] = ActionKind.View, ["set_selection"] = ActionKind.View, ["export_image"] = ActionKind.View,
        };

        static readonly Dictionary<string, string> Verbs = new Dictionary<string, string>
        {
            ["get_revit_context"] = "Checking Revit", ["query_elements"] = "Finding elements", ["get_element_info"] = "Reading elements",
            ["list_types"] = "Reading types", ["list_datums"] = "Reading levels and grids", ["list_views"] = "Reading views",
            ["list_sheets"] = "Reading sheets", ["get_schedule_data"] = "Reading a schedule", ["model_overview"] = "Checking the model",
            ["get_warnings"] = "Reading warnings", ["get_selection"] = "Reading the selection", ["export_image"] = "Looking at the view",
            ["set_active_view"] = "Opening a view", ["set_selection"] = "Selecting", ["set_parameter"] = "Setting parameters",
            ["create_walls"] = "Drawing walls", ["create_floor"] = "Drawing a floor", ["create_levels"] = "Adding levels",
            ["create_grids"] = "Adding grids", ["place_family_instance"] = "Placing families", ["create_rooms"] = "Placing rooms",
            ["load_family"] = "Loading a family", ["create_view"] = "Creating a view", ["create_sheets"] = "Creating sheets",
            ["place_views_on_sheet"] = "Placing views", ["create_annotation"] = "Annotating", ["create_schedule"] = "Creating a schedule",
            ["view_graphics"] = "Changing graphics", ["export_documents"] = "Exporting", ["manage_document"] = "Working on files",
            ["delete_elements"] = "Deleting", ["transform_elements"] = "Transforming",
            ["list_worksets"] = "Reading worksets", ["manage_worksets"] = "Changing worksets", ["set_element_workset"] = "Moving to a workset",
            ["list_links"] = "Reading links", ["manage_links"] = "Working on links", ["manage_groups"] = "Working on groups",
            ["list_materials"] = "Reading materials", ["manage_material"] = "Changing materials", ["get_material_quantities"] = "Counting materials",
            ["manage_revisions"] = "Working on revisions", ["project_storage"] = "Storing notes", ["manage_view_templates"] = "Working on view templates",
            ["list_view_filters"] = "Reading view filters", ["create_view_filter"] = "Adding a view filter", ["manage_view"] = "Changing a view",
            ["list_project_parameters"] = "Reading parameters", ["create_project_parameter"] = "Adding a parameter",
            ["purge_unused"] = "Purging", ["join_geometry"] = "Joining geometry", ["split_element"] = "Splitting",
            ["rehost_to_level"] = "Changing levels", ["rename_elements"] = "Renaming", ["tag_all_in_view"] = "Tagging",
            ["edit_text_notes"] = "Editing text notes", ["place_line_based"] = "Placing beams", ["transfer_elements"] = "Copying from another model",
            ["export_room_data"] = "Reading rooms", ["schedule_roundtrip"] = "Schedule round trip", ["send_code_to_revit"] = "Running Python",
        };

        static ActionKind ActionOf(ToolDef def, Args args)
        {
            if (def.Name == "transform_elements")
                return string.Equals(args.Str("operation"), "copy", StringComparison.OrdinalIgnoreCase) || args.Bool("copy", false) ? ActionKind.Create : ActionKind.Modify;
            if ((def.Name == "delete_elements" || def.Name == "rename_elements" || def.Name == "edit_text_notes") && args.Bool("dry_run", false)) return ActionKind.Read;
            if (def.Name == "purge_unused" && args.Bool("dry_run", true)) return ActionKind.Read;
            if (Kinds.TryGetValue(def.Name, out ActionKind k)) return k;
            switch (def.Kind)
            {
                case ToolKind.Read: return ActionKind.Read;
                case ToolKind.App: return ActionKind.File;
                default: return ActionKind.Modify;
            }
        }

        /// <summary>"Moving 3 elements", "Setting parameters · 12 elements", "Exporting PDF".</summary>
        static string Describe(ToolDef def, Args args)
        {
            int count = args.List("element_ids")?.Count ?? args.List("walls")?.Count ?? args.List("points")?.Count ?? args.List("items")?.Count ?? 0;
            string things = count == 1 ? "1 element" : count + " elements";
            switch (def.Name)
            {
                case "transform_elements":
                {
                    string op = (args.Str("operation") ?? "").ToLowerInvariant();
                    string verb = op == "move" ? "Moving" : op == "rotate" ? "Rotating" : op == "mirror" ? "Mirroring" : op == "copy" ? "Copying" : "Transforming";
                    return verb + " " + things;
                }
                case "export_documents": return "Exporting " + (args.Str("format") ?? "").ToUpperInvariant();
                case "manage_document":
                {
                    string act = (args.Str("action") ?? "").ToLowerInvariant();
                    return act == "open" ? "Opening a model" : act == "save" || act == "save_as" ? "Saving" : act == "sync" ? "Syncing with central"
                        : act == "close" ? "Closing a model" : act == "new" ? "Creating a model" : "Switching model";
                }
                case "delete_elements": return (args.Bool("dry_run", false) ? "Checking delete of " : "Deleting ") + things;
                case "set_parameter": return "Setting parameters" + (count > 0 ? " · " + things : "");
                case "set_active_view": return "Opening " + (args.Str("view") ?? "a view");
                case "send_code_to_revit": return string.IsNullOrWhiteSpace(args.Str("purpose")) ? "Running Python" : Clip(args.Str("purpose").Trim(), 60);
                case "purge_unused": return args.Bool("dry_run", true) ? "Checking unused" : "Purging unused";
            }
            if (Verbs.TryGetValue(def.Name, out string caption)) return caption;
            string human = def.Name.Replace('_', ' ');
            return char.ToUpperInvariant(human[0]) + human.Substring(1);
        }

        static readonly HashSet<string> IdKeys = new HashSet<string> { "id", "element_ids", "created_ids", "viewport_id" };

        static void CollectResultIds(object value, List<long> into, int depth)
        {
            if (depth > 4 || into.Count >= MaxCapture) return;
            switch (value)
            {
                case Dictionary<string, object> d:
                    if (d.TryGetValue("element_ids", out object ids)) { AddIds(into, ids); return; }
                    foreach (var kv in d)
                        if (kv.Value is IList && !(kv.Value is string)) CollectResultIds(kv.Value, into, depth + 1);
                    break;
                case IList list:
                    foreach (object o in list)
                    {
                        if (into.Count >= MaxCapture) return;
                        if (o is Dictionary<string, object> row && row.TryGetValue("id", out object id) && TryLong(id, out long v) && depth > 0) into.Add(v);
                        else CollectResultIds(o, into, depth + 1);
                    }
                    break;
            }
        }

        // ---------------------------------------------------------------- snapshot for the bar

        internal sealed class AgentView
        {
            public string Name, Status, Goal, Now;
            public System.Windows.Media.Color Color;
            public List<string> Tasks;
            public int Step, Calls, Changes;
            public bool PlanDone, Working;
            public ActionKind NowAction;
            public DateTime NowStarted;
        }

        internal sealed class StepView
        {
            public string Agent, Caption, Error;
            public System.Windows.Media.Color Color;
            public ActionKind Action;
            public StepState State;
            public double Seconds;
            public DateTime When;
            public int Changed;
        }

        internal sealed class View
        {
            public List<AgentView> Agents = new List<AgentView>();
            public List<StepView> Feed = new List<StepView>();
            public bool Paused, ShowOnModel, Follow;
            public int Touched;
        }

        internal static View Snapshot()
        {
            lock (Gate)
            {
                string active = Entry.ActiveDocument ?? "";
                var v = new View
                {
                    Paused = Paused, ShowOnModel = ShowOnModel, Follow = Follow,
                    Touched = Touched.TryGetValue(active, out HashSet<long> set) ? set.Count : 0,
                };
                foreach (AgentInfo a in Agents.Values.OrderByDescending(x => x.LastSeen))
                {
                    AgentStep cur = a.Current;
                    v.Agents.Add(new AgentView
                    {
                        Name = a.Name, Color = a.Color, Goal = a.Goal, Tasks = a.Tasks.ToList(), Step = a.Step, PlanDone = a.PlanDone,
                        Working = cur != null, Now = cur?.Caption, NowAction = cur?.Action ?? ActionKind.Read,
                        NowStarted = cur?.Started ?? DateTime.Now,
                        Status = Paused ? "Paused" : cur != null ? "Working" : (DateTime.Now - a.LastSeen).TotalSeconds < 15 ? "Thinking" : "Idle",
                        Calls = a.Calls, Changes = a.Changes,
                    });
                }
                foreach (AgentStep s in Feed)
                {
                    v.Feed.Add(new StepView
                    {
                        Agent = Agents.TryGetValue(s.Agent, out AgentInfo a) ? a.Name : s.Agent,
                        Color = a?.Color ?? Rgb(0x88, 0x88, 0x88),
                        Caption = s.Caption, Error = s.Error, Action = s.Action, State = s.State, Seconds = s.Seconds,
                        When = s.Started.AddSeconds(s.Seconds), Changed = s.Changed,
                    });
                }
                return v;
            }
        }

        static void Refresh(bool reveal)
        {
            if (!Enabled) return;
            try { CursorUi.ShowBar(Snapshot(), reveal); }
            catch (Exception ex) { Log.Error("agent cursors bar: " + ex.Message); }
        }

        // ---------------------------------------------------------------- on the model (main thread)

        /// <summary>Screen rectangles (physical px) of what a step touched, in every open view of the active model, then the cursor glides there.</summary>
        static void ShowOnModelNow(UIApplication app, AgentInfo agent, AgentStep step)
        {
            try
            {
                UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null || !string.Equals(uidoc.Document.Title, step.Document, StringComparison.OrdinalIgnoreCase)) return;
                Document doc = uidoc.Document;
                List<long> ids;
                lock (Gate) ids = step.Created.Concat(step.Modified).Concat(step.Named).Distinct().ToList();
                var elements = ids.Select(i => doc.GetElement(Id.Make(i))).Where(e => e != null).Take(MaxCapture).ToList();
                if (Follow && IsChange(step) && elements.Count > 0)
                {
                    try { uidoc.ShowElements(elements.Select(e => e.Id).ToList()); }
                    catch (Exception) { /* not shown in any view */ }
                }
                var frame = new CursorUi.Frame
                {
                    Agent = agent.Name, Color = agent.Color, Caption = step.Caption, Action = step.Action,
                    Failed = step.State != StepState.Ok, Change = IsChange(step),
                };
                foreach (UIView uiv in uidoc.GetOpenUIViews())
                {
                    if (!(doc.GetElement(uiv.ViewId) is Autodesk.Revit.DB.View view)) continue;
                    if (view is View3D v3 && v3.IsPerspective) continue;
                    if (view.ViewType == ViewType.Schedule || view.ViewType == ViewType.ProjectBrowser) continue;
                    Projector p = Projector.For(uiv, view);
                    if (p == null) continue;
                    frame.Views.Add(p.Rect);
                    foreach (Element e in elements.Take(MaxBoxes))
                    {
                        BoundingBoxXYZ b = null;
                        try { b = e.get_BoundingBox(view); } catch (Exception) { }
                        if (b == null) continue;
                        CursorUi.Box r = p.Project(b, Label(e), false);
                        if (r != null) frame.Boxes.Add(r);
                    }
                    foreach (BoundingBoxXYZ b in step.DeletedBoxes.Take(MaxBoxes))
                    {
                        CursorUi.Box r = p.Project(b, "deleted", true);
                        if (r != null) frame.Boxes.Add(r);
                    }
                }
                CursorUi.ShowFrame(frame);
            }
            catch (Exception ex) { Log.Error("agent cursors overlay: " + ex.Message); }
        }

        static string Label(Element e)
        {
            string cat = e.Category?.Name ?? e.GetType().Name;
            ElementType t = Rv.TypeElement(e);
            return t != null ? cat + " · " + t.Name : cat;
        }

        /// <summary>Model point -> screen pixel for one open, non-perspective view (zoom corners + window rectangle).</summary>
        sealed class Projector
        {
            XYZ _right, _up;
            double _u0, _u1, _v0, _v1;
            public CursorUi.Rect Rect;

            public static Projector For(UIView uiv, Autodesk.Revit.DB.View view)
            {
                IList<XYZ> c = uiv.GetZoomCorners();
                Rectangle w = uiv.GetWindowRectangle();
                if (c == null || c.Count < 2 || w.Right - w.Left < 20 || w.Bottom - w.Top < 20) return null;
                var p = new Projector { _right = view.RightDirection, _up = view.UpDirection };
                p._u0 = c[0].DotProduct(p._right); p._u1 = c[1].DotProduct(p._right);
                p._v0 = c[0].DotProduct(p._up); p._v1 = c[1].DotProduct(p._up);
                if (Math.Abs(p._u1 - p._u0) < 1e-9 || Math.Abs(p._v1 - p._v0) < 1e-9) return null;
                p.Rect = new CursorUi.Rect { L = w.Left, T = w.Top, R = w.Right, B = w.Bottom };
                return p;
            }

            void ToScreen(XYZ q, out double x, out double y)
            {
                x = Rect.L + (q.DotProduct(_right) - _u0) / (_u1 - _u0) * (Rect.R - Rect.L);
                y = Rect.B - (q.DotProduct(_up) - _v0) / (_v1 - _v0) * (Rect.B - Rect.T);
            }

            public CursorUi.Box Project(BoundingBoxXYZ b, string label, bool deleted)
            {
                Transform t = b.Transform ?? Transform.Identity;
                double l = double.MaxValue, top = double.MaxValue, r = double.MinValue, bot = double.MinValue;
                foreach (double x in new[] { b.Min.X, b.Max.X })
                    foreach (double y in new[] { b.Min.Y, b.Max.Y })
                        foreach (double z in new[] { b.Min.Z, b.Max.Z })
                        {
                            ToScreen(t.OfPoint(new XYZ(x, y, z)), out double sx, out double sy);
                            l = Math.Min(l, sx); r = Math.Max(r, sx); top = Math.Min(top, sy); bot = Math.Max(bot, sy);
                        }
                // clip to the view; nothing left = not on screen
                double cl = Math.Max(l, Rect.L), cr = Math.Min(r, Rect.R), ct = Math.Max(top, Rect.T), cb = Math.Min(bot, Rect.B);
                if (cr - cl < 1 || cb - ct < 1) return null;
                return new CursorUi.Box { L = cl, T = ct, R = cr, B = cb, Label = label, Deleted = deleted };
            }
        }

        // ---------------------------------------------------------------- bar buttons

        static List<ElementId> TouchedIds(Document doc)
        {
            List<long> ids;
            lock (Gate) ids = Touched.TryGetValue(doc.Title, out HashSet<long> set) ? set.ToList() : new List<long>();
            return ids.Select(Id.Make).Where(i => doc.GetElement(i) != null).ToList();
        }

        public static void SelectTouched(bool zoom)
        {
            Entry.Main?.Post(app =>
            {
                UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                List<ElementId> ids = TouchedIds(uidoc.Document);
                if (ids.Count == 0) { CursorUi.Toast("Nothing the AI changed is left in this model."); return; }
                uidoc.Selection.SetElementIds(ids);
                if (zoom)
                {
                    try { uidoc.ShowElements(ids); } catch (Exception) { }
                }
                CursorUi.Toast(ids.Count + " elements the AI changed are selected.");
            });
        }

        public static void ClearFeed()
        {
            lock (Gate)
            {
                Feed.Clear();
                Touched.Clear();
                foreach (AgentInfo a in Agents.Values) { a.Goal = null; a.Tasks.Clear(); a.Step = 0; a.Calls = 0; a.Changes = 0; }
            }
            CursorUi.ClearModel();
            Refresh(false);
        }

        // ---------------------------------------------------------------- t3lab_narrate

        public static void Register()
        {
            Registry.Add(NarrateTool, "Show goal and steps", ToolKind.Read,
                "Tell the user what you are doing in Revit: your goal and task list appear on the T3Lab Agent Cursors bar and the current " +
                "task is ticked off as you go. Call it at the start of multi-step work (goal + tasks), with step=N when you move to " +
                "task N, and done=true at the end. Changes nothing in the model.",
                Obj(P("goal", Str("What you are doing, one short line")),
                    P("tasks", Strs("The steps you plan, short (max 8)")),
                    P("step", Int("The task you are on now, 1-based")),
                    P("done", Bool("All tasks are finished"))),
                NarrateHandler, needsDocument: false, background: true);
        }

        static object NarrateHandler(ToolContext unused, Args a)
        {
            List<string> tasks = a.Has("tasks") ? a.Strs("tasks").Where(t => !string.IsNullOrWhiteSpace(t)).ToList() : null;
            Narrate(CurrentAgent, a.Str("goal"), tasks, a.Int("step", 0), a.Bool("done", false));
            var result = new Dictionary<string, object> { ["shown"] = Enabled };
            if (!Enabled) result["note"] = "Agent Cursors is off in Revit; the user turns it on with T3Lab MCP > Agent Cursors on the ribbon.";
            return result;
        }
    }
}
