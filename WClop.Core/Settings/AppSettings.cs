namespace WClop.Core.Settings;

/// <summary>
/// All user settings. Defaults follow project.md §23, adapted to Windows (§26.3).
/// Folder paths are stored in portable form; use <see cref="PortablePath.Expand"/> before touching disk.
/// New properties must have initialisers so older settings files pick up the default.
/// </summary>
public sealed class AppSettings
{
    public ClipboardSettings Clipboard { get; set; } = new();
    public WatchingSettings Watching { get; set; } = new();
    public CompressionSettings Compression { get; set; } = new();
    public FileSettings Files { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public PipelineSettings Pipelines { get; set; } = new();
}

/// <summary>Saved pipelines and where they run automatically (project.md §19).</summary>
public sealed class PipelineSettings
{
    public List<SavedPipeline> Saved { get; set; } = [];
    public List<PipelineAttachment> Attached { get; set; } = [];

    /// <summary>Let AI assistants (the MCP server) run pipelines with <c>runScript</c> steps.</summary>
    public bool AllowScriptsFromAssistants { get; set; }

    public SavedPipeline? Find(string name) =>
        Saved.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class SavedPipeline
{
    public string Name { get; set; } = "";
    public string Script { get; set; } = "";

    /// <summary>Feed the original straight in instead of optimising it first (for pipelines that re-encode anyway).</summary>
    public bool SkipOptimisation { get; set; }

    /// <summary>Don't show a result card when it runs automatically.</summary>
    public bool HideResult { get; set; }

    /// <summary>Offer it as a drop zone preset (scroll while holding files over the zone).</summary>
    public bool ShowInDropZone { get; set; } = true;

    /// <summary>Offer it in the result card's pipeline menu.</summary>
    public bool ShowOnResults { get; set; } = true;

    /// <summary>
    /// Only for these kinds of file ("image", "video", "pdf", "audio"; empty = all): it's only offered, and only runs
    /// automatically, for them.
    /// </summary>
    public List<string> Kinds { get; set; } = [];

    public bool AppliesTo(Media.FileFormat format) =>
        Kinds.Count == 0 || format != Media.FileFormat.Unknown && Kinds.Any(k => Media.FileFormats.FromExtension("x." + k) == format
                                           || string.Equals(k, Media.FileFormats.Kind(format).ToString(), StringComparison.OrdinalIgnoreCase));
}

public enum PipelineTrigger
{
    Clipboard,
    DropZone,
    Folder,
}

/// <summary>Runs a saved pipeline automatically after WClop handles a file from somewhere.</summary>
public sealed class PipelineAttachment
{
    public string Pipeline { get; set; } = "";
    public PipelineTrigger Trigger { get; set; }

    /// <summary>For <see cref="PipelineTrigger.Folder"/>: the watched folder (portable path).</summary>
    public string? Folder { get; set; }

    /// <summary>Only for these kinds of file ("image", "video", "pdf", "audio"); empty = all.</summary>
    public List<string> Kinds { get; set; } = [];
}

/// <summary>
/// Global hotkeys (project.md §17). Clop uses Ctrl+Shift, but on Windows those combinations are taken
/// (Ctrl+Shift+Esc is Task Manager, Ctrl+Shift+Z is redo, Ctrl+Shift+C/P are devtools and command palettes),
/// so the default is Ctrl+Alt+Shift. On layouts with AltGr, some Ctrl+Alt+Shift combinations type characters;
/// disable those keys or change the modifiers.
/// </summary>
public sealed class HotkeySettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Any of Ctrl, Alt, Shift, Win joined with "+".</summary>
    public string Modifiers { get; set; } = "Ctrl+Alt+Shift";

    /// <summary>Key overrides by action name (see <c>HotkeyCatalog</c>), e.g. <c>"Restore": "Backspace"</c>.</summary>
    public Dictionary<string, string> Keys { get; set; } = [];

    /// <summary>Actions not to register, by action name, e.g. <c>"Preview"</c>.</summary>
    public List<string> DisabledActions { get; set; } = [];
}

public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Executable names (e.g. <c>KeePassXC.exe</c>) whose clipboard writes are ignored.</summary>
    public List<string> IgnoredApps { get; set; } = [];

    /// <summary>Extra clipboard format names that mean "rich app content, leave the clipboard alone".</summary>
    public List<string> ExtraDeniedFormats { get; set; } = [];

