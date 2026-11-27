using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    internal static class ScheduleTools
    {
        public static void Register()
        {
            Registry.Add("get_schedule_data", "Read a schedule", ToolKind.Read,
                "Rows of a schedule as Revit shows them (the first row is usually the headers). Without schedule: lists the schedules. " +
                "csv_path also writes a UTF-8 CSV that Excel opens (delimiter ; for Excel with a decimal comma).",
                Obj(P("schedule", Str("Schedule name or id")),
                    P("max_rows", Int("Max rows returned (default 500)")),
                    P("csv_path", Str("Optional .csv path to write")),
                    P("delimiter", Str("CSV delimiter, default comma"))),
                Read, writesFiles: true);

            Registry.Add("create_schedule", "Create a schedule", ToolKind.Write,
                "Create a schedule of a category with fields by name (as in Revit's Fields dialog, e.g. Family and Type, Level, Mark, " +
                "Area, Volume, Count). Unknown fields are listed with the available ones.",
                ObjR("category,fields", P("category", Str("Category, e.g. Walls, Doors, Rooms")),
                    P("fields", Strs("Field names in order")),
                    P("name", Str("Schedule name"))),
                Create);
        }

        static ViewSchedule Find(Document doc, string key)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(s => !s.IsTemplate).ToList();
            ViewSchedule vs = all.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
                              ?? (long.TryParse(key, out long v) ? all.FirstOrDefault(s => Id.V(s.Id) == v) : null)
                              ?? all.FirstOrDefault(s => s.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
            if (vs == null) throw new ToolError($"No schedule '{key}'. Call get_schedule_data without schedule to list them.");
            return vs;
        }

        static object Read(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            if (!a.Has("schedule"))
            {
                var list = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                    .OrderBy(s => s.Name).Select(s => (object)new Dictionary<string, object>
                    {
                        ["id"] = Id.V(s.Id), ["name"] = s.Name,
                        ["category"] = Id.Valid(s.Definition.CategoryId) ? Category.GetCategory(doc, s.Definition.CategoryId)?.Name : "(multi-category)",
                    }).ToList();
                return new Dictionary<string, object> { ["schedules"] = list };
            }
            ViewSchedule vs = Find(doc, a.Str("schedule"));
            TableSectionData body = vs.GetTableData().GetSectionData(SectionType.Body);
            int rows = body.NumberOfRows, cols = body.NumberOfColumns, max = Math.Max(1, a.Int("max_rows", 500));
            var data = new List<object>();
            var all = new List<string[]>();
            for (int r = 0; r < rows; r++)
            {
                var row = new string[cols];
                for (int col = 0; col < cols; col++) row[col] = vs.GetCellText(SectionType.Body, r, col);
                all.Add(row);
                if (data.Count < max) data.Add(row);
            }
            string csv = a.Str("csv_path");
            if (!string.IsNullOrEmpty(csv))
            {
                string sep = a.Str("delimiter") ?? ",";
                var sb = new StringBuilder();
                foreach (string[] row in all)
                    sb.AppendLine(string.Join(sep, row.Select(v => (v ?? "").Contains(sep) || (v ?? "").Contains("\"") || (v ?? "").Contains("\n") ? "\"" + v.Replace("\"", "\"\"") + "\"" : v)));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csv)));
                File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
            }
            return new Dictionary<string, object> { ["schedule"] = vs.Name, ["rows"] = rows, ["columns"] = cols, ["data"] = data, ["truncated"] = rows > max, ["csv"] = csv };
        }

        static object Create(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            Category cat = Rv.Category(doc, a.ReqStr("category"));
            ViewSchedule vs = ViewSchedule.CreateSchedule(doc, cat.Id);
            if (a.Has("name")) vs.Name = a.Str("name");
            ScheduleDefinition def = vs.Definition;
            var fields = def.GetSchedulableFields();
            var added = new List<string>();
            var missing = new List<string>();
            foreach (string want in a.Strs("fields"))
            {
                SchedulableField f = fields.FirstOrDefault(x => string.Equals(x.GetName(doc), want, StringComparison.OrdinalIgnoreCase));
                if (f == null) { missing.Add(want); continue; }
                def.AddField(f);
                added.Add(f.GetName(doc));
            }
            var result = new Dictionary<string, object> { ["id"] = Id.V(vs.Id), ["name"] = vs.Name, ["fields"] = added, ["element_ids"] = Rv.IdList(new[] { vs.Id }) };
            if (missing.Count > 0)
            {
                result["missing_fields"] = missing;
                result["available_fields"] = fields.Select(f => f.GetName(doc)).Distinct().OrderBy(n => n).Take(150).ToList();
            }
            return result;
        }
    }

    internal static class ExportTools
    {
        public static void Register()
        {
            Registry.Add("export_documents", "Export PDF, DWG or IFC", ToolKind.App,
                "Export to a folder. format=pdf (sheets / views; combine=true makes one PDF), dwg (one file per sheet / view), ifc (the " +
                "whole model). sheets = sheet numbers or ['*'] for all; views = view names or ids. file_name without extension.",
                ObjR("format,folder",
                    P("format", Enum("Output format", "pdf", "dwg", "ifc")),
                    P("folder", Str("Output folder (created if missing)")),
                    P("sheets", Strs("Sheet numbers, or * for every sheet")),
                    P("views", Strs("View names or ids")),
                    P("file_name", Str("File name (pdf combined / ifc / dwg prefix)")),
                    P("combine", Bool("pdf: one file for everything (default true)"))),
                Export, writesFiles: true);
        }

        static List<ElementId> Targets(Document doc, Args a)
        {
            var ids = new List<ElementId>();
            List<string> sheets = a.Strs("sheets");
            if (sheets.Contains("*"))
                ids.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder && !s.IsTemplate).OrderBy(s => s.SheetNumber).Select(s => s.Id));
            else
                foreach (string s in sheets) ids.Add(SheetTools.Sheet(doc, s).Id);
            foreach (string v in a.Strs("views")) ids.Add(Rv.View(doc, v, null).Id);
            return ids;
        }

        static object Export(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string format = a.ReqStr("format").ToLowerInvariant();
            string folder = a.ReqStr("folder");
            Directory.CreateDirectory(folder);
            DateTime start = DateTime.Now.AddSeconds(-2);
            string name = a.Str("file_name") ?? Path.GetFileNameWithoutExtension(doc.Title);
            bool ok;
            int count = 0;
            switch (format)
            {
                case "pdf":
                {
                    List<ElementId> ids = Targets(doc, a);
                    if (ids.Count == 0) throw new ToolError("Give sheets (numbers or *) and/or views.");
                    var opts = new PDFExportOptions { Combine = a.Bool("combine", true) };
                    if (opts.Combine) opts.FileName = name;
                    ok = doc.Export(folder, ids, opts);
                    count = ids.Count;
                    break;
                }
                case "dwg":
                {
                    List<ElementId> ids = Targets(doc, a);
                    if (ids.Count == 0) throw new ToolError("Give sheets (numbers or *) and/or views.");
                    ok = doc.Export(folder, name, ids, new DWGExportOptions { MergedViews = true });
                    count = ids.Count;
                    break;
                }
                case "ifc":
                {
                    // IFC export needs an open transaction; nothing it changes is kept.
                    using (var t = new Transaction(doc, "T3Lab MCP · IFC export"))
                    {
                        t.Start();
                        ok = doc.Export(folder, name + ".ifc", new IFCExportOptions());
                        t.RollBack();
                    }
                    count = 1;
                    break;
                }
                default: throw new ToolError("Unknown format " + format);
            }
            var files = Directory.GetFiles(folder).Where(f => File.GetLastWriteTime(f) >= start).Select(Path.GetFileName).OrderBy(f => f).Take(200).ToList();
            if (!ok && files.Count == 0) throw new ToolError($"Revit reported the {format.ToUpperInvariant()} export failed.");
            return new Dictionary<string, object> { ["format"] = format, ["exported"] = count, ["folder"] = folder, ["files"] = files };
        }
    }

    internal static class ModelTools
    {
        public static void Register()
        {
            Registry.Add("model_overview", "Model health overview", ToolKind.Read,
                "One-call audit of the model: element counts per category (top 40), warnings (count + top kinds), families, " +
                "in-place families, views / sheets / views not on sheets, rooms (unplaced / not enclosed), links, imported CAD, worksets, file size.",
                Obj(), Overview);

            Registry.Add("get_warnings", "Revit warnings", ToolKind.Read,
                "The model's warnings grouped by message, with counts and the element ids involved (first ids per group).",
                Obj(P("contains", Str("Only messages containing this text")), Limit), Warnings);
        }

        static object Overview(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            var cats = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                .GroupBy(e => e.Category.Name).Select(g => new { g.Key, N = g.Count() }).OrderByDescending(g => g.N).ToList();
            IList<FailureMessage> warnings = doc.GetWarnings();
            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && !(v is ViewSheet) && v.CanBePrinted).ToList();
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            var onSheets = new HashSet<long>(sheets.SelectMany(s => s.GetAllPlacedViews()).Select(Id.V));
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Autodesk.Revit.DB.Architecture.Room>().ToList();
            var result = new Dictionary<string, object>
            {
                ["title"] = doc.Title,
                ["path"] = doc.PathName,
                ["model_elements"] = cats.Sum(g => g.N),
                ["by_category"] = cats.Take(40).ToDictionary(g => g.Key, g => (object)g.N),
                ["warnings"] = warnings.Count,
                ["top_warnings"] = warnings.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).Take(8)
                    .Select(g => (object)new Dictionary<string, object> { ["message"] = g.Key, ["count"] = g.Count() }).ToList(),
                ["families"] = families.Count,
                ["in_place_families"] = families.Count(f => f.IsInPlace),
                ["views"] = views.Count,
                ["views_not_on_sheets"] = views.Count(v => v.ViewType != ViewType.Schedule && v.ViewType != ViewType.Legend && !onSheets.Contains(Id.V(v.Id))),
                ["sheets"] = sheets.Count,
                ["rooms"] = rooms.Count,
                ["rooms_unplaced"] = rooms.Count(r => r.Location == null),
                ["rooms_not_enclosed"] = rooms.Count(r => r.Location != null && r.Area <= 0),
                ["revit_links"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .Select(l => (object)new Dictionary<string, object> { ["name"] = l.Name, ["loaded"] = RevitLinkType.IsLoaded(doc, l.Id) }).ToList(),
                ["cad_imports"] = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Count(i => !i.IsLinked),
                ["cad_links"] = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Count(i => i.IsLinked),
            };
            if (doc.IsWorkshared)
                result["worksets"] = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => (object)w.Name).ToList();
            try
            {
                if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName))
                    result["file_size_mb"] = Math.Round(new FileInfo(doc.PathName).Length / 1048576.0, 1);
            }
            catch (Exception) { }
            return result;
        }

        static object Warnings(ToolContext c, Args a)
        {
            IList<FailureMessage> warnings = c.Doc.GetWarnings();
            IEnumerable<IGrouping<string, FailureMessage>> groups = warnings.GroupBy(w => w.GetDescriptionText());
            if (a.Has("contains")) groups = groups.Where(g => g.Key.IndexOf(a.Str("contains"), StringComparison.OrdinalIgnoreCase) >= 0);
            var rows = groups.OrderByDescending(g => g.Count()).Take(a.Int("limit", 50)).Select(g => (object)new Dictionary<string, object>
            {
                ["message"] = g.Key,
                ["count"] = g.Count(),
                ["element_ids"] = g.SelectMany(w => w.GetFailingElements().Concat(w.GetAdditionalElements())).Select(Id.V).Distinct().Take(40).Cast<object>().ToList(),
            }).ToList();
            return new Dictionary<string, object> { ["total"] = warnings.Count, ["groups"] = rows };
        }
    }
}
