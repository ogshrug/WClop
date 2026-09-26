namespace WClop.Core.Processes;

public enum Tool
{
    Pngquant,
    Jpegoptim,
    Gifsicle,
    Exiftool,
    Ffmpeg,
    Ffprobe,
    Ghostscript,
}

public sealed class ToolNotFoundException(Tool tool, IEnumerable<string> searched)
    : Exception($"{ToolLocator.ExecutableName(tool)} not found. Searched: {string.Join("; ", searched)}. "
                + "In a dev checkout, run scripts/fetch-tools.ps1.")
{
    public Tool Tool { get; } = tool;
}

/// <summary>Finds the external tools WClop drives.</summary>
public sealed class ToolLocator(IReadOnlyList<string> searchDirectories)
{
    public IReadOnlyList<string> SearchDirectories { get; } = searchDirectories;

    /// <summary>
    /// Release builds: <c>%LOCALAPPDATA%\WClop\bin</c>, then <c>tools</c> next to the executable.
    /// Dev builds: also the <c>tools</c> folder at the repository root (found by walking up to <c>WClop.sln</c>).
    /// </summary>
    public static ToolLocator CreateDefault()
    {
        var dirs = new List<string>
        {
            AppPaths.BundledToolsDir,
            Path.Combine(AppContext.BaseDirectory, "tools"),
        };

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WClop.sln")))
            {
                dirs.Add(Path.Combine(dir.FullName, "tools"));
                break;
            }
        }

        return new ToolLocator(dirs);
    }

    public static string ExecutableName(Tool tool) => tool switch
    {
        Tool.Pngquant => "pngquant.exe",
        Tool.Jpegoptim => "jpegoptim.exe",
        Tool.Gifsicle => "gifsicle.exe",
        Tool.Exiftool => "exiftool.exe",
        Tool.Ffmpeg => "ffmpeg.exe",
        Tool.Ffprobe => "ffprobe.exe",
        Tool.Ghostscript => "gswin64c.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    public string? Find(Tool tool)
    {
        var name = ExecutableName(tool);
        return SearchDirectories.Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
    }

    public string Require(Tool tool) => Find(tool) ?? throw new ToolNotFoundException(tool, SearchDirectories);

    public IReadOnlyList<Tool> Missing() => Enum.GetValues<Tool>().Where(t => Find(t) is null).ToList();
}
