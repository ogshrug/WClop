using System.Globalization;
using WClop.Core.Compression;
using WClop.Core.Media;
using WClop.Core.Processes;

namespace WClop.Core.Images;

public sealed record ImageOptimiseOptions
{
    /// <summary>Compression factor, 5–100 (§7).</summary>
    public int Factor { get; init; } = CompressionModel.DefaultImageFactor;

    /// <summary>Accept an output that isn't smaller than the input (e.g. after a resize).</summary>
    public bool AllowLarger { get; init; }

    public bool StripMetadata { get; init; } = true;
    public bool PreserveColorMetadata { get; init; } = true;

    /// <summary>
    /// Where to take metadata from, if not the input itself: a resized intermediate has none, so tags such as
    /// orientation come from the original.
    /// </summary>
    public string? MetadataSource { get; init; }

    /// <summary>Try the other of PNG / JPEG too and keep it if it's clearly smaller (§8.2, §8.3).</summary>
    public bool Adaptive { get; init; }

    /// <summary>Force a PNG palette size (the downscale guard re-quantises to the original's colour count).</summary>
    public int? MaxColors { get; init; }
}

/// <summary>An optimised image in the working directory, not yet placed.</summary>
public sealed record ImageOptimiseOutput(
    string Path,
    FileFormat InputFormat,
    FileFormat OutputFormat,
    long InputSize,
    long OutputSize,
    ImageSize Size);

/// <summary>
/// Optimises one image into the working directory (project.md §8). The input file is never modified:
/// it is staged under an ASCII temp name named after its real format, and tools write separate outputs.
/// </summary>
public sealed class ImageOptimiser(ToolLocator tools, AppPaths paths)
{
    private const int ToolAttempts = 2;
    private static readonly ProcessRunOptions ToolOptions = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Clop's threshold for the adaptive format test: the other format must win by more than this (§8.2).</summary>
    public const long AdaptiveMinimumSaving = 100 * 1024;

    public async Task<ImageOptimiseOutput> OptimiseAsync(
        string inputPath,
        ImageOptimiseOptions options,
        CancellationToken cancellationToken = default)
    {
        var inputSize = new FileInfo(inputPath).Length;
        var format = FileTypeSniffer.Detect(inputPath);

        Directory.CreateDirectory(paths.Images);
        var staged = AppPaths.NewTempPath(paths.Images, format.Extension());
        File.Copy(inputPath, staged);

        string? output = null;
        try
        {
            output = format switch
            {
                FileFormat.Png => await OptimisePngAsync(staged, options.Factor, cancellationToken, options.MaxColors),
                FileFormat.Jpeg => await OptimiseJpegAsync(staged, options.Factor, cancellationToken),
                FileFormat.Gif => await OptimiseGifAsync(staged, options, cancellationToken),
                FileFormat.WebP when WebPInfo.IsAnimated(staged) => await OptimiseAnimatedWebPAsync(staged, options.Factor, cancellationToken),
                FileFormat.JpegXl => throw new NotSmallerException(inputSize, inputSize),
                FileFormat.Unknown => throw new UnsupportedFormatException("Unknown image type"),
                _ => throw new UnsupportedFormatException($"{format} can't be optimised directly (it's converted first, if enabled)"),
            };
            var outputFormat = format;

            if (options.Adaptive && format is FileFormat.Png or FileFormat.Jpeg)
            {
                if (await TryOtherFormatAsync(staged, format, output, options.Factor, cancellationToken) is { } better)
                {
                    TryDelete(output);
                    (output, outputFormat) = better;
                }
            }

            // Windows only decodes WebP with its optional codec, so a WebP is checked from its header (lossy, lossless
            // or extended) rather than decoded.
            var size = outputFormat == FileFormat.WebP && WebPInfo.Size(output) is { } webp
                ? new ImageSize(webp.Width, webp.Height)
                : ImageDecoding.Verify(output);

            if (outputFormat is FileFormat.Png or FileFormat.Jpeg)
            {
                var metadata = new MetadataWriter(tools.Require(Tool.Exiftool));
                await metadata.ApplyAsync(
                    options.MetadataSource ?? staged, output, options.StripMetadata, options.PreserveColorMetadata, cancellationToken);
            }

            var outputSize = new FileInfo(output).Length;
            if (outputSize >= inputSize && !options.AllowLarger)
                throw new NotSmallerException(inputSize, outputSize);

            return new ImageOptimiseOutput(output, format, outputFormat, inputSize, outputSize, size);
        }
        catch
        {
            if (output is not null)
                TryDelete(output);
            throw;
        }
        finally
        {
            TryDelete(staged);
        }
    }

    /// <summary>
    /// Adaptive format test (§8.2, §8.3): an opaque PNG is also tried as JPEG (photographic screenshots); a JPEG over
    /// 1 megapixel with entropy below 5 (flat, graphic content) is also tried as a quantised PNG. The alternative wins
    /// only if it's more than 100 KB smaller. Returns null to keep the original format.
    /// </summary>
    private async Task<(string Path, FileFormat Format)?> TryOtherFormatAsync(
        string staged, FileFormat format, string current, int factor, CancellationToken cancellationToken)
    {
        var pixels = await Task.Run(() => ImageAnalysis.Load(staged), cancellationToken);
        FileFormat other;
        if (format == FileFormat.Png)
        {
            if (ImageAnalysis.HasTransparency(pixels))
                return null;
            other = FileFormat.Jpeg;
        }
        else
        {
            if ((long)pixels.Width * pixels.Height <= 1_000_000 || ImageAnalysis.Entropy(pixels) >= 5)
                return null;
            other = FileFormat.Png;
        }

        var converted = await new ImageConverter(tools, paths).ConvertAsync(staged, other, factor, cancellationToken);
        try
        {
            var alternative = other == FileFormat.Jpeg
                ? await OptimiseJpegAsync(converted, factor, cancellationToken)
                : await OptimisePngAsync(converted, factor, cancellationToken);

            if (new FileInfo(alternative).Length + AdaptiveMinimumSaving < new FileInfo(current).Length)
                return (alternative, other);

            TryDelete(alternative);
            return null;
        }
        finally
        {
            TryDelete(converted);
        }
    }

