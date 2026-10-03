using WClop.Core.Media;

namespace WClop.Core.Video;

/// <summary>
/// How a filter step (crop, capFps, watermark…) re-encodes a video: at a high quality, keeping the container where it
/// suits the codec, so the normal optimisation afterwards (or none) decides the final size.
/// </summary>
public static class VideoReencode
{
    /// <summary>VP9 for WebM, H.264 for everything else.</summary>
    public static string[] Codec(string path) => FileTypeSniffer.Detect(path) == FileFormat.WebM
        ? ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1"]
        : ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"];

    /// <summary>Keeps the container when re-encoding, except ones that don't suit H.264 (AVI, MPEG), which become MP4.</summary>
    public static string OutputExtension(string path) => FileTypeSniffer.Detect(path) switch
    {
        FileFormat.WebM => ".webm",
        FileFormat.Mov => ".mov",
        FileFormat.Mkv => ".mkv",
        _ => ".mp4",
    };
}
