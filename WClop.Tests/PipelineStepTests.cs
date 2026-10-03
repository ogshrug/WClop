using WClop.Core;
using WClop.Core.DropZone;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Tests;

public class PipelineKindsTests
{
    [Theory]
    [InlineData(new string[0], FileFormat.Png, true)]
    [InlineData(new[] { "image" }, FileFormat.Png, true)]
    [InlineData(new[] { "image" }, FileFormat.Mp4, false)]
    [InlineData(new[] { "video", "pdf" }, FileFormat.Pdf, true)]
    [InlineData(new[] { "gif" }, FileFormat.Gif, true)]
    [InlineData(new[] { "gif" }, FileFormat.Png, false)]
    [InlineData(new[] { "image" }, FileFormat.Unknown, false)]
    public void PipelinesApplyToTheirKinds(string[] kinds, FileFormat format, bool applies) =>
        Assert.Equal(applies, new SavedPipeline { Kinds = [.. kinds] }.AppliesTo(format));

    [Fact]
    public void DropPresetsFollowWhatsDragged()
    {
        var settings = new PipelineSettings
        {
            Saved =
            [
                new SavedPipeline { Name = "Pics", Script = "optimise", Kinds = ["image"] },
                new SavedPipeline { Name = "Clips", Script = "removeAudio", Kinds = ["video"] },
                new SavedPipeline { Name = "Any", Script = "stripExif" },
            ],
        };

        Assert.Equal(["Pics", "Any"], Names(DropPresets.WithPipelines(settings, [FileFormat.Png])));
        Assert.Equal(["Pics", "Clips", "Any"], Names(DropPresets.WithPipelines(settings, [FileFormat.Png, FileFormat.Mp4])));
        Assert.Equal(["Pics", "Clips", "Any"], Names(DropPresets.WithPipelines(settings)));

        static IEnumerable<string?> Names(IReadOnlyList<DropPreset> presets) => presets.Where(p => p.Pipeline is not null).Select(p => p.Name);
    }

    [Fact]
    public void ForkStepsAreCheckedUnlessTheyNameASavedPipeline()
    {
        PipelineCatalog.Compile("fork(\"convert(webp) -> move(to: '~/Web/')\")");
        PipelineCatalog.Compile("fork(\"My saved one\")");
        var e = Assert.Throws<PipelineSyntaxException>(() => PipelineCatalog.Compile("fork(\"convrt(webp) -> stripExif\")"));
        Assert.StartsWith("In fork: Unknown step 'convrt'", e.Message);
    }
}

public class SmartCropTests
{
    [Fact]
    public void FindsTheDetailedCorner()
    {
        // 100×100 flat grey with a noisy patch in the bottom-right quarter.
        const int size = 100;
        var pixels = Enumerable.Repeat((byte)128, size * size).ToArray();
        var random = new Random(1);
        for (var y = 60; y < 100; y++)
        for (var x = 60; x < 100; x++)
            pixels[y * size + x] = (byte)random.Next(256);

        var (x0, y0) = ImageInterest.BestWindow(pixels, size, size, new ImageSize(1000, 1000), 400, 400);
        Assert.Equal((600, 600), (x0, y0));
    }

    [Fact]
    public void FlatImagesStayCentred()
    {
        var pixels = Enumerable.Repeat((byte)50, 80 * 40).ToArray();
        Assert.Equal((200, 0), ImageInterest.BestWindow(pixels, 80, 40, new ImageSize(800, 400), 400, 400));
    }
}

