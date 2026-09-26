using WClop.Core;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Tests;

/// <summary>End-to-end tests that run the real tools.</summary>
public sealed class ImageOptimisationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;
    private readonly ImageOptimiser _optimiser;

    public ImageOptimisationTests()
    {
        _paths = new AppPaths(_dir.File("cache"));
        _paths.EnsureCreated();
        var tools = ToolLocator.CreateDefault();
        _service = new FileOptimisationService(_settings, _paths, tools, new OptimisationDatabase(_dir.File("db.sqlite")));
        _optimiser = new ImageOptimiser(tools, _paths);
    }

    public void Dispose() => _dir.Dispose();

    [ToolsFact]
    public async Task PngGetsSmallerAndStillDecodes()
    {
        var input = TestImages.SavePng(TestImages.Photo(), _dir.File("photo.png"));

        var result = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions());

        Assert.Equal(FileFormat.Png, result.OutputFormat);
        Assert.True(result.OutputSize < result.InputSize, $"{result.OutputSize} >= {result.InputSize}");
        Assert.Equal(new ImageSize(400, 300), ImageDecoding.Verify(result.Path));
    }

    [ToolsFact]
    public async Task JpegGetsSmaller()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(), _dir.File("photo.jpg"), quality: 100);

        var result = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions());

        Assert.Equal(FileFormat.Jpeg, result.OutputFormat);
        Assert.True(result.OutputSize < result.InputSize);
    }

    [ToolsFact]
    public async Task HigherFactorGivesSmallerJpeg()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(), _dir.File("photo.jpg"), quality: 100);

        var normal = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions { Factor = 30 });
        var aggressive = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions { Factor = 64 });

        Assert.True(aggressive.OutputSize < normal.OutputSize);
    }

    [ToolsFact]
    public async Task JpegNamedPngIsRoutedToJpegoptim()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(), _dir.File("lying.png"), quality: 100);

        var result = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions());

        Assert.Equal(FileFormat.Jpeg, result.InputFormat);
        Assert.Equal(".jpg", Path.GetExtension(result.Path));
    }

    [ToolsFact]
    public async Task AnimatedGifStaysAnimated()
    {
        var input = TestImages.SaveAnimatedGif(_dir.File("anim.gif"));

        var result = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions { AllowLarger = true });

        Assert.Equal(12, GifInfo.Read(result.Path).FrameCount);
    }

    [ToolsFact]
    public async Task HighFactorDropsGifFramesButKeepsAnimation()
    {
        var input = TestImages.SaveAnimatedGif(_dir.File("anim.gif"));

        var result = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions { Factor = 85, AllowLarger = true });

        // Every 4th frame dropped: 12 → 9.
        Assert.Equal(9, GifInfo.Read(result.Path).FrameCount);
    }

    [ToolsFact]
    public async Task GifLoopCountIsPreserved()
    {
        var input = _dir.File("loop.gif");
        File.WriteAllBytes(input, TestImages.Gif(frames: 4, loopCount: 3));

        var result = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions { AllowLarger = true });

        Assert.Equal(3, GifInfo.Read(result.Path).LoopCount);
    }

    [ToolsFact]
    public async Task OutputThatIsNotSmallerIsRejected()
    {
        var input = TestImages.SavePng(TestImages.Flat(), _dir.File("tiny.png"));
        await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions { AllowLarger = true }); // warm-up: must not throw

        // Optimising pngquant's own output again can't win.
        var once = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions { AllowLarger = true });
        await Assert.ThrowsAsync<NotSmallerException>(() =>
            _optimiser.OptimiseAsync(once.Path, new ImageOptimiseOptions()));
    }

    [ToolsFact]
    public async Task NonImageIsUnsupported()
    {
        var input = _dir.File("notes.txt");
        File.WriteAllText(input, "hello");

        await Assert.ThrowsAsync<UnsupportedFormatException>(() =>
            _optimiser.OptimiseAsync(input, new ImageOptimiseOptions()));
    }

    [ToolsFact]
    public async Task StripsMetadataButKeepsOrientation()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(), _dir.File("tagged.jpg"), quality: 100);
        var exiftool = ToolLocator.CreateDefault().Require(Tool.Exiftool);
        await ProcessRunner.RunAsync(exiftool, ["-q", "-overwrite_original", "-Orientation#=6", "-Artist=Someone", input]);

        var result = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions());

        var tags = await ProcessRunner.RunAsync(exiftool, ["-s", "-s", "-s", "-Orientation#", "-Artist", result.Path]);
        Assert.Equal("6", tags.StdOut.Trim());
    }

    [ToolsFact]
    public async Task ServiceOptimisesInPlaceWithBackupMarkerAndDates()
    {
        var input = TestImages.SavePng(TestImages.Photo(), _dir.File("shot.png"));
        var mtime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(input, mtime);
        var originalBytes = File.ReadAllBytes(input);

        var result = await _service.OptimiseImageAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.InPlace });

        Assert.Equal(input, result.OutputPath);
        Assert.True(new FileInfo(input).Length < originalBytes.Length);
        Assert.Equal(originalBytes, File.ReadAllBytes(result.BackupPath));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(input));
        Assert.Equal(MarkerStatus.Optimised, _service.Markers.Get(input));

        // A second automatic pass must skip it.
        await Assert.ThrowsAsync<AlreadyOptimisedException>(() =>
            _service.OptimiseImageAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.InPlace }));
    }

    [ToolsFact]
    public async Task ServiceSameFolderMarksSourceAsProcessed()
    {
        var input = TestImages.SavePng(TestImages.Photo(), _dir.File("shot.png"));

        var result = await _service.OptimiseImageAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.SameFolder });

        Assert.Equal(_dir.File("shot-optimised.png"), result.OutputPath);
        Assert.Equal(MarkerStatus.OriginalProcessed, _service.Markers.Get(input));
        Assert.Equal(MarkerStatus.Optimised, _service.Markers.Get(result.OutputPath));
    }

    [ToolsFact]
    public async Task ServiceReusesCachedResultForIdenticalContent()
    {
        var first = TestImages.SavePng(TestImages.Photo(), _dir.File("a.png"));
        File.Copy(first, _dir.File("b.png"));

        var a = await _service.OptimiseImageAsync(first, new FileOptimisationRequest { Behaviour = OutputBehaviour.SameFolder });
        var b = await _service.OptimiseImageAsync(_dir.File("b.png"), new FileOptimisationRequest { Behaviour = OutputBehaviour.SameFolder });

        Assert.False(a.FromCache);
        Assert.True(b.FromCache);
        Assert.Equal(File.ReadAllBytes(a.OutputPath), File.ReadAllBytes(b.OutputPath));
    }

    [ToolsFact]
    public async Task ServiceMarksIncompressibleFilesSoTheyAreNotRetried()
    {
        var input = TestImages.SavePng(TestImages.Photo(), _dir.File("shot.png"));
        var once = await _optimiser.OptimiseAsync(input, new ImageOptimiseOptions());
        var alreadySmall = _dir.File("small.png");
        File.Copy(once.Path, alreadySmall);

        await Assert.ThrowsAsync<NotSmallerException>(() =>
            _service.OptimiseImageAsync(alreadySmall, new FileOptimisationRequest { Behaviour = OutputBehaviour.InPlace }));

        Assert.Equal(MarkerStatus.Optimised, _service.Markers.Get(alreadySmall));
    }
}
