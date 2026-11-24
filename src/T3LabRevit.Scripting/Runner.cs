using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;

namespace T3LabRevit.Scripting
{
    /// <summary>
    /// Runs one IronPython 3.4 snippet for send_code_to_revit, on Revit's main thread (the caller's job). Called by
    /// T3LabRevit.Core through reflection: Run(code, uiApplication, stdlibDir) -> {"result", "output"}; a Python error
    /// throws ScriptError with the traceback. Results are turned into plain .NET values (dict, list, string, number,
    /// bool, null) so they cross the AssemblyLoadContext boundary and serialize to JSON.
    /// </summary>
    public static class Runner
    {
        static ScriptEngine _engine;
        static readonly object Gate = new object();

        const string Prelude = @"
import clr
import Autodesk.Revit.DB as DB
import Autodesk.Revit.UI as UI
from Autodesk.Revit.DB import *
import System
";

        public static string Version => "IronPython " + typeof(Python).Assembly.GetName().Version;

        static ScriptEngine Engine(string stdlib)
        {
            lock (Gate)
            {
                if (_engine != null) return _engine;
                var e = Python.CreateEngine();
                var paths = e.GetSearchPaths();
                if (!string.IsNullOrEmpty(stdlib) && Directory.Exists(stdlib))
                {
                    paths.Add(stdlib);
                    string site = Path.Combine(stdlib, "site-packages");
                    if (Directory.Exists(site)) paths.Add(site);
                }
                e.SetSearchPaths(paths);
                e.Runtime.LoadAssembly(typeof(Document).Assembly);      // RevitAPI
                e.Runtime.LoadAssembly(typeof(UIApplication).Assembly); // RevitAPIUI
                _engine = e;
                return e;
            }
        }

        public static Dictionary<string, object> Run(string code, object uiapp, string stdlib)
        {
            ScriptEngine engine = Engine(stdlib);
            var app = uiapp as UIApplication;
            UIDocument uidoc = app?.ActiveUIDocument;
            ScriptScope scope = engine.CreateScope();
            scope.SetVariable("uiapp", app);
            scope.SetVariable("app", app?.Application);
            scope.SetVariable("uidoc", uidoc);
            scope.SetVariable("doc", uidoc?.Document);
            scope.SetVariable("revit_version", app?.Application.VersionNumber);
            scope.SetVariable("result", null);
            scope.SetVariable("output", new IronPython.Runtime.PythonList());
            scope.SetVariable("eid", (Func<long, ElementId>)Make);
            scope.SetVariable("eid_value", (Func<ElementId, long>)Value);
            scope.SetVariable("mm", (Func<double, double>)(ft => ft * 304.8));
            scope.SetVariable("ft", (Func<double, double>)(mm => mm / 304.8));

            var stdout = new MemoryStream();
            engine.Runtime.IO.SetOutput(stdout, new UTF8Encoding(false));
            engine.Runtime.IO.SetErrorOutput(stdout, new UTF8Encoding(false));
            try
            {
                engine.CreateScriptSourceFromString(Prelude, Microsoft.Scripting.SourceCodeKind.Statements).Execute(scope);
                engine.CreateScriptSourceFromString(code, "<send_code_to_revit>", Microsoft.Scripting.SourceCodeKind.Statements).Execute(scope);
            }
            catch (Exception ex)
            {
                string trace;
                try { trace = engine.GetService<ExceptionOperations>().FormatException(ex); }
                catch (Exception) { trace = ex.GetType().Name + ": " + ex.Message; }
                string printed = Text(stdout);
                throw new ScriptError(trace + (printed.Length > 0 ? "\n--- printed before the error ---\n" + Clip(printed, 4000) : ""));
            }

            var outList = new List<object>();
            if (scope.TryGetVariable("output", out object o) && o is IList items)
                foreach (object item in items) outList.Add(Plain(item, 0));
            scope.TryGetVariable("result", out object r);
            return new Dictionary<string, object>
            {
                ["result"] = Plain(r, 0),
                ["output"] = outList,
                ["printed"] = Clip(Text(stdout), 20000),
            };
        }

        static string Text(MemoryStream s) => Encoding.UTF8.GetString(s.ToArray()).TrimEnd();

        static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "\n... [truncated]";

#if REVIT2024_OR_GREATER
        static ElementId Make(long v) => new ElementId(v);
        static long Value(ElementId id) => id.Value;
#else
        static ElementId Make(long v) => new ElementId((int)v);
        static long Value(ElementId id) => id.IntegerValue;
#endif

        /// <summary>Python / Revit values -> JSON-friendly .NET values (ElementId -> integer, XYZ -> [x, y, z] in mm, Element -> summary).</summary>
        static object Plain(object v, int depth)
        {
            if (v == null) return null;
            if (depth > 8) return v.ToString();
            switch (v)
            {
                case string s: return s;
                case bool b: return b;
                case int _: case long _: case double _: case float _: case decimal _: case short _: case byte _: return v;
                case BigInteger bi: return bi >= long.MinValue && bi <= long.MaxValue ? (object)(long)bi : bi.ToString();
                case ElementId id: return Value(id);
                case XYZ p: return new[] { Math.Round(p.X * 304.8, 3), Math.Round(p.Y * 304.8, 3), Math.Round(p.Z * 304.8, 3) };
                case Element e:
                    string name;
                    try { name = e.Name; } catch (Exception) { name = ""; }
                    return new Dictionary<string, object> { ["id"] = Value(e.Id), ["category"] = e.Category?.Name, ["name"] = name };
                case IDictionary d:
                {
                    var outD = new Dictionary<string, object>();
                    foreach (DictionaryEntry kv in d) outD[Convert.ToString(Plain(kv.Key, depth + 1))] = Plain(kv.Value, depth + 1);
                    return outD;
                }
                case IEnumerable seq:
                {
                    var list = new List<object>();
                    foreach (object item in seq)
                    {
                        if (list.Count >= 5000) { list.Add("... more"); break; }
                        list.Add(Plain(item, depth + 1));
                    }
                    return list;
                }
            }
            return v.ToString();
        }
    }

    public sealed class ScriptError : Exception
    {
        public ScriptError(string message) : base(message) { }
    }
}
