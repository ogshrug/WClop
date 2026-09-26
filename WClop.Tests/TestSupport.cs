using System.Windows.Media;
using System.Windows.Media.Imaging;
using WClop.Core.Processes;

namespace WClop.Tests;

internal static class TestSetup
{
    /// <summary>Keep test runs out of the real app log.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialise()
    {
        WClop.Core.Logging.Log.FilePath = Path.Combine(Path.GetTempPath(), "wclop-tests", "wclop.log");
        WClop.Core.Watching.FileSettler.PollInterval = TimeSpan.FromMilliseconds(100);
    }

    /// <summary>The checkout's root (the folder with WClop.sln).</summary>
    public static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "WClop.sln")))
                    return dir.FullName;
            }

            throw new DirectoryNotFoundException("WClop.sln not found above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds or the timeout passes.</summary>
    public static async Task<bool> WaitUntil(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }

        return condition();
    }
}

/// <summary>A test that needs the external tools; skipped (not silently passed) when they're missing.</summary>
public sealed class ToolsFactAttribute : FactAttribute
{
    public ToolsFactAttribute()
    {
        var missing = ToolLocator.CreateDefault().Missing();
        if (missing.Count > 0)
            Skip = "Tools missing (run scripts/fetch-tools.ps1): " + string.Join(", ", missing);
    }
}

/// <summary>A theory that needs the external tools; skipped when they're missing.</summary>
public sealed class ToolsTheoryAttribute : TheoryAttribute
{
    public ToolsTheoryAttribute()
    {
        var missing = ToolLocator.CreateDefault().Missing();
        if (missing.Count > 0)
            Skip = "Tools missing (run scripts/fetch-tools.ps1): " + string.Join(", ", missing);
    }
}

/// <summary>A temp folder deleted after the test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "wclop-tests-" + Guid.NewGuid().ToString("N")[..12]);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public static class TestImages
{
    /// <summary>A photo-like image: smooth gradients plus noise, which pngquant and jpegoptim can shrink.</summary>
    public static BitmapSource Photo(int width = 400, int height = 300, int seed = 1)
    {
        var random = new Random(seed);
        var stride = width * 3;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * stride + x * 3;
            pixels[i] = Noise(x * 255 / width, random);
            pixels[i + 1] = Noise(y * 255 / height, random);
            pixels[i + 2] = Noise((x + y) * 255 / (width + height), random);
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
    }

    /// <summary>A tiny two-colour image that no optimiser can meaningfully improve.</summary>
    public static BitmapSource Flat(int width = 8, int height = 8) =>
        BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, new byte[width * height * 3], width * 3);

    public static string SavePng(BitmapSource image, string path) => Save(new PngBitmapEncoder(), image, path);

    public static string SaveJpeg(BitmapSource image, string path, int quality = 100) =>
        Save(new JpegBitmapEncoder { QualityLevel = quality }, image, path);

    /// <summary>
    /// A hand-built GIF of 1×1 frames, optionally with a NETSCAPE2.0 loop block.
    /// <paramref name="truncateAfterFrames"/> cuts the file mid-way, like an interrupted download.
    /// </summary>
    public static byte[] Gif(int frames, int? loopCount, int? truncateAfterFrames = null)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange([1, 0, 1, 0, 0x80, 0, 0]); // 1×1, global colour table of 2 entries
        bytes.AddRange([0, 0, 0, 255, 255, 255]);
        if (loopCount is { } loop)
        {
            bytes.AddRange([0x21, 0xFF, 0x0B]);
            bytes.AddRange("NETSCAPE2.0"u8.ToArray());
            bytes.AddRange([0x03, 0x01, (byte)(loop & 0xFF), (byte)(loop >> 8), 0x00]);
        }

        for (var i = 0; i < frames; i++)
        {
            if (i == truncateAfterFrames)
            {
                bytes.AddRange([0x2C, 0, 0]); // a descriptor that stops half-way
                return bytes.ToArray();
            }

            bytes.AddRange([0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00]); // 0.1 s delay
            bytes.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0]);
            bytes.AddRange([0x02, 0x02, (byte)(i % 2 == 0 ? 0x44 : 0x4C), 0x01, 0x00]);
        }

        bytes.Add(0x3B);
        return bytes.ToArray();
    }

    /// <summary>An animated GIF large enough to optimise, via WPF's encoder (which writes no loop block).</summary>
    public static string SaveAnimatedGif(string path, int frames = 12, int width = 120, int height = 90)
    {
        var encoder = new GifBitmapEncoder();
        for (var i = 0; i < frames; i++)
            encoder.Frames.Add(BitmapFrame.Create(Photo(width, height, seed: i)));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static string Save(BitmapEncoder encoder, BitmapSource image, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte Noise(int value, Random random) => (byte)Math.Clamp(value + random.Next(-12, 13), 0, 255);
}
