using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace T3LabRevit.Core
{
    /// <summary>An error the model should see and can fix: returned as a tool result with isError=true.</summary>
    internal sealed class ToolError : Exception
    {
        public ToolError(string message) : base(message) { }
    }

    /// <summary>
    /// Typed, case-insensitive access to a tool's JSON arguments.
    /// Lengths and points are millimetres in the API of every tool; Revit works in feet, so Pt / Mm convert.
    /// </summary>
    internal sealed class Args
    {
        readonly Dictionary<string, object> _d;

        public Args(object value)
        {
            _d = value is Dictionary<string, object> d
                ? new Dictionary<string, object>(d, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public IEnumerable<string> Keys => _d.Keys;

        public bool Has(string key) => _d.TryGetValue(key, out var v) && v != null;

        public object Raw(string key) => _d.TryGetValue(key, out var v) ? v : null;

        /// <summary>Takes an argument out (the revit_document pin: the tool itself never sees it).</summary>
        public string Take(string key)
        {
            string value = Str(key);
            _d.Remove(key);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public string Str(string key, string def = null)
        {
            var v = Raw(key);
            return v == null ? def : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public string ReqStr(string key)
        {
            var s = Str(key);
            if (string.IsNullOrWhiteSpace(s)) throw Missing(key);
            return s;
        }

        public double Num(string key, double def) => Has(key) ? ToDouble(Raw(key), key) : def;

        public double ReqNum(string key)
        {
            if (!Has(key)) throw Missing(key);
            return ToDouble(Raw(key), key);
        }

        /// <summary>A length in millimetres, returned in feet (Revit internal units).</summary>
        public double Mm(string key, double defMm) => U.Ft(Num(key, defMm));

        public double ReqMm(string key) => U.Ft(ReqNum(key));

        public int Int(string key, int def) => (int)Math.Round(Num(key, def));

        public bool Bool(string key, bool def)
        {
            var v = Raw(key);
            return v == null ? def : ToBool(v, key);
        }

        public static bool ToBool(object v, string key)
        {
            if (v is bool b) return b;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            throw new ToolError($"Argument '{key}' must be true or false.");
        }

        /// <summary>Point [x, y] or [x, y, z] in mm -> XYZ in feet.</summary>
        public XYZ Pt(string key, XYZ def) => Has(key) ? ToPoint(Raw(key), key) : def;

        public XYZ ReqPt(string key)
        {
            if (!Has(key)) throw Missing(key);
            return ToPoint(Raw(key), key);
        }

        public IList List(string key)
        {
            var v = Raw(key);
            if (v == null) return null;
            if (v is IList list && !(v is string)) return list;
            return new[] { v };
        }

        public List<string> Strs(string key)
        {
            var result = new List<string>();
            var list = List(key);
            if (list == null) return result;
            foreach (var o in list)
                if (o != null) result.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            return result;
        }

        public List<XYZ> Pts(string key)
        {
            var list = List(key) ?? throw Missing(key);
            var result = new List<XYZ>();
            for (int i = 0; i < list.Count; i++) result.Add(ToPoint(list[i], $"{key}[{i}]"));
            return result;
        }

        /// <summary>Element ids from a number, a numeric string, or a list of them.</summary>
        public List<ElementId> Ids(string key)
        {
            var result = new List<ElementId>();
            var list = List(key);
            if (list == null) return result;
            foreach (var o in list)
            {
                if (o == null) continue;
                result.Add(Id.Make(ToLong(o, key)));
            }
            return result;
        }

        public ElementId IdOrNull(string key) => Has(key) ? Id.Make(ToLong(Raw(key), key)) : null;

        public Args Obj(string key) => new Args(Raw(key));

        public List<Args> Objs(string key)
        {
            var result = new List<Args>();
            var list = List(key);
            if (list == null) return result;
            foreach (var o in list) result.Add(new Args(o));
            return result;
        }

        static ToolError Missing(string key) => new ToolError($"Missing required argument '{key}'.");

        public static double ToDouble(object v, string key)
        {
            try
            {
                if (v is string s) return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new ToolError($"Argument '{key}' must be a number.");
            }
        }

        public static long ToLong(object v, string key)
        {
            try
            {
                if (v is string s) return long.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new ToolError($"Argument '{key}' must be an element id (integer).");
            }
        }

        public static XYZ ToPoint(object v, string key)
        {
            if (v is string s) v = s.Split(',');
            if (v is IList l && l.Count >= 2 && l.Count <= 3)
                return new XYZ(U.Ft(ToDouble(l[0], key)), U.Ft(ToDouble(l[1], key)), l.Count == 3 ? U.Ft(ToDouble(l[2], key)) : 0);
            if (v is Dictionary<string, object> d)
            {
                var a = new Args(d);
                return new XYZ(U.Ft(a.ReqNum("x")), U.Ft(a.ReqNum("y")), U.Ft(a.Num("z", 0)));
            }
            throw new ToolError($"Argument '{key}' must be a point [x, y] or [x, y, z] in millimetres.");
        }
    }
}
