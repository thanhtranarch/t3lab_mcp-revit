using System;
using System.Diagnostics;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace T3LabRevit.Core
{
    /// <summary>
    /// Called by T3LabRevit.Loader through reflection, always on Revit's main thread in an API context
    /// (OnStartup, a ribbon command, or the ExternalEvent of reload_addin).
    /// Start() picks the first free port in PortMin..PortMax (one per Revit process), the range
    /// bridge/t3lab_revit_bridge.py scans. host is a UIControlledApplication (startup) or a UIApplication.
    /// </summary>
    public static class Entry
    {
        public const string Version = "1.0.0";
        public const int PortMin = 48910, PortMax = 48918;

        internal static MainThread Main { get; private set; }
        internal static string ContentsDir { get; private set; }
        internal static string RevitVersion { get; private set; } = "";
        internal static IntPtr MainWindow { get; private set; }
        internal static volatile string ActiveDocument = "";
        internal static volatile string LastCall = "";
        internal static long CallCount;

        static Server _server;
        static Action _requestReload;
        static UIControlledApplication _uic;
        static UIApplication _uia;

        public static void Start(string contentsDir, object host, Action requestReload)
        {
            if (_server != null) return;
            ContentsDir = contentsDir;
            _requestReload = requestReload;
            _uic = host as UIControlledApplication;
            _uia = host as UIApplication;
            if (_uic == null && _uia == null) throw new ArgumentException("host must be a UIControlledApplication or a UIApplication");

            if (_uic != null)
            {
                ControlledApplication ca = _uic.ControlledApplication;
                RevitVersion = ca.VersionNumber + " (" + ca.VersionBuild + ")";
                MainWindow = SafeHandle(() => _uic.MainWindowHandle);
            }
            else
            {
                Application a = _uia.Application;
                RevitVersion = a.VersionNumber + " (" + a.VersionBuild + ")";
                MainWindow = SafeHandle(() => _uia.MainWindowHandle);
                ActiveDocument = _uia.ActiveUIDocument?.Document?.Title ?? "";
            }

            Registry.Build();
            Main = new MainThread();
            var server = new Server(Rpc.Handle);
            try { server.Start(PortMin, PortMax); }
            catch
            {
                Main.Dispose();
                Main = null;
                throw;
            }
            _server = server;
            Hook(true);
            AgentCursors.Load();
            Log.Info($"started v{Version} on 127.0.0.1:{server.Port}, {Registry.Count} tools, Revit {RevitVersion}, pid {Process.GetCurrentProcess().Id}");
        }

        public static void Stop()
        {
            if (_server == null) return;
            Hook(false);
            try { AgentCursors.Shutdown(); }
            catch (Exception ex) { Log.Error("agent cursors: " + ex.Message); }
            _server.Stop();
            _server = null;
            Main?.Dispose();
            Main = null;
            Log.Info("stopped");
        }

        public static bool Running => _server != null;

        public static string Status()
        {
            if (_server == null) return $"T3Lab Revit MCP v{Version}: stopped.";
            string last = string.IsNullOrEmpty(LastCall) ? "none" : LastCall;
            return $"T3Lab Revit MCP v{Version}: listening on 127.0.0.1:{_server.Port}\n{Registry.Count} tools · {CallCount} calls · last: {last}\n" +
                   $"Agent Cursors: {(AgentCursors.Enabled ? "on" : "off")}\nLog: {Log.FilePath}";
        }

        internal static int Port => _server?.Port ?? 0;

        /// <summary>Ribbon button: Agent Cursors on / off. Main thread; returns the message to show.</summary>
        public static string ToggleAgentCursors()
        {
            if (_server == null) return "T3Lab Revit MCP is stopped (press Start first).";
            return AgentCursors.Toggle();
        }

        internal static bool RequestReload()
        {
            if (_requestReload == null) return false;
            _requestReload();
            return true;
        }

        static IntPtr SafeHandle(Func<IntPtr> get)
        {
            try { return get(); }
            catch (Exception) { return IntPtr.Zero; }
        }

        static void Hook(bool on)
        {
            try
            {
                if (_uic != null)
                {
                    if (on)
                    {
                        _uic.ViewActivated += OnViewActivated;
                        _uic.ControlledApplication.DocumentClosing += OnDocumentClosing;
                        _uic.ControlledApplication.DocumentChanged += AgentCursors.OnDocumentChanged;
                    }
                    else
                    {
                        _uic.ViewActivated -= OnViewActivated;
                        _uic.ControlledApplication.DocumentClosing -= OnDocumentClosing;
                        _uic.ControlledApplication.DocumentChanged -= AgentCursors.OnDocumentChanged;
                    }
                }
                else if (_uia != null)
                {
                    if (on)
                    {
                        _uia.ViewActivated += OnViewActivated;
                        _uia.Application.DocumentClosing += OnDocumentClosing;
                        _uia.Application.DocumentChanged += AgentCursors.OnDocumentChanged;
                    }
                    else
                    {
                        _uia.ViewActivated -= OnViewActivated;
                        _uia.Application.DocumentClosing -= OnDocumentClosing;
                        _uia.Application.DocumentChanged -= AgentCursors.OnDocumentChanged;
                    }
                }
            }
            catch (Exception ex) { Log.Error("events: " + ex.Message); }
        }

        static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            ActiveDocument = e.Document?.Title ?? "";
        }

        static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            try
            {
                string title = e.Document?.Title;
                if (string.Equals(ActiveDocument, title, StringComparison.OrdinalIgnoreCase)) ActiveDocument = "";
                AgentCursors.ForgetDocument(title);
            }
            catch (Exception) { /* never fail a close */ }
        }
    }
}
