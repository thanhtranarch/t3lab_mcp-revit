using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace T3LabRevit.Core
{
    /// <summary>ElementId across Revit 2022-2027: IntegerValue (int) until 2023, Value (long) from 2024, int API gone in 2026.</summary>
    internal static class Id
    {
#if REVIT2024_OR_GREATER
        public static long V(ElementId id) => id?.Value ?? -1;
        public static ElementId Make(long v) => new ElementId(v);
#else
        public static long V(ElementId id) => id?.IntegerValue ?? -1;
        public static ElementId Make(long v) => new ElementId((int)v);
#endif
        public static bool Valid(ElementId id) => id != null && id != ElementId.InvalidElementId;

        public static object Json(ElementId id) => Valid(id) ? (object)V(id) : null;
    }

    /// <summary>Units: every tool speaks millimetres, m², m³ and degrees; Revit stores feet and radians.</summary>
    internal static class U
    {
        public const double MmPerFt = 304.8;
        public static double Ft(double mm) => mm / MmPerFt;
        public static double Mm(double ft) => Math.Round(ft * MmPerFt, 3);
        public static double Deg(double rad) => Math.Round(rad * 180.0 / Math.PI, 4);
        public static double Rad(double deg) => deg * Math.PI / 180.0;
        public static double[] Pt(XYZ p) => p == null ? null : new[] { Mm(p.X), Mm(p.Y), Mm(p.Z) };
    }

    internal static class Rv
    {
        // ---------------------------------------------------------------- lookup

        public static Element Get(Document doc, ElementId id)
        {
            Element e = doc.GetElement(id);
            if (e == null) throw new ToolError($"No element with id {Id.V(id)} in '{doc.Title}'.");
            return e;
        }

        public static List<Element> GetMany(Document doc, IEnumerable<ElementId> ids)
        {
            var list = new List<Element>();
            var missing = new List<long>();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) missing.Add(Id.V(id));
                else list.Add(e);
            }
            if (missing.Count > 0) throw new ToolError("No element with id " + string.Join(", ", missing.Take(10)) + $" in '{doc.Title}'.");
            return list;
        }

        public static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant().Replace("_", " ");

        /// <summary>"Walls", "OST_Walls", "walls", or a localized category name.</summary>
        public static Category Category(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string n = name.Trim();
            string ost = n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? n : "OST_" + n.Replace(" ", "");
            if (Enum.TryParse(ost, true, out BuiltInCategory bic))
            {
                try
                {
                    Category c = global::Autodesk.Revit.DB.Category.GetCategory(doc, bic);
                    if (c != null) return c;
                }
                catch (Exception) { /* not a real category */ }
            }
            string want = Norm(n);
            foreach (Category c in doc.Settings.Categories)
            {
                if (Norm(c.Name) == want) return c;
                foreach (Category sub in c.SubCategories)
                    if (Norm(sub.Name) == want) return sub;
            }
            throw new ToolError($"Unknown category '{name}'. Use a name as Revit shows it (Walls, Doors, Structural Columns) or OST_Walls.");
        }

        public static BuiltInCategory Bic(Category c) => c == null ? BuiltInCategory.INVALID : (BuiltInCategory)(int)Id.V(c.Id);

        public static Level Level(Document doc, string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return null;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            if (long.TryParse(nameOrId, out long idv))
            {
                Level byId = levels.FirstOrDefault(l => Id.V(l.Id) == idv);
                if (byId != null) return byId;
            }
            Level byName = levels.FirstOrDefault(l => string.Equals(l.Name, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
            throw new ToolError($"No level '{nameOrId}'. Levels: " + string.Join(", ", levels.OrderBy(l => l.Elevation).Select(l => l.Name).Take(40)));
        }

        /// <summary>The level named, or the active view's level, or the lowest level.</summary>
        public static Level LevelOrDefault(Document doc, View active, string nameOrId)
        {
            if (!string.IsNullOrWhiteSpace(nameOrId)) return Level(doc, nameOrId);
            if (active?.GenLevel != null) return active.GenLevel;
            Level low = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
            if (low == null) throw new ToolError("The model has no level; create one with create_levels first.");
            return low;
        }

        /// <summary>A type of class T by "Family: Type", "Type", or id; null name = the first one.</summary>
        public static T TypeOf<T>(Document doc, string name, BuiltInCategory bic = BuiltInCategory.INVALID) where T : ElementType
        {
            var col = new FilteredElementCollector(doc).OfClass(typeof(T));
            if (bic != BuiltInCategory.INVALID) col = col.OfCategory(bic);
            List<T> all = col.Cast<T>().ToList();
            if (all.Count == 0) throw new ToolError($"The model has no {typeof(T).Name}" + (bic != BuiltInCategory.INVALID ? " of " + bic : "") + ". Load a family first (load_family).");
            if (string.IsNullOrWhiteSpace(name)) return all[0];
            T found = FindType(all, name);
            if (found != null) return found;
            throw new ToolError($"No type '{name}'. Available: " + string.Join(" | ", all.Select(TypeLabel).Take(40)));
        }

        public static T FindType<T>(IEnumerable<T> all, string name) where T : ElementType
        {
            string n = name.Trim();
            if (long.TryParse(n, out long idv))
            {
                T byId = all.FirstOrDefault(t => Id.V(t.Id) == idv);
                if (byId != null) return byId;
            }
            return all.FirstOrDefault(t => string.Equals(TypeLabel(t), n, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(t => TypeLabel(t).IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string TypeLabel(ElementType t) => string.IsNullOrEmpty(t.FamilyName) ? t.Name : t.FamilyName + ": " + t.Name;

        public static View View(Document doc, string nameOrId, View fallback)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
            {
                if (fallback == null) throw new ToolError("No active view.");
                return fallback;
            }
            if (long.TryParse(nameOrId, out long idv) && doc.GetElement(Id.Make(idv)) is View byId) return byId;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            View found = views.FirstOrDefault(v => string.Equals(v.Name, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase))
                         ?? views.FirstOrDefault(v => string.Equals(v.Title, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found == null) throw new ToolError($"No view '{nameOrId}'. Use list_views to see names and ids.");
            return found;
        }

        // ---------------------------------------------------------------- describe

        public static ElementType TypeElement(Element e)
        {
            ElementId tid = e.GetTypeId();
            return Id.Valid(tid) ? e.Document.GetElement(tid) as ElementType : null;
        }

        /// <summary>id, category, family, type, name, level: one line per element in lists.</summary>
        public static Dictionary<string, object> Summary(Element e)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id.V(e.Id),
                ["category"] = e.Category?.Name,
                ["name"] = SafeName(e),
            };
            ElementType t = TypeElement(e);
            if (t != null)
            {
                d["family"] = t.FamilyName;
                d["type"] = t.Name;
            }
            string level = LevelName(e);
            if (level != null) d["level"] = level;
            return d;
        }

        public static string SafeName(Element e)
        {
            try { return e.Name; }
            catch (Exception) { return ""; }
        }

        public static string LevelName(Element e)
        {
            try
            {
                ElementId lid = e.LevelId;
                if (!Id.Valid(lid))
                {
                    Parameter p = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                    lid = p?.AsElementId();
                }
                return Id.Valid(lid) ? e.Document.GetElement(lid)?.Name : null;
            }
            catch (Exception) { return null; }
        }

        public static Dictionary<string, object> Location(Element e)
        {
            switch (e.Location)
            {
                case LocationPoint lp:
                    var d = new Dictionary<string, object> { ["point"] = U.Pt(lp.Point) };
                    try { d["rotation_deg"] = U.Deg(lp.Rotation); } catch (Exception) { }
                    return d;
                case LocationCurve lc:
                    Curve c = lc.Curve;
                    return new Dictionary<string, object>
                    {
                        ["start"] = U.Pt(c.GetEndPoint(0)),
                        ["end"] = U.Pt(c.GetEndPoint(1)),
                        ["length_mm"] = U.Mm(c.Length),
                        ["curve"] = c.GetType().Name,
                    };
            }
            return null;
        }

        public static Dictionary<string, object> Box(Element e, View view = null)
        {
            BoundingBoxXYZ b = null;
            try { b = e.get_BoundingBox(view); } catch (Exception) { }
            if (b == null) return null;
            return new Dictionary<string, object> { ["min"] = U.Pt(b.Min), ["max"] = U.Pt(b.Max) };
        }

        // ---------------------------------------------------------------- parameters

        static bool Is(ForgeTypeId spec, ForgeTypeId want)
        {
            try { return spec != null && want != null && spec.TypeId == want.TypeId; }
            catch (Exception) { return false; }
        }

        static ForgeTypeId Spec(Parameter p)
        {
            try { return p.Definition.GetDataType(); }
            catch (Exception) { return null; }
        }

        /// <summary>A Double in tool units (mm, m², m³, degrees) or Revit's raw value for other specs.</summary>
        public static double ToToolUnits(Parameter p, double raw)
        {
            ForgeTypeId s = Spec(p);
            if (Is(s, SpecTypeId.Length)) return U.Mm(raw);
            if (Is(s, SpecTypeId.Area)) return Math.Round(raw * 0.09290304, 6);
            if (Is(s, SpecTypeId.Volume)) return Math.Round(raw * 0.028316846592, 6);
            if (Is(s, SpecTypeId.Angle)) return U.Deg(raw);
            return raw;
        }

        public static double FromToolUnits(Parameter p, double value)
        {
            ForgeTypeId s = Spec(p);
            if (Is(s, SpecTypeId.Length)) return U.Ft(value);
            if (Is(s, SpecTypeId.Area)) return value / 0.09290304;
            if (Is(s, SpecTypeId.Volume)) return value / 0.028316846592;
            if (Is(s, SpecTypeId.Angle)) return U.Rad(value);
            return value;
        }

        public static string UnitOf(Parameter p)
        {
            ForgeTypeId s = Spec(p);
            if (Is(s, SpecTypeId.Length)) return "mm";
            if (Is(s, SpecTypeId.Area)) return "m2";
            if (Is(s, SpecTypeId.Volume)) return "m3";
            if (Is(s, SpecTypeId.Angle)) return "deg";
            return null;
        }

        /// <summary>The value an AI can use: number in tool units, text, id, or yes/no.</summary>
        public static object Value(Parameter p)
        {
            if (p == null || !p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.Double: return ToToolUnits(p, p.AsDouble());
                case StorageType.Integer:
                    try
                    {
                        if (Is(Spec(p), SpecTypeId.Boolean.YesNo)) return p.AsInteger() != 0;
                    }
                    catch (Exception) { }
                    return p.AsInteger();
                case StorageType.String: return p.AsString();
                case StorageType.ElementId: return Id.Json(p.AsElementId());
            }
            return null;
        }

        public static Dictionary<string, object> ParamInfo(Parameter p)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = p.Definition?.Name,
                ["value"] = Value(p),
                ["display"] = SafeValueString(p),
                ["storage"] = p.StorageType.ToString(),
                ["read_only"] = p.IsReadOnly,
            };
            string unit = UnitOf(p);
            if (unit != null) d["unit"] = unit;
            if (p.IsShared) d["shared"] = true;
            try
            {
                if (p.Definition is InternalDefinition idf && idf.BuiltInParameter != BuiltInParameter.INVALID)
                    d["builtin"] = idf.BuiltInParameter.ToString();
            }
            catch (Exception) { }
            return d;
        }

        public static string SafeValueString(Parameter p)
        {
            try
            {
                if (p.StorageType == StorageType.String) return p.AsString();
                if (p.StorageType == StorageType.ElementId)
                {
                    ElementId id = p.AsElementId();
                    Element e = Id.Valid(id) ? p.Element?.Document?.GetElement(id) : null;
                    if (e != null) return SafeName(e);
                }
                return p.AsValueString();
            }
            catch (Exception) { return null; }
        }

        /// <summary>A parameter by name (instance first) or by BuiltInParameter name (e.g. ALL_MODEL_MARK).</summary>
        public static Parameter Param(Element e, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (name.StartsWith("BuiltInParameter.", StringComparison.Ordinal)) name = name.Substring(17);
            if (name.ToUpperInvariant() == name && name.Contains("_") && Enum.TryParse(name, out BuiltInParameter bip))
            {
                try
                {
                    Parameter b = e.get_Parameter(bip);
                    if (b != null) return b;
                }
                catch (Exception) { }
            }
            Parameter p = e.LookupParameter(name);
            if (p != null) return p;
            foreach (Parameter q in e.Parameters)
                if (string.Equals(q.Definition?.Name, name, StringComparison.OrdinalIgnoreCase)) return q;
            return null;
        }

        /// <summary>
        /// Sets a parameter from a JSON value. Numbers are tool units (mm, m², m³, degrees); a string on a number
        /// parameter goes through Revit's own unit parsing ("1200", "1.2 m", "45°"); ids by number or by element name.
        /// </summary>
        public static void SetParam(Parameter p, object value)
        {
            if (p == null) throw new ToolError("Parameter not found.");
            if (p.IsReadOnly) throw new ToolError($"Parameter '{p.Definition.Name}' is read-only.");
            bool ok;
            switch (p.StorageType)
            {
                case StorageType.Double:
                    if (value is string s && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        ok = p.SetValueString(s);
                    else ok = p.Set(FromToolUnits(p, Args.ToDouble(value, p.Definition.Name)));
                    break;
                case StorageType.Integer:
                    if (value is bool b) ok = p.Set(b ? 1 : 0);
                    else if (value is string si && (si.Equals("yes", StringComparison.OrdinalIgnoreCase) || si.Equals("true", StringComparison.OrdinalIgnoreCase))) ok = p.Set(1);
                    else if (value is string sn && (sn.Equals("no", StringComparison.OrdinalIgnoreCase) || sn.Equals("false", StringComparison.OrdinalIgnoreCase))) ok = p.Set(0);
                    else ok = p.Set((int)Math.Round(Args.ToDouble(value, p.Definition.Name)));
                    break;
                case StorageType.String:
                    ok = p.Set(value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case StorageType.ElementId:
                    ok = p.Set(ResolveIdValue(p, value));
                    break;
                default:
                    throw new ToolError($"Parameter '{p.Definition.Name}' cannot be set.");
            }
            if (!ok) throw new ToolError($"Revit refused the value '{value}' for '{p.Definition.Name}'.");
        }

        static ElementId ResolveIdValue(Parameter p, object value)
        {
            if (value == null) return ElementId.InvalidElementId;
            string s = Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            if (long.TryParse(s, out long v)) return Id.Make(v);
            Document doc = p.Element.Document;
            Element named = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.OrdinalIgnoreCase))
                            ?? new FilteredElementCollector(doc).OfClass(typeof(Material)).FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.OrdinalIgnoreCase))
                            ?? new FilteredElementCollector(doc).OfClass(typeof(Phase)).FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.OrdinalIgnoreCase))
                            ?? new FilteredElementCollector(doc).WhereElementIsElementType().FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.OrdinalIgnoreCase));
            if (named == null) throw new ToolError($"'{s}' is not an element id or the name of a level, material, phase or type.");
            return named.Id;
        }

        public static List<object> IdList(IEnumerable<ElementId> ids) => ids.Select(i => (object)Id.V(i)).ToList();
    }
}
