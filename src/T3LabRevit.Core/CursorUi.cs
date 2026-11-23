using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace T3LabRevit.Core
{
    /// <summary>
    /// The Agent Cursors windows, code-built WPF on their own STA thread with their own Dispatcher, so the bar keeps
    /// ticking ("Working 8 s") while Revit's main thread runs a long call. Two windows: the bar (Hud) and a click-through
    /// canvas over Revit's main window (Overlay) for the cursors and boxes. Both are Topmost without an owner (an owner on
    /// another thread couples the input queues), so they hide whenever Revit is not the foreground app or is minimized.
    /// Everything here is fed plain data; no Revit API call ever happens on this thread.
    /// </summary>
    internal static class CursorUi
    {
        internal sealed class Rect { public double L, T, R, B; }

        internal sealed class Box
        {
            public double L, T, R, B;
            public string Label;
            public bool Deleted;
        }

        internal sealed class Frame
        {
            public string Agent, Caption;
            public Color Color;
            public ActionKind Action;
            public bool Failed, Change;
            public readonly List<Rect> Views = new List<Rect>();
            public readonly List<Box> Boxes = new List<Box>();
        }

        static Dispatcher _ui;
        static Hud _hud;
        static Overlay _overlay;
        static volatile bool _hudVisible;

        public static bool HudVisible => _hudVisible;

        static void Post(Action action)
        {
            try
            {
                EnsureThread();
                _ui?.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                {
                    try { action(); }
                    catch (Exception ex) { Log.Error("agent cursors ui: " + ex); }
                }));
            }
            catch (Exception ex) { Log.Error("agent cursors ui thread: " + ex.Message); }
        }

        static void EnsureThread()
        {
            if (_ui != null) return;
            var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                _ui = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true, Name = "T3Lab Agent Cursors" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(5000);
        }

        public static void ShowBar(AgentCursors.View view, bool reveal) => Post(() =>
        {
            if (_hud == null) _hud = new Hud();
            _hud.SetView(view);
            if (reveal) _hud.UserClosed = false;
            if (!_hud.UserClosed) _hud.Reveal();
            _hudVisible = !_hud.UserClosed;
        });

        public static void ShowFrame(Frame frame) => Post(() =>
        {
            if (_overlay == null) _overlay = new Overlay();
            _overlay.Show(frame);
        });

        public static void ClearModel() => Post(() => _overlay?.Clear());

        public static void Toast(string text) => Post(() => _hud?.Toast(text));

        public static void HideAll() => Post(() =>
        {
            _hud?.Close();
            _hud = null;
            _overlay?.Close();
            _overlay = null;
            _hudVisible = false;
        });

        public static void Shutdown()
        {
            Dispatcher ui = _ui;
            if (ui == null) return;
            try
            {
                ui.Invoke((Action)(() =>
                {
                    try { _hud?.Close(); _overlay?.Close(); } catch (Exception) { }
                    _hud = null;
                    _overlay = null;
                }), TimeSpan.FromSeconds(2));
                ui.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
            catch (Exception) { /* Revit is closing */ }
            _ui = null;
            _hudVisible = false;
        }

        internal static void SetHudVisible(bool v) => _hudVisible = v;

        // ---------------------------------------------------------------- shared

        public static Color ActionColor(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.Create: return AgentCursors.Rgb(0x14, 0xAE, 0x5C);
                case ActionKind.Modify: return AgentCursors.Rgb(0xFF, 0x8A, 0x00);
                case ActionKind.Delete: return AgentCursors.Rgb(0xE5, 0x48, 0x4D);
                case ActionKind.File: return AgentCursors.Rgb(0x8E, 0x6C, 0xEF);
                case ActionKind.View: return AgentCursors.Rgb(0x00, 0xA3, 0xBF);
                default: return AgentCursors.Rgb(0x0D, 0x99, 0xFF);
            }
        }

        internal static SolidColorBrush B(Color c, byte alpha = 255)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        internal static SolidColorBrush Hex(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        // ---------------------------------------------------------------- Win32

        internal static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left, Top, Right, Bottom; }

            [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
            [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
            [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
            [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);

            public const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

            static readonly uint Pid = (uint)Process.GetCurrentProcess().Id;

            public static double Scale(IntPtr hwnd)
            {
                try
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    return dpi >= 48 ? dpi / 96.0 : 1.0;
                }
                catch (Exception) { return 1.0; }   // before Windows 10 1607
            }

            /// <summary>Revit (or one of our windows) is the foreground app and Revit is not minimized.</summary>
            public static bool RevitInFront()
            {
                IntPtr main = Entry.MainWindow;
                if (main != IntPtr.Zero && IsIconic(main)) return false;
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return true;
                GetWindowThreadProcessId(fg, out uint pid);
                return pid == Pid;
            }

            public static void AddExStyle(Window w, int flags)
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero) SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | flags);
            }
        }

        // ---------------------------------------------------------------- the bar

        sealed class Hud
        {
            readonly Window _win;
            readonly StackPanel _agents = new StackPanel(), _feed = new StackPanel();
            readonly Button _pause, _select, _clear;
            readonly CheckBox _onModel, _follow;
            readonly TextBlock _toast;
            readonly DispatcherTimer _tick;
            AgentCursors.View _view = new AgentCursors.View();
            bool _updating, _placed, _hiddenForFocus;
            DateTime _toastUntil;
            public bool UserClosed;

            static readonly Brush Fore = Hex(0xFF1F1F1F), Muted = Hex(0xFF6B6B6B), Line = Hex(0x22000000), Back = Hex(0xF7FFFFFF), Orange = Hex(0xFFEA680C);

            public Hud()
            {
                _win = new Window
                {
                    Title = "T3Lab Agent Cursors", Width = 312, SizeToContent = SizeToContent.Height,
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Foreground = Fore,
                };
                _win.SourceInitialized += (s, e) => Native.AddExStyle(_win, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                _win.Closed += (s, e) => _tick?.Stop();

                var root = new StackPanel();
                // header
                var header = new DockPanel { Margin = new Thickness(12, 10, 8, 6), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
                header.MouseLeftButtonDown += (s, e) => { try { _win.DragMove(); } catch (Exception) { } };
                var close = SmallButton("✕", "Hide the bar (Agent Cursors stays on; the ribbon button shows it again)");
                close.Click += (s, e) => { UserClosed = true; _win.Hide(); SetHudVisible(false); };
                DockPanel.SetDock(close, Dock.Right);
                _pause = SmallButton("Pause", "Pause AI changes: tools that change the model are refused until you press Resume");
                _pause.Click += (s, e) => AgentCursors.SetPaused(!_view.Paused);
                DockPanel.SetDock(_pause, Dock.Right);
                header.Children.Add(close);
                header.Children.Add(_pause);
                var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                title.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Orange, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                title.Children.Add(new TextBlock { Text = "T3Lab Agent Cursors", FontWeight = FontWeights.SemiBold });
                header.Children.Add(title);
                root.Children.Add(header);

                _agents.Margin = new Thickness(12, 0, 12, 4);
                root.Children.Add(_agents);
                root.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(12, 4, 12, 6) });
                root.Children.Add(new TextBlock { Text = "RECENT", FontSize = 10, Foreground = Muted, Margin = new Thickness(12, 0, 12, 2) });
                _feed.Margin = new Thickness(12, 0, 12, 6);
                root.Children.Add(_feed);
                root.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(12, 2, 12, 6) });

                var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 6) };
                _onModel = new CheckBox { Content = "Show on the model", Margin = new Thickness(0, 0, 12, 0), ToolTip = "Move a cursor per AI over the Revit views and frame what it touches" };
                _onModel.Click += (s, e) => { if (!_updating) AgentCursors.SetOption("show_on_model", _onModel.IsChecked == true); };
                _follow = new CheckBox { Content = "Follow", ToolTip = "Zoom Revit to what the AI changes" };
                _follow.Click += (s, e) => { if (!_updating) AgentCursors.SetOption("follow", _follow.IsChecked == true); };
                opts.Children.Add(_onModel);
                opts.Children.Add(_follow);
                root.Children.Add(opts);

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
                _select = SmallButton("Select changes", "Select in Revit every element the AI created or changed in this model, and zoom to them");
                _select.Click += (s, e) => AgentCursors.SelectTouched(true);
                _clear = SmallButton("Clear", "Forget the list, the goals and the changed elements");
                _clear.Click += (s, e) => AgentCursors.ClearFeed();
                buttons.Children.Add(_select);
                buttons.Children.Add(_clear);
                root.Children.Add(buttons);

                _toast = new TextBlock { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 8), Visibility = Visibility.Collapsed };
                root.Children.Add(_toast);

                _win.Content = new Border
                {
                    Background = Back, BorderBrush = Hex(0x33000000), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    Child = root,
                };

                _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _tick.Tick += (s, e) => OnTick();
                _tick.Start();
            }

            static Button SmallButton(string text, string tip) => new Button
            {
                Content = text, ToolTip = tip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0),
                Background = Hex(0xFFF0F0F0), BorderBrush = Hex(0x33000000), Focusable = false,
            };

            public void Reveal()
            {
                if (!_placed)
                {
                    _placed = true;
                    if (Native.GetWindowRect(Entry.MainWindow, out Native.RECT r) && r.Right > r.Left)
                    {
                        double s = Native.Scale(Entry.MainWindow);
                        _win.Left = r.Right / s - _win.Width - 24;
                        _win.Top = r.Top / s + 150;
                    }
                    else { _win.Left = SystemParameters.WorkArea.Right - _win.Width - 24; _win.Top = 150; }
                }
                if (!_win.IsVisible && Native.RevitInFront()) _win.Show();
            }

            public void Close()
            {
                try { _tick.Stop(); _win.Close(); } catch (Exception) { }
            }

            public void Toast(string text)
            {
                _toast.Text = text;
                _toast.Visibility = Visibility.Visible;
                _toastUntil = DateTime.Now.AddSeconds(6);
            }

            void OnTick()
            {
                bool front = Native.RevitInFront();
                if (!UserClosed)
                {
                    if (!front && _win.IsVisible) { _win.Hide(); _hiddenForFocus = true; }
                    else if (front && _hiddenForFocus) { _hiddenForFocus = false; _win.Show(); }
                }
                if (_toast.Visibility == Visibility.Visible && DateTime.Now > _toastUntil) _toast.Visibility = Visibility.Collapsed;
                if (_view.Agents.Any(a => a.Working)) Render();   // the seconds count
            }

            public void SetView(AgentCursors.View view)
            {
                _view = view;
                Render();
            }

            void Render()
            {
                _updating = true;
                try
                {
                    _pause.Content = _view.Paused ? "Resume" : "Pause";
                    _pause.Background = _view.Paused ? Hex(0xFFFFE2C4) : Hex(0xFFF0F0F0);
                    _onModel.IsChecked = _view.ShowOnModel;
                    _follow.IsChecked = _view.Follow;
                    _select.Content = _view.Touched > 0 ? $"Select changes ({_view.Touched})" : "Select changes";
                    _select.IsEnabled = _view.Touched > 0;

                    _agents.Children.Clear();
                    if (_view.Agents.Count == 0)
                        _agents.Children.Add(new TextBlock { Text = "No AI app has worked in Revit yet. Ask Claude (or another MCP app) to do something in the model.", TextWrapping = TextWrapping.Wrap, Foreground = Muted });
                    foreach (AgentCursors.AgentView a in _view.Agents.Take(4)) _agents.Children.Add(AgentBlock(a));

                    _feed.Children.Clear();
                    if (_view.Feed.Count == 0) _feed.Children.Add(new TextBlock { Text = "Nothing yet.", Foreground = Muted });
                    foreach (AgentCursors.StepView s in _view.Feed.Take(6)) _feed.Children.Add(StepRow(s));
                }
                finally { _updating = false; }
            }

            UIElement AgentBlock(AgentCursors.AgentView a)
            {
                var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
                var row = new DockPanel();
                var status = new TextBlock
                {
                    Text = a.Working ? $"Working {Math.Max(0, (int)(DateTime.Now - a.NowStarted).TotalSeconds)} s" : a.Status,
                    Foreground = a.Status == "Paused" ? Orange : Muted, FontSize = 11,
                };
                DockPanel.SetDock(status, Dock.Right);
                row.Children.Add(status);
                var name = new StackPanel { Orientation = Orientation.Horizontal };
                name.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = B(a.Color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                name.Children.Add(new TextBlock { Text = a.Name, FontWeight = FontWeights.SemiBold });
                name.Children.Add(new TextBlock { Text = $"  {a.Calls} calls · {a.Changes} changes", Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(name);
                panel.Children.Add(row);
                if (!string.IsNullOrEmpty(a.Goal))
                    panel.Children.Add(new TextBlock { Text = a.Goal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 2, 0, 0) });
                for (int i = 0; i < a.Tasks.Count; i++)
                {
                    bool done = a.PlanDone || i + 1 < a.Step, now = !a.PlanDone && i + 1 == a.Step;
                    panel.Children.Add(new TextBlock
                    {
                        Text = (done ? "✓  " : now ? "▸  " : "○  ") + a.Tasks[i],
                        Foreground = done ? Muted : Fore, FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal,
                        Margin = new Thickness(16, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                }
                if (a.Working && !string.IsNullOrEmpty(a.Now))
                {
                    var now = new Border
                    {
                        Background = B(ActionColor(a.NowAction), 0x26), BorderBrush = B(ActionColor(a.NowAction)), BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(16, 4, 0, 0),
                        Child = new TextBlock { Text = a.Now, TextTrimming = TextTrimming.CharacterEllipsis },
                    };
                    panel.Children.Add(now);
                }
                return panel;
            }

            UIElement StepRow(AgentCursors.StepView s)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), ToolTip = s.Error ?? s.Caption };
                string state = s.State == StepState.Ok ? "ok" : s.State == StepState.Refused ? "paused" : s.State == StepState.Failed ? "failed" : "…";
                var right = new TextBlock
                {
                    Text = (s.Changed > 0 ? s.Changed + " · " : "") + state,
                    Foreground = s.State == StepState.Failed ? Hex(0xFFE5484D) : s.State == StepState.Refused ? Orange : Muted, FontSize = 11,
                };
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                row.Children.Add(new Border { Width = 3, Background = B(ActionColor(s.Action)), Margin = new Thickness(0, 1, 6, 1) });
                var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                text.Inlines.Add(new System.Windows.Documents.Run(s.Agent + "  ") { Foreground = B(s.Color), FontWeight = FontWeights.SemiBold });
                text.Inlines.Add(new System.Windows.Documents.Run(s.Caption));
                row.Children.Add(text);
                return row;
            }
        }

        // ---------------------------------------------------------------- the cursors over Revit

        sealed class Overlay
        {
            const double MoveMs = 650, BoxHoldS = 3.5, IdleHideS = 12;
            readonly Window _win;
            readonly Canvas _canvas = new Canvas();
            readonly Dictionary<string, Pointer> _pointers = new Dictionary<string, Pointer>();
            readonly DispatcherTimer _tick;
            double _scale = 1, _originX, _originY;
            bool _hiddenForFocus;

            sealed class Pointer
            {
                public Canvas Root;
                public TextBlock Caption;
                public Border Tag;
                public double X, Y;
                public DateTime Seen;
            }

            public Overlay()
            {
                _win = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                    IsHitTestVisible = false, Content = _canvas, Title = "T3Lab Agent Cursors overlay",
                };
                _win.SourceInitialized += (s, e) => Native.AddExStyle(_win, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _tick.Tick += (s, e) => OnTick();
                _tick.Start();
            }

            void Place()
            {
                if (!Native.GetWindowRect(Entry.MainWindow, out Native.RECT r) || r.Right <= r.Left) return;
                _scale = Native.Scale(Entry.MainWindow);
                _originX = r.Left;
                _originY = r.Top;
                _win.Left = r.Left / _scale;
                _win.Top = r.Top / _scale;
                _win.Width = (r.Right - r.Left) / _scale;
                _win.Height = (r.Bottom - r.Top) / _scale;
            }

            double X(double px) => (px - _originX) / _scale;
            double Y(double px) => (px - _originY) / _scale;

            public void Show(Frame f)
            {
                Place();
                if (!_win.IsVisible && Native.RevitInFront()) _win.Show();
                Color ac = f.Failed ? AgentCursors.Rgb(0xE5, 0x48, 0x4D) : ActionColor(f.Action);
                foreach (Box b in f.Boxes) AddBox(b, ac, f.Change || b.Deleted);

                // the cursor goes to the first thing it touched, else into the view next to where it was
                double tx, ty;
                Box first = f.Boxes.FirstOrDefault(b => !b.Deleted) ?? f.Boxes.FirstOrDefault();
                if (first != null) { tx = X((first.L + first.R) / 2); ty = Y((first.T + first.B) / 2); }
                else if (f.Views.Count > 0)
                {
                    Rect v = f.Views[0];
                    int n = _pointers.Count;
                    tx = X(v.L + (v.R - v.L) * (0.35 + 0.1 * (n % 3)));
                    ty = Y(v.T + (v.B - v.T) * 0.4);
                }
                else return;
                MovePointer(f.Agent, f.Color, f.Caption, tx, ty);
            }

            void AddBox(Box b, Color c, bool strong)
            {
                double l = X(b.L), t = Y(b.T), w = Math.Max(6, X(b.R) - l), h = Math.Max(6, Y(b.B) - t);
                var rect = new Rectangle
                {
                    Width = w + 8, Height = h + 8, Stroke = B(c), StrokeThickness = strong ? 2 : 1.25,
                    Fill = B(c, (byte)(strong ? 0x22 : 0x10)), RadiusX = 3, RadiusY = 3,
                };
                if (b.Deleted) rect.StrokeDashArray = new DoubleCollection { 4, 3 };
                Canvas.SetLeft(rect, l - 4);
                Canvas.SetTop(rect, t - 4);
                _canvas.Children.Add(rect);
                var chip = new Border
                {
                    Background = B(c), CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 0, 4, 1),
                    Child = new TextBlock { Text = b.Label, Foreground = Brushes.White, FontSize = 10, FontFamily = new FontFamily("Segoe UI") },
                };
                Canvas.SetLeft(chip, l - 4);
                Canvas.SetTop(chip, Math.Max(0, t - 20));
                _canvas.Children.Add(chip);
                FadeOut(rect, BoxHoldS);
                FadeOut(chip, BoxHoldS);
            }

            void FadeOut(UIElement e, double afterS)
            {
                var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromSeconds(afterS) };
                anim.Completed += (s, a) => _canvas.Children.Remove(e);
                e.BeginAnimation(UIElement.OpacityProperty, anim);
            }

            void MovePointer(string agent, Color color, string caption, double x, double y)
            {
                if (!_pointers.TryGetValue(agent, out Pointer p))
                {
                    p = new Pointer { Root = new Canvas { IsHitTestVisible = false }, X = x - 60, Y = y - 60 };
                    var arrow = new Path
                    {
                        Data = Geometry.Parse("M0,0 L0,18 L4.8,13.8 L8.2,21 L11,19.8 L7.6,12.8 L14,12.8 Z"),
                        Fill = B(color), Stroke = Brushes.White, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
                    };
                    p.Caption = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontFamily = new FontFamily("Segoe UI") };
                    var label = new StackPanel { Orientation = Orientation.Horizontal };
                    label.Children.Add(new TextBlock { Text = agent + "  ", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") });
                    label.Children.Add(p.Caption);
                    p.Tag = new Border { Background = B(color), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 2, 8, 3), Child = label, MaxWidth = 340 };
                    Canvas.SetLeft(p.Tag, 16);
                    Canvas.SetTop(p.Tag, 18);
                    p.Root.Children.Add(arrow);
                    p.Root.Children.Add(p.Tag);
                    Canvas.SetLeft(p.Root, p.X);
                    Canvas.SetTop(p.Root, p.Y);
                    _canvas.Children.Add(p.Root);
                    _pointers[agent] = p;
                }
                p.Caption.Text = caption ?? "";
                p.Seen = DateTime.Now;
                p.Root.BeginAnimation(UIElement.OpacityProperty, null);
                p.Root.Opacity = 1;
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var dur = TimeSpan.FromMilliseconds(MoveMs);
                p.Root.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(p.X, x, dur) { EasingFunction = ease });
                p.Root.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(p.Y, y, dur) { EasingFunction = ease });
                p.X = x;
                p.Y = y;
            }

            void OnTick()
            {
                bool front = Native.RevitInFront();
                if (!front && _win.IsVisible) { _win.Hide(); _hiddenForFocus = true; }
                else if (front && _hiddenForFocus) { _hiddenForFocus = false; Place(); _win.Show(); }
                foreach (var kv in _pointers.ToList())
                {
                    Pointer p = kv.Value;
                    if ((DateTime.Now - p.Seen).TotalSeconds > IdleHideS && p.Root.Opacity > 0.99)
                        p.Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(800)));
                }
            }

            public void Clear()
            {
                _canvas.Children.Clear();
                _pointers.Clear();
            }

            public void Close()
            {
                try { _tick.Stop(); _win.Close(); } catch (Exception) { }
            }
        }
    }
}
