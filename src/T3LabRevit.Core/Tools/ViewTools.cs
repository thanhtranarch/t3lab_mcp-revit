using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    internal static class ViewTools
    {
        public static void Register()
        {
            Registry.Add("list_views", "List views", ToolKind.Read,
                "Views of the model (no sheets: use list_sheets). Filter by type (FloorPlan, CeilingPlan, EngineeringPlan, " +
                "AreaPlan, Section, Elevation, ThreeD, Drafting, Legend, Schedule, DraftingView), name, level or placement on a sheet.",
                Obj(P("type", Str("ViewType name, e.g. FloorPlan")),
                    P("name_contains", Str("Part of the view name")),
                    P("level", Str("Level name")),
                    P("placed", Bool("true = only views on a sheet, false = only views on no sheet")),
                    P("templates", Bool("true = list view templates instead")),
                    Limit, Offset),
                ListViews);

            Registry.Add("set_active_view", "Open a view", ToolKind.App,
                "Make a view (or sheet) the active view in Revit, by name or id.",
                ObjR("view", P("view", Str("View or sheet name, or id"))), SetActiveView, scope: ToolScope.Active);

            Registry.Add("create_view", "Create a view", ToolKind.Write,
                "Create a view: kind=floor_plan, ceiling_plan, structural_plan (level), 3d (isometric), drafting, or duplicate " +
                "(view + mode plain | detailing | dependent). Optional name, scale and view template.",
                ObjR("kind",
                    P("kind", Enum("View kind", "floor_plan", "ceiling_plan", "structural_plan", "3d", "drafting", "duplicate")),
                    P("level", Str("Level name or id for plans (default: active view level)")),
                    P("view", Str("duplicate: view to duplicate (default active)")),
                    P("mode", Enum("duplicate mode", "plain", "detailing", "dependent")),
                    P("name", Str("New view name")),
                    P("scale", Int("View scale, e.g. 100 for 1:100")),
                    P("template", Str("View template name to apply"))),
                CreateView);

            Registry.Add("get_selection", "Get selection", ToolKind.Read,
                "Elements the user has selected in Revit (ids + summary).",
                Obj(Limit), GetSelection, scope: ToolScope.Active);

            Registry.Add("set_selection", "Select / zoom elements", ToolKind.App,
                "Select elements in Revit so the user sees them; zoom=true also zooms the active view to them. Empty list clears the selection.",
                Obj(Ids, P("zoom", Bool("Zoom to the elements (default true)"))), SetSelection, scope: ToolScope.Active);

            Registry.Add("view_graphics", "Color, hide or isolate in a view", ToolKind.Write,
                "Change how elements look in a view (active by default): action=color (rgb [r,g,b], transparency 0-100, solid fill), " +
                "reset (remove overrides), hide, unhide, isolate (temporary isolate), clear_temporary (end temporary hide/isolate).",
                ObjR("action",
                    P("action", Enum("What to do", "color", "reset", "hide", "unhide", "isolate", "clear_temporary")),
                    Ids,
                    P("rgb", "{'type':'array','items':{'type':'integer'},'description':'Colour [r, g, b] 0-255 (default orange)'}"),
                    P("transparency", Int("Surface transparency 0-100")),
                    P("view", Str("View name or id (default active)"))),
                ViewGraphics);

            Registry.Add("export_image", "Look at a view (image)", ToolKind.Read,
                "Render a view (default: the active one) to a PNG and return it so you can see the model. pixels = width (default 1400, " +
                "max 2400). save_path also keeps the file.",
                Obj(P("view", Str("View or sheet name or id (default active)")),
                    P("pixels", Int("Image width in pixels")),
                    P("save_path", Str("Optional .png path to keep"))),
                ExportImage, writesFiles: true);
        }

        static object ListViews(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            bool templates = a.Bool("templates", false);
            string type = a.Str("type"), contains = a.Str("name_contains"), level = a.Str("level");
            var sheets = new Dictionary<long, string>();
            foreach (ViewSheet s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                foreach (ElementId vid in s.GetAllPlacedViews()) sheets[Id.V(vid)] = s.SheetNumber;
            foreach (ScheduleSheetInstance si in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                if (doc.GetElement(si.OwnerViewId) is ViewSheet s) sheets[Id.V(si.ScheduleId)] = s.SheetNumber;
            }

            IEnumerable<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate == templates && !(v is ViewSheet) && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.Internal);
            if (!string.IsNullOrEmpty(type)) views = views.Where(v => string.Equals(v.ViewType.ToString(), type.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(contains)) views = views.Where(v => v.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(level)) views = views.Where(v => v.GenLevel != null && string.Equals(v.GenLevel.Name, level, StringComparison.OrdinalIgnoreCase));
            if (a.Has("placed"))
            {
                bool placed = a.Bool("placed", true);
                views = views.Where(v => sheets.ContainsKey(Id.V(v.Id)) == placed);
            }
            List<View> all = views.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name).ToList();
            int offset = a.Int("offset", 0), limit = a.Int("limit", 200);
            var rows = all.Skip(offset).Take(limit).Select(v =>
            {
                var d = new Dictionary<string, object> { ["id"] = Id.V(v.Id), ["name"] = v.Name, ["type"] = v.ViewType.ToString() };
                if (v.GenLevel != null) d["level"] = v.GenLevel.Name;
                try { if (v.ViewType != ViewType.Schedule && v.ViewType != ViewType.Legend) d["scale"] = v.Scale; } catch (Exception) { }
                if (Id.Valid(v.ViewTemplateId)) d["template"] = doc.GetElement(v.ViewTemplateId)?.Name;
                if (sheets.TryGetValue(Id.V(v.Id), out string sn)) d["sheet"] = sn;
                return (object)d;
            }).ToList();
            return new Dictionary<string, object> { ["total"] = all.Count, ["offset"] = offset, ["views"] = rows };
        }

        static object SetActiveView(ToolContext c, Args a)
        {
            View v = Rv.View(c.Doc, a.ReqStr("view"), null);
            if (v.IsTemplate) throw new ToolError($"'{v.Name}' is a view template and cannot be opened.");
            try { c.UiDoc.ActiveView = v; }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                c.UiDoc.RequestViewChange(v);   // modeless contexts: Revit switches right after this call
                return new Dictionary<string, object> { ["requested"] = v.Name, ["id"] = Id.V(v.Id) };
            }
            return new Dictionary<string, object> { ["active_view"] = v.Name, ["id"] = Id.V(v.Id), ["type"] = v.ViewType.ToString() };
        }

        static ViewFamilyType Vft(Document doc, ViewFamily family)
        {
            ViewFamilyType t = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == family);
            if (t == null) throw new ToolError($"The model has no view type for {family}.");
            return t;
        }

        static object CreateView(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string kind = a.ReqStr("kind").ToLowerInvariant();
            View created;
            switch (kind)
            {
                case "floor_plan":
                case "ceiling_plan":
                case "structural_plan":
                {
                    Level lv = Rv.LevelOrDefault(doc, c.ActiveView, a.Str("level"));
                    ViewFamily f = kind == "floor_plan" ? ViewFamily.FloorPlan : kind == "ceiling_plan" ? ViewFamily.CeilingPlan : ViewFamily.StructuralPlan;
                    created = ViewPlan.Create(doc, Vft(doc, f).Id, lv.Id);
                    break;
                }
                case "3d":
                    created = View3D.CreateIsometric(doc, Vft(doc, ViewFamily.ThreeDimensional).Id);
                    break;
                case "drafting":
                    created = ViewDrafting.Create(doc, Vft(doc, ViewFamily.Drafting).Id);
                    break;
                case "duplicate":
                {
                    View src = Rv.View(doc, a.Str("view"), c.ActiveView);
                    string mode = (a.Str("mode") ?? "plain").ToLowerInvariant();
                    ViewDuplicateOption opt = mode == "dependent" ? ViewDuplicateOption.AsDependent
                        : mode == "detailing" ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate;
                    if (!src.CanViewBeDuplicated(opt)) throw new ToolError($"'{src.Name}' cannot be duplicated as {mode}.");
                    created = doc.GetElement(src.Duplicate(opt)) as View;
                    break;
                }
                default: throw new ToolError("Unknown kind " + kind);
            }
            if (a.Has("name")) created.Name = a.Str("name");
            if (a.Has("scale")) created.Scale = a.Int("scale", 100);
            if (a.Has("template"))
            {
                View t = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => v.IsTemplate && string.Equals(v.Name, a.Str("template"), StringComparison.OrdinalIgnoreCase));
                if (t == null) throw new ToolError($"No view template '{a.Str("template")}' (list_views templates=true).");
                created.ViewTemplateId = t.Id;
            }
            return new Dictionary<string, object> { ["id"] = Id.V(created.Id), ["name"] = created.Name, ["type"] = created.ViewType.ToString() };
        }

        static object GetSelection(ToolContext c, Args a)
        {
            ICollection<ElementId> ids = c.UiDoc.Selection.GetElementIds();
            int limit = a.Int("limit", 200);
            return new Dictionary<string, object>
            {
                ["count"] = ids.Count,
                ["elements"] = ids.Take(limit).Select(i => c.Doc.GetElement(i)).Where(e => e != null).Select(e => (object)Rv.Summary(e)).ToList(),
            };
        }

        static object SetSelection(ToolContext c, Args a)
        {
            List<ElementId> ids = a.Ids("element_ids");
            Rv.GetMany(c.Doc, ids);
            c.UiDoc.Selection.SetElementIds(ids);
            if (ids.Count > 0 && a.Bool("zoom", true))
            {
                try { c.UiDoc.ShowElements(ids); }
                catch (Exception) { /* not visible in any view: selection still set */ }
            }
            return new Dictionary<string, object> { ["selected"] = ids.Count };
        }

        internal static ElementId SolidFill(Document doc)
        {
            foreach (FillPatternElement f in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                FillPattern p = f.GetFillPattern();
                if (p.IsSolidFill && p.Target == FillPatternTarget.Drafting) return f.Id;
            }
            return ElementId.InvalidElementId;
        }

        static object ViewGraphics(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            View view = Rv.View(doc, a.Str("view"), c.ActiveView);
            string action = a.ReqStr("action").ToLowerInvariant();
            List<ElementId> ids = a.Ids("element_ids");
            if (action != "clear_temporary" && ids.Count == 0) throw new ToolError("element_ids is empty.");
            if (ids.Count > 0) Rv.GetMany(doc, ids);
            switch (action)
            {
                case "color":
                {
                    List<string> rgb = a.Strs("rgb");
                    var col = rgb.Count == 3
                        ? new Color((byte)Args.ToDouble(rgb[0], "rgb"), (byte)Args.ToDouble(rgb[1], "rgb"), (byte)Args.ToDouble(rgb[2], "rgb"))
                        : new Color(234, 104, 12);
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(col);
                    ogs.SetCutLineColor(col);
                    ElementId solid = SolidFill(doc);
                    if (Id.Valid(solid))
                    {
                        ogs.SetSurfaceForegroundPatternId(solid);
                        ogs.SetSurfaceForegroundPatternColor(col);
                        ogs.SetCutForegroundPatternId(solid);
                        ogs.SetCutForegroundPatternColor(col);
                    }
                    if (a.Has("transparency")) ogs.SetSurfaceTransparency(Math.Max(0, Math.Min(100, a.Int("transparency", 0))));
                    foreach (ElementId id in ids) view.SetElementOverrides(id, ogs);
                    break;
                }
                case "reset":
                    foreach (ElementId id in ids) view.SetElementOverrides(id, new OverrideGraphicSettings());
                    break;
                case "hide":
                    view.HideElements(ids.Where(i => doc.GetElement(i).CanBeHidden(view)).ToList());
                    break;
                case "unhide":
                    view.UnhideElements(ids);
                    break;
                case "isolate":
                    view.IsolateElementsTemporary(ids);
                    break;
                case "clear_temporary":
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    break;
                default: throw new ToolError("Unknown action " + action);
            }
            return new Dictionary<string, object> { ["view"] = view.Name, ["action"] = action, ["elements"] = ids.Count };
        }

        static object ExportImage(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            View view = Rv.View(doc, a.Str("view"), c.ActiveView);
            if (view.ViewType == ViewType.Schedule) throw new ToolError("A schedule has no image: use get_schedule_data.");
            int pixels = Math.Max(300, Math.Min(2400, a.Int("pixels", 1400)));
            string dir = Path.Combine(Path.GetTempPath(), "T3LabRevitMCP", "img");
            Directory.CreateDirectory(dir);
            string prefix = Path.Combine(dir, "v" + DateTime.Now.ToString("HHmmssfff"));
            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = prefix,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = FitDirectionType.Horizontal,
                PixelSize = pixels,
            };
            opts.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(opts);
            string file = Directory.GetFiles(dir, Path.GetFileName(prefix) + "*.png").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (file == null) throw new ToolError("Revit wrote no image for this view.");
            byte[] bytes = File.ReadAllBytes(file);
            string keep = a.Str("save_path");
            if (!string.IsNullOrEmpty(keep))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keep)));
                File.Copy(file, keep, true);
            }
            try { File.Delete(file); } catch (Exception) { }
            return new ImageResult
            {
                Base64 = Convert.ToBase64String(bytes),
                Info = new Dictionary<string, object> { ["view"] = view.Name, ["type"] = view.ViewType.ToString(), ["bytes"] = bytes.Length, ["saved"] = keep },
            };
        }
    }
}
