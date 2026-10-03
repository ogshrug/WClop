using System.Globalization;
using WClop.Core.Compression;
using WClop.Core.Settings;

namespace WClop.Core.Video;

/// <summary>Everything that decides how a video is re-encoded.</summary>
public sealed record VideoEncodeSpec
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public required VideoInfo Info { get; init; }

    /// <summary>The encoder to try first. Software (x264) is always the last fallback.</summary>
    public required VideoEncoder Encoder { get; init; }

    public int Factor { get; init; } = CompressionModel.DefaultVideoFactor;

    /// <summary>Lossless tier: x264 CRF 17 (§7).</summary>
    public bool Lossless { get; init; }

    /// <summary>0–1; 1 = full size.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Playback speed; 1 = unchanged, 2 = twice as fast.</summary>
    public double Speed { get; init; } = 1;

    /// <summary>
    /// With a speed change: stay at the source frame rate, dropping frames, instead of keeping every frame (the frame
    /// rate rises with the speed, up to the cap).
    /// </summary>
    public bool DropFrames { get; init; }

    /// <summary>Cap the frame rate at this value; null = no cap.</summary>
    public int? FpsCap { get; init; }

    public bool RemoveAudio { get; init; }

    /// <summary>Average video bitrate for a target size (x264, ABR); null = constant quality.</summary>
    public int? TargetKbps { get; init; }
}

