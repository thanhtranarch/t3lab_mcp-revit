using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace T3LabRevit.Loader
{
    /// <summary>
    /// Panel "T3Lab MCP" on Revit's own Add-Ins tab (owner's request, 2026-11-27: no tab of our own). Same buttons, order,
    /// names, icons (T3Icons) and tooltips as T3Lab CAD MCP's panel on AutoCAD's Add-ins tab:
    /// [Start / Stop] [Agent Cursors] [Connect AI] [Status | Reload].
    /// </summary>
    internal static class T3Ribbon
    {
        internal const string PanelName = "T3Lab MCP";
        const string Host = "Revit";

        static readonly List<KeyValuePair<PushButton, string>> Buttons = new List<KeyValuePair<PushButton, string>>();

        internal static void Create(UIControlledApplication app)
        {
            foreach (RibbonPanel existing in app.GetRibbonPanels(Tab.AddIns))
                if (existing.Name == PanelName) return;   // already built in this session
            RibbonPanel panel = app.CreateRibbonPanel(Tab.AddIns, PanelName);
            string dll = typeof(T3Ribbon).Assembly.Location;

            Add(panel, Button(dll, "t3mcp_startstop", "Start /\nStop", typeof(StartStopCommand), "power",
                $"Start or stop the MCP server that lets AI apps (Claude, ChatGPT / Codex, Cursor, Gemini...) work in this {Host}. " +
                $"Stopped, it stays stopped at the next {Host} start until you press Start again."));
            Add(panel, Button(dll, "t3mcp_cursors", "Agent\nCursors", typeof(AgentCursorsCommand), "cursors",
                $"Show what each AI app does in {Host}: its goal, the current step, the last calls and the elements it touched. " +
                "Pause AI changes at any time. Press again to turn it off."));
            Add(panel, Button(dll, "t3mcp_connect", "Connect\nAI", typeof(ConnectCommand), "connect",
                $"Add this {Host} to Claude Desktop, Claude Code, Codex, Cursor, VS Code, Gemini CLI, Windsurf and Antigravity in one click, " +
                "or check what is connected."));

            PushButtonData status = Button(dll, "t3mcp_status", "Status", typeof(StatusCommand), "status",
                "Server status: port, tools, calls, the call queue, Agent Cursors and the log folder.");
            PushButtonData reload = Button(dll, "t3mcp_reload", "Reload", typeof(ReloadCommand), "reload",
                $"Load T3LabRevit.Core.dll again after an update, without restarting {Host}.");
            IList<RibbonItem> stacked = panel.AddStackedItems(status, reload);
            if (stacked != null && stacked.Count == 2)
            {
                if (stacked[0] is PushButton s) Buttons.Add(new KeyValuePair<PushButton, string>(s, "status"));
                if (stacked[1] is PushButton r) Buttons.Add(new KeyValuePair<PushButton, string>(r, "reload"));
            }
            WatchTheme(app);
        }

        static void Add(RibbonPanel panel, PushButtonData data)
        {
            if (panel.AddItem(data) is PushButton button) Buttons.Add(new KeyValuePair<PushButton, string>(button, IconOf[data.Name]));
        }

        static readonly Dictionary<string, string> IconOf = new Dictionary<string, string>();

        static PushButtonData Button(string dll, string name, string text, Type cmd, string icon, string tip)
        {
            IconOf[name] = icon;
            bool dark = Dark();
            return new PushButtonData(name, text, dll, cmd.FullName)
            {
                ToolTip = tip,
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
                LargeImage = T3Icons.Get(icon, 32, dark),
                Image = T3Icons.Get(icon, 16, dark),
            };
        }

        static bool Dark()
        {
#if REVIT2024_OR_GREATER
            try { return UIThemeManager.CurrentTheme == UITheme.Dark; }
            catch (Exception) { return false; }
#else
            return false;   // no dark theme before Revit 2024
#endif
        }

        /// <summary>Revit 2024+: repaint the icons when the user switches the light / dark theme (AutoCAD: COLORTHEME).</summary>
        static void WatchTheme(UIControlledApplication app)
        {
#if REVIT2024_OR_GREATER
            try { app.ThemeChanged += (s, e) => Repaint(); }
            catch (Exception) { /* the icons keep the theme of the start */ }
#endif
        }

        static void Repaint()
        {
            bool dark = Dark();
            foreach (KeyValuePair<PushButton, string> b in Buttons)
            {
                try
                {
                    b.Key.LargeImage = T3Icons.Get(b.Value, 32, dark);
                    b.Key.Image = T3Icons.Get(b.Value, 16, dark);
                }
                catch (Exception) { /* keep the old icon */ }
            }
        }
    }
}
