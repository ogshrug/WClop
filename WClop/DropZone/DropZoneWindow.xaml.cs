using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WClop.Core.DropZone;
using WClop.Core.Logging;
using WClop.Core.Settings;
using WClop.Results;


namespace WClop.DropZone
{
    /// <summary>
    /// The drop zone (project.md §18.2), as a tab on a screen edge the user chooses. Hidden until a drag-and-drop
    /// is under way, then it peeks out of the edge as a translucent half box; dragging over it opens it into a
    /// labelled target. Windows has no global drag pasteboard (§26.1), so drags are noticed with a low-level mouse
    /// hook and told apart from text selection or window moves by the drag-and-drop cursor; the content itself is
    /// only inspected once it's over the tab. The tab can be dragged to any edge in positioning mode.
    /// Tapping a modifier (Alt by default) during a drag shows the zone under the cursor instead; tapping again hides it.
    /// </summary>
    public partial class DropZoneWindow : Window
    {
        private enum ZoneState
        {
            Hidden,
            Peeking,
            Open,
            Positioning,
        }

        // Sizes in DIPs: length along the edge, depth out from it.
        private const double VerticalLength = 170, HorizontalLength = 250;
        private const double PeekDepth = 26;
        private const double VerticalOpenDepth = 220, HorizontalOpenDepth = 96;
        private const double CornerRadius = 14;
        // The zone shown under the cursor by a modifier tap is laid out like an open tab on a vertical edge.
        private const double CursorZoneWidth = VerticalOpenDepth, CursorZoneHeight = VerticalLength;

        private const int DragThreshold = 12;    // px before a press counts as a drag
        private const int ReachAroundTab = 150;  // px: dragging this close to the tab's spot always reveals it
        private static readonly Duration Animation = new(TimeSpan.FromMilliseconds(160));
        private static readonly Brush IdleBrush = Frozen("#B3202124");
        // Same translucent dark as idle, a little more opaque, so the target reads as "active" without a colour.
        private static readonly Brush AcceptBrush = Frozen("#D9202124");
        private static readonly Brush RejectBrush = Frozen("#D9A4262C");

        private readonly AppSettings _settings;
        private readonly DropHandler _handler;
        private readonly DispatcherTimer _hideTimer;
        private ZoneState _state = ZoneState.Hidden;
        private (int X, int Y)? _pressedAt;
        private bool _dragging;
        private int _dragCursorSamples;
        private bool _movingTab;
        private readonly MouseHook _hook;
        private int _presetIndex;
        private IReadOnlyList<DropPreset> _presets = DropPresets.All;
        private int _wheelRemainder;
        private bool _usable;
        private DragDropKeyStates _keys;
        private readonly KeyboardHook _keyboard;
        // Shown under the cursor by a modifier tap rather than on the edge.
        private bool _atCursor;
        // Tapped away for the rest of this drag, so the edge tab doesn't pop straight back.
        private bool _tapHidden;

