using System.Text.RegularExpressions;
using WClop.Core.Media;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Core.Clipboard;

/// <summary>Clipboard format names WClop reads, writes or checks.</summary>
public static class ClipboardFormatNames
{
    /// <summary>Added to every WClop write so the watcher ignores it (project.md §3.2, §26.1).</summary>
    public const string OwnMarker = "WClop.Optimised";

    public const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    public const string ClipboardViewerIgnore = "Clipboard Viewer Ignore";
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";
    public const string PreferredDropEffect = "Preferred DropEffect";

    // Standard formats are reported under these names.
    public const string Dib = "CF_DIB";
    public const string DibV5 = "CF_DIBV5";
    public const string HDrop = "CF_HDROP";

    public static readonly string[] Gif = ["GIF", "image/gif"];
    public static readonly string[] Png = ["PNG", "image/png"];
    public static readonly string[] Jpeg = ["JFIF", "image/jpeg"];
}

/// <summary>What was on the clipboard when it changed. Built by the Win32 reader on the clipboard thread.</summary>
public sealed record ClipboardSnapshot
{
    public required IReadOnlyList<string> Formats { get; init; }

    /// <summary>Executable name of the clipboard owner (or the foreground app), e.g. <c>chrome.exe</c>.</summary>
    public string? OwnerProcess { get; init; }

    /// <summary>DWORD value of <c>CanIncludeInClipboardHistory</c>, if present.</summary>
    public uint? CanIncludeInHistory { get; init; }

    /// <summary>DWORD value of <c>CanUploadToCloudClipboard</c>, if present.</summary>
    public uint? CanUploadToCloud { get; init; }

    /// <summary>DWORD value of <c>Preferred DropEffect</c>, if present (2 = move, i.e. the files were cut).</summary>
    public uint? PreferredDropEffect { get; init; }

    /// <summary>Paths from <c>CF_HDROP</c>.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    public bool Has(string format) => Formats.Contains(format, StringComparer.OrdinalIgnoreCase);

    public string? FirstPresent(IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(Has);
}

public enum ClipboardImageEncoding
{
    Gif,
    Png,
    Jpeg,
    Dib,
}

public abstract record ClipboardDecision
{
    public sealed record Ignore(string Reason) : ClipboardDecision;

    /// <summary>Read image data in <paramref name="Format"/>.</summary>
    public sealed record ReadImage(string Format, ClipboardImageEncoding Encoding) : ClipboardDecision
    {
        public bool IsDib => Encoding == ClipboardImageEncoding.Dib;
    }

    /// <summary>Optimise a copy of this image file (the "optimise image paths" option).</summary>
    public sealed record ImageFile(string Path) : ClipboardDecision;

    /// <summary>On demand: read the clipboard text and see if it's a base64 image, a file path or a URL.</summary>
    public sealed record ReadText(string Format) : ClipboardDecision;
}

/// <summary>
/// Decides whether a clipboard change should be optimised (project.md §3.2–3.4, adapted to Windows).
/// Runs on the clipboard thread, so it must stay cheap: no file reads beyond the marker check.
/// </summary>
public static class ClipboardPolicy
{
    /// <summary>
    /// App-native formats: their presence means the image is a preview of something richer
    /// (Office shapes, cells, OLE objects, layers in an editor) that replacing the clipboard would destroy.
    /// Extend empirically with the tray's "Log clipboard formats" option, or <see cref="ClipboardSettings.ExtraDeniedFormats"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> DeniedFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Rich Text Format",
        "Rich Text Format Without Objects",
        "RTF As Text",
        "Object Descriptor",
        "Link Source",
        "Link Source Descriptor",
        "Embed Source",
        "Embedded Object",
        "Native",
        "OwnerLink",
        "ObjectLink",
        "Office Drawing Shape Format",
        "XML Spreadsheet",
        "image/svg+xml",
        "Portable Document Format",
    };

    public static readonly IReadOnlyList<Regex> DeniedFormatPatterns =
    [
        new(@"^Art::", RegexOptions.IgnoreCase),              // Office shapes, text, tables
        new(@"^PowerPoint", RegexOptions.IgnoreCase),
        new(@"^Biff\d", RegexOptions.IgnoreCase),             // Excel cells
        new(@"^(Adobe|com\.adobe|Photoshop|Illustrator)", RegexOptions.IgnoreCase),
        new(@"^PaintDotNet", RegexOptions.IgnoreCase),
        new(@"^application/x-krita", RegexOptions.IgnoreCase),
        new(@"^Affinity", RegexOptions.IgnoreCase),
        new(@"^(Figma|application/x-figma)", RegexOptions.IgnoreCase),
        new(@"^(raw|image/x-.*raw)", RegexOptions.IgnoreCase), // camera RAW
    ];

    private const uint DropEffectMove = 2;

