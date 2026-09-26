using System.Globalization;
using WClop.Core.Images;
using WClop.Core.Processes;

namespace WClop.Core.Pdf;

public sealed record PdfOptimiseOutput(string Path, long InputSize, long OutputSize, int Dpi, double? SourceMaxDpi);

/// <summary>
/// Compresses PDFs with Ghostscript's pdfwrite device (project.md §10). Always from the original (the backup), so
/// stepping the DPI down and back up never compresses twice. Encrypted PDFs are skipped.
/// </summary>
public sealed class PdfOptimiser(ToolLocator tools, AppPaths paths)
{
    public async Task<PdfOptimiseOutput> OptimiseAsync(
        string input, int? fixedDpi, int adaptiveCap, bool allowLarger, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        var analysis = await Task.Run(() => PdfAnalysis.Analyse(input), cancellationToken).ConfigureAwait(false);
        if (analysis.Encrypted)
            throw new UnsupportedFormatException("The PDF is encrypted (password protected), so it's left as it is");

        var dpi = fixedDpi ?? PdfAnalysis.ChooseDpi(analysis.ImageDpis, adaptiveCap);
        Directory.CreateDirectory(paths.Pdfs);
        var output = AppPaths.NewTempPath(paths.Pdfs, ".pdf");

        try
        {
            var result = await ProcessRunner.RunAsync(
                tools.Require(Tool.Ghostscript),
                PdfArguments.Build(input, output, dpi),
                new ProcessRunOptions
                {
                    Timeout = TimeSpan.FromMinutes(Math.Max(5, analysis.Pages / 10.0)),
                    OnStdOutLine = line => ReportProgress(line, analysis.Pages, onProgress),
                },
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                var error = (result.StdErr + result.StdOut).Contains("password", StringComparison.OrdinalIgnoreCase)
                    ? "The PDF is encrypted (password protected), so it's left as it is"
                    : $"Ghostscript couldn't process the PDF: {Tail(result.StdErr + result.StdOut)}";
                throw new UnsupportedFormatException(error);
            }

            var inputSize = new FileInfo(input).Length;
            var outputSize = new FileInfo(output).Length;
            if (!allowLarger && outputSize >= inputSize)
                throw new NotSmallerException(inputSize, outputSize);

            onProgress?.Invoke(1);
            return new PdfOptimiseOutput(output, inputSize, outputSize, dpi, analysis.MaxDpi);
        }
        catch
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    /// <summary>Ghostscript prints "Page N" as it goes.</summary>
    internal static void ReportProgress(string line, int pages, Action<double>? onProgress)
    {
        if (onProgress is null || pages <= 0 || !line.StartsWith("Page ", StringComparison.Ordinal))
            return;
        if (int.TryParse(line.AsSpan(5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
            onProgress(Math.Clamp((page - 1) / (double)pages, 0, 0.99));
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 200 ? trimmed : "…" + trimmed[^200..];
    }
}

/// <summary>The Ghostscript command line (project.md §10.1, §10.2).</summary>
public static class PdfArguments
{
    public static IReadOnlyList<string> Build(string input, string output, int dpi)
    {
        var lossless = dpi >= PdfAnalysis.LosslessDpi;
        var args = new List<string>
        {
            "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.6",
            // The /screen preset first, so the settings after it win (its colour strategy would flatten
            // transparency-group icons, §10.1).
            "-dPDFSETTINGS=/screen",
            "-sColorConversionStrategy=LeaveColorUnchanged",
            "-dConvertCMYKImagesToRGB=true",
            "-dCompressFonts=true", "-dCompressPages=true", "-dEmbedAllFonts=true", "-dSubsetFonts=true",
            "-dDetectDuplicateImages=true",
            "-dPreserveAnnots=true", "-dPreserveOverprintSettings=true", "-dPreserveSeparation=true", "-dPreserveDeviceN=true",
            "-dPreserveHalftoneInfo=false", "-dPreserveOPIComments=false", "-dPreserveEPSInfo=false",
            "-dAutoRotatePages=/None",
        };

        if (lossless)
        {
            // No downsampling: existing JPEG / JPEG 2000 images pass through untouched, forms stay interactive.
            args.AddRange(
            [
                "-dDownsampleColorImages=false", "-dDownsampleGrayImages=false", "-dDownsampleMonoImages=false",
                "-dPassThroughJPEGImages=true", "-dPassThroughJPXImages=true",
                "-dAutoFilterColorImages=false", "-dAutoFilterGrayImages=false",
                "-dColorImageFilter=/FlateEncode", "-dGrayImageFilter=/FlateEncode",
            ]);
        }
        else
        {
            var d = dpi.ToString(CultureInfo.InvariantCulture);
            // Mono (1-bit) images compress badly at low resolution, so they never go below 300 DPI (§10.2).
            var mono = Math.Max(dpi, PdfAnalysis.LosslessDpi).ToString(CultureInfo.InvariantCulture);
            args.AddRange(
            [
                "-dDownsampleColorImages=true", "-dColorImageDownsampleType=/Bicubic", $"-dColorImageResolution={d}",
                "-dColorImageDownsampleThreshold=1.0",
                "-dDownsampleGrayImages=true", "-dGrayImageDownsampleType=/Bicubic", $"-dGrayImageResolution={d}",
                "-dGrayImageDownsampleThreshold=1.0",
                "-dDownsampleMonoImages=true", "-dMonoImageDownsampleType=/Subsample", $"-dMonoImageResolution={mono}",
                "-dPassThroughJPEGImages=false",
                "-dAutoFilterColorImages=false", "-dAutoFilterGrayImages=false",
                "-dColorImageFilter=/DCTEncode", "-dGrayImageFilter=/DCTEncode", "-dMonoImageFilter=/CCITTFaxEncode",
                "-dShowAcroForm=false",
            ]);
        }

        args.Add("-o");
        args.Add(output);

        // JPEG quality: lower at the lowest stops, with chroma subsampling (§10.2); a sensible default otherwise.
        var qFactor = dpi switch
        {
            <= 48 => "1.3",
            <= 72 => "1.0",
            <= 100 => "0.76",
            _ => "0.4",
        };
        if (!lossless)
        {
            args.AddRange(
            [
                "-c",
                $"<< /ColorImageDict << /QFactor {qFactor} /Blend 1 /HSamples [2 1 1 2] /VSamples [2 1 1 2] >> " +
                $"/GrayImageDict << /QFactor {qFactor} /Blend 1 /HSamples [2 1 1 2] /VSamples [2 1 1 2] >> >> setdistillerparams",
                "-f",
            ]);
        }

        args.Add(input);
        return args;
    }
}
