using System.Globalization;
using WClop.Core.Audio;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Pdf;
using WClop.Core.Processes;
using WClop.Core.Video;

namespace WClop.Core.Optimisation;

/// <summary>The arithmetic behind "fit under X" (project.md §12), kept pure so it can be tested.</summary>
public static class TargetSizeMath
{
    /// <summary>
    /// The next image compression factor to try, from how far over the target the last attempt was:
    /// more than 3× over jumps straight to 100, more than 1.5× adds 30, otherwise 18.
    /// </summary>
    public static int NextImageFactor(int factor, long size, long target)
    {
        var ratio = (double)size / target;
        return ratio > 3 ? 100 : Math.Min(100, factor + (ratio > 1.5 ? 30 : 18));
    }

    /// <summary>Downscale so the pixel count shrinks in proportion to the bytes still to lose, with a margin; at least 20 %.</summary>
    public static double NextImageScale(double scale, long size, long target) =>
        Math.Max(0.2, Math.Round(scale * Math.Sqrt((double)target / size) * 0.92, 3));

    /// <summary>
    /// Video bitrate for a target: bytes × 8 × 0.93 (7 % container overhead) ÷ duration, minus 128 kbps reserved for
    /// audio, at least 40 kbps.
    /// </summary>
    public static int VideoKbps(long targetBytes, double seconds, bool hasAudio) =>
        (int)Math.Max(40, targetBytes * 8 * 0.93 / Math.Max(0.1, seconds) / 1000 - (hasAudio ? 128 : 0));

    /// <summary>Bits per pixel per frame; below 0.04 the picture turns to mush.</summary>
    public static double BitsPerPixel(int kbps, int width, int height, double fps) =>
        kbps * 1000.0 / Math.Max(1, (double)width * height * Math.Max(1, fps));

    public const double MinBitsPerPixel = 0.04;

    /// <summary>Audio bitrate for a target: bytes × 8 × 0.95 ÷ duration, never above the source.</summary>
    public static int AudioKbps(long targetBytes, double seconds, int? sourceKbps) =>
        Math.Max(32, Math.Min(sourceKbps ?? int.MaxValue, (int)(targetBytes * 8 * 0.95 / Math.Max(0.1, seconds) / 1000)));

    /// <summary>
    /// The order PDF DPI stops are tried in: a binary search over 250 … 48 for the highest DPI that fits
    /// (at most 3 Ghostscript passes, §12).
    /// </summary>
    public static IReadOnlyList<int> PdfSearchStops => [250, 200, 150, 100, 72, 48];
}

public sealed class TargetSizeNotReachedException(long target, long best)
    : Images.OptimisationException($"Couldn't get under {target / 1024} KB; the smallest was {best / 1024} KB");

public sealed partial class FileOptimisationService
{
    /// <summary>
    /// Makes a file fit under <paramref name="targetBytes"/> (project.md §12), always working from the original.
    /// If the target can't be reached the smallest attempt is kept and marked <see cref="Produced.MissedTarget"/>.
    /// </summary>
    private async Task<Produced> FitAsync(
        MediaKind kind, string original, long targetBytes, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        var variant = string.Create(CultureInfo.InvariantCulture, $"fit:{targetBytes}");
        var attempts = new List<Produced>();
        Produced? winner = null;
        try
        {
            winner = kind switch
            {
                MediaKind.Image => await FitImageAsync(original, targetBytes, variant, attempts, cancellationToken).ConfigureAwait(false),
                MediaKind.Video => await FitVideoAsync(original, targetBytes, variant, attempts, onProgress, cancellationToken).ConfigureAwait(false),
                MediaKind.Pdf => await FitPdfAsync(original, targetBytes, variant, attempts, onProgress, cancellationToken).ConfigureAwait(false),
                MediaKind.Audio => await FitAudioAsync(original, targetBytes, variant, attempts, onProgress, cancellationToken).ConfigureAwait(false),
                _ => throw new UnsupportedFormatException("This kind of file can't be fitted to a size"),
            };
            return winner with { MissedTarget = Size(winner) > targetBytes };
        }
        finally
        {
            // Every attempt except the winner is a temporary file.
            foreach (var attempt in attempts.Where(a => winner is null || a.Output != winner.Output))
                TryDeleteFile(attempt.Output);
        }
    }

    private static long Size(Produced produced) => new FileInfo(produced.Output).Length;

    /// <summary>Records an attempt and returns the smallest so far (the rest are deleted when fitting ends).</summary>
    private static Produced Keep(List<Produced> attempts, Produced attempt)
    {
        attempts.Add(attempt);
        return attempts.MinBy(Size)!;
    }

    /// <summary>
    /// Images: aggressive first; then up to two probes at higher factors (from 64: +18, +30, or straight to 100);
    /// then, at maximum compression, downscale by √(target ÷ size) × 0.92 up to five times (§12).
    /// </summary>
    private async Task<Produced> FitImageAsync(
        string original, long target, string variant, List<Produced> attempts, CancellationToken cancellationToken)
    {
        async Task<Produced> Attempt(int factor, double scale) =>
            Keep(attempts, await ProduceImageAsync(
                original, new Adjustments(factor, scale, 1, null), allowLarger: true, variant, AutoConversionTarget(original), cancellationToken)
                .ConfigureAwait(false));

        var factor = Compression.CompressionModel.AggressiveImageFactor;
        var best = await Attempt(factor, 1).ConfigureAwait(false);
        for (var probe = 0; probe < 2 && Size(best) > target && factor < 100; probe++)
        {
            factor = TargetSizeMath.NextImageFactor(factor, Size(best), target);
            best = await Attempt(factor, 1).ConfigureAwait(false);
        }

        var scale = 1.0;
        for (var step = 0; step < 5 && Size(best) > target && scale > 0.2; step++)
        {
            scale = TargetSizeMath.NextImageScale(scale, Size(best), target);
            best = await Attempt(100, scale).ConfigureAwait(false);
        }

        return best;
    }

