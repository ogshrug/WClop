using WClop.Core.Compression;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Settings;

namespace WClop.Core.DropZone;

/// <summary>
/// A compression choice for a drop, picked by scrolling while holding files over the drop zone.
/// Null values mean "use the settings". <see cref="PdfDpi"/> is how the same intent applies to PDFs.
/// </summary>
public sealed record DropPreset(
    string Name, string Description, int? Factor = null, double? Scale = null, int? PdfDpi = null, long? TargetBytes = null,
    string? Pipeline = null)
{
    public FileOptimisationRequest Apply(FileOptimisationRequest request) =>
        request with
        {
            Factor = Factor ?? request.Factor,
            Scale = Scale ?? request.Scale,
            PdfDpi = PdfDpi ?? request.PdfDpi,
            TargetBytes = TargetBytes ?? request.TargetBytes,
        };
}

public static class DropPresets
{
    /// <summary>Scroll order. The first is what a drop does when you don't scroll.</summary>
    public static readonly IReadOnlyList<DropPreset> All =
    [
        new("Normal", "Your compression settings"),
        new("Aggressive", "Smaller files, some quality loss", Factor: CompressionModel.AggressiveImageFactor, PdfDpi: 100),
        new("Maximum", "Smallest files, visible quality loss", Factor: 90, PdfDpi: 72),
        new("Half size", "Downscale images and videos to 50%", Scale: 0.5),
        new("Under 10 MB", "Fit under 10 MB (Discord's limit)", TargetBytes: 10L * 1024 * 1024),
        new("Under 25 MB", "Fit under 25 MB (email attachments)", TargetBytes: 25L * 1024 * 1024),
        new("Gentle", "Barely any quality loss", Factor: 10, PdfDpi: 300),
    ];

    /// <summary>The built-in presets, then saved pipelines that are offered in the drop zone (project.md §18.3).</summary>
    /// <param name="dragged">Formats being dragged, to offer only pipelines for them; null = don't filter.</param>
    public static IReadOnlyList<DropPreset> WithPipelines(PipelineSettings pipelines, IReadOnlyCollection<FileFormat>? dragged = null) =>
    [
        .. All,
        .. WatermarkPreset(pipelines, dragged),
        .. pipelines.Saved
            .Where(p => p.ShowInDropZone && p.Name.Length > 0 && PipelineCatalog.TryCompile(p.Script, out _, out _))
            .Where(p => dragged is null || dragged.Count == 0 || dragged.Any(p.AppliesTo))
            .Select(p => new DropPreset(p.Name, "Pipeline: " + Summary(p.Script), Pipeline: p.Name)),
    ];

    /// <summary>
    /// "Watermark": just the default watermark, no optimisation. Offered once a default watermark image is set, for
    /// images and videos, unless a saved pipeline is already called Watermark.
    /// </summary>
    private static IEnumerable<DropPreset> WatermarkPreset(PipelineSettings pipelines, IReadOnlyCollection<FileFormat>? dragged)
    {
        if (pipelines.DefaultWatermark is not { Length: > 0 } || pipelines.Find("Watermark") is not null)
            yield break;
        if (dragged is { Count: > 0 } && !dragged.Any(f => f.Kind() is MediaKind.Image or MediaKind.Video))
            yield break;
        yield return new DropPreset("Watermark", "Add your watermark, nothing else", Pipeline: "watermark");
    }

    private static string Summary(string script)
    {
        var steps = PipelineSyntax.Parse(script).Select(s => s.Name);
        var text = string.Join(" → ", steps);
        return text.Length <= 60 ? text : text[..57] + "…";
    }

    /// <summary>The preset <paramref name="steps"/> away (positive = scrolling down), wrapping around.</summary>
    public static int Step(int index, int steps, int? count = null)
    {
        var n = count ?? All.Count;
        return ((index + steps) % n + n) % n;
    }
}
