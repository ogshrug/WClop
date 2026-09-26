using WClop.Core.Logging;
using WClop.Core.Processes;

namespace WClop.Core.Video;

public enum VideoEncoder
{
    /// <summary>libx264, software.</summary>
    X264,

    /// <summary>NVIDIA NVENC.</summary>
    Nvenc,

    /// <summary>Intel Quick Sync.</summary>
    QuickSync,

    /// <summary>AMD AMF.</summary>
    Amf,

    /// <summary>Windows Media Foundation, vendor-agnostic hardware encoding (uses the GPU's encoder through Windows).</summary>
    MediaFoundation,
}

/// <summary>
/// Finds which hardware H.264 encoder actually works (project.md §26.2: presence in the ffmpeg build doesn't mean
/// the GPU or driver supports it, e.g. NVENC needs a recent enough driver). Each candidate encodes a few tiny frames
/// with the same arguments real jobs use; the first that succeeds wins. Checked once, lazily.
/// </summary>
public static class VideoEncoders
{
    private static readonly VideoEncoder[] HardwareCandidates =
        [VideoEncoder.Nvenc, VideoEncoder.QuickSync, VideoEncoder.Amf, VideoEncoder.MediaFoundation];

    private static readonly object Gate = new();
    private static Task<VideoEncoder?>? _detection;

    /// <summary>The working hardware encoder, or null if none (then software is used).</summary>
    public static Task<VideoEncoder?> DetectHardwareAsync(string ffmpeg)
    {
        lock (Gate)
            return _detection ??= Task.Run(() => DetectAsync(ffmpeg));
    }

    private static async Task<VideoEncoder?> DetectAsync(string ffmpeg)
    {
        foreach (var encoder in HardwareCandidates)
        {
            var args = new List<string> { "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=black:s=320x240:r=30:d=0.2" };
            args.AddRange(VideoArguments.EncoderArguments(encoder, crf: 24, hardwareQuality: 40, preset: "fast"));
            args.AddRange(["-frames:v", "3", "-f", "null", "-"]);
            try
            {
                var result = await ProcessRunner.RunAsync(ffmpeg, args, new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(20) })
                    .ConfigureAwait(false);
                if (result.Succeeded)
                {
                    Log.Info($"Video: hardware encoder {encoder} works");
                    return encoder;
                }

                Log.Info($"Video: {encoder} unavailable: {result.StdErr.Split('\n').FirstOrDefault()?.Trim()}");
            }
            catch (TimeoutException)
            {
                Log.Info($"Video: {encoder} timed out");
            }
        }

        Log.Info("Video: no hardware encoder; using software (x264)");
        return null;
    }

    public static string DisplayName(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.Nvenc => "NVIDIA NVENC",
        VideoEncoder.QuickSync => "Intel Quick Sync",
        VideoEncoder.Amf => "AMD AMF",
        VideoEncoder.MediaFoundation => "Windows Media Foundation (hardware)",
        _ => "x264 (software)",
    };
}
