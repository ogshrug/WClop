using System.Globalization;
using WClop.Core;
using WClop.Core.Batch;
using WClop.Core.DropZone;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Video;

namespace WClop.Tests;

public class TargetSizeMathTests
{
    [Theory]
    [InlineData(64, 400, 100, 100)]  // more than 3× over → straight to 100
    [InlineData(64, 200, 100, 94)]   // more than 1.5× → +30
    [InlineData(64, 120, 100, 82)]   // otherwise +18
    [InlineData(90, 120, 100, 100)]  // capped
    public void ImageFactorProbes(int factor, long size, long target, int expected) =>
        Assert.Equal(expected, TargetSizeMath.NextImageFactor(factor, size, target));

    [Fact]
    public void ImageScaleShrinksInProportionButNotBelow20Percent()
    {
        Assert.Equal(Math.Round(Math.Sqrt(0.25) * 0.92, 3), TargetSizeMath.NextImageScale(1, 400, 100));
        Assert.Equal(0.2, TargetSizeMath.NextImageScale(0.25, 1000, 10));
    }

    [Fact]
    public void VideoBitrateLeavesRoomForContainerAndAudio()
    {
        // 10 MB over 60 s: 10485760 × 8 × 0.93 / 60 / 1000 ≈ 1300 kbps, minus 128 for audio.
        Assert.InRange(TargetSizeMath.VideoKbps(10L * 1024 * 1024, 60, hasAudio: true), 1170, 1175);
        Assert.Equal(40, TargetSizeMath.VideoKbps(10_000, 600, hasAudio: true));
    }

    [Fact]
    public void BitsPerPixel()
    {
        Assert.Equal(0.04, TargetSizeMath.BitsPerPixel(4977, 1920, 1080, 60), 3);
    }

    [Fact]
    public void AudioBitrateNeverAboveSource()
    {
        Assert.Equal(128, TargetSizeMath.AudioKbps(100L * 1024 * 1024, 60, 128));
        Assert.InRange(TargetSizeMath.AudioKbps(1024 * 1024, 60, null), 130, 135);
    }

    [Fact]
    public void TargetPresetsSetTheTarget()
    {
        var request = DropPresets.All.Single(p => p.Name == "Under 10 MB").Apply(new FileOptimisationRequest());

        Assert.Equal(10L * 1024 * 1024, request.TargetBytes);
    }
}

/// <summary>Fits real files under a size.</summary>
public sealed class TargetSizeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();

    public TargetSizeTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(_settings, paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose() => _dir.Dispose();

    [ToolsFact]
    public async Task ImageFitsUnderATightTarget()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(2000, 1500), _dir.File("big.jpg"), quality: 100);
        const long target = 60 * 1024;

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest { TargetBytes = target, Behaviour = OutputBehaviour.InPlace });

        Assert.True(result.NewSize <= target, $"{result.NewSize} > {target}");
        Assert.False(result.MissedTarget);
        Assert.Equal(target, result.TargetBytes);
        // Only the winning attempt is left behind in the working folder.
        Assert.Empty(Directory.GetFiles(_service.Paths.Images));
    }

    [ToolsFact]
    public async Task ImpossibleTargetKeepsTheSmallestAndSaysSo()
    {
        var input = TestImages.SavePng(TestImages.Photo(400, 300), _dir.File("small.png"));

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest { TargetBytes = 200, Behaviour = OutputBehaviour.InPlace });

        Assert.True(result.MissedTarget);
        Assert.True(result.NewSize < result.OldSize);
    }

    [ToolsFact]
    public async Task VideoFitsUnderTarget()
    {
        var input = _dir.File("clip.mp4");
        var made = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=6",
            "-f", "lavfi", "-i", "sine=f=440:d=6", "-c:v", "libx264", "-preset", "ultrafast", "-qp", "12",
            "-c:a", "aac", "-shortest", input,
        ]);
        Assert.True(made.Succeeded, made.StdErr);
        const long target = 400 * 1024;

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest { TargetBytes = target, Behaviour = OutputBehaviour.Temporary });

        Assert.True(result.NewSize <= target, $"{result.NewSize} > {target}");
        var info = await VideoInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), result.OutputPath);
        Assert.InRange(info!.Duration.TotalSeconds, 5.8, 6.2);
    }

    [ToolsFact]
    public async Task PdfFitsAtTheSharpestDpiThatWorks()
    {
        var jpeg = File.ReadAllBytes(TestImages.SaveJpeg(TestImages.Photo(2400, 1800), _dir.File("page.jpg"), quality: 95));
        var input = _dir.File("scan.pdf");
        File.WriteAllBytes(input, TestPdfs.WithImage(jpeg, 2400, 1800, drawnInches: 8, pages: 2));
        var target = new FileInfo(input).Length / 4;

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest { TargetBytes = target, Behaviour = OutputBehaviour.InPlace });

        Assert.True(result.NewSize <= target, $"{result.NewSize} > {target}");
        Assert.NotNull(result.Dpi);
    }

    [ToolsFact]
    public async Task AudioFitsUnderTarget()
    {
        var input = _dir.File("talk.wav");
        var made = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
            ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=300:d=30", "-c:a", "pcm_s16le", input]);
        Assert.True(made.Succeeded, made.StdErr);
        const long target = 250 * 1024;

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest { TargetBytes = target });

        Assert.True(result.NewSize <= target, $"{result.NewSize} > {target}");
    }
}

