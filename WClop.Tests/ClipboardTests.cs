using System.Buffers.Binary;
using System.Windows.Media.Imaging;
using WClop.Core.Clipboard;
using WClop.Core.Settings;
using WClop.Core.Storage;
using Decision = WClop.Core.Clipboard.ClipboardDecision;

namespace WClop.Tests;

public class ClipboardPolicyTests
{
    private static ClipboardDecision Decide(ClipboardSnapshot snapshot, ClipboardSettings? settings = null) =>
        ClipboardPolicy.Decide(snapshot, settings ?? new ClipboardSettings());

    private static ClipboardSnapshot Snap(params string[] formats) => new() { Formats = formats };

    [Fact]
    public void SnippingToolScreenshotReadsPng()
    {
        var decision = Decide(Snap("DataObject", "PNG", "CF_DIB", "CF_BITMAP", "CF_DIBV5") with { OwnerProcess = "SnippingTool.exe" });

        Assert.Equal(new Decision.ReadImage("PNG", ClipboardImageEncoding.Png), decision);
    }

    [Fact]
    public void GifIsPreferredOverItsStillPreview()
    {
        var decision = Decide(Snap("PNG", "GIF", "CF_DIB"));

        Assert.Equal(new Decision.ReadImage("GIF", ClipboardImageEncoding.Gif), decision);
    }

    [Fact]
    public void CandidatesListEveryReadableImageFormatBestFirst()
    {
        var candidates = ClipboardPolicy.ImageCandidates(Snap("CF_DIB", "PNG", "CF_DIBV5", "image/png", "GIF"));

        Assert.Equal(["GIF", "PNG", "image/png", "CF_DIBV5", "CF_DIB"], candidates.Select(c => c.Format));
        Assert.True(candidates[^1].IsDib);
    }

    [Fact]
    public void BitmapOnlyFallsBackToDib()
    {
        Assert.Equal(new Decision.ReadImage("CF_DIBV5", ClipboardImageEncoding.Dib), Decide(Snap("CF_BITMAP", "CF_DIB", "CF_DIBV5")));
        Assert.Equal(new Decision.ReadImage("CF_DIB", ClipboardImageEncoding.Dib), Decide(Snap("CF_BITMAP", "CF_DIB")));
    }

    [Fact]
    public void BrowserCopyImageIsOptimised()
    {
        var decision = Decide(Snap("HTML Format", "CF_DIB", "PNG", "CF_BITMAP") with { OwnerProcess = "chrome.exe" });

        Assert.IsType<Decision.ReadImage>(decision);
    }

