using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    internal static class QueryTools
    {
        public static void Register()
        {
            Registry.Add("query_elements", "Find elements", ToolKind.Read,
                "Find model elements in the active model. Filters combine: categories (Walls, Doors, OST_StructuralColumns...), class (Wall, " +
                "Floor, FamilyInstance...), in_view (only what the active view shows), selection (only the selected elements), level, " +
                "type_contains (family or type name), name_contains, where [{parameter, op: eq|ne|contains|gt|lt|gte|lte|empty|not_empty, " +
                "value}]. detail=count (default on an unfiltered query) | summary | full (location + bounding box). fields = parameter " +
                "values to add per element. group_by=category|family|type|level returns counts. Numbers are mm, m2, m3, degrees.",
                Obj(P("categories", Strs("Category names or OST_ names")),
                    P("class", Str("Revit API class, e.g. Wall, Floor, FamilyInstance, Grid")),
                    P("in_view", Bool("Only elements visible in the active view")),
                    P("view", Str("Only elements visible in this view (name or id)")),
                    P("selection", Bool("Only the selected elements")),
                    P("level", Str("Level name")),
                    P("type_contains", Str("Part of the family or type name")),
                    P("name_contains", Str("Part of the element name")),
                    P("where", "{'type':'array','items':{'type':'object','properties':{'parameter':{'type':'string'},'op':{'type':'string'},'value':{}}},'description':'Parameter conditions, all must match'}"),
                    P("fields", Strs("Parameter names to return per element")),
                    P("detail", Enum("How much per element", "count", "summary", "full")),
                    P("group_by", Enum("Return counts per group", "category", "family", "type", "level")),
                    P("types", Bool("Query element types instead of instances")),
                    Limit, Offset),
                Query);

            Registry.Add("get_element_info", "Element details", ToolKind.Read,
                "Everything about up to 50 elements: category, family, type, level, location, bounding box, host, workset, phase, group, " +
                "and all parameters (instance; type ones too with include_type=true). Use parameter names from here in set_parameter.",
                ObjR("element_ids", Ids,
                    P("include_type", Bool("Also list the type parameters")),
                    P("parameters", Bool("List parameters (default true)"))),
                Info);

            Registry.Add("list_types", "List family types", ToolKind.Read,
                "Element types (wall types, floor types, family symbols, title blocks, text types...) by category or class, with their " +
                "ids; use the 'Family: Type' label in create tools. count=true adds how many instances use each type.",
                Obj(P("category", Str("Category, e.g. Walls, Doors, Title Blocks")),
                    P("class", Str("Type class, e.g. WallType, FloorType, FamilySymbol, TextNoteType, ViewFamilyType")),
                    P("name_contains", Str("Part of the family or type name")),
                    P("count", Bool("Count instances per type")),
                    Limit, Offset),
                ListTypes);

            Registry.Add("list_datums", "Levels and grids", ToolKind.Read,
                "All levels (name, elevation mm) and grids (name, start/end mm) of the active model.",
                Obj(), Datums);
        }

        internal static Type ApiClass(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string n = name.Trim();
            var asm = typeof(Element).Assembly;
            foreach (string ns in new[] { "Autodesk.Revit.DB.", "Autodesk.Revit.DB.Architecture.", "Autodesk.Revit.DB.Structure.", "Autodesk.Revit.DB.Mechanical.",
                                          "Autodesk.Revit.DB.Plumbing.", "Autodesk.Revit.DB.Electrical.", "" })
            {
                Type t = asm.GetType(ns + n, false, true);
                if (t != null && typeof(Element).IsAssignableFrom(t)) return t;
            }
            throw new ToolError($"Unknown Revit class '{name}'. Examples: Wall, Floor, FamilyInstance, Grid, Level, ViewSheet, WallType.");
        }

        static bool Matches(Element e, Args cond)
        {
            string pname = cond.ReqStr("parameter");
            string op = (cond.Str("op") ?? "eq").ToLowerInvariant();
            Parameter p = Rv.Param(e, pname);
            if (p == null)
            {
                ElementType t = Rv.TypeElement(e);
                p = t == null ? null : Rv.Param(t, pname);
            }
            object v = Rv.Value(p);
            string text = p == null ? null : (p.StorageType == StorageType.String ? p.AsString() : Rv.SafeValueString(p));
            if (op == "empty") return p == null || !p.HasValue || string.IsNullOrEmpty(text);
            if (op == "not_empty") return p != null && p.HasValue && !string.IsNullOrEmpty(text);
            if (p == null) return op == "ne";
            object want = cond.Raw("value");
            if (op == "contains") return (text ?? "").IndexOf(Convert.ToString(want, CultureInfo.InvariantCulture) ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
            if (v is double || v is int)
            {
                double have = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                double w;
                if (!double.TryParse(Convert.ToString(want, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out w))
                    return op == "ne" ? !string.Equals(text, Convert.ToString(want), StringComparison.OrdinalIgnoreCase)
                                      : op == "eq" && string.Equals(text, Convert.ToString(want), StringComparison.OrdinalIgnoreCase);
                double tol = Math.Max(1e-6, Math.Abs(w) * 1e-6);
                switch (op)
                {
                    case "eq": return Math.Abs(have - w) <= tol;
                    case "ne": return Math.Abs(have - w) > tol;
                    case "gt": return have > w;
                    case "lt": return have < w;
                    case "gte": return have >= w - tol;
                    case "lte": return have <= w + tol;
                }
            }
            string ws = Convert.ToString(want, CultureInfo.InvariantCulture) ?? "";
            string hs = v is bool b ? (b ? "true" : "false") : v is long || v is int ? Convert.ToString(v, CultureInfo.InvariantCulture) : (text ?? "");
            bool eq = string.Equals(hs, ws, StringComparison.OrdinalIgnoreCase) || string.Equals(text ?? "", ws, StringComparison.OrdinalIgnoreCase);
            if (op == "eq") return eq;
            if (op == "ne") return !eq;
            int cmp = string.Compare(text ?? "", ws, StringComparison.OrdinalIgnoreCase);
            switch (op)
            {
                case "gt": return cmp > 0;
                case "lt": return cmp < 0;
                case "gte": return cmp >= 0;
                case "lte": return cmp <= 0;
            }
            throw new ToolError($"Unknown op '{op}' (eq, ne, contains, gt, lt, gte, lte, empty, not_empty).");
        }

        static object Query(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            bool types = a.Bool("types", false);
            List<string> cats = a.Strs("categories");
            string cls = a.Str("class");
            View inView = a.Has("view") ? Rv.View(doc, a.Str("view"), null) : a.Bool("in_view", false) ? c.UiDoc.ActiveView : null;
            bool selection = a.Bool("selection", false);
            List<Args> where = a.Objs("where");
            bool filtered = cats.Count > 0 || cls != null || inView != null || selection || a.Has("level") || a.Has("type_contains") || a.Has("name_contains") || where.Count > 0;

            IEnumerable<Element> elements;
            if (selection)
            {
                elements = c.UiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null);
                if (cats.Count > 0 || cls != null)
                {
                    var catIds = new HashSet<long>(cats.Select(n => Id.V(Rv.Category(doc, n).Id)));
                    Type t = ApiClass(cls);
                    elements = elements.Where(e => (catIds.Count == 0 || (e.Category != null && catIds.Contains(Id.V(e.Category.Id)))) && (t == null || t.IsInstanceOfType(e)));
                }
            }
            else
            {
                FilteredElementCollector col = inView != null ? new FilteredElementCollector(doc, inView.Id) : new FilteredElementCollector(doc);
                col = types ? col.WhereElementIsElementType() : col.WhereElementIsNotElementType();
                if (cats.Count > 0)
                {
                    var bics = cats.Select(n => Rv.Bic(Rv.Category(doc, n))).ToList();
                    col = bics.Count == 1 ? col.OfCategory(bics[0]) : col.WherePasses(new ElementMulticategoryFilter(bics));
                }
                if (cls != null)
                {
                    try { col = col.OfClass(ApiClass(cls)); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException)
                    {
                        throw new ToolError($"Revit cannot filter by class '{cls}' directly (e.g. Room, Area): filter by category instead.");
                    }
                }
                elements = col;
                if (cats.Count == 0 && cls == null) elements = elements.Where(e => e.Category != null && e.Category.CategoryType != CategoryType.Invalid);
            }
            if (a.Has("level"))
            {
                string lv = a.Str("level");
                elements = elements.Where(e => string.Equals(Rv.LevelName(e), lv, StringComparison.OrdinalIgnoreCase));
            }
            if (a.Has("type_contains"))
            {
                string tc = a.Str("type_contains");
                elements = elements.Where(e =>
                {
                    ElementType t = e as ElementType ?? Rv.TypeElement(e);
                    return t != null && Rv.TypeLabel(t).IndexOf(tc, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }
            if (a.Has("name_contains"))
            {
                string nc = a.Str("name_contains");
                elements = elements.Where(e => Rv.SafeName(e).IndexOf(nc, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            foreach (Args cond in where)
            {
                Args captured = cond;
                elements = elements.Where(e => Matches(e, captured));
            }

            string groupBy = a.Str("group_by");
            string detail = (a.Str("detail") ?? (filtered ? "summary" : "count")).ToLowerInvariant();
            if (detail == "count" && groupBy == null) groupBy = filtered ? null : "category";

            if (groupBy != null)
            {
                Func<Element, string> key;
                switch (groupBy.ToLowerInvariant())
                {
                    case "family": key = e => (e as ElementType ?? Rv.TypeElement(e))?.FamilyName ?? "(none)"; break;
                    case "type": key = e => { ElementType t = e as ElementType ?? Rv.TypeElement(e); return t == null ? "(none)" : Rv.TypeLabel(t); }; break;
                    case "level": key = e => Rv.LevelName(e) ?? "(none)"; break;
                    default: key = e => e.Category?.Name ?? "(none)"; break;
                }
                var groups = elements.GroupBy(key).Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(g => g.Count).ToList();
                return new Dictionary<string, object>
                {
                    ["total"] = groups.Sum(g => g.Count),
                    ["group_by"] = groupBy,
                    ["groups"] = groups.Take(a.Int("limit", 200)).Select(g => (object)new Dictionary<string, object> { ["key"] = g.Key, ["count"] = g.Count }).ToList(),
                    ["note"] = filtered ? null : "Unfiltered query: counts per category. Add categories / class / in_view for element rows.",
                };
            }
            if (detail == "count") return new Dictionary<string, object> { ["total"] = elements.Count() };

            List<Element> all = elements.ToList();
            int offset = a.Int("offset", 0), limit = a.Int("limit", 200);
            List<string> fields = a.Strs("fields");
            bool full = detail == "full";
            var rows = all.Skip(offset).Take(limit).Select(e =>
            {
                Dictionary<string, object> d = Rv.Summary(e);
                if (full)
                {
                    var loc = Rv.Location(e);
                    if (loc != null) d["location"] = loc;
                    var box = Rv.Box(e);
                    if (box != null) d["bbox"] = box;
                }
                if (fields.Count > 0)
                {
                    var vals = new Dictionary<string, object>();
                    foreach (string f in fields)
                    {
                        Parameter p = Rv.Param(e, f) ?? (Rv.TypeElement(e) is ElementType t ? Rv.Param(t, f) : null);
                        vals[f] = p == null ? null : p.StorageType == StorageType.ElementId ? (object)Rv.SafeValueString(p) : Rv.Value(p);
                    }
                    d["values"] = vals;
                }
                return (object)d;
            }).ToList();
            var result = new Dictionary<string, object> { ["total"] = all.Count, ["offset"] = offset, ["elements"] = rows };
            if (offset + rows.Count < all.Count) result["next_offset"] = offset + rows.Count;
            return result;
        }

        static object Info(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            List<ElementId> ids = a.Ids("element_ids");
            if (ids.Count > 50) throw new ToolError("At most 50 elements per call; use query_elements with fields for more.");
            bool withParams = a.Bool("parameters", true), withType = a.Bool("include_type", false);
            var list = new List<object>();
            foreach (Element e in Rv.GetMany(doc, ids))
            {
                Dictionary<string, object> d = Rv.Summary(e);
                d["class"] = e.GetType().Name;
                d["unique_id"] = e.UniqueId;
                var loc = Rv.Location(e);
                if (loc != null) d["location"] = loc;
                var box = Rv.Box(e);
                if (box != null) d["bbox"] = box;
                if (e is FamilyInstance fi)
                {
                    if (fi.Host != null) d["host"] = Rv.Summary(fi.Host);
                    if (fi.SuperComponent != null) d["super_component"] = Id.V(fi.SuperComponent.Id);
                    try { d["facing"] = U.Pt(fi.FacingOrientation); } catch (Exception) { }
                }
                if (Id.Valid(e.GroupId)) d["group"] = doc.GetElement(e.GroupId)?.Name;
                if (doc.IsWorkshared)
                {
                    try { d["workset"] = doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name; } catch (Exception) { }
                }
                try
                {
                    if (e.HasPhases())
                    {
                        if (Id.Valid(e.CreatedPhaseId)) d["phase_created"] = doc.GetElement(e.CreatedPhaseId)?.Name;
                        if (Id.Valid(e.DemolishedPhaseId)) d["phase_demolished"] = doc.GetElement(e.DemolishedPhaseId)?.Name;
                    }
                }
                catch (Exception) { }
                if (withParams)
                    d["parameters"] = e.Parameters.Cast<Parameter>().Where(p => p.Definition != null)
                        .OrderBy(p => p.Definition.Name).Select(p => (object)Rv.ParamInfo(p)).ToList();
                if (withType && Rv.TypeElement(e) is ElementType t)
                    d["type_parameters"] = t.Parameters.Cast<Parameter>().Where(p => p.Definition != null)
                        .OrderBy(p => p.Definition.Name).Select(p => (object)Rv.ParamInfo(p)).ToList();
                list.Add(d);
            }
            return new Dictionary<string, object> { ["elements"] = list };
        }

        static object ListTypes(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            FilteredElementCollector col = new FilteredElementCollector(doc).WhereElementIsElementType();
            if (a.Has("category")) col = col.OfCategory(Rv.Bic(Rv.Category(doc, a.Str("category"))));
            if (a.Has("class")) col = col.OfClass(ApiClass(a.Str("class")));
            if (!a.Has("category") && !a.Has("class")) throw new ToolError("Give a category (e.g. Walls, Doors) or a class (e.g. WallType).");
            IEnumerable<ElementType> types = col.OfType<ElementType>();
            if (a.Has("name_contains"))
            {
                string n = a.Str("name_contains");
                types = types.Where(t => Rv.TypeLabel(t).IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            List<ElementType> all = types.OrderBy(Rv.TypeLabel).ToList();
            Dictionary<long, int> counts = null;
            if (a.Bool("count", false))
            {
                counts = new Dictionary<long, int>();
                FilteredElementCollector inst = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                if (a.Has("category")) inst = inst.OfCategory(Rv.Bic(Rv.Category(doc, a.Str("category"))));
                foreach (Element e in inst)
                {
                    long k = Id.V(e.GetTypeId());
                    counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
                }
            }
            int offset = a.Int("offset", 0), limit = a.Int("limit", 200);
            var rows = all.Skip(offset).Take(limit).Select(t =>
            {
                var d = new Dictionary<string, object>
                {
                    ["id"] = Id.V(t.Id),
                    ["label"] = Rv.TypeLabel(t),
                    ["family"] = t.FamilyName,
                    ["type"] = t.Name,
                    ["category"] = t.Category?.Name,
                };
                if (t is FamilySymbol fs) d["active"] = fs.IsActive;
                if (counts != null) d["instances"] = counts.TryGetValue(Id.V(t.Id), out int n) ? n : 0;
                return (object)d;
            }).ToList();
            return new Dictionary<string, object> { ["total"] = all.Count, ["offset"] = offset, ["types"] = rows };
        }

        static object Datums(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            return new Dictionary<string, object>
            {
                ["levels"] = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation)
                    .Select(l => (object)new Dictionary<string, object> { ["id"] = Id.V(l.Id), ["name"] = l.Name, ["elevation_mm"] = U.Mm(l.Elevation) }).ToList(),
                ["grids"] = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => g.Name)
                    .Select(g =>
                    {
                        var d = new Dictionary<string, object> { ["id"] = Id.V(g.Id), ["name"] = g.Name };
                        try
                        {
                            d["start"] = U.Pt(g.Curve.GetEndPoint(0));
                            d["end"] = U.Pt(g.Curve.GetEndPoint(1));
                            d["curved"] = !(g.Curve is Line);
                        }
                        catch (Exception) { }
                        return (object)d;
                    }).ToList(),
            };
        }
    }
}
