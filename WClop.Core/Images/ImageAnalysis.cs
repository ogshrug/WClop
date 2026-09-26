using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WClop.Core.Images;

/// <summary>Pixel statistics used to pick formats (project.md §8.2, §8.3, §8.9).</summary>
public static class ImageAnalysis
{
    public sealed record Pixels(int Width, int Height, byte[] Bgra);

    /// <summary>First frame as 32-bit BGRA.</summary>
    public static Pixels Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return new Pixels(bgra.PixelWidth, bgra.PixelHeight, pixels);
    }

    /// <summary>Any pixel that isn't fully opaque. PNGs with transparency are never turned into JPEGs.</summary>
    public static bool HasTransparency(Pixels image)
    {
        for (var i = 3; i < image.Bgra.Length; i += 4)
        {
            if (image.Bgra[i] != 255)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Shannon entropy over the R, G and B histograms together (256 bins each): −Σ p·log₂p. Flat, graphic-like
    /// content (UI screenshots) scores low; photos score high. Clop treats below 5 as "graphic" (§8.3).
    /// </summary>
    public static double Entropy(Pixels image)
    {
        var histogram = new long[3 * 256];
        for (var i = 0; i < image.Bgra.Length; i += 4)
        {
            histogram[image.Bgra[i]]++;             // B
            histogram[256 + image.Bgra[i + 1]]++;   // G
            histogram[512 + image.Bgra[i + 2]]++;   // R
        }

        double total = image.Bgra.Length / 4 * 3;
        var entropy = 0.0;
        foreach (var count in histogram)
        {
            if (count == 0)
                continue;
            var p = count / total;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    /// <summary>
    /// Unique colours, counted on at most 262,144 pixels picked nearest-neighbour (so no new colours are invented),
    /// stopping as soon as there are more than <paramref name="stopAbove"/> (§8.9).
    /// </summary>
    public static int CountColors(Pixels image, int stopAbove = 256)
    {
        const int maxSamples = 262_144;
        var pixelCount = (long)image.Width * image.Height;
        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(pixelCount / (double)maxSamples)));
        var colors = new HashSet<uint>();

        for (var y = 0; y < image.Height; y += step)
        {
            for (var x = 0; x < image.Width; x += step)
            {
                var i = (y * image.Width + x) * 4;
                colors.Add(BitConverter.ToUInt32(image.Bgra, i));
                if (colors.Count > stopAbove)
                    return colors.Count;
            }
        }

        return colors.Count;
    }
}
