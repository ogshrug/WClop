using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace WClop.Core.Pdf;

/// <summary>What a PDF contains, for choosing how hard to compress it (project.md §10.3).</summary>
/// <param name="ImageDpis">Effective resolution of each image as it's drawn on its page.</param>
public sealed record PdfAnalysis(int Pages, bool Encrypted, IReadOnlyList<double> ImageDpis)
{
    /// <summary>The DPI stops Clop offers; 300 means "no downsampling, lossless" (§10.2).</summary>
    public static readonly IReadOnlyList<int> DpiStops = [300, 250, 200, 150, 100, 72, 48];

    public const int LosslessDpi = 300;

    /// <summary>The highest image resolution in the document (shown as "300 → 150 DPI").</summary>
    public double? MaxDpi => ImageDpis.Count == 0 ? null : ImageDpis.Max();

    /// <summary>
    /// Reads page count, encryption and every image's effective DPI. Unlike Clop's full-page estimate (§10.3), this
    /// uses each image's actual size on the page, so small images aren't mistaken for low-resolution ones.
    /// </summary>
    public static PdfAnalysis Analyse(string path)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            var dpis = new List<double>();
            foreach (var page in document.GetPages())
            {
                foreach (var image in page.GetImages())
                {
                    var widthInches = image.BoundingBox.Width / 72.0;
                    var heightInches = image.BoundingBox.Height / 72.0;
                    if (widthInches < 0.05 || heightInches < 0.05 || image.WidthInSamples <= 1 || image.HeightInSamples <= 1)
                        continue; // invisible, degenerate or a 1-pixel mask

                    dpis.Add((image.WidthInSamples / widthInches + image.HeightInSamples / heightInches) / 2);
                }
            }

            return new PdfAnalysis(document.NumberOfPages, document.IsEncrypted, dpis);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new PdfAnalysis(0, true, []);
        }
    }

    /// <summary>
    /// Adaptive DPI (§10.3): drops abnormally low outliers (below the lower Tukey fence, Q1 − 1.5·IQR), then picks the
    /// highest stop at or below <paramref name="cap"/> that more than 3 images reach (with fewer images, all of them).
    /// Documents without countable images use the cap.
    /// </summary>
    public static int ChooseDpi(IReadOnlyList<double> imageDpis, int cap)
    {
        var stops = DpiStops.Where(s => s <= cap).ToList();
        if (stops.Count == 0)
            return DpiStops[^1];
        if (imageDpis.Count == 0)
            return stops[0];

        var sorted = imageDpis.Order().ToList();
        var q1 = Quantile(sorted, 0.25);
        var q3 = Quantile(sorted, 0.75);
        var fence = q1 - 1.5 * (q3 - q1);
        var kept = sorted.Where(d => d >= fence).ToList();

        var required = Math.Min(4, kept.Count);
        foreach (var stop in stops)
        {
            if (kept.Count(d => d >= stop) >= required)
                return stop;
        }

        return stops[^1];
    }

    /// <summary>The next lower DPI stop, for stepping down with the − key (§10.6).</summary>
    public static int NextLowerDpi(int dpi) => DpiStops.FirstOrDefault(s => s < dpi, DpiStops[^1]);

    private static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        var position = (sorted.Count - 1) * q;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
