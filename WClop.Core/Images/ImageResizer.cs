using System.Globalization;
using System.Windows.Media.Imaging;
using WClop.Core.Media;
using WClop.Core.Processes;

namespace WClop.Core.Images;

/// <summary>
/// Downscaling (project.md §8.9, §8.10). Still images use WIC's decoder-level scaling in-process
/// (Clop uses vipsthumbnail); GIFs use gifsicle so every frame is resized and the animation survives.
/// Output is written at maximum quality and optimised afterwards, so it's only lossily encoded once.
/// </summary>
public sealed class ImageResizer(ToolLocator tools, AppPaths paths)
{
    /// <summary>Scales steps: 100% → 75% → 50% → 40% → 30% → 20% → 10% (minus 0.25 above 50%, then 0.1).</summary>
    public static double NextDownscaleStep(double scale)
    {
        var next = scale > 0.5 + 1e-9 ? scale - 0.25 : scale - 0.1;
        return Math.Round(Math.Max(next, 0.1), 2);
    }

    /// <summary>Target size for a scale, rounded to even numbers (video-encoder friendly), at least 2 px.</summary>
    public static ImageSize ScaledSize(ImageSize size, double scale) =>
        new(Even(size.Width * scale), Even(size.Height * scale));

    public async Task<(string Path, ImageSize Size)> DownscaleAsync(string input, double scale, CancellationToken cancellationToken)
    {
        if (scale is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Downscale factor must be between 0 and 1");

        var format = FileTypeSniffer.Detect(input);
        var original = ImageDecoding.TryReadSize(input)
                       ?? throw new UnsupportedFormatException("Can't read the image size");
        var target = ScaledSize(original, scale);

        Directory.CreateDirectory(paths.ForResize);
        var output = AppPaths.NewTempPath(paths.ForResize, format.Extension());

        switch (format)
        {
            case FileFormat.Gif:
                await ProcessRunner.RunWithRetriesAsync(
                    tools.Require(Tool.Gifsicle),
                    ["--unoptimize", "--resize-method", "box", "--resize-colors", "256",
                     "--resize", string.Create(CultureInfo.InvariantCulture, $"{target.Width}x{target.Height}"),
                     "-o", output, input],
                    attempts: 2, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case FileFormat.Png or FileFormat.Jpeg:
                await Task.Run(() => ResizeWithWic(input, output, format, target), cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new UnsupportedFormatException($"Downscaling {format} isn't supported yet");
        }

        return (output, target);
    }

    private static void ResizeWithWic(string input, string output, FileFormat format, ImageSize target)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(File.ReadAllBytes(input));
        image.CacheOption = BitmapCacheOption.OnLoad;
        // Scaling at decode time uses WIC's high-quality (Fant) resampling.
        image.DecodePixelWidth = target.Width;
        image.DecodePixelHeight = target.Height;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.EndInit();
        image.Freeze();

        BitmapEncoder encoder = format == FileFormat.Jpeg
            ? new JpegBitmapEncoder { QualityLevel = 100 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(output);
        encoder.Save(stream);
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
}
