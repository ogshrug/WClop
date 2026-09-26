namespace WClop.Core.Storage;

/// <summary>
/// Reads and writes file markers using the NTFS alternate data stream first and the database as backup,
/// so the marker survives volumes and tools that drop streams.
/// </summary>
public sealed class OptimisationMarkers(OptimisationDatabase database)
{
    public MarkerStatus Get(string path)
    {
        var fromStream = AlternateStreamMarker.Read(path);
        if (fromStream != MarkerStatus.None)
            return fromStream;

        var info = new FileInfo(path);
        return info.Exists ? database.GetMarker(path, info.Length, info.LastWriteTimeUtc) : MarkerStatus.None;
    }

    /// <summary>Sets the marker without changing the file's timestamps.</summary>
    public void Set(string path, MarkerStatus status)
    {
        var info = new FileInfo(path);
        if (info.Exists)
        {
            AlternateStreamMarker.TryWrite(path, status);
            info.Refresh();
            database.SetMarker(path, info.Length, info.LastWriteTimeUtc, status);
        }
        else if (status == MarkerStatus.None)
        {
            // The file is gone (e.g. replaced by a new format); drop its database row.
            database.SetMarker(path, 0, default, MarkerStatus.None);
        }
    }

    public void Clear(string path) => Set(path, MarkerStatus.None);
}
