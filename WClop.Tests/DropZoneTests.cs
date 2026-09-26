using System.Buffers.Binary;
using System.Text;
using WClop.Core.DropZone;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Settings;

namespace WClop.Tests;

public class DropInputsTests
{
    [Fact]
    public void ExpandsFoldersToTheImagesDirectlyInside()
    {
        using var dir = new TempDir();
        var folder = dir.File("shots");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        foreach (var name in new[] { "b.png", "a.jpg", "notes.txt", ".hidden.png", "clip.webp", "nested\\deep.png" })
            File.WriteAllText(Path.Combine(folder, name), "x");
        var loose = dir.File("loose.gif");
        File.WriteAllText(loose, "x");

        var images = DropInputs.ExpandMediaPaths([folder, loose, loose], DropInputs.OptimisableImages);

        Assert.Equal(["a.jpg", "b.png", "clip.webp", "loose.gif"], images.Select(Path.GetFileName));
    }

    [Fact]
    public void MissingPathsAreIgnored() =>
        Assert.Empty(DropInputs.ExpandMediaPaths([@"C:\does\not\exist.png"], DropInputs.OptimisableImages));

    private static byte[] Descriptor(params (string Name, uint Flags, uint Attributes, long Size)[] files)
    {
        var data = new byte[4 + files.Length * 592];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            var record = data.AsSpan(4 + i * 592, 592);
            BinaryPrimitives.WriteUInt32LittleEndian(record, files[i].Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(record[36..], files[i].Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(record[64..], (uint)(files[i].Size >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(record[68..], (uint)files[i].Size);
            Encoding.Unicode.GetBytes(files[i].Name).CopyTo(record[72..]);
        }

        return data;
    }

    [Fact]
    public void ParsesVirtualFileDescriptors()
    {
        var data = Descriptor(
            ("photo.jpg", 0x40, 0, 12345),
            ("Attachments", 0x04, 0x10, 0),   // a folder
            (@"sub\diagram.png", 0x00, 0, 0)); // no size flag

        var files = DropInputs.ParseFileGroupDescriptor(data);

        Assert.Equal(2, files.Count);
        Assert.Equal(new VirtualFile(0, "photo.jpg", 12345), files[0]);
        Assert.Equal(new VirtualFile(2, "diagram.png", null), files[1]);
    }

    [Fact]
    public void TruncatedDescriptorDoesNotThrow()
    {
        var data = Descriptor(("a.png", 0, 0, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(data, 50); // claims 50 records

        Assert.Single(DropInputs.ParseFileGroupDescriptor(data));
        Assert.Empty(DropInputs.ParseFileGroupDescriptor([1, 2]));
    }

}

public class DropZoneGeometryTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1040);

    [Fact]
    public void RightEdgeTabHugsTheEdgeAtItsPosition()
    {
        var rect = DropZoneGeometry.Place(Screen, ScreenEdge.Right, 50, length: 160, depth: 30);

        Assert.Equal(new ScreenRect(1890, 440, 1920, 600), rect);
    }

    [Fact]
    public void LeftAndBottomEdges()
    {
        Assert.Equal(new ScreenRect(0, 440, 30, 600), DropZoneGeometry.Place(Screen, ScreenEdge.Left, 50, 160, 30));
        Assert.Equal(new ScreenRect(840, 1010, 1080, 1040), DropZoneGeometry.Place(Screen, ScreenEdge.Bottom, 50, 240, 30));
    }

    [Fact]
    public void TabStaysOnScreenAtTheEnds()
    {
        Assert.Equal(0, DropZoneGeometry.Place(Screen, ScreenEdge.Right, 0, 160, 30).Top);
        Assert.Equal(1040, DropZoneGeometry.Place(Screen, ScreenEdge.Right, 100, 160, 30).Bottom);
    }

    [Theory]
    [InlineData(1910, 520, ScreenEdge.Right, 50)]
    [InlineData(5, 104, ScreenEdge.Left, 10)]
    [InlineData(960, 3, ScreenEdge.Top, 50)]
    [InlineData(1536, 1030, ScreenEdge.Bottom, 80)]
    public void NearestEdgeForDraggingTheTab(int x, int y, ScreenEdge edge, double percent)
    {
        var (nearest, along) = DropZoneGeometry.Nearest(x, y, Screen);

        Assert.Equal(edge, nearest);
        Assert.Equal(percent, along);
    }

    [Theory]
    [InlineData(1800, 520, true)]   // 90 px left of the tab
    [InlineData(1500, 520, false)]
    [InlineData(1900, 300, true)]   // above the tab, within reach
    [InlineData(1900, 100, false)]
    public void Nearness(int x, int y, bool near) =>
        Assert.Equal(near, DropZoneGeometry.IsNear(x, y, new ScreenRect(1890, 440, 1920, 600), 150));
}

public class WorkSlotTests
{
    private static readonly FileOptimisationResult SomeResult =
        new("in.png", "out.png", "backup.png", FileFormat.Png, FileFormat.Png, 100, 50, false);

    [Fact]
    public async Task FileJobsBeyondTheLimitQueue()
    {
        var manager = new OptimisationManager(maxConcurrentFileJobs: 2);
        var gate = new TaskCompletionSource<FileOptimisationResult>();
        var running = 0;
        var peak = 0;

        var jobs = Enumerable.Range(0, 5).Select(i => manager.Start($"file{i}", JobSource.DropZone, $"{i}", null, async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            try
            {
                return await gate.Task;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        })).ToList();

        Assert.True(await TestSetup.WaitUntil(() => jobs.Count(j => j.Status == "Queued") == 3));
        gate.SetResult(SomeResult);
        Assert.True(await TestSetup.WaitUntil(() => jobs.All(j => j.State == JobState.Succeeded)));
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task ClipboardJobsNeverWaitBehindFileJobs()
    {
        var manager = new OptimisationManager(maxConcurrentFileJobs: 1);
        var gate = new TaskCompletionSource<FileOptimisationResult>();
        manager.Start("file", JobSource.File, "file", null, (_, _) => gate.Task);

        var clipboard = manager.Start("clipboard", JobSource.Clipboard, "clip", null, (_, _) => Task.FromResult(SomeResult));

        Assert.True(await TestSetup.WaitUntil(() => clipboard.State == JobState.Succeeded));
        gate.SetResult(SomeResult);
    }

    [Fact]
    public async Task CancellingAQueuedJobFreesNothingAndDoesNotRun()
    {
        var manager = new OptimisationManager(maxConcurrentFileJobs: 1);
        var gate = new TaskCompletionSource<FileOptimisationResult>();
        var first = manager.Start("a", JobSource.File, "a", null, (_, _) => gate.Task);
        var ran = false;
        var queued = manager.Start("b", JobSource.File, "b", null, (_, _) => { ran = true; return Task.FromResult(SomeResult); });
        Assert.True(await TestSetup.WaitUntil(() => queued.Status == "Queued"));

        queued.Cancel();
        Assert.True(await TestSetup.WaitUntil(() => queued.State == JobState.Cancelled));
        gate.SetResult(SomeResult);
        Assert.True(await TestSetup.WaitUntil(() => first.State == JobState.Succeeded));
        Assert.False(ran);

        // The slot is still usable afterwards.
        var next = manager.Start("c", JobSource.File, "c", null, (_, _) => Task.FromResult(SomeResult));
        Assert.True(await TestSetup.WaitUntil(() => next.State == JobState.Succeeded));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = target) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

public class DropPresetTests
{
    [Fact]
    public void ScrollingWrapsAround()
    {
        var count = DropPresets.All.Count;

        Assert.Equal(1, DropPresets.Step(0, 1));
        Assert.Equal(count - 1, DropPresets.Step(0, -1));
        Assert.Equal(0, DropPresets.Step(count - 1, 1));
        Assert.Equal(2, DropPresets.Step(0, count + 2));
    }

    [Fact]
    public void DefaultPresetChangesNothing()
    {
        var request = new FileOptimisationRequest { Force = true, Behaviour = OutputBehaviour.SameFolder };

        Assert.Equal(request, DropPresets.All[0].Apply(request));
    }

    [Fact]
    public void PresetsSetFactorScaleAndPdfDpi()
    {
        var aggressive = DropPresets.All.Single(p => p.Name == "Aggressive").Apply(new FileOptimisationRequest());
        var half = DropPresets.All.Single(p => p.Name == "Half size").Apply(new FileOptimisationRequest());
        var gentle = DropPresets.All.Single(p => p.Name == "Gentle").Apply(new FileOptimisationRequest());

        Assert.Equal((64, (double?)null, 100), (aggressive.Factor!.Value, aggressive.Scale, aggressive.PdfDpi!.Value));
        Assert.Equal(0.5, half.Scale);
        Assert.Null(half.Factor);
        Assert.Equal(300, gentle.PdfDpi); // PDFs: lossless
    }

    [Fact]
    public void PresetKeepsOtherRequestSettings()
    {
        var request = DropPresets.All[1].Apply(new FileOptimisationRequest { Behaviour = OutputBehaviour.SameFolder, Force = true });

        Assert.Equal(OutputBehaviour.SameFolder, request.Behaviour);
        Assert.True(request.Force);
    }
}
