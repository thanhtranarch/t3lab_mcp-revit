using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace T3LabRevit.Loader
{
    /// <summary>Panel "T3Lab MCP" on Revit's own Add-Ins tab (owner's request, 2026-11-27: no tab of our own). Icons are drawn in
    /// code (T3 Space Line: 2-unit outline on a 32 grid, round caps, no fill, one orange detail), so the add-in ships no image files.</summary>
    internal static class T3Ribbon
    {
        internal const string PanelName = "T3Lab MCP";
        const string Orange = "#EA680C";

        internal static void Create(UIControlledApplication app)
        {
            foreach (RibbonPanel existing in app.GetRibbonPanels(Tab.AddIns))
                if (existing.Name == PanelName) return;   // already built in this session
            RibbonPanel panel = app.CreateRibbonPanel(Tab.AddIns, PanelName);
            string dll = typeof(T3Ribbon).Assembly.Location;

            Add(panel, dll, "t3mcp_startstop", "Start /\nStop", typeof(StartStopCommand),
                "Start or stop the MCP server that lets AI apps (Claude, ChatGPT/Codex, Cursor, Gemini...) work in this Revit.",
                "M16 5 L16 15", "M10 8.5 A10 10 0 1 0 22 8.5");
            Add(panel, dll, "t3mcp_cursors", "Agent\nCursors", typeof(AgentCursorsCommand),
                "Show what each AI app does in Revit: its goal, the current step, the last calls and the elements it touched. Pause AI changes at any time.",
                "M25 25 L19 19", "M7 5 L7 24 L12 19.5 L15.5 27 L19 25.5 L15.5 18 L22 18 Z");
            Add(panel, dll, "t3mcp_connect", "Connect\nAI", typeof(ConnectCommand),
                "Add this Revit to Claude Desktop, Claude Code, Codex, Cursor, VS Code, Gemini CLI, Windsurf and Antigravity in one click.",
                "M14 18 L18 14", "M11 13 L8 16 A4.2 4.2 0 0 0 16 24 L19 21 M21 19 L24 16 A4.2 4.2 0 0 0 16 8 L13 11");

            var status = Button(dll, "t3mcp_status", "Status", typeof(StatusCommand), "Server status, port, calls and the log folder.",
                "M16 15 L16 22", "M16 4 A12 12 0 1 0 16.01 4 M16 10 L16 10.2");
            var reload = Button(dll, "t3mcp_reload", "Reload", typeof(ReloadCommand), "Load T3LabRevit.Core.dll again after an update, without restarting Revit.",
                "M25 6 L25 12 L19 12", "M25 12 A10 10 0 1 0 26 18");
            panel.AddStackedItems(status, reload);
        }

        static void Add(RibbonPanel panel, string dll, string name, string text, Type cmd, string tip, string accent, string outline)
        {
            var data = Button(dll, name, text, cmd, tip, accent, outline);
            panel.AddItem(data);
        }

        static PushButtonData Button(string dll, string name, string text, Type cmd, string tip, string accent, string outline)
        {
            return new PushButtonData(name, text, dll, cmd.FullName)
            {
                ToolTip = tip,
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
                LargeImage = Icon(32, outline, accent),
                Image = Icon(16, outline, accent),
            };
        }

        static bool Dark()
        {
#if REVIT2024_OR_GREATER
            try { return UIThemeManager.CurrentTheme == UITheme.Dark; }
            catch (Exception) { return false; }
#else
            return false;
#endif
        }

        static ImageSource Icon(int size, string outline, string accent)
        {
            try
            {
                var line = (Color)ColorConverter.ConvertFromString(Dark() ? "#EDEDED" : "#000000");
                var orange = (Color)ColorConverter.ConvertFromString(Orange);
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
                    dc.DrawGeometry(null, PenOf(line), Geometry.Parse(outline));
                    dc.DrawGeometry(null, PenOf(orange), Geometry.Parse(accent));
                    dc.Pop();
                }
                var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual);
                bmp.Freeze();
                return bmp;
            }
            catch (Exception) { return null; }
        }

        static Pen PenOf(Color c)
        {
            var pen = new Pen(new SolidColorBrush(c), 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            return pen;
        }
    }
}
