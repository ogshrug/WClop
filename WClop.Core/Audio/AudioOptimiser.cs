using System.Globalization;
using System.Text.Json;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Processes;
using WClop.Core.Settings;

namespace WClop.Core.Audio;

/// <summary>What ffprobe reports about an audio file.</summary>
public sealed record AudioInfo(TimeSpan Duration, string Codec, int? BitrateKbps, int SampleRate, bool HasCoverArt)
{
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "flac", "alac", "wavpack", "ape", "tta" };

    public bool IsLossless => LosslessCodecs.Contains(Codec) || Codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase);

    public static async Task<AudioInfo?> ProbeAsync(string ffprobe, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(ffprobe,
                ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? Parse(result.StdOut) : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public static AudioInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams))
            return null;

        JsonElement? audio = null;
        var cover = false;
        foreach (var stream in streams.EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            if (type == "audio" && audio is null)
                audio = stream;
            else if (type == "video")
                cover = true; // in an audio file, a "video" stream is the cover picture
        }

        if (audio is not { } a)
            return null;

        root.TryGetProperty("format", out var format);
        var duration = Number(format, "duration") ?? Number(a, "duration") ?? 0;
        var bitrate = Number(a, "bit_rate") ?? Number(format, "bit_rate");
        return new AudioInfo(
            TimeSpan.FromSeconds(duration),
            a.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "unknown" : "unknown",
            bitrate is { } bits ? (int)Math.Round(bits / 1000) : null,
            (int)(Number(a, "sample_rate") ?? 44100),
            cover);
    }

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && double.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}

/// <summary>Bitrates for the audio compression factor (project.md §11).</summary>
public static class AudioBitrates
{
    private static readonly int[] Mp3Bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    /// <summary>AAC: 256 kbps at f5, 192 at the default f35, down to 64 at f100; multiples of 16.</summary>
    public static int Aac(int factor) => RoundTo16(Math.Max(48, 256 - (Math.Clamp(factor, 5, 100) - 5) * 192.0 / 95));

    /// <summary>MP3 needs a bit more than AAC for the same quality; snapped to a valid MP3 bitrate.</summary>
    public static int Mp3(int factor) => SnapMp3(Math.Min(320, Aac(factor) * 1.25));

    /// <summary>Opus is efficient: about 60 % of the AAC bitrate.</summary>
    public static int Opus(int factor) => RoundTo16(Math.Max(32, Aac(factor) * 0.6));

    public static int For(FileFormat output, int factor) => output switch
    {
        FileFormat.Mp3 => Mp3(factor),
        FileFormat.Ogg => Opus(factor),
        _ => Aac(factor),
    };

    /// <summary>Never raise the bitrate: cap at the source's, snapped to what the output format allows (§11).</summary>
    public static int Capped(FileFormat output, int target, int? sourceKbps)
    {
        if (sourceKbps is not { } source || source <= 0)
            return target;
        var capped = Math.Min(target, source);
        return output == FileFormat.Mp3 ? SnapMp3(capped) : Math.Max(16, (int)Math.Floor(capped / 16.0) * 16);
    }

    public static int SnapMp3(double kbps) => Mp3Bitrates.LastOrDefault(b => b <= kbps, Mp3Bitrates[0]);

    private static int RoundTo16(double kbps) => (int)Math.Round(kbps / 16) * 16;
}

public sealed record AudioOptimiseOutput(string Path, FileFormat Format, long InputSize, long OutputSize, int BitrateKbps);

/// <summary>Re-encodes audio with ffmpeg into the working directory (project.md §11). The input is never modified.</summary>
public sealed class AudioOptimiser(ToolLocator tools, AppPaths paths)
{
    /// <summary>
    /// The output format: WAV → MP3, AIFF / FLAC → AAC (§11 defaults), MP3 stays MP3, Ogg becomes Opus,
    /// everything else AAC.
    /// </summary>
    public static FileFormat OutputFormat(FileFormat input) => input switch
    {
        FileFormat.Wav or FileFormat.Mp3 => FileFormat.Mp3,
        FileFormat.Ogg => FileFormat.Ogg,
        _ => FileFormat.M4a,
    };

