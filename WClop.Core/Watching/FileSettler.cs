namespace WClop.Core.Watching;

/// <summary>
/// Waits for a newly appeared file to finish being written (project.md §4.6, §26.1): size and modification
/// time stop changing across polls, nobody else has it open for writing (on Windows a file being written is
/// usually locked), and it passes a type-specific validity check. Absorbs antivirus scans and slow downloads.
/// </summary>
public static class FileSettler
{
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(300);
    public const int MaxInvalidChecks = 5;

    /// <summary>True once the file is settled and valid; false if it vanished, never became valid, or timed out.</summary>
    public static async Task<bool> WaitAsync(
        string path, Func<string, bool> isValid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        (long Length, DateTime LastWrite)? previous = null;
        var invalidChecks = 0;

        while (true)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            var current = (info.Length, info.LastWriteTimeUtc);
            if (current == previous && current.Length > 0 && IsNotBeingWritten(path))
            {
                if (isValid(path))
                    return true;
                if (++invalidChecks >= MaxInvalidChecks)
                    return false;
            }

            previous = current;
            if (DateTime.UtcNow > deadline)
                return false;
        }
    }

    /// <summary>Opening while refusing to share write access fails if another process still has it open for writing.</summary>
    public static bool IsNotBeingWritten(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
