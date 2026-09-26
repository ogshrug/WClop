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
