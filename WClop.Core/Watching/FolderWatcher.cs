using WClop.Core.Images;
using WClop.Core.Logging;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Core.Watching;

/// <summary>What a folder watcher looks for and how it processes a file.</summary>
public sealed record WatchKind
{
    /// <summary>"image", "video", "pdf", "audio": used in notices and ignore-file names.</summary>
    public required string Name { get; init; }

    /// <summary>Formats this watcher picks up (only those its engine can handle).</summary>
    public required IReadOnlySet<FileFormat> Formats { get; init; }

    /// <summary>Validity check once the file has settled (can it actually be opened?).</summary>
    public required Func<string, bool> IsValid { get; init; }

    /// <summary>Pixel size for the resolution limits, or null to skip that check.</summary>
    public Func<string, ImageSize?>? ReadSize { get; init; }

    /// <summary>Optimises the file; the callback reports progress 0–1 (videos).</summary>
    public required Func<string, Action<double>, CancellationToken, Task<FileOptimisationResult>> Process { get; init; }

    /// <summary>How long a file may take to finish being written (screen recordings can take a while).</summary>
    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Clipboard content seen recently: a file with the same content (a screenshot that was also copied) is still
    /// optimised, but without a second result card.
    /// </summary>
    public RecentCopies? Copies { get; init; }
}

