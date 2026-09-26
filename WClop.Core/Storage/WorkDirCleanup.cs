using WClop.Core.Logging;
using WClop.Core.Settings;

namespace WClop.Core.Storage;

/// <summary>
/// Deletes old files from the working folders (project.md §14.4): checks every 10 minutes, removes files that
/// arrived longer ago than the configured interval, and never touches batch backups.
/// A file's age counts from when it arrived in the working folder (the later of creation and modification),
/// because backups keep the original's modification date.
/// </summary>
public sealed class WorkDirCleanup(AppPaths paths, FileSettings settings) : IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);
    private Timer? _timer;

    public static TimeSpan? MaxAge(CleanupInterval interval) => interval switch
    {
        CleanupInterval.Every10Minutes => TimeSpan.FromMinutes(10),
        CleanupInterval.Hourly => TimeSpan.FromHours(1),
        CleanupInterval.Every12Hours => TimeSpan.FromHours(12),
        CleanupInterval.Daily => TimeSpan.FromDays(1),
        CleanupInterval.Every3Days => TimeSpan.FromDays(3),
        _ => null,
    };

    public void Start()
    {
        _timer?.Dispose();
        _timer = new Timer(_ => RunOnce(DateTime.UtcNow), null, TimeSpan.FromMinutes(1), CheckEvery);
    }

    /// <summary>Returns how many files were deleted.</summary>
    public int RunOnce(DateTime nowUtc)
    {
        if (MaxAge(settings.CleanupInterval) is not { } maxAge)
            return 0;

        var cutoff = nowUtc - maxAge;
        var deleted = 0;
        foreach (var folder in paths.CleanableFolders.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    var arrived = file.CreationTimeUtc > file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;
                    if (arrived >= cutoff)
                        continue;
                    try
                    {
                        file.Delete();
                        deleted++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // In use (e.g. dragged into an app right now); try again next time.
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Cleanup of {folder} failed: {e.Message}");
            }
        }

        if (deleted > 0)
            Log.Info($"Cleanup: deleted {deleted} old working file(s)");
        return deleted;
    }

    public void Dispose() => _timer?.Dispose();
}