/// <summary>
/// Builds ffmpeg argument sets for a video job (project.md §9.3). The result is a list of fallbacks tried in order:
/// the full set; without explicit stream mapping; with audio re-encoded instead of copied; and finally the same
/// with software x264, in case the hardware encoder rejects this particular file.
/// </summary>
public static class VideoArguments
{
    /// <summary>Audio codecs an MP4 can carry as-is.</summary>
    private static readonly HashSet<string> Mp4AudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "alac", "ac3", "eac3" };

    public static IReadOnlyList<IReadOnlyList<string>> Build(VideoEncodeSpec spec)
    {
        var sets = new List<IReadOnlyList<string>>
        {
            Assemble(spec, spec.Encoder, map: true, copyAudio: true),
            Assemble(spec, spec.Encoder, map: false, copyAudio: true),
            Assemble(spec, spec.Encoder, map: false, copyAudio: false),
        };
        if (spec.Encoder != VideoEncoder.X264)
            sets.Add(Assemble(spec, VideoEncoder.X264, map: false, copyAudio: false));

        // Identical sets are pointless retries (e.g. when audio is re-encoded anyway).
        return sets.DistinctBy(set => string.Join('\u0001', set)).ToList();
    }

    /// <summary>Codec arguments for one encoder at a quality. Hardware encoders take NV12 input.</summary>
    public static IReadOnlyList<string> EncoderArguments(VideoEncoder encoder, int crf, int hardwareQuality, string preset) => encoder switch
    {
        // NVENC's constant-quality value behaves close to x264's CRF.
        VideoEncoder.Nvenc => ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", I(crf + 1), "-b:v", "0", "-pix_fmt", "yuv420p"],
        // Quick Sync's ICQ mode: lower is better, like CRF.
        VideoEncoder.QuickSync => ["-c:v", "h264_qsv", "-preset", "slow", "-global_quality", I(crf + 2), "-pix_fmt", "nv12"],
        // AMF constant QP; P-frames a little coarser than I-frames.
        VideoEncoder.Amf => ["-c:v", "h264_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", I(crf), "-qp_p", I(crf + 2), "-pix_fmt", "nv12"],
        VideoEncoder.MediaFoundation => ["-c:v", "h264_mf", "-hw_encoding", "1", "-rate_control", "quality", "-quality", I(hardwareQuality), "-pix_fmt", "nv12"],
        _ => ["-c:v", "libx264", "-preset", preset, "-crf", I(crf), "-pix_fmt", "yuv420p"],
    };

    /// <summary>
    /// Whether the frame rate needs capping: only if the source (after any speed-up) runs above the cap.
    /// A cap at or above the source rate is omitted (§9.3 step 3).
    /// </summary>
    public static bool NeedsFpsCap(VideoEncodeSpec spec) =>
        spec.FpsCap is { } cap && spec.Info.PeakFrameRate * (spec.DropFrames ? 1 : spec.Speed) > cap + 0.5;

    /// <summary>atempo only takes 0.5–2.0 per filter, so larger changes are chained.</summary>
    public static string AtempoChain(double speed)
    {
        var filters = new List<string>();
        var remaining = speed;
        while (remaining > 2.0)
        {
            filters.Add("atempo=2.0");
            remaining /= 2.0;
        }

        while (remaining < 0.5)
        {
            filters.Add("atempo=0.5");
            remaining /= 0.5;
        }

        filters.Add("atempo=" + remaining.ToString("0.###", CultureInfo.InvariantCulture));
        return string.Join(",", filters);
    }

    private static List<string> Assemble(VideoEncodeSpec spec, VideoEncoder encoder, bool map, bool copyAudio)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-progress", "pipe:2", "-stats_period", "0.25", "-y", "-i", spec.Input,
        };

        var filters = new List<string>();
        if (Math.Abs(spec.Speed - 1) > 0.001)
            filters.Add("setpts=PTS/" + spec.Speed.ToString("0.###", CultureInfo.InvariantCulture));
        if (spec.Scale < 0.999)
        {
            var width = Even(spec.Info.Width * spec.Scale);
            var height = Even(spec.Info.Height * spec.Scale);
            filters.Add(I($"scale={width}:{height}:flags=bicubic"));
        }

        var capped = NeedsFpsCap(spec);
        var dropping = !capped && spec.DropFrames && Math.Abs(spec.Speed - 1) > 0.001 && spec.Info.PeakFrameRate > 0;
        if (capped)
            filters.Add("fps=" + I(spec.FpsCap!.Value));
        else if (dropping)
            filters.Add("fps=" + spec.Info.PeakFrameRate.ToString("0.###", CultureInfo.InvariantCulture));
        if (filters.Count > 0)
            args.AddRange(["-vf", string.Join(",", filters)]);

        if (!capped && !dropping)
        {
            // Keep every frame where it was. Resampling a variable-frame-rate recording to its (low) average rate
            // throws away exactly the frames where something moves (§9.3 step 4).
            args.AddRange(["-fps_mode", "passthrough", "-enc_time_base", filters.Count > 0 ? "filter" : "demux"]);
        }

        if (map)
            args.AddRange(["-map", "0:v:0", "-map", "0:a?"]);

        if (spec.TargetKbps is { } kbps)
        {
            // Target size (§12): average bitrate in software; hardware encoders ignore very low bitrate targets.
            args.AddRange(["-c:v", "libx264", "-preset", "fast", "-b:v", I(kbps) + "k",
                           "-maxrate", I((int)(kbps * 1.2)) + "k", "-bufsize", I(kbps * 2) + "k", "-pix_fmt", "yuv420p"]);
        }
        else
        {
            var crf = spec.Lossless ? 17 : CompressionModel.X264Crf(spec.Factor);
            args.AddRange(EncoderArguments(
                spec.Lossless ? VideoEncoder.X264 : encoder,
                crf,
                CompressionModel.MediaFoundationQuality(spec.Factor),
                spec.Lossless ? "medium" : CompressionModel.X264Preset(spec.Factor)));
        }

        if (spec.RemoveAudio || !spec.Info.HasAudio)
        {
            args.Add("-an");
        }
        else if (Math.Abs(spec.Speed - 1) > 0.001)
        {
            args.AddRange(["-af", AtempoChain(spec.Speed), "-c:a", "aac", "-b:a", "192k"]);
        }
        else if (spec.TargetKbps is not null)
        {
            args.AddRange(["-c:a", "aac", "-b:a", "128k"]); // a known size, reserved in the video budget
        }
        else if (copyAudio && Mp4AudioCodecs.Contains(spec.Info.AudioCodec!))
        {
            args.AddRange(["-c:a", "copy"]);
        }
        else
        {
            args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }

        args.AddRange(["-map_metadata", "0", "-movflags", "+faststart", spec.Output]);
        return args;
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string I(FormattableString value) => FormattableString.Invariant(value);
}
