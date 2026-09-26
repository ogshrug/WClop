namespace WClop.Core.Media;

public enum MediaKind
{
    Unknown,
    Image,
    Video,
    Pdf,
    Audio,
}

public enum FileFormat
{
    Unknown,

    // Images
    Png,
    Jpeg,
    Gif,
    Tiff,
    WebP,
    Avif,
    Heic,
    Bmp,
    JpegXl,

    // Video
    Mp4,
    Mov,
    WebM,
    Mkv,
    Avi,
    Mpeg,

    Pdf,

    // Audio
    Mp3,
    M4a,
    Aac,
    Wav,
    Flac,
    Ogg,
    Aiff,
}

public static class FileFormats
{
    private static readonly Dictionary<string, FileFormat> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = FileFormat.Png,
        ["jpg"] = FileFormat.Jpeg,
        ["jpeg"] = FileFormat.Jpeg,
        ["jpe"] = FileFormat.Jpeg,
        ["jfif"] = FileFormat.Jpeg,
        ["gif"] = FileFormat.Gif,
        ["tif"] = FileFormat.Tiff,
        ["tiff"] = FileFormat.Tiff,
        ["webp"] = FileFormat.WebP,
        ["avif"] = FileFormat.Avif,
        ["heic"] = FileFormat.Heic,
        ["heif"] = FileFormat.Heic,
        ["bmp"] = FileFormat.Bmp,
        ["jxl"] = FileFormat.JpegXl,
        ["mp4"] = FileFormat.Mp4,
        ["m4v"] = FileFormat.Mp4,
        ["mov"] = FileFormat.Mov,
        ["webm"] = FileFormat.WebM,
        ["mkv"] = FileFormat.Mkv,
        ["avi"] = FileFormat.Avi,
        ["mpg"] = FileFormat.Mpeg,
        ["mpeg"] = FileFormat.Mpeg,
        ["m2v"] = FileFormat.Mpeg,
        ["pdf"] = FileFormat.Pdf,
        ["mp3"] = FileFormat.Mp3,
        ["m4a"] = FileFormat.M4a,
        ["aac"] = FileFormat.Aac,
        ["wav"] = FileFormat.Wav,
        ["flac"] = FileFormat.Flac,
        ["ogg"] = FileFormat.Ogg,
        ["opus"] = FileFormat.Ogg,
        ["aif"] = FileFormat.Aiff,
        ["aiff"] = FileFormat.Aiff,
    };

    public static MediaKind Kind(this FileFormat format) => format switch
    {
        FileFormat.Png or FileFormat.Jpeg or FileFormat.Gif or FileFormat.Tiff or FileFormat.WebP
            or FileFormat.Avif or FileFormat.Heic or FileFormat.Bmp or FileFormat.JpegXl => MediaKind.Image,
        FileFormat.Mp4 or FileFormat.Mov or FileFormat.WebM or FileFormat.Mkv or FileFormat.Avi
            or FileFormat.Mpeg => MediaKind.Video,
        FileFormat.Pdf => MediaKind.Pdf,
        FileFormat.Mp3 or FileFormat.M4a or FileFormat.Aac or FileFormat.Wav or FileFormat.Flac
            or FileFormat.Ogg or FileFormat.Aiff => MediaKind.Audio,
        _ => MediaKind.Unknown,
    };

    /// <summary>The canonical extension (with the dot) used when WClop writes a file of this format.</summary>
    public static string Extension(this FileFormat format) => format switch
    {
        FileFormat.Png => ".png",
        FileFormat.Jpeg => ".jpg",
        FileFormat.Gif => ".gif",
        FileFormat.Tiff => ".tiff",
        FileFormat.WebP => ".webp",
        FileFormat.Avif => ".avif",
        FileFormat.Heic => ".heic",
        FileFormat.Bmp => ".bmp",
        FileFormat.JpegXl => ".jxl",
        FileFormat.Mp4 => ".mp4",
        FileFormat.Mov => ".mov",
        FileFormat.WebM => ".webm",
        FileFormat.Mkv => ".mkv",
        FileFormat.Avi => ".avi",
        FileFormat.Mpeg => ".mpg",
        FileFormat.Pdf => ".pdf",
        FileFormat.Mp3 => ".mp3",
        FileFormat.M4a => ".m4a",
        FileFormat.Aac => ".aac",
        FileFormat.Wav => ".wav",
        FileFormat.Flac => ".flac",
        FileFormat.Ogg => ".ogg",
        FileFormat.Aiff => ".aiff",
        _ => "",
    };

    /// <summary>Every extension WClop recognises (without the dot), e.g. for registering the Explorer menu.</summary>
    public static IEnumerable<(string Extension, FileFormat Format)> KnownExtensions =>
        ByExtension.Select(pair => (pair.Key, pair.Value));

    /// <summary>
    /// Format implied by the file extension. Handles <c>@</c>-suffixed names like <c>image@2x.png</c>
    /// because only the part after the last dot is used.
    /// </summary>
    public static FileFormat FromExtension(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return ByExtension.GetValueOrDefault(ext, FileFormat.Unknown);
    }

    /// <summary>Whether <paramref name="path"/>'s extension already names <paramref name="format"/> (e.g. <c>.jpeg</c> for JPEG).</summary>
    public static bool HasExtensionFor(string path, FileFormat format) => FromExtension(path) == format;
}
