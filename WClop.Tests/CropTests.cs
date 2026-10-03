using WClop.Core;
using WClop.Core.Cropping;
using WClop.Core.Images;
using WClop.Core.Ipc;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Video;

namespace WClop.Tests;

/// <summary>The crop shared by the pipeline step, result cards and <c>wclop crop</c>.</summary>
public sealed class CropTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();
    private readonly FileOptimisationService _service;

    public CropTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(_settings, paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("16:9", 16 / 9.0)]
    [InlineData("1.91:1", 1.91)]
    [InlineData("9/16", 9 / 16.0)]
    [InlineData("4x3", 4 / 3.0)]
    public void ParsesRatios(string text, double expected)
    {
        Assert.True(CropSpec.TryParseRatio(text, out var ratio));
        Assert.Equal(expected, ratio, 6);
    }

    [Theory]
    [InlineData("wide")]
    [InlineData("0:9")]
    [InlineData("16:9:1")]
    [InlineData("")]
    public void RejectsBadRatios(string text) => Assert.False(CropSpec.TryParseRatio(text, out _));

    [Theory]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData("1920×1080", 1920, 1080)]
    [InlineData("1280x", 1280, null)]
    [InlineData("x720", null, 720)]
    [InlineData("800", 800, null)]
    public void ParsesSizes(string text, int? width, int? height)
    {
        Assert.True(CropSpec.TryParseSize(text, out var w, out var h));
        Assert.Equal((width, height), (w, h));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("big")]
    [InlineData("1x2x3")]
    [InlineData("1x1")]
    public void RejectsBadSizes(string text) => Assert.False(CropSpec.TryParseSize(text, out _, out _));

    [Fact]
    public void ParseNeedsASizeOrARatio()
    {
        Assert.Null(CropSpec.Parse(null, null, false, out var error));
        Assert.Contains("--size", error);
        Assert.Null(CropSpec.Parse("big", null, false, out error));
        Assert.Contains("isn't a size", error);
        var spec = CropSpec.Parse("1280x", "16:9", true, out error);
        Assert.Null(error);
        Assert.Equal(new CropSpec { Width = 1280, AspectRatio = 16 / 9.0, Smart = true }, spec);
    }

    [Theory]
    [InlineData(1920, 1080, "16:9", null)] // already that shape
    [InlineData(1920, 1080, "1:1", "1080x1080")]
    [InlineData(1920, 1080, "9:16", "608x1080")]
    [InlineData(1000, 1000, "1.91:1", "1000x524")]
    public void RectanglesForPresets(int width, int height, string ratio, string? expected)
    {
        CropSpec.TryParseRatio(ratio, out var value);
        var rectangle = new CropSpec { AspectRatio = value }.Rectangle(new ImageSize(width, height), even: true);
        Assert.Equal(expected, rectangle is var (w, h, _, _) ? $"{w}x{h}" : null);
    }

    [Fact]
    public void PipelineStepAndSpecAgree()
    {
        var step = PipelineCatalog.Compile("crop(width: 300, aspectRatio: \"4:3\", smart: true)").Steps[0];
        Assert.Equal(new CropSpec { Width = 300, AspectRatio = 4 / 3.0, Smart = true }, PipelineRunner.CropOf(step));
    }

    [Fact]
    public void DescribesItself()
    {
        Assert.Equal("16:9", new CropSpec { AspectRatio = 16 / 9.0 }.ToString());
        Assert.Equal("1280×720 (smart)", new CropSpec { Width = 1280, Height = 720, Smart = true }.ToString());
        Assert.Equal("4:3", CropSpec.RatioText(4 / 3.0));
        Assert.Equal("1.5:1", CropSpec.RatioText(3 / 2.0));
    }

    [Fact]
    public void CliOptionsBecomeACrop()
    {
        var builder = new OptimisationRequestBuilder(new OptimiseCommand { Paths = ["x.png"], CropAspect = "1:1", SmartCrop = true });
        Assert.Null(builder.Error);
        Assert.Equal(new CropSpec { AspectRatio = 1, Smart = true }, builder.Crop);
        Assert.True(builder.HasOptions);

        var bad = new OptimisationRequestBuilder(new OptimiseCommand { Paths = ["x.png"], CropSize = "huge" });
        Assert.NotNull(bad.Error);
    }

    [ToolsFact]
    public async Task CropsFromTheOriginalAndRestores()
    {
        var input = TestImages.SavePng(TestImages.Photo(400, 300), _dir.File("photo.png"));
        var start = await _service.DescribeAsync(input);

        var square = await _service.CropAsync(start, new CropSpec { AspectRatio = 1 });
        Assert.Equal(input, square.OutputPath); // in place
        Assert.Equal(new ImageSize(300, 300), ImageDecoding.TryReadSize(square.OutputPath));
        Assert.Equal("300×300", square.Crop);
        Assert.True(square.IsConversion);

        // A second crop starts from the 400×300 original again, not the square.
        var wide = await _service.CropAsync(square, new CropSpec { Width = 400, Height = 100 });
        Assert.Equal(new ImageSize(400, 100), ImageDecoding.TryReadSize(wide.OutputPath));

        var restored = _service.Restore(wide);
        Assert.Equal(new ImageSize(400, 300), ImageDecoding.TryReadSize(restored));
    }

    [ToolsFact]
    public async Task NothingToCropIsSaid()
    {
        var input = TestImages.SavePng(TestImages.Photo(160, 90), _dir.File("wide.png"));
        var start = await _service.DescribeAsync(input);
        var error = await Assert.ThrowsAsync<NothingToCropException>(() => _service.CropAsync(start, new CropSpec { AspectRatio = 16 / 9.0 }));
        Assert.Contains("160×90", error.Message);
    }

    [ToolsFact]
    public async Task SameFolderKeepsTheOriginal()
    {
        var input = TestImages.SavePng(TestImages.Photo(400, 300), _dir.File("keep.png"));
        var start = await _service.DescribeAsync(input);
        var cropped = await _service.CropAsync(start, new CropSpec { Width = 200 }, OutputBehaviour.SameFolder);
        Assert.Equal(_dir.File("keep-cropped.png"), cropped.OutputPath);
        Assert.Equal(new ImageSize(400, 300), ImageDecoding.TryReadSize(input));
        Assert.Equal(new ImageSize(200, 300), ImageDecoding.TryReadSize(cropped.OutputPath));
    }

    [ToolsFact]
    public async Task CropsVideosToEvenSizes()
    {
        var input = _dir.File("clip.mp4");
        var made = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=1",
            "-c:v", "libx264", "-preset", "ultrafast", input,
        ]);
        Assert.True(made.Succeeded, made.StdErr);

        var cropped = await _service.Cropper.CropAsync(input, new CropSpec { AspectRatio = 9 / 16.0 });
        Assert.NotNull(cropped);
        var info = await VideoInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), cropped.Path);
        Assert.Equal((202, 360), (info!.Width, info.Height));
    }
}