    [Fact]
    public void OwnWriteIsIgnored() =>
        Assert.IsType<Decision.Ignore>(Decide(Snap("WClop.Optimised", "PNG", "CF_DIB")));

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("Clipboard Viewer Ignore")]
    public void SensitiveMarkersAreIgnored(string marker) =>
        Assert.IsType<Decision.Ignore>(Decide(Snap(marker, "PNG")));

    [Fact]
    public void HistoryOptOutIsTreatedAsSensitive()
    {
        Assert.IsType<Decision.Ignore>(Decide(Snap("PNG") with { CanIncludeInHistory = 0 }));
        Assert.IsType<Decision.ReadImage>(Decide(Snap("PNG") with { CanIncludeInHistory = 1 }));
        Assert.IsType<Decision.Ignore>(Decide(Snap("PNG") with { CanUploadToCloud = 0 }));
    }

    [Theory]
    [InlineData("KeePassXC.exe")]
    [InlineData("keepassxc")]
    public void IgnoredAppsMatchWithOrWithoutExe(string configured)
    {
        var settings = new ClipboardSettings { IgnoredApps = [configured] };

        Assert.IsType<Decision.Ignore>(Decide(Snap("PNG") with { OwnerProcess = "KeePassXC.exe" }, settings));
    }

    [Fact]
    public void RemoteDesktopContentIgnoredByDefault()
    {
        var snapshot = Snap("CF_DIB") with { OwnerProcess = "rdpclip.exe" };

        Assert.IsType<Decision.Ignore>(Decide(snapshot));
        Assert.IsType<Decision.ReadImage>(Decide(snapshot, new ClipboardSettings { IgnoreRemoteClipboard = false }));
    }

    [Theory]
    [InlineData("Art::GVML ClipFormat")]
    [InlineData("PowerPoint 12.0 Internal Shapes")]
    [InlineData("Biff12")]
    [InlineData("Rich Text Format")]
    [InlineData("Object Descriptor")]
    [InlineData("Embed Source")]
    [InlineData("image/svg+xml")]
    [InlineData("PaintDotNet.MaskedSurface")]
    public void AppNativeContentIsLeftAlone(string nativeFormat) =>
        Assert.IsType<Decision.Ignore>(Decide(Snap(nativeFormat, "PNG", "CF_DIB")));

    [Fact]
    public void ExtraDeniedFormatsFromSettings()
    {
        var settings = new ClipboardSettings { ExtraDeniedFormats = ["MyApp Layer"] };

        Assert.IsType<Decision.Ignore>(Decide(Snap("MyApp Layer", "PNG"), settings));
    }

    [Fact]
    public void TextOnlyHasNoImage() =>
        Assert.Equal(new Decision.Ignore("no image"), Decide(Snap("CF_UNICODETEXT", "CF_LOCALE", "CF_TEXT")));

    [Fact]
    public void CopiedFilesAreIgnoredUnlessImagePathsEnabled()
    {
        var snapshot = Snap("CF_HDROP", "Preferred DropEffect", "Shell IDList Array") with
        {
            Files = [@"C:\Pics\a.png"],
            PreferredDropEffect = 5, // copy | link
        };

        Assert.IsType<Decision.Ignore>(Decide(snapshot));
        Assert.Equal(new Decision.ImageFile(@"C:\Pics\a.png"), Decide(snapshot, new ClipboardSettings { OptimiseImagePaths = true }));
    }

    [Fact]
    public void CutFilesAreNeverTouched()
    {
        var snapshot = Snap("CF_HDROP") with { Files = [@"C:\Pics\a.png"], PreferredDropEffect = 2 };

        Assert.Equal(
            new Decision.Ignore("files were cut, not copied"),
            Decide(snapshot, new ClipboardSettings { OptimiseImagePaths = true }));
    }

    [Fact]
    public void FilePathsNeverFallThroughToImageData()
    {
        // Explorer-style copies can carry a thumbnail; the files branch must win.
        var snapshot = Snap("CF_HDROP", "CF_DIB") with { Files = [@"C:\Docs\report.pdf"] };

        Assert.IsType<Decision.Ignore>(Decide(snapshot, new ClipboardSettings { OptimiseImagePaths = true }));
    }

    [Fact]
    public void AlreadyOptimisedImageFileIsIgnored()
    {
        var snapshot = Snap("CF_HDROP") with { Files = [@"C:\Pics\a.png"] };

        var decision = ClipboardPolicy.Decide(
            snapshot, new ClipboardSettings { OptimiseImagePaths = true }, _ => MarkerStatus.Optimised);

        Assert.IsType<Decision.Ignore>(decision);
    }
}

public class DibConverterTests
{
    [Fact]
    public void RoundTripsThroughDib()
    {
        using var dir = new TempDir();
        var source = TestImages.SavePng(TestImages.Photo(64, 48), dir.File("in.png"));

        var dib = DibConverter.ImageFileToDib(source);
        var png = DibConverter.DibToPng(dib);

        var decoded = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal((64, 48), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(ReadPixels(BitmapDecoder.Create(new Uri(source), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0]), ReadPixels(decoded));
    }

    [Fact]
    public void DibHeaderIsBottomUp32Bit()
    {
        using var dir = new TempDir();
        var source = TestImages.SavePng(TestImages.Photo(10, 7), dir.File("in.png"));

        var dib = DibConverter.ImageFileToDib(source);

        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(dib));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)));
        Assert.Equal(40 + 10 * 7 * 4, dib.Length);
    }

    [Fact]
    public void BitfieldsDibPixelOffsetAccountsForMasks()
    {
        var dib = new byte[40 + 12 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), 3); // BI_BITFIELDS

        var file = DibConverter.WrapAsBmpFile(dib);

        Assert.Equal(14u + 40 + 12, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(10)));
    }

    private static byte[] ReadPixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr24, null, 0);
        var stride = converted.PixelWidth * 3;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
