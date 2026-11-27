using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    internal static class StatusTools
    {
        public static void Register()
        {
            Registry.Add("get_revit_context", "Revit context", ToolKind.Read,
                "Start here. Revit version, open models (active, in other windows, or in the background), and for the active model: active view, selection, " +
                "levels, project info, worksharing and length units. All tools use millimetres, m2, m3 and degrees; element ids are integers.",
                Obj(), GetContext, needsDocument: false);

            Registry.Add("reload_addin", "Reload the add-in", ToolKind.App,
                "Load T3LabRevit.Core.dll again (after an update). The connection drops for a second; call get_revit_context after.",
                Obj(), Reload, needsDocument: false);
        }

        static object GetContext(ToolContext c, Args a)
        {
            UIApplication app = c.App;
            Document active = c.Doc;
            var docs = new List<object>();
            foreach (Document d in Documents.Open(app))
            {
                bool isActive = active != null && d.Equals(active);
                docs.Add(new Dictionary<string, object>
                {
                    ["title"] = d.Title,
                    ["path"] = d.PathName,
                    ["active"] = isActive,
                    ["background"] = !isActive && Documents.IsBackground(d),
                    ["family"] = d.IsFamilyDocument,
                    ["workshared"] = d.IsWorkshared,
                    ["modified"] = d.IsModified,
                    ["read_only"] = d.IsReadOnly,
                });
            }
            var result = new Dictionary<string, object>
            {
                ["addin"] = "T3LabRevit " + Entry.Version,
                ["port"] = Entry.Port,
                ["revit"] = app.Application.VersionName + " " + app.Application.VersionBuild,
                ["user"] = app.Application.Username,
                ["units"] = "Tool arguments and results: lengths mm, areas m2, volumes m3, angles degrees, points [x, y, z] mm in project coordinates (internal origin).",
                ["documents"] = docs,
                ["other_models"] = "Tools that list revit_document work in any model above without switching: pass its title. " +
                                   "manage_document open background=true opens one out of sight.",
            };
            if (active == null)
            {
                result["note"] = "No model open. Ask the user to open one, or use manage_document action=open.";
                return result;
            }

            View view = c.UiDoc.ActiveView;
            var v = new Dictionary<string, object>
            {
                ["id"] = Id.V(view.Id),
                ["name"] = view.Name,
                ["type"] = view.ViewType.ToString(),
            };
            try { if (view.ViewType != ViewType.Schedule && view.ViewType != ViewType.DrawingSheet) v["scale"] = view.Scale; } catch (Exception) { }
            if (view.GenLevel != null) v["level"] = view.GenLevel.Name;
            result["active_view"] = v;

            ICollection<ElementId> sel = c.UiDoc.Selection.GetElementIds();
            result["selection"] = new Dictionary<string, object>
            {
                ["count"] = sel.Count,
                ["element_ids"] = Rv.IdList(sel.Take(50)),
            };
            result["levels"] = new FilteredElementCollector(active).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => (object)new Dictionary<string, object> { ["id"] = Id.V(l.Id), ["name"] = l.Name, ["elevation_mm"] = U.Mm(l.Elevation) })
                .Take(60).ToList();
            if (!active.IsFamilyDocument)
            {
                ProjectInfo pi = active.ProjectInformation;
                result["project"] = new Dictionary<string, object>
                {
                    ["name"] = pi?.Name,
                    ["number"] = pi?.Number,
                    ["client"] = pi?.ClientName,
                    ["status"] = pi?.Status,
                };
            }
            try
            {
                FormatOptions fo = active.GetUnits().GetFormatOptions(SpecTypeId.Length);
                result["display_length_unit"] = fo.GetUnitTypeId().TypeId;
            }
            catch (Exception) { }
            if (active.IsWorkshared)
            {
                try
                {
                    Workset ws = active.GetWorksetTable().GetWorkset(active.GetWorksetTable().GetActiveWorksetId());
                    result["active_workset"] = ws?.Name;
                }
                catch (Exception) { }
            }
            return result;
        }

        static object Reload(ToolContext c, Args a)
        {
            if (!Entry.RequestReload()) throw new ToolError("This loader cannot reload the core; restart Revit instead.");
            return new Dictionary<string, object> { ["reloading"] = true, ["note"] = "Reconnects in a second. Call get_revit_context." };
        }
    }

    internal static class DocumentTools
    {
        public static void Register()
        {
            Registry.Add("manage_document", "Open, save, switch or close a model", ToolKind.App,
                "Model files. action=open (path; detach=true detaches a central model, audit=true audits; background=true opens it " +
                "without a window and without switching: work in it with revit_document=<title>, then save / close it here), activate (title or path of an " +
                "open model: makes it the active one), save (active or title), save_as (path, overwrite), close (title of a model that is " +
                "NOT active; save=true saves first), sync (synchronize with central: comment, relinquish=true), new (save_as path, " +
                "optional template path; the new model becomes active, or stays in the background with background=true).",
                ObjR("action",
                    P("action", Enum("What to do", "open", "activate", "save", "save_as", "close", "sync", "new")),
                    P("path", Str("File path of the .rvt for open / save_as / new")),
                    P("title", Str("Title of an open model (from get_revit_context) for activate / save / close")),
                    P("template", Str("Template .rte for new (default: the project template set in Revit)")),
                    P("detach", Bool("open: detach from central (preserve worksets)")),
                    P("background", Bool("open / new: keep it out of sight, the active model stays active (default false)")),
                    P("audit", Bool("open: audit the model while opening")),
                    P("overwrite", Bool("save_as: overwrite an existing file")),
                    P("save", Bool("close: save before closing")),
                    P("comment", Str("sync: comment")),
                    P("relinquish", Bool("sync: relinquish all borrowed elements and worksets (default true)"))),
                Manage, needsDocument: false, destructive: true);
        }

        static Document Find(UIApplication app, string titleOrPath)
        {
            if (!string.IsNullOrWhiteSpace(titleOrPath)) return Documents.Find(app, titleOrPath);
            return app.ActiveUIDocument?.Document ?? throw new ToolError("No model is open.");
        }

        static object Manage(ToolContext c, Args a)
        {
            UIApplication app = c.App;
            string action = a.ReqStr("action").ToLowerInvariant();
            switch (action)
            {
                case "open":
                {
                    string path = a.ReqStr("path");
                    if (!System.IO.File.Exists(path)) throw new ToolError($"File not found: {path}");
                    var opts = new OpenOptions { Audit = a.Bool("audit", false) };
                    if (a.Bool("detach", false)) opts.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    ModelPath model = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
                    if (a.Bool("background", false))
                    {
                        Document bg = app.Application.OpenDocumentFile(model, opts);   // no window: the user's model stays active
                        return new Dictionary<string, object>
                        {
                            ["opened"] = bg.Title, ["background"] = true, ["workshared"] = bg.IsWorkshared,
                            ["note"] = $"Work in it with revit_document='{bg.Title}'; save or close it with manage_document title='{bg.Title}'.",
                        };
                    }
                    UIDocument ui = app.OpenAndActivateDocument(model, opts, false);
                    return new Dictionary<string, object> { ["opened"] = ui.Document.Title, ["active_view"] = ui.ActiveView?.Name };
                }
                case "activate":
                {
                    Document d = Find(app, a.ReqStr("title"));
                    if (string.IsNullOrEmpty(d.PathName)) throw new ToolError($"'{d.Title}' was never saved; Revit can only activate a saved model by its path. Ask the user to click its window.");
                    UIDocument ui = app.OpenAndActivateDocument(d.PathName);
                    return new Dictionary<string, object> { ["active"] = ui.Document.Title };
                }
                case "save":
                {
                    Document d = Find(app, a.Str("title"));
                    if (string.IsNullOrEmpty(d.PathName)) throw new ToolError($"'{d.Title}' has no file yet: use action=save_as with a path.");
                    d.Save();
                    return new Dictionary<string, object> { ["saved"] = d.PathName };
                }
                case "save_as":
                {
                    Document d = Find(app, a.Str("title"));
                    string path = a.ReqStr("path");
                    var opts = new SaveAsOptions { OverwriteExistingFile = a.Bool("overwrite", false) };
                    if (d.IsWorkshared)
                    {
                        var wso = new WorksharingSaveAsOptions { SaveAsCentral = false };
                        opts.SetWorksharingOptions(wso);
                    }
                    d.SaveAs(path, opts);
                    return new Dictionary<string, object> { ["saved_as"] = d.PathName, ["title"] = d.Title };
                }
                case "close":
                {
                    Document d = Find(app, a.ReqStr("title"));
                    Document active = app.ActiveUIDocument?.Document;
                    if (active != null && d.Equals(active))
                        throw new ToolError("Revit cannot close the active model through its API. Activate another model first, or ask the user to close it.");
                    string title = d.Title;
                    bool ok = d.Close(a.Bool("save", false));
                    return new Dictionary<string, object> { ["closed"] = ok ? title : null, ["ok"] = ok };
                }
                case "sync":
                {
                    Document d = Find(app, a.Str("title"));
                    if (!d.IsWorkshared) throw new ToolError($"'{d.Title}' is not workshared: use action=save.");
                    var swc = new SynchronizeWithCentralOptions { Comment = a.Str("comment", "T3Lab MCP") ?? "" };
                    swc.SetRelinquishOptions(new RelinquishOptions(a.Bool("relinquish", true)));
                    d.SynchronizeWithCentral(new TransactWithCentralOptions(), swc);
                    return new Dictionary<string, object> { ["synchronized"] = d.Title };
                }
                case "new":
                {
                    string path = a.ReqStr("path");
                    string template = a.Str("template") ?? app.Application.DefaultProjectTemplate;
                    Document d = string.IsNullOrEmpty(template) || !System.IO.File.Exists(template)
                        ? app.Application.NewProjectDocument(UnitSystem.Metric)
                        : app.Application.NewProjectDocument(template);
                    d.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = a.Bool("overwrite", false) });
                    string saved = d.PathName;
                    if (a.Bool("background", false))
                        return new Dictionary<string, object> { ["created"] = saved, ["title"] = d.Title, ["background"] = true, ["template"] = template };
                    d.Close(false);   // opened without a window: close it, then open it in one
                    UIDocument ui = app.OpenAndActivateDocument(saved);
                    return new Dictionary<string, object> { ["created"] = saved, ["active"] = ui.Document.Title, ["template"] = template };
                }
            }
            throw new ToolError("Unknown action " + action);
        }
    }
}
