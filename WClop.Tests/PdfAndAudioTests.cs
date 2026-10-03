using System.Globalization;
using System.Text;
using WClop.Core;
using WClop.Core.Audio;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Pdf;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Tests;

/// <summary>Writes small, valid PDFs for tests: pages with an embedded JPEG drawn at a chosen size, or text.</summary>
internal static class TestPdfs
{
    /// <summary>Each page draws <paramref name="jpeg"/> across <paramref name="drawnInches"/> width (keeping its aspect).</summary>
    public static byte[] WithImage(byte[] jpeg, int pixelWidth, int pixelHeight, double drawnInches, int pages = 1)
    {
        var writer = new PdfWriter();
        var drawnWidth = drawnInches * 72;
        var drawnHeight = drawnWidth * pixelHeight / pixelWidth;
        var imageObject = writer.Add(
            $"<< /Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} /ColorSpace /DeviceRGB " +
            $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>", jpeg);
        var content = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"q {drawnWidth:0.##} 0 0 {drawnHeight:0.##} 36 36 cm /Im0 Do Q"));
        var pageIds = new List<int>();
        for (var i = 0; i < pages; i++)
        {
            var contentObject = writer.Add($"<< /Length {content.Length} >>", content);
            pageIds.Add(writer.Reserve());
            writer.Set(pageIds[^1], string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent {{PAGES}} /MediaBox [0 0 {drawnWidth + 72:0.##} {drawnHeight + 72:0.##}] " +
                $"/Resources << /XObject << /Im0 {imageObject} 0 R >> >> /Contents {contentObject} 0 R >>"));
        }

        return writer.Finish(pageIds);
    }

    public static byte[] TextOnly(string text)
    {
        var writer = new PdfWriter();
        var font = writer.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var content = Encoding.ASCII.GetBytes($"BT /F1 18 Tf 72 720 Td ({text}) Tj ET");
        var contentObject = writer.Add($"<< /Length {content.Length} >>", content);
        var page = writer.Reserve();
        writer.Set(page, $"<< /Type /Page /Parent {{PAGES}} /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {contentObject} 0 R >>");
        return writer.Finish([page]);
    }

    private sealed class PdfWriter
    {
        private readonly List<(string Dictionary, byte[]? Stream)?> _objects = [];

        public int Add(string dictionary, byte[]? stream = null)
        {
            _objects.Add((dictionary, stream));
            return _objects.Count;
        }

        public int Reserve()
        {
            _objects.Add(null);
            return _objects.Count;
        }

        public void Set(int id, string dictionary) => _objects[id - 1] = (dictionary, null);

        public byte[] Finish(IReadOnlyList<int> pageIds)
        {
            var pagesId = Add($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => $"{p} 0 R"))}] /Count {pageIds.Count} >>");
            var catalogId = Add($"<< /Type /Catalog /Pages {pagesId} 0 R >>");

            using var output = new MemoryStream();
            void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
            Write("%PDF-1.4\n");
            var offsets = new List<long>();
            for (var i = 0; i < _objects.Count; i++)
            {
                offsets.Add(output.Position);
                var (dictionary, stream) = _objects[i]!.Value;
                Write($"{i + 1} 0 obj\n{dictionary.Replace("{PAGES}", $"{pagesId} 0 R")}\n");
                if (stream is not null)
                {
                    Write("stream\n");
                    output.Write(stream);
                    Write("\nendstream\n");
                }

                Write("endobj\n");
            }

            var xref = output.Position;
            Write($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
                Write($"{offset:D10} 00000 n \n");
            Write($"trailer\n<< /Size {_objects.Count + 1} /Root {catalogId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return output.ToArray();
        }
    }
}

public class PdfDpiTests
{
    [Fact]
    public void NoImagesUsesTheCap() => Assert.Equal(150, PdfAnalysis.ChooseDpi([], cap: 150));

    [Fact]
    public void HighResolutionScanDownsamplesToTheCap() =>
        Assert.Equal(150, PdfAnalysis.ChooseDpi([600, 600, 600, 600, 600], cap: 150));

    [Fact]
    public void LowResolutionDocumentIsntPointlesslyReencodedHigher() =>
        Assert.Equal(100, PdfAnalysis.ChooseDpi([110, 115, 120, 105, 118], cap: 300));

    [Fact]
    public void TinyOutlierImagesAreIgnored()
    {
        // Mostly 300 DPI images plus a couple of abnormally low ones (the Tukey fence drops those).
        Assert.Equal(250, PdfAnalysis.ChooseDpi([280, 290, 300, 300, 310, 300, 20, 25], cap: 250));
    }

