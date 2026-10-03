using System.Buffers.Binary;
using System.Text;
using WClop.Core.Clipboard;
using WClop.Core.Settings;

namespace WClop.Tests;

public class ClipboardCollectionTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public void CollectsResultsOldestFirst()
    {
        var collection = new ClipboardCollection();

        collection.Add(@"C:\work\a.png", Idle);
        collection.Add(@"C:\work\b.png", Idle);
        var all = collection.Add(@"C:\work\c.jpg", Idle);

        Assert.Equal([@"C:\work\a.png", @"C:\work\b.png", @"C:\work\c.jpg"], all);
        Assert.Equal(3, collection.Count);
    }

    [Fact]
    public void TheSameFileIsListedOnceAndMovesToTheEnd()
    {
        var collection = new ClipboardCollection();
        collection.Add(@"C:\work\a.png", Idle);
        collection.Add(@"C:\work\b.png", Idle);

        var all = collection.Add(@"c:\WORK\A.png", Idle);

        Assert.Equal([@"C:\work\b.png", @"c:\WORK\A.png"], all);
    }

    [Fact]
    public void StartsAgainAfterTheIdleTime()
    {
        var clock = new Clock();
        var collection = new ClipboardCollection(() => clock.Now);
        collection.Add(@"C:\work\a.png", Idle);
        clock.Now += TimeSpan.FromSeconds(59);
        collection.Add(@"C:\work\b.png", Idle);

        // Idle time counts from the last copy, not the first.
        clock.Now += TimeSpan.FromSeconds(59);
        Assert.Equal(3, collection.Add(@"C:\work\c.png", Idle).Count);

        clock.Now += TimeSpan.FromSeconds(61);
        Assert.Equal([@"C:\work\d.png"], collection.Add(@"C:\work\d.png", Idle));
    }

    [Fact]
    public void ZeroIdleTimeNeverExpires()
    {
        var clock = new Clock();
        var collection = new ClipboardCollection(() => clock.Now);
        collection.Add(@"C:\work\a.png", TimeSpan.Zero);
        clock.Now += TimeSpan.FromDays(2);

        Assert.Equal(2, collection.Add(@"C:\work\b.png", TimeSpan.Zero).Count);
    }

    [Fact]
    public void ClearingStartsANewCollection()
    {
        var collection = new ClipboardCollection();
        collection.Add(@"C:\work\a.png", Idle);
        collection.Add(@"C:\work\b.png", Idle);

        collection.Clear();

        Assert.Equal(0, collection.Count);
        Assert.Equal([@"C:\work\c.png"], collection.Add(@"C:\work\c.png", Idle));
    }

    [Fact]
    public void ReturnsASnapshotNotTheLiveList()
    {
        var collection = new ClipboardCollection();
        var first = collection.Add(@"C:\work\a.png", Idle);
        collection.Add(@"C:\work\b.png", Idle);

        Assert.Single(first);
    }

    [Fact]
    public void IsOffByDefault()
    {
        var settings = new ClipboardSettings();

        Assert.False(settings.CollectResults);
        Assert.Equal(60, settings.CollectResultsIdleSeconds);
    }

    private static IReadOnlyList<string> ParseDropFiles(byte[] data)
    {
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16))); // wide characters
        var text = Encoding.Unicode.GetString(data, offset, data.Length - offset);
        Assert.EndsWith("\0\0", text);
        return text.TrimEnd('\0').Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void DropFilesListsEveryPath()
    {
        string[] paths = [@"C:\work\a.png", @"C:\Users\Zoë\b c.jpg", @"D:\x.gif"];

        Assert.Equal(paths, ParseDropFiles(ClipboardCollection.DropFiles(paths)));
    }

    [Fact]
    public void DropFilesForOnePathMatchesTheSingleFileLayout()
    {
        var data = ClipboardCollection.DropFiles([@"C:\a.png"]);

        Assert.Equal(20 + Encoding.Unicode.GetByteCount(@"C:\a.png" + "\0\0"), data.Length);
        Assert.Equal([@"C:\a.png"], ParseDropFiles(data));
    }

    [Fact]
    public void EmptyDropFilesIsStillDoubleNullTerminated() =>
        Assert.Empty(ParseDropFiles(ClipboardCollection.DropFiles([])));
}