public sealed class BatchTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;

    public BatchTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(_settings, paths, ToolLocator.CreateDefault(), new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose() => _dir.Dispose();

    private string Folder()
    {
        var root = _dir.File("Holiday");
        Directory.CreateDirectory(Path.Combine(root, "Day 2"));
        for (var i = 0; i < 3; i++)
            TestImages.SavePng(TestImages.Photo(300, 200, seed: i), Path.Combine(root, $"shot{i}.png"));
        TestImages.SaveJpeg(TestImages.Photo(300, 200, seed: 9), Path.Combine(root, "Day 2", "beach.jpg"), quality: 100);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "not media");
        return root;
    }

    [Fact]
    public void CollectsMediaKeepingTheFolderStructure()
    {
        var root = Folder();

        var shallow = BatchJob.Collect([root], includeSubfolders: false);
        var deep = BatchJob.Collect([root], includeSubfolders: true);

        Assert.Equal(3, shallow.Count);
        Assert.Equal(4, deep.Count);
        Assert.Contains(deep, i => i.RelativePath == Path.Combine("Holiday", "Day 2", "beach.jpg"));
        Assert.DoesNotContain(deep, i => i.Path.EndsWith("notes.txt"));
    }

    [ToolsFact]
    public async Task RunsBacksUpAndRestoresEverything()
    {
        var root = Folder();
        var items = BatchJob.Collect([root], includeSubfolders: true);
        var originals = items.ToDictionary(i => i.Path, i => File.ReadAllBytes(i.Path));
        var job = new BatchJob(_service, items);

        await job.BackUpAsync();
        await job.RunAsync(new BatchOptions());

        Assert.All(items, i => Assert.True(i.State is BatchItemState.Done or BatchItemState.Skipped, $"{i.RelativePath}: {i.Status}"));
        Assert.Contains(items, i => i.State == BatchItemState.Done && i.NewSize < i.OldSize);
        Assert.True(File.Exists(Path.Combine(job.BackupFolder, "Holiday", "Day 2", "beach.jpg")));

        var restored = await job.RestoreAllAsync();

        Assert.Equal(items.Count, restored);
        Assert.All(items, i => Assert.Equal(originals[i.Path], File.ReadAllBytes(i.Path)));
        Assert.True(Directory.Exists(job.BackupFolder)); // batch backups are kept
    }

    [ToolsFact]
    public async Task KeepOriginalsSavesCopies()
    {
        var root = Folder();
        var items = BatchJob.Collect([root], includeSubfolders: false);
        var job = new BatchJob(_service, items);

        await job.BackUpAsync();
        await job.RunAsync(new BatchOptions { KeepOriginals = true });

        Assert.True(File.Exists(Path.Combine(root, "shot0-optimised.png")));
        Assert.Equal(items[0].OldSize, new FileInfo(items[0].Path).Length);
    }

    [ToolsFact]
    public async Task StoppingLeavesTheRestUntouched()
    {
        var root = Folder();
        var items = BatchJob.Collect([root], includeSubfolders: true);
        var job = new BatchJob(_service, items);

        job.Stop();
        await job.RunAsync(new BatchOptions());

        Assert.All(items, i => Assert.Equal(BatchItemState.Cancelled, i.State));
    }
}