    /// <summary>
    /// Video: a bitrate from the target and duration; if that's under 0.04 bits per pixel, cap at 30 fps and then
    /// downscale ("fewer, better pixels beat a full-resolution mush"); one retry aiming lower if it overshoots (§12).
    /// </summary>
    private async Task<Produced> FitVideoAsync(
        string original, long target, string variant, List<Produced> attempts, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        var info = await VideoInfo.ProbeAsync(Tools.Require(Tool.Ffprobe), original, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnsupportedFormatException("Not a video ffmpeg can read");
        var hasAudio = info.HasAudio && !_settings.Compression.RemoveAudioFromVideos;
        var seconds = info.Duration.TotalSeconds;

        async Task<Produced> Attempt(long aim)
        {
            var kbps = TargetSizeMath.VideoKbps(aim, seconds, hasAudio);
            var fps = Math.Min(info.PeakFrameRate, _settings.Compression.CapVideoFps ? _settings.Compression.VideoFpsTarget : double.MaxValue);
            int? fpsCap = _settings.Compression.CapVideoFps ? _settings.Compression.VideoFpsTarget : null;
            var scale = 1.0;
            if (TargetSizeMath.BitsPerPixel(kbps, info.Width, info.Height, fps) < TargetSizeMath.MinBitsPerPixel)
            {
                if (fps > 30)
                {
                    fps = 30;
                    fpsCap = 30;
                }

                var bpp = TargetSizeMath.BitsPerPixel(kbps, info.Width, info.Height, fps);
                if (bpp < TargetSizeMath.MinBitsPerPixel)
                    scale = Math.Max(0.25, Math.Sqrt(bpp / TargetSizeMath.MinBitsPerPixel));
            }

            var output = await _videos.OptimiseAsync(original, new VideoOptimiseOptions
            {
                Scale = scale,
                FpsCap = fpsCap,
                RemoveAudio = !hasAudio,
                TargetKbps = kbps,
                AllowLarger = true,
            }, onProgress, cancellationToken).ConfigureAwait(false);
            return Keep(attempts, new Produced(output.Path, FileFormat.Mp4, false, variant));
        }

        var best = await Attempt(target).ConfigureAwait(false);
        if (Size(best) > target)
            best = await Attempt((long)(target * (double)target / Size(best) * 0.95)).ConfigureAwait(false);
        return best;
    }

    /// <summary>PDF: binary search over the DPI stops for the highest that fits, at most 3 passes (§12).</summary>
    private async Task<Produced> FitPdfAsync(
        string original, long target, string variant, List<Produced> attempts, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        var stops = TargetSizeMath.PdfSearchStops;
        int low = 0, high = stops.Count - 1;
        Produced? fitting = null;
        Produced? smallest = null;
        for (var pass = 0; pass < 3 && low <= high; pass++)
        {
            var middle = (low + high) / 2;
            var output = await _pdfs.OptimiseAsync(original, stops[middle], stops[middle], allowLarger: true, onProgress, cancellationToken)
                .ConfigureAwait(false);
            var attempt = new Produced(output.Path, FileFormat.Pdf, false, variant) { Dpi = output.Dpi, SourceDpi = output.SourceMaxDpi };
            smallest = Keep(attempts, attempt);
            if (new FileInfo(output.Path).Length <= target)
            {
                fitting = attempt;
                high = middle - 1; // fits: try a sharper DPI
            }
            else
            {
                low = middle + 1; // too big: go lower
            }
        }

        // Prefer the sharpest attempt that fits over merely the smallest.
        if (fitting is not null)
            return fitting;

        // Nothing fitted in 3 passes: one more at the lowest stop if it wasn't tried.
        if (smallest!.Dpi != stops[^1])
        {
            var output = await _pdfs.OptimiseAsync(original, stops[^1], stops[^1], allowLarger: true, onProgress, cancellationToken).ConfigureAwait(false);
            smallest = Keep(attempts, new Produced(output.Path, FileFormat.Pdf, false, variant) { Dpi = output.Dpi, SourceDpi = output.SourceMaxDpi });
        }

        return smallest;
    }

    /// <summary>Audio: bitrate = target × 8 × 0.95 ÷ duration, never above the source (§12).</summary>
    private async Task<Produced> FitAudioAsync(
        string original, long target, string variant, List<Produced> attempts, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        var info = await AudioInfo.ProbeAsync(Tools.Require(Tool.Ffprobe), original, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnsupportedFormatException("Not an audio file ffmpeg can read");
        var kbps = TargetSizeMath.AudioKbps(target, info.Duration.TotalSeconds, info.IsLossless ? null : info.BitrateKbps);
        var output = await _audio.OptimiseAsync(original, _settings.Compression.AudioFactor, allowLarger: true, onProgress, cancellationToken, kbps,
            coverArt: _settings.Compression.AudioCoverArt).ConfigureAwait(false);
        return Keep(attempts, new Produced(output.Path, output.Format, false, variant) { BitrateKbps = output.BitrateKbps });
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
