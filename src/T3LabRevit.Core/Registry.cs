using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace T3LabRevit.Core
{
    internal enum ToolKind
    {
        /// <summary>Reads only: no transaction.</summary>
        Read,
        /// <summary>Changes the model: one Transaction = one Undo step; warnings dismissed, errors roll back.</summary>
        Write,
        /// <summary>Application level (documents, active view, selection): no transaction.</summary>
        App,
    }

    /// <summary>
    /// Which models a tool can work in. Any: the active one, another open one or one opened in the background (pinned with
    /// revit_document). Active: the active model only (it uses the UI: selection, the active view, the Python engine).
    /// scripts/check_tool_scopes.py proves that no tool declared Any reaches the UI side.
    /// </summary>
    internal enum ToolScope { Any, Active }

    internal delegate object ToolHandler(ToolContext ctx, Args args);

    internal sealed class ToolContext
    {
        public UIApplication App;
        /// <summary>Null when the call works in a model that is not the active one (pinned with revit_document).</summary>
        public UIDocument UiDoc;
        public Document Doc;
        /// <summary>Revit warnings dismissed during the call's transaction (reported back to the AI).</summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>The model's active view, or null in a model that is not the active one (tools then need a view argument).</summary>
        public View ActiveView => UiDoc?.ActiveView;

        /// <summary>The UI of the model, for a branch of a tool that needs it; elsewhere a clear refusal, never a null.</summary>
        public UIDocument RequireUi(string what) =>
            UiDoc ?? throw new NeedsActiveException($"{what} needs the active model: '{Doc?.Title}' is not active in Revit.");

        /// <summary>The user's selection in the active model.</summary>
        public ICollection<ElementId> SelectedIds() => RequireUi("The selection").Selection.GetElementIds();
    }

    /// <summary>A pinned call reached something only the active model has; nothing was changed. Rpc answers needs_active_document.</summary>
    internal sealed class NeedsActiveException : Exception
    {
        public NeedsActiveException(string message) : base(message) { }
    }

    internal sealed class ToolDef
    {
        public string Name, Title, Description;
        public Dictionary<string, object> Schema;
        public ToolKind Kind;
        public ToolHandler Handler;
        public bool NeedsDocument = true;
        /// <summary>Write tools only: false = the handler opens its own transaction(s).</summary>
        public bool Tx = true;
        public bool Destructive;
        /// <summary>Runs on the socket thread, never touches the Revit API (t3lab_narrate).</summary>
        public bool Background;
        /// <summary>A Read tool that can also write a file (CSV, image): not advertised as read-only.</summary>
        public bool WritesFiles;
        public ToolScope Scope;
    }

    /// <summary>Tool result rendered as an MCP image block plus a JSON text block.</summary>
    internal sealed class ImageResult
    {
        public string Base64;
        public string MimeType = "image/png";
        public object Info;
    }

    internal static class Registry
    {
        static Dictionary<string, ToolDef> _tools = new Dictionary<string, ToolDef>();
        static readonly List<string> Order = new List<string>();

        public static int Count => _tools.Count;

        public static IEnumerable<ToolDef> All => Order.Select(n => _tools[n]);

        public static void Build()
        {
            _tools = new Dictionary<string, ToolDef>(StringComparer.Ordinal);
            Order.Clear();
            StatusTools.Register();
            AgentCursors.Register();
            DocumentTools.Register();
            ViewTools.Register();
            QueryTools.Register();
            ParameterTools.Register();
            CreateTools.Register();
            ModifyTools.Register();
            AnnotationTools.Register();
            SheetTools.Register();
            ScheduleTools.Register();
            ExportTools.Register();
            ModelTools.Register();
            ManageTools.Register();
            TemplateTools.Register();
            EditTools.Register();
            CodeTools.Register();
        }

        public static void Add(string name, string title, ToolKind kind, string description, string schema, ToolHandler handler,
            bool needsDocument = true, bool tx = true, bool destructive = false, bool background = false, bool writesFiles = false,
            ToolScope scope = ToolScope.Any)
        {
            if (!_tools.ContainsKey(name)) Order.Add(name);
            _tools[name] = new ToolDef
            {
                Name = name,
                Title = title,
                Kind = kind,
                Description = description,
                Schema = Json.Schema(schema),
                Handler = handler,
                NeedsDocument = needsDocument,
                Tx = tx && kind == ToolKind.Write,
                Destructive = destructive,
                Background = background,
                WritesFiles = writesFiles,
                Scope = scope,
            };
        }

        /// <summary>The argument that sends a call to another open model (stripped before the tool runs, Rpc.Execute).</summary>
        public const string DocumentPin = "revit_document";

        const string PinDescription =
            "Model to work in: title or path of an open model, also one opened in the background (manage_document open background=true). " +
            "Default: the active model. It does not switch the model the user sees";

        /// <summary>Tools that can work in any open model advertise the pin (scripts/gen_tools_snapshot.py adds the same).</summary>
        public static bool TakesPin(ToolDef t) => t.NeedsDocument && t.Scope == ToolScope.Any;

        public static ToolDef Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;

        public static object ListJson()
        {
            var tools = All.Select(t => (object)new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["title"] = t.Title,
                ["description"] = t.Description,
                ["inputSchema"] = TakesPin(t) ? WithPin(t.Schema) : t.Schema,
                ["annotations"] = new Dictionary<string, object>
                {
                    ["title"] = t.Title,
                    ["readOnlyHint"] = t.Kind == ToolKind.Read && !t.WritesFiles,
                    ["destructiveHint"] = t.Destructive,
                    ["openWorldHint"] = false,
                },
            }).ToList();
            return new Dictionary<string, object> { ["tools"] = tools };
        }

        /// <summary>A copy of the schema with the revit_document property (the stored schema is shared, never changed).</summary>
        static Dictionary<string, object> WithPin(Dictionary<string, object> schema)
        {
            var copy = new Dictionary<string, object>(schema);
            var props = schema.TryGetValue("properties", out object p) && p is Dictionary<string, object> d
                ? new Dictionary<string, object>(d) : new Dictionary<string, object>();
            props[DocumentPin] = new Dictionary<string, object> { ["type"] = "string", ["description"] = PinDescription };
            copy["properties"] = props;
            return copy;
        }
    }

    /// <summary>Helpers to write JSON schemas compactly (single quotes, no quotes inside descriptions).</summary>
    internal static class Sch
    {
        public static string Obj(params string[] props) => ObjR("", props);

        /// <summary>Object schema; required is a comma-separated list of property names.</summary>
        public static string ObjR(string required, params string[] props)
        {
            string req = string.IsNullOrEmpty(required)
                ? ""
                : ",'required':[" + string.Join(",", required.Split(',').Select(r => "'" + r.Trim() + "'")) + "]";
            return "{'type':'object','properties':{" + string.Join(",", props) + "}" + req + "}";
        }

        public static string P(string name, string schema) => "'" + name + "':" + schema;
        public static string Str(string d) => "{'type':'string','description':'" + d + "'}";
        public static string Num(string d) => "{'type':'number','description':'" + d + "'}";
        public static string Int(string d) => "{'type':'integer','description':'" + d + "'}";
        public static string Bool(string d) => "{'type':'boolean','description':'" + d + "'}";
        public static string Any(string d) => "{'description':'" + d + "'}";
        public static string Strs(string d) => "{'type':'array','items':{'type':'string'},'description':'" + d + "'}";
        public static string IdList(string d) => "{'type':'array','items':{'type':'integer'},'description':'" + d + "'}";
        public static string Pt(string d) => "{'type':'array','items':{'type':'number'},'minItems':2,'maxItems':3,'description':'" + d + "'}";
        public static string Pts(string d) => "{'type':'array','items':{'type':'array','items':{'type':'number'}},'description':'" + d + "'}";
        public static string Enum(string d, params string[] values) =>
            "{'type':'string','enum':[" + string.Join(",", values.Select(v => "'" + v + "'")) + "],'description':'" + d + "'}";
        public static string ObjAny(string d) => "{'type':'object','description':'" + d + "'}";
        public static string Arr(string d, string itemSchema) => "{'type':'array','items':" + itemSchema + ",'description':'" + d + "'}";

        public const string Ids = "'element_ids':{'type':'array','items':{'type':'integer'},'description':'Element ids (from query_elements, get_selection or a create tool)'}";
        public const string Limit = "'limit':{'type':'integer','description':'Max rows returned (default 200)'}";
        public const string Offset = "'offset':{'type':'integer','description':'Rows to skip, for paging'}";
    }
}