    [Fact]
    public void FewImagesMustAllReachTheStop() => Assert.Equal(150, PdfAnalysis.ChooseDpi([400, 160], cap: 300));

    [Theory]
    [InlineData(300, 250)]
    [InlineData(150, 100)]
    [InlineData(48, 48)]
    public void SteppingDown(int from, int to) => Assert.Equal(to, PdfAnalysis.NextLowerDpi(from));

    [Fact]
    public void MonoImagesStayAtLeast300AndLowStopsGetHarderJpeg()
    {
        var args = string.Join(" ", PdfArguments.Build("in.pdf", "out.pdf", 72));

        Assert.Contains("-dColorImageResolution=72", args);
        Assert.Contains("-dMonoImageResolution=300", args);
        Assert.Contains("/QFactor 1.0", args);
        Assert.True(args.IndexOf("/screen", StringComparison.Ordinal) < args.IndexOf("ColorConversionStrategy", StringComparison.Ordinal));
    }

    [Fact]
    public void LosslessPassesJpegsThrough()
    {
        var args = string.Join(" ", PdfArguments.Build("in.pdf", "out.pdf", 300));

        Assert.Contains("-dPassThroughJPEGImages=true", args);
        Assert.Contains("-dDownsampleColorImages=false", args);
        Assert.DoesNotContain("setdistillerparams", args);
    }
}

public class AudioBitrateTests
{
    [Theory]
    [InlineData(5, 256)]
    [InlineData(35, 192)]
    [InlineData(100, 64)]
    public void Aac(int factor, int kbps) => Assert.Equal(kbps, AudioBitrates.Aac(factor));

    [Fact]
    public void Mp3SnapsToValidBitrates() => Assert.Contains(AudioBitrates.Mp3(35), new[] { 224, 256 });

    [Fact]
    public void NeverRaisesTheBitrate()
    {
        Assert.Equal(128, AudioBitrates.Capped(FileFormat.Mp3, 256, 128));
        Assert.Equal(96, AudioBitrates.Capped(FileFormat.M4a, 192, 100));
        Assert.Equal(192, AudioBitrates.Capped(FileFormat.M4a, 192, null));
    }

    [Theory]
    [InlineData(FileFormat.Wav, FileFormat.Mp3)]
    [InlineData(FileFormat.Flac, FileFormat.M4a)]
    [InlineData(FileFormat.Aiff, FileFormat.M4a)]
    [InlineData(FileFormat.Mp3, FileFormat.Mp3)]
    [InlineData(FileFormat.Ogg, FileFormat.Ogg)]
    public void OutputFormats(FileFormat input, FileFormat output) => Assert.Equal(output, AudioOptimiser.OutputFormat(input));
}

