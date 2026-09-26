using System.Buffers.Binary;

namespace WClop.Core.Media;

/// <summary>Animated-WebP detection and loop count from the RIFF header (project.md §6).</summary>
public static class WebPInfo
{
    private const int MaxChunksToWalk = 8;

    /// <summary>
    /// True for an extended (<c>VP8X</c>) WebP with the animation flag set.
    /// Simple <c>VP8 </c> / <c>VP8L</c> files are always single-frame.
    /// </summary>
    public static bool IsAnimated(ReadOnlySpan<byte> header) =>
        header.Length >= 21
        && header.StartsWith("RIFF"u8)
        && header.Slice(8, 4).SequenceEqual("WEBP"u8)
        && header.Slice(12, 4).SequenceEqual("VP8X"u8)
        && (header[20] & 0x02) != 0;

    public static bool IsAnimated(string path) => IsAnimated(FileTypeSniffer.ReadHeader(path, 21));

    /// <summary>Canvas size from an extended (<c>VP8X</c>) header: 24-bit width−1 and height−1 at offsets 24 and 27.</summary>
    public static (int Width, int Height)? CanvasSize(ReadOnlySpan<byte> header)
    {
        if (header.Length < 30 || !header.Slice(12, 4).SequenceEqual("VP8X"u8))
            return null;
        var width = 1 + (header[24] | header[25] << 8 | header[26] << 16);
        var height = 1 + (header[27] | header[28] << 8 | header[29] << 16);
        return (width, height);
    }

    /// <summary>
    /// Pixel size of any WebP from its first chunk, without a decoder: lossy (<c>VP8 </c>), lossless (<c>VP8L</c>) or
    /// extended (<c>VP8X</c>). Windows only decodes WebP with the Web Media Extensions installed, which Windows Server
    /// and some PCs don't have.
    /// </summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> header)
    {
        if (header.Length < 30 || !header.StartsWith("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WEBP"u8))
            return null;

        var chunk = header.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
            return CanvasSize(header);

        if (chunk.SequenceEqual("VP8 "u8))
        {
            // 3-byte frame tag, then the start code 9D 01 2A, then 14-bit width and height.
            if (header[23] != 0x9D || header[24] != 0x01 || header[25] != 0x2A)
                return null;
            return (BinaryPrimitives.ReadUInt16LittleEndian(header[26..]) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(header[28..]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            // Signature 0x2F, then width−1 and height−1 as 14-bit fields.
            if (header[20] != 0x2F)
                return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(header[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        return null;
    }

    public static (int Width, int Height)? Size(string path) => Size(FileTypeSniffer.ReadHeader(path, 30));

    /// <summary>
    /// Loop count from the <c>ANIM</c> chunk (0 = forever), walking chunks because an <c>ICCP</c>
    /// chunk may come first. Null if not found in the first few chunks.
    /// </summary>
    public static int? LoopCount(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data.StartsWith("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return null;

        var offset = 12;
        for (var i = 0; i < MaxChunksToWalk && offset + 8 <= data.Length; i++)
        {
            var fourCc = data.Slice(offset, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));

            if (fourCc.SequenceEqual("ANIM"u8))
                return offset + 14 <= data.Length
                    ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 12, 2))
                    : null;

            // Chunks are padded to an even size.
            offset += 8 + size + (size & 1);
        }

        return null;
    }

    public static int? LoopCount(string path) => LoopCount(FileTypeSniffer.ReadHeader(path, 4096));
}
