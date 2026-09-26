using System.Globalization;
using WClop.Core.Images;
using WClop.Core.Logging;
using WClop.Core.Media;
using WClop.Core.Processes;
using WClop.Core.Settings;

namespace WClop.Core.Video;

public sealed record VideoOptimiseOptions
{
    public int Factor { get; init; } = Compression.CompressionModel.DefaultVideoFactor;
    public VideoTier Tier { get; init; } = VideoTier.Fast;
    public double Scale { get; init; } = 1;
    public double Speed { get; init; } = 1;
    public int? FpsCap { get; init; } = 60;
    public bool RemoveAudio { get; init; }
    public bool AllowLarger { get; init; }

    /// <summary>Target size mode: average video bitrate (forces x264).</summary>
    public int? TargetKbps { get; init; }
}

public sealed record VideoOptimiseOutput(string Path, long InputSize, long OutputSize, VideoInfo Info, VideoEncoder Encoder);

/// <summary>
/// Re-encodes a video to H.264 MP4 into the working directory (project.md §9). The input is never modified.
/// </summary>
public sealed class VideoOptimiser(ToolLocator tools, AppPaths paths)
{
    public async Task<VideoOptimiseOutput> OptimiseAsync(
        string input, VideoOptimiseOptions options, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        var ffmpeg = tools.Require(Tool.Ffmpeg);
        var ffprobe = tools.Require(Tool.Ffprobe);
        var info = await VideoInfo.ProbeAsync(ffprobe, input, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnsupportedFormatException("Not a video ffmpeg can read");
        if (info.Duration <= TimeSpan.Zero)
            throw new UnsupportedFormatException("The video has no duration (still being written, or damaged)");

        var encoder = await ChooseEncoderAsync(ffmpeg, options, info, new FileInfo(input).Length).ConfigureAwait(false);
        Directory.CreateDirectory(paths.Videos);
        var output = AppPaths.NewTempPath(paths.Videos, ".mp4");

        var spec = new VideoEncodeSpec
        {
            Input = input,
            Output = output,
            Info = info,
            Encoder = encoder,
            Factor = options.Factor,
            Lossless = options.Tier == VideoTier.Lossless,
            Scale = options.Scale,
            Speed = options.Speed,
            FpsCap = options.FpsCap,
            RemoveAudio = options.RemoveAudio,
            TargetKbps = options.TargetKbps,
        };

        var expectedSeconds = info.Duration.TotalSeconds / options.Speed;
        var runOptions = new ProcessRunOptions
        {
            Timeout = TimeSpan.FromMinutes(Math.Max(10, expectedSeconds / 60 * 5)),
            OnStdErrLine = line => ReportProgress(line, expectedSeconds, onProgress),
        };

        try
        {
            var result = await ProcessRunner.RunFirstSuccessfulAsync(ffmpeg, VideoArguments.Build(spec), runOptions, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Arguments.Contains(EncoderName(encoder)))
                Log.Info($"Video: {encoder} failed on this file; encoded with the fallback instead");

            var outputInfo = await VideoInfo.ProbeAsync(ffprobe, output, cancellationToken).ConfigureAwait(false);
            if (outputInfo is null || outputInfo.Duration <= TimeSpan.Zero)
                throw new OutputUnreadableException("The encoded video can't be read back");

            var inputSize = new FileInfo(input).Length;
            var outputSize = new FileInfo(output).Length;
            if (!options.AllowLarger && outputSize >= inputSize)
                throw new NotSmallerException(inputSize, outputSize);

            onProgress?.Invoke(1);
            return new VideoOptimiseOutput(output, inputSize, outputSize, outputInfo, encoder);
        }
        catch
        {
            TryDelete(output);
            throw;
        }
    }

    /// <summary>
    /// Fast = hardware when available; Smaller and Lossless = software; Adaptive = software for short or small clips
    /// (better compression, quick anyway), hardware for long recordings.
    /// </summary>
    private static async Task<VideoEncoder> ChooseEncoderAsync(string ffmpeg, VideoOptimiseOptions options, VideoInfo info, long size)
    {
        if (options.Tier is VideoTier.Smaller or VideoTier.Lossless or VideoTier.Custom)
            return VideoEncoder.X264;

        if (options.Tier == VideoTier.Adaptive && (info.Duration < TimeSpan.FromMinutes(1) || size < 100L * 1024 * 1024))
            return VideoEncoder.X264;

        return await VideoEncoders.DetectHardwareAsync(ffmpeg).ConfigureAwait(false) ?? VideoEncoder.X264;
    }

    private static string EncoderName(VideoEncoder encoder) => VideoArguments.EncoderArguments(encoder, 24, 40, "fast")[1];

    /// <summary>ffmpeg <c>-progress</c> lines: <c>out_time_us=12345678</c>.</summary>
    internal static void ReportProgress(string line, double expectedSeconds, Action<double>? onProgress)
    {
        if (onProgress is null || expectedSeconds <= 0)
            return;
        const string key = "out_time_us=";
        if (line.StartsWith(key, StringComparison.Ordinal)
            && long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
            && microseconds >= 0)
        {
            onProgress(Math.Clamp(microseconds / 1_000_000.0 / expectedSeconds, 0, 0.99));
        }
    }

    private static void TryDelete(string path)
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

/// <summary>Grabs a frame for result thumbnails.</summary>
public static class VideoThumbnail
{
    /// <summary>A PNG of a frame near the start (1 s in, or the first frame for short clips); null if it fails.</summary>
    public static async Task<string?> ExtractAsync(string ffmpeg, string video, string folder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var output = AppPaths.NewTempPath(folder, ".png");
        foreach (var seek in new[] { "1", "0" })
        {
            try
            {
                var result = await ProcessRunner.RunAsync(ffmpeg,
                    ["-hide_banner", "-v", "error", "-y", "-ss", seek, "-i", video, "-frames:v", "1", "-vf", "scale=320:-2", output],
                    new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(20) }, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded && File.Exists(output) && new FileInfo(output).Length > 0)
                    return output;
            }
            catch (TimeoutException)
            {
            }
        }

        return null;
    }
}
