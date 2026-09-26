using System.Text.RegularExpressions;
using WClop.Core.Media;

namespace WClop.Core.Clipboard;

/// <summary>What a piece of copied text refers to, for on-demand optimisation (project.md §3.8).</summary>
public abstract record TextTarget
{
    public sealed record Base64Image(byte[] Data, FileFormat Format) : TextTarget;
    public sealed record WebUrl(Uri Url) : TextTarget;
    public sealed record FilePath(string Path) : TextTarget;
    public sealed record Nothing : TextTarget;
}

public static partial class ClipboardText
{
    /// <summary>
    /// Recognises, in order: a <c>data:image/...;base64,</c> string (also inside CSS <c>url(...)</c>),
    /// an existing file path (quotes allowed), and an http(s) URL. Only single-item text counts.
    /// </summary>
    public static TextTarget Classify(string? text, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 50_000_000)
            return new TextTarget.Nothing();

        var dataUri = DataUriPattern().Match(text);
        if (dataUri.Success)
        {
            try
            {
                var data = Convert.FromBase64String(Regex.Replace(dataUri.Groups["data"].Value, @"\s+", ""));
                var format = FileTypeSniffer.Sniff(data);
                if (format.Kind() == MediaKind.Image)
                    return new TextTarget.Base64Image(data, format);
            }
            catch (FormatException)
            {
            }

            return new TextTarget.Nothing();
        }

        var trimmed = text.Trim().Trim('"', '\'');
        if (trimmed.Contains('\n'))
            return new TextTarget.Nothing();

        if (Path.IsPathFullyQualified(trimmed) && fileExists(trimmed))
            return new TextTarget.FilePath(trimmed);

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return new TextTarget.WebUrl(uri);

        return new TextTarget.Nothing();
    }

    [GeneratedRegex(@"data:image/[a-z0-9.+-]+;base64,(?<data>[A-Za-z0-9+/=\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DataUriPattern();
}