/// <summary>Real Ghostscript and ffmpeg runs.</summary>
public sealed class PdfAndAudioTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();

    public PdfAndAudioTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(_settings, paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose() => _dir.Dispose();

    private string ScanPdf(string name, int pages = 3)
    {
        var jpeg = File.ReadAllBytes(TestImages.SaveJpeg(TestImages.Photo(2400, 1800), _dir.File(name + ".jpg"), quality: 95));
        var path = _dir.File(name);
        File.WriteAllBytes(path, TestPdfs.WithImage(jpeg, 2400, 1800, drawnInches: 8, pages)); // 300 DPI
        return path;
    }

    [ToolsFact]
    public void AnalysisMeasuresImagesAsDrawn()
    {
        var analysis = PdfAnalysis.Analyse(ScanPdf("scan.pdf"));

        Assert.Equal(3, analysis.Pages);
        Assert.False(analysis.Encrypted);
        Assert.All(analysis.ImageDpis, dpi => Assert.InRange(dpi, 295, 305));
    }

    [ToolsFact]
    public async Task ScannedPdfShrinksAtAdaptiveDpi()
    {
        var input = ScanPdf("scan.pdf");
        var before = new FileInfo(input).Length;
        var progress = new List<double>();

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest(), progress.Add);

        Assert.Equal(input, result.OutputPath);
        Assert.Equal(150, result.Dpi);
        Assert.InRange(result.SourceDpi!.Value, 295, 305);
        Assert.True(result.NewSize < before / 2, $"{result.NewSize} vs {before}");
        Assert.Equal(3, PdfAnalysis.Analyse(input).Pages);
        Assert.All(PdfAnalysis.Analyse(input).ImageDpis, dpi => Assert.InRange(dpi, 140, 160));
        Assert.Contains(progress, p => p > 0);
    }

    [ToolsFact]
    public async Task SteppingTheDpiDownStartsFromTheOriginal()
    {
        var input = ScanPdf("scan.pdf", pages: 1);
        var first = await _service.OptimiseAsync(input, new FileOptimisationRequest());

        var lower = await _service.AdjustAsync(first, new FileOptimisationRequest { PdfDpi = 72 });

        Assert.Equal(72, lower.Dpi);
        Assert.True(lower.NewSize < first.NewSize);
        Assert.All(PdfAnalysis.Analyse(input).ImageDpis, dpi => Assert.InRange(dpi, 65, 80));
    }

    [ToolsFact]
    public async Task EncryptedPdfIsLeftAlone()
    {
        var plain = _dir.File("plain.pdf");
        File.WriteAllBytes(plain, TestPdfs.TextOnly("Secret"));
        var encrypted = _dir.File("locked.pdf");
        var made = await ProcessRunner.RunAsync(_tools.Require(Tool.Ghostscript),
            ["-dNOPAUSE", "-dBATCH", "-dSAFER", "-q", "-sDEVICE=pdfwrite", "-sOwnerPassword=owner", "-sUserPassword=user", "-o", encrypted, plain]);
        Assert.True(made.Succeeded, made.StdErr);
        var bytes = File.ReadAllBytes(encrypted);

        await Assert.ThrowsAsync<UnsupportedFormatException>(() => _service.OptimiseAsync(encrypted, new FileOptimisationRequest()));
        Assert.Equal(bytes, File.ReadAllBytes(encrypted));
    }

    [ToolsFact]
    public async Task TextOnlyPdfIsNeverMadeBigger()
    {
        var input = _dir.File("letter.pdf");
        File.WriteAllBytes(input, TestPdfs.TextOnly("Hello from WClop"));
        var before = File.ReadAllBytes(input);

        try
        {
            var result = await _service.OptimiseAsync(input, new FileOptimisationRequest());
            Assert.True(result.NewSize < before.Length);
        }
        catch (NotSmallerException)
        {
            Assert.Equal(before, File.ReadAllBytes(input)); // left untouched
        }
    }

    private string MakeAudio(string name, string codecArgs, bool cover = false)
    {
        var path = _dir.File(name);
        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:d=6" };
        if (cover)
        {
            var art = TestImages.SaveJpeg(TestImages.Photo(300, 300), _dir.File("cover.jpg"));
            args.AddRange(["-i", art, "-map", "0:a", "-map", "1:v", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        }

        args.AddRange(codecArgs.Split(' '));
        args.Add(path);
        var result = ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg), args).GetAwaiter().GetResult();
        Assert.True(result.Succeeded, result.StdErr);
        return path;
    }

    [ToolsFact]
    public async Task WavBecomesMp3InPlace()
    {
        var input = MakeAudio("voice.wav", "-c:a pcm_s16le");

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest());

        Assert.Equal(_dir.File("voice.mp3"), result.OutputPath);
        Assert.False(File.Exists(input));
        var info = await AudioInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), result.OutputPath);
        Assert.Equal("mp3", info!.Codec);
        Assert.InRange(info.Duration.TotalSeconds, 5.8, 6.2);
    }

    [ToolsFact]
    public async Task Mp3KeepsCoverArtAndLowersBitrate()
    {
        var input = MakeAudio("song.mp3", "-c:a libmp3lame -b:a 320k", cover: true);

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.InPlace });

        var info = await AudioInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), result.OutputPath);
        Assert.True(info!.HasCoverArt);
        Assert.True(result.BitrateKbps < 320);
    }

    [ToolsFact]
    public async Task LowBitrateMp3IsNotReencoded()
    {
        var input = MakeAudio("podcast.mp3", "-c:a libmp3lame -b:a 96k");
        var before = File.ReadAllBytes(input);

        await Assert.ThrowsAsync<NotSmallerException>(() => _service.OptimiseAsync(input, new FileOptimisationRequest()));
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [ToolsFact]
    public async Task FlacBecomesAac()
    {
        var input = MakeAudio("track.flac", "-c:a flac");

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest());

        Assert.Equal(FileFormat.M4a, result.OutputFormat);
        Assert.EndsWith(".m4a", result.OutputPath);
    }
}
