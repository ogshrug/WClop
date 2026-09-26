namespace WClop.Core.Watching;

/// <summary>
/// Content recently copied to the clipboard, by hash. The Snipping Tool both copies a screenshot and saves it to
/// Pictures\Screenshots, so the clipboard and the folder watcher each get the same image; the watcher still
/// optimises the file but checks here so only one result card appears.
/// </summary>
public sealed class RecentCopies(TimeSpan window, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private readonly List<(string Hash, DateTime At)> _items = [];

    public void Add(string hash)
    {
        lock (_gate)
        {
            Prune();
            _items.Add((hash, _now()));
        }
    }

    public bool Contains(string hash)
    {
        lock (_gate)
        {
            Prune();
            return _items.Exists(i => i.Hash == hash);
        }
    }

    /// <summary>
    /// Whether <paramref name="hash"/> was copied, waiting up to <paramref name="wait"/> for it: the screenshot file
    /// can land a moment before the clipboard update.
    /// </summary>
    public async Task<bool> WaitForAsync(string hash, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            if (Contains(hash))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Prune()
    {
        var cutoff = _now() - window;
        _items.RemoveAll(i => i.At < cutoff);
    }
}
