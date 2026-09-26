using System.Collections.Concurrent;
using WClop.Core;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Watching;

namespace WClop.Tests;

public class IgnoreRulesTests
{
    [Theory]
    [InlineData("*.gif", "anim.gif", true)]
    [InlineData("*.gif", "sub/anim.gif", true)]
    [InlineData("*.gif", "photo.png", false)]
    [InlineData("skip-*", "skip-me.png", true)]
    [InlineData("/top.png", "top.png", true)]
    [InlineData("/top.png", "sub/top.png", false)]
    [InlineData("drafts/", "drafts/a.png", true)]
    [InlineData("drafts/", "drafts.png", false)]
    [InlineData("drafts", "drafts/a.png", true)]
    [InlineData("**/raw/*.png", "a/b/raw/x.png", true)]
    [InlineData("**/raw/*.png", "raw/x.png", true)]
    [InlineData("shot?.png", "shot1.png", true)]
    [InlineData("shot?.png", "shot12.png", false)]
    [InlineData("SCREEN*.PNG", "screenshot.png", true)]
    public void MatchesLikeGitignore(string rule, string path, bool ignored) =>
        Assert.Equal(ignored, new IgnoreRules([rule]).IsIgnored(path));

    [Fact]
    public void LaterNegationWins()
    {
        var rules = new IgnoreRules(["# comment", "", "*.png", "!keep.png"]);

        Assert.True(rules.IsIgnored("other.png"));
        Assert.False(rules.IsIgnored("keep.png"));
    }

    [Fact]
    public void LoadsWClopAndClopFileNames()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".wclopignore-image"), "a.png");
        File.WriteAllText(dir.File(".clopignore-image"), "b.png");

        var rules = IgnoreRules.Load(dir.Path, "image");

        Assert.True(rules.IsIgnored("a.png"));
        Assert.True(rules.IsIgnored("b.png"));
        Assert.False(IgnoreRules.Load(dir.Path, "video").IsIgnored("a.png"));
    }
}

public class WatchFiltersTests
{
    private static readonly IReadOnlySet<FileFormat> Images = new HashSet<FileFormat> { FileFormat.Png, FileFormat.Jpeg };

    [Theory]
    [InlineData(@"C:\s\Screenshot 2026.png", true)]
    [InlineData(@"C:\s\photo.JPEG", true)]
    [InlineData(@"C:\s\.hidden.png", false)]
    [InlineData(@"C:\s\~$lock.png", false)]
    [InlineData(@"C:\s\photo.png.crdownload", false)]
    [InlineData(@"C:\s\photo.part", false)]
    [InlineData(@"C:\s\notes.txt", false)]
    [InlineData(@"C:\s\clip.webp", false)] // not a format this watcher handles
    [InlineData(@"C:\s\scan.tiff", false)]
    public void CandidateNames(string path, bool expected) =>
        Assert.Equal(expected, WatchFilters.IsCandidateName(path, Images, ["tiff"]));

    [Fact]
    public void SkipFormatsWin()
    {
        Assert.False(WatchFilters.IsCandidateName(@"C:\s\a.jpg", Images, ["jpg"]));
    }

    [Theory]
    [InlineData(FileAttributes.Normal, false)]
    [InlineData(FileAttributes.Archive, false)]
    [InlineData(FileAttributes.Hidden, true)]
    [InlineData(FileAttributes.Offline, true)]
    [InlineData((FileAttributes)0x00400000, true)] // OneDrive cloud-only placeholder
    public void Attributes(FileAttributes attributes, bool skip) =>
        Assert.Equal(skip, WatchFilters.HasSkippableAttributes(attributes));

    [Fact]
    public void Limits()
    {
        var settings = new WatcherSettings { MinSizeBytes = 100, MaxSizeBytes = 1000, MinResolution = 20, MaxResolution = 0 };

        Assert.False(WatchFilters.IsWithinSizeLimits(99, settings));
        Assert.True(WatchFilters.IsWithinSizeLimits(500, settings));
        Assert.False(WatchFilters.IsWithinSizeLimits(1001, settings));
        Assert.True(WatchFilters.IsWithinSizeLimits(long.MaxValue, new WatcherSettings()));

        Assert.False(WatchFilters.IsWithinResolutionLimits(19, 500, settings));
        Assert.True(WatchFilters.IsWithinResolutionLimits(20, 20, settings));
        Assert.True(WatchFilters.IsWithinResolutionLimits(9000, 9000, settings));
    }

    [Fact]
    public void Inside()
    {
        Assert.True(WatchFilters.IsInside(@"C:\a\cache\x.png", @"C:\a\cache"));
        Assert.False(WatchFilters.IsInside(@"C:\a\cache2\x.png", @"C:\a\cache"));
    }
}

