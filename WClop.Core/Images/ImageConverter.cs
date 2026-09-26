using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WClop.Core.Compression;
using WClop.Core.Media;
using WClop.Core.Processes;

namespace WClop.Core.Images;

/// <summary>
/// Converts images between formats (project.md §8.7, §8.8), into the working directory's conversions folder.
/// <list type="bullet">
/// <item>Decoding: Windows Imaging Component first; HEIC / AVIF (whose Windows codecs are optional Store extensions)
/// and anything WIC can't read go through ffmpeg.</item>
/// <item>JPEG / PNG / GIF are written at maximum quality and optimised afterwards, so there's only one lossy step.</item>
/// <item>WebP and AVIF are encoded by ffmpeg at the compression model's quality. Animated GIF / WebP keep their animation.</item>
/// </list>
/// </summary>
public sealed class ImageConverter(ToolLocator tools, AppPaths paths)
{
    /// <summary>Targets this machine's tools can write.</summary>
    public static readonly IReadOnlyList<FileFormat> Targets =
        [FileFormat.Jpeg, FileFormat.Png, FileFormat.WebP, FileFormat.Avif, FileFormat.Gif];

    public async Task<string> ConvertAsync(string input, FileFormat target, int factor, CancellationToken cancellationToken)
    {
        var source = FileTypeSniffer.Detect(input);
        Directory.CreateDirectory(paths.Conversions);
        var output = AppPaths.NewTempPath(paths.Conversions, target.Extension());
        var animated = IsAnimated(input, source);

        try
        {
            switch (target)
            {
                case FileFormat.WebP:
                    await RunFfmpegAsync(WebPArguments(input, output, factor, animated, LoopCount(input, source)), cancellationToken);
                    break;

                case FileFormat.Avif:
                    await RunFfmpegAsync(AvifArguments(input, output, factor), cancellationToken);
                    break;

                case FileFormat.Gif when animated:
                    // A palette built across the whole animation, so colours that appear later survive (§8.8).
                    await RunFfmpegAsync(
                    [
                        "-hide_banner", "-v", "error", "-y", "-i", input,
                        "-filter_complex", "split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5",
                        "-loop", (LoopCount(input, source) ?? 0).ToString(CultureInfo.InvariantCulture), output,
                    ], cancellationToken);
                    break;

                case FileFormat.Jpeg or FileFormat.Png or FileFormat.Gif:
                    await DecodeAndEncodeAsync(input, source, output, target, cancellationToken);
                    break;

                default:
                    throw new UnsupportedFormatException($"Converting to {target} isn't supported");
            }

            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new OutputUnreadableException($"The {target} conversion produced nothing");
            return output;
        }
        catch
        {
            TryDelete(output);
            throw;
        }
    }

    public static bool IsAnimated(string path, FileFormat format) => format switch
    {
        FileFormat.Gif => GifInfo.Read(path).IsAnimated,
        FileFormat.WebP => WebPInfo.IsAnimated(path),
        _ => false,
    };

    private static int? LoopCount(string path, FileFormat format) => format switch
    {
        FileFormat.Gif => GifInfo.Read(path).LoopCount,
        FileFormat.WebP => WebPInfo.LoopCount(path),
        _ => null,
    };

    internal static List<string> WebPArguments(string input, string output, int factor, bool animated, int? loop)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", input };
        args.AddRange(animated
            ? ["-c:v", "libwebp_anim", "-loop", (loop ?? 0).ToString(CultureInfo.InvariantCulture)]
            : ["-frames:v", "1", "-c:v", "libwebp"]);
        args.AddRange(["-quality", CompressionModel.ModernFormatQuality(factor).ToString(CultureInfo.InvariantCulture),
                       "-compression_level", "6", "-an", output]);
        return args;
    }

    /// <summary>AVIF quality (0–100, higher = better) mapped onto libaom's CRF (0–63, lower = better).</summary>
    public static int AvifCrf(int factor) =>
        (int)Math.Round((100 - CompressionModel.ModernFormatQuality(factor)) * 63 / 100.0, MidpointRounding.AwayFromZero);

    internal static List<string> AvifArguments(string input, string output, int factor) =>
    [
        "-hide_banner", "-v", "error", "-y", "-i", input, "-frames:v", "1",
        "-c:v", "libaom-av1", "-still-picture", "1", "-crf", AvifCrf(factor).ToString(CultureInfo.InvariantCulture),
        "-cpu-used", "6", "-pix_fmt", "yuv420p", "-f", "avif", output,
    ];

    /// <summary>
    /// Decodes the first frame (WIC when it can; HEIC / AVIF and anything else via ffmpeg → PNG) and re-encodes it.
    /// WPF bitmaps belong to the thread that made them, so decoding and encoding happen together on one thread.
    /// </summary>
    private async Task DecodeAndEncodeAsync(string input, FileFormat source, string output, FileFormat target, CancellationToken cancellationToken)
    {
        if (source is not (FileFormat.Heic or FileFormat.Avif)
            && await Task.Run(() => TryConvertWithWic(input, output, target), cancellationToken))
            return;

        var intermediate = AppPaths.NewTempPath(paths.Conversions, ".png");
        try
        {
            await RunFfmpegAsync(["-hide_banner", "-v", "error", "-y", "-i", input, "-frames:v", "1", intermediate], cancellationToken);
            if (!await Task.Run(() => TryConvertWithWic(intermediate, output, target), cancellationToken))
                throw new UnsupportedFormatException("The image couldn't be decoded");
        }
        finally
        {
            TryDelete(intermediate);
        }
    }

    private static bool TryConvertWithWic(string input, string output, FileFormat target)
    {
        BitmapSource frame;
        try
        {
            using var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or ArgumentException
                                      or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }

        BitmapEncoder encoder = target switch
        {
            FileFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = 100 },
            FileFormat.Gif => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };

        // JPEG has no transparency: flatten onto white rather than letting transparent pixels turn black.
        encoder.Frames.Add(BitmapFrame.Create(target == FileFormat.Jpeg ? FlattenOnWhite(frame) : frame));
        using var file = File.Create(output);
        encoder.Save(file);
        return true;
    }

    /// <summary>Composites onto white with plain pixel maths (no WPF rendering, which needs a UI thread).</summary>
    internal static BitmapSource FlattenOnWhite(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        var rgb = new byte[width * height * 3];
        for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
        {
            var alpha = pixels[i + 3];
            for (var c = 0; c < 3; c++)
                rgb[j + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha) + 127) / 255);
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, rgb, width * 3);
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        await ProcessRunner.RunWithRetriesAsync(
            tools.Require(Tool.Ffmpeg), args, attempts: 1,
            new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(5) }, cancellationToken).ConfigureAwait(false);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
