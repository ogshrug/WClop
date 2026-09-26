using WClop.Core.Media;
using WClop.Core.Settings;

namespace WClop.Core.Watching;

/// <summary>Cheap per-event checks for the folder watchers (project.md §4.3, §26.3, §26.4).</summary>
public static class WatchFilters
{
    /// <summary>Browsers and download managers write under these names, then rename to the real one.</summary>
    public static readonly IReadOnlySet<string> PartialDownloadExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp", ".temp", ".!ut", ".!qb",
    };

    // Cloud placeholders (OneDrive Files On-Demand): reading them triggers a download.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>Name-only checks, no disk access: hidden names, partial downloads, formats this watcher handles.</summary>
    public static bool IsCandidateName(string path, IReadOnlySet<FileFormat> formats, IEnumerable<string> skipFormats)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal))
            return false;

        var extension = Path.GetExtension(name);
        if (PartialDownloadExtensions.Contains(extension))
            return false;
        if (skipFormats.Contains(extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            return false;

        return formats.Contains(FileFormats.FromExtension(name));
    }

    /// <summary>Hidden, system, offline or cloud-only files are left alone.</summary>
    public static bool HasSkippableAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Offline | FileAttributes.Directory
                       | RecallOnDataAccess | RecallOnOpen)) != 0;

    public static bool IsWithinSizeLimits(long size, WatcherSettings settings) =>
        (settings.MinSizeBytes <= 0 || size >= settings.MinSizeBytes)
        && (settings.MaxSizeBytes <= 0 || size <= settings.MaxSizeBytes);

    public static bool IsWithinResolutionLimits(int width, int height, WatcherSettings settings) =>
        (settings.MinResolution <= 0 || (width >= settings.MinResolution && height >= settings.MinResolution))
        && (settings.MaxResolution <= 0 || (width <= settings.MaxResolution && height <= settings.MaxResolution));

    public static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
