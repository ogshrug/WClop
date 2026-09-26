namespace WClop.Core;

/// <summary>
/// Locations of the settings file and the working directory (project.md §14.1).
/// </summary>
public sealed class AppPaths
{
    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WClop");

    public static string LocalAppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WClop");

    public static string SettingsFile { get; } = Path.Combine(AppDataDir, "settings.json");

    /// <summary>The version being run ("0.12.0"), stamped by scripts/package.ps1 from the release tag.</summary>
    public static string Version { get; } =
        typeof(AppPaths).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static string DefaultWorkDir { get; } = Path.Combine(LocalAppDataDir, "Cache");

    /// <summary>Where release builds unpack bundled tools.</summary>
    public static string BundledToolsDir { get; } = Path.Combine(LocalAppDataDir, "bin");

    public AppPaths(string workDir)
    {
        WorkDir = Path.GetFullPath(workDir);
        Backups = Path.Combine(WorkDir, "backups");
        BatchBackups = Path.Combine(WorkDir, "batch-backups");
        Images = Path.Combine(WorkDir, "images");
        Videos = Path.Combine(WorkDir, "videos");
        Pdfs = Path.Combine(WorkDir, "pdfs");
        Audios = Path.Combine(WorkDir, "audios");
        Conversions = Path.Combine(WorkDir, "conversions");
        Downloads = Path.Combine(WorkDir, "downloads");
        ForResize = Path.Combine(WorkDir, "for-resize");
        ForFilters = Path.Combine(WorkDir, "for-filters");
        ProcessLogs = Path.Combine(WorkDir, "process-logs");
    }

    public string WorkDir { get; }

    /// <summary>Originals, named with a content hash.</summary>
    public string Backups { get; }

    /// <summary>One subfolder per batch run. Never cleaned up automatically.</summary>
    public string BatchBackups { get; }

    public string Images { get; }
    public string Videos { get; }
    public string Pdfs { get; }
    public string Audios { get; }
    public string Conversions { get; }
    public string Downloads { get; }
    public string ForResize { get; }
    public string ForFilters { get; }
    public string ProcessLogs { get; }

    public IEnumerable<string> AllFolders =>
    [
        Backups, BatchBackups, Images, Videos, Pdfs, Audios, Conversions,
        Downloads, ForResize, ForFilters, ProcessLogs,
    ];

    /// <summary>Folders that periodic cleanup may delete old files from (everything except batch backups).</summary>
    public IEnumerable<string> CleanableFolders => AllFolders.Where(f => f != BatchBackups);

    public void EnsureCreated()
    {
        foreach (var folder in AllFolders)
            Directory.CreateDirectory(folder);
    }

    /// <summary>
    /// Returns a new, not-yet-existing path in <paramref name="folder"/> with a random ASCII name.
    /// Tools get ASCII paths so non-ASCII filenames can't trip up older command-line tools.
    /// </summary>
    public static string NewTempPath(string folder, string extension)
    {
        if (extension.Length > 0 && extension[0] != '.')
            extension = "." + extension;

        string path;
        do
        {
            path = Path.Combine(folder, Guid.NewGuid().ToString("N")[..16] + extension);
        } while (File.Exists(path) || Directory.Exists(path));

        return path;
    }
}