    /// <summary>Log the formats of every clipboard change (to build the deny-list against real apps).</summary>
    public bool LogFormats { get; set; }

    public bool OptimiseVideo { get; set; }
    public bool OptimiseAudio { get; set; }
    public bool OptimisePdf { get; set; }
    public bool OptimiseImagePaths { get; set; }

    /// <summary>Also put a file reference (CF_HDROP) to the optimised image on the clipboard.</summary>
    public bool CopyImageFilePath { get; set; } = true;

    /// <summary>Ignore content redirected from a Remote Desktop / VM session (owned by rdpclip.exe).</summary>
    public bool IgnoreRemoteClipboard { get; set; } = true;

    /// <summary>Whether WClop's write-back should appear in Win+V clipboard history (§26.4).</summary>
    public bool IncludeResultsInClipboardHistory { get; set; } = true;

    public bool AppendResults { get; set; }
    public int AppendResultsTimeoutSeconds { get; set; } = 30;
}

public sealed class WatchingSettings
{
    /// <summary>Global kill switch for all automatic optimisation.</summary>
    public bool Paused { get; set; }

    public int OptimisedFileProtectionMs { get; set; } = 3000;

    /// <summary>
    /// When WClop first ran. The folder watchers are extra careful for 30 s after this (project.md §4.5).
    /// Set on first launch.
    /// </summary>
    public DateTime? FirstLaunchUtc { get; set; }

    public WatcherSettings Images { get; set; } = new()
    {
        Enabled = true,
        Folders = DefaultFolders(
            Path.Combine(KnownFolder(Environment.SpecialFolder.MyPictures), "Screenshots"),
            Path.Combine(KnownFolder(Environment.SpecialFolder.UserProfile), "OneDrive", "Pictures", "Screenshots")),
        MaxFilesPerBurst = 4,
        MinSizeBytes = 50 * 1024,
        MaxSizeBytes = 50 * 1024 * 1024,
        MinResolution = 20,
        SkipFormats = ["tiff", "tif"],
    };

    public WatcherSettings Videos { get; set; } = new()
    {
        Enabled = true,
        Folders = DefaultFolders(
            Path.Combine(KnownFolder(Environment.SpecialFolder.MyVideos), "Screen Recordings"),
            Path.Combine(KnownFolder(Environment.SpecialFolder.MyVideos), "Captures")),
        MaxFilesPerBurst = 1,
        MinSizeBytes = 200 * 1024,
        MaxSizeBytes = 500L * 1024 * 1024,
        MinResolution = 50,
        SkipFormats = ["mkv", "m4v"],
    };

    public WatcherSettings Pdfs { get; set; } = new()
    {
        Enabled = true,
        MaxFilesPerBurst = 2,
        MaxSizeBytes = 100L * 1024 * 1024,
    };

    public WatcherSettings Audio { get; set; } = new()
    {
        Enabled = false,
        MaxFilesPerBurst = 2,
        MaxSizeBytes = 100L * 1024 * 1024,
    };

    private static string KnownFolder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

    private static List<string> DefaultFolders(params string[] folders) =>
        folders.Select(PortablePath.Contract).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed class WatcherSettings
{
    public bool Enabled { get; set; }

    /// <summary>Portable paths. Missing folders are kept so they resume when a drive is mounted (§4.2).</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>Folders (from <see cref="Folders"/>) whose results are optimised without showing a result card.</summary>
    public List<string> QuietFolders { get; set; } = [];

    public int MaxFilesPerBurst { get; set; } = 1;

    /// <summary>0 means no limit.</summary>
    public long MinSizeBytes { get; set; }

    /// <summary>0 means no limit.</summary>
    public long MaxSizeBytes { get; set; }

    /// <summary>Minimum width and height in pixels. 0 means no limit.</summary>
    public int MinResolution { get; set; }

    /// <summary>Maximum width and height in pixels. 0 means no limit.</summary>
    public int MaxResolution { get; set; }

    /// <summary>Lowercase extensions without the dot.</summary>
    public List<string> SkipFormats { get; set; } = [];
}

public enum VideoTier
{
    Adaptive,
    Lossless,
    Fast,
    Smaller,
    Custom,
}

public enum PdfDpiMode
{
    Adaptive,
    Fixed,
}

public sealed class CompressionSettings
{
    /// <summary>5–100, higher means smaller files (§7).</summary>
    public int ImageFactor { get; set; } = 30;

    public bool AdaptiveImageFormat { get; set; }

