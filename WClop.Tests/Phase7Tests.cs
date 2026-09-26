using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WClop.Core;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Video;

namespace WClop.Tests;

internal static class Phase7Images
{
    /// <summary>Flat blocks of a few colours, like a logo or UI graphic.</summary>
    public static BitmapSource Flat(int width, int height, int colors = 4)
    {
        var palette = new[] { (byte)30, (byte)90, (byte)160, (byte)230, (byte)60, (byte)200, (byte)120, (byte)10 };
        var stride = width * 3;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var c = ((x / 40) + (y / 40)) % colors;
            var i = y * stride + x * 3;
            pixels[i] = palette[c];
            pixels[i + 1] = palette[(c + 3) % palette.Length];
            pixels[i + 2] = palette[(c + 5) % palette.Length];
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
    }

    /// <summary>Half transparent, half opaque.</summary>
    public static BitmapSource WithAlpha(int width = 64, int height = 64)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 3] = (byte)(i < pixels.Length / 2 ? 0 : 255);
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    public static string Save(BitmapEncoder encoder, BitmapSource image, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}

public class ImageAnalysisTests
{
    [Fact]
    public void Transparency()
    {
        using var dir = new TempDir();
        var alpha = TestImages.SavePng(Phase7Images.WithAlpha(), dir.File("a.png"));
        var opaque = TestImages.SavePng(TestImages.Photo(32, 32), dir.File("b.png"));

        Assert.True(ImageAnalysis.HasTransparency(ImageAnalysis.Load(alpha)));
        Assert.False(ImageAnalysis.HasTransparency(ImageAnalysis.Load(opaque)));
    }

    [Fact]
    public void EntropySeparatesGraphicsFromPhotos()
    {
        using var dir = new TempDir();
        var flat = ImageAnalysis.Entropy(ImageAnalysis.Load(TestImages.SavePng(Phase7Images.Flat(200, 200), dir.File("flat.png"))));
        var photo = ImageAnalysis.Entropy(ImageAnalysis.Load(TestImages.SavePng(TestImages.Photo(200, 200), dir.File("photo.png"))));

        Assert.True(flat < 5, $"flat {flat}");
        Assert.True(photo > 5, $"photo {photo}");
    }

    [Fact]
    public void CountsColorsAndStopsEarly()
    {
        using var dir = new TempDir();
        var flat = ImageAnalysis.Load(TestImages.SavePng(Phase7Images.Flat(200, 200, colors: 4), dir.File("flat.png")));
        var photo = ImageAnalysis.Load(TestImages.SavePng(TestImages.Photo(200, 200), dir.File("photo.png")));

        Assert.Equal(4, ImageAnalysis.CountColors(flat));
        Assert.Equal(257, ImageAnalysis.CountColors(photo));
    }

    [Fact]
    public void WebPCanvasSize()
    {
        byte[] header = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "VP8X"u8, 10, 0, 0, 0, 0, 0, 0, 0, 0x7F, 0x07, 0, 0x37, 0x04, 0];

        Assert.Equal((1920, 1080), WebPInfo.CanvasSize(header));
    }

    [Theory]
    [InlineData(30, 25)]
    [InlineData(100, 54)]
    public void AvifCrf(int factor, int crf) => Assert.Equal(crf, ImageConverter.AvifCrf(factor));
}

