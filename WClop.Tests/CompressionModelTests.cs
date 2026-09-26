using WClop.Core.Compression;

namespace WClop.Tests;

/// <summary>Checks the curves against the example values in project.md §7.1.</summary>
public class CompressionModelTests
{
    [Theory]
    [InlineData(30, 85)]
    [InlineData(64, 58)]
    [InlineData(100, 18)]
    [InlineData(5, 95)] // clamped
    public void JpegoptimMax(int factor, int expected) =>
        Assert.Equal(expected, CompressionModel.JpegoptimMaxQuality(factor));

    [Fact]
    public void JpegoptimFallbackAtNormal() => Assert.Equal(90, CompressionModel.JpegoptimFallbackQuality(30));

    [Theory]
    [InlineData(30, 100)]
    [InlineData(100, 25)]
    [InlineData(5, 100)]
    public void PngquantQuality(int factor, int expected) =>
        Assert.Equal(expected, CompressionModel.PngquantMaxQuality(factor));

    [Theory]
    [InlineData(30, 256)]
    [InlineData(79, 256)]
    [InlineData(80, 224)]
    [InlineData(100, 64)]
    public void PngquantColors(int factor, int expected) =>
        Assert.Equal(expected, CompressionModel.PngquantColors(factor));

    [Theory]
    [InlineData(30, 4)]
    [InlineData(40, 3)]
    [InlineData(60, 2)]
    [InlineData(85, 1)]
    public void PngquantSpeed(int factor, int expected) =>
        Assert.Equal(expected, CompressionModel.PngquantSpeed(factor));

    [Theory]
    [InlineData(10, 1)]
    [InlineData(20, 2)]
    [InlineData(50, 3)]
    public void GifsicleLevel(int factor, int expected) =>
        Assert.Equal(expected, CompressionModel.GifsicleOptimisationLevel(factor));

    [Theory]
    [InlineData(30, 30)]
    [InlineData(64, 80)]
    [InlineData(70, 89)]
    [InlineData(100, 2000)]
    public void GifsicleLossy(int factor, int expected) =>
        Assert.Equal(expected, CompressionModel.GifsicleLossy(factor));

    [Fact]
    public void GifsicleLossyIsMonotonic()
    {
        var values = Enumerable.Range(5, 96).Select(CompressionModel.GifsicleLossy).ToList();
        Assert.Equal(values.Order(), values);
    }

    [Theory]
    [InlineData(49, null)]
    [InlineData(50, 256)]
    [InlineData(64, 202)]
    [InlineData(100, 64)]
    public void GifsicleColors(int factor, int? expected) =>
        Assert.Equal(expected, CompressionModel.GifsicleColors(factor));

    [Theory]
    [InlineData(79, null)]
    [InlineData(80, 4)]
    [InlineData(90, 3)]
    [InlineData(98, 2)]
    public void GifFrameDrop(int factor, int? expected) =>
        Assert.Equal(expected, CompressionModel.GifFrameDropInterval(factor));

    [Theory]
    [InlineData(30, 60)]
    [InlineData(70, 40)]
    [InlineData(100, 15)]
    public void ModernFormatQuality(int factor, int expected) =>
        Assert.Equal(expected, CompressionModel.ModernFormatQuality(factor));

    [Theory]
    [InlineData(30, 7)]
    [InlineData(50, 8)]
    [InlineData(70, 9)]
    public void JpegXlEffort(int factor, int expected) =>
        Assert.Equal(expected, CompressionModel.JpegXlEffort(factor));

    [Fact]
    public void FactorIsClamped()
    {
        Assert.Equal(CompressionModel.JpegoptimMaxQuality(100), CompressionModel.JpegoptimMaxQuality(500));
        Assert.Equal(CompressionModel.JpegoptimMaxQuality(5), CompressionModel.JpegoptimMaxQuality(-3));
    }
}
