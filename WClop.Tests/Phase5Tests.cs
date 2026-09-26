using System.Net;
using WClop.Core;
using WClop.Core.Clipboard;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using Decision = WClop.Core.Clipboard.ClipboardDecision;

namespace WClop.Tests;

public class DownscaleSteppingTests
{
    [Fact]
    public void StepsLikeClop()
    {
        var steps = new List<double>();
        for (var scale = 1.0; steps.Count < 8; )
        {
            scale = ImageResizer.NextDownscaleStep(scale);
            steps.Add(scale);
        }

        Assert.Equal([0.75, 0.5, 0.4, 0.3, 0.2, 0.1, 0.1, 0.1], steps);
    }

    [Theory]
    [InlineData(1920, 1080, 0.75, 1440, 810)]
    [InlineData(1001, 501, 0.5, 500, 250)]
    [InlineData(3, 3, 0.1, 2, 2)]
    public void ScaledSizesAreEven(int w, int h, double scale, int ew, int eh) =>
        Assert.Equal(new ImageSize(ew, eh), ImageResizer.ScaledSize(new ImageSize(w, h), scale));
}

public class ClipboardTextTests
{
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    [Fact]
    public void DataUri()
    {
        var target = ClipboardText.Classify($"data:image/png;base64,{TinyPngBase64}");

        var image = Assert.IsType<TextTarget.Base64Image>(target);
        Assert.Equal(FileFormat.Png, image.Format);
    }

    [Fact]
    public void DataUriInsideCss()
    {
        var target = ClipboardText.Classify($".logo {{ background: url(\"data:image/png;base64,{TinyPngBase64}\") no-repeat; }}");

        Assert.IsType<TextTarget.Base64Image>(target);
    }

    [Fact]
    public void DataUriThatIsNotAnImageIsNothing() =>
        Assert.IsType<TextTarget.Nothing>(ClipboardText.Classify("data:image/png;base64,aGVsbG8gd29ybGQ="));

    [Theory]
    [InlineData(@"C:\Pics\a.png")]
    [InlineData("\"C:\\Pics\\a.png\"")]
    [InlineData("  C:\\Pics\\a.png  ")]
    public void ExistingFilePath(string text) =>
        Assert.Equal(new TextTarget.FilePath(@"C:\Pics\a.png"), ClipboardText.Classify(text, _ => true));

    [Fact]
    public void MissingFilePathIsNothing() =>
        Assert.IsType<TextTarget.Nothing>(ClipboardText.Classify(@"C:\Pics\gone.png", _ => false));

    [Theory]
    [InlineData("https://example.com/cat.png")]
    [InlineData("http://example.com/a?b=c")]
    public void WebUrl(string text) => Assert.IsType<TextTarget.WebUrl>(ClipboardText.Classify(text));

    [Theory]
    [InlineData("")]
    [InlineData("just some words")]
    [InlineData("ftp://example.com/a.png")]
    [InlineData("https://example.com/a.png\nhttps://example.com/b.png")]
    public void OtherTextIsNothing(string text) => Assert.IsType<TextTarget.Nothing>(ClipboardText.Classify(text));
}

public class OnDemandPolicyTests
{
    private static ClipboardSnapshot Snap(params string[] formats) => new() { Formats = formats };

    [Fact]
    public void DenyListDoesNotApplyOnDemand()
    {
        // Automatically this is left alone; when the user asks, the image is taken.
        var snapshot = Snap("Rich Text Format", "CF_DIB");

        Assert.IsType<Decision.Ignore>(ClipboardPolicy.Decide(snapshot, new ClipboardSettings()));
        Assert.IsType<Decision.ReadImage>(ClipboardPolicy.DecideOnDemand(snapshot));
    }

    [Fact]
    public void CopiedImageFileIsTakenEvenWithImagePathsOff()
    {
        var snapshot = Snap("CF_HDROP") with { Files = [@"C:\a.txt", @"C:\b.jpg"] };

        Assert.Equal(new Decision.ImageFile(@"C:\b.jpg"), ClipboardPolicy.DecideOnDemand(snapshot));
    }

    [Fact]
    public void CutFilesStillLeftAlone()
    {
        var snapshot = Snap("CF_HDROP") with { Files = [@"C:\b.jpg"], PreferredDropEffect = 2 };

        Assert.IsType<Decision.Ignore>(ClipboardPolicy.DecideOnDemand(snapshot));
    }

    [Fact]
    public void TextIsReadForUrlsAndBase64() =>
        Assert.Equal(new Decision.ReadText("CF_UNICODETEXT"), ClipboardPolicy.DecideOnDemand(Snap("CF_UNICODETEXT", "CF_TEXT")));

    [Fact]
    public void NothingUsable() =>
        Assert.IsType<Decision.Ignore>(ClipboardPolicy.DecideOnDemand(Snap("CF_LOCALE")));
}