/// <summary>Conversions and format decisions with the real tools.</summary>
public sealed class ConversionTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;
    private readonly ImageConverter _converter;
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();

    public ConversionTests()
    {
        _paths = new AppPaths(_dir.File("cache"));
        _paths.EnsureCreated();
        _settings.Compression.VideoTier = VideoTier.Smaller;
        _service = new FileOptimisationService(_settings, _paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite")));
        _converter = new ImageConverter(_tools, _paths);
    }

    public void Dispose() => _dir.Dispose();

    private static readonly FileOptimisationRequest InPlace = new() { Behaviour = OutputBehaviour.InPlace };

    [ToolsTheory]
    [InlineData(FileFormat.Jpeg)]
    [InlineData(FileFormat.WebP)]
    [InlineData(FileFormat.Avif)]
    [InlineData(FileFormat.Gif)]
    [InlineData(FileFormat.Png)]
    public async Task ConvertsToEachTarget(FileFormat target)
    {
        var input = TestImages.SavePng(TestImages.Photo(160, 120), _dir.File("in.png"));

        var output = await _converter.ConvertAsync(input, target, 30, CancellationToken.None);

        Assert.Equal(target, FileTypeSniffer.Detect(output));
    }

    [ToolsFact]
    public async Task JpegFlattensTransparencyOntoWhite()
    {
        var input = TestImages.SavePng(Phase7Images.WithAlpha(), _dir.File("alpha.png"));

        var output = await _converter.ConvertAsync(input, FileFormat.Jpeg, 30, CancellationToken.None);

        var pixels = ImageAnalysis.Load(output);
        Assert.True(pixels.Bgra[0] > 240 && pixels.Bgra[1] > 240 && pixels.Bgra[2] > 240, "transparent area should be white");
    }

    [ToolsFact]
    public async Task AnimationSurvivesConversionToWebP()
    {
        var input = _dir.File("loop.gif");
        File.WriteAllBytes(input, TestImages.Gif(frames: 4, loopCount: 3));

        var output = await _converter.ConvertAsync(input, FileFormat.WebP, 30, CancellationToken.None);

        Assert.True(WebPInfo.IsAnimated(output));
        Assert.Equal(3, WebPInfo.LoopCount(output));
    }

    [ToolsFact]
    public async Task AnimatedWebPIsOptimisedAndStaysAnimated()
    {
        var gif = _dir.File("anim.gif");
        var made = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg), ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=160x120:r=10:d=1.2", gif]);
        Assert.True(made.Succeeded, made.StdErr);
        var webp = await _converter.ConvertAsync(gif, FileFormat.WebP, 5, CancellationToken.None);
        var input = _dir.File("anim.webp");
        File.Move(webp, input);

        var result = await _service.OptimiseImageAsync(input, InPlace with { AllowLarger = true });

        Assert.Equal(FileFormat.WebP, result.OutputFormat);
        Assert.True(WebPInfo.IsAnimated(result.OutputPath));
    }

    [ToolsFact]
    public async Task BmpIsConvertedToJpegNextToIt()
    {
        var input = Phase7Images.Save(new BmpBitmapEncoder(), TestImages.Photo(300, 200), _dir.File("scan.bmp"));

        var result = await _service.OptimiseImageAsync(input, new FileOptimisationRequest());

        Assert.Equal(_dir.File("scan.jpg"), result.OutputPath);
        Assert.Equal(FileFormat.Jpeg, FileTypeSniffer.Detect(result.OutputPath));
        Assert.True(File.Exists(input)); // auto-converted images keep the original by default
    }

    [ToolsFact]
    public async Task AvifIsDecodedThroughFfmpegAndConverted()
    {
        var png = TestImages.SavePng(TestImages.Photo(160, 120), _dir.File("src.png"));
        var avif = await _converter.ConvertAsync(png, FileFormat.Avif, 30, CancellationToken.None);
        var input = _dir.File("phone.avif");
        File.Move(avif, input);

        var result = await _service.OptimiseImageAsync(input, new FileOptimisationRequest());

        Assert.Equal(FileFormat.Jpeg, result.OutputFormat);
        Assert.Equal(new ImageSize(160, 120), ImageDecoding.TryReadSize(result.OutputPath));
    }

    [ToolsFact]
    public async Task ConversionCanBeTurnedOff()
    {
        _settings.Compression.ConvertToJpeg = [];
        var input = Phase7Images.Save(new BmpBitmapEncoder(), TestImages.Photo(64, 64), _dir.File("keep.bmp"));

        await Assert.ThrowsAsync<UnsupportedFormatException>(() => _service.OptimiseImageAsync(input, new FileOptimisationRequest()));
    }

    [ToolsFact]
    public async Task AdaptiveTurnsAPhotoPngIntoJpeg()
    {
        _settings.Compression.AdaptiveImageFormat = true;
        var input = TestImages.SavePng(TestImages.Photo(1200, 900), _dir.File("photo-screenshot.png"));

        var result = await _service.OptimiseImageAsync(input, InPlace);

        Assert.Equal(FileFormat.Jpeg, result.OutputFormat);
        Assert.Equal(_dir.File("photo-screenshot.jpg"), result.OutputPath);
    }

    [ToolsFact]
    public async Task AdaptiveKeepsTransparentPngs()
    {
        _settings.Compression.AdaptiveImageFormat = true;
        var photo = TestImages.Photo(1200, 900);
        var withAlpha = new FormatConvertedBitmap(photo, PixelFormats.Bgra32, null, 0);
        var stride = 1200 * 4;
        var pixels = new byte[stride * 900];
        withAlpha.CopyPixels(pixels, stride, 0);
        pixels[3] = 0; // one transparent pixel is enough
        var input = TestImages.SavePng(BitmapSource.Create(1200, 900, 96, 96, PixelFormats.Bgra32, null, pixels, stride), _dir.File("alpha.png"));

        var result = await _service.OptimiseImageAsync(input, InPlace);

        Assert.Equal(FileFormat.Png, result.OutputFormat);
    }

    [ToolsFact]
    public async Task AdaptiveTurnsAFlatJpegIntoPng()
    {
        _settings.Compression.AdaptiveImageFormat = true;
        var input = TestImages.SaveJpeg(Phase7Images.Flat(2400, 1800, colors: 3), _dir.File("ui.jpg"), quality: 100);
        var entropy = ImageAnalysis.Entropy(ImageAnalysis.Load(input));

        var result = await _service.OptimiseImageAsync(input, InPlace);

        Assert.True(result.OutputFormat == FileFormat.Png, $"stayed {result.OutputFormat}: entropy {entropy:0.00}, JPEG in {new FileInfo(result.BackupPath).Length}, out {result.NewSize}");
    }

    [ToolsFact]
    public async Task DownscalingAFlatPngNeverGrowsIt()
    {
        var input = TestImages.SavePng(Phase7Images.Flat(800, 800, colors: 4), _dir.File("logo.png"));
        var original = new FileInfo(input).Length;

        var result = await _service.OptimiseImageAsync(input, InPlace with { Scale = 0.5 });

        Assert.True(result.NewSize < original, $"{result.NewSize} >= {original}");
        Assert.Equal(new ImageSize(400, 400), ImageDecoding.TryReadSize(result.OutputPath));
    }

    [ToolsFact]
    public async Task ConvertAResultAndRestoreIt()
    {
        var input = TestImages.SavePng(TestImages.Photo(300, 200), _dir.File("pic.png"));
        var optimised = await _service.OptimiseImageAsync(input, InPlace);

        var converted = await _service.ConvertAsync(optimised, FileFormat.WebP);

        Assert.Equal(_dir.File("pic.webp"), converted.OutputPath);
        Assert.True(File.Exists(input)); // the optimised PNG stays
        Assert.True(converted.IsConversion);
        await Assert.ThrowsAsync<UnsupportedFormatException>(() => _service.AdjustAsync(converted, new FileOptimisationRequest { Scale = 0.5 }));

        _service.Restore(converted);
        Assert.False(File.Exists(converted.OutputPath));
    }

    private string MakeClip(string name, double seconds = 2)
    {
        var path = _dir.File(name);
        var result = ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=s=640x360:r=30:d={seconds}"),
            "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"sine=f=440:d={seconds}"),
            "-c:v", "libx264", "-preset", "ultrafast", "-qp", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path,
        ]).GetAwaiter().GetResult();
        Assert.True(result.Succeeded, result.StdErr);
        return path;
    }

    [ToolsTheory]
    [InlineData(FileFormat.Gif, "clip.gif")]
    [InlineData(FileFormat.WebM, "clip.webm")]
    [InlineData(FileFormat.Mp4, "clip-hevc.mp4")]
    public async Task VideoConversions(FileFormat target, string expectedName)
    {
        var input = MakeClip("clip.mp4");
        var described = await _service.DescribeAsync(input);

        var converted = await _service.ConvertAsync(described, target);

        Assert.Equal(_dir.File(expectedName), converted.OutputPath);
        if (target == FileFormat.Gif)
        {
            Assert.True(GifInfo.Read(converted.OutputPath).FrameCount > 10);
        }
        else
        {
            var info = await VideoInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), converted.OutputPath);
            Assert.Equal(target == FileFormat.WebM ? "vp9" : "hevc", info!.VideoCodec);
        }

        Assert.True(File.Exists(input));
    }
}
