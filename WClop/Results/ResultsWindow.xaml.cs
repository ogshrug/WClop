using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WClop.Core.Compression;
using WClop.Core.Cropping;
using WClop.Core.Images;
using WClop.Core.Logging;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Settings;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace WClop.Results
{
    /// <summary>
    /// Floating results in a screen corner (project.md §16). One window holds a stack of cards, or a compact list once
    /// there are more than the threshold; it never takes focus (except while you type a name or a crop size), stays on
    /// top, and is hidden from screenshots unless the user allows it.
    /// </summary>
    public partial class ResultsWindow : Window
    {
        /// <summary>The compact list holds this many; older results go (and can be brought back).</summary>
        private const int MaxResults = 30;

        private const double ScreenMargin = 4;

        /// <summary>Jobs that finish (or get skipped) within this time never flash up a "running" card.</summary>
        private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(150);

        private readonly OptimisationManager _manager;
        private readonly ResultActions _actions;
        private readonly AppSettings _settings;
        private readonly ObservableCollection<ResultCardViewModel> _cards = [];

        private Point? _dragStart;
        private HoverKeys? _hoverKeys;
        private bool _isCompact;

        /// <summary>While a card has the keyboard: the window the user was in, to give it back to afterwards.</summary>
        private bool _takingInput;

        private IntPtr _focusBefore;

        internal ResultsWindow(OptimisationManager manager, ResultActions actions, AppSettings settings)
        {
            _manager = manager;
            _actions = actions;
            _settings = settings;
            InitializeComponent();
            Cards.ItemsSource = _cards;
            CompactItems.ItemsSource = _cards;
            _cards.CollectionChanged += (_, _) => UpdateLayoutMode();

            SourceInitialized += (_, _) =>
            {
                var hwnd = Handle;
                WindowInterop.MakeNonActivating(hwnd);
                WindowInterop.SetExcludedFromCapture(hwnd, !_settings.Ui.AllowInScreenshots);
                _hoverKeys = new HoverKeys(HwndSource.FromHwnd(hwnd)!, OnHoverKey);
            };
            SizeChanged += (_, _) => PlaceInCorner();
            // Typing on a card ends when you click elsewhere: the window stops taking focus again.
            Deactivated += (_, _) => EndInput(giveFocusBack: false);

            _manager.JobStarted += job => Dispatcher.BeginInvoke(() => OnJobStarted(job));
            _manager.JobFinished += job => Dispatcher.BeginInvoke(() => OnJobFinished(job));
        }

        private IntPtr Handle => new WindowInteropHelper(this).Handle;

        /// <summary>Dismiss every card and stop running jobs (Clop's Ctrl+Shift+Escape).</summary>
        internal void ClearAll()
        {
            _manager.CancelAll();
            foreach (var card in _cards.ToList())
                Dismiss(card);
        }

        /// <summary>Re-read display settings (screen-capture visibility, corner, compact list) after they change.</summary>
        internal void ApplySettings()
        {
            var hwnd = Handle;
            if (hwnd != IntPtr.Zero)
                WindowInterop.SetExcludedFromCapture(hwnd, !_settings.Ui.AllowInScreenshots);
            UpdateLayoutMode();
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
                ShowFormatBar = _settings.ResultCards.ShowFormatBar,
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
            {
                // The redo keeps its place in a selection.
                card.IsSelected = old.IsSelected;
                Remove(old);
            }

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
            while (_cards.Count > MaxResults)
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

        /// <summary>More results than the threshold collapse into the compact list (project.md §16.2).</summary>
        private void UpdateLayoutMode()
        {
            var compact = _settings.ResultCards.IsCompact(_cards.Count);
            if (compact != _isCompact)
            {
                _isCompact = compact;
                Cards.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                CompactPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                if (!compact)
                {
                    foreach (var card in _cards)
                        card.IsSelected = false;
                }

                UpdateHoverKeys();
            }

            UpdateCompactHeader();
        }

        private void UpdateCompactHeader()
        {
            if (!_isCompact)
                return;
            var selected = _cards.Count(c => c.IsSelected);
            var saved = _cards.Where(c => c.Job.Result is not null && c.Job.State == JobState.Succeeded)
                .Sum(c => c.Job.Result!.OldSize - c.Job.Result.NewSize);
            DragAllText.Text = selected == 0 ? "Drag all" : $"Drag {selected}";
            CompactSummary.Text = (selected == 0 ? $"{_cards.Count} results" : $"{selected} of {_cards.Count} selected")
                                  + (saved > 0 ? $" · {OptimisationJob.FormatBytes(saved)} saved" : "");
        }

        /// <summary>The selected rows, or every row if none are selected.</summary>
        private List<ResultCardViewModel> SelectedOrAll()
        {
            var selected = _cards.Where(c => c.IsSelected).ToList();
            return selected.Count > 0 ? selected : _cards.ToList();
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

        // Keyboard: hover shortcuts, and typing on a card

        private void Window_MouseEnter(object sender, MouseEventArgs e) => UpdateHoverKeys();

        private void Window_MouseLeave(object sender, MouseEventArgs e) => UpdateHoverKeys();

        /// <summary>Ctrl+E while hovering; Ctrl+A and Esc too in the compact list. Never while a card has the keyboard.</summary>
        private void UpdateHoverKeys()
        {
            if (_hoverKeys is null)
                return;
            if (_takingInput || !IsMouseOver)
            {
                _hoverKeys.Clear();
                return;
            }

            _hoverKeys.Set(_isCompact ? [HoverKey.Edit, HoverKey.SelectAll, HoverKey.ClearSelection] : [HoverKey.Edit]);
        }

        private void OnHoverKey(HoverKey key)
        {
            switch (key)
            {
                case HoverKey.Edit when _cards.FirstOrDefault(c => c.IsHovered) is { } hovered:
                    EditWith(hovered);
                    break;
                case HoverKey.SelectAll:
                    foreach (var card in _cards)
                        card.IsSelected = true;
                    UpdateCompactHeader();
                    break;
                case HoverKey.ClearSelection:
                    foreach (var card in _cards)
                        card.IsSelected = false;
                    UpdateCompactHeader();
                    break;
            }
        }

        /// <summary>
        /// Lets the window take the keyboard (it normally never activates): for typing a new name or a crop size, and so
        /// the share sheet has an active window to belong to. Ends when the window loses focus.
        /// </summary>
        private void BeginInput(bool rememberFocus = true)
        {
            if (_takingInput)
                return;
            _takingInput = true;
            _focusBefore = rememberFocus ? WindowInterop.ForegroundWindow() : IntPtr.Zero;
            UpdateHoverKeys(); // Esc and Ctrl+A belong to the text box now
            WindowInterop.SetNonActivating(Handle, false);
            Activate();
        }

        private void EndInput(bool giveFocusBack = true)
        {
            if (!_takingInput)
                return;
            _takingInput = false;
            WindowInterop.SetNonActivating(Handle, true);
            var before = _focusBefore;
            _focusBefore = IntPtr.Zero;
            if (giveFocusBack && IsActive)
                WindowInterop.GiveFocusTo(before);
            UpdateHoverKeys();
        }

        private void CardInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            BeginInput();
            if (sender is TextBox box && !box.IsKeyboardFocusWithin)
            {
                box.Focus();
                box.SelectAll();
                e.Handled = true;
            }
        }

        // Rename: click the name

        private void Title_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (CardOf(sender) is { } card)
                StartRename(card);
        }

        private void StartRename(ResultCardViewModel card)
        {
            if (!card.CanRename)
                return;
            card.RenameText = Path.GetFileName(card.Job.CurrentPath!);
            BeginInput();
            card.IsRenaming = true;
        }

        private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true || sender is not TextBox box)
                return;
            // Select the name without its extension, as Explorer does.
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                Keyboard.Focus(box);
                var stem = Path.GetFileNameWithoutExtension(box.Text);
                box.Select(0, stem.Length > 0 ? stem.Length : box.Text.Length);
            }, DispatcherPriority.Input);
        }

        private async void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (CardOf(sender) is not { IsRenaming: true } card)
                return;
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                card.IsRenaming = false;
                EndInput();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await CommitRenameAsync(card);
                EndInput();
            }
        }

        private async void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Clicking away keeps what was typed, like Explorer.
            if (CardOf(sender) is { IsRenaming: true } card)
                await CommitRenameAsync(card);
        }

        private async Task CommitRenameAsync(ResultCardViewModel card)
        {
            card.IsRenaming = false;
            var typed = card.RenameText;
            if (card.Job.CurrentPath is { } path && typed.Trim() != Path.GetFileName(path))
                await _actions.RenameAsync(card.Job, typed);
        }

        // Format bar, sliders, crop

        private void Format_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is { } card && (sender as FrameworkElement)?.Tag is FormatChoice choice)
                _actions.Convert(card.Job, choice.Target);
        }

        /// <summary>Downscale with a slider (10–100 %), redone from the original on release like the − and 1–9 hotkeys.</summary>
        private void Downscale_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            var job = card.Job;
            var current = ResultActions.CurrentScale(job);
            // The original's size, for "75% (1440×810)" as you drag.
            var original = job.Result?.BackupPath is { } backup && ImageDecoding.TryReadSize(backup) is { } size
                ? size
                : card.PixelSize is { } now ? new ImageSize((int)Math.Round(now.Width / current), (int)Math.Round(now.Height / current)) : null;

            ShowSlider((FrameworkElement)sender, card, 10, 100, 5, Math.Round(current * 100),
                value => value >= 100
                    ? "Full size" + (original is null ? "" : $" ({original.Width}×{original.Height})")
                    : $"{value:0}%" + (original is null ? "" : $" ({ImageResizer.ScaledSize(original, value / 100)})"),
                value => _actions.Adjust(job, null, value / 100, ResultActions.ScalingLabel(job, value / 100)),
                "Drag, then let go to downscale from the original");
        }

        /// <summary>
        /// Compression with a slider: the factor for images, video and audio (like the A hotkey, but any level), the DPI
        /// stops for PDFs (like −). Redone from the original on release.
        /// </summary>
        private void Compression_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            var job = card.Job;
            var anchor = (FrameworkElement)sender;
            if (card.Kind == MediaKind.Pdf)
            {
                var stops = Core.Pdf.PdfAnalysis.DpiStops;
                var currentDpi = job.Result?.Dpi ?? Core.Pdf.PdfAnalysis.LosslessDpi;
                var index = Math.Max(0, stops.ToList().FindIndex(s => s <= currentDpi));
                ShowSlider(anchor, card, 0, stops.Count - 1, 1, index,
                    value => stops[(int)value] >= Core.Pdf.PdfAnalysis.LosslessDpi ? "Lossless (300 DPI)" : $"Images at {stops[(int)value]} DPI",
                    value => _actions.AdjustPdf(job, stops[(int)value]),
                    "Lower DPI, smaller file");
                return;
            }

            var audio = card.Kind == MediaKind.Audio;
            ShowSlider(anchor, card, CompressionModel.MinFactor, CompressionModel.MaxFactor, 5, ResultActions.CurrentFactor(job, _settings),
                value => $"Factor {value:0}" + (audio ? "" : CompressionModel.IsAggressive((int)value) ? " (aggressive)" : ""),
                value => _actions.Adjust(job, (int)value, null, audio
                    ? $"Lowering the bitrate (factor {value:0})"
                    : $"Optimising at factor {value:0}" + (CompressionModel.IsAggressive((int)value) ? " (aggressive)" : "")),
                "Higher is smaller, with more loss");
        }

        /// <summary>A small slider beside a card button; <paramref name="apply"/> runs when you let go at a new value.</summary>
        private void ShowSlider(
            FrameworkElement anchor, ResultCardViewModel card, double minimum, double maximum, double step, double value,
            Func<double, string> describe, Action<double> apply, string hint)
        {
            var label = new TextBlock
            {
                Text = describe(value),
                Foreground = (Brush)FindResource("PrimaryText"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
            };
            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Clamp(value, minimum, maximum),
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                IsMoveToPointEnabled = true,
                Focusable = false,
                Width = 190,
            };
            slider.ValueChanged += (_, args) => label.Text = describe(args.NewValue);

            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Left,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = new Border
                {
                    Background = (Brush)FindResource("CardBackground"),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new StackPanel
                    {
                        Children =
                        {
                            label,
                            slider,
                            new TextBlock { Text = hint, Foreground = (Brush)FindResource("SecondaryText"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) },
                        },
                    },
                },
            };

            var start = slider.Value;
            // After the thumb (or a click on the track) lets go, so the value is the final one.
            slider.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (Math.Abs(slider.Value - start) < 1e-6)
                    return;
                popup.IsOpen = false;
                apply(slider.Value);
            }, DispatcherPriority.Input);

            card.IsHovered = true; // keep the card while the slider is open
            popup.Closed += (_, _) => card.IsHovered = false;
            popup.IsOpen = true;
        }

        private void Crop_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is { } card)
                card.IsCropOpen = !card.IsCropOpen;
        }

        private void CropRatio_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card || (sender as FrameworkElement)?.Tag is not string ratio
                || !CropSpec.TryParseRatio(ratio, out var value))
                return;
            StartCrop(card, new CropSpec { AspectRatio = value, Smart = card.CropSmart });
        }

        private void CropSize_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is { } card)
                CropToTypedSize(card);
        }

        private void CropSize_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CropToTypedSize(card);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                card.IsCropOpen = false;
                EndInput();
            }
        }

        private void CropToTypedSize(ResultCardViewModel card)
        {
            if (!CropSpec.TryParseSize($"{card.CropWidthText}x{card.CropHeightText}", out var width, out var height))
            {
                card.Job.Status = "Type a width and height in pixels to crop to";
                return;
            }

            StartCrop(card, new CropSpec { Width = width, Height = height, Smart = card.CropSmart });
        }

        private void StartCrop(ResultCardViewModel card, CropSpec crop)
        {
            card.IsCropOpen = false;
            EndInput();
            _actions.Crop(card.Job, crop);
        }

        // Share, Edit with…, menus

        private async void Share_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is { Job.CurrentPath: { } path } card && File.Exists(path))
                await ShareAsync([path], card);
        }

        private async void ShareSelected_Click(object sender, RoutedEventArgs e)
        {
            var paths = SelectedOrAll().Select(c => c.Job.CurrentPath).OfType<string>().Where(File.Exists).ToList();
            if (paths.Count > 0)
                await ShareAsync(paths, null);
        }

        /// <summary>The Windows share sheet (project.md §20.6) for the window the cards are in.</summary>
        private async Task ShareAsync(IReadOnlyList<string> paths, ResultCardViewModel? card)
        {
            if (card is not null)
                card.IsHovered = true;
            try
            {
                BeginInput(rememberFocus: false);
                await ShareSheet.ShowAsync(Handle, paths);
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException or TimeoutException
                                          or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                Log.Error("Sharing failed", e);
                if (card is not null)
                    card.Job.Status = "Couldn't share: " + e.Message;
            }
            finally
            {
                if (card is not null)
                    card.IsHovered = card.IsHovered && IsMouseOver;
            }
        }

        private void EditWith_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is { } card)
                EditWith(card);
        }

        private void EditWith(ResultCardViewModel card)
        {
            if (!card.CanEdit)
                return;
            card.IsHovered = true; // keep it while the "Open with" dialog is up
            try
            {
                ResultActions.EditWith(card.Job, Handle, _settings);
            }
            finally
            {
                card.IsHovered = IsMouseOver && card.IsHovered;
            }
        }

        /// <summary>Right-click a card or row: everything it can do, including what has no button.</summary>
        private void Card_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            e.Handled = true;
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.MousePoint };

            MenuItem Item(string header, bool enabled, Action action, string? gesture = null)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
                return item;
            }

            Item("Rename", card.CanRename && !_isCompact, () => StartRename(card));
            Item("Edit with…", card.CanEdit, () => EditWith(card), "Ctrl+E");
            Item("Share…", card.CanShare, () => _ = ShareAsync([card.Job.CurrentPath!], card));
            Item("Crop…", card.CanCrop && !_isCompact, () => card.IsCropOpen = true);
            var convert = Item("Convert to", card.CanConvert, () => { });
            foreach (var choice in ResultActions.FormatChoicesFor(card.Job, card.Codec))
            {
                var target = new MenuItem { Header = choice.Label };
                target.Click += (_, _) => _actions.Convert(card.Job, choice.Target);
                convert.Items.Add(target);
            }

            convert.IsEnabled = convert.Items.Count > 0;
            Item("Show in Explorer", card.CanShowFile, () => ShowInFolder(card));
            menu.Items.Add(new Separator());
            Item("Dismiss", true, () => Dismiss(card));

            card.IsHovered = true; // keep the card while the menu is open
            menu.Closed += (_, _) => card.IsHovered = false;
            menu.IsOpen = true;
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
            var menu = new ContextMenu
            {
                PlacementTarget = (FrameworkElement)sender,
                Placement = PlacementMode.Left,
            };
            foreach (var bytes in ResultActions.FitTargets(card.Job))
            {
                var label = OptimisationJob.FormatBytes(bytes) + bytes switch
                {
                    10L << 20 => "  (Discord)",
                    25L << 20 => "  (email)",
                    _ => "",
                };
                var item = new MenuItem { Header = "Under " + label };
                item.Click += (_, _) => _actions.Fit(card.Job, bytes);
                menu.Items.Add(item);
            }

            card.IsHovered = true;
            menu.Closed += (_, _) => card.IsHovered = false;
            menu.IsOpen = true;
        }

        /// <summary>
        /// A menu of formats this result can be converted to (§8.8, §9.4): the format bar's choices, for when the bar is
        /// turned off in Settings → Results.
        /// </summary>
        private void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card)
                return;
            var button = (FrameworkElement)sender;
            // Anchored to the button (the default is the mouse position, which is wrong for keyboard or accessibility use).
            var menu = new ContextMenu
            {
                PlacementTarget = button,
                Placement = PlacementMode.Left,
            };
            foreach (var choice in ResultActions.FormatChoicesFor(card.Job, card.Codec))
            {
                var item = new MenuItem { Header = choice.Label };
                item.Click += (_, _) => _actions.Convert(card.Job, choice.Target);
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
            var menu = new ContextMenu
            {
                PlacementTarget = (FrameworkElement)sender,
                Placement = PlacementMode.Left,
            };
            foreach (var name in _actions.PipelinesFor(card.Job))
            {
                var item = new MenuItem { Header = name };
                item.Click += (_, _) => _actions.RunPipeline(card.Job, name);
                menu.Items.Add(item);
            }

            card.IsHovered = true; // keep the card while the menu is open
            menu.Closed += (_, _) => card.IsHovered = false;
            menu.IsOpen = true;
        }

        private void ShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is { } card)
                ShowInFolder(card);
        }

        private static void ShowInFolder(ResultCardViewModel card)
        {
            if (card.Job.CurrentPath is { } path && File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        // Compact list

        private void CompactCheck_Click(object sender, RoutedEventArgs e) => UpdateCompactHeader();

        private void SelectAll_Click(object sender, RoutedEventArgs e) => OnHoverKey(HoverKey.SelectAll);

        private void DismissSelected_Click(object sender, RoutedEventArgs e)
        {
            foreach (var card in SelectedOrAll())
                Dismiss(card);
        }

        private void DragAll_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

        /// <summary>Drags every selected file (all of them if none are selected) into another app at once.</summary>
        private void DragAll_MouseMove(object sender, MouseEventArgs e)
        {
            if (!DragStarted(e))
                return;

            var cards = SelectedOrAll().Where(c => c.Job.CurrentPath is { } path && File.Exists(path)).ToList();
            if (cards.Count == 0)
                return;
            foreach (var card in cards)
                card.IsHovered = true; // keep them alive while dragging

            var data = new DataObject(DataFormats.FileDrop, cards.Select(c => c.Job.CurrentPath!).ToArray());
            var effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);

            foreach (var card in cards)
            {
                if (effect != DragDropEffects.None && _settings.Ui.DismissOnDrop)
                    Dismiss(card);
                else
                    card.IsHovered = false;
            }
        }

        private bool DragStarted(MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start)
                return false;

            var delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return false;

            _dragStart = null;
            return true;
        }

        private void Thumbnail_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

        /// <summary>Drag the result out as a file into any app (project.md §16.1, §26.1 DoDragDrop).</summary>
        private void Thumbnail_MouseMove(object sender, MouseEventArgs e)
        {
            if (!DragStarted(e))
                return;
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
