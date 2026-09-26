using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WClop.Core.Images;

/// <summary>
/// Smart crop (project.md §19.2): where to put a crop window so it keeps the most detailed part of an image.
/// Detail is edge energy (gradient magnitude) on a small greyscale copy; ties go to the window nearest the centre.
/// </summary>
public static class ImageInterest
{
    private const int AnalysisSize = 256;

    /// <summary>Top-left corner of the best <paramref name="width"/>×<paramref name="height"/> window, in full-size pixels.</summary>
    public static (int X, int Y) BestWindow(string path, ImageSize size, int width, int height)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var scale = Math.Min(1.0, (double)AnalysisSize / Math.Max(frame.PixelWidth, frame.PixelHeight));
        BitmapSource small = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
        var gray = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);

        var w = gray.PixelWidth;
        var h = gray.PixelHeight;
        var pixels = new byte[w * h];
        gray.CopyPixels(pixels, w, 0);
        return BestWindow(pixels, w, h, size, width, height);
    }

    /// <summary>The search itself, on greyscale pixels (<paramref name="w"/>×<paramref name="h"/>) standing in for the full image.</summary>
    internal static (int X, int Y) BestWindow(byte[] pixels, int w, int h, ImageSize size, int width, int height)
    {
        // Integral image of edge energy, so every window's total is four lookups.
        var integral = new double[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            double row = 0;
            for (var x = 0; x < w; x++)
            {
                var p = pixels[y * w + x];
                var dx = x + 1 < w ? Math.Abs(pixels[y * w + x + 1] - p) : 0;
                var dy = y + 1 < h ? Math.Abs(pixels[(y + 1) * w + x] - p) : 0;
                row += dx + dy;
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
            }
        }

        var sx = (double)w / size.Width;
        var sy = (double)h / size.Height;
        var ww = Math.Clamp((int)Math.Round(width * sx), 1, w);
        var wh = Math.Clamp((int)Math.Round(height * sy), 1, h);
        double Energy(int x, int y) =>
            integral[(y + wh) * (w + 1) + x + ww] - integral[y * (w + 1) + x + ww] - integral[(y + wh) * (w + 1) + x] + integral[y * (w + 1) + x];

        var total = Math.Max(1, integral[h * (w + 1) + w]);
        var centreX = (w - ww) / 2.0;
        var centreY = (h - wh) / 2.0;
        var maxDistance = Math.Max(1, Math.Sqrt(centreX * centreX + centreY * centreY));

        (int X, int Y) best = ((int)centreX, (int)centreY);
        var bestScore = double.MinValue;
        for (var y = 0; y <= h - wh; y++)
        {
            for (var x = 0; x <= w - ww; x++)
            {
                // A 5% pull towards the centre breaks ties on flat images.
                var distance = Math.Sqrt((x - centreX) * (x - centreX) + (y - centreY) * (y - centreY)) / maxDistance;
                var score = Energy(x, y) / total - 0.05 * distance;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (x, y);
                }
            }
        }

        var fullX = Math.Clamp((int)Math.Round(best.X / sx), 0, size.Width - width);
        var fullY = Math.Clamp((int)Math.Round(best.Y / sy), 0, size.Height - height);
        return (fullX, fullY);
    }
}
