using WClop.Core.Media;

namespace WClop.Tests;

public class FileTypeSnifferTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, FileFormat.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46 }, FileFormat.Jpeg)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0 }, FileFormat.Gif)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0 }, FileFormat.Tiff)]
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8 }, FileFormat.Tiff)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, FileFormat.Pdf)]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x77, 0x65, 0x62, 0x6D }, FileFormat.WebM)] // contains "webm"
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 }, FileFormat.Mkv)]
    [InlineData(new byte[] { 0x66, 0x4C, 0x61, 0x43, 0, 0 }, FileFormat.Flac)]
    [InlineData(new byte[] { 0x49, 0x44, 0x33, 4, 0, 0 }, FileFormat.Mp3)]
    [InlineData(new byte[] { 0xFF, 0x0A, 0, 0 }, FileFormat.JpegXl)]
    [InlineData(new byte[] { 0, 0, 1, 0xBA, 0x44 }, FileFormat.Mpeg)]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6 }, FileFormat.Unknown)]
    public void SniffsMagicBytes(byte[] header, FileFormat expected) =>
        Assert.Equal(expected, FileTypeSniffer.Sniff(header));

    [Theory]
    [InlineData("WEBP", FileFormat.WebP)]
    [InlineData("WAVE", FileFormat.Wav)]
    [InlineData("AVI ", FileFormat.Avi)]
    public void SniffsRiffContainers(string type, FileFormat expected)
    {
        byte[] header = [.. "RIFF"u8, 0, 0, 0, 0, .. System.Text.Encoding.ASCII.GetBytes(type)];
        Assert.Equal(expected, FileTypeSniffer.Sniff(header));
    }

    [Theory]
    [InlineData("heic", "mif1", FileFormat.Heic)]
    [InlineData("mif1", "heic", FileFormat.Heic)]
    [InlineData("avif", "mif1", FileFormat.Avif)]
    [InlineData("mif1", "avif", FileFormat.Avif)]
    [InlineData("qt  ", "qt  ", FileFormat.Mov)]
    [InlineData("isom", "mp41", FileFormat.Mp4)]
    [InlineData("M4A ", "isom", FileFormat.M4a)]
    public void SniffsIsoBaseMediaBrands(string major, string compatible, FileFormat expected)
    {
        byte[] header =
        [
            0, 0, 0, 20, .. "ftyp"u8, .. System.Text.Encoding.ASCII.GetBytes(major),
            0, 0, 0, 0, .. System.Text.Encoding.ASCII.GetBytes(compatible),
        ];
        Assert.Equal(expected, FileTypeSniffer.Sniff(header));
    }

    [Fact]
    public void ContentWinsOverExtension()
    {
        using var dir = new TempDir();
        var path = TestImages.SaveJpeg(TestImages.Photo(16, 16), dir.File("actually-a-jpeg.png"));

        Assert.Equal(FileFormat.Jpeg, FileTypeSniffer.Detect(path));
    }

    [Fact]
    public void FallsBackToExtension()
    {
        using var dir = new TempDir();
        var path = dir.File("clip.mkv");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Equal(FileFormat.Mkv, FileTypeSniffer.Detect(path));
    }

    [Theory]
    [InlineData("image@2x.png", FileFormat.Png)]
    [InlineData("PHOTO.JPEG", FileFormat.Jpeg)]
    [InlineData("noext", FileFormat.Unknown)]
    public void ExtensionMapping(string name, FileFormat expected) =>
        Assert.Equal(expected, FileFormats.FromExtension(name));
}

public class GifInfoTests
{
    [Fact]
    public void CountsFramesAndReadsLoopCount()
    {
        var info = GifInfo.Read(new MemoryStream(TestImages.Gif(frames: 5, loopCount: 0)));

        Assert.Equal(5, info.FrameCount);
        Assert.Equal(0, info.LoopCount);
        Assert.True(info.IsAnimated);
        Assert.False(info.Truncated);
        Assert.Equal((1, 1), (info.Width, info.Height));
    }

    [Fact]
    public void NoLoopBlockMeansPlayOnce()
    {
        var info = GifInfo.Read(new MemoryStream(TestImages.Gif(frames: 3, loopCount: null)));

        Assert.Null(info.LoopCount);
    }

    [Fact]
    public void ReadsFiniteLoopCount()
    {
        var info = GifInfo.Read(new MemoryStream(TestImages.Gif(frames: 2, loopCount: 3)));

        Assert.Equal(3, info.LoopCount);
    }

    [Fact]
    public void StillGifIsNotAnimated()
    {
        var info = GifInfo.Read(new MemoryStream(TestImages.Gif(frames: 1, loopCount: null)));

        Assert.False(info.IsAnimated);
    }

    [Fact]
    public void TruncatedGifReportsFramesBeforeTheCut()
    {
        var info = GifInfo.Read(new MemoryStream(TestImages.Gif(frames: 6, loopCount: 0, truncateAfterFrames: 2)));

        Assert.True(info.Truncated);
        Assert.Equal(2, info.FrameCount);
    }

    [Fact]
    public void HandBuiltGifDecodes()
    {
        using var dir = new TempDir();
        var path = dir.File("tiny.gif");
        File.WriteAllBytes(path, TestImages.Gif(frames: 2, loopCount: 0));

        Assert.Equal(new Core.Images.ImageSize(1, 1), Core.Images.ImageDecoding.Verify(path));
    }
}

public class WebPInfoTests
{
    private static byte[] ExtendedWebP(bool animated, bool iccFirst, ushort loopCount)
    {
        var chunks = new List<byte>();
        chunks.AddRange("VP8X"u8.ToArray());
        chunks.AddRange([10, 0, 0, 0]);
        chunks.AddRange([(byte)((animated ? 0x02 : 0) | (iccFirst ? 0x20 : 0)), 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        if (iccFirst)
        {
            chunks.AddRange("ICCP"u8.ToArray());
            chunks.AddRange([3, 0, 0, 0, 1, 2, 3, 0]); // odd size, so a padding byte follows
        }

        chunks.AddRange("ANIM"u8.ToArray());
        chunks.AddRange([6, 0, 0, 0, 0, 0, 0, 0, (byte)(loopCount & 0xFF), (byte)(loopCount >> 8)]);

        return [.. "RIFF"u8, .. BitConverter.GetBytes(chunks.Count + 4), .. "WEBP"u8, .. chunks];
    }

    [Fact]
    public void DetectsAnimationFlag()
    {
        Assert.True(WebPInfo.IsAnimated(ExtendedWebP(animated: true, iccFirst: false, 0)));
        Assert.False(WebPInfo.IsAnimated(ExtendedWebP(animated: false, iccFirst: false, 0)));
    }

    [Fact]
    public void SimpleWebPIsNeverAnimated()
    {
        byte[] simple = [.. "RIFF"u8, 30, 0, 0, 0, .. "WEBP"u8, .. "VP8L"u8, 10, 0, 0, 0, 0x2F, 0xFF, 0xFF, 0xFF, 0xFF];
        Assert.False(WebPInfo.IsAnimated(simple));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsLoopCountWalkingPastIccChunk(bool iccFirst) =>
        Assert.Equal(7, WebPInfo.LoopCount(ExtendedWebP(animated: true, iccFirst, loopCount: 7)));
}
