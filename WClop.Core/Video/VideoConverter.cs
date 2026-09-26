using System.Globalization;
using WClop.Core.Compression;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Processes;
using WClop.Core.Settings;

namespace WClop.Core.Video;

/// <summary>
/// Converts videos to other formats (project.md §9.4, §7.2): an animated GIF, VP9 WebM, or HEVC MP4.
/// Output goes to the working directory's conversions folder.
/// </summary>
public sealed class VideoConverter(ToolLocator tools, AppPaths paths)
{
    public static readonly IReadOnlyList<FileFormat> Targets = [FileFormat.Gif, FileFormat.WebM, FileFormat.Mp4];

    /// <summary>GIFs get big fast: cap the frame rate and width.</summary>
    public const int GifFps = 15;
    public const int GifMaxWidth = 720;

    public async Task<string> ConvertAsync(
        string input, FileFormat target, int factor, VideoTier tier, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        var ffmpeg = tools.Require(Tool.Ffmpeg);
        var info = await VideoInfo.ProbeAsync(tools.Require(Tool.Ffprobe), input, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnsupportedFormatException("Not a video ffmpeg can read");

        Directory.CreateDirectory(paths.Conversions);
        var extension = target == FileFormat.Mp4 ? ".mp4" : target.Extension();
        var output = AppPaths.NewTempPath(paths.Conversions, extension);

        var hardware = tier == VideoTier.Fast ? await VideoEncoders.DetectHardwareAsync(ffmpeg).ConfigureAwait(false) : null;
        var argumentSets = target switch
        {
            FileFormat.Gif => [GifArguments(input, output, info)],
            FileFormat.WebM => [WebMArguments(input, output, factor)],
            FileFormat.Mp4 when hardware == VideoEncoder.MediaFoundation =>
                new List<IReadOnlyList<string>> { HevcArguments(input, output, factor, hardwareQuality: true), HevcArguments(input, output, factor, hardwareQuality: false) },
            FileFormat.Mp4 => [HevcArguments(input, output, factor, hardwareQuality: false)],
            _ => throw new UnsupportedFormatException($"Converting a video to {target} isn't supported"),
        };

        var seconds = info.Duration.TotalSeconds;
        try
        {
            await ProcessRunner.RunFirstSuccessfulAsync(ffmpeg, argumentSets, new ProcessRunOptions
            {
                Timeout = TimeSpan.FromMinutes(Math.Max(10, seconds / 60 * 10)),
                OnStdErrLine = line => VideoOptimiser.ReportProgress(line, seconds, onProgress),
            }, cancellationToken).ConfigureAwait(false);

            if (target == FileFormat.Gif && GifInfo.Read(output).FrameCount == 0)
                throw new OutputUnreadableException("The GIF came out empty");
            if (target != FileFormat.Gif && await VideoInfo.ProbeAsync(tools.Require(Tool.Ffprobe), output, cancellationToken) is not { Duration.TotalSeconds: > 0 })
                throw new OutputUnreadableException("The converted video can't be read back");

            onProgress?.Invoke(1);
            return output;
        }
        catch
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    /// <summary>
    /// One pass with a palette built over the whole clip (<c>stats_mode=diff</c> favours what moves) and ordered dithering,
    /// which also compresses better than error diffusion. Clop assembles frames with gifski; this needs no extra tool.
    /// </summary>
    internal static IReadOnlyList<string> GifArguments(string input, string output, VideoInfo info)
    {
        var fps = Math.Min(GifFps, Math.Max(1, (int)Math.Round(info.PeakFrameRate)));
        var width = Math.Min(GifMaxWidth, info.Width);
        return
        [
            "-hide_banner", "-nostats", "-progress", "pipe:2", "-y", "-i", input,
            "-filter_complex",
            I($"fps={fps},scale={width}:-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5"),
            "-loop", "0", output,
        ];
    }

    internal static IReadOnlyList<string> WebMArguments(string input, string output, int factor) =>
    [
        "-hide_banner", "-nostats", "-progress", "pipe:2", "-y", "-i", input,
        "-c:v", "libvpx-vp9", "-crf", I(CompressionModel.Vp9Crf(factor)), "-b:v", "0", "-row-mt", "1",
        "-deadline", "good", "-cpu-used", "4",
        "-c:a", "libopus", "-b:a", "128k", output,
    ];

    /// <summary>HEVC in MP4, tagged <c>hvc1</c> so Apple devices and browsers play it.</summary>
    internal static IReadOnlyList<string> HevcArguments(string input, string output, int factor, bool hardwareQuality)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-progress", "pipe:2", "-y", "-i", input };
        args.AddRange(hardwareQuality
            ? ["-c:v", "hevc_mf", "-hw_encoding", "1", "-rate_control", "quality",
               "-quality", I(CompressionModel.MediaFoundationQuality(factor)), "-pix_fmt", "nv12"]
            : ["-c:v", "libx265", "-preset", "medium", "-crf", I(CompressionModel.HevcCrf(factor)), "-pix_fmt", "yuv420p",
               "-x265-params", "log-level=error"]);
        args.AddRange(["-tag:v", "hvc1", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output]);
        return args;
    }

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string I(FormattableString value) => FormattableString.Invariant(value);
}
