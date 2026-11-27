using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    internal static class AnnotationTools
    {
        public static void Register()
        {
            Registry.Add("create_annotation", "Add text, lines, tags or dimensions", ToolKind.Write,
                "2D annotation in a view (default active). kind=text: items [{point, text}] (+ text_type); kind=detail_line: items " +
                "[{start, end}] (+ line_style); kind=tag: element_ids, tags each element at its centre (leader=true adds a leader); " +
                "kind=dimension: items [{start, end, element_ids?}] dimensions the elements' faces crossed by the line (walls, grids) " +
                "or, without element_ids, grids crossed by it. Points in mm.",
                ObjR("kind",
                    P("kind", Enum("What to add", "text", "detail_line", "tag", "dimension")),
                    P("items", "{'type':'array','items':{'type':'object'},'description':'text: {point, text}; detail_line: {start, end}; dimension: {start, end, element_ids?}'}"),
                    Ids,
                    P("view", Str("View name or id (default active)")),
                    P("text_type", Str("Text note type name")),
                    P("line_style", Str("Line style name, e.g. Thin Lines")),
                    P("leader", Bool("tag: add a leader"))),
                Create);
        }

        static object Create(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            View view = Rv.View(doc, a.Str("view"), c.ActiveView);
            string kind = a.ReqStr("kind").ToLowerInvariant();
            var ids = new List<ElementId>();
            var skipped = new List<object>();
            switch (kind)
            {
                case "text":
                {
                    ElementId typeId = a.Has("text_type")
                        ? Rv.TypeOf<TextNoteType>(doc, a.Str("text_type")).Id
                        : doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                    foreach (Args it in a.Objs("items"))
                        ids.Add(TextNote.Create(doc, view.Id, it.ReqPt("point"), it.ReqStr("text"), typeId).Id);
                    break;
                }
                case "detail_line":
                {
                    GraphicsStyle style = null;
                    if (a.Has("line_style"))
                    {
                        Category lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
                        Category sub = lines.SubCategories.Cast<Category>().FirstOrDefault(s => string.Equals(s.Name, a.Str("line_style"), StringComparison.OrdinalIgnoreCase));
                        if (sub == null) throw new ToolError($"No line style '{a.Str("line_style")}'. Styles: " + string.Join(", ", lines.SubCategories.Cast<Category>().Select(s => s.Name).Take(40)));
                        style = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                    }
                    double z = view.Origin?.Z ?? 0;
                    if (view is ViewPlan vp && vp.GenLevel != null) z = vp.GenLevel.Elevation;
                    foreach (Args it in a.Objs("items"))
                    {
                        XYZ p0 = it.ReqPt("start"), p1 = it.ReqPt("end");
                        if (view is ViewPlan) { p0 = new XYZ(p0.X, p0.Y, z); p1 = new XYZ(p1.X, p1.Y, z); }
                        DetailCurve dc = doc.Create.NewDetailCurve(view, Line.CreateBound(p0, p1));
                        if (style != null) dc.LineStyle = style;
                        ids.Add(dc.Id);
                    }
                    break;
                }
                case "tag":
                {
                    bool leader = a.Bool("leader", false);
                    foreach (Element e in Rv.GetMany(doc, a.Ids("element_ids")))
                    {
                        BoundingBoxXYZ b = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                        if (b == null) { skipped.Add(new Dictionary<string, object> { ["id"] = Id.V(e.Id), ["why"] = "no extents in this view" }); continue; }
                        XYZ at = (b.Min + b.Max) / 2;
                        try
                        {
                            IndependentTag tag = IndependentTag.Create(doc, view.Id, new Reference(e), leader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, at);
                            ids.Add(tag.Id);
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                        {
                            skipped.Add(new Dictionary<string, object> { ["id"] = Id.V(e.Id), ["why"] = ex.Message + " (no tag family loaded for " + e.Category?.Name + "?)" });
                        }
                    }
                    break;
                }
                case "dimension":
                {
                    foreach (Args it in a.Objs("items"))
                    {
                        XYZ p0 = it.ReqPt("start"), p1 = it.ReqPt("end");
                        double z = view is ViewPlan vp2 && vp2.GenLevel != null ? vp2.GenLevel.Elevation : p0.Z;
                        Line line = Line.CreateBound(new XYZ(p0.X, p0.Y, z), new XYZ(p1.X, p1.Y, z));
                        var refs = new ReferenceArray();
                        List<ElementId> targets = it.Ids("element_ids");
                        IEnumerable<Element> els = targets.Count > 0 ? Rv.GetMany(doc, targets) : new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).ToElements();
                        foreach (Element e in els)
                        {
                            if (e is Grid g)
                            {
                                if (targets.Count > 0 || g.Curve is Line gl && Crosses(gl, line)) refs.Append(new Reference(g));
                            }
                            else if (e is Wall w)
                            {
                                foreach (Reference r in HostObjectUtils.GetSideFaces(w, ShellLayerType.Exterior)) refs.Append(r);
                                foreach (Reference r in HostObjectUtils.GetSideFaces(w, ShellLayerType.Interior)) refs.Append(r);
                            }
                            else refs.Append(new Reference(e));
                        }
                        if (refs.Size < 2) { skipped.Add(new Dictionary<string, object> { ["line"] = U.Pt(p0), ["why"] = "fewer than 2 references crossed" }); continue; }
                        Dimension dim = doc.Create.NewDimension(view, line, refs);
                        ids.Add(dim.Id);
                    }
                    break;
                }
                default: throw new ToolError("Unknown kind " + kind);
            }
            if (ids.Count == 0) throw new ToolError("Nothing was added: " + Json.Write(skipped.Take(10).ToList()));
            return new Dictionary<string, object> { ["created"] = ids.Count, ["view"] = view.Name, ["element_ids"] = Rv.IdList(ids), ["skipped"] = skipped };
        }

        /// <summary>2D segment test in plan (Curve.Intersect changed signature in Revit 2027).</summary>
        static bool Crosses(Line a, Line b)
        {
            XYZ p = a.GetEndPoint(0), r = a.GetEndPoint(1) - p, q = b.GetEndPoint(0), s = b.GetEndPoint(1) - q;
            double den = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(den) < 1e-12) return false;
            double t = ((q.X - p.X) * s.Y - (q.Y - p.Y) * s.X) / den;
            double u = ((q.X - p.X) * r.Y - (q.Y - p.Y) * r.X) / den;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }
    }

    internal static class SheetTools
    {
        public static void Register()
        {
            Registry.Add("list_sheets", "List sheets", ToolKind.Read,
                "Sheets of the model: number, name, title block, the views and schedules placed on each, current revision.",
                Obj(P("number_contains", Str("Part of the sheet number")), P("name_contains", Str("Part of the sheet name")), Limit, Offset),
                List);

            Registry.Add("create_sheets", "Create sheets", ToolKind.Write,
                "Create sheets: sheets=[{number, name}] with a title block ('Family: Type' label, default the first one loaded).",
                ObjR("sheets", P("sheets", "{'type':'array','items':{'type':'object','properties':{'number':{'type':'string'},'name':{'type':'string'}}},'description':'Sheets to create'}"),
                    P("title_block", Str("Title block type label or name"))),
                CreateSheets);

            Registry.Add("place_views_on_sheet", "Place views on a sheet", ToolKind.Write,
                "Place views (plans, sections, 3D, drafting, legends) and schedules on a sheet: views=[{view, point?}] with view name or id " +
                "and the centre point on the sheet in mm (default: the sheet centre, then shifted). A view already on another sheet is reported.",
                ObjR("sheet,views", P("sheet", Str("Sheet number, name or id")),
                    P("views", "{'type':'array','items':{'type':'object','properties':{'view':{'type':'string'},'point':{'type':'array'}}},'description':'Views to place'}")),
                Place);
        }

        internal static ViewSheet Sheet(Document doc, string key)
        {
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            ViewSheet s = sheets.FirstOrDefault(x => string.Equals(x.SheetNumber, key, StringComparison.OrdinalIgnoreCase))
                          ?? sheets.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase))
                          ?? (long.TryParse(key, out long v) ? sheets.FirstOrDefault(x => Id.V(x.Id) == v) : null);
            if (s == null) throw new ToolError($"No sheet '{key}' (list_sheets).");
            return s;
        }

        static object List(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            IEnumerable<ViewSheet> sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate);
            if (a.Has("number_contains")) sheets = sheets.Where(s => s.SheetNumber.IndexOf(a.Str("number_contains"), StringComparison.OrdinalIgnoreCase) >= 0);
            if (a.Has("name_contains")) sheets = sheets.Where(s => s.Name.IndexOf(a.Str("name_contains"), StringComparison.OrdinalIgnoreCase) >= 0);
            List<ViewSheet> all = sheets.OrderBy(s => s.SheetNumber).ToList();
            var schedulesBySheet = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .Where(si => !si.IsTitleblockRevisionSchedule).GroupBy(si => Id.V(si.OwnerViewId)).ToDictionary(g => g.Key, g => g.ToList());
            var titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
                .GroupBy(e => Id.V(e.OwnerViewId)).ToDictionary(g => g.Key, g => g.First());
            int offset = a.Int("offset", 0), limit = a.Int("limit", 200);
            var rows = all.Skip(offset).Take(limit).Select(s =>
            {
                var d = new Dictionary<string, object> { ["id"] = Id.V(s.Id), ["number"] = s.SheetNumber, ["name"] = s.Name };
                if (titleBlocks.TryGetValue(Id.V(s.Id), out Element tb) && Rv.TypeElement(tb) is ElementType tbt) d["title_block"] = Rv.TypeLabel(tbt);
                d["views"] = s.GetAllPlacedViews().Select(doc.GetElement).OfType<View>()
                    .Select(v => (object)new Dictionary<string, object> { ["id"] = Id.V(v.Id), ["name"] = v.Name, ["type"] = v.ViewType.ToString() }).ToList();
                if (schedulesBySheet.TryGetValue(Id.V(s.Id), out var sch))
                    d["schedules"] = sch.Select(si => (object)doc.GetElement(si.ScheduleId)?.Name).ToList();
                try
                {
                    ElementId rev = s.GetCurrentRevision();
                    if (Id.Valid(rev) && doc.GetElement(rev) is Revision r) d["revision"] = r.Description;
                }
                catch (Exception) { }
                return (object)d;
            }).ToList();
            return new Dictionary<string, object> { ["total"] = all.Count, ["offset"] = offset, ["sheets"] = rows };
        }

        static object CreateSheets(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            ElementId tb = ElementId.InvalidElementId;
            var tbs = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            if (a.Has("title_block"))
                tb = (Rv.FindType(tbs, a.Str("title_block")) ?? throw new ToolError($"No title block '{a.Str("title_block")}'. Loaded: " + string.Join(" | ", tbs.Select(Rv.TypeLabel)))).Id;
            else if (tbs.Count > 0) tb = tbs[0].Id;
            var made = new List<object>();
            var ids = new List<ElementId>();
            foreach (Args s in a.Objs("sheets"))
            {
                ViewSheet sheet = ViewSheet.Create(doc, tb);
                if (s.Has("number")) sheet.SheetNumber = s.Str("number");
                if (s.Has("name")) sheet.Name = s.Str("name");
                ids.Add(sheet.Id);
                made.Add(new Dictionary<string, object> { ["id"] = Id.V(sheet.Id), ["number"] = sheet.SheetNumber, ["name"] = sheet.Name });
            }
            return new Dictionary<string, object> { ["sheets"] = made, ["element_ids"] = Rv.IdList(ids), ["title_block"] = Id.Valid(tb) ? Rv.TypeLabel((ElementType)doc.GetElement(tb)) : "(none)" };
        }

        static XYZ SheetCenter(ViewSheet s)
        {
            try
            {
                BoundingBoxUV o = s.Outline;
                return new XYZ((o.Min.U + o.Max.U) / 2, (o.Min.V + o.Max.V) / 2, 0);
            }
            catch (Exception) { return new XYZ(U.Ft(420) / 2, U.Ft(297) / 2, 0); }
        }

        static object Place(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            ViewSheet sheet = Sheet(doc, a.ReqStr("sheet"));
            XYZ center = SheetCenter(sheet);
            var placed = new List<object>();
            var failed = new List<object>();
            var ids = new List<ElementId>();
            int i = 0;
            foreach (Args item in a.Objs("views"))
            {
                XYZ pt = item.Has("point") ? item.ReqPt("point") : center + new XYZ(U.Ft(60) * i, -U.Ft(40) * i, 0);
                i++;
                View v;
                try { v = Rv.View(doc, item.ReqStr("view"), null); }
                catch (ToolError te) { failed.Add(new Dictionary<string, object> { ["view"] = item.Str("view"), ["error"] = te.Message }); continue; }
                try
                {
                    if (v is ViewSchedule vs)
                    {
                        ScheduleSheetInstance si = ScheduleSheetInstance.Create(doc, sheet.Id, vs.Id, pt);
                        ids.Add(si.Id);
                        placed.Add(new Dictionary<string, object> { ["view"] = v.Name, ["schedule_instance_id"] = Id.V(si.Id) });
                    }
                    else
                    {
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id))
                            throw new ToolError("Revit refuses it (already on a sheet, or a sheet itself; legends and drafting views can repeat)");
                        Viewport vp = Viewport.Create(doc, sheet.Id, v.Id, pt);
                        ids.Add(vp.Id);
                        placed.Add(new Dictionary<string, object> { ["view"] = v.Name, ["viewport_id"] = Id.V(vp.Id) });
                    }
                }
                catch (Exception ex) when (ex is ToolError || ex is Autodesk.Revit.Exceptions.ApplicationException)
                {
                    failed.Add(new Dictionary<string, object> { ["view"] = v.Name, ["error"] = ex.Message });
                }
            }
            if (placed.Count == 0) throw new ToolError("No view was placed: " + Json.Write(failed));
            return new Dictionary<string, object> { ["sheet"] = sheet.SheetNumber + " - " + sheet.Name, ["placed"] = placed, ["failed"] = failed, ["element_ids"] = Rv.IdList(ids) };
        }
    }
}