    public async Task<AudioOptimiseOutput> OptimiseAsync(
        string input, int factor, bool allowLarger, Action<double>? onProgress, CancellationToken cancellationToken, int? kbpsOverride = null,
        double speed = 1, CoverArtMode coverArt = CoverArtMode.Keep)
    {
        var info = await AudioInfo.ProbeAsync(tools.Require(Tool.Ffprobe), input, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnsupportedFormatException("Not an audio file ffmpeg can read");

        var inputFormat = FileTypeSniffer.Detect(input);
        var output = OutputFormat(inputFormat);
        var target = kbpsOverride ?? AudioBitrates.For(output, factor);
        var sameFamily = output == inputFormat || (output == FileFormat.M4a && inputFormat is FileFormat.M4a or FileFormat.Aac);
        var bitrate = info.IsLossless ? target : AudioBitrates.Capped(output, target, info.BitrateKbps);
        var changesSpeed = Math.Abs(speed - 1) > 0.001;

        var inputSize = new FileInfo(input).Length;
        // Re-encoding at the same bitrate only loses quality: unless the speed changes, keep the sound as it is, and
        // only bother if there's cover art to shrink or drop.
        var copyAudio = sameFamily && !info.IsLossless && info.BitrateKbps is { } source && bitrate >= source - 8 && !changesSpeed;
        if (copyAudio && (!info.HasCoverArt || coverArt == CoverArtMode.Keep) && !allowLarger)
            throw new NotSmallerException(inputSize, inputSize);

        Directory.CreateDirectory(paths.Audios);
        var path = AppPaths.NewTempPath(paths.Audios, output.Extension());
        var seconds = info.Duration.TotalSeconds / speed;
        try
        {
            var arguments = Arguments(input, path, output, bitrate, info, new AudioEncodeOptions(speed, coverArt, copyAudio));
            var result = await ProcessRunner.RunAsync(tools.Require(Tool.Ffmpeg), arguments,
                new ProcessRunOptions
                {
                    Timeout = TimeSpan.FromMinutes(Math.Max(5, seconds / 60)),
                    OnStdErrLine = line => Video.VideoOptimiser.ReportProgress(line, seconds, onProgress),
                }, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
                throw new ToolFailedException(result);

            var outputSize = new FileInfo(path).Length;
            if (!allowLarger && outputSize >= inputSize)
                throw new NotSmallerException(inputSize, outputSize);

            onProgress?.Invoke(1);
            return new AudioOptimiseOutput(path, output, inputSize, outputSize, copyAudio ? info.BitrateKbps!.Value : bitrate);
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    /// <summary>
    /// The JPEG quantiser for optimised cover art (ffmpeg's -q:v, 2 best – 31 worst): coarse, since players show the
    /// art small, but not blocky.
    /// </summary>
    internal const int CoverArtJpegQuantiser = 9;

    internal static IReadOnlyList<string> Arguments(
        string input, string output, FileFormat format, int kbps, AudioInfo info, AudioEncodeOptions? options = null)
    {
        options ??= new AudioEncodeOptions();
        var args = new List<string> { "-hide_banner", "-nostats", "-progress", "pipe:2", "-y", "-i", input, "-map", "0:a:0" };

        // MP3 and M4A carry cover art; Ogg Opus can't through ffmpeg. Optimised art is re-encoded as JPEG at the same
        // resolution (full-range 4:2:0, which every player reads).
        if (info.HasCoverArt && format != FileFormat.Ogg && options.CoverArt != CoverArtMode.Remove)
        {
            args.AddRange(["-map", "0:v:0"]);
            args.AddRange(options.CoverArt == CoverArtMode.Optimise
                ? ["-c:v", "mjpeg", "-q:v", CoverArtJpegQuantiser.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuvj420p"]
                : ["-c:v", "copy"]);
            args.AddRange(["-disposition:v:0", "attached_pic"]);
        }

        // atempo keeps the pitch; it only takes 0.5–2 per filter, so bigger changes are chained.
        if (Math.Abs(options.Speed - 1) > 0.001)
            args.AddRange(["-af", Video.VideoArguments.AtempoChain(options.Speed)]);

        var bitrate = kbps.ToString(CultureInfo.InvariantCulture) + "k";
        string[] copy = ["-c:a", "copy"];
        switch (format)
        {
            case FileFormat.Mp3:
                args.AddRange(options.CopyAudio ? copy : ["-c:a", "libmp3lame", "-b:a", bitrate]);
                args.AddRange(["-id3v2_version", "3"]);
                if (info.SampleRate > 48000 && !options.CopyAudio)
                    args.AddRange(["-ar", "48000"]); // MP3 tops out at 48 kHz
                break;
            case FileFormat.Ogg:
                args.AddRange(options.CopyAudio ? copy : ["-c:a", "libopus", "-b:a", bitrate, "-ar", "48000"]); // Opus always runs at 48 kHz
                break;
            default:
                args.AddRange(options.CopyAudio ? copy : ["-c:a", "aac", "-b:a", bitrate]);
                args.AddRange(["-movflags", "+faststart"]);
                if (info.SampleRate > 96000 && !options.CopyAudio)
                    args.AddRange(["-ar", "96000"]);
                break;
        }

        args.AddRange(["-map_metadata", "0", output]);
        return args;
    }
}

/// <summary>
/// How audio is re-encoded beyond its bitrate: playback speed (1 = unchanged), what happens to cover art, and whether
/// the sound itself is copied unchanged (when only the cover art has anything to gain).
/// </summary>
internal sealed record AudioEncodeOptions(double Speed = 1, CoverArtMode CoverArt = CoverArtMode.Keep, bool CopyAudio = false);
