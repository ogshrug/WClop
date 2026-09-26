namespace WClop.Core.Storage;

/// <summary>
/// Stores <see cref="MarkerStatus"/> in an NTFS alternate data stream (<c>file.png:WClop.Status</c>),
/// the Windows equivalent of Clop's extended attribute (project.md §26.1).
/// Streams are lost on FAT32/exFAT, some network shares, cloud sync, zip and email,
/// so <see cref="OptimisationMarkers"/> backs this with a database.
/// </summary>
public static class AlternateStreamMarker
{
    public const string StreamName = "WClop.Status";

    private static string StreamPath(string path) => path + ":" + StreamName;

    public static MarkerStatus Read(string path)
    {
        try
        {
            return MarkerStatusText.Parse(File.ReadAllText(StreamPath(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return MarkerStatus.None;
        }
    }

    /// <summary>
    /// Writes (or with <see cref="MarkerStatus.None"/>, removes) the stream, keeping the file's timestamps,
    /// since writing a stream otherwise bumps the modification time. Returns false if the volume doesn't support streams.
    /// </summary>
    public static bool TryWrite(string path, MarkerStatus status)
    {
        try
        {
            var creation = File.GetCreationTimeUtc(path);
            var lastWrite = File.GetLastWriteTimeUtc(path);

            if (status == MarkerStatus.None)
                File.Delete(StreamPath(path));
            else
                File.WriteAllText(StreamPath(path), MarkerStatusText.ToText(status));

            File.SetCreationTimeUtc(path, creation);
            File.SetLastWriteTimeUtc(path, lastWrite);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
