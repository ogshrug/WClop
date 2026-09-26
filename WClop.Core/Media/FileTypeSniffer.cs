using System.Buffers.Binary;
using System.Text;

namespace WClop.Core.Media;

/// <summary>
/// Determines a file's real format from its first bytes (project.md §6: extensions lie).
/// Everything is done in-process from the first 512 bytes.
/// </summary>
public static class FileTypeSniffer
{
    public const int HeaderLength = 512;

    // Signatures containing bytes >= 0x80 can't be u8 string literals (those are UTF-8 encoded).
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JxlCodestream => [0xFF, 0x0A];
    private static ReadOnlySpan<byte> JxlContainer => [0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
    private static ReadOnlySpan<byte> EbmlSignature => [0x1A, 0x45, 0xDF, 0xA3];
    private static ReadOnlySpan<byte> MpegPackHeader => [0, 0, 0x01, 0xBA];
    private static ReadOnlySpan<byte> MpegSequenceHeader => [0, 0, 0x01, 0xB3];

    /// <summary>Magic bytes first, then the extension, else <see cref="FileFormat.Unknown"/>.</summary>
    public static FileFormat Detect(string path)
    {
        var header = ReadHeader(path);
        var sniffed = Sniff(header);
        return sniffed != FileFormat.Unknown ? sniffed : FileFormats.FromExtension(path);
    }

    public static byte[] ReadHeader(string path, int length = HeaderLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(length, Math.Max(0, stream.Length))];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public static FileFormat Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4)
            return FileFormat.Unknown;

        if (h.StartsWith(PngSignature))
            return FileFormat.Png;
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
            return FileFormat.Jpeg;
        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8))
            return FileFormat.Gif;
        if (h.StartsWith("II*\0"u8) || h.StartsWith("MM\0*"u8))
            return FileFormat.Tiff;
        if (h.StartsWith("%PDF"u8))
            return FileFormat.Pdf;
        if (h.StartsWith(JxlCodestream) || h.StartsWith(JxlContainer))
            return FileFormat.JpegXl;
        if (h.StartsWith("fLaC"u8))
            return FileFormat.Flac;
        if (h.StartsWith("OggS"u8))
            return FileFormat.Ogg;
        if (h.StartsWith("ID3"u8))
            return FileFormat.Mp3;

        if (h.Length >= 12 && h.StartsWith("RIFF"u8))
        {
            var riffType = h.Slice(8, 4);
            if (riffType.SequenceEqual("WEBP"u8)) return FileFormat.WebP;
            if (riffType.SequenceEqual("WAVE"u8)) return FileFormat.Wav;
            if (riffType.SequenceEqual("AVI "u8)) return FileFormat.Avi;
        }

        if (h.Length >= 12 && h.StartsWith("FORM"u8))
        {
            var formType = h.Slice(8, 4);
            if (formType.SequenceEqual("AIFF"u8) || formType.SequenceEqual("AIFC"u8))
                return FileFormat.Aiff;
        }

        if (h.Length >= 12 && h.Slice(4, 4).SequenceEqual("ftyp"u8))
            return SniffIsoBaseMedia(h);

        if (h.StartsWith(EbmlSignature))
            return h.IndexOf("webm"u8) >= 0 ? FileFormat.WebM : FileFormat.Mkv;

        if (h.StartsWith(MpegPackHeader) || h.StartsWith(MpegSequenceHeader))
            return FileFormat.Mpeg;

        if (h[0] == 'B' && h[1] == 'M' && h.Length >= 26)
            return FileFormat.Bmp;

        // MPEG audio frame sync (checked after JPEG, which also starts with 0xFF).
        if (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)
        {
            // Layer bits 00 with the MPEG-4 ID pattern is ADTS AAC; anything else is MP1/2/3.
            return (h[1] & 0x06) == 0 ? FileFormat.Aac : FileFormat.Mp3;
        }

        return FileFormat.Unknown;
    }

    /// <summary>MP4 / MOV / HEIC / AVIF all share the ISO base media "ftyp" box.</summary>
    private static FileFormat SniffIsoBaseMedia(ReadOnlySpan<byte> h)
    {
        var boxSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(h), (uint)h.Length);
        var brands = new List<string> { Encoding.ASCII.GetString(h.Slice(8, 4)) };
        for (var offset = 16; offset + 4 <= boxSize; offset += 4)
            brands.Add(Encoding.ASCII.GetString(h.Slice(offset, 4)));

        var major = brands[0];
        if (major is "avif" or "avis" || (major is "mif1" or "msf1" && brands.Contains("avif")))
            return FileFormat.Avif;
        if (major is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1")
            return FileFormat.Heic;
        if (major == "qt  ")
            return FileFormat.Mov;
        if (major is "M4A " or "M4B ")
            return FileFormat.M4a;
        return FileFormat.Mp4;
    }
}
