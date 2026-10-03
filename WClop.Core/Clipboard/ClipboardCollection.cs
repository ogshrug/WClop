using System.Buffers.Binary;
using System.Text;

namespace WClop.Core.Clipboard;

/// <summary>
/// "Collect clipboard results" (Clop 3.0, project.md §3.5 accumulation): each optimised clipboard image joins a
/// running collection instead of replacing the last, so all of them can go on the clipboard as one file list.
/// The collection starts again after <see cref="Settings.ClipboardSettings.CollectResultsIdleSeconds"/>, or when it's cleared
/// (tray "Clear results", the Escape hotkey). Thread-safe: results arrive from the thread pool, clears from the UI.
/// </summary>
public sealed class ClipboardCollection(Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly List<string> _paths = [];
    private readonly Lock _lock = new();
    private DateTime _lastAdded;

    public int Count
    {
        get
        {
            lock (_lock)
                return _paths.Count;
        }
    }

    /// <summary>
    /// Adds a result and returns everything collected, oldest first. If nothing was added for longer than
    /// <paramref name="idleTimeout"/> (zero or less = never), the old collection is dropped first.
    /// Adding a path that's already there moves it to the end rather than listing it twice.
    /// </summary>
    public IReadOnlyList<string> Add(string path, TimeSpan idleTimeout)
    {
        lock (_lock)
        {
            var now = _clock();
            if (idleTimeout > TimeSpan.Zero && _paths.Count > 0 && now - _lastAdded > idleTimeout)
                _paths.Clear();

            _paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _paths.Add(path);
            _lastAdded = now;
            return _paths.ToList();
        }
    }

    /// <summary>Forgets every collected result (the files themselves stay in the working folder).</summary>
    public void Clear()
    {
        lock (_lock)
            _paths.Clear();
    }

    /// <summary>
    /// The <c>CF_HDROP</c> payload for <paramref name="paths"/>: a <c>DROPFILES</c> header followed by the
    /// wide-character paths, each ending in a null, and an extra null after the last.
    /// </summary>
    public static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        const int headerSize = 20;
        var text = new StringBuilder();
        foreach (var path in paths)
            text.Append(path).Append('\0');
        text.Append('\0');
        if (paths.Count == 0)
            text.Append('\0'); // an empty list is still double-null terminated

        var pathBytes = Encoding.Unicode.GetBytes(text.ToString());
        var data = new byte[headerSize + pathBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, headerSize); // pFiles
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 1); // fWide
        pathBytes.CopyTo(data, headerSize);
        return data;
    }
}