/// <summary>
/// Watches folders for new files of one media type and optimises them (project.md §4, §26.1).
/// <list type="bullet">
/// <item>Only created and renamed files count, so sync tools touching old files don't trigger work.</item>
/// <item>Only the first event per path within 1 s starts a job (a save produces several events).</item>
/// <item>More than <see cref="WatcherSettings.MaxFilesPerBurst"/> new files within 1 s is treated as a bulk
/// operation (unzip, copy): the jobs are cancelled and a notice raised.</item>
/// <item>In the first 30 s after WClop's very first launch, events are held 3 s, and more than 5 events means
/// another app is constantly rewriting the folder: the watcher disables itself and says why.</item>
/// <item>A missing folder (unplugged drive) never stops the others and is retried periodically.</item>
/// <item>On buffer overflow, the folder is rescanned for recently modified files.</item>
/// </list>
/// All disk access happens on thread-pool threads.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly TimeSpan JustAddedWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FirstLaunchGuardDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirstLaunchHold = TimeSpan.FromSeconds(3);
    private const int FirstLaunchMaxEvents = 5;
    private static readonly TimeSpan OverflowRescanAge = TimeSpan.FromSeconds(30);

    private readonly WatchKind _kind;
    private readonly WatcherSettings _settings;
    private readonly WatchingSettings _watching;
    private readonly AppPaths _paths;
    private readonly OptimisationManager _manager;
    private readonly OptimisationMarkers _markers;
    private readonly RecentWrites _recentWrites;
    private readonly DateTime? _firstLaunchUtc;

    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _justAdded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BurstEntry> _burst = [];
    private DateTime _ignoreUntil;
    private int _firstLaunchEvents;
    private bool _stormDetected;
    private int _generation;
    private Timer? _missingRetry;

    public FolderWatcher(
        WatchKind kind,
        WatcherSettings settings,
        WatchingSettings watching,
        AppPaths paths,
        OptimisationManager manager,
        OptimisationMarkers markers,
        RecentWrites recentWrites,
        DateTime? firstLaunchUtc = null)
    {
        _kind = kind;
        _settings = settings;
        _watching = watching;
        _paths = paths;
        _manager = manager;
        _markers = markers;
        _recentWrites = recentWrites;
        _firstLaunchUtc = firstLaunchUtc;
    }

    /// <summary>Something the user should know (e.g. a burst was ignored). Raised on a thread-pool thread.</summary>
    public event Action<string>? Notice;

    /// <summary>
    /// The first-launch guard fired; the watcher has disabled itself (<see cref="WatcherSettings.Enabled"/> is now false)
    /// and the caller should save settings and explain. Raised on a thread-pool thread.
    /// </summary>
    public event Action<WatchKind>? StormDetected;

    public WatchKind Kind => _kind;

    /// <summary>How often missing folders are checked again.</summary>
    public TimeSpan MissingFolderRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public IReadOnlyCollection<string> ActiveFolders
    {
        get
        {
            lock (_gate)
                return _watchers.Keys.ToList();
        }
    }

    /// <summary>Starts (or restarts, after a settings change) watching. Folder checks happen off the calling thread.</summary>
    public Task StartAsync()
    {
        Stop();
        if (!_settings.Enabled)
            return Task.CompletedTask;

        int generation;
        lock (_gate)
            generation = ++_generation;

        var folders = _settings.Folders.Select(PortablePath.Expand).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Task.Run(() =>
        {
            // Directory.Exists can block for a network timeout on a stale share, so never on the UI thread.
            foreach (var folder in folders)
            {
                lock (_gate)
                {
                    if (generation != _generation)
                        return; // superseded by a newer start
                }

                TryWatch(folder);
            }

            lock (_gate)
            {
                if (generation == _generation && _missing.Count > 0)
                    _missingRetry = new Timer(_ => RetryMissing(generation), null, MissingFolderRetryInterval, MissingFolderRetryInterval);
            }
        });
    }

    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            _missingRetry?.Dispose();
            _missingRetry = null;
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
            _missing.Clear();
        }
    }

    public void Dispose() => Stop();

    private void TryWatch(string folder)
    {
        if (!Directory.Exists(folder))
        {
            lock (_gate)
                _missing.Add(folder);
            Log.Info($"Watcher ({_kind.Name}): {folder} doesn't exist (yet); will retry");
            return;
        }

        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => OnFileEvent(e.FullPath, folder);
        watcher.Renamed += (_, e) => OnFileEvent(e.FullPath, folder);
        watcher.Error += (_, e) => OnWatcherError(folder, e.GetException());

        lock (_gate)
        {
            _watchers[folder] = watcher;
            _missing.Remove(folder);
        }

        watcher.EnableRaisingEvents = true;
        Log.Info($"Watcher ({_kind.Name}): watching {folder}");
    }

    private void RetryMissing(int generation)
    {
        List<string> missing;
        lock (_gate)
        {
            if (generation != _generation)
                return;
            missing = _missing.ToList();
        }

        foreach (var folder in missing.Where(Directory.Exists))
            TryWatch(folder);
    }

    private void OnWatcherError(string folder, Exception error)
    {
        if (error is InternalBufferOverflowException)
        {
            // Too many changes at once; events were lost. Pick up whatever changed recently.
            Log.Warn($"Watcher ({_kind.Name}): event buffer overflowed in {folder}; rescanning");
            Task.Run(() => Rescan(folder));
            return;
        }

        // The folder went away (drive unplugged, share dropped): stop this one and retry it later.
        Log.Warn($"Watcher ({_kind.Name}): lost {folder}: {error.Message}");
        lock (_gate)
        {
            if (_watchers.Remove(folder, out var watcher))
                watcher.Dispose();
            _missing.Add(folder);
            _missingRetry ??= new Timer(_ => RetryMissing(_generation), null, MissingFolderRetryInterval, MissingFolderRetryInterval);
        }
    }

    private void Rescan(string folder)
    {
        try
        {
            var cutoff = DateTime.UtcNow - OverflowRescanAge;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                if (file.LastWriteTimeUtc >= cutoff)
                    OnFileEvent(file.FullName, folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Watcher ({_kind.Name}): rescan of {folder} failed: {e.Message}");
        }
    }

    /// <summary>Entry point for every created / renamed file. Internal so tests can drive it directly.</summary>
    internal void OnFileEvent(string path, string root)
    {
        if (_watching.Paused || !_settings.Enabled)
            return;

        // Name-only checks first: no disk access.
        if (!WatchFilters.IsCandidateName(path, _kind.Formats, _settings.SkipFormats))
            return;
        if (WatchFilters.IsInside(path, _paths.WorkDir))
            return;
        if (_recentWrites.IsProtected(path))
            return;

        var now = DateTime.UtcNow;
        var inFirstLaunchGuard = _firstLaunchUtc is { } first && now - first < FirstLaunchGuardDuration;
        BurstEntry entry;
        string? notice = null;
        var storm = false;

        lock (_gate)
        {
            foreach (var stale in _justAdded.Where(p => now - p.Value > JustAddedWindow).Select(p => p.Key).ToList())
                _justAdded.Remove(stale);
            if (!_justAdded.TryAdd(path, now))
                return; // a follow-up event for the same save

            if (_stormDetected)
                return;

            if (now < _ignoreUntil)
            {
                _ignoreUntil = now + BurstWindow; // keep ignoring while the burst continues
                return;
            }

            _burst.RemoveAll(e => now - e.Time > BurstWindow);
            entry = new BurstEntry(path, now);
            _burst.Add(entry);

            if (_burst.Count > Math.Max(1, _settings.MaxFilesPerBurst))
            {
                foreach (var burstEntry in _burst)
                    burstEntry.Cancel();
                _burst.Clear();
                _ignoreUntil = now + BurstWindow;
                notice = $"More than {_settings.MaxFilesPerBurst} {_kind.Name}s appeared in {Path.GetFileName(root.TrimEnd('\\'))} at once; ignoring them";
            }
            else if (inFirstLaunchGuard && ++_firstLaunchEvents > FirstLaunchMaxEvents)
            {
                _stormDetected = true;
                storm = true;
            }
        }

        if (notice is not null)
        {
            Log.Info($"Watcher ({_kind.Name}): {notice}");
            Notice?.Invoke(notice);
            return;
        }

        if (storm)
        {
            OnStorm();
            return;
        }

        // Cheap disk checks before a job exists, so files that will be skipped never show a card.
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || WatchFilters.HasSkippableAttributes(info.Attributes))
                return;
            if (_markers.Get(path) != MarkerStatus.None)
                return;
            if (IgnoreRules.Load(root, _kind.Name).IsIgnored(Path.GetRelativePath(root, path)))
                return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var job = _manager.Start(
            path, JobSource.File, Path.GetFileName(path), path,
            (job, cancellationToken) => ProcessAsync(job, path, inFirstLaunchGuard, cancellationToken),
            status: "Waiting for the file to be written",
            visible: false);
        entry.Attach(job);
    }

    /// <summary>A folder set to optimise without showing results.</summary>
    private bool IsQuiet(string path) =>
        _settings.QuietFolders.Any(folder => WatchFilters.IsInside(path, PortablePath.Expand(folder)));

    /// <summary>The Snipping Tool saves the file a moment before it updates the clipboard.</summary>
    private static readonly TimeSpan CopyMatchWait = TimeSpan.FromSeconds(1);

    private async Task<FileOptimisationResult> ProcessAsync(
        OptimisationJob job, string path, bool holdForFirstLaunch, CancellationToken cancellationToken)
    {
        if (holdForFirstLaunch)
        {
            job.Status = "Initialising file watcher";
            await Task.Delay(FirstLaunchHold, cancellationToken).ConfigureAwait(false);
            if (_stormDetected)
                throw new OperationCanceledException();
        }

        if (!await FileSettler.WaitAsync(path, _kind.IsValid, _kind.SettleTimeout, cancellationToken).ConfigureAwait(false))
            throw new JobSkippedException("The file never finished writing, or isn't a valid file");

        var size = new FileInfo(path).Length;
        if (!WatchFilters.IsWithinSizeLimits(size, _settings))
            throw new JobSkippedException("Outside the size limits");

        if (_kind.ReadSize?.Invoke(path) is { } pixels && !WatchFilters.IsWithinResolutionLimits(pixels.Width, pixels.Height, _settings))
            throw new JobSkippedException("Outside the resolution limits");

        // Another job may have handled it while this one waited.
        if (_markers.Get(path) != MarkerStatus.None)
            throw new JobSkippedException("Already optimised");

        job.Status = "Optimising";
        if (_kind.Copies is not { } copies)
        {
            job.IsVisible = !IsQuiet(path);
            return await _kind.Process(path, p => job.Progress = p, cancellationToken).ConfigureAwait(false);
        }

        // Hash before optimising (it may replace the file), then decide on the card while the work runs.
        var hash = await ContentHash.OfFileAsync(path, cancellationToken).ConfigureAwait(false);
        var work = _kind.Process(path, p => job.Progress = p, cancellationToken);
        var copied = false;
        try
        {
            copied = await copies.WaitForAsync(hash, CopyMatchWait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (copied)
            Log.Info($"Watcher ({_kind.Name}): {Path.GetFileName(path)} was also copied; only the clipboard result is shown");
        else
            job.IsVisible = !IsQuiet(path);
        return await work.ConfigureAwait(false);
    }

    private void OnStorm()
    {
        _settings.Enabled = false;
        Stop();
        lock (_gate)
        {
            foreach (var entry in _burst)
                entry.Cancel();
            _burst.Clear();
        }

        Log.Warn($"Watcher ({_kind.Name}): more than {FirstLaunchMaxEvents} files changed in the first seconds; " +
                 "another app seems to be rewriting the folder constantly. Watcher disabled.");
        StormDetected?.Invoke(_kind);
    }

    /// <summary>A file seen in the current burst window, and the job started for it (if any yet).</summary>
    private sealed class BurstEntry(string path, DateTime time)
    {
        private readonly object _gate = new();
        private OptimisationJob? _job;
        private bool _cancelled;

        public string Path { get; } = path;
        public DateTime Time { get; } = time;

        public void Attach(OptimisationJob job)
        {
            lock (_gate)
            {
                _job = job;
                if (_cancelled)
                    job.Cancel(); // the burst was detected before this job existed
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _cancelled = true;
                _job?.Cancel();
            }
        }
    }
}
