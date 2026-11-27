using System;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace T3LabRevit.Loader
{
    /// <summary>Entry point Revit calls through T3LabRevit.addin: starts the MCP server and adds the T3Lab MCP panel to the Add-Ins tab.</summary>
    public sealed class LoaderApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            CoreHost.Uic = application;
            if (Settings.AutoStart)
            {
                try { CoreHost.Start(application); }
                catch (Exception ex) { Settings.StartError = CoreHost.Unwrap(ex).Message; }
            }
            try { T3Ribbon.Create(application); }
            catch (Exception) { /* the server still runs; the panel is a convenience */ }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { CoreHost.Stop(); }
            catch { /* Revit is closing */ }
            return Result.Succeeded;
        }
    }

    /// <summary>%APPDATA%\T3LabRevitMCP\autostart.off turns the start with Revit off.</summary>
    internal static class Settings
    {
        internal static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "T3LabRevitMCP");
        static string OffFlag => Path.Combine(DataDir, "autostart.off");
        internal static string StartError;

        internal static bool AutoStart
        {
            get => !File.Exists(OffFlag);
            set
            {
                try
                {
                    if (value) { if (File.Exists(OffFlag)) File.Delete(OffFlag); }
                    else { Directory.CreateDirectory(DataDir); File.WriteAllText(OffFlag, "T3Lab Revit MCP does not start with Revit while this file exists."); }
                }
                catch { }
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class StartStopCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                if (CoreHost.Running)
                {
                    CoreHost.Stop();
                    Settings.AutoStart = false;
                    CoreHost.Show("T3Lab Revit MCP", "Server stopped. AI apps can no longer reach this Revit.\nIt stays off at the next Revit start until you press Start again.");
                }
                else
                {
                    CoreHost.Start(data.Application);
                    Settings.AutoStart = true;
                    Settings.StartError = null;
                    CoreHost.Show("T3Lab Revit MCP", CoreHost.Status());
                }
            }
            catch (Exception ex) { CoreHost.Show("T3Lab Revit MCP", "Could not start: " + CoreHost.Unwrap(ex).Message); }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class StatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            string text = CoreHost.Status();
            if (!string.IsNullOrEmpty(Settings.StartError)) text += "\n\nLast start error: " + Settings.StartError;
            var td = new TaskDialog("T3Lab Revit MCP")
            {
                MainInstruction = CoreHost.Running ? "MCP server is running" : "MCP server is stopped",
                MainContent = text,
                FooterText = "Connect an AI app: Connect AI button, or run install.ps1 -ConnectOnly",
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the log folder");
            if (td.Show() == TaskDialogResult.CommandLink1)
            {
                try
                {
                    string logs = Path.Combine(Settings.DataDir, "logs");
                    Directory.CreateDirectory(logs);
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + logs + "\"") { UseShellExecute = true });
                }
                catch { }
            }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class AgentCursorsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                if (!CoreHost.Running) CoreHost.Start(data.Application);
                string text = CoreHost.ToggleAgentCursors();
                if (!string.IsNullOrEmpty(text) && text.StartsWith("T3Lab Revit MCP is stopped", StringComparison.Ordinal))
                    CoreHost.Show("T3Lab Agent Cursors", text);
            }
            catch (Exception ex) { CoreHost.Show("T3Lab Agent Cursors", CoreHost.Unwrap(ex).Message); }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ReloadCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                CoreHost.Reload(data.Application);
                CoreHost.Show("T3Lab Revit MCP", "Core reloaded.\n" + CoreHost.Status());
            }
            catch (Exception ex) { CoreHost.Show("T3Lab Revit MCP", "Reload failed: " + CoreHost.Unwrap(ex).Message); }
            return Result.Succeeded;
        }
    }

    /// <summary>Runs the installer's client step (%APPDATA%\T3LabRevitMCP\install.ps1 -ConnectOnly) in a console the user can read.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ConnectCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            string script = Path.Combine(Settings.DataDir, "install.ps1");
            var td = new TaskDialog("Connect an AI app")
            {
                MainInstruction = "Connect Claude, ChatGPT (Codex), Cursor, VS Code or Gemini to this Revit",
                MainContent = "This adds the server 't3lab-revit-mcp' to every AI app found on this PC " +
                              "(config files are backed up first). Restart the AI app afterwards.\n\n" +
                              "Any AI with a terminal can also do it for you: give it the link\n" +
                              "https://github.com/thanhtranarch/t3lab_revitmcp and ask it to connect Revit.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Connect all AI apps found", "Claude Desktop, Claude Code, Codex, Cursor, VS Code, Gemini CLI, Windsurf, Antigravity");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Check only", "List what is installed and connected, change nothing");
            TaskDialogResult r = td.Show();
            if (r != TaskDialogResult.CommandLink1 && r != TaskDialogResult.CommandLink2) return Result.Cancelled;
            if (!File.Exists(script))
            {
                CoreHost.Show("Connect an AI app", "The installer is missing: " + script + "\nRun the one-line install from the README again.");
                return Result.Succeeded;
            }
            string mode = r == TaskDialogResult.CommandLink1 ? "-ConnectOnly" : "-Check";
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{script}\" {mode}") { UseShellExecute = true });
            }
            catch (Exception ex) { CoreHost.Show("Connect an AI app", "Could not start PowerShell: " + ex.Message); }
            return Result.Succeeded;
        }
    }

    /// <summary>The ribbon buttons need no document.</summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected) => true;
    }
}
