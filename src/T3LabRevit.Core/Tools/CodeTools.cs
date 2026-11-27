using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using static T3LabRevit.Core.Sch;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace T3LabRevit.Core
{
    /// <summary>
    /// send_code_to_revit: IronPython 3.4 from &lt;addin&gt;\scripting (T3LabRevit.Scripting.dll), loaded on first use.
    /// .NET 8+: its own AssemblyLoadContext, so IronPython / DLR never meet pyRevit's or Dynamo's copies; RevitAPI still
    /// resolves from Revit. .NET Framework: LoadFrom (strong-named, side by side with IronPython 2.7).
    /// </summary>
    internal static class CodeTools
    {
        static MethodInfo _run;
        static string _version;

        public static void Register()
        {
            Registry.Add("send_code_to_revit", "Run Python in Revit", ToolKind.Write,
                "Run IronPython 3.4 code (Python 3 syntax) inside Revit with the full Revit API, for anything no other tool does. " +
                "In scope: doc, uidoc, uiapp, app, DB and UI (the Autodesk.Revit namespaces, and from Autodesk.Revit.DB import *), " +
                "revit_version, eid(n) -> ElementId, eid_value(id) -> int, mm(feet) and ft(mm) unit helpers. Revit works in feet " +
                "internally. Return data by assigning result = ... (dicts, lists, numbers, strings; ElementId becomes an int, XYZ " +
                "becomes [x, y, z] mm) or output.append(...); print() is captured too. transaction=true (default): the code already " +
                "runs inside ONE transaction (one Undo step) and must NOT start its own; transaction=false: no transaction is open " +
                "(read only, or manage your own Transaction). Never open dialogs or windows. Errors return the Python traceback and " +
                "roll everything back.",
                ObjR("code", P("code", Str("Python 3 code")),
                    P("purpose", Str("What the code does, a few plain words (shown to the user)")),
                    P("transaction", Bool("Run inside one transaction (default true)"))),
                Run, needsDocument: false, tx: false, destructive: true, scope: ToolScope.Active);   // the engine is given the UI app
        }

        static string StdLib => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Entry.ContentsDir ?? "")) ?? "", "pylib");

        static MethodInfo Runner()
        {
            if (_run != null) return _run;
            string dir = Path.Combine(Entry.ContentsDir ?? "", "scripting");
            string dll = Path.Combine(dir, "T3LabRevit.Scripting.dll");
            if (!File.Exists(dll))
                throw new ToolError("Python scripting is not installed next to the add-in (" + dll + "). Run the installer again.");
            Assembly asm;
#if NETFRAMEWORK
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string file = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            asm = Assembly.LoadFrom(dll);
#else
            asm = new ScriptContext(dir).LoadFromAssemblyPath(dll);
#endif
            Type t = asm.GetType("T3LabRevit.Scripting.Runner", true);
            _version = (string)t.GetProperty("Version").GetValue(null, null);
            _run = t.GetMethod("Run");
            Log.Info("scripting loaded: " + _version);
            return _run;
        }

#if !NETFRAMEWORK
        sealed class ScriptContext : AssemblyLoadContext
        {
            readonly string _dir;
            public ScriptContext(string dir) : base("T3LabRevit.Scripting", false) { _dir = dir; }

            protected override Assembly Load(AssemblyName name)
            {
                string file = Path.Combine(_dir, name.Name + ".dll");
                // RevitAPI, WPF and the framework come from Revit's context (null = default resolution)
                return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
            }
        }
#endif

        static object Run(ToolContext c, Args a)
        {
            string code = a.ReqStr("code");
            MethodInfo run = Runner();
            Func<object> exec = () =>
            {
                try { return run.Invoke(null, new object[] { code, c.App, StdLib }); }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    Exception inner = tie.InnerException;
                    if (inner.GetType().Name == "ScriptError") throw new ToolError("Python error, nothing was changed:\n" + inner.Message);
                    throw new ToolError(inner.GetType().Name + ": " + inner.Message);
                }
            };
            object raw;
            bool tx = a.Bool("transaction", true) && c.Doc != null;
            if (tx)
            {
                if (c.Doc.IsReadOnly) throw new ToolError($"'{c.Doc.Title}' is read-only; use transaction=false for reading.");
                raw = Rpc.InTransaction(c, a.Str("purpose") ?? "Python code", exec);
            }
            else raw = exec();
            var result = raw as Dictionary<string, object> ?? new Dictionary<string, object> { ["result"] = raw };
            result["engine"] = _version;
            result["transaction"] = tx;
            return result;
        }
    }
}
