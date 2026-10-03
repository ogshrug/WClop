using System.Globalization;
using WClop.Core.Images;

namespace WClop.Core.Cropping;

/// <summary>
/// What to crop to (project.md §8.9 CropSize): a pixel size, a width or height alone, an aspect ratio (optionally with a
/// width or height), and whether a smart crop may move the window off-centre. Shared by the <c>crop</c> pipeline step,
/// the result cards and <c>wclop crop</c>.
/// </summary>
public sealed record CropSpec
{
    public int? Width { get; init; }
    public int? Height { get; init; }

    /// <summary>Width ÷ height, e.g. 16/9.</summary>
    public double? AspectRatio { get; init; }

    /// <summary>Keep the most detailed part of an image instead of the centre (images only; videos crop from the centre).</summary>
    public bool Smart { get; init; }

    /// <summary>The ratios offered on result cards, as typed.</summary>
    public static readonly IReadOnlyList<string> AspectPresets = ["16:9", "4:3", "1:1", "9:16", "1.91:1"];

    public bool IsEmpty => Width is null && Height is null && AspectRatio is null;

    /// <summary>"16:9", "1.91:1", "16/9" or "4x3" → width ÷ height.</summary>
    public static bool TryParseRatio(string? text, out double ratio)
    {
        ratio = 0;
        var parts = (text ?? "").Trim().Split(':', '/', 'x', 'X', '×');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || w <= 0 || h <= 0)
            return false;
        ratio = w / h;
        return true;
    }

    /// <summary>"1920x1080", "1920×1080", "1920x" (width only), "x1080" (height only) or "1920" (width).</summary>
    public static bool TryParseSize(string? text, out int? width, out int? height)
    {
        width = height = null;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return false;
        var parts = trimmed.Split('x', 'X', '×', '*');
        if (parts.Length > 2)
            return false;

        static bool Side(string part, out int? value)
        {
            value = null;
            if (part.Trim().Length == 0)
                return true;
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixels) || pixels < 2)
                return false;
            value = pixels;
            return true;
        }

        if (!Side(parts[0], out width) || (parts.Length == 2 && !Side(parts[1], out height)))
            return false;
        return width is not null || height is not null;
    }

    /// <summary>
    /// Builds a crop from command-line style text (<c>--size</c>, <c>--aspect</c>); null with an error if neither is
    /// usable.
    /// </summary>
    public static CropSpec? Parse(string? size, string? aspect, bool smart, out string? error)
    {
        error = null;
        int? width = null, height = null;
        double? ratio = null;
        if (size is not null && !TryParseSize(size, out width, out height))
            error = $"'{size}' isn't a size like 1920x1080 (or 1920x for a width, x1080 for a height)";
        if (aspect is not null)
        {
            if (TryParseRatio(aspect, out var parsed))
                ratio = parsed;
            else
                error = $"'{aspect}' isn't a ratio like 16:9";
        }

        if (error is not null)
            return null;
        if (width is null && height is null && ratio is null)
        {
            error = "Say what to crop to: --size 1920x1080 or --aspect 16:9";
            return null;
        }

        return new CropSpec { Width = width, Height = height, AspectRatio = ratio, Smart = smart };
    }

    /// <summary>
    /// The crop window inside <paramref name="size"/>, centred; null if the crop wouldn't change anything. A size larger
    /// than the image is clamped to it; with a ratio, the window is the largest of that shape that fits (or the given
    /// width / height). <paramref name="even"/> rounds to even numbers, which video encoders need.
    /// </summary>
    public (int Width, int Height, int X, int Y)? Rectangle(ImageSize size, bool even)
    {
        double width = size.Width, height = size.Height;
        if (AspectRatio is { } ratio)
        {
            if (Width is { } fixedWidth)
            {
                width = Math.Min(fixedWidth, size.Width);
                height = width / ratio;
            }
            else if (Height is { } fixedHeight)
            {
                height = Math.Min(fixedHeight, size.Height);
                width = height * ratio;
            }
            else if ((double)size.Width / size.Height > ratio)
            {
                width = size.Height * ratio;
            }
            else
            {
                height = size.Width / ratio;
            }

            // Shrink to fit if the other side came out too big.
            var fit = Math.Min(1, Math.Min(size.Width / width, size.Height / height));
            width *= fit;
            height *= fit;
        }
        else
        {
            width = Math.Min(Width ?? size.Width, size.Width);
            height = Math.Min(Height ?? size.Height, size.Height);
        }

        var w = (int)Math.Round(width);
        var h = (int)Math.Round(height);
        if (even)
        {
            w -= w % 2;
            h -= h % 2;
        }

        w = Math.Max(w, 2);
        h = Math.Max(h, 2);
        if (w >= size.Width && h >= size.Height)
            return null;
        return (w, h, (size.Width - w) / 2, (size.Height - h) / 2);
    }

    /// <summary>For status lines: "16:9", "1280×720", "1280×720 at 16:9", plus " (smart)".</summary>
    public override string ToString()
    {
        var size = (Width, Height) switch
        {
            ({ } w, { } h) => $"{w}×{h}",
            ({ } w, null) => $"{w} px wide",
            (null, { } h) => $"{h} px high",
            _ => null,
        };
        var ratio = AspectRatio is { } r ? RatioText(r) : null;
        var text = (size, ratio) switch
        {
            ({ } s, { } a) => $"{s} at {a}",
            ({ } s, null) => s,
            (null, { } a) => a,
            _ => "the same size",
        };
        return Smart ? text + " (smart)" : text;
    }

    /// <summary>A preset's own text when the ratio is one ("16:9"), else the decimal ("1.33:1").</summary>
    public static string RatioText(double ratio) =>
        AspectPresets.FirstOrDefault(p => TryParseRatio(p, out var preset) && Math.Abs(preset - ratio) < 0.001)
        ?? ratio.ToString("0.##", CultureInfo.InvariantCulture) + ":1";
}
