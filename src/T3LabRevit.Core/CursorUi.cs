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
            public Color Color = ToMedia(T3Style.Action("Read"));   // created, changed, deleted or read (T3Style)
        }

        /// <summary>A curve the cursor draws along (screen px, physical), start to end.</summary>
        internal sealed class Seg
        {
            public readonly List<Point> Points = new List<Point>();
        }

        internal sealed class Frame
        {
            public string Agent, Caption, Subject;     // Subject: what was made ("Wall 3450 × 200"), null = say the Caption
            public Color Color;
            public ActionKind Action;
            public bool Failed, Change;
            public readonly List<Rect> Views = new List<Rect>();
            public readonly List<Box> Boxes = new List<Box>();
            public readonly List<Seg> Path = new List<Seg>();
        }

        /// <summary>What a cursor's tag says between frames: the running call, the result, thinking dots, done.</summary>
        internal enum PointerMode { Working, Done, Finished }

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
                // an exception on this thread (a timer, a layout pass) would end Revit: log it and keep the thread
                _ui.UnhandledException += (s, e) =>
                {
                    Log.Error("agent cursors ui: " + e.Exception);
                    e.Handled = true;
                };
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

        /// <summary>Retag an agent's cursor that is already on screen (no cursor is created here: it has no place yet).</summary>
        public static void PointerState(string agent, PointerMode mode, string text) => Post(() => _overlay?.SetState(agent, mode, text));

        public static void ClearModel() => Post(() => _overlay?.Clear());

        /// <summary>Bar button Replay: frames shown one after the other.</summary>
        public static void Replay(List<Frame> frames) => Post(() =>
        {
            if (_overlay == null) _overlay = new Overlay();
            _overlay.Replay(frames);
        });

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

        /// <summary>The action's colour (T3Style, the same in T3Lab CAD MCP).</summary>
        public static Color ActionColor(ActionKind kind) => ToMedia(T3Style.Action(kind.ToString()));

        internal static Color ToMedia(uint argb) => AgentCursors.C(argb);

        /// <summary>Tag ink on the pastel wash (T3Style.TagInk).</summary>
        internal static readonly Color Ink = AgentCursors.C(T3Style.TagInk);

        /// <summary>The colour mixed into white (T3Style.Wash): amount 0 = white, 1 = the colour.</summary>
        internal static Color Pastel(Color c, double amount = 0.2) =>
            AgentCursors.C(T3Style.Wash(0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B, amount));

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

        /// <summary>
        /// The bar, laid out like T3Lab CAD MCP's palette (same order, words and T3Style colours; Revit's theme picks light or
        /// dark): per agent its pointer, name, status, counts, goal, plan with progress and the band of what runs now; RECENT;
        /// then Show on model / Follow and Pause / Select changes / Replay / Clear.
        /// </summary>
        sealed class Hud
        {
            const string Host = "Revit", Place = "model";
            readonly Window _win;
            readonly Border _frame;
            readonly StackPanel _agents = new StackPanel(), _feed = new StackPanel();
            readonly TextBlock _title, _recent, _toast, _paused;
            readonly Border _line1, _line2;
            readonly Button _close, _pause, _select, _replay, _clear;
            readonly CheckBox _onModel, _follow;
            readonly DispatcherTimer _tick;
            AgentCursors.View _view = new AgentCursors.View();
            T3Style.Palette _p = T3Style.Light;
            bool _updating, _placed, _hiddenForFocus;
            int _idleTicks;
            DateTime _toastUntil;
            public bool UserClosed;

            public Hud()
            {
                _win = new Window
                {
                    Title = T3Style.Title, Width = 320, SizeToContent = SizeToContent.Height,
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                };
                _win.SourceInitialized += (s, e) => Native.AddExStyle(_win, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                _win.Closed += (s, e) => _tick?.Stop();

                var root = new StackPanel();
                // header: drag to move; ✕ hides the bar
                var header = new DockPanel { Margin = new Thickness(12, 10, 8, 6), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
                header.MouseLeftButtonDown += (s, e) => { try { _win.DragMove(); } catch (Exception) { } };
                _close = FlatButton("✕", T3Style.CloseTip);
                _close.Click += (s, e) => { UserClosed = true; _win.Hide(); SetHudVisible(false); };
                DockPanel.SetDock(_close, Dock.Right);
                header.Children.Add(_close);
                var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                title.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(AgentCursors.C(T3Style.Light.Accent)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                _title = new TextBlock { Text = T3Style.Title, FontWeight = FontWeights.SemiBold };
                title.Children.Add(_title);
                header.Children.Add(title);
                root.Children.Add(header);

                _agents.Margin = new Thickness(12, 0, 12, 0);
                root.Children.Add(_agents);
                _paused = new TextBlock { Text = T3Style.PausedNote, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(12, 0, 12, 6), Visibility = Visibility.Collapsed };
                root.Children.Add(_paused);
                _line1 = new Border { Height = 1, Margin = new Thickness(12, 2, 12, 6) };
                root.Children.Add(_line1);
                _recent = new TextBlock { Text = T3Style.Recent, FontSize = 10, Margin = new Thickness(12, 0, 12, 2) };
                root.Children.Add(_recent);
                _feed.Margin = new Thickness(12, 0, 12, 6);
                root.Children.Add(_feed);
                _line2 = new Border { Height = 1, Margin = new Thickness(12, 2, 12, 6) };
                root.Children.Add(_line2);

                var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 6) };
                _onModel = new CheckBox { Content = T3Style.ShowOn(Place), Margin = new Thickness(0, 0, 12, 0), ToolTip = T3Style.ShowOnTip(Place) };
                _onModel.Click += (s, e) => { if (!_updating) AgentCursors.SetOption("show_on_model", _onModel.IsChecked == true); };
                _follow = new CheckBox { Content = T3Style.Follow, ToolTip = T3Style.FollowTip(Host) };
                _follow.Click += (s, e) => { if (!_updating) AgentCursors.SetOption("follow", _follow.IsChecked == true); };
                opts.Children.Add(_onModel);
                opts.Children.Add(_follow);
                root.Children.Add(opts);

                var buttons = new WrapPanel { Margin = new Thickness(8, 0, 12, 8) };
                _pause = FlatButton(T3Style.Pause, T3Style.PauseTip);
                _pause.Click += (s, e) => AgentCursors.SetPaused(!_view.Paused);
                _select = FlatButton(T3Style.Select, T3Style.SelectTip("element"));
                _select.Click += (s, e) => AgentCursors.SelectTouched(true);
                _replay = FlatButton(T3Style.Replay, T3Style.ReplayTip);
                _replay.Click += (s, e) => AgentCursors.Replay();
                _clear = FlatButton(T3Style.Clear, T3Style.ClearTip);
                _clear.Click += (s, e) => AgentCursors.ClearFeed();
                foreach (Button b in new[] { _pause, _select, _replay, _clear }) buttons.Children.Add(b);
                root.Children.Add(buttons);

                _toast = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 8), Visibility = Visibility.Collapsed };
                root.Children.Add(_toast);

                _frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = root };
                _win.Content = _frame;

                _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _tick.Tick += (s, e) =>
                {
                    try { OnTick(); }
                    catch (Exception ex) { Log.Error("agent cursors bar tick: " + ex.Message); }
                };
                _tick.Start();
            }

            SolidColorBrush P(uint argb, byte alpha = 255) => B(AgentCursors.C(argb), alpha);

            /// <summary>A flat button that follows the bar's palette (WPF's own chrome stays light in a dark bar).</summary>
            static Button FlatButton(string text, string tip)
            {
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
                var content = new FrameworkElementFactory(typeof(ContentPresenter));
                content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                border.AppendChild(content);
                return new Button
                {
                    Content = text, ToolTip = tip, Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(4, 0, 0, 4),
                    Focusable = false, Cursor = Cursors.Hand, Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
                };
            }

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
                // the seconds count, "in line" -> "waiting for Revit", thinking -> idle: a fresh snapshot (plain data, short lock)
                if (_view.Agents.Any(a => a.Working || a.State == AgentCursors.AgentState.Thinking))
                {
                    try { _view = AgentCursors.Snapshot(); }
                    catch (Exception ex) { Log.Error("agent cursors snapshot: " + ex.Message); }
                    Render();
                }
                else if (++_idleTicks % 10 == 0 && (_view.Feed.Count > 0 || _view.Agents.Any(a => a.LastEnd.HasValue))) Render();   // "3 min ago"
            }

            public void SetView(AgentCursors.View view)
            {
                _view = view;
                Render();
            }

            void Theme()
            {
                _p = T3Style.For(_view.Dark);
                _frame.Background = P(_p.Bg, 0xF7);
                _frame.BorderBrush = P(_p.Line);
                _win.Foreground = P(_p.Text);
                _title.Foreground = P(_p.Text);
                _recent.Foreground = P(_p.Muted);
                _toast.Foreground = P(_p.Muted);
                _paused.Foreground = P(_p.Warn);
                _line1.Background = _line2.Background = P(_p.Line);
                _onModel.Foreground = _follow.Foreground = P(_p.Text);
                foreach (Button b in new[] { _close, _pause, _select, _replay, _clear })
                {
                    b.Background = P(_p.Card);
                    b.BorderBrush = P(_p.Line);
                    b.Foreground = P(_p.Text);
                    b.Opacity = b.IsEnabled ? 1 : 0.45;
                }
                _close.Background = Brushes.Transparent;
                _close.BorderBrush = Brushes.Transparent;
                if (_view.Paused) { _pause.Background = P(_p.Warn, 0x33); _pause.BorderBrush = P(_p.Warn); }
            }

            void Render()
            {
                _updating = true;
                try
                {
                    _pause.Content = _view.Paused ? T3Style.Resume : T3Style.Pause;
                    _onModel.IsChecked = _view.ShowOnModel;
                    _follow.IsChecked = _view.Follow;
                    _follow.IsEnabled = _view.ShowOnModel;
                    _select.Content = T3Style.SelectLabel(_view.Touched);
                    _select.IsEnabled = _view.Touched > 0;
                    _paused.Visibility = _view.Paused ? Visibility.Visible : Visibility.Collapsed;
                    Theme();

                    _agents.Children.Clear();
                    if (_view.Agents.Count == 0)
                    {
                        _agents.Children.Add(new TextBlock { Text = T3Style.Empty, FontWeight = FontWeights.SemiBold, Foreground = P(_p.Text) });
                        _agents.Children.Add(new TextBlock { Text = T3Style.EmptyHint(Host), TextWrapping = TextWrapping.Wrap, Foreground = P(_p.Muted), Margin = new Thickness(0, 2, 0, 6) });
                    }
                    foreach (AgentCursors.AgentView a in _view.Agents.Take(T3Style.AgentRows)) _agents.Children.Add(AgentBlock(a));

                    _feed.Children.Clear();
                    if (_view.Feed.Count == 0) _feed.Children.Add(new TextBlock { Text = "Nothing yet.", Foreground = P(_p.Muted) });
                    foreach (AgentCursors.StepView s in _view.Feed.Take(T3Style.FeedRows)) _feed.Children.Add(StepRow(s));
                }
                finally { _updating = false; }
            }

            /// <summary>The agent's avatar on the bar: the cursor it moves over the model.</summary>
            static UIElement Pointer(Color c) => new Path
            {
                Data = Geometry.Parse("M0,0 L0,13 L3.6,9.6 L6.2,14.5 L8.2,13.6 L5.8,8.8 L10.5,8.8 Z"),
                Fill = B(c), Stroke = Brushes.White, StrokeThickness = 1, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            };

            UIElement AgentBlock(AgentCursors.AgentView a)
            {
                var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
                int secs = Math.Max(0, (int)(DateTime.Now - a.NowStarted).TotalSeconds);
                string statusText;
                uint statusColor;
                switch (a.State)
                {
                    case AgentCursors.AgentState.Working: statusText = $"Working {secs} s"; statusColor = T3Style.Action(a.NowAction.ToString()); break;
                    case AgentCursors.AgentState.Blocked: statusText = $"Waiting for {Host} {secs} s"; statusColor = _p.Warn; break;
                    case AgentCursors.AgentState.Queued:
                    case AgentCursors.AgentState.Paused: statusText = a.Status; statusColor = _p.Warn; break;
                    case AgentCursors.AgentState.Thinking: statusText = "Thinking" + T3Style.Dots(); statusColor = _p.Info; break;
                    default:
                        statusText = a.LastEnd.HasValue ? "Idle · last step " + T3Style.Ago(a.LastEnd.Value) : "Idle"; statusColor = _p.Muted; break;
                }
                var row = new DockPanel();
                var status = new TextBlock { Text = statusText, Foreground = P(statusColor), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(status, Dock.Right);
                row.Children.Add(status);
                var name = new StackPanel { Orientation = Orientation.Horizontal };
                name.Children.Add(Pointer(a.Color));
                name.Children.Add(new TextBlock { Text = a.Name, FontWeight = FontWeights.SemiBold, Foreground = P(_p.Text) });
                row.Children.Add(name);
                panel.Children.Add(row);
                panel.Children.Add(new TextBlock { Text = T3Style.Counts(a.Calls, a.Changes), Foreground = P(_p.Muted), FontSize = 11, Margin = new Thickness(16, 0, 0, 0) });
                if (!string.IsNullOrEmpty(a.Goal))
                    panel.Children.Add(new TextBlock { Text = a.Goal, TextWrapping = TextWrapping.Wrap, Foreground = P(_p.Text), Margin = new Thickness(16, 2, 0, 0) });
                if (a.Tasks.Count > 0)
                {
                    int done = a.PlanDone ? a.Tasks.Count : Math.Max(0, a.Step - 1);
                    var progress = new DockPanel { Margin = new Thickness(16, 4, 0, 2) };
                    var label = new TextBlock { Text = T3Style.StepOf(a.Step, a.Tasks.Count, a.PlanDone), Foreground = P(_p.Muted), FontSize = 11, Width = 64 };
                    progress.Children.Add(label);
                    var track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center };
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, done), GridUnitType.Star) });
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, a.Tasks.Count - done), GridUnitType.Star) });
                    var fill = new Border { Background = B(a.Color), CornerRadius = new CornerRadius(2) };
                    var rest = new Border { Background = P(_p.Track), CornerRadius = new CornerRadius(2) };
                    Grid.SetColumn(rest, 1);
                    track.Children.Add(fill);
                    track.Children.Add(rest);
                    progress.Children.Add(track);
                    panel.Children.Add(progress);
                    for (int i = 0; i < a.Tasks.Count; i++)
                    {
                        bool isDone = a.PlanDone || i + 1 < a.Step, now = !a.PlanDone && i + 1 == a.Step;
                        var task = new TextBlock { Margin = new Thickness(16, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                        task.Inlines.Add(new System.Windows.Documents.Run(T3Style.TaskMark(isDone, now) + "  ")
                        {
                            Foreground = isDone ? P(_p.Ok) : now ? B(a.Color) : P(_p.Muted),
                        });
                        task.Inlines.Add(new System.Windows.Documents.Run($"{i + 1}  {a.Tasks[i]}")
                        {
                            Foreground = now ? P(_p.Text) : P(_p.Muted), FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal,
                        });
                        panel.Children.Add(task);
                    }
                }
                if (a.Working && !string.IsNullOrEmpty(a.Now))
                {
                    // the band: the action's colour while it runs; amber while it waits its turn or for Revit
                    bool waits = a.State == AgentCursors.AgentState.Queued || a.State == AgentCursors.AgentState.Blocked;
                    uint c = waits ? _p.Warn : T3Style.Action(a.NowAction.ToString());
                    string head = a.State == AgentCursors.AgentState.Queued
                        ? "IN LINE · " + (a.Behind != null ? "after " + a.Behind : "waiting its turn") + (a.Ahead > 0 ? $", {a.Ahead} more ahead" : "")
                        : a.State == AgentCursors.AgentState.Blocked ? $"WAITING · {Host} is busy (a dialog, an edit mode or a long operation)"
                        : a.NowAction.ToString().ToUpperInvariant();
                    var band = new StackPanel();
                    band.Children.Add(new TextBlock { Text = head, Foreground = P(c), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });
                    band.Children.Add(new TextBlock { Text = a.Now, Foreground = P(_p.Text), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    panel.Children.Add(new Border
                    {
                        Background = P(c, 0x28), BorderBrush = P(c), BorderThickness = new Thickness(4, 0, 0, 0),
                        Padding = new Thickness(8, 3, 6, 4), Margin = new Thickness(0, 6, 0, 0), Child = band,
                    });
                }
                else if (a.State == AgentCursors.AgentState.Thinking)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = T3Style.Thinking + " " + T3Style.Dots(), Foreground = P(_p.Muted), FontSize = 11, FontStyle = FontStyles.Italic,
                        Margin = new Thickness(16, 4, 0, 0),
                    });
                }
                panel.Children.Add(new Border { Height = 1, Background = P(_p.Line), Margin = new Thickness(0, 8, 0, 0) });
                return panel;
            }

            UIElement StepRow(AgentCursors.StepView s)
            {
                string state = s.State.ToString();
                var block = new DockPanel { Margin = new Thickness(0, 1, 0, 3), ToolTip = s.Error ?? s.Caption };
                var stripe = new Border { Width = 3, Background = P(T3Style.Action(s.Action.ToString())), Margin = new Thickness(0, 1, 6, 1) };
                DockPanel.SetDock(stripe, Dock.Left);
                block.Children.Add(stripe);
                var lines = new StackPanel();
                var top = new DockPanel();
                var right = new TextBlock { Text = s.Agent + " · " + T3Style.Ago(s.When), Foreground = P(_p.Muted), FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
                DockPanel.SetDock(right, Dock.Right);
                top.Children.Add(right);
                var caption = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                caption.Inlines.Add(new System.Windows.Documents.Run(T3Style.Mark(state) + "  ") { Foreground = P(T3Style.MarkColor(state, _p)) });
                caption.Inlines.Add(new System.Windows.Documents.Run(s.Caption) { Foreground = P(_p.Text) });
                top.Children.Add(caption);
                lines.Children.Add(top);
                string detail = T3Style.Detail(state, s.Changed, s.Seconds, s.Error);
                if (detail.Length > 0)
                    lines.Children.Add(new TextBlock
                    {
                        Text = detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 0, 0, 0),
                        Foreground = P(s.State == StepState.Ok ? _p.Muted : T3Style.MarkColor(state, _p)),
                    });
                block.Children.Add(lines);
                return block;
            }
        }

        // ---------------------------------------------------------------- the cursors over Revit

        sealed class Overlay
        {
            const double MoveMs = 650, HopMs = 380, BoxHoldS = 3.5, ThinkAfterS = 2.5, IdleHideS = AgentCursors.ThinkS + 10, ReplayMs = 900;
            const double DrawSpeed = 0.9;   // DIP per ms while drawing along a new element
            readonly Window _win;
            readonly Canvas _canvas = new Canvas();
            readonly Dictionary<string, Pointer> _pointers = new Dictionary<string, Pointer>();
            readonly DispatcherTimer _tick;
            double _scale = 1, _originX, _originY;
            bool _hiddenForFocus;

            sealed class Pointer
            {
                public Canvas Root;
                public TextBlock Name, Caption;
                public Border Tag;
                public double X, Y;
                public int Slot;
                public DateTime Seen, ModeSince;
                public PointerMode Mode = PointerMode.Done;
                public string Text = "";
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
                _tick.Tick += (s, e) =>
                {
                    try { OnTick(); }
                    catch (Exception ex) { Log.Error("agent cursors overlay tick: " + ex.Message); }
                };
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
                Pointer p = PointerFor(f.Agent, f.Color, f.Path.Count > 0 || f.Boxes.Count > 0 || f.Views.Count > 0);
                if (p == null) return;
                SetText(p, PointerMode.Done, f.Subject ?? f.Caption);

                double hold = BoxHoldS;
                if (f.Path.Count > 0) hold += DrawAlong(p, f.Path, f.Color) / 1000.0;
                foreach (Box b in f.Boxes) AddBox(b, b.Color, f.Change || b.Deleted, hold);
                if (f.Path.Count > 0) return;

                // the cursor goes to the first thing it touched, else into the view; agents on one spot sit side by side
                double tx, ty;
                Box first = f.Boxes.FirstOrDefault(b => !b.Deleted) ?? f.Boxes.FirstOrDefault();
                if (first != null) { tx = X((first.L + first.R) / 2); ty = Y((first.T + first.B) / 2); }
                else if (f.Views.Count > 0)
                {
                    Rect v = f.Views[0];
                    tx = X(v.L + (v.R - v.L) * (0.35 + 0.1 * (p.Slot % 3)));
                    ty = Y(v.T + (v.B - v.T) * 0.4);
                }
                else return;
                Glide(p, tx + (p.Slot % 4) * 14, ty + (p.Slot % 4) * 10);
            }

            public void SetState(string agent, PointerMode mode, string text)
            {
                if (agent == null || !_pointers.TryGetValue(agent, out Pointer p)) return;
                SetText(p, mode, text);
            }

            void SetText(Pointer p, PointerMode mode, string text)
            {
                p.Mode = mode;
                p.Text = text ?? "";
                p.ModeSince = p.Seen = DateTime.Now;
                p.Caption.Text = p.Text;
                p.Root.BeginAnimation(UIElement.OpacityProperty, null);
                p.Root.Opacity = 1;
            }

            void AddBox(Box b, Color c, bool strong, double holdS)
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
                    Background = B(Pastel(c, 0.22)), BorderBrush = B(c), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(4, 0, 4, 1),
                    Child = new TextBlock { Text = b.Label, Foreground = B(Ink), FontSize = 11, FontFamily = new FontFamily("Segoe UI") },
                };
                Canvas.SetLeft(chip, l - 4);
                Canvas.SetTop(chip, Math.Max(0, t - 21));
                _canvas.Children.Add(chip);
                FadeOut(rect, holdS);
                FadeOut(chip, holdS);
            }

            void FadeOut(UIElement e, double afterS)
            {
                var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromSeconds(afterS) };
                anim.Completed += (s, a) => _canvas.Children.Remove(e);
                e.BeginAnimation(UIElement.OpacityProperty, anim);
            }

            /// <summary>The agent's cursor: a filled arrow + a pastel tag (agent colour at 20 % on white, 1 px border, dark ink).</summary>
            Pointer PointerFor(string agent, Color color, bool create)
            {
                if (_pointers.TryGetValue(agent, out Pointer p)) return p;
                if (!create) return null;
                p = new Pointer { Root = new Canvas { IsHitTestVisible = false }, Slot = _pointers.Count };
                var arrow = new Path
                {
                    Data = Geometry.Parse("M0,0 L0,18 L4.8,13.8 L8.2,21 L11,19.8 L7.6,12.8 L14,12.8 Z"),
                    Fill = B(color), Stroke = Brushes.White, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
                };
                p.Name = new TextBlock { Text = agent, Foreground = B(Ink), FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") };
                p.Caption = new TextBlock { Foreground = B(Ink), FontSize = 11, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(p.Name);
                label.Children.Add(p.Caption);
                p.Tag = new Border
                {
                    Background = B(Pastel(color)), BorderBrush = B(color), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(7, 2, 8, 3), Child = label, MaxWidth = 340,
                };
                Canvas.SetLeft(p.Tag, 16);
                Canvas.SetTop(p.Tag, 18);
                p.Root.Children.Add(arrow);
                p.Root.Children.Add(p.Tag);
                // a new cursor fades in where its first move starts (the view's top left area, by slot)
                p.X = 60 + p.Slot * 18;
                p.Y = 80 + p.Slot * 14;
                Canvas.SetLeft(p.Root, p.X);
                Canvas.SetTop(p.Root, p.Y);
                _canvas.Children.Add(p.Root);
                _pointers[agent] = p;
                return p;
            }

            void Glide(Pointer p, double x, double y)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var dur = TimeSpan.FromMilliseconds(MoveMs);
                p.Root.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(p.X, x, dur) { EasingFunction = ease });
                p.Root.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(p.Y, y, dur) { EasingFunction = ease });
                p.X = x;
                p.Y = y;
            }

            /// <summary>Hop to each new curve's start, then draw along it while a line grows behind the tip. Returns the duration (ms).</summary>
            double DrawAlong(Pointer p, List<Seg> path, Color color)
            {
                var kx = new DoubleAnimationUsingKeyFrames();
                var ky = new DoubleAnimationUsingKeyFrames();
                kx.KeyFrames.Add(new DiscreteDoubleKeyFrame(p.X, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                ky.KeyFrames.Add(new DiscreteDoubleKeyFrame(p.Y, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                double t = 0, x = p.X, y = p.Y;
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                bool firstHop = true;
                foreach (Seg seg in path)
                {
                    Point a = new Point(X(seg.Points[0].X), Y(seg.Points[0].Y));
                    t += firstHop ? MoveMs : HopMs;
                    firstHop = false;
                    kx.KeyFrames.Add(new EasingDoubleKeyFrame(a.X, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t)), ease));
                    ky.KeyFrames.Add(new EasingDoubleKeyFrame(a.Y, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t)), ease));
                    x = a.X;
                    y = a.Y;
                    foreach (Point raw in seg.Points.Skip(1))
                    {
                        var b = new Point(X(raw.X), Y(raw.Y));
                        double ms = Math.Max(60, Math.Min(900, (b - new Point(x, y)).Length / DrawSpeed));
                        Trail(x, y, b.X, b.Y, t, ms, color);
                        t += ms;
                        kx.KeyFrames.Add(new LinearDoubleKeyFrame(b.X, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
                        ky.KeyFrames.Add(new LinearDoubleKeyFrame(b.Y, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
                        x = b.X;
                        y = b.Y;
                    }
                }
                p.Root.BeginAnimation(Canvas.LeftProperty, kx);
                p.Root.BeginAnimation(Canvas.TopProperty, ky);
                p.X = x;
                p.Y = y;
                return t;
            }

            void Trail(double x1, double y1, double x2, double y2, double beginMs, double ms, Color color)
            {
                var line = new Line
                {
                    X1 = x1, Y1 = y1, X2 = x1, Y2 = y1, Stroke = B(color), StrokeThickness = 3, Opacity = 0.85,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                _canvas.Children.Insert(0, line);   // under the cursors
                var begin = TimeSpan.FromMilliseconds(beginMs);
                var dur = TimeSpan.FromMilliseconds(ms);
                line.BeginAnimation(Line.X2Property, new DoubleAnimation(x1, x2, dur) { BeginTime = begin, FillBehavior = FillBehavior.HoldEnd });
                line.BeginAnimation(Line.Y2Property, new DoubleAnimation(y1, y2, dur) { BeginTime = begin, FillBehavior = FillBehavior.HoldEnd });
                var fade = new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(600)) { BeginTime = begin + dur + TimeSpan.FromSeconds(BoxHoldS) };
                fade.Completed += (s, a) => _canvas.Children.Remove(line);
                line.BeginAnimation(UIElement.OpacityProperty, fade);
            }

            void OnTick()
            {
                bool front = Native.RevitInFront();
                if (!front && _win.IsVisible) { _win.Hide(); _hiddenForFocus = true; }
                else if (front && _hiddenForFocus) { _hiddenForFocus = false; Place(); _win.Show(); }
                DateTime now = DateTime.Now;
                foreach (Pointer p in _pointers.Values)
                {
                    double since = (now - p.ModeSince).TotalSeconds;
                    // between calls the tag says the agent is thinking (waiting for its model), like the bar
                    if (p.Mode == PointerMode.Done && since > ThinkAfterS)
                        p.Caption.Text = since < AgentCursors.ThinkS ? "thinking " + new string('·', 1 + (int)(since * 2) % 3) : "";
                    if (p.Mode != PointerMode.Working && (now - p.Seen).TotalSeconds > IdleHideS && p.Root.Opacity > 0.99)
                        p.Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(800)) { FillBehavior = FillBehavior.HoldEnd });
                }
            }

            readonly Queue<Frame> _replay = new Queue<Frame>();
            DispatcherTimer _replayTick;

            /// <summary>Up to the last 20 frames, one every ReplayMs (as AutoCAD's Replay).</summary>
            public void Replay(List<Frame> frames)
            {
                Clear();
                foreach (Frame f in frames.Skip(Math.Max(0, frames.Count - 20))) _replay.Enqueue(f);
                if (_replayTick == null)
                {
                    _replayTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReplayMs) };
                    _replayTick.Tick += (s, e) =>
                    {
                        try
                        {
                            if (_replay.Count == 0) _replayTick.Stop();
                            else Show(_replay.Dequeue());
                        }
                        catch (Exception ex) { Log.Error("agent cursors replay: " + ex.Message); _replayTick.Stop(); }
                    };
                }
                if (_replay.Count > 0) Show(_replay.Dequeue());
                _replayTick.Start();
            }

            public void Clear()
            {
                _replay.Clear();
                _replayTick?.Stop();
                _canvas.Children.Clear();
                _pointers.Clear();
            }

            public void Close()
            {
                try { _tick.Stop(); _replayTick?.Stop(); _win.Close(); } catch (Exception) { }
            }
        }
    }
}
