namespace WClop.Core.Storage;

/// <summary>Per-file optimisation state (project.md §5).</summary>
public enum MarkerStatus
{
    None,

    /// <summary>WClop wrote or already optimised this file.</summary>
    Optimised,

    /// <summary>A job is working on this file.</summary>
    Pending,

    /// <summary>Restored from a backup; don't re-optimise automatically.</summary>
    Original,

    /// <summary>The source of a templated copy (e.g. <c>photo.png</c> for <c>photo-optimised.png</c>).</summary>
    OriginalProcessed,
}

internal static class MarkerStatusText
{
    public static string ToText(MarkerStatus status) => status switch
    {
        MarkerStatus.Optimised => "true",
        MarkerStatus.Pending => "pending",
        MarkerStatus.Original => "original",
        MarkerStatus.OriginalProcessed => "original-processed",
        _ => "",
    };

    public static MarkerStatus Parse(string? text) => text?.Trim() switch
    {
        "true" => MarkerStatus.Optimised,
        "pending" => MarkerStatus.Pending,
        "original" => MarkerStatus.Original,
        "original-processed" => MarkerStatus.OriginalProcessed,
        _ => MarkerStatus.None,
    };
}