    public static ClipboardDecision Decide(
        ClipboardSnapshot snapshot,
        ClipboardSettings settings,
        Func<string, MarkerStatus>? markerOf = null)
    {
        if (snapshot.Has(ClipboardFormatNames.OwnMarker))
            return new ClipboardDecision.Ignore("WClop's own write");

        if (snapshot.Has(ClipboardFormatNames.ExcludeFromMonitoring)
            || snapshot.Has(ClipboardFormatNames.ClipboardViewerIgnore)
            || snapshot.CanIncludeInHistory == 0
            || snapshot.CanUploadToCloud == 0)
            return new ClipboardDecision.Ignore("marked sensitive by the source app");

        var owner = snapshot.OwnerProcess;
        if (owner is not null)
        {
            if (settings.IgnoredApps.Any(app => MatchesApp(owner, app)))
                return new ClipboardDecision.Ignore($"{owner} is on the ignore list");

            if (settings.IgnoreRemoteClipboard && MatchesApp(owner, "rdpclip.exe"))
                return new ClipboardDecision.Ignore("redirected from a remote session");
        }

        if (snapshot.Has(ClipboardFormatNames.HDrop))
            return DecideFiles(snapshot, settings, markerOf);

        var denied = snapshot.Formats.FirstOrDefault(f =>
            DeniedFormats.Contains(f)
            || settings.ExtraDeniedFormats.Contains(f, StringComparer.OrdinalIgnoreCase)
            || DeniedFormatPatterns.Any(p => p.IsMatch(f)));
        if (denied is not null)
            return new ClipboardDecision.Ignore($"rich app content ({denied})");

        return ImageCandidates(snapshot).FirstOrDefault() as ClipboardDecision
               ?? new ClipboardDecision.Ignore("no image");
    }

    /// <summary>
    /// Every image format on the clipboard, best first. The reader tries them in order, because a source app
    /// may list a format it can't actually hand over (e.g. PNG offered only as an OLE stream).
    /// GIF comes first: an animated GIF usually rides along with a still PNG/DIB preview (§3.4).
    /// </summary>
    public static IReadOnlyList<ClipboardDecision.ReadImage> ImageCandidates(ClipboardSnapshot snapshot)
    {
        var candidates = new List<ClipboardDecision.ReadImage>();
        foreach (var name in ClipboardFormatNames.Gif.Where(snapshot.Has))
            candidates.Add(new(name, ClipboardImageEncoding.Gif));
        foreach (var name in ClipboardFormatNames.Png.Where(snapshot.Has))
            candidates.Add(new(name, ClipboardImageEncoding.Png));
        foreach (var name in ClipboardFormatNames.Jpeg.Where(snapshot.Has))
            candidates.Add(new(name, ClipboardImageEncoding.Jpeg));
        if (snapshot.Has(ClipboardFormatNames.DibV5))
            candidates.Add(new(ClipboardFormatNames.DibV5, ClipboardImageEncoding.Dib));
        if (snapshot.Has(ClipboardFormatNames.Dib))
            candidates.Add(new(ClipboardFormatNames.Dib, ClipboardImageEncoding.Dib));
        return candidates;
    }

    /// <summary>
    /// For the "optimise the clipboard now" hotkey (project.md §3.8): the user asked, so the automatic filters
    /// (deny-lists, ignored apps, the image-paths setting, markers) don't apply. Order: a copied image file,
    /// any image data, then text (base64 image, file path, URL). Cut files are still left alone.
    /// </summary>
    public static ClipboardDecision DecideOnDemand(ClipboardSnapshot snapshot)
    {
        if (snapshot.Has(ClipboardFormatNames.HDrop))
        {
            if (snapshot.PreferredDropEffect is { } effect && (effect & DropEffectMove) != 0)
                return new ClipboardDecision.Ignore("files were cut, not copied");

            var image = snapshot.Files.FirstOrDefault(f => FileFormats.FromExtension(f).Kind() == MediaKind.Image);
            if (image is not null)
                return new ClipboardDecision.ImageFile(image);

            return snapshot.Files.Count > 0
                ? new ClipboardDecision.Ignore($"{Path.GetFileName(snapshot.Files[0])} isn't an image (other media comes in later phases)")
                : new ClipboardDecision.Ignore("no files");
        }

        if (ImageCandidates(snapshot).FirstOrDefault() is { } read)
            return read;

        if (snapshot.Has("CF_UNICODETEXT"))
            return new ClipboardDecision.ReadText("CF_UNICODETEXT");

        return new ClipboardDecision.Ignore("nothing to optimise on the clipboard");
    }

    /// <summary>Copied files never fall through to the image branch.</summary>
    private static ClipboardDecision DecideFiles(
        ClipboardSnapshot snapshot, ClipboardSettings settings, Func<string, MarkerStatus>? markerOf)
    {
        if (snapshot.PreferredDropEffect is { } effect && (effect & DropEffectMove) != 0)
            return new ClipboardDecision.Ignore("files were cut, not copied");

        if (!settings.OptimiseImagePaths)
            return new ClipboardDecision.Ignore("file paths (optimising image paths is off)");

        if (snapshot.Files.Count != 1)
            return new ClipboardDecision.Ignore($"{snapshot.Files.Count} files");

        var path = snapshot.Files[0];
        if (FileFormats.FromExtension(path).Kind() != MediaKind.Image)
            return new ClipboardDecision.Ignore("not an image file");

        if (markerOf?.Invoke(path) is { } status && status != MarkerStatus.None)
            return new ClipboardDecision.Ignore("image file already optimised");

        return new ClipboardDecision.ImageFile(path);
    }

    private static bool MatchesApp(string owner, string app)
    {
        static string Normalise(string name) =>
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return string.Equals(Normalise(Path.GetFileName(owner)), Normalise(Path.GetFileName(app)), StringComparison.OrdinalIgnoreCase);
    }
}
