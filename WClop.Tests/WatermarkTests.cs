using System.Windows.Media;
using System.Windows.Media.Imaging;
using WClop.Core;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Video;

namespace WClop.Tests;

public sealed class WatermarkTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly PipelineRunner _runner;
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();

    public WatermarkTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _runner = new PipelineRunner(
            new FileOptimisationService(_settings, paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite"))), _settings);
    }

    public void Dispose() => _dir.Dispose();

    private Task<PipelineOutcome> Run(string pipeline, string file) =>
        _runner.RunFileAsync("test", PipelineCatalog.Compile(pipeline), file, skipOptimisation: true, new PipelineContext());

    private static BitmapSource Solid(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    /// <summary>(B, G, R, A) at a pixel.</summary>
    private static (byte B, byte G, byte R, byte A) Pixel(string path, int x, int y)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    [ToolsFact]
    public async Task PlacesAScaledWatermarkInTheCorner()
    {
        var input = TestImages.SavePng(Solid(400, 300, 0, 0, 0), _dir.File("base.png"));
        var logo = TestImages.SavePng(Solid(200, 100, 255, 255, 255), _dir.File("logo.png"));

        // 25% of 400 px wide = 100×50, 20 px from the bottom-right corner: x 280–379, y 230–279.
        var outcome = await Run($"watermark(image: \"{logo}\", scale: 25%)", input);

        var output = outcome.Result.OutputPath;
        Assert.Equal(new ImageSize(400, 300), ImageDecoding.TryReadSize(output));
        Assert.True(Pixel(output, 330, 255).R > 240, "inside the watermark");
        Assert.True(Pixel(output, 10, 10).R < 15, "outside it");
        Assert.True(Pixel(output, 390, 290).R < 15, "in the margin");
        Assert.Contains("watermarked (bottomRight)", outcome.Log[0]);
    }

    [ToolsFact]
    public async Task OpacityBlends()
    {
        var input = TestImages.SavePng(Solid(400, 300, 0, 0, 0), _dir.File("base.png"));
        var logo = TestImages.SavePng(Solid(100, 100, 255, 255, 255), _dir.File("logo.png"));

        var outcome = await Run($"watermark(image: \"{logo}\", position: center, opacity: 50%, scale: 0.5)", input);

        Assert.InRange((int)Pixel(outcome.Result.OutputPath, 200, 150).R, 110, 145);
    }

    [ToolsFact]
    public async Task KeepsTransparencyOfBothImages()
    {
        var input = TestImages.SavePng(Solid(300, 300, 0, 0, 255, a: 0), _dir.File("clear.png"));
        var logo = TestImages.SavePng(Solid(100, 100, 255, 0, 0), _dir.File("logo.png"));

        var outcome = await Run($"watermark(image: \"{logo}\", position: topLeft, margin: 0, scale: 50%)", input);

        Assert.Equal(255, Pixel(outcome.Result.OutputPath, 50, 50).A);
        Assert.Equal(0, Pixel(outcome.Result.OutputPath, 250, 250).A);
    }

    [ToolsFact]
    public async Task UsesTheDefaultWatermarkFromSettings()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(640, 480), _dir.File("photo.jpg"));
        _settings.Pipelines.DefaultWatermark = TestImages.SavePng(Solid(64, 64, 255, 255, 255), _dir.File("default.png"));

        var outcome = await Run("watermark", input);

        Assert.Equal(FileFormat.Jpeg, FileTypeSniffer.Detect(outcome.Result.OutputPath));
        Assert.True(outcome.Changed);
    }

    [ToolsFact]
    public async Task ExplainsAMissingWatermark()
    {
        var input = TestImages.SavePng(Solid(100, 100, 0, 0, 0), _dir.File("a.png"));
        var none = await Assert.ThrowsAsync<PipelineException>(() => Run("watermark", input));
        Assert.Contains("set a default in Settings", none.Message);
        var missing = await Assert.ThrowsAsync<PipelineException>(() => Run($"watermark(\"{_dir.File("nope.png")}\")", input));
        Assert.Contains("not found", missing.Message);
    }

    [ToolsFact]
    public async Task WatermarksEveryFrameOfVideosAndGifs()
    {
        var logo = TestImages.SavePng(Solid(80, 40, 255, 255, 255), _dir.File("logo.png"));
        var video = _dir.File("clip.mp4");
        var made = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=2",
            "-f", "lavfi", "-i", "sine=d=2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", video,
        ]);
        Assert.True(made.Succeeded, made.StdErr);
        // .NET's GIF encoder only writes one frame, so the animation comes from ffmpeg.
        var gif = _dir.File("anim.gif");
        var gifMade = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
            ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=10:d=0.8", gif]);
        Assert.True(gifMade.Succeeded, gifMade.StdErr);
        var framesIn = GifInfo.Read(gif).FrameCount;
        Assert.Equal(8, framesIn);

        var videoOut = await Run($"watermark(\"{logo}\")", video);
        var gifOut = await Run($"watermark(\"{logo}\", position: topRight)", gif);

        var info = await VideoInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), videoOut.Result.OutputPath);
        Assert.NotNull(info);
        Assert.Equal((640, 360), (info.Width, info.Height));
        Assert.True(info.HasAudio, "audio is kept");
        Assert.Equal(FileFormat.Gif, FileTypeSniffer.Detect(gifOut.Result.OutputPath));
        Assert.Equal(framesIn, GifInfo.Read(gifOut.Result.OutputPath).FrameCount);
    }

    [Theory]
    [InlineData("bottomRight", "W-w-20", "H-h-20")]
    [InlineData("bottomLeft", "20", "H-h-20")]
    [InlineData("topRight", "W-w-20", "20")]
    [InlineData("topLeft", "20", "20")]
    [InlineData("center", "(W-w)/2", "(H-h)/2")]
    public void Placements(string position, string x, string y) =>
        Assert.Equal((x, y), PipelineRunner.WatermarkPlacement(position, 20));

    [Fact]
    public void ChecksItsArguments()
    {
        var step = PipelineCatalog.Compile("watermark(\"logo.png\", position: topLeft, opacity: 0.4, scale: 20%, margin: 8)").Steps[0];
        Assert.Equal("logo.png", step.Get<string>("image"));
        Assert.Equal(0.4, step.Get<double>("opacity"));
        Assert.Equal(0.2, step.Get<double>("scale"));
        Assert.Contains("should be one of", Assert.Throws<PipelineSyntaxException>(() => PipelineCatalog.Compile("watermark(position: middle)")).Message);
    }

    [Fact]
    public void TheDropZoneOffersWatermarkOnceADefaultIsSet()
    {
        var pipelines = new PipelineSettings();
        Assert.DoesNotContain(WClop.Core.DropZone.DropPresets.WithPipelines(pipelines), p => p.Name == "Watermark");

        pipelines.DefaultWatermark = @"C:\logo.png";
        var preset = Assert.Single(WClop.Core.DropZone.DropPresets.WithPipelines(pipelines), p => p.Name == "Watermark");
        Assert.Equal("watermark", preset.Pipeline);
        Assert.Contains(WClop.Core.DropZone.DropPresets.WithPipelines(pipelines, [FileFormat.Png]), p => p.Name == "Watermark");
        Assert.DoesNotContain(WClop.Core.DropZone.DropPresets.WithPipelines(pipelines, [FileFormat.Pdf]), p => p.Name == "Watermark");

        // A saved pipeline called Watermark replaces the built-in one rather than doubling up.
        pipelines.Saved.Add(new SavedPipeline { Name = "Watermark", Script = "watermark(opacity: 50%)" });
        Assert.Single(WClop.Core.DropZone.DropPresets.WithPipelines(pipelines), p => p.Name == "Watermark");
    }

    [ToolsFact]
    public async Task TheWatermarkPresetOnlyWatermarks()
    {
        _settings.Pipelines.DefaultWatermark = TestImages.SavePng(Solid(64, 64, 255, 255, 255), _dir.File("logo.png"));
        var library = new PipelineLibrary(_settings, _runner);
        var resolved = library.Resolve("watermark");
        Assert.True(resolved.SkipOptimisation); // no optimisation, just the watermark
        Assert.Equal(["watermark"], resolved.Compiled.Steps.Select(s => s.Name));
    }

    [ToolsFact]
    public async Task UsesTheWatermarkSettingsForWhatTheStepLeavesOut()
    {
        _settings.Pipelines.DefaultWatermark = TestImages.SavePng(Solid(100, 100, 255, 255, 255), _dir.File("logo.png"));
        _settings.Pipelines.WatermarkPosition = "topLeft";
        _settings.Pipelines.WatermarkScale = 0.25;
        _settings.Pipelines.WatermarkMargin = 0;
        _settings.Pipelines.WatermarkOpacity = 0.5;

        // 25% of 400 = a 100 px square in the top-left corner at half opacity.
        var fromSettings = await Run("watermark", TestImages.SavePng(Solid(400, 300, 0, 0, 0), _dir.File("a.png")));
        Assert.InRange((int)Pixel(fromSettings.Result.OutputPath, 50, 50).R, 110, 145);
        Assert.True(Pixel(fromSettings.Result.OutputPath, 350, 250).R < 15);

        // What the step says wins over the settings.
        var overridden = await Run("watermark(position: bottomRight, opacity: 100%)", TestImages.SavePng(Solid(400, 300, 0, 0, 0), _dir.File("b.png")));
        Assert.True(Pixel(overridden.Result.OutputPath, 350, 250).R > 240);
        Assert.True(Pixel(overridden.Result.OutputPath, 50, 50).R < 15);
    }

    [ToolsFact]
    public async Task OutOfRangeSettingsAreClamped()
    {
        _settings.Pipelines.DefaultWatermark = TestImages.SavePng(Solid(50, 50, 255, 255, 255), _dir.File("logo.png"));
        _settings.Pipelines.WatermarkPosition = "sideways";
        _settings.Pipelines.WatermarkScale = 7;
        _settings.Pipelines.WatermarkOpacity = -1;
        _settings.Pipelines.WatermarkMargin = -5;

        var outcome = await Run("watermark", TestImages.SavePng(Solid(200, 200, 0, 0, 0), _dir.File("c.png")));
        Assert.Contains("watermarked (bottomRight)", outcome.Log[0]);
    }
}
