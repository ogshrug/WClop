using System.Globalization;
using System.Text.Json;
using WClop.Core.Processes;

namespace WClop.Core.Video;

/// <summary>What ffprobe reports about a video file.</summary>
public sealed record VideoInfo(
    TimeSpan Duration,
    int Width,
    int Height,
    double AverageFrameRate,
    double RealFrameRate,
    string VideoCodec,
    string? AudioCodec,
    long? Bitrate)
{
    public bool HasAudio => AudioCodec is not null;

    /// <summary>The highest rate frames arrive at (screen recordings report a low average while running at 60).</summary>
    public double PeakFrameRate => Math.Max(AverageFrameRate, RealFrameRate);

    /// <summary>
    /// Screen recordings are variable frame rate: they report their average (say 23 fps) while running at 60 when
    /// things move (project.md §9.3 step 4).
    /// </summary>
    public bool IsVariableFrameRate => AverageFrameRate > 0 && RealFrameRate > 0 && Math.Abs(AverageFrameRate - RealFrameRate) > 0.5;

    public static async Task<VideoInfo?> ProbeAsync(string ffprobe, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                ffprobe,
                ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(30) },
                cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? Parse(result.StdOut) : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Parses <c>ffprobe -print_format json -show_format -show_streams</c> output; null if there's no video stream.</summary>
    public static VideoInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams))
            return null;

        JsonElement? video = null;
        string? audioCodec = null;
        foreach (var stream in streams.EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            var disposition = stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1;
            if (type == "video" && video is null && !disposition)
                video = stream;
            else if (type == "audio" && audioCodec is null)
                audioCodec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : "unknown";
        }

        if (video is not { } v)
            return null;

        var duration = ParseDouble(root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var fd) ? fd.GetString() : null)
                       ?? ParseDouble(v.TryGetProperty("duration", out var vd) ? vd.GetString() : null)
                       ?? 0;
        long? bitrate = format.ValueKind == JsonValueKind.Object && format.TryGetProperty("bit_rate", out var br)
                        && long.TryParse(br.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)
            ? bits
            : null;

        return new VideoInfo(
            TimeSpan.FromSeconds(duration),
            v.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            v.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
            ParseRate(v.TryGetProperty("avg_frame_rate", out var avg) ? avg.GetString() : null),
            ParseRate(v.TryGetProperty("r_frame_rate", out var r) ? r.GetString() : null),
            v.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "unknown" : "unknown",
            audioCodec,
            bitrate);
    }

    /// <summary>"60000/1001" → 59.94; "0/0" → 0.</summary>
    public static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate))
            return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d > 0)
            return n / d;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static double? ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