public class FileSettlerTests
{
    [Fact]
    public async Task WaitsUntilTheWriterCloses()
    {
        using var dir = new TempDir();
        var path = dir.File("a.png");
        var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(new byte[100]);
        writer.Flush();

        var settle = FileSettler.WaitAsync(path, _ => true, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(600);
        Assert.False(settle.IsCompleted);

        writer.Dispose();
        Assert.True(await settle);
    }

    [Fact]
    public async Task GivesUpOnInvalidFiles()
    {
        using var dir = new TempDir();
        var path = dir.File("a.png");
        File.WriteAllBytes(path, new byte[10]);
        var checks = 0;

        var settled = await FileSettler.WaitAsync(path, _ => { checks++; return false; }, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(settled);
        Assert.Equal(FileSettler.MaxInvalidChecks, checks);
    }

    [Fact]
    public async Task DeletedFileIsNotSettled()
    {
        using var dir = new TempDir();
        var path = dir.File("a.png");
        File.WriteAllBytes(path, new byte[10]);
        File.Delete(path);

        Assert.False(await FileSettler.WaitAsync(path, _ => true, TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}

public class RecentWritesTests
{
    [Fact]
    public async Task ProtectsForTheWindowOnly()
    {
        var writes = new RecentWrites(TimeSpan.FromMilliseconds(200));
        writes.Register(@"C:\s\a.png");

        Assert.True(writes.IsProtected(@"C:\S\A.PNG"));
        Assert.False(writes.IsProtected(@"C:\s\b.png"));
        await Task.Delay(300);
        Assert.False(writes.IsProtected(@"C:\s\a.png"));
    }
}

/// <summary>Runs real FileSystemWatchers on temp folders with a fake processor.</summary>
public sealed class FolderWatcherTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _watched;
    private readonly AppPaths _paths;
    private readonly OptimisationManager _manager = new();
    private readonly OptimisationMarkers _markers;
    private readonly RecentWrites _recentWrites = new(TimeSpan.FromSeconds(3));
    private readonly ConcurrentBag<string> _processed = [];
    private readonly ConcurrentBag<string> _notices = [];
    private readonly WatchingSettings _watching = new();
    private readonly List<FolderWatcher> _watchers = [];

    public FolderWatcherTests()
    {
        _watched = _dir.File("Screenshots");
        Directory.CreateDirectory(_watched);
        _paths = new AppPaths(_dir.File("cache"));
        _markers = new OptimisationMarkers(new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _dir.Dispose();
    }

    private WatcherSettings Settings(string? folder = null, int burst = 4) => new()
    {
        Enabled = true,
        Folders = [folder ?? _watched],
        MaxFilesPerBurst = burst,
    };

    private async Task<FolderWatcher> Start(WatcherSettings settings, DateTime? firstLaunch = null, Func<string, bool>? isValid = null)
    {
        var kind = new WatchKind
        {
            Name = "image",
            Formats = new HashSet<FileFormat> { FileFormat.Png },
            IsValid = isValid ?? (_ => true),
            Process = (path, _, _) =>
            {
                _processed.Add(Path.GetFileName(path));
                return Task.FromResult(new FileOptimisationResult(path, path, path, FileFormat.Png, FileFormat.Png, 2, 1, false));
            },
        };
        var watcher = new FolderWatcher(kind, settings, _watching, _paths, _manager, _markers, _recentWrites, firstLaunch)
        {
            MissingFolderRetryInterval = TimeSpan.FromMilliseconds(200),
        };
        watcher.Notice += _notices.Add;
        _watchers.Add(watcher);
        await watcher.StartAsync();
        return watcher;
    }

    private string Write(string name, int bytes = 1000)
    {
        var path = Path.Combine(_watched, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task NewFileIsProcessedOnce()
    {
        await Start(Settings());

        Write("Screenshot 1.png");

        Assert.True(await TestSetup.WaitUntil(() => _processed.Count == 1));
        await Task.Delay(1000);
        Assert.Equal(["Screenshot 1.png"], _processed);
    }

    [Fact]
    public async Task DownloadIsPickedUpOnRename()
    {
        await Start(Settings());

        var partial = Write("photo.png.crdownload");
        await Task.Delay(300);
        File.Move(partial, Path.Combine(_watched, "photo.png"));

        Assert.True(await TestSetup.WaitUntil(() => _processed.Count == 1));
        Assert.Equal(["photo.png"], _processed);
    }

    [Fact]
    public async Task IgnoresHiddenNamesAndOtherFormats()
    {
        await Start(Settings());

        Write(".hidden.png");
        Write("notes.txt");
        Write("clip.webp");
        var hidden = Write("attr-hidden.png");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        await Task.Delay(1500);
        Assert.DoesNotContain(".hidden.png", _processed);
        Assert.DoesNotContain("notes.txt", _processed);
        Assert.DoesNotContain("clip.webp", _processed);
    }

    [Fact]
    public async Task BurstIsIgnoredWithANotice()
    {
        await Start(Settings(burst: 4));

        for (var i = 0; i < 6; i++)
            Write($"unzipped {i}.png");

        Assert.True(await TestSetup.WaitUntil(() => !_notices.IsEmpty));
        await Task.Delay(1500);
        Assert.Empty(_processed);
        Assert.Contains("More than 4 images", _notices.Single());
    }

    [Fact]
    public async Task FilesWithinTheBurstLimitAreProcessed()
    {
        await Start(Settings(burst: 4));

        for (var i = 0; i < 3; i++)
            Write($"shot {i}.png");

        Assert.True(await TestSetup.WaitUntil(() => _processed.Count == 3));
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task AlreadyOptimisedFileIsIgnored()
    {
        await Start(Settings());
        var elsewhere = _dir.File("done.png");
        File.WriteAllBytes(elsewhere, new byte[1000]);
        _markers.Set(elsewhere, MarkerStatus.Optimised);

        File.Move(elsewhere, Path.Combine(_watched, "done.png")); // the NTFS stream moves with the file
        Write("fresh.png");

        Assert.True(await TestSetup.WaitUntil(() => _processed.Contains("fresh.png")));
        await Task.Delay(500);
        Assert.DoesNotContain("done.png", _processed);
    }

    [Fact]
    public async Task OwnWritesAreIgnored()
    {
        await Start(Settings());
        _recentWrites.Register(Path.Combine(_watched, "mine.png"));

        Write("mine.png");
        Write("theirs.png");

        Assert.True(await TestSetup.WaitUntil(() => _processed.Contains("theirs.png")));
        await Task.Delay(500);
        Assert.DoesNotContain("mine.png", _processed);
    }

    [Fact]
    public async Task IgnoreFileIsHonoured()
    {
        File.WriteAllText(Path.Combine(_watched, ".wclopignore-image"), "skip-*.png");
        await Start(Settings());

        Write("skip-this.png");
        Write("keep.png");

        Assert.True(await TestSetup.WaitUntil(() => _processed.Contains("keep.png")));
        await Task.Delay(500);
        Assert.DoesNotContain("skip-this.png", _processed);
    }

    [Fact]
    public async Task MissingFolderIsPickedUpWhenItAppears()
    {
        var later = _dir.File("External Drive");
        var watcher = await Start(Settings(later));
        Assert.Empty(watcher.ActiveFolders);

        Directory.CreateDirectory(later);
        Assert.True(await TestSetup.WaitUntil(() => watcher.ActiveFolders.Count == 1));
        File.WriteAllBytes(Path.Combine(later, "late.png"), new byte[1000]);

        Assert.True(await TestSetup.WaitUntil(() => _processed.Contains("late.png")));
    }

    [Fact]
    public async Task OneMissingFolderDoesNotStopTheOthers()
    {
        var settings = Settings();
        settings.Folders.Insert(0, _dir.File("does-not-exist"));
        var watcher = await Start(settings);

        Write("still works.png");

        Assert.True(await TestSetup.WaitUntil(() => _processed.Contains("still works.png")));
        Assert.Single(watcher.ActiveFolders);
    }

    [Fact]
    public async Task WaitsForTheFileToBeFullyWritten()
    {
        await Start(Settings());
        var path = Path.Combine(_watched, "recording.png");

        await using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            writer.Write(new byte[1000]);
            await writer.FlushAsync();
            await Task.Delay(1000);
            Assert.Empty(_processed);
        }

        Assert.True(await TestSetup.WaitUntil(() => _processed.Contains("recording.png")));
    }

    [Fact]
    public async Task SizeLimitsApplyAfterTheFileSettles()
    {
        var settings = Settings();
        settings.MinSizeBytes = 5000;
        await Start(settings);

        Write("tiny.png", 100);
        Write("big.png", 6000);

        Assert.True(await TestSetup.WaitUntil(() => _processed.Contains("big.png")));
        await Task.Delay(500);
        Assert.DoesNotContain("tiny.png", _processed);
    }

    [Fact]
    public async Task PausedWatcherDoesNothing()
    {
        await Start(Settings());
        _watching.Paused = true;

        Write("while paused.png");

        await Task.Delay(1500);
        Assert.Empty(_processed);
    }

    [Fact]
    public async Task FirstLaunchStormDisablesTheWatcher()
    {
        var settings = Settings(burst: 100);
        var storm = new TaskCompletionSource();
        var watcher = await Start(settings, firstLaunch: DateTime.UtcNow);
        watcher.StormDetected += _ => storm.TrySetResult();

        // Another app rewriting the folder: a steady stream, too slow to count as one burst.
        for (var i = 0; i < 7; i++)
        {
            Write($"synced {i}.png");
            await Task.Delay(150);
        }

        await storm.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(settings.Enabled);
        await Task.Delay(3500); // past the first-launch hold
        Assert.Empty(_processed);
    }

    [Fact]
    public async Task FirstLaunchHoldStillProcessesNormalFiles()
    {
        await Start(Settings(), firstLaunch: DateTime.UtcNow);

        Write("first screenshot.png");

        await Task.Delay(1500);
        Assert.Empty(_processed); // held for 3 s
        Assert.True(await TestSetup.WaitUntil(() => _processed.Contains("first screenshot.png")));
    }
}
