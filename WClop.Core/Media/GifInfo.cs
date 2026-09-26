namespace WClop.Core.Media;

/// <summary>
/// GIF structure read by walking the block index, without decoding pixels (project.md §6).
/// </summary>
/// <param name="FrameCount">Image descriptors found. A truncated file reports the frames before the cut.</param>
/// <param name="LoopCount">From the NETSCAPE2.0 extension: 0 = forever, null = no extension (play once).</param>
/// <param name="Truncated">The file ended before the trailer byte.</param>
public sealed record GifInfo(int Width, int Height, int FrameCount, int? LoopCount, bool Truncated)
{
    public bool IsAnimated => FrameCount > 1;

    public static GifInfo Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    public static GifInfo Read(Stream stream)
    {
        var reader = new BinaryReader(new BufferedStream(stream));
        var width = 0;
        var height = 0;
        var frames = 0;
        int? loopCount = null;

        try
        {
            var signature = reader.ReadBytes(6);
            if (signature.Length < 6 || signature[0] != 'G' || signature[1] != 'I' || signature[2] != 'F')
                throw new InvalidDataException("Not a GIF file");

            width = reader.ReadUInt16();
            height = reader.ReadUInt16();
            var packed = reader.ReadByte();
            reader.ReadBytes(2); // background colour index, pixel aspect ratio
            if ((packed & 0x80) != 0)
                Skip(reader, 3 * (1 << ((packed & 0x07) + 1)));

            while (true)
            {
                switch (reader.ReadByte())
                {
                    case 0x21: // extension
                        var label = reader.ReadByte();
                        if (label == 0xFF)
                            loopCount = ReadApplicationExtension(reader) ?? loopCount;
                        else
                            SkipSubBlocks(reader);
                        break;

                    case 0x2C: // image descriptor
                        Skip(reader, 8); // left, top, width, height
                        var imagePacked = reader.ReadByte();
                        if ((imagePacked & 0x80) != 0)
                            Skip(reader, 3 * (1 << ((imagePacked & 0x07) + 1)));
                        reader.ReadByte(); // LZW minimum code size
                        SkipSubBlocks(reader);
                        frames++;
                        break;

                    case 0x3B: // trailer
                        return new GifInfo(width, height, frames, loopCount, Truncated: false);

                    default:
                        // Garbage where a block should be: treat as the end of usable data.
                        return new GifInfo(width, height, frames, loopCount, Truncated: true);
                }
            }
        }
        catch (EndOfStreamException)
        {
            return new GifInfo(width, height, frames, loopCount, Truncated: true);
        }
    }

    /// <summary>Returns the loop count for a NETSCAPE2.0 / ANIMEXTS1.0 block, else skips it and returns null.</summary>
    private static int? ReadApplicationExtension(BinaryReader reader)
    {
        var blockSize = reader.ReadByte();
        var identifier = reader.ReadBytes(blockSize);
        var isLoopBlock = blockSize == 11
                          && (identifier.AsSpan().SequenceEqual("NETSCAPE2.0"u8)
                              || identifier.AsSpan().SequenceEqual("ANIMEXTS1.0"u8));

        int? loopCount = null;
        while (true)
        {
            var size = reader.ReadByte();
            if (size == 0)
                return loopCount;

            var data = reader.ReadBytes(size);
            if (data.Length < size)
                throw new EndOfStreamException();
            if (isLoopBlock && size >= 3 && data[0] == 1)
                loopCount = data[1] | (data[2] << 8);
        }
    }

    private static void SkipSubBlocks(BinaryReader reader)
    {
        while (true)
        {
            var size = reader.ReadByte();
            if (size == 0)
                return;
            Skip(reader, size);
        }
    }

    private static void Skip(BinaryReader reader, int count)
    {
        if (reader.ReadBytes(count).Length < count)
            throw new EndOfStreamException();
    }
}