    public VideoTier VideoTier { get; set; } = VideoTier.Fast;
    public int VideoFactor { get; set; } = 50;
    public bool AdaptiveVideoEncoder { get; set; } = true;
    public bool CapVideoFps { get; set; } = true;
    public int VideoFpsTarget { get; set; } = 60;
    public bool RemoveAudioFromVideos { get; set; }

    public int AudioFactor { get; set; } = 35;

    public PdfDpiMode PdfDpiMode { get; set; } = PdfDpiMode.Adaptive;
    public int PdfFixedDpi { get; set; } = 150;

    /// <summary>Lowercase source extensions converted to JPEG automatically.</summary>
    public List<string> ConvertToJpeg { get; set; } = ["webp", "avif", "heic", "heif", "bmp"];

    public List<string> ConvertToPng { get; set; } = ["tiff", "tif"];

    public List<string> ConvertToMp4 { get; set; } = ["mov", "mpg", "mpeg", "m2v", "webm"];
}

/// <summary>Where an output goes (§13.1).</summary>
public enum OutputBehaviour
{
    /// <summary>Stay in the working directory; original untouched.</summary>
    Temporary,

    /// <summary>Replace the original; the original goes to backups.</summary>
    InPlace,

    /// <summary>Save next to the original using <see cref="OutputPlacement.SameFolderTemplate"/>.</summary>
    SameFolder,

    /// <summary>Save to <see cref="OutputPlacement.SpecificFolderTemplate"/>.</summary>
    SpecificFolder,
}

public sealed class OutputPlacement
{
    public OutputBehaviour Optimised { get; set; } = OutputBehaviour.InPlace;
    public OutputBehaviour AutoConverted { get; set; } = OutputBehaviour.InPlace;
    public OutputBehaviour ManuallyConverted { get; set; } = OutputBehaviour.SameFolder;

    public string SameFolderTemplate { get; set; } = "%f-optimised";
    public string SpecificFolderTemplate { get; set; } = "%P/optimised/%f";
    public string ConvertedSameFolderTemplate { get; set; } = "%f";
    public string ConvertedSpecificFolderTemplate { get; set; } = "%P/converted/%f";
}

public enum CleanupInterval
{
    Every10Minutes,
    Hourly,
    Every12Hours,
    Daily,
    Every3Days,
    Never,
}

public sealed class FileSettings
{
    public bool StripMetadata { get; set; } = true;
    public bool PreserveDates { get; set; } = true;
    public bool PreserveColorMetadata { get; set; } = true;

    /// <summary>Portable path; null means <see cref="AppPaths.DefaultWorkDir"/>.</summary>
    public string? WorkDir { get; set; }

    public CleanupInterval CleanupInterval { get; set; } = CleanupInterval.Every3Days;

    public bool BatchModeForFolders { get; set; } = true;
    public int BatchModeFileCountThreshold { get; set; } = 30;

    public OutputPlacement Images { get; set; } = new() { AutoConverted = OutputBehaviour.SameFolder };
    public OutputPlacement Videos { get; set; } = new();
    public OutputPlacement Pdfs { get; set; } = new();
    public OutputPlacement Audio { get; set; } = new();

    public string ResolveWorkDir() => WorkDir is { Length: > 0 } dir ? PortablePath.Expand(dir) : AppPaths.DefaultWorkDir;
}

public enum ScreenCorner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

public sealed class UiSettings
{
    public bool ShowTrayIcon { get; set; } = true;
    public bool ShowFloatingResults { get; set; } = true;
    public ScreenCorner FloatingResultsCorner { get; set; } = ScreenCorner.BottomRight;
    public bool FollowCursorScreen { get; set; } = true;
    public bool AutoHideResults { get; set; } = true;
    public int AutoHideSeconds { get; set; } = 30;
    public int ClipboardAutoHideSeconds { get; set; } = 10;
    public int CompactClearSeconds { get; set; } = 120;
    public bool AllowInScreenshots { get; set; }
    public bool DismissOnDrop { get; set; } = true;
    public bool DropZoneEnabled { get; set; } = true;

    /// <summary>Which screen edge the drop-zone tab pops out of.</summary>
    public DropZone.ScreenEdge DropZoneEdge { get; set; } = DropZone.ScreenEdge.Right;

    /// <summary>Where along that edge, 0–100 % (top→bottom or left→right) to the tab's centre.</summary>
    public double DropZonePosition { get; set; } = 72;
}
