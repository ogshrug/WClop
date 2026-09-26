using WClop.Core;
using WClop.Core.Storage;

namespace WClop.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string NewFile(string name, string content = "data")
    {
        var path = _dir.File(name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AlternateStreamRoundTripsAndKeepsTimestamps()
    {
        var path = NewFile("a.png");
        var mtime = new DateTime(2020, 5, 5, 5, 5, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, mtime);

        Assert.True(AlternateStreamMarker.TryWrite(path, MarkerStatus.OriginalProcessed));

        Assert.Equal(MarkerStatus.OriginalProcessed, AlternateStreamMarker.Read(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
        Assert.Equal("data", File.ReadAllText(path));
    }

    [Fact]
    public void AlternateStreamCanBeRemoved()
    {
        var path = NewFile("a.png");
        AlternateStreamMarker.TryWrite(path, MarkerStatus.Optimised);

        AlternateStreamMarker.TryWrite(path, MarkerStatus.None);

        Assert.Equal(MarkerStatus.None, AlternateStreamMarker.Read(path));
    }

    [Fact]
    public void MissingStreamReadsAsNone() => Assert.Equal(MarkerStatus.None, AlternateStreamMarker.Read(NewFile("a.png")));

    [Fact]
    public void DatabaseMarkerSurvivesLostStream()
    {
        var markers = new OptimisationMarkers(new OptimisationDatabase(_dir.File("db.sqlite")));
        var path = NewFile("a.png");
        markers.Set(path, MarkerStatus.Optimised);

        // Simulate a copy through something that drops streams (zip, OneDrive, FAT32).
        File.Delete(path + ":" + AlternateStreamMarker.StreamName);

        Assert.Equal(MarkerStatus.Optimised, markers.Get(path));
    }

    [Fact]
    public void DatabaseMarkerIgnoredOnceFileChanges()
    {
        var markers = new OptimisationMarkers(new OptimisationDatabase(_dir.File("db.sqlite")));
        var path = NewFile("a.png");
        markers.Set(path, MarkerStatus.Optimised);
        File.Delete(path + ":" + AlternateStreamMarker.StreamName);

        File.WriteAllText(path, "a different, longer file");

        Assert.Equal(MarkerStatus.None, markers.Get(path));
    }

    [Fact]
    public void HashCacheReturnsOnlyExistingUnchangedOutputs()
    {
        var db = new OptimisationDatabase(_dir.File("db.sqlite"));
        var output = NewFile("out.png", "optimised");
        db.PutCachedOutput("abc", "image:f30", output);

        Assert.Equal(output, db.GetCachedOutput("abc", "image:f30"));
        Assert.Null(db.GetCachedOutput("abc", "image:f64"));

        File.WriteAllText(output, "edited since");
        Assert.Null(db.GetCachedOutput("abc", "image:f30"));
    }

    [Fact]
    public async Task BackupsAreNamedByContentAndDeduplicated()
    {
        var paths = new AppPaths(_dir.File("cache"));
        var store = new BackupStore(paths);
        var a = NewFile("photo.png", "one");
        var hash = await ContentHash.OfFileAsync(a);

        var first = store.Backup(a, hash);
        var second = store.Backup(a, hash);

        Assert.Equal(first, second);
        Assert.StartsWith(Path.Combine(paths.Backups, "photo-" + hash[..16]), first);
        Assert.Equal("one", File.ReadAllText(first));
        Assert.Single(Directory.GetFiles(paths.Backups));
    }

    [Fact]
    public async Task HashesMatchForFileAndBytes()
    {
        var path = NewFile("a.bin", "hello");
        Assert.Equal(ContentHash.OfBytes("hello"u8), await ContentHash.OfFileAsync(path));
    }
}
