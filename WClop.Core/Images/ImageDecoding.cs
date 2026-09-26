using System.Windows.Media.Imaging;

namespace WClop.Core.Images;

public sealed record ImageSize(int Width, int Height)
{
    public override string ToString() => $"{Width}×{Height}";
}

/// <summary>Decoding through Windows Imaging Component, used to check outputs are valid images.</summary>
public static class ImageDecoding
{
    /// <summary>Pixel size from the image header only (no full decode), or null if it can't be read.</summary>
    public static ImageSize? TryReadSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return new ImageSize(frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException
                                      or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True if the image fully decodes.</summary>
    public static bool IsValid(string path)
    {
        try
        {
            Verify(path);
            return true;
        }
        catch (OutputUnreadableException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fully decodes the first frame and returns its size. Throws <see cref="OutputUnreadableException"/> if the file
    /// can't be decoded.
    /// </summary>
    public static ImageSize Verify(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            // Pull pixels through the decoder so a corrupt image stream fails here, not in the user's app.
            var stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
            frame.CopyPixels(new System.Windows.Int32Rect(0, 0, frame.PixelWidth, 1), new byte[stride], stride, 0);

            return new ImageSize(frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new OutputUnreadableException($"Output image can't be decoded: {e.Message}", e);
        }
    }
}
