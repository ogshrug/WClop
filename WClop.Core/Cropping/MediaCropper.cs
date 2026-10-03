using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Processes;
using WClop.Core.Video;

namespace WClop.Core.Cropping;

/// <summary>A crop that would change nothing (the file is already that size or smaller).</summary>
public sealed class NothingToCropException(string message) : OptimisationException(message);

/// <summary>A cropped file in the working folder, and the window that was kept.</summary>
public sealed record CropOutput(string Path, int Width, int Height, bool Smart)
{
    public string Size => $"{Width}×{Height}";
}

/// <summary>
/// Crops images, animated GIFs and videos (project.md §8.9): gifsicle for GIFs so every frame is kept, FFmpeg for
/// everything else. The input is never modified; the output is a new file in the working folder for the caller to
/// place. Used by the <c>crop</c> pipeline step, result cards and <c>wclop crop</c>.
/// </summary>
public sealed class MediaCropper(ToolLocator tools, AppPaths paths)
{
    /// <summary>Null if <paramref name="crop"/> wouldn't change <paramref name="input"/>.</summary>
    public async Task<CropOutput?> CropAsync(string input, CropSpec crop, CancellationToken cancellationToken = default)
    {
        var format = FileTypeSniffer.Detect(input);
        var kind = format.Kind();
        if (kind is not (MediaKind.Image or MediaKind.Video))
            throw new UnsupportedFormatException("Only images and videos can be cropped");

        var size = await ReadSizeAsync(input, kind, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnsupportedFormatException("Can't read the size to crop");
        if (crop.Rectangle(size, even: kind == MediaKind.Video) is not var (w, h, x, y))
            return null;

        var smart = false;
        if (crop.Smart && kind == MediaKind.Image)
        {
            try
            {
                (x, y) = ImageInterest.BestWindow(input, size, w, h);
                smart = true;
            }
            catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
            {
                // WIC can't decode it (HEIC without the extension…): the centre it is.
            }
        }

        if (format == FileFormat.Gif)
        {
            var gif = TempPath(".gif");
            var result = await ProcessRunner.RunAsync(tools.Require(Tool.Gifsicle),
                ["--crop", $"{x},{y}+{w}x{h}", "-o", gif, input], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                File.Delete(gif);
                throw new ToolFailedException(result);
            }

            return new CropOutput(gif, w, h, smart);
        }

        var filter = $"crop={w}:{h}:{x}:{y}";
        if (kind == MediaKind.Video)
        {
            var video = await FfmpegAsync(input, ["-vf", filter, .. VideoReencode.Codec(input), "-c:a", "copy"],
                VideoReencode.OutputExtension(input), cancellationToken).ConfigureAwait(false);
            return new CropOutput(video, w, h, smart);
        }

        string[] quality = format switch
        {
            FileFormat.Jpeg => ["-q:v", "2"],
            FileFormat.WebP => ["-quality", "92"],
            FileFormat.Png or FileFormat.Bmp or FileFormat.Tiff => [],
            _ => throw new UnsupportedFormatException($"Can't crop {format} images; convert them first"),
        };
        var image = await FfmpegAsync(input, ["-vf", filter, "-frames:v", "1", "-update", "1", .. quality], format.Extension(), cancellationToken)
            .ConfigureAwait(false);
        return new CropOutput(image, w, h, smart);
    }

    /// <summary>Pixel size of an image (header only) or a video (ffprobe).</summary>
    public async Task<ImageSize?> ReadSizeAsync(string path, MediaKind kind, CancellationToken cancellationToken = default) => kind switch
    {
        MediaKind.Image => ImageDecoding.TryReadSize(path),
        MediaKind.Video => await VideoInfo.ProbeAsync(tools.Require(Tool.Ffprobe), path, cancellationToken).ConfigureAwait(false) is { } info
            ? new ImageSize(info.Width, info.Height)
            : null,
        _ => null,
    };

    private async Task<string> FfmpegAsync(string input, IReadOnlyList<string> arguments, string extension, CancellationToken cancellationToken)
    {
        var output = TempPath(extension);
        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", input };
        args.AddRange(arguments);
        if (extension is ".mp4" or ".mov")
            args.AddRange(["-movflags", "+faststart"]);
        args.Add(output);

        var result = await ProcessRunner.RunAsync(tools.Require(Tool.Ffmpeg), args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            File.Delete(output);
            throw new ToolFailedException(result);
        }

        return output;
    }

    private string TempPath(string extension)
    {
        Directory.CreateDirectory(paths.ForFilters);
        return AppPaths.NewTempPath(paths.ForFilters, extension);
    }
}
