using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WClop.Core.Clipboard;

/// <summary>
/// Converts between clipboard device-independent bitmaps (CF_DIB / CF_DIBV5) and image files, via WIC.
/// </summary>
public static class DibConverter
{
    private const int FileHeaderSize = 14;
    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;
    private const uint BiAlphaBitfields = 6;

    /// <summary>
    /// Encodes a clipboard DIB as PNG. Alpha follows project.md §26.1: CF_DIBV5 with an alpha mask keeps it;
    /// plain 32-bit CF_DIB alpha is unreliable, so WIC decodes it as opaque.
    /// </summary>
    public static byte[] DibToPng(ReadOnlySpan<byte> dib)
    {
        var bmp = WrapAsBmpFile(dib);
        var decoder = BitmapDecoder.Create(
            new MemoryStream(bmp), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>
    /// A bottom-up 32-bit BI_RGB DIB of the image's first frame, for apps that paste pixels (Paint, Office, older apps).
    /// Windows synthesises CF_DIBV5 and CF_BITMAP from it.
    /// </summary>
    public static byte[] ImageFileToDib(string path)
    {
        BitmapSource frame;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }

        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        const int headerSize = 40;
        var dib = new byte[headerSize + pixels.Length];
        var header = dib.AsSpan(0, headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height); // positive = bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);    // planes
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);   // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], BiRgb);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)pixels.Length);

        for (var y = 0; y < height; y++)
        {
            pixels.AsSpan((height - 1 - y) * stride, stride).CopyTo(dib.AsSpan(headerSize + y * stride));
        }

        return dib;
    }

    /// <summary>Prepends a BITMAPFILEHEADER so WIC's BMP decoder can read the clipboard DIB.</summary>
    internal static byte[] WrapAsBmpFile(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
            throw new InvalidDataException("DIB too short");

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);

        // A BITMAPINFOHEADER (40 bytes) with bitfields is followed by the colour masks; V4/V5 headers contain them.
        var masks = headerSize == 40 ? compression switch
        {
            BiBitfields => 12u,
            BiAlphaBitfields => 16u,
            _ => 0u,
        } : 0u;
        var paletteEntries = bitCount <= 8 ? (colorsUsed == 0 ? 1u << bitCount : colorsUsed) : colorsUsed;
        var pixelOffset = FileHeaderSize + headerSize + masks + paletteEntries * 4;

        var file = new byte[FileHeaderSize + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), pixelOffset);
        dib.CopyTo(file.AsSpan(FileHeaderSize));
        return file;
    }
}
