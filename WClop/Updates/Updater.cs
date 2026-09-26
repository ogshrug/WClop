using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;
using WClop.Core;
using WClop.Core.Logging;
using WClop.Core.Optimisation;
using WClop.Core.Settings;
using WClop.Core.Updates;
using WClop.Integration;
using WClop.Settings;

namespace WClop.Updates
{
    /// <summary>
    /// Finds and installs new versions from the GitHub releases. Checks at startup and daily (unless turned off);
    /// installing downloads the MSI, verifies it, and runs it: the installer quits this copy, upgrades it and starts
    /// the new one. Only a copy installed by the MSI updates itself; a dev build can check but not install.
    /// </summary>
    internal sealed class Updater : IDisposable
    {
        private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
        private static readonly TimeSpan TimerPeriod = TimeSpan.FromHours(1);

        private readonly AppSettings _settings;
        private readonly Action _saveSettings;
        private readonly AppPaths _paths;
        private readonly OptimisationManager _manager;
        private readonly UpdateChecker _checker = new();
        private readonly Timer _timer;
        private readonly SemaphoreSlim _busy = new(1, 1);

        public Updater(AppSettings settings, Action saveSettings, AppPaths paths, OptimisationManager manager)
        {
            _settings = settings;
            _saveSettings = saveSettings;
            _paths = paths;
            _manager = manager;
            _timer = new Timer(_ => _ = OnTimerAsync());
        }

        /// <summary>Raised (on any thread) whenever <see cref="Status"/>, <see cref="Available"/> or <see cref="Progress"/> change.</summary>
        public event Action? Changed;

        /// <summary>Raised (on any thread) when a check finds a new version.</summary>
        public event Action<AvailableUpdate>? Found;

        public AvailableUpdate? Available { get; private set; }
        public string Status { get; private set; } = "";
        public double? Progress { get; private set; }
        public bool IsBusy => _busy.CurrentCount == 0;

        /// <summary>
        /// True when this is the copy the installer put in place (its folder is the one it recorded). A dev build or a
        /// copied folder can't update itself: installing the MSI would put a second copy elsewhere.
        /// </summary>
        public static bool IsInstalledCopy
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\WClop");
                return key?.GetValue("InstallDir") is string dir
                       && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),
                           StringComparison.OrdinalIgnoreCase);
            }
        }

        public void Start() => _timer.Change(FirstCheckDelay, TimerPeriod);

        public void Dispose() => _timer.Dispose();

        private async Task OnTimerAsync()
        {
            var updates = _settings.Updates;
            if (!updates.CheckAutomatically || IsBusy)
                return;
            if (updates.LastCheckedUtc is { } last && DateTime.UtcNow - last < CheckInterval && Available is null)
                return;

            await CheckAsync(manual: false);
            if (Available is not null && updates.InstallAutomatically && IsInstalledCopy)
            {
                // Don't pull the rug out from under running jobs; the next hourly tick tries again.
                if (_manager.HasRunningJobs)
                    Log.Info("Updates: waiting for running jobs before installing");
                else
                    await InstallAsync();
            }
        }

        /// <summary>Looks for a new version. Failures are reported in <see cref="Status"/>, never thrown.</summary>
        public async Task CheckAsync(bool manual)
        {
            if (!await _busy.WaitAsync(0))
                return;
            try
            {
                Report("Checking for updates…");
                var update = await _checker.CheckAsync(AppPaths.Version);
                _settings.Updates.LastCheckedUtc = DateTime.UtcNow;
                _saveSettings();

                var isNew = update is not null && update.Version != Available?.Version;
                Available = update;
                if (update is null)
                {
                    Report($"WClop {AppPaths.Version} is up to date.");
                }
                else
                {
                    Log.Info($"Updates: {update.Version} is available");
                    Report(IsInstalledCopy
                        ? $"WClop {update.Version} is available (you have {AppPaths.Version})."
                        : $"WClop {update.Version} is available. This copy wasn't installed with the installer, so download it from the release page.");
                    if (isNew && !manual)
                        Found?.Invoke(update);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                          or InvalidOperationException or KeyNotFoundException)
            {
                Log.Warn($"Updates: check failed: {e.Message}");
                Report(manual ? $"Couldn't check for updates: {e.Message}" : "");
            }
            finally
            {
                _busy.Release();
            }
        }

        /// <summary>Downloads, verifies and runs the installer for <see cref="Available"/>. WClop is closed by the installer.</summary>
        public async Task InstallAsync()
        {
            if (Available is not { } update || !IsInstalledCopy || !await _busy.WaitAsync(0))
                return;
            try
            {
                Report($"Downloading WClop {update.Version}…", 0);
                var msi = await _checker.DownloadAsync(update, _paths.Downloads, p => Report($"Downloading WClop {update.Version}…", p));

                Report($"Installing WClop {update.Version}; WClop will restart…");
                Log.Info($"Updates: installing {update.Version} from {msi}");
                // Keep the user's current choices rather than the installer's defaults.
                var start = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false };
                foreach (var argument in new[]
                         {
                             "/i", msi, "/passive", "/norestart",
                             "LAUNCHAPP=1",
                             "LAUNCHATLOGIN=" + (LaunchAtLogin.IsEnabled ? "1" : "0"),
                             "EXPLORERMENU=" + (ShellIntegration.IsContextMenuEnabled ? "1" : "0"),
                             "/l*v", Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "update-install.log"),
                         })
                {
                    start.ArgumentList.Add(argument);
                }

                Process.Start(start)?.Dispose();
                // From here the installer asks this copy to quit (--quit over the local API) and starts the new one.
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                          or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Log.Warn($"Updates: install failed: {e.Message}");
                Report($"Couldn't install the update: {e.Message}");
            }
            finally
            {
                _busy.Release();
            }
        }

        private void Report(string status, double? progress = null)
        {
            Status = status;
            Progress = progress;
            Changed?.Invoke();
        }
    }
}