public class MediaDownloaderTests
{
    private sealed class StubHandler(HttpStatusCode status, string contentType, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task DownloadsAndNamesByRealFormat()
    {
        using var dir = new TempDir();
        var jpeg = File.ReadAllBytes(TestImages.SaveJpeg(TestImages.Photo(32, 32), dir.File("src.jpg")));
        var downloader = new MediaDownloader(new AppPaths(dir.File("cache")),
            new HttpClient(new StubHandler(HttpStatusCode.OK, "application/octet-stream", jpeg)));

        var path = await downloader.DownloadAsync(new Uri("https://example.com/pics/cat%20photo.png"), CancellationToken.None);

        Assert.Equal(".jpg", Path.GetExtension(path)); // content wins over the URL's extension
        Assert.StartsWith("cat photo-", Path.GetFileName(path));
        Assert.Equal(jpeg, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WebPagesAreRejected()
    {
        using var dir = new TempDir();
        var downloader = new MediaDownloader(new AppPaths(dir.File("cache")),
            new HttpClient(new StubHandler(HttpStatusCode.OK, "text/html", "<html></html>"u8.ToArray())));

        await Assert.ThrowsAsync<UnsupportedFormatException>(() =>
            downloader.DownloadAsync(new Uri("https://example.com/"), CancellationToken.None));
    }

    [Fact]
    public async Task HttpErrorsSurface()
    {
        using var dir = new TempDir();
        var downloader = new MediaDownloader(new AppPaths(dir.File("cache")),
            new HttpClient(new StubHandler(HttpStatusCode.NotFound, "image/png", [])));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            downloader.DownloadAsync(new Uri("https://example.com/missing.png"), CancellationToken.None));
    }
}

/// <summary>Downscale and adjust with the real tools.</summary>
public sealed class AdjustTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;
    private readonly FileOptimisationService _service;

    public AdjustTests()
    {
        _paths = new AppPaths(_dir.File("cache"));
        _paths.EnsureCreated();
        _service = new FileOptimisationService(
            new AppSettings(), _paths, ToolLocator.CreateDefault(), new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose() => _dir.Dispose();

    private static readonly FileOptimisationRequest InPlace = new() { Behaviour = OutputBehaviour.InPlace };

    [ToolsFact]
    public async Task DownscaleOnFirstOptimise()
    {
        var input = TestImages.SavePng(TestImages.Photo(400, 300), _dir.File("shot.png"));

        var result = await _service.OptimiseImageAsync(input, InPlace with { Scale = 0.5 });

        Assert.Equal(new ImageSize(200, 150), ImageDecoding.TryReadSize(result.OutputPath));
        Assert.Equal(0.5, result.Scale);
    }

    [ToolsFact]
    public async Task AdjustmentsAlwaysStartFromTheOriginal()
    {
        var input = TestImages.SavePng(TestImages.Photo(400, 300), _dir.File("shot.png"));
        var first = await _service.OptimiseImageAsync(input, InPlace);

        var half = await _service.AdjustAsync(first, new FileOptimisationRequest { Scale = 0.5 });
        var threeQuarters = await _service.AdjustAsync(half, new FileOptimisationRequest { Scale = 0.75 });

        Assert.Equal(input, threeQuarters.OutputPath);
        // 75% of the original, not 75% of the 50% version.
        Assert.Equal(ImageResizer.ScaledSize(new ImageSize(400, 300), 0.75), ImageDecoding.TryReadSize(input));
        Assert.Equal(MarkerStatus.Optimised, _service.Markers.Get(input));
        Assert.Equal(first.BackupPath, threeQuarters.BackupPath);
    }

    [ToolsFact]
    public async Task AggressiveKeepsTheCurrentScale()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(400, 300), _dir.File("photo.jpg"));
        var first = await _service.OptimiseImageAsync(input, InPlace);
        var half = await _service.AdjustAsync(first, new FileOptimisationRequest { Scale = 0.5 });

        var aggressive = await _service.AdjustAsync(half, new FileOptimisationRequest { Factor = 64 });

        Assert.Equal(0.5, aggressive.Scale);
        Assert.Equal(64, aggressive.Factor);
        Assert.True(aggressive.NewSize < half.NewSize);
    }

    [ToolsFact]
    public async Task DownscaleKeepsOrientation()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(400, 300), _dir.File("rotated.jpg"));
        var exiftool = ToolLocator.CreateDefault().Require(Tool.Exiftool);
        await ProcessRunner.RunAsync(exiftool, ["-q", "-overwrite_original", "-Orientation#=6", input]);

        var result = await _service.OptimiseImageAsync(input, InPlace with { Scale = 0.5 });

        var tags = await ProcessRunner.RunAsync(exiftool, ["-s", "-s", "-s", "-Orientation#", result.OutputPath]);
        Assert.Equal("6", tags.StdOut.Trim());
    }

    [ToolsFact]
    public async Task DownscalingAnAnimatedGifKeepsItsFrames()
    {
        var input = TestImages.SaveAnimatedGif(_dir.File("anim.gif"));

        var result = await _service.OptimiseImageAsync(input, InPlace with { Scale = 0.5, AllowLarger = true });

        var info = GifInfo.Read(result.OutputPath);
        Assert.Equal(12, info.FrameCount);
        Assert.Equal((60, 44), (info.Width, info.Height));
    }

    [ToolsFact]
    public async Task ClipboardResultsStayInTheWorkingFolder()
    {
        var input = TestImages.SavePng(TestImages.Photo(400, 300), _dir.File("clip.png"));
        var first = await _service.OptimiseImageAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.Temporary });

        var adjusted = await _service.AdjustAsync(first, new FileOptimisationRequest { Scale = 0.5 });

        Assert.StartsWith(_paths.WorkDir, adjusted.OutputPath);
        Assert.Equal(new ImageSize(200, 150), ImageDecoding.TryReadSize(adjusted.OutputPath));
    }
}