public sealed class PipelineStepRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;
    private readonly PipelineRunner _runner;

    public PipelineStepRunnerTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(_settings, paths, ToolLocator.CreateDefault(), new OptimisationDatabase(_dir.File("db.sqlite")));
        _runner = new PipelineRunner(_service, _settings);
    }

    public void Dispose() => _dir.Dispose();

    private Task<PipelineOutcome> Run(string pipeline, string file) =>
        _runner.RunFileAsync("test", PipelineCatalog.Compile(pipeline), file, skipOptimisation: true, new PipelineContext());

    [ToolsFact]
    public async Task ExtractsPdfPages()
    {
        var jpeg = await File.ReadAllBytesAsync(TestImages.SaveJpeg(TestImages.Photo(300, 200), _dir.File("img.jpg")));
        var pdf = _dir.File("Report.pdf");
        await File.WriteAllBytesAsync(pdf, TestPdfs.WithImage(jpeg, 300, 200, 4, pages: 3));

        var outcome = await Run("extractPagesAsImages(format: png, quality: low)", pdf);

        var pages = Directory.GetFiles(_dir.File("Report pages")).Order().ToList();
        Assert.Equal(3, pages.Count);
        Assert.EndsWith("Report page-001.png", pages[0]);
        Assert.Equal(FileFormat.Png, FileTypeSniffer.Detect(pages[0]));
        Assert.Equal(pdf, outcome.Result.OutputPath); // the PDF itself carries on unchanged
        Assert.Contains("3 pages saved as PNG at 100 DPI", outcome.Log[0]);
    }

    [ToolsFact]
    public async Task ExtractsIntoAChosenFolder()
    {
        var pdf = _dir.File("t.pdf");
        await File.WriteAllBytesAsync(pdf, TestPdfs.TextOnly("Hello"));
        var target = _dir.File("out");

        await Run($"extractPagesAsImages(to: \"{target}\\\", dpi: 72)", pdf);

        var page = Assert.Single(Directory.GetFiles(target));
        Assert.Equal(FileFormat.Jpeg, FileTypeSniffer.Detect(page));
        Assert.Equal(new ImageSize(612, 792), ImageDecoding.TryReadSize(page));
    }

    [ToolsFact]
    public async Task ForkWorksOnACopyAndTheMainLineCarriesOn()
    {
        var input = TestImages.SavePng(TestImages.Photo(800, 600), _dir.File("IMG_12.png"));
        var web = _dir.File("web");

        var outcome = await Run(
            $"if(regex: \"^IMG_(\\d+)\") -> fork(\"downscale(width: 200) -> convert(webp) -> move(to: '{web}\\\\')\") -> rename(to: \"photo-$1\")",
            input);

        var forked = Path.Combine(web, "IMG_12.webp");
        Assert.True(File.Exists(forked));
        Assert.Equal(new ImageSize(200, 150), ImageDecoding.TryReadSize(forked));
        Assert.Equal(_dir.File("photo-12.png"), outcome.Result.OutputPath);
        Assert.Equal(new ImageSize(800, 600), ImageDecoding.TryReadSize(outcome.Result.OutputPath));
    }

    [ToolsFact]
    public async Task ForkCanRunASavedPipelineButNotItself()
    {
        _settings.Pipelines.Saved.Add(new SavedPipeline { Name = "Loop", Script = "fork(\"Loop\")" });
        _settings.Pipelines.Saved.Add(new SavedPipeline { Name = "Grey", Script = "stripExif" });
        var input = TestImages.SavePng(TestImages.Photo(100, 100), _dir.File("a.png"));

        var ok = await Run("fork(\"Grey\")", input);
        Assert.Contains(ok.Log, l => l.StartsWith("fork (", StringComparison.Ordinal));

        await Assert.ThrowsAsync<PipelineException>(() => Run("fork(\"Loop\")", input));
    }

    [ToolsFact]
    public async Task SmartCropKeepsTheDetail()
    {
        // A flat image with a busy block on the right: a square crop should slide right.
        var image = TestImages.Flat(900, 300);
        var busy = TestImages.Photo(300, 300);
        var canvas = new System.Windows.Media.DrawingVisual();
        using (var context = canvas.RenderOpen())
        {
            context.DrawImage(image, new System.Windows.Rect(0, 0, 900, 300));
            context.DrawImage(busy, new System.Windows.Rect(600, 0, 300, 300));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(900, 300, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var input = TestImages.SavePng(bitmap, _dir.File("wide.png"));
        var centreCrop = TestImages.SavePng(bitmap, _dir.File("wide2.png"));

        var smart = await Run("crop(aspectRatio: \"1:1\", smart: true)", input);
        var plain = await Run("crop(aspectRatio: \"1:1\")", centreCrop);

        // The smart crop holds the noisy block, so it compresses far worse than the flat centre crop.
        Assert.Contains("(smart)", smart.Log[0]);
        Assert.True(new FileInfo(smart.Result.OutputPath).Length > 2 * new FileInfo(plain.Result.OutputPath).Length);
    }
}

public sealed class ToolManifestTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void VerifiesTheManifestTheInstallerWrites()
    {
        Directory.CreateDirectory(_dir.File("sub"));
        File.WriteAllText(_dir.File("a.exe"), "tool a");
        File.WriteAllText(_dir.File("sub/b.dll"), "tool b");
        Assert.Null(ToolManifest.Verify(_dir.Path)); // no manifest: a dev build

        ToolManifest.Write(_dir.Path);
        Assert.Empty(ToolManifest.Verify(_dir.Path)!);

        File.WriteAllText(_dir.File("a.exe"), "tampered");
        File.Delete(_dir.File("sub/b.dll"));
        Assert.Equal(["changed: a.exe", "missing: sub/b.dll"], ToolManifest.Verify(_dir.Path)!);
    }

    [Fact]
    public void ReadsThePackagedManifest()
    {
        var packaged = Path.Combine(TestSetup.RepoRoot, "artifacts", "publish", "tools");
        if (!File.Exists(Path.Combine(packaged, ToolManifest.FileName)))
            return; // only after scripts/package.ps1
        Assert.Empty(ToolManifest.Verify(packaged)!);
    }
}

public sealed class WebPSizeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Sizes must read without a WebP codec (Windows Server, PCs without the Web Media Extensions).</summary>
    [ToolsTheory]
    [InlineData("lossy", new[] { "-c:v", "libwebp", "-quality", "80" }, "rgb24")]
    [InlineData("lossless", new[] { "-c:v", "libwebp", "-lossless", "1" }, "rgb24")]
    [InlineData("alpha", new[] { "-c:v", "libwebp", "-quality", "80" }, "rgba")]
    public async Task ReadsEveryKindOfWebPHeader(string name, string[] codec, string pixelFormat)
    {
        var output = _dir.File(name + ".webp");
        var made = await ProcessRunner.RunAsync(ToolLocator.CreateDefault().Require(Tool.Ffmpeg),
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=734x412", "-frames:v", "1",
            "-pix_fmt", pixelFormat == "rgba" ? "yuva420p" : "yuv420p", .. codec, output,
        ]);
        Assert.True(made.Succeeded, made.StdErr);

        Assert.Equal((734, 412), WebPInfo.Size(output));
    }
}
