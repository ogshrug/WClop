namespace WClop.Core.Compression;

/// <summary>
/// Translates the single compression factor (5–100, higher = smaller files) into per-tool parameters.
/// Curves follow project.md §7.1. Factor 30 reproduces the "normal" preset and 64 the "aggressive" one.
/// </summary>
public static class CompressionModel
{
    public const int MinFactor = 5;
    public const int MaxFactor = 100;
    public const int DefaultImageFactor = 30;
    public const int AggressiveImageFactor = 64;

    public static int Clamp(int factor) => Math.Clamp(factor, MinFactor, MaxFactor);

    /// <summary>Factor 50 and above is labelled "aggressive" in the UI.</summary>
    public static bool IsAggressive(int factor) => factor >= 50;

    /// <summary>jpegoptim <c>--max</c> quality ceiling. f30 → 85, f64 → 58, f100 → 18.</summary>
    public static int JpegoptimMaxQuality(int factor)
    {
        double f = Clamp(factor);
        return f <= 70
            ? Round(Math.Clamp(85 - (f - 30) * 55 / 70, 25, 95))
            : Round(Math.Max(54 - (f - 70) * 36 / 30, 18));
    }

    /// <summary>jpegoptim quality for the retry after the first pass fails. f30 → 90.</summary>
    public static int JpegoptimFallbackQuality(int factor)
    {
        double f = Clamp(factor);
        return f <= 70
            ? Round(Math.Clamp(90 - (f - 30) * 60 / 70, 20, 95))
            : Round(Math.Max(56 - (f - 70) * 36 / 30, 20));
    }

    /// <summary>Upper bound for pngquant <c>--quality 0-MAX</c>. f30 → 100, f100 → 25.</summary>
    public static int PngquantMaxQuality(int factor)
    {
        double f = Clamp(factor);
        return Round(Math.Clamp(100 - (f - 30) * 75 / 70, 25, 100));
    }

    /// <summary>pngquant palette size. 256 below f80, then down to 64 at f100.</summary>
    public static int PngquantColors(int factor)
    {
        var f = Clamp(factor);
        return f < 80 ? 256 : Math.Max(224 - (f - 80) * 8, 64);
    }

    /// <summary>pngquant <c>--speed</c>: slower (lower) compresses better.</summary>
    public static int PngquantSpeed(int factor) => Clamp(factor) switch
    {
        < 40 => 4,
        < 60 => 3,
        < 85 => 2,
        _ => 1,
    };

    /// <summary>gifsicle <c>-O</c> level.</summary>
    public static int GifsicleOptimisationLevel(int factor) => Clamp(factor) switch
    {
        >= 50 => 3,
        >= 20 => 2,
        _ => 1,
    };

    /// <summary>gifsicle <c>--lossy</c>. f30 → 30, f64 → 80, then exponential from 89 at f70 to 2000 at f100.</summary>
    public static int GifsicleLossy(int factor)
    {
        double f = Clamp(factor);
        if (f <= 70)
            return Round(Math.Clamp(30 + (f - 30) * 50 / 34, 0, 200));

        const double start = 89;
        const double end = 2000;
        return Round(start * Math.Pow(end / start, (f - 70) / 30));
    }

    /// <summary>gifsicle <c>--colors</c>, only from f50: 256 down to a minimum of 32. Null means keep the palette.</summary>
    public static int? GifsicleColors(int factor)
    {
        double f = Clamp(factor);
        if (f < 50)
            return null;
        return Round(Math.Max(256 - (f - 50) * 192 / 50, 32));
    }

    /// <summary>
    /// Drop every Nth frame of GIFs with more than <see cref="GifFrameDropMinFrames"/> frames.
    /// Null below f80. 80–89 → every 4th, 90–97 → every 3rd, 98+ → every 2nd.
    /// </summary>
    public static int? GifFrameDropInterval(int factor) => Clamp(factor) switch
    {
        < 80 => null,
        < 90 => 4,
        < 98 => 3,
        _ => 2,
    };

    public const int GifFrameDropMinFrames = 8;

    /// <summary>Quality for WebP / HEIC / AVIF encoders. f30 → 60.</summary>
    public static int ModernFormatQuality(int factor)
    {
        double f = Clamp(factor);
        return f <= 70
            ? Round(Math.Clamp(75 - f * 0.5, 20, 90))
            : Round(Math.Max(40 - (f - 70) * 25 / 30, 15));
    }

    /// <summary>JPEG XL quality: the same curve as <see cref="ModernFormatQuality"/>, capped at 95.</summary>
    public static int JpegXlQuality(int factor) => Math.Min(ModernFormatQuality(factor), 95);

    public static int JpegXlEffort(int factor) => Clamp(factor) switch
    {
        < 50 => 7,
        < 70 => 8,
        _ => 9,
    };

    public const int DefaultVideoFactor = 50;

    /// <summary>libx264 CRF (§7.2). f5 → 18, f50 → 24, f100 → 38.</summary>
    public static int X264Crf(int factor)
    {
        double f = Clamp(factor);
        return f <= 70
            ? Round(Math.Clamp(18 + (f - 5) / 95 * 12, 17, 32))
            : Round(Math.Min(26 + (f - 70) * 12 / 30, 38));
    }

    /// <summary>libx264 preset: slower presets compress better at the same quality (§7.2).</summary>
    public static string X264Preset(int factor) => Clamp(factor) switch
    {
        < 20 => "veryfast",
        < 40 => "fast",
        < 60 => "medium",
        < 85 => "slow",
        _ => "slower",
    };

    /// <summary>Clop's hardware H.264 quality (higher = better), tuned for Apple's encoder (§7.2). f50 → 49.</summary>
    public static int HardwareQuality(int factor)
    {
        double f = Clamp(factor);
        return f <= 70
            ? Round(Math.Clamp(70 - (f - 5) / 95 * 45, 25, 75))
            : Round(Math.Max(39 - (f - 70) * 21 / 30, 18));
    }

    /// <summary>
    /// Media Foundation quality (0–100, higher = better). Its scale runs larger than Apple's, so this is calibrated
    /// separately: on a 1080p60 test clip, 39 at f50 gives a file close to x264 CRF 24. f5 → 60, f100 → 15.
    /// </summary>
    public static int MediaFoundationQuality(int factor)
    {
        double f = Clamp(factor);
        return Round(Math.Clamp(60 - (f - 5) * 45 / 95, 10, 60));
    }

    /// <summary>x265 CRF for HEVC conversions (§7.2): 18 → 26 at f50 → 40 at f100.</summary>
    public static int HevcCrf(int factor) => Piecewise(factor, 18, 26, 40);

    /// <summary>VP9 CRF for WebM conversions (§7.2): 18 → 31 at f50 → 50 at f100.</summary>
    public static int Vp9Crf(int factor) => Piecewise(factor, 18, 31, 50);

    /// <summary>Linear from f5 to f50, then from f50 to f100.</summary>
    private static int Piecewise(int factor, double atMin, double atNormal, double atMax)
    {
        double f = Clamp(factor);
        return f <= 50
            ? Round(atMin + (f - 5) / 45 * (atNormal - atMin))
            : Round(atNormal + (f - 50) / 50 * (atMax - atNormal));
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