        internal DropZoneWindow(AppSettings settings, DropHandler handler, MouseHook hook, KeyboardHook keyboard)
        {
            _settings = settings;
            _handler = handler;
            _hook = hook;
            _keyboard = keyboard;
            InitializeComponent();

            _hideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) => Retract(), Dispatcher);
            _hideTimer.Stop();

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                WindowInterop.MakeNonActivating(hwnd);
                WindowInterop.SetExcludedFromCapture(hwnd, !_settings.Ui.AllowInScreenshots);
            };
            new WindowInteropHelper(this).EnsureHandle();

            DragEnter += OnDragEnter;
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += (_, _) =>
            {
                if (_state == ZoneState.Open)
                    Peek();
            };
            Drop += OnDrop;

            // The hook reports on its own thread; everything here runs on the UI thread.
            hook.Wheel += delta => Dispatcher.BeginInvoke(() => OnWheel(delta));
            hook.LeftDown += (x, y) => Dispatcher.BeginInvoke(() =>
            {
                _pressedAt = (x, y);
                _dragging = false;
                _dragCursorSamples = 0;
                _tapHidden = false;
            });
            hook.DragMoved += (x, y) => Dispatcher.BeginInvoke(() => OnDragMoved(x, y));
            keyboard.Tapped += () => Dispatcher.BeginInvoke(OnModifierTapped);
            hook.LeftUp += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _keyboard.StopWatching();
                _pressedAt = null;
                _dragging = false;
                if (_state is ZoneState.Peeking or ZoneState.Open)
                    _hideTimer.Start(); // after any drop has been handled
            });
        }

        /// <summary>Raised when the user has moved the tab to a new place, so settings get saved.</summary>
        internal event Action? PositionChanged;

        /// <summary>
        /// Shows the tab open where it's configured, on the screen with the mouse, so the user can drag it to any edge.
        /// </summary>
        internal void StartPositioning()
        {
            _state = ZoneState.Positioning;
            _hideTimer.Stop();
            _atCursor = false;
            Layout(CurrentScreen());
            SetLook(IdleBrush, "Drag me to any edge", "Click to finish");
            PresetDots.Text = "";
            PresetHint.Text = "";
            Show();
            AnimateDepth(OpenDepth, showOpenContent: true);
            Tab.Cursor = Cursors.SizeAll;
        }

        /// <summary>Re-read the settings after they change.</summary>
        internal void ApplySettings()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                WindowInterop.SetExcludedFromCapture(hwnd, !_settings.Ui.AllowInScreenshots);
            if (_state != ZoneState.Hidden && !_atCursor)
                Layout(CurrentScreen());
        }

        private bool IsVerticalEdge => _atCursor || DropZoneGeometry.IsVertical(_settings.Ui.DropZoneEdge);
        private double Length => IsVerticalEdge ? VerticalLength : HorizontalLength;
        private double OpenDepth => IsVerticalEdge ? VerticalOpenDepth : HorizontalOpenDepth;

        // Drag detection

        private void OnDragMoved(int x, int y)
        {
            if (_state == ZoneState.Positioning || _pressedAt is not { } start)
                return;

            if (!_dragging)
            {
                if (Math.Abs(x - start.X) < DragThreshold && Math.Abs(y - start.Y) < DragThreshold)
                    return;
                _dragging = true;
                // Taps work even with the edge tab turned off: then the zone only appears when asked for.
                _keyboard.Watch(_settings.Ui.DropZoneTapKey);
            }

            if (!_settings.Ui.DropZoneEnabled || _state != ZoneState.Hidden || _tapHidden)
                return;

            // Two samples in a row, so a cursor that flickers between states doesn't count.
            _dragCursorSamples = DragCursor.LooksLikeDragAndDrop() ? _dragCursorSamples + 1 : 0;
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
            _atCursor = false;
            if (_dragCursorSamples >= 2 || IsNearTab(screen, x, y))
            {
                Layout(screen);
                Peek();
            }
        }

        /// <summary>
        /// The tap modifier was pressed and released on its own during a drag (Clop 3.0): show the drop zone under the
        /// cursor, or hide it if it's already there.
        /// </summary>
        private void OnModifierTapped()
        {
            if (_state == ZoneState.Positioning || !_dragging || _pressedAt is null)
                return;

            if (_atCursor && _state != ZoneState.Hidden)
            {
                _tapHidden = true;
                Retract();
                return;
            }

            // A text selection or a window being moved isn't something to drop.
            if (_state == ZoneState.Hidden && !DragCursor.LooksLikeDragAndDrop())
                return;

            _tapHidden = false;
            GetCursorPos(out var cursor);
            var wasHidden = _state == ZoneState.Hidden;
            _atCursor = true;
            LayoutAtCursor(System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)), cursor.X, cursor.Y);
            if (!wasHidden)
                SetDepth(0); // it was on the edge: grow afresh where the cursor is
            Peek();
        }

        /// <summary>Whether a drag at (x, y) physical pixels is close to where the tab lives on that screen.</summary>
        private bool IsNearTab(System.Windows.Forms.Screen screen, int x, int y)
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var area = screen.WorkingArea;
            var tab = DropZoneGeometry.Place(
                new ScreenRect(area.Left, area.Top, area.Right, area.Bottom), _settings.Ui.DropZoneEdge,
                _settings.Ui.DropZonePosition, (int)(Length * scale), (int)(PeekDepth * scale));
            return DropZoneGeometry.IsNear(x, y, tab, (int)(ReachAroundTab * scale));
        }

        // States

        private void Peek()
        {
            _hideTimer.Stop();
            if (_state == ZoneState.Hidden)
            {
                // A new drag starts from the default preset, with the current saved pipelines.
                _presetIndex = 0;
                _presets = DropPresets.WithPipelines(_settings.Pipelines);
                _wheelRemainder = 0;
            }

            _state = ZoneState.Peeking;
            _hook.SwallowWheel = false;
            SetLook(IdleBrush, "Drop to optimise", "Alt keeps the original · Ctrl for aggressive");
            if (!IsVisible)
            {
                SetDepth(0);
                Show();
            }

            // Under the cursor the zone stays full size, ready to drop on; on the edge it only peeks out.
            AnimateDepth(_atCursor ? OpenDepth : PeekDepth, showOpenContent: _atCursor);
        }

        private void Open()
        {
            _hideTimer.Stop();
            _state = ZoneState.Open;
            _hook.SwallowWheel = true;
            AnimateDepth(OpenDepth, showOpenContent: true);
        }

        private void Retract()
        {
            _hideTimer.Stop();
            _state = ZoneState.Hidden;
            _hook.SwallowWheel = false;
            Tab.Cursor = Cursors.Arrow;
            AnimateDepth(0, showOpenContent: false, then: () =>
            {
                if (_state == ZoneState.Hidden)
                    Hide();
            });
        }

        // Layout and animation

        private System.Windows.Forms.Screen CurrentScreen() =>
            System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);

        /// <summary>Sizes and places the window (the tab's fully open area) on the chosen edge of a screen.</summary>
        private void Layout(System.Windows.Forms.Screen screen)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var area = screen.WorkingArea;
            var areaDip = new ScreenRect(
                (int)(area.Left / dpi.DpiScaleX), (int)(area.Top / dpi.DpiScaleY),
                (int)(area.Right / dpi.DpiScaleX), (int)(area.Bottom / dpi.DpiScaleY));
            var edge = _settings.Ui.DropZoneEdge;
            var rect = DropZoneGeometry.Place(areaDip, edge, _settings.Ui.DropZonePosition, (int)Length, (int)OpenDepth);

            Left = rect.Left;
            Top = rect.Top;
            Width = rect.Right - rect.Left;
            Height = rect.Bottom - rect.Top;

            // The tab hugs the edge; its rounded side faces the screen.
            Tab.HorizontalAlignment = edge switch
            {
                ScreenEdge.Left => HorizontalAlignment.Left,
                ScreenEdge.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Stretch,
            };
            Tab.VerticalAlignment = edge switch
            {
                ScreenEdge.Top => VerticalAlignment.Top,
                ScreenEdge.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Stretch,
            };
            Tab.CornerRadius = edge switch
            {
                ScreenEdge.Left => new CornerRadius(0, CornerRadius, CornerRadius, 0),
                ScreenEdge.Right => new CornerRadius(CornerRadius, 0, 0, CornerRadius),
                ScreenEdge.Top => new CornerRadius(0, 0, CornerRadius, CornerRadius),
                _ => new CornerRadius(CornerRadius, CornerRadius, 0, 0),
            };
            Tab.BorderThickness = edge switch
            {
                ScreenEdge.Left => new Thickness(0, 1, 1, 1),
                ScreenEdge.Right => new Thickness(1, 1, 0, 1),
                ScreenEdge.Top => new Thickness(1, 0, 1, 1),
                _ => new Thickness(1, 1, 1, 0),
            };
            // The dimension along the edge fills the window; drop any animation left from the other orientation.
            var along = IsVerticalEdge ? HeightProperty : WidthProperty;
            Tab.BeginAnimation(along, null);
            Tab.ClearValue(along);
        }

        /// <summary>
        /// Places the window centred on the cursor (physical pixels), kept on screen. The box grows out from its
        /// middle and has all its corners rounded, since it isn't attached to an edge.
        /// </summary>
        private void LayoutAtCursor(System.Windows.Forms.Screen screen, int x, int y)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var area = screen.WorkingArea;
            var areaDip = new ScreenRect(
                (int)(area.Left / dpi.DpiScaleX), (int)(area.Top / dpi.DpiScaleY),
                (int)(area.Right / dpi.DpiScaleX), (int)(area.Bottom / dpi.DpiScaleY));
            var rect = DropZoneGeometry.AtCursor(
                (int)(x / dpi.DpiScaleX), (int)(y / dpi.DpiScaleY), (int)CursorZoneWidth, (int)CursorZoneHeight, areaDip);

            Left = rect.Left;
            Top = rect.Top;
            Width = rect.Right - rect.Left;
            Height = rect.Bottom - rect.Top;

            Tab.HorizontalAlignment = HorizontalAlignment.Center;
            Tab.VerticalAlignment = VerticalAlignment.Stretch;
            Tab.CornerRadius = new CornerRadius(CornerRadius);
            Tab.BorderThickness = new Thickness(1);
            Tab.BeginAnimation(HeightProperty, null);
            Tab.ClearValue(HeightProperty);
        }

        private DependencyProperty DepthProperty => IsVerticalEdge ? WidthProperty : HeightProperty;

        private void SetDepth(double depth)
        {
            Tab.BeginAnimation(DepthProperty, null);
            Tab.SetValue(DepthProperty, depth);
        }

        private void AnimateDepth(double depth, bool showOpenContent, Action? then = null)
        {
            var from = Tab.GetValue(DepthProperty) is double current && !double.IsNaN(current) ? current : 0;
            var animation = new DoubleAnimation(from, depth, Animation)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            if (then is not null)
                animation.Completed += (_, _) => then();
            Tab.BeginAnimation(DepthProperty, animation);

            OpenContent.BeginAnimation(OpacityProperty, new DoubleAnimation(showOpenContent ? 1 : 0, Animation));
            PeekIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(showOpenContent || depth == 0 ? 0 : 1, Animation));
        }

        private void SetLook(Brush background, string heading, string detail)
        {
            Tab.Background = background;
            Heading.Text = heading;
            Detail.Text = detail;
        }

        // Drag and drop

        /// <summary>Offers only the pipelines meant for what's being dragged.</summary>
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            var selected = SelectedPreset;
            _presets = DropPresets.WithPipelines(_settings.Pipelines, DraggedFormats(e.Data));
            var index = _presets.ToList().IndexOf(selected);
            _presetIndex = index >= 0 ? index : 0;
        }

        /// <summary>Formats of dragged files, or null when it can't be told cheaply (folders, virtual files).</summary>
        private static IReadOnlyCollection<Core.Media.FileFormat>? DraggedFormats(IDataObject data)
        {
            try
            {
                if (data.GetData(DataFormats.FileDrop) is string[] paths)
                {
                    return paths.Any(System.IO.Directory.Exists)
                        ? null
                        : paths.Select(Core.Media.FileFormats.FromExtension).Distinct().ToList();
                }
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or OutOfMemoryException)
            {
                // Some drag sources refuse to render data early.
            }

            return null;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (_state == ZoneState.Positioning)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            _hideTimer.Stop();
            _usable = DropHandler.MightAccept(e.Data);
            _keys = e.KeyStates;
            e.Effects = _usable ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;

            ShowDropLook();
            if (_state != ZoneState.Open)
                Open();
        }

        /// <summary>The open tab's text for what's being dragged, the selected preset and the held keys.</summary>
        private void ShowDropLook()
        {
            if (!_usable)
            {
                SetLook(RejectBrush, "WClop can't use this", "Drop images, videos, PDFs, audio or links");
                PresetDots.Text = "";
                PresetHint.Text = "";
                return;
            }

            var preset = SelectedPreset;
            var keep = _keys.HasFlag(DragDropKeyStates.AltKey);
            var heading = preset == DropPresets.All[0]
                ? _keys.HasFlag(DragDropKeyStates.ControlKey) ? "Drop to optimise aggressively" : "Drop to optimise"
                : preset.Pipeline is not null ? $"Drop · run {preset.Name}" : $"Drop · {preset.Name}";
            SetLook(AcceptBrush, heading, keep ? "The original is kept; a copy is saved next to it" : preset.Description);
            PresetDots.Text = string.Join("  ", _presets.Select((_, i) => i == _presetIndex ? "●" : "○"));
            PresetHint.Text = "Scroll to change";
        }

        private DropPreset SelectedPreset => _presets[Math.Min(_presetIndex, _presets.Count - 1)];

        /// <summary>Scrolling over the open tab steps through the presets (one step per notch; touchpads accumulate).</summary>
        private void OnWheel(int delta)
        {
            if (_state != ZoneState.Open || !_usable)
                return;

            _wheelRemainder += delta;
            var steps = _wheelRemainder / 120;
            if (steps == 0)
                return;
            _wheelRemainder -= steps * 120;
            // Wheel away from you (positive) goes back up the list, towards you goes down it.
            _presetIndex = DropPresets.Step(_presetIndex, -steps, _presets.Count);
            ShowDropLook();
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (_state == ZoneState.Positioning)
                return;
            try
            {
                var count = _handler.Handle(e.Data, e.KeyStates, SelectedPreset);
                Log.Info($"Drop zone: started {count} job(s)");
            }
            catch (Exception ex)
            {
                Log.Error("Drop zone: handling the drop failed", ex);
            }

            Retract();
        }

        // Positioning: drag the tab to any edge

        private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_state != ZoneState.Positioning)
                return;
            _movingTab = false;
            Tab.CaptureMouse();
            e.Handled = true;
        }

        private void Tab_MouseMove(object sender, MouseEventArgs e)
        {
            if (_state != ZoneState.Positioning || !Tab.IsMouseCaptured)
                return;

            GetCursorPos(out var cursor);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
            var area = screen.WorkingArea;
            var (edge, percent) = DropZoneGeometry.Nearest(cursor.X, cursor.Y, new ScreenRect(area.Left, area.Top, area.Right, area.Bottom));
            if (edge == _settings.Ui.DropZoneEdge && Math.Abs(percent - _settings.Ui.DropZonePosition) < 0.5)
                return;

            _movingTab = true;
            var edgeChanged = edge != _settings.Ui.DropZoneEdge;
            _settings.Ui.DropZoneEdge = edge;
            _settings.Ui.DropZonePosition = percent;
            Layout(screen);
            if (edgeChanged)
                SetDepth(OpenDepth);
        }

        private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_state != ZoneState.Positioning)
                return;
            Tab.ReleaseMouseCapture();
            e.Handled = true;

            if (_movingTab)
            {
                PositionChanged?.Invoke();
                Heading.Text = "Placed here";
                Detail.Text = "Drag to move · click to finish";
                _movingTab = false;
            }
            else
            {
                Retract(); // a click without moving finishes
            }
        }

        private static Brush Frozen(string color)
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
            brush.Freeze();
            return brush;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);
    }
}
