using WClop.Core.Media;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Watching;

namespace WClop.Core.Placement;

/// <summary>Creation and modification times captured before a file is replaced.</summary>
public sealed record FileTimes(DateTime CreationUtc, DateTime LastWriteUtc)
{
    public static FileTimes Of(string path) =>
        new(File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path));

    public void ApplyTo(string path)
    {
        File.SetCreationTimeUtc(path, CreationUtc);
        File.SetLastWriteTimeUtc(path, LastWriteUtc);
    }
}

public sealed record PlacementRequest
{
    /// <summary>The finished output in the working directory. It is moved, not copied.</summary>
    public required string OutputPath { get; init; }

    /// <summary>The user's file the output was made from. Must already be backed up for <see cref="OutputBehaviour.InPlace"/>.</summary>
    public required string OriginalPath { get; init; }

    public required OutputBehaviour Behaviour { get; init; }

    /// <summary>Template for <see cref="OutputBehaviour.SameFolder"/> or <see cref="OutputBehaviour.SpecificFolder"/>.</summary>
    public string? Template { get; init; }

    /// <summary>Times to copy onto the placed file, or null to leave them as written.</summary>
    public FileTimes? PreserveTimes { get; init; }

    public DateTime Now { get; init; } = DateTime.Now;
    public Func<int>? NextCounter { get; init; }
}

/// <summary>Moves a finished output to where the user wants it (project.md §13).</summary>
public sealed class FilePlacer(OptimisationMarkers markers, RecentWrites? recentWrites = null)
{
    /// <summary>Returns the final path. The placed file gets its times restored and the "optimised" marker.</summary>
    public string Place(PlacementRequest request)
    {
        var output = Path.GetFullPath(request.OutputPath);
        var original = Path.GetFullPath(request.OriginalPath);
        // Keep the user's spelling (.jpeg stays .jpeg) unless the format actually changed.
        var extension = FileFormats.FromExtension(original) == FileFormats.FromExtension(output)
            ? Path.GetExtension(original)
            : Path.GetExtension(output);

        var target = request.Behaviour switch
        {
            OutputBehaviour.Temporary => output,
            OutputBehaviour.InPlace => Path.ChangeExtension(original, extension),
            OutputBehaviour.SameFolder => SameFolderTarget(request, original, extension),
            OutputBehaviour.SpecificFolder => TemplatedTarget(request, original, extension),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Behaviour, null),
        };

        if (!PathsEqual(target, output))
        {
            // Overwrite the original itself (it's backed up) or a previous WClop output; never someone else's file.
            if (File.Exists(target) && !PathsEqual(target, original) && markers.Get(target) != MarkerStatus.Optimised)
                target = UniquePath(target);

            // Registered before writing: the watcher event can arrive before File.Move returns.
            recentWrites?.Register(target);
            recentWrites?.Register(original);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            MoveWithRetry(output, target);

            // In place with a new format (photo.png → photo.jpg): the original is backed up, so remove it.
            if (request.Behaviour == OutputBehaviour.InPlace && !PathsEqual(target, original) && File.Exists(original))
                File.Delete(original);
        }

        request.PreserveTimes?.ApplyTo(target);
        markers.Set(target, MarkerStatus.Optimised);
        return target;
    }

    /// <summary>
    /// Thumbnailers, indexers and antivirus open new files for a moment; replacing one then fails with a sharing or
    /// access error that clears by itself, so try again for a few seconds before giving up.
    /// </summary>
    internal static void MoveWithRetry(string source, string destination, int attempts = 12)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < attempts && File.Exists(source))
            {
                Thread.Sleep(Math.Min(50 * attempt, 400));
            }
        }
    }

    private static string SameFolderTarget(PlacementRequest request, string original, string extension)
    {
        var template = request.Template ?? "%f-optimised";
        if (NameTemplate.IsAlreadyTemplated(original, template))
            return Path.ChangeExtension(original, extension);

        var folder = Path.GetDirectoryName(original)!;
        var name = Path.GetFileName(NameTemplate.Render(template, original, request.Now, request.NextCounter));
        return Path.Combine(folder, name + extension);
    }

    private static string TemplatedTarget(PlacementRequest request, string original, string extension) =>
        NameTemplate.Render(request.Template ?? "%P/optimised/%f", original, request.Now, request.NextCounter) + extension;

    private static string UniquePath(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
