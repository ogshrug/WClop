using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WClop.Clipboard;
using WClop.Core.Logging;
using WClop.Core.Optimisation;
using WClop.Core.Settings;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace WClop.Results
{
    /// <summary>
    /// Floating results in a screen corner (project.md §16). One window holds a stack of cards;
    /// it never takes focus, stays on top, and is hidden from screenshots unless the user allows it.
    /// </summary>
    public partial class ResultsWindow : Window
    {
        private const int MaxCards = 5;
        private const double ScreenMargin = 4;

        /// <summary>Jobs that finish (or get skipped) within this time never flash up a "running" card.</summary>
        private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(150);

        private readonly OptimisationManager _manager;
        private readonly ResultActions _actions;
        private readonly AppSettings _settings;
        private readonly ObservableCollection<ResultCardViewModel> _cards = [];

        private Point? _dragStart;

        internal ResultsWindow(OptimisationManager manager, ResultActions actions, AppSettings settings)
        {
            _manager = manager;
            _actions = actions;
            _settings = settings;
            InitializeComponent();
            Cards.ItemsSource = _cards;

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                WindowInterop.MakeNonActivating(hwnd);
                WindowInterop.SetExcludedFromCapture(hwnd, !_settings.Ui.AllowInScreenshots);
            };
            SizeChanged += (_, _) => PlaceInCorner();

            _manager.JobStarted += job => Dispatcher.BeginInvoke(() => OnJobStarted(job));
            _manager.JobFinished += job => Dispatcher.BeginInvoke(() => OnJobFinished(job));
        }

        /// <summary>Dismiss every card and stop running jobs (Clop's Ctrl+Shift+Escape).</summary>
        internal void ClearAll()
        {
            _manager.CancelAll();
            foreach (var card in _cards.ToList())
                Dismiss(card);
        }

        /// <summary>Re-read display settings (screen-capture visibility, corner) after they change.</summary>
        internal void ApplySettings()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                WindowInterop.SetExcludedFromCapture(hwnd, !_settings.Ui.AllowInScreenshots);
            PlaceInCorner();
        }

        /// <summary>The result hotkeys act on: the one under the mouse, else the newest shown (project.md §17).</summary>
        internal OptimisationJob? CurrentJob =>
            (_cards.FirstOrDefault(c => c.IsHovered) ?? _cards.LastOrDefault())?.Job;

        internal void DismissNewest()
        {
            if (_cards.LastOrDefault() is { } newest)
                Dismiss(newest);
        }

        /// <summary>Bring back the most recently dismissed result.</summary>
        internal void BringBackLast()
        {
            if (_manager.TakeLastRemoved() is { } job)
                Add(CreateCard(job));
        }

        private ResultCardViewModel CreateCard(OptimisationJob job)
        {
            var autoHide = TimeSpan.FromSeconds(job.Source == JobSource.Clipboard
                ? _settings.Ui.ClipboardAutoHideSeconds
                : _settings.Ui.AutoHideSeconds);
            return new ResultCardViewModel(job, Dispatcher, autoHide, Dismiss)
            {
                AutoHideEnabled = _settings.Ui.AutoHideResults,
                HasPipelines = _actions.HasPipelines,
                PipelinesFor = _actions.PipelinesFor,
            };
        }

        private void OnJobStarted(OptimisationJob job)
        {
            if (!_settings.Ui.ShowFloatingResults)
                return;

            var card = CreateCard(job);

            // A redo of the same file (downscale step, aggressive) replaces its card; clipboard results replace
            // each other unless they're set to accumulate.
            var appendClipboard = job.Source == JobSource.Clipboard && _settings.Clipboard.AppendResults;
            foreach (var old in _cards.Where(c => !appendClipboard && c.Job.Key == job.Key).ToList())
                Remove(old);

            // Show once the short delay has passed and the job says there's something to show
            // (a watched file is invisible while it's still being written and checked).
            var delayElapsed = false;
            void TryShow()
            {
                if (delayElapsed && card.Job.IsVisible && !IsHiddenOutcome(card.Job.State) && card.Job.Result is not { HideResult: true })
                    Add(card);
            }

            job.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OptimisationJob.IsVisible))
                    Dispatcher.BeginInvoke(TryShow);
            };
            var timer = new DispatcherTimer(ShowDelay, DispatcherPriority.Normal, (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                delayElapsed = true;
                TryShow();
            }, Dispatcher);
            timer.Start();
        }

        private void OnJobFinished(OptimisationJob job)
        {
            var card = _cards.FirstOrDefault(c => c.Job == job);
            if (card is not null && (IsHiddenOutcome(job.State) || job.Result is { HideResult: true }))
                Remove(card);
        }

        /// <summary>
        /// Skipped jobs had nothing to show, and cancelled ones were either superseded by a newer job
        /// (which gets its own card) or stopped by the user clearing results.
        /// </summary>
        private static bool IsHiddenOutcome(JobState state) => state is JobState.Skipped or JobState.Cancelled;

        private void Add(ResultCardViewModel card)
        {
            if (card.IsShown)
                return;
            card.IsShown = true;
            _cards.Add(card);
            while (_cards.Count > MaxCards)
                Remove(_cards[0]);

            card.RestartAutoHide();
            if (!IsVisible)
                Show();
            PlaceInCorner();
        }

        private void Remove(ResultCardViewModel card)
        {
            card.Detach();
            _cards.Remove(card);
            if (_cards.Count == 0)
                Hide();
        }

        private void Dismiss(ResultCardViewModel card)
        {
            Remove(card);
            _manager.Removed(card.Job);
        }

        private void PlaceInCorner()
        {
            if (!IsVisible)
                return;

            var screen = _settings.Ui.FollowCursorScreen
                ? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position)
                : System.Windows.Forms.Screen.PrimaryScreen!;
            var area = screen.WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);

            double left = area.Left / dpi.DpiScaleX + ScreenMargin;
            double right = area.Right / dpi.DpiScaleX - ActualWidth - ScreenMargin;
            double top = area.Top / dpi.DpiScaleY + ScreenMargin;
            double bottom = area.Bottom / dpi.DpiScaleY - ActualHeight - ScreenMargin;

            (Left, Top) = _settings.Ui.FloatingResultsCorner switch
            {
                ScreenCorner.BottomLeft => (left, bottom),
                ScreenCorner.TopRight => (right, top),
                ScreenCorner.TopLeft => (left, top),
                _ => (right, bottom),
            };
        }

        /// <summary>
        /// The card an event came from, or null if it has already gone: when a card is removed or replaced while the
        /// mouse is over it, WPF still raises MouseLeave, with a placeholder instead of the card.
        /// </summary>
        private static ResultCardViewModel? CardOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as ResultCardViewModel;

        private void Card_MouseEnter(object sender, MouseEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            card.IsHovered = true;
            _manager.Hovered = card.Job;
        }

        private void Card_MouseLeave(object sender, MouseEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            card.IsHovered = false;
            if (_manager.Hovered == card.Job)
                _manager.Hovered = null;
        }

        private void Dismiss_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is { } card)
                Dismiss(card);
        }

        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is { } card)
                await _actions.RestoreAsync(card.Job);
        }

        /// <summary>A menu of sizes to fit the result under (§12).</summary>
        private void Fit_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = (FrameworkElement)sender,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Left,
            };
            foreach (var bytes in ResultActions.FitTargets(card.Job))
            {
                var label = OptimisationJob.FormatBytes(bytes) + bytes switch
                {
                    10L << 20 => "  (Discord)",
                    25L << 20 => "  (email)",
                    _ => "",
                };
                var item = new System.Windows.Controls.MenuItem { Header = "Under " + label };
                item.Click += (_, _) => _actions.Fit(card.Job, bytes);
                menu.Items.Add(item);
            }

            card.IsHovered = true;
            menu.Closed += (_, _) => card.IsHovered = false;
            menu.IsOpen = true;
        }

        /// <summary>A menu of formats this result can be converted to (§8.8, §9.4).</summary>
        private void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            var button = (FrameworkElement)sender;
            // Anchored to the button (the default is the mouse position, which is wrong for keyboard or accessibility use).
            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = button,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Left,
            };
            foreach (var target in ResultActions.ConversionTargets(card.Job))
            {
                var label = target switch
                {
                    Core.Media.FileFormat.Mp4 => "MP4 (HEVC, smaller)",
                    Core.Media.FileFormat.WebM => "WebM (VP9)",
                    Core.Media.FileFormat.Gif when card.Job.Result?.Kind == Core.Media.MediaKind.Video => "Animated GIF",
                    _ => target.ToString().ToUpperInvariant(),
                };
                var item = new System.Windows.Controls.MenuItem { Header = label };
                item.Click += (_, _) => _actions.Convert(card.Job, target);
                menu.Items.Add(item);
            }

            card.IsHovered = true; // keep the card while the menu is open
            menu.Closed += (_, _) => card.IsHovered = false;
            menu.IsOpen = true;
        }

        private void RunPipeline_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = (FrameworkElement)sender,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Left,
            };
            foreach (var name in _actions.PipelinesFor(card.Job))
            {
                var item = new System.Windows.Controls.MenuItem { Header = name };
                item.Click += (_, _) => _actions.RunPipeline(card.Job, name);
                menu.Items.Add(item);
            }

            card.IsHovered = true; // keep the card while the menu is open
            menu.Closed += (_, _) => card.IsHovered = false;
            menu.IsOpen = true;
        }

        private void ShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender)?.Job.CurrentPath is { } path && File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        private void Thumbnail_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

        /// <summary>Drag the result out as a file into any app (project.md §16.1, §26.1 DoDragDrop).</summary>
        private void Thumbnail_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start)
                return;

            var delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragStart = null;
            if (CardOf(sender) is not { } card)
                return;
            if (card.Job.CurrentPath is not { } path || !File.Exists(path))
                return;

            card.IsHovered = true; // keep it alive while dragging
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            var effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);

            if (effect != DragDropEffects.None && _settings.Ui.DismissOnDrop)
                Dismiss(card);
            else
                card.IsHovered = false;
        }
    }
}
