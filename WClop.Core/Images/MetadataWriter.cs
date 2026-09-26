using WClop.Core.Processes;

namespace WClop.Core.Images;

/// <summary>
/// Metadata handling with exiftool (project.md §15). Stripping keeps orientation, resolution and
/// (optionally) colour space tags and the ICC profile; otherwise all tags are copied from the original.
/// </summary>
public sealed class MetadataWriter(string exiftoolPath)
{
    /// <summary>
    /// Rewrites <paramref name="targetPath"/>'s metadata from <paramref name="sourcePath"/>.
    /// Returns false (and leaves the target as it was) if exiftool fails: losing metadata handling
    /// must not lose the optimisation.
    /// </summary>
    public async Task<bool> ApplyAsync(
        string sourcePath,
        string targetPath,
        bool strip,
        bool preserveColor,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "-q", "-q", "-m", "-overwrite_original", "-charset", "filename=utf8" };
        if (strip)
        {
            args.AddRange(["-all=", "-tagsFromFile", sourcePath, "-Orientation", "-XResolution", "-YResolution", "-ResolutionUnit"]);
            if (preserveColor)
                args.Add("-ColorSpaceTags");
        }
        else
        {
            args.AddRange(["-tagsFromFile", sourcePath, "-all:all", "-unsafe"]);
        }

        args.Add(targetPath);

        try
        {
            var result = await ProcessRunner.RunAsync(
                exiftoolPath, args,
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(60) },
                cancellationToken).ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