    /// <summary>
    /// Animated WebP re-encoded by ffmpeg at the compression model's quality, loop count kept, audio dropped;
    /// refuses to flatten the animation (§8.6).
    /// </summary>
    private async Task<string> OptimiseAnimatedWebPAsync(string staged, int factor, CancellationToken cancellationToken)
    {
        var output = AppPaths.NewTempPath(paths.Images, ".webp");
        await ProcessRunner.RunWithRetriesAsync(
            tools.Require(Tool.Ffmpeg),
            ImageConverter.WebPArguments(staged, output, factor, animated: true, WebPInfo.LoopCount(staged)),
            ToolAttempts, ToolOptions, cancellationToken);

        if (!WebPInfo.IsAnimated(output))
        {
            TryDelete(output);
            throw new AnimationFlattenedException(2, 1);
        }

        return output;
    }

    /// <summary>pngquant with speed, quality ceiling and palette size from the compression model (§8.2).</summary>
    private async Task<string> OptimisePngAsync(string staged, int factor, CancellationToken cancellationToken, int? maxColors = null)
    {
        var output = AppPaths.NewTempPath(paths.Images, ".png");
        var args = new List<string>
        {
            "--force",
            "--speed", Invariant(CompressionModel.PngquantSpeed(factor)),
            "--quality", "0-" + Invariant(CompressionModel.PngquantMaxQuality(factor)),
            "--output", output,
        };
        var colors = maxColors ?? CompressionModel.PngquantColors(factor);
        if (colors < 256)
            args.Add(Invariant(colors));
        args.AddRange(["--", staged]);

        await ProcessRunner.RunWithRetriesAsync(
            tools.Require(Tool.Pngquant), args, ToolAttempts, ToolOptions, cancellationToken);
        return output;
    }

    /// <summary>
    /// jpegoptim in place on a copy, with a retry at the secondary quality if the first pass fails (§8.3).
    /// Metadata is kept here and handled afterwards by exiftool.
    /// </summary>
    private async Task<string> OptimiseJpegAsync(string staged, int factor, CancellationToken cancellationToken)
    {
        var jpegoptim = tools.Require(Tool.Jpegoptim);
        var output = AppPaths.NewTempPath(paths.Images, ".jpg");

        async Task<ProcessResult> Run(int quality)
        {
            File.Copy(staged, output, overwrite: true);
            return await ProcessRunner.RunAsync(
                jpegoptim,
                ["--keep-all", "--force", "--quiet", "--max=" + Invariant(quality), output],
                ToolOptions, cancellationToken);
        }

        var result = await Run(CompressionModel.JpegoptimMaxQuality(factor));
        if (!result.Succeeded)
        {
            result = await Run(CompressionModel.JpegoptimFallbackQuality(factor));
            if (!result.Succeeded)
                throw new ToolFailedException(result);
        }

        return output;
    }

    /// <summary>
    /// gifsicle with level, lossy and colours from the compression model, plus frame dropping at high factors
    /// ("play faster": frames are deleted, so the animation gets shorter). Refuses to flatten an animation (§8.4).
    /// </summary>
    private async Task<string> OptimiseGifAsync(string staged, ImageOptimiseOptions options, CancellationToken cancellationToken)
    {
        var factor = options.Factor;
        var input = GifInfo.Read(staged);
        var output = AppPaths.NewTempPath(paths.Images, ".gif");

        var args = new List<string>
        {
            "-O" + Invariant(CompressionModel.GifsicleOptimisationLevel(factor)),
            "--lossy=" + Invariant(CompressionModel.GifsicleLossy(factor)),
        };
        if (CompressionModel.GifsicleColors(factor) is { } colors)
            args.AddRange(["--colors", Invariant(colors)]);
        if (options.StripMetadata)
            args.AddRange(["--no-comments", "--no-names", "--no-extensions"]);
        args.AddRange(["-o", output, staged]);

        if (CompressionModel.GifFrameDropInterval(factor) is { } interval
            && input.FrameCount > CompressionModel.GifFrameDropMinFrames)
        {
            // Never drop frame 0. Frame selections apply to the input file named just before them.
            var dropped = Enumerable.Range(0, input.FrameCount).Where(i => i % interval == interval - 1).ToList();
            args.Add("--delete");
            args.AddRange(dropped.Select(i => "#" + Invariant(i)));
        }

        await ProcessRunner.RunWithRetriesAsync(
            tools.Require(Tool.Gifsicle), args, ToolAttempts, ToolOptions, cancellationToken);

        var result = GifInfo.Read(output);
        if (input.IsAnimated && !result.IsAnimated)
        {
            TryDelete(output);
            throw new AnimationFlattenedException(input.FrameCount, result.FrameCount);
        }

        return output;
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
