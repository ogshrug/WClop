using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WClop.Core;
using WClop.Core.Hotkeys;
using WClop.Core.Logging;
using WClop.Core.Placement;
using WClop.Core.Settings;
using WClop.Hotkeys;
using WClop.Integration;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace WClop.Settings
{
    /// <summary>
    /// The settings window. It edits the live <see cref="AppSettings"/> directly; every change is applied
    /// (saved, watchers restarted, hotkeys re-registered) shortly after it's made, so there's no OK button.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private static readonly TimeSpan ApplyDelay = TimeSpan.FromMilliseconds(350);

        private readonly AppSettings _settings;
        private readonly AppPaths _paths;
        private readonly SettingsStore _store;
        private readonly HotkeyManager _hotkeys;
        private readonly Action _apply;
        private readonly Action _positionDropZone;
        private readonly DispatcherTimer _applyTimer;
        private readonly ObservableCollection<string> _ignoredApps;
        private readonly ObservableCollection<string> _folders = [];
        private readonly List<HotkeyRow> _hotkeyRows;
        private bool _loaded;
        private string _appliedSnapshot;

        internal SettingsWindow(
            AppSettings settings, AppPaths paths, SettingsStore store, HotkeyManager hotkeys, Action apply, Action positionDropZone)
        {
            _settings = settings;
            _paths = paths;
            _store = store;
            _hotkeys = hotkeys;
            _apply = apply;
            _positionDropZone = positionDropZone;
            _applyTimer = new DispatcherTimer(ApplyDelay, DispatcherPriority.Normal, (_, _) => ApplyNow(), Dispatcher);
            _applyTimer.Stop();

            InitializeComponent();
            AddSearch();
            DataContext = settings;
            _appliedSnapshot = SettingsStore.Snapshot(settings);
            PipelinesTab.Load(settings);
            VersionText.Text = $"WClop {AppPaths.Version}";
            PipelinesTab.Changed += ScheduleApply;
            WatermarkTab.Load(settings);
            WatermarkTab.Changed += ScheduleApply;
            ResultCardsSection.Load(settings);
            ResultCardsSection.Changed += ScheduleApply;

            CleanupBox.ItemsSource = new[]
            {
                new Choice<CleanupInterval>(CleanupInterval.Every10Minutes, "After 10 minutes"),
                new Choice<CleanupInterval>(CleanupInterval.Hourly, "After an hour"),
                new Choice<CleanupInterval>(CleanupInterval.Every12Hours, "After 12 hours"),
                new Choice<CleanupInterval>(CleanupInterval.Daily, "After a day"),
                new Choice<CleanupInterval>(CleanupInterval.Every3Days, "After 3 days"),
                new Choice<CleanupInterval>(CleanupInterval.Never, "Never"),
            };
            PlacementBox.ItemsSource = new[]
            {
                new Choice<OutputBehaviour>(OutputBehaviour.InPlace, "Replace the original"),
                new Choice<OutputBehaviour>(OutputBehaviour.SameFolder, "Save a copy next to it"),
                new Choice<OutputBehaviour>(OutputBehaviour.SpecificFolder, "Save a copy in another folder"),
                new Choice<OutputBehaviour>(OutputBehaviour.Temporary, "Keep it in WClop's working folder"),
            };
            VideoTierBox.ItemsSource = new[]
            {
                new Choice<VideoTier>(VideoTier.Fast, "Fast: graphics card when available"),
                new Choice<VideoTier>(VideoTier.Smaller, "Smaller: software (x264), slower"),
                new Choice<VideoTier>(VideoTier.Adaptive, "Adaptive: software for short clips"),
                new Choice<VideoTier>(VideoTier.Lossless, "Near-lossless (x264, large files)"),
            };
            _ = ShowEncoderStatusAsync();
            PdfModeBox.ItemsSource = new[]
            {
                new Choice<PdfDpiMode>(PdfDpiMode.Adaptive, "Adaptive"),
                new Choice<PdfDpiMode>(PdfDpiMode.Fixed, "Fixed"),
            };
            PdfDpiBox.ItemsSource = Core.Pdf.PdfAnalysis.DpiStops
                .Select(dpi => new Choice<int>(dpi, dpi == 300 ? "300 (lossless)" : $"{dpi} DPI"))
                .ToArray();
            AudioFactor_Changed(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
            CoverArtBox.ItemsSource = new[]
            {
                new Choice<CoverArtMode>(CoverArtMode.Keep, "Keep it as it is"),
                new Choice<CoverArtMode>(CoverArtMode.Optimise, "Optimise it (smaller JPEG, same size)"),
                new Choice<CoverArtMode>(CoverArtMode.Remove, "Remove it"),
            };
            ConvertToJpegBox.IsChecked = settings.Compression.ConvertToJpeg.Count > 0;
            ConvertToPngBox.IsChecked = settings.Compression.ConvertToPng.Count > 0;

            DropEdgeBox.ItemsSource = new[]
            {
                new Choice<WClop.Core.DropZone.ScreenEdge>(WClop.Core.DropZone.ScreenEdge.Right, "Right"),
                new Choice<WClop.Core.DropZone.ScreenEdge>(WClop.Core.DropZone.ScreenEdge.Left, "Left"),
                new Choice<WClop.Core.DropZone.ScreenEdge>(WClop.Core.DropZone.ScreenEdge.Top, "Top"),
                new Choice<WClop.Core.DropZone.ScreenEdge>(WClop.Core.DropZone.ScreenEdge.Bottom, "Bottom"),
            };
            DropTapKeyBox.ItemsSource = new[]
            {
                new Choice<WClop.Core.DropZone.TapModifier>(WClop.Core.DropZone.TapModifier.Alt, "Alt"),
                new Choice<WClop.Core.DropZone.TapModifier>(WClop.Core.DropZone.TapModifier.Ctrl, "Ctrl"),
                new Choice<WClop.Core.DropZone.TapModifier>(WClop.Core.DropZone.TapModifier.Shift, "Shift"),
                new Choice<WClop.Core.DropZone.TapModifier>(WClop.Core.DropZone.TapModifier.None, "Nothing (off)"),
            };
            CornerBox.ItemsSource = new[]
            {
                new Choice<ScreenCorner>(ScreenCorner.BottomRight, "Bottom right"),
                new Choice<ScreenCorner>(ScreenCorner.BottomLeft, "Bottom left"),
                new Choice<ScreenCorner>(ScreenCorner.TopRight, "Top right"),
                new Choice<ScreenCorner>(ScreenCorner.TopLeft, "Top left"),
            };

            WorkDirBox.Tag = AppPaths.DefaultWorkDir;
            if (string.IsNullOrEmpty(settings.Files.WorkDir))
                WorkDirBox.Text = "";
            WorkDirBox.ToolTip = $"Empty means the default: {AppPaths.DefaultWorkDir}";

            LaunchAtLoginBox.IsChecked = LaunchAtLogin.IsEnabled;
            LaunchAtLoginBox.Click += (_, _) => SetLaunchAtLogin(LaunchAtLoginBox.IsChecked == true);
            BindIntegration(ContextMenuBox, ShellIntegration.SetContextMenuEnabled, () => ShellIntegration.IsContextMenuEnabled);
            BindIntegration(SendToBox, ShellIntegration.SetSendToEnabled, () => ShellIntegration.IsSendToEnabled);

            _ignoredApps = new ObservableCollection<string>(settings.Clipboard.IgnoredApps);
            IgnoredAppsList.ItemsSource = _ignoredApps;
            RunningAppsBox.ItemsSource = RunningApps();

            FoldersList.ItemsSource = _folders;
            ShowWatcher(settings.Watching.Images, hasEngine: true);

            var modifiers = HotkeyManager.ModifierNames(settings.Hotkeys.Modifiers);
            ModCtrl.IsChecked = modifiers.Contains("Ctrl");
            ModAlt.IsChecked = modifiers.Contains("Alt");
            ModShift.IsChecked = modifiers.Contains("Shift");
            ModWin.IsChecked = modifiers.Contains("Win");
            UpdateModifierHint();

            _hotkeyRows = HotkeyCatalog.Commands.Select(c => new HotkeyRow(c, settings.Hotkeys)).ToList();
            foreach (var row in _hotkeyRows)
                row.Edited += ScheduleApply;
            HotkeyRows.ItemsSource = _hotkeyRows;
            RefreshHotkeyStatus();
            _hotkeys.Changed += OnHotkeysChanged;

            UpdateFactorLabel();
            UpdateTemplatePreviews();

            // Any edit anywhere in the window schedules an apply. Bindings have already updated the settings by the
            // time these bubble up (a text box commits in its own LostFocus handling first).
            AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnEdited));
            AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnEdited));
            AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnEdited));
            AddHandler(UIElement.LostFocusEvent, new RoutedEventHandler(OnEdited));
            AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(OnEdited));

            Loaded += (_, _) => _loaded = true;
            Closing += (_, _) => CommitFocusedTextBox();
            Closed += (_, _) =>
            {
                _hotkeys.Changed -= OnHotkeysChanged;
                ApplyNow();
            };
        }

        /// <summary>
        /// Puts the search box above the tabs. It's added here rather than in the XAML so the tabs' markup stays as it
        /// is; the search reads whatever the tabs contain.
        /// </summary>
        private void AddSearch()
        {
            var tabs = (TabControl)Content;
            Content = null;
            var search = new SettingsSearchBox { Margin = new Thickness(8, 8, 8, 0) };
            DockPanel.SetDock(search, Dock.Top);
            Content = new DockPanel { Children = { search, tabs } };
            search.Attach(tabs, this);
        }

        private void OnEdited(object sender, RoutedEventArgs e)
        {
            if (!_loaded)
                return;

            // Only real edits count: switching tabs, selecting in a list or tabbing past a button doesn't.
            var isEdit = e.OriginalSource switch
            {
                ComboBox box => box != WatchKindBox,
                TextBox => e.RoutedEvent == UIElement.LostFocusEvent,
                ToggleButton => e.RoutedEvent != UIElement.LostFocusEvent,
                RangeBase => e.RoutedEvent != UIElement.LostFocusEvent,
                _ => false,
            };
            if (isEdit)
                ScheduleApply();
        }

        private Updates.Updater? _updater;

        /// <summary>The app's updater, for Settings → General → Updates.</summary>
        internal Updates.Updater? Updater
        {
            set
            {
                if (_updater is not null)
                    _updater.Changed -= OnUpdaterChanged;
                _updater = value;
                if (value is not null)
                {
                    value.Changed += OnUpdaterChanged;
                    Closed += (_, _) => value.Changed -= OnUpdaterChanged;
                }

                ShowUpdateState();
            }
        }

        private void OnUpdaterChanged() => Dispatcher.BeginInvoke(ShowUpdateState);

        private void ShowUpdateState()
        {
            var updater = _updater;
            CheckUpdatesButton.IsEnabled = updater is { IsBusy: false };
            UpdateStatusText.Text = updater?.Status is { Length: > 0 } status ? status : $"You have WClop {AppPaths.Version}.";
            UpdateProgress.Visibility = updater?.Progress is not null ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgress.Value = updater?.Progress ?? 0;

            var update = updater?.Available;
            InstallUpdateButton.Visibility = update is not null && Updates.Updater.IsInstalledCopy ? Visibility.Visible : Visibility.Collapsed;
            InstallUpdateButton.IsEnabled = updater is { IsBusy: false };
            InstallUpdateButton.Content = update is null ? "" : $"Download and install {update.Version}";
            ReleaseNotesText.Visibility = update is not null ? Visibility.Visible : Visibility.Collapsed;
            if (update is not null)
                ReleaseNotesLink.NavigateUri = update.ReleasePage;
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_updater is not null)
                await _updater.CheckAsync(manual: true);
        }

        private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_updater is not null)
                await _updater.InstallAsync();
        }

        /// <summary>Runs a saved pipeline on a file, for "Try it on a file…".</summary>
        public Action<string, string>? TryPipeline
        {
            set => PipelinesTab.TryPipeline = value;
        }

        private void ScheduleApply()
        {
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        private void ApplyNow()
        {
            _applyTimer.Stop();
            _settings.Clipboard.IgnoredApps = _ignoredApps.ToList();

            // Focus changes and re-selections fire the same events as edits; only apply real changes.
            var snapshot = SettingsStore.Snapshot(_settings);
            if (snapshot == _appliedSnapshot)
                return;
            _appliedSnapshot = snapshot;

            try
            {
                _apply();
            }
            catch (Exception e)
            {
                Log.Error("Applying settings failed", e);
            }
        }

        private void CommitFocusedTextBox()
        {
            if (Keyboard.FocusedElement is TextBox box)
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        // General

        private void SetLaunchAtLogin(bool enabled)
        {
            try
            {
                LaunchAtLogin.SetEnabled(enabled);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Error("Couldn't change start-at-login", e);
                LaunchAtLoginBox.IsChecked = LaunchAtLogin.IsEnabled;
            }
        }

        /// <summary>
        /// A checkbox mirroring something outside the settings file. Checked/Unchecked rather than Click, so keyboard
        /// and UI Automation toggles work too.
        /// </summary>
        private static void BindIntegration(CheckBox box, Action<bool> set, Func<bool> isEnabled)
        {
            box.IsChecked = isEnabled();
            var updating = false;
            void Changed(object sender, RoutedEventArgs e)
            {
                if (updating)
                    return;
                try
                {
                    set(box.IsChecked == true);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException
                                                  or IOException or System.Runtime.InteropServices.COMException)
                {
                    Log.Error($"Couldn't change {box.Content}", error);
                }

                updating = true;
                box.IsChecked = isEnabled();
                updating = false;
            }

            box.Checked += Changed;
            box.Unchecked += Changed;
        }

        private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void BrowseWorkDir_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose WClop's working folder",
                InitialDirectory = _settings.Files.ResolveWorkDir(),
            };
            if (dialog.ShowDialog(this) != true)
                return;

            _settings.Files.WorkDir = PortablePath.Contract(dialog.FolderName);
            WorkDirBox.Text = _settings.Files.WorkDir;
            ScheduleApply();
        }

        private void OpenWorkDir_Click(object sender, RoutedEventArgs e) => Open(_paths.WorkDir);
        private void OpenSettingsFile_Click(object sender, RoutedEventArgs e) => Open(_store.Path);
        private void OpenLog_Click(object sender, RoutedEventArgs e) => Open(Log.FilePath);

        // Clipboard

        private static List<string> RunningApps()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                            names.Add(process.ProcessName + ".exe");
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited while listing.
                    }
                }
            }

            return names.ToList();
        }

        private void AddIgnoredApp_Click(object sender, RoutedEventArgs e)
        {
            var name = RunningAppsBox.Text.Trim();
            if (name.Length == 0)
                return;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name += ".exe";
            if (!_ignoredApps.Contains(name, StringComparer.OrdinalIgnoreCase))
                _ignoredApps.Add(name);
            RunningAppsBox.Text = "";
            ScheduleApply();
        }

        private void RemoveIgnoredApp_Click(object sender, RoutedEventArgs e)
        {
            if (IgnoredAppsList.SelectedItem is string app)
            {
                _ignoredApps.Remove(app);
                ScheduleApply();
            }
        }

        // Watched folders

        private WatcherSettings CurrentWatcher => WatchKindBox.SelectedIndex switch
        {
            1 => _settings.Watching.Videos,
            2 => _settings.Watching.Pdfs,
            3 => _settings.Watching.Audio,
            _ => _settings.Watching.Images,
        };

        private void WatchKind_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (WatcherPanel is null)
                return; // still initialising
            ShowWatcher(CurrentWatcher, hasEngine: true);
            e.Handled = true;
        }

        private void ShowWatcher(WatcherSettings watcher, bool hasEngine)
        {
            CommitFocusedTextBox();
            WatcherPanel.DataContext = watcher;
            WatchKindNote.Visibility = hasEngine ? Visibility.Collapsed : Visibility.Visible;
            _folders.Clear();
            foreach (var folder in watcher.Folders)
                _folders.Add(PortablePath.Expand(folder));
            ShowQuietFolder();
        }

        private bool _showingQuiet;

        private void FoldersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowQuietFolder();
            e.Handled = true;
        }

        private void ShowQuietFolder()
        {
            _showingQuiet = true;
            var index = FoldersList.SelectedIndex;
            QuietFolderBox.IsEnabled = index >= 0;
            QuietFolderBox.IsChecked = index >= 0 && index < CurrentWatcher.Folders.Count
                                       && CurrentWatcher.QuietFolders.Contains(CurrentWatcher.Folders[index], StringComparer.OrdinalIgnoreCase);
            _showingQuiet = false;
        }

        private void QuietFolder_Changed(object sender, RoutedEventArgs e)
        {
            if (_showingQuiet || FoldersList.SelectedIndex is not (var index and >= 0) || index >= CurrentWatcher.Folders.Count)
                return;
            var folder = CurrentWatcher.Folders[index];
            CurrentWatcher.QuietFolders.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (QuietFolderBox.IsChecked == true)
                CurrentWatcher.QuietFolders.Add(folder);
            ScheduleApply();
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Choose a folder to watch", Multiselect = true };
            if (dialog.ShowDialog(this) != true)
                return;

            foreach (var folder in dialog.FolderNames)
            {
                if (_folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (IsInside(folder, _paths.WorkDir) || IsInside(_paths.WorkDir, folder))
                {
                    MessageBox.Show(this, "WClop can't watch its own working folder.", "WClop", MessageBoxButton.OK, MessageBoxImage.Information);
                    continue;
                }

                CurrentWatcher.Folders.Add(PortablePath.Contract(folder));
                _folders.Add(folder);
            }

            ScheduleApply();
        }

        private void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (FoldersList.SelectedIndex is var index and >= 0)
            {
                var folder = CurrentWatcher.Folders[index];
                CurrentWatcher.QuietFolders.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
                CurrentWatcher.Folders.RemoveAt(index);
                _folders.RemoveAt(index);
                ScheduleApply();
            }
        }

        private static bool IsInside(string path, string folder) =>
            (Path.GetFullPath(path).TrimEnd('\\') + "\\").StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        // Compression

        private void Factor_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateFactorLabel();

        private void UpdateFactorLabel()
        {
            if (FactorLabel is null)
                return;
            var factor = (int)Math.Round(FactorSlider.Value);
            FactorLabel.Text = factor switch
            {
                30 => "30 · Normal",
                64 => "64 · Aggressive",
                < 30 => $"{factor} · Gentle",
                < 50 => $"{factor}",
                _ => $"{factor} · Aggressive",
            };
        }

        private static readonly string[] DefaultJpegConversions = new CompressionSettings().ConvertToJpeg.ToArray();
        private static readonly string[] DefaultPngConversions = new CompressionSettings().ConvertToPng.ToArray();

        private void Conversions_Click(object sender, RoutedEventArgs e)
        {
            _settings.Compression.ConvertToJpeg = ConvertToJpegBox.IsChecked == true ? [.. DefaultJpegConversions] : [];
            _settings.Compression.ConvertToPng = ConvertToPngBox.IsChecked == true ? [.. DefaultPngConversions] : [];
            ScheduleApply();
        }

        private void AudioFactor_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AudioFactorLabel is null)
                return;
            var factor = (int)Math.Round(AudioFactorSlider.Value);
            AudioFactorLabel.Text = $"{Core.Audio.AudioBitrates.Aac(factor)} kbps AAC · {Core.Audio.AudioBitrates.Mp3(factor)} MP3";
        }

        private void VideoFactor_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VideoFactorLabel is null)
                return;
            var factor = (int)Math.Round(VideoFactorSlider.Value);
            VideoFactorLabel.Text = factor switch
            {
                50 => "50 · Normal",
                < 30 => $"{factor} · Higher quality",
                > 70 => $"{factor} · Smallest",
                _ => $"{factor}",
            };
        }

        private async Task ShowEncoderStatusAsync()
        {
            VideoFactor_Changed(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
            if (Core.Processes.ToolLocator.CreateDefault().Find(Core.Processes.Tool.Ffmpeg) is not { } ffmpeg)
            {
                EncoderStatus.Text = "ffmpeg wasn't found, so videos can't be optimised.";
                return;
            }

            var hardware = await Core.Video.VideoEncoders.DetectHardwareAsync(ffmpeg);
            EncoderStatus.Text = hardware is { } encoder
                ? $"This PC's hardware encoder: {Core.Video.VideoEncoders.DisplayName(encoder)}. \"Smaller\" gives smaller files but takes longer."
                : "No working hardware encoder on this PC, so every option uses software (x264). That works everywhere, just more slowly.";
        }

        // Output

        private void Template_Changed(object sender, RoutedEventArgs e) => UpdateTemplatePreviews();

        private void UpdateTemplatePreviews()
        {
            if (SameFolderPreview is null || SpecificFolderPreview is null)
                return;

            var sample = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots", "Screenshot 2026-09-26 101500.png");
            SameFolderPreview.Text = Preview(SameFolderTemplateBox.Text, sample, sameFolder: true);
            SpecificFolderPreview.Text = Preview(SpecificFolderTemplateBox.Text, sample, sameFolder: false);
        }

        private static string Preview(string template, string sample, bool sameFolder)
        {
            if (string.IsNullOrWhiteSpace(template))
                return "";
            try
            {
                var rendered = NameTemplate.Render(template, sample, DateTime.Now, () => 1);
                if (sameFolder)
                    rendered = Path.Combine(Path.GetDirectoryName(sample)!, Path.GetFileName(rendered));
                return "e.g. " + rendered + ".png";
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "That isn't a valid name or path";
            }
        }

        // Drop zone

        private void PositionDropZone_Click(object sender, RoutedEventArgs e)
        {
            ApplyNow();
            _positionDropZone();
        }

        /// <summary>After the tab has been dragged to a new place, show where it is now.</summary>
        internal void RefreshDropZonePosition()
        {
            DropEdgeBox.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.UpdateTarget();
            foreach (var slider in FindSliders(this))
                slider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateTarget();
            _appliedSnapshot = SettingsStore.Snapshot(_settings);
        }

        private static IEnumerable<Slider> FindSliders(DependencyObject root)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Slider slider)
                    yield return slider;
                foreach (var nested in FindSliders(child))
                    yield return nested;
            }
        }

        // Hotkeys

        private void Modifier_Click(object sender, RoutedEventArgs e)
        {
            var chosen = new[] { (ModCtrl, "Ctrl"), (ModAlt, "Alt"), (ModShift, "Shift"), (ModWin, "Win") }
                .Where(m => m.Item1.IsChecked == true)
                .Select(m => m.Item2)
                .ToList();
            if (chosen.Count >= 2)
            {
                _settings.Hotkeys.Modifiers = string.Join("+", chosen);
                ScheduleApply();
            }

            UpdateModifierHint(chosen.Count);
        }

        private void UpdateModifierHint(int? count = null)
        {
            ModifierHint.Text = count is < 2
                ? "Pick at least two modifier keys, so hotkeys don't get in the way of normal shortcuts."
                : $"Hotkeys are {HotkeyManager.ModifierNames(_settings.Hotkeys.Modifiers).Aggregate((a, b) => a + "+" + b)} plus a key." +
                  (ModCtrl.IsChecked == true && ModAlt.IsChecked == true
                      ? " On keyboards with an AltGr key, Ctrl+Alt combinations can clash with typing some characters."
                      : "");
        }

        private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
        {
            _settings.Hotkeys.Keys.Clear();
            _settings.Hotkeys.DisabledActions.Clear();
            foreach (var row in _hotkeyRows)
                row.Reload();
            ScheduleApply();
        }

        private void OnHotkeysChanged() => Dispatcher.BeginInvoke(RefreshHotkeyStatus);

        private void RefreshHotkeyStatus()
        {
            foreach (var row in _hotkeyRows)
                row.Status = _hotkeys.StatusOf(row.Action);
        }

        private static void Open(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Log.Warn($"Couldn't open {path}: {e.Message}");
            }
        }
    }
}
