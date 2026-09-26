using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WClop.Clipboard;
using WClop.Core.Hotkeys;
using WClop.Hotkeys;
using WClop.Core;
using WClop.Core.Logging;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Results;
using WClop.Watching;

namespace WClop
{
    /// <summary>Clop's pause key cycle (project.md §17): Running → Paused (skip the next copy) → Stopped → Running.</summary>
    internal enum AutomationState
    {
        Running,
        PausedForNextCopy,
        Stopped,
    }

    /// <summary>The notification-area icon and its menu (the Windows version of Clop's menu bar icon).</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly System.Windows.Application _app;
        private readonly AppSettings _settings;
        private readonly SettingsStore _settingsStore;
        private readonly ClipboardWatcher _clipboard;
        private readonly ToolStripMenuItem _hotkeysMenu = new("Hotkeys");
        private readonly NotifyIcon _notifyIcon;
        private readonly Dictionary<AutomationState, Icon> _icons;
        private readonly Dictionary<AutomationState, ToolStripMenuItem> _stateItems = new();

        public TrayIcon(
            System.Windows.Application app,
            AppPaths paths,
            AppSettings settings,
            SettingsStore settingsStore,
            ClipboardWatcher clipboard,
            ResultsWindow results,
            WatcherHost watchers)
        {
            _app = app;
            _settings = settings;
            _settingsStore = settingsStore;
            _clipboard = clipboard;

            _icons = new()
            {
                [AutomationState.Running] = DrawIcon(Color.FromArgb(25, 91, 188)),
                [AutomationState.PausedForNextCopy] = DrawIcon(Color.FromArgb(0xE3, 0xA2, 0x1A)),
                [AutomationState.Stopped] = DrawIcon(Color.FromArgb(0x8A, 0x8A, 0x8A)),
            };

            AddStateItem(AutomationState.Running, "Running");
            AddStateItem(AutomationState.PausedForNextCopy, "Skip the next copy");
            AddStateItem(AutomationState.Stopped, "Stopped (all automation off)");

            var clipboardItem = new ToolStripMenuItem("Optimise copied images")
            {
                Checked = settings.Clipboard.Enabled,
                CheckOnClick = true,
            };
            clipboardItem.CheckedChanged += (_, _) => UpdateSettings(s => s.Clipboard.Enabled = clipboardItem.Checked);

            var logFormatsItem = new ToolStripMenuItem("Log clipboard formats")
            {
                Checked = settings.Clipboard.LogFormats,
                CheckOnClick = true,
                ToolTipText = "Record the formats of every copy in the log, to find apps whose copies must be left alone",
            };
            logFormatsItem.CheckedChanged += (_, _) => UpdateSettings(s => s.Clipboard.LogFormats = logFormatsItem.Checked);

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("WClop") { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke())
            {
                Font = new Font(menu.Font, FontStyle.Bold),
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.AddRange(_stateItems.Values.ToArray<ToolStripItem>());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(clipboardItem);
            var imagesFolderItem = new ToolStripMenuItem("Optimise new images in watched folders")
            {
                Checked = settings.Watching.Images.Enabled,
                CheckOnClick = true,
                ToolTipText = string.Join("\n", settings.Watching.Images.Folders.Select(PortablePath.Expand)),
            };
            imagesFolderItem.CheckedChanged += (_, _) =>
            {
                UpdateSettings(s => s.Watching.Images.Enabled = imagesFolderItem.Checked);
                watchers.Restart();
            };
            menu.Items.Add(imagesFolderItem);

            // Settings can change behind the menu's back (the first-launch guard turns watching off).
            menu.Opening += (_, _) =>
            {
                clipboardItem.Checked = settings.Clipboard.Enabled;
                imagesFolderItem.Checked = settings.Watching.Images.Enabled;
                Refresh();
            };
            menu.Items.Add("Clear results", null, (_, _) => results.ClearAll());
            menu.Items.Add("Batch optimise a folder…", null, (_, _) => BatchRequested?.Invoke());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open working folder", null, (_, _) => Open(paths.WorkDir));
            menu.Items.Add("Open settings file", null, (_, _) => Open(settingsStore.Path));
            menu.Items.Add("Open log", null, (_, _) => Open(Log.FilePath));
            menu.Items.Add(logFormatsItem);
            menu.Items.Add(_hotkeysMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => _app.Shutdown());

            _notifyIcon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    SettingsRequested?.Invoke();
            };
            _clipboard.PauseConsumed += () => _app.Dispatcher.BeginInvoke(Refresh);
            _clipboard.Notice += message => _app.Dispatcher.BeginInvoke(() =>
                _notifyIcon.ShowBalloonTip(3000, "WClop", message, ToolTipIcon.Info));
            watchers.Notice += message => _app.Dispatcher.BeginInvoke(() =>
                _notifyIcon.ShowBalloonTip(4000, "WClop", message, ToolTipIcon.Info));
            Refresh();

            var missing = ToolLocator.CreateDefault().Missing();
            if (missing.Count > 0)
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    "WClop: tools missing",
                    "Not found: " + string.Join(", ", missing.Select(ToolLocator.ExecutableName)),
                    ToolTipIcon.Warning);
            }
        }

        /// <summary>Left-click on the icon, or "Settings…" in the menu.</summary>
        public event Action? SettingsRequested;

        /// <summary>"Batch optimise a folder…" in the menu.</summary>
        public event Action? BatchRequested;

        public void RequestSettings() => SettingsRequested?.Invoke();

        public void ShowNotice(string message) => _notifyIcon.ShowBalloonTip(3000, "WClop", message, ToolTipIcon.Info);

        public AutomationState State =>
            _settings.Watching.Paused ? AutomationState.Stopped
            : _clipboard.IsPausedForNextEvent ? AutomationState.PausedForNextCopy
            : AutomationState.Running;

        public void SetState(AutomationState state)
        {
            switch (state)
            {
                case AutomationState.Running:
                    _clipboard.CancelPause();
                    UpdateSettings(s => s.Watching.Paused = false);
                    break;
                case AutomationState.PausedForNextCopy:
                    _clipboard.PauseNextEvent();
                    UpdateSettings(s => s.Watching.Paused = false);
                    break;
                case AutomationState.Stopped:
                    _clipboard.CancelPause();
                    UpdateSettings(s => s.Watching.Paused = true);
                    break;
            }

            Refresh();
        }

        /// <summary>For the pause hotkey (Phase 5).</summary>
        public void CycleState() => SetState(State switch
        {
            AutomationState.Running => AutomationState.PausedForNextCopy,
            AutomationState.PausedForNextCopy => AutomationState.Stopped,
            _ => AutomationState.Running,
        });

        /// <summary>
        /// Lists the hotkeys in the tray menu and keeps the list current. Warns (once per set of keys)
        /// when another app has taken some.
        /// </summary>
        public void ShowHotkeys(HotkeyManager hotkeys)
        {
            void Rebuild()
            {
                _hotkeysMenu.DropDownItems.Clear();
                if (!_settings.Hotkeys.Enabled)
                {
                    _hotkeysMenu.DropDownItems.Add(new ToolStripMenuItem("Hotkeys are turned off") { Enabled = false });
                    return;
                }

                foreach (var command in HotkeyCatalog.Commands.Where(c => HotkeyCatalog.IsEnabled(c.Action, _settings.Hotkeys)))
                {
                    var key = HotkeyCatalog.DisplayKey(HotkeyCatalog.KeyFor(command, _settings.Hotkeys));
                    var status = hotkeys.StatusOf(command.Action);
                    var working = status == "Active";
                    _hotkeysMenu.DropDownItems.Add(new ToolStripMenuItem(command.Description + (working ? "" : $"   ({status.ToLowerInvariant()})"))
                    {
                        ShortcutKeyDisplayString = $"{hotkeys.ModifierText}+{key}",
                        ShowShortcutKeys = true,
                        Enabled = working,
                    });
                }

                var takenNow = string.Join(", ", hotkeys.Taken.Select(b => $"{hotkeys.ModifierText}+{HotkeyCatalog.DisplayKey(b.Key)}"));
                if (takenNow.Length > 0 && takenNow != _lastTakenWarning)
                {
                    _notifyIcon.ShowBalloonTip(5000, "WClop: some hotkeys unavailable",
                        $"Already used by another app: {takenNow}. You can remap them in Settings.", ToolTipIcon.Warning);
                }

                _lastTakenWarning = takenNow;
            }

            hotkeys.Changed += Rebuild;
            Rebuild();
        }

        private string? _lastTakenWarning;

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            foreach (var icon in _icons.Values)
                icon.Dispose();
        }

        private void AddStateItem(AutomationState state, string text)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (_, _) => SetState(state);
            _stateItems[state] = item;
        }

        private void Refresh()
        {
            var state = State;
            foreach (var (itemState, item) in _stateItems)
                item.Checked = itemState == state;

            _notifyIcon.Icon = _icons[state];
            _notifyIcon.Text = state switch
            {
                AutomationState.PausedForNextCopy => "WClop: skipping the next copy",
                AutomationState.Stopped => "WClop: stopped",
                _ => "WClop",
            };
        }

        private void UpdateSettings(Action<AppSettings> change)
        {
            change(_settings);
            try
            {
                _settingsStore.Save(_settings);
            }
            catch (IOException e)
            {
                Log.Error("Couldn't save settings", e);
            }
        }

        /// <summary>A placeholder icon until there's proper artwork: a coloured disc with a "W".</summary>
        /// <summary>The clipboard with a W (the app icon, from Icons8), the W in the state's colour.</summary>
        private static Icon DrawIcon(Color color)
        {
            using var blankStream = typeof(TrayIcon).Assembly.GetManifestResourceStream("WClop.clipboard-blank.png")!;
            using var blank = new Bitmap(blankStream);
            using var bitmap = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Color.Transparent);
                graphics.DrawImage(blank, 0, 0, 32, 32);
                // Same proportions as scripts/make-icon.ps1 (drawn there on a 256 px grid).
                const float u = 32f / 256;
                using var font = new Font("Segoe UI", 118 * u, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(color);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                graphics.DrawString("W", font, brush, new RectangleF(0, 60 * u, 32, 150 * u), format);
            }

            var handle = bitmap.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(handle).Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static void Open(string path) =>
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
