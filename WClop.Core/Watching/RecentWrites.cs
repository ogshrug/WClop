using System.Collections.Concurrent;

namespace WClop.Core.Watching;

/// <summary>
/// Paths WClop has just written, ignored by the folder watchers for a short window (project.md §4.8, §5:
/// the protection window). Windows has no FSEvents-style "ignore my own writes" flag, so this does that job.
/// Paths are registered <em>before</em> writing, because the change event can arrive before the write call returns.
/// </summary>
public sealed class RecentWrites(TimeSpan window)
{
    private readonly ConcurrentDictionary<string, DateTime> _until = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Window { get; set; } = window;

    public void Register(string path) => _until[Path.GetFullPath(path)] = DateTime.UtcNow + Window;

    public bool IsProtected(string path)
    {
        var key = Path.GetFullPath(path);
        if (!_until.TryGetValue(key, out var until))
            return false;
        if (DateTime.UtcNow < until)
            return true;
        _until.TryRemove(key, out _);
        return false;
    }
}
