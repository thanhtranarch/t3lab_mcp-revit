using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace T3LabRevit.Loader
{
    /// <summary>
    /// Loads T3LabRevit.Core.dll from bytes (so the file is never locked and can be replaced while Revit
    /// runs) and drives it through reflection: Entry.Start(string contentsDir, object host, Action requestReload),
    /// Entry.Stop(), Entry.Status(), Entry.ToggleAgentCursors(), Entry.Running.
    /// </summary>
    internal static class CoreHost
    {
        const string CoreFile = "T3LabRevit.Core.dll";
        const string EntryTypeName = "T3LabRevit.Core.Entry";

        static Type _entry;
        static bool _reloadPending;
        internal static UIControlledApplication Uic;

        internal static string ContentsDir => Path.GetDirectoryName(typeof(CoreHost).Assembly.Location);

        /// <summary>host: UIControlledApplication (OnStartup) or UIApplication (a command, Idling).</summary>
        internal static void Start(object host)
        {
            if (_entry == null) _entry = LoadCore();
            _entry.GetMethod("Start").Invoke(null, new object[] { ContentsDir, host, (Action)RequestReload });
        }

        internal static void Stop()
        {
            _entry?.GetMethod("Stop").Invoke(null, null);
        }

        internal static bool Running =>
            _entry != null && (bool)_entry.GetProperty("Running").GetValue(null, null);

        internal static string Status() =>
            _entry == null ? "T3Lab Revit MCP: not loaded" : (string)_entry.GetMethod("Status").Invoke(null, null);

        internal static string ToggleAgentCursors() =>
            _entry == null ? "T3Lab Revit MCP: not loaded" : (string)_entry.GetMethod("ToggleAgentCursors").Invoke(null, null);

        internal static void Reload(object host)
        {
            Stop();
            _entry = LoadCore();
            Start(host);
        }

        /// <summary>Called by Core (reload_addin tool) on the main thread: reload at the next Idling, after the tool's response went out.</summary>
        static void RequestReload()
        {
            if (_reloadPending || Uic == null) return;
            _reloadPending = true;
            Uic.Idling += OnIdleReload;
        }

        static void OnIdleReload(object sender, IdlingEventArgs e)
        {
            Uic.Idling -= OnIdleReload;
            _reloadPending = false;
            try { Reload(sender as UIApplication); }
            catch (Exception ex) { Show("T3Lab Revit MCP", "Reload failed: " + Unwrap(ex).Message); }
        }

        static Type LoadCore()
        {
            string dll = Path.Combine(ContentsDir, CoreFile);
            if (!File.Exists(dll)) throw new FileNotFoundException("T3LabRevit.Core.dll is missing next to the loader.", dll);
            byte[] raw = File.ReadAllBytes(dll);
            string pdb = Path.ChangeExtension(dll, ".pdb");
            Assembly asm;
            try { asm = File.Exists(pdb) ? Assembly.Load(raw, File.ReadAllBytes(pdb)) : Assembly.Load(raw); }
            catch (BadImageFormatException) { throw; }
            catch { asm = Assembly.Load(raw); } // unreadable symbols: load without line numbers
            return asm.GetType(EntryTypeName, true);
        }

        internal static void Show(string title, string message)
        {
            try
            {
                var td = new TaskDialog(title) { MainInstruction = title, MainContent = message };
                td.Show();
            }
            catch { /* no UI yet */ }
        }

        internal static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
