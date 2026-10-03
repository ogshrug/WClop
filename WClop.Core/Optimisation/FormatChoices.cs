using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Video;

namespace WClop.Core.Optimisation;

/// <summary>
/// One chip on a result card's format bar: the format to convert to, its extension, and the codec when the extension
/// alone doesn't say what you get ("mp4 · HEVC", "m4a · AAC", "webm · VP9").
/// </summary>
public sealed record FormatChoice(FileFormat Target, string Extension, string? Codec)
{
    public string Label => Codec is null ? Extension : $"{Extension} · {Codec}";
}

/// <summary>
/// The format bar (Clop 3's one-click conversions): every format the file's kind can be converted to, never the one it
/// already is. For video and audio "already is" means the codec, not the container, so an H.264 MP4 is offered
/// MP4 (HEVC) but an HEVC one isn't.
/// </summary>
public static class FormatChoices
{
    public static IReadOnlyList<FormatChoice> For(MediaKind kind, FileFormat current, string? currentCodec)
    {
        var codec = currentCodec?.ToLowerInvariant();
        IEnumerable<FormatChoice> choices = kind switch
        {
            MediaKind.Image => ImageConverter.Targets
                .Where(t => t != current)
                .Select(t => new FormatChoice(t, Short(t), null)),
            MediaKind.Video => VideoConverter.Targets
                .Select(t => t switch
                {
                    FileFormat.Mp4 => new FormatChoice(t, "mp4", "HEVC"),
                    FileFormat.WebM => new FormatChoice(t, "webm", "VP9"),
                    _ => new FormatChoice(t, Short(t), null),
                })
                .Where(c => c.Target switch
                {
                    FileFormat.Mp4 => codec is not ("hevc" or "h265"),
                    FileFormat.WebM => !(current == FileFormat.WebM && codec == "vp9"),
                    var t => t != current,
                }),
            MediaKind.Audio => FileOptimisationService.AudioConversionTargets
                .Select(t => t switch
                {
                    FileFormat.M4a => new FormatChoice(t, "m4a", "AAC"),
                    FileFormat.Ogg => new FormatChoice(t, "ogg", "Opus"),
                    _ => new FormatChoice(t, Short(t), null),
                })
                .Where(c => !IsAudioAlready(c.Target, current, codec)),
            _ => [],
        };
        return choices.ToList();
    }

    /// <summary>A codec name from ffprobe as people write it: h264 → H.264, hevc → HEVC, pcm_s16le → PCM.</summary>
    public static string? CodecLabel(string? codec) => codec?.ToLowerInvariant() switch
    {
        null or "" or "unknown" => null,
        "h264" => "H.264",
        "hevc" or "h265" => "HEVC",
        "vp8" => "VP8",
        "vp9" => "VP9",
        "av1" => "AV1",
        "mpeg4" => "MPEG-4",
        "mpeg2video" => "MPEG-2",
        "prores" => "ProRes",
        "aac" => "AAC",
        "mp3" => "MP3",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "flac" => "FLAC",
        "alac" => "ALAC",
        var pcm when pcm.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
        var other => other.ToUpperInvariant(),
    };

    private static bool IsAudioAlready(FileFormat target, FileFormat current, string? codec) => target switch
    {
        FileFormat.Mp3 => codec is not null ? codec == "mp3" : current == FileFormat.Mp3,
        FileFormat.M4a => codec is not null ? codec == "aac" : current is FileFormat.M4a or FileFormat.Aac,
        FileFormat.Ogg => codec is not null ? codec == "opus" : current == FileFormat.Ogg,
        _ => target == current,
    };

    private static string Short(FileFormat format) => format.Extension().TrimStart('.');
}
