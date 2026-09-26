using WClop.Core;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Tests;

public class OptimisationManagerTests
{
    private static readonly FileOptimisationResult SomeResult =
        new("in.png", "out.png", "backup.png", FileFormat.Png, FileFormat.Png, 100, 50, false);

    private static async Task<OptimisationJob> Finished(OptimisationManager manager, OptimisationJob job)
    {
        var done = new TaskCompletionSource();
        manager.JobFinished += j =>
        {
            if (j == job) done.TrySetResult();
        };
        if (!job.IsFinished)
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return job;
    }

    [Fact]
    public async Task SuccessRecordsResult()
    {
        var manager = new OptimisationManager();
        var job = manager.Start("a", JobSource.File, "a.png", "a.png", (_, _) => Task.FromResult(SomeResult));

        await Finished(manager, job);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(SomeResult, job.Result);
        Assert.Equal("out.png", job.CurrentPath);
    }

    [Theory]
    [InlineData(typeof(NotSmallerException), JobState.NotSmaller)]
    [InlineData(typeof(JobSkippedException), JobState.Skipped)]
    [InlineData(typeof(UnsupportedFormatException), JobState.Failed)]
    [InlineData(typeof(IOException), JobState.Failed)]
    public async Task ExceptionsMapToStates(Type exceptionType, JobState expected)
    {
        Exception exception = exceptionType == typeof(NotSmallerException) ? new NotSmallerException(1, 2)
            : (Exception)Activator.CreateInstance(exceptionType, "boom")!;
        var manager = new OptimisationManager();

        var job = await Finished(manager, manager.Start("a", JobSource.File, "a", null, (_, _) => throw exception));

        Assert.Equal(expected, job.State);
    }

    [Fact]
    public async Task NewJobForSameKeyCancelsAndWaitsForTheOld()
    {
        var manager = new OptimisationManager();
        var firstStarted = new TaskCompletionSource();
        var firstStopped = false;
        var overlap = false;

        var first = manager.Start("clipboard", JobSource.Clipboard, "1", null, async (_, token) =>
        {
            firstStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                await Task.Delay(50, CancellationToken.None); // cleanup takes a moment
                firstStopped = true;
            }

            return SomeResult;
        });
        await firstStarted.Task;

        var second = manager.Start("clipboard", JobSource.Clipboard, "2", null, (_, _) =>
        {
            overlap = !firstStopped;
            return Task.FromResult(SomeResult);
        });

        await Finished(manager, second);
        Assert.Equal(JobState.Cancelled, first.State);
        Assert.Equal(JobState.Succeeded, second.State);
        Assert.False(overlap, "second job ran before the first finished stopping");
        Assert.Same(second, manager.Current);
    }

    [Fact]
    public async Task DifferentKeysRunConcurrently()
    {
        var manager = new OptimisationManager();
        var gate = new TaskCompletionSource<FileOptimisationResult>();

        var a = manager.Start("a", JobSource.File, "a", null, (_, _) => gate.Task);
        var b = await Finished(manager, manager.Start("b", JobSource.File, "b", null, (_, _) => Task.FromResult(SomeResult)));

        Assert.Equal(JobState.Succeeded, b.State);
        Assert.Equal(JobState.Running, a.State);
        gate.SetResult(SomeResult);
        await Finished(manager, a);
    }

    [Fact]
    public async Task CancelAllStopsRunningJobs()
    {
        var manager = new OptimisationManager();
        var job = manager.Start("a", JobSource.File, "a", null, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return SomeResult;
        });

        manager.CancelAll();

        Assert.Equal(JobState.Cancelled, (await Finished(manager, job)).State);
    }

    [Fact]
    public void RemovedJobsCanBeBroughtBackNewestFirst()
    {
        var manager = new OptimisationManager();
        var a = manager.Start("a", JobSource.File, "a", null, (_, _) => Task.FromResult(SomeResult));
        var b = manager.Start("b", JobSource.File, "b", null, (_, _) => Task.FromResult(SomeResult));

        manager.Removed(a);
        manager.Removed(b);

        Assert.Same(b, manager.TakeLastRemoved());
        Assert.Same(a, manager.TakeLastRemoved());
        Assert.Null(manager.TakeLastRemoved());
    }

    [Fact]
    public async Task PropertyChangesAreRaised()
    {
        var manager = new OptimisationManager();
        var gate = new TaskCompletionSource<FileOptimisationResult>();
        var job = manager.Start("a", JobSource.File, "a", null, (_, _) => gate.Task);
        var changed = new List<string>();
        job.PropertyChanged += (_, e) => { lock (changed) changed.Add(e.PropertyName!); };

        gate.SetResult(SomeResult);
        await Finished(manager, job);

        Assert.Contains(nameof(OptimisationJob.State), changed);
        Assert.Contains(nameof(OptimisationJob.Result), changed);
        Assert.Contains(nameof(OptimisationJob.IsFinished), changed);
    }
}

public sealed class RestoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FileOptimisationService _service;

    public RestoreTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(
            new AppSettings(), paths, ToolLocator.CreateDefault(), new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose() => _dir.Dispose();

    [ToolsFact]
    public async Task RestoresInPlaceOptimisation()
    {
        var input = TestImages.SavePng(TestImages.Photo(), _dir.File("shot.png"));
        var original = File.ReadAllBytes(input);
        var mtime = new DateTime(2023, 3, 3, 3, 3, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(input, mtime);

        var result = await _service.OptimiseImageAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.InPlace });
        var restored = _service.Restore(result);

        Assert.Equal(input, restored);
        Assert.Equal(original, File.ReadAllBytes(restored));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(restored));
        Assert.Equal(MarkerStatus.Original, _service.Markers.Get(restored));

        // Restored files aren't picked up again automatically.
        await Assert.ThrowsAsync<AlreadyOptimisedException>(() =>
            _service.OptimiseImageAsync(restored, new FileOptimisationRequest { Behaviour = OutputBehaviour.InPlace }));
    }

    [ToolsFact]
    public async Task RestoringASameFolderCopyPutsOriginalContentThere()
    {
        var input = TestImages.SavePng(TestImages.Photo(), _dir.File("shot.png"));
        var original = File.ReadAllBytes(input);

        var result = await _service.OptimiseImageAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.SameFolder });
        var restored = _service.Restore(result);

        Assert.Equal(_dir.File("shot-optimised.png"), restored);
        Assert.Equal(original, File.ReadAllBytes(restored));
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public void RestoringAConvertedFileBringsBackTheOriginalFormat()
    {
        // photo.png was optimised in place into photo.jpg.
        var backup = _dir.File("backup.png");
        File.WriteAllText(backup, "original png");
        var output = _dir.File("photo.jpg");
        File.WriteAllText(output, "optimised jpg");
        var result = new FileOptimisationResult(
            _dir.File("photo.png"), output, backup, FileFormat.Png, FileFormat.Jpeg, 12, 13, false);

        var restored = _service.Restore(result);

        Assert.Equal(_dir.File("photo.png"), restored);
        Assert.Equal("original png", File.ReadAllText(restored));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void MissingBackupThrows()
    {
        var result = new FileOptimisationResult(
            _dir.File("a.png"), _dir.File("a.png"), _dir.File("gone.png"), FileFormat.Png, FileFormat.Png, 1, 1, false);

        Assert.Throws<FileNotFoundException>(() => _service.Restore(result));
    }
}
