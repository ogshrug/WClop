using System.IO;
using WClop.Clipboard;
using WClop.Core;
using WClop.Core.Logging;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Watching;
using WClop.DropZone;
using WClop.Hotkeys;
using WClop.Integration;
using WClop.Results;
using WClop.Settings;
using WClop.Watching;

namespace WClop
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Local\WClop.SingleInstance";

        [STAThread]
        public static int Main(string[] args)
        {
            var launch = LaunchArgs.Parse(args);
            if (launch.Unregister || launch.Register)
            {
                SetShellIntegration(launch.Register);
                return 0;
            }

            using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (launch.Quit)
            {
                // Only ever stops a running instance; never starts one.
                if (!createdNew)
                {
                    mutex.Dispose();
                    return LaunchArgs.QuitRunning() ? 0 : 1;
                }

                return 0;
            }

            if (!createdNew)
            {
                // Already running: pass along what this launch was for (a right-click, Send To, --batch…) and leave.
                if (!launch.IsEmpty && !launch.ForwardAsync().GetAwaiter().GetResult())
                {
                    Log.Warn("Another WClop is running but didn't answer; request dropped");
                    return 1;
                }

                return 0;
            }

            var settingsStore = new SettingsStore(AppPaths.SettingsFile);
            var settings = settingsStore.Load();
            // The folder watchers are extra careful right after the very first launch.
            settings.Watching.FirstLaunchUtc ??= DateTime.UtcNow;
            // Write defaults on first launch so the file exists for the user to inspect.
            settingsStore.Save(settings);

            var paths = new AppPaths(settings.Files.ResolveWorkDir());
            paths.EnsureCreated();
            Log.Info($"WClop {AppPaths.Version} starting");

            var recentWrites = new RecentWrites(TimeSpan.FromMilliseconds(settings.Watching.OptimisedFileProtectionMs));
            var service = new FileOptimisationService(
                settings, paths, ToolLocator.CreateDefault(), new OptimisationDatabase(OptimisationDatabase.DefaultPath),
                recentWrites);

            var app = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
            };

            // A background tool shouldn't vanish over one bad event: log it and keep running.
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log.Error("Unhandled exception on the UI thread", e.Exception);
                e.Handled = true;
            };
            System.Windows.Forms.Application.ThreadException += (_, e) =>
                Log.Error("Unhandled exception on the clipboard thread", e.Exception);
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error("Unobserved background task exception", e.Exception);
                e.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Error("Fatal unhandled exception", e.ExceptionObject as Exception);

            var pipelines = new PipelineLibrary(settings, new PipelineRunner(service, settings));
            var manager = new OptimisationManager();
            // A screenshot is both copied and saved to a watched folder; this keeps it to one result card.
            var recentCopies = new RecentCopies(TimeSpan.FromSeconds(30));
            using var clipboard = new ClipboardWatcher(settings, service, manager, recentCopies) { Pipelines = pipelines };
            clipboard.Start();

            using var watchers = new WatcherHost(settings, settingsStore, paths, manager, service, recentWrites, recentCopies, pipelines, app.Dispatcher);
            watchers.Start();

            using var cleanup = new WorkDirCleanup(paths, settings.Files);
            cleanup.Start();

            var actions = new ResultActions(service, clipboard, manager, pipelines);
            var results = new ResultsWindow(manager, actions, settings);
            using var tray = new TrayIcon(app, paths, settings, settingsStore, clipboard, results, watchers);

            using var mouseHook = new MouseHook();
            mouseHook.Start();
            var dropHandler = new DropHandler(service, manager, settings, pipelines) { CopyToClipboard = clipboard.PutForPipelineAsync };
            void OpenBatch(IReadOnlyList<string> paths, Core.DropZone.DropPreset preset, bool keepOriginals) =>
                app.Dispatcher.BeginInvoke(() => new WClop.Batch.BatchWindow(service, paths, preset, keepOriginals).Show());
            dropHandler.BatchRequested += OpenBatch;
            dropHandler.Notice += message => app.Dispatcher.BeginInvoke(() => tray.ShowNotice(message));
            var dropZone = new DropZoneWindow(settings, dropHandler, mouseHook);

            var hotkeyActions = new HotkeyActions(results, actions, clipboard, tray);
            using var hotkeys = new HotkeyManager(settings.Hotkeys, hotkeyActions.Handle);
            tray.ShowHotkeys(hotkeys);

            // The settings window edits the live settings; this applies them everywhere.
            void ApplySettings()
            {
                try
                {
                    settingsStore.Save(settings);
                }
                catch (IOException e)
                {
                    Log.Error("Couldn't save settings", e);
                }

                recentWrites.Window = TimeSpan.FromMilliseconds(settings.Watching.OptimisedFileProtectionMs);
                watchers.Restart();
                hotkeys.Register();
                results.ApplySettings();
                dropZone.ApplySettings();
            }

            dropZone.PositionChanged += ApplySettings;

            // New versions from GitHub (installed copies install them; dev builds only check).
            using var updater = new Updates.Updater(settings, () => settingsStore.Save(settings), paths, manager);
            updater.Found += update => app.Dispatcher.BeginInvoke(() => tray.ShowUpdate(update.Version, announce: true));
            updater.Changed += () => app.Dispatcher.BeginInvoke(() =>
                tray.ShowUpdate(updater.Available is { } u && Updates.Updater.IsInstalledCopy ? u.Version : null, announce: false));
            tray.UpdateRequested += () => _ = updater.InstallAsync();
            updater.Start();

            SettingsWindow? settingsWindow = null;
            dropZone.PositionChanged += () => settingsWindow?.RefreshDropZonePosition();
            tray.BatchRequested += () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to optimise", Multiselect = true };
                if (dialog.ShowDialog() == true)
                    OpenBatch(dialog.FolderNames, Core.DropZone.DropPresets.All[0], keepOriginals: false);
            };

            tray.SettingsRequested += () =>
            {
                if (settingsWindow is not null)
                {
                    settingsWindow.Activate();
                    return;
                }

                settingsWindow = new SettingsWindow(settings, paths, settingsStore, hotkeys, ApplySettings, dropZone.StartPositioning);
                settingsWindow.TryPipeline = actions.RunPipelineOnFile;
                settingsWindow.Updater = updater;
                settingsWindow.Closed += (_, _) => settingsWindow = null;
                settingsWindow.Show();
                settingsWindow.Activate();
            };

            using var ipc = new IpcHost(service, manager, settings, pipelines, clipboard, OpenBatch, ApplySettings, tray.RequestSettings);
            ipc.Start();
            Task.Run(ShellIntegration.Refresh);
            Task.Run(() => CheckBundledTools(message => app.Dispatcher.BeginInvoke(() => tray.ShowNotice(message))));

            // This launch's own request (the first right-click or Send To also starts the app).
            if (launch.Files.Count > 0)
                ipc.Gather(launch.Files);
            if (launch.Batch.Count > 0)
                OpenBatch(launch.Batch, Core.DropZone.DropPresets.All[0], keepOriginals: false);
            if (launch.Settings)
                app.Dispatcher.BeginInvoke(() => tray.RequestSettings());

            var exitCode = app.Run();
            Log.Info("WClop exiting");
            return exitCode;
        }

        /// <summary>The installed tools must match the manifest written when WClop was packaged.</summary>
        private static void CheckBundledTools(Action<string> notify)
        {
            try
            {
                var problems = Core.Processes.ToolManifest.Verify(Path.Combine(AppContext.BaseDirectory, "tools"));
                if (problems is null)
                    return; // a dev build: nothing to check against
                if (problems.Count == 0)
                {
                    Log.Info("Bundled tools verified");
                    return;
                }

                Log.Warn("Bundled tools don't match the manifest: " + string.Join("; ", problems));
                notify($"Some of WClop's tools are damaged or were changed ({problems.Count}). Reinstall WClop to fix this.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Log.Error("Couldn't check the bundled tools", e);
            }
        }

        /// <summary>
        /// Installer hooks: <c>--register</c> adds the Explorer menu and Send To; <c>--unregister</c> (uninstall) takes out
        /// everything WClop added outside its folder, including start at sign-in.
        /// </summary>
        private static void SetShellIntegration(bool enabled)
        {
            try
            {
                if (enabled || ShellIntegration.IsContextMenuEnabled)
                    ShellIntegration.SetContextMenuEnabled(enabled);
                ShellIntegration.SetSendToEnabled(enabled);
                if (!enabled)
                    Settings.LaunchAtLogin.SetEnabled(false);
            }
            catch (Exception e)
            {
                Log.Error(enabled ? "Registering failed" : "Unregistering failed", e);
            }
        }
    }
}
