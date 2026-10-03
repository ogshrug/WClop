using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WClop.Core.Images;
using WClop.Core.Ipc;
using WClop.Core.Media;
using WClop.Core.Video;

namespace WClop.Core.Pipelines;

public enum ArgType
{
    Text,
    Integer,
    Number,

    /// <summary>0–1, also written as a percentage: <c>0.5</c> or <c>50%</c>.</summary>
    Fraction,

    /// <summary>Bytes: <c>10MB</c>, <c>500KB</c>.</summary>
    Size,

    Boolean,
    Choice,
    Regex,

    /// <summary>Width:height, e.g. <c>"16:9"</c>.</summary>
    Ratio,
}

public sealed record ArgSpec(string Name, ArgType Type, string Description)
{
    public IReadOnlyList<string> Choices { get; init; } = [];
    public double? Min { get; init; }
    public double? Max { get; init; }
}

public enum StepCategory
{
    Processing,
    FileOperation,
    Filter,
    Action,
}

public sealed record StepSpec(string Name, StepCategory Category, string Description, IReadOnlyList<ArgSpec> Args, string Example)
{
    /// <summary>Media the step applies to; others pass through unchanged. Empty = everything.</summary>
    public IReadOnlySet<MediaKind> Kinds { get; init; } = new HashSet<MediaKind>();

    /// <summary>The argument an unnamed value means: <c>convert(webp)</c> = <c>convert(to: webp)</c>.</summary>
    public string? DefaultArg { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>At least one of these must be given.</summary>
    public IReadOnlyList<string> RequiresOneOf { get; init; } = [];

    public bool AppliesTo(MediaKind kind) => Kinds.Count == 0 || Kinds.Contains(kind);
}

/// <summary>A step with its arguments checked and converted (int, double, long, bool, string, Regex).</summary>
public sealed record CompiledStep(StepSpec Spec, IReadOnlyDictionary<string, object> Values, PipelineStep Source)
{
    public string Name => Spec.Name;
    public bool Has(string name) => Values.ContainsKey(name);
    public T? Get<T>(string name) => Values.TryGetValue(name, out var value) ? (T)value : default;
    public override string ToString() => Source.ToString();
}

public sealed record CompiledPipeline(IReadOnlyList<CompiledStep> Steps, string Text)
{
    /// <summary>Whether a step re-encodes (so optimising first would encode twice, project.md §18.3).</summary>
    public bool Encodes => Steps.Any(s => s.Spec.Category == StepCategory.Processing && s.Name != "stripExif");

    /// <summary>
    /// Whether it starts programs of its own choosing: <c>runScript</c>, <c>openWith</c> a named app, or either inside
    /// an inline <c>fork</c> (a fork of a saved pipeline is checked when that runs).
    /// </summary>
    public bool RunsScripts => Steps.Any(s => s.Name == "runScript"
                                              || s.Name == "openWith" && s.Has("app")
                                              || s.Name == "fork" && PipelineCatalog.TryCompile(s.Get<string>("steps")!, out var forked, out _)
                                                                  && forked!.RunsScripts);
}

/// <summary>Every step of the pipeline language (project.md §19.2), with argument types for validation.</summary>
public static class PipelineCatalog
{
    private static readonly HashSet<MediaKind> Images = [MediaKind.Image];
    private static readonly HashSet<MediaKind> Visual = [MediaKind.Image, MediaKind.Video];
    private static readonly HashSet<MediaKind> Videos = [MediaKind.Video];
    private static readonly HashSet<MediaKind> Sound = [MediaKind.Video, MediaKind.Audio];

    public static readonly IReadOnlyList<string> ConvertTargets =
        ImageConverter.Targets.Concat(VideoConverter.Targets).Distinct()
            .Select(f => f.Extension().TrimStart('.')).ToList();

    public static readonly IReadOnlyList<string> WatermarkPositions = ["bottomRight", "bottomLeft", "topRight", "topLeft", "center"];

    private static readonly ArgSpec To = new("to", ArgType.Text,
        "Destination: a folder (ending in / or \\) or a path; ~ is your user folder, %f %e %y %m %d %H %M %S are name/date tokens, $1 a regex capture");

    public static readonly IReadOnlyList<StepSpec> Steps =
    [
        // Processing
        new("optimise", StepCategory.Processing, "Optimise with WClop's normal engine (images, videos, PDFs, audio)",
            [
                new("factor", ArgType.Integer, "Compression factor 1–100 (higher = smaller)") { Min = 1, Max = 100 },
                new("aggressive", ArgType.Boolean, "Use the aggressive factor"),
                new("dpi", ArgType.Integer, "PDFs: compress images to this DPI") { Min = 36, Max = 1200 },
            ],
            "optimise(factor: 50)") { Aliases = ["optimize"], DefaultArg = "factor" },
        new("downscale", StepCategory.Processing, "Make images or videos smaller, by a factor or to fit a size (never enlarges)",
            [
                new("factor", ArgType.Fraction, "Fraction of the current size, e.g. 0.5 or 50%") { Min = 0.01, Max = 1 },
                new("width", ArgType.Integer, "At most this many pixels wide") { Min = 2 },
                new("height", ArgType.Integer, "At most this many pixels tall") { Min = 2 },
                new("longEdge", ArgType.Integer, "Longest side at most this many pixels") { Min = 2 },
            ],
            "downscale(longEdge: 1920)")
        {
            Kinds = Visual, DefaultArg = "factor", Aliases = ["resize"], RequiresOneOf = ["factor", "width", "height", "longEdge"],
        },
        new("convert", StepCategory.Processing, "Convert to another format (replaces the file with the new extension)",
            [new("to", ArgType.Choice, "Target format") { Choices = ConvertTargets }],
            "convert(to: webp)") { Kinds = Visual, DefaultArg = "to", RequiresOneOf = ["to"] },
        new("crop", StepCategory.Processing, "Crop from the centre to a size or aspect ratio",
            [
                new("width", ArgType.Integer, "Width in pixels") { Min = 2 },
                new("height", ArgType.Integer, "Height in pixels") { Min = 2 },
                new("aspectRatio", ArgType.Ratio, "Width:height, e.g. \"16:9\" or \"1:1\""),
                new("smart", ArgType.Boolean, "Images: keep the most detailed part instead of the centre"),
            ],
            "crop(aspectRatio: \"1:1\", smart: true)") { Kinds = Visual, RequiresOneOf = ["width", "height", "aspectRatio"] },
        new("targetSize", StepCategory.Processing, "Make the file fit under a size (the smallest attempt is kept if it can't)",
            [new("size", ArgType.Size, "e.g. 10MB, 500KB")],
            "targetSize(10MB)") { DefaultArg = "size", Aliases = ["fit"], RequiresOneOf = ["size"] },
        new("watermark", StepCategory.Processing,
            "Overlay an image (a logo, a signature) on images, videos and animated GIFs",
            [
                new("image", ArgType.Text, "The watermark: a PNG with transparency works best (default: the one set in Settings → Watermark)"),
                new("position", ArgType.Choice, "Where it goes (default: Settings → Watermark, bottom right)") { Choices = WatermarkPositions },
                new("opacity", ArgType.Fraction, "0–1 or a percentage (default: Settings → Watermark, 100%)") { Min = 0.01, Max = 1 },
                new("scale", ArgType.Fraction, "Its width as a share of the file's width (default: Settings → Watermark, 15%)") { Min = 0.01, Max = 1 },
                new("margin", ArgType.Integer, "Pixels from the edges (default: Settings → Watermark, 20)") { Min = 0, Max = 2000 },
            ],
            "watermark(image: \"~/Pictures/logo.png\", position: bottomRight, opacity: 80%)") { Kinds = Visual, DefaultArg = "image" },
        new("stripExif", StepCategory.Processing, "Remove metadata (camera, location, dates, comments)", [],
            "stripExif") { Aliases = ["stripMetadata"] },
        new("changeSpeed", StepCategory.Processing,
            "Speed a video or audio file up (or slow it down), keeping the pitch. The speed is relative to the original, so changeSpeed(2) -> changeSpeed(1.5) ends at 1.5×",
            [
                new("factor", ArgType.Number, "2 = twice as fast") { Min = 0.25, Max = 10 },
                new("frames", ArgType.Choice, "Videos: keep (default) every frame, so the frame rate rises with the speed (up to the cap in Settings), or drop frames to stay at the source frame rate")
                {
                    Choices = ["keep", "drop"],
                },
            ],
            "changeSpeed(factor: 2, frames: drop)") { Kinds = Sound, DefaultArg = "factor", RequiresOneOf = ["factor"] },
        new("removeAudio", StepCategory.Processing, "Drop a video's audio track", [], "removeAudio") { Kinds = Videos },
        new("capFps", StepCategory.Processing, "Limit a video's frame rate",
            [new("fps", ArgType.Number, "Frames per second") { Min = 1, Max = 240 }],
            "capFps(30)") { Kinds = Videos, DefaultArg = "fps", RequiresOneOf = ["fps"] },
        new("lowerBitrate", StepCategory.Processing, "Re-encode a video or audio file at this bitrate",
            [new("kbps", ArgType.Integer, "Kilobits per second") { Min = 8, Max = 100_000 }],
            "lowerBitrate(kbps: 128)") { Kinds = Sound, DefaultArg = "kbps", RequiresOneOf = ["kbps"] },
        new("normalize", StepCategory.Processing, "Normalise loudness (EBU R128)",
            [new("lufs", ArgType.Number, "Target loudness, default -16") { Min = -70, Max = -5 }],
            "normalize(lufs: -16)") { Kinds = Sound, DefaultArg = "lufs", Aliases = ["normalise"] },

        // File operations
        new("copy", StepCategory.FileOperation, "Copy the file somewhere; later steps keep working on the file itself",
            [To, new("overwrite", ArgType.Boolean, "Replace an existing file (default: add a number)")],
            "copy(to: \"~/Pictures/Shared/\")") { DefaultArg = "to", RequiresOneOf = ["to"] },
        new("move", StepCategory.FileOperation, "Move the file; later steps work on it in its new place",
            [To, new("overwrite", ArgType.Boolean, "Replace an existing file (default: add a number)")],
            "move(to: \"~/Pictures/Screenshots/%y/%m/\")") { DefaultArg = "to", RequiresOneOf = ["to"] },
        new("rename", StepCategory.FileOperation, "Rename in the same folder (the extension is kept unless given)",
            [new("to", ArgType.Text, "New name; %f is the current name, e.g. \"%f-web\"")],
            "rename(to: \"%y-%m-%d %f\")") { DefaultArg = "to", RequiresOneOf = ["to"] },
        new("delete", StepCategory.FileOperation, "Send the file to the Recycle Bin and stop", [], "delete"),

        // Filters
        new("if", StepCategory.Filter, "Continue only if every condition holds",
            FilterArgs(), "if(regex: \"^screenshot\", sizeGreaterThan: 1MB)") { DefaultArg = "regex" },
        new("ifNot", StepCategory.Filter, "Continue only if the conditions don't all hold",
            FilterArgs(), "ifNot(type: gif)") { DefaultArg = "regex" },

        // Actions
        new("runScript", StepCategory.Action,
            "Run a program or script with the file as its first argument (also in %WCLOP_INPUT_FILE%). If it prints the path of an existing file of the same kind, that becomes the working file",
            [
                new("path", ArgType.Text, "An .exe, .ps1, .bat/.cmd, .py or .js file"),
                new("code", ArgType.Text, "Inline code instead of a file"),
                new("shell", ArgType.Choice, "What runs inline code (default powershell)") { Choices = ["powershell", "pwsh", "cmd"] },
            ],
            "runScript(code: \"Copy-Item $env:WCLOP_INPUT_FILE 'D:\\Backup'\")") { DefaultArg = "path", RequiresOneOf = ["path", "code"] },
        new("extractPagesAsImages", StepCategory.Action,
            "Save every page of a PDF as an image (the PDF itself carries on through the pipeline)",
            [
                new("format", ArgType.Choice, "jpeg (default) or png") { Choices = ["jpeg", "png"] },
                new("quality", ArgType.Choice, "low (100 DPI), medium (150, default) or high (220)") { Choices = ["low", "medium", "high"] },
                new("dpi", ArgType.Integer, "Exact resolution instead of a quality preset") { Min = 36, Max = 600 },
                new("to", ArgType.Text, "Folder for the images (default: a \"<name> pages\" folder next to the PDF)"),
            ],
            "extractPagesAsImages(format: png, quality: high)") { Kinds = new HashSet<MediaKind> { MediaKind.Pdf }, DefaultArg = "format" },
        new("fork", StepCategory.Action,
            "Run some steps on a copy (usually ending in move or copy), then carry on with the original",
            [new("steps", ArgType.Text, "Steps in quotes, or the name of a saved pipeline")],
            "fork(\"convert(webp) -> move(to: '~/Pictures/Web/')\")") { DefaultArg = "steps", RequiresOneOf = ["steps"] },
        new("copyToClipboard", StepCategory.Action, "Put the result on the clipboard",
            [new("as", ArgType.Choice, "file (default), image, path or markdown") { Choices = ["file", "image", "path", "markdown"] }],
            "copyToClipboard(as: path)") { DefaultArg = "as" },
        new("openWith", StepCategory.Action, "Open the file in an app (default: the file's usual app)",
            [new("app", ArgType.Text, "Program name or path, e.g. mspaint or \"C:\\Program Files\\GIMP 2\\bin\\gimp-2.10.exe\"")],
            "openWith(app: mspaint)") { DefaultArg = "app" },
    ];

    private static IReadOnlyList<ArgSpec> FilterArgs() =>
    [
        new("type", ArgType.Text, "image, video, pdf, audio, or formats like png / jpeg / mp4 (comma-separated)"),
        new("regex", ArgType.Regex, "Matches the file name (case-insensitive unless it has capitals); groups become $1, $2 …"),
        new("nameContains", ArgType.Text, "Text somewhere in the file name (case-insensitive)"),
        new("nameIs", ArgType.Text, "The file name, with or without extension (case-insensitive)"),
        new("sizeGreaterThan", ArgType.Size, "e.g. 1MB"),
        new("sizeLowerThan", ArgType.Size, "e.g. 500KB"),
        new("widthGreaterThan", ArgType.Integer, "Pixels (images and videos)"),
        new("widthLowerThan", ArgType.Integer, "Pixels"),
        new("heightGreaterThan", ArgType.Integer, "Pixels"),
        new("heightLowerThan", ArgType.Integer, "Pixels"),
        new("copiedBy", ArgType.Text, "Clipboard only: the app it was copied from, e.g. chrome or SnippingTool"),
        new("source", ArgType.Choice, "Where the file came from") { Choices = ["clipboard", "dropzone", "folder", "cli", "manual"] },
    ];

    private static readonly Dictionary<string, StepSpec> ByName = Steps
        .SelectMany(s => s.Aliases.Prepend(s.Name).Select(n => (n, s)))
        .ToDictionary(p => p.n, p => p.s, StringComparer.OrdinalIgnoreCase);

    public static StepSpec? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>Parses and checks a pipeline. Throws <see cref="PipelineSyntaxException"/> with the position of the problem.</summary>
    public static CompiledPipeline Compile(string text)
    {
        var steps = PipelineSyntax.Parse(text).Select(CompileStep).ToList();
        var deleteAt = steps.FindIndex(s => s.Name == "delete");
        if (deleteAt >= 0 && deleteAt < steps.Count - 1)
            throw new PipelineSyntaxException("Nothing can come after delete", steps[deleteAt + 1].Source.Position);
        return new CompiledPipeline(steps, text);
    }

    public static bool TryCompile(string text, out CompiledPipeline? pipeline, out string? error)
    {
        try
        {
            pipeline = Compile(text);
            error = null;
            return true;
        }
        catch (PipelineSyntaxException e)
        {
            var (line, column) = PipelineSyntax.LineAndColumn(text, e.Position);
            pipeline = null;
            error = text.Contains('\n') ? $"Line {line}, column {column}: {e.Message}" : $"At {column}: {e.Message}";
            return false;
        }
    }

    private static CompiledStep CompileStep(PipelineStep step)
    {
        var spec = Find(step.Name)
                   ?? throw new PipelineSyntaxException(
                       $"Unknown step '{step.Name}'{Suggest(step.Name, ByName.Keys)}", step.Position);

        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (givenName, value) in step.Args)
        {
            var name = givenName ?? spec.DefaultArg
                       ?? throw new PipelineSyntaxException(spec.Args.Count == 0
                           ? $"{spec.Name} takes no values"
                           : $"{spec.Name} needs named values, e.g. {spec.Example}", value.Position);
            var arg = spec.Args.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                      ?? throw new PipelineSyntaxException(
                          $"{spec.Name} has no '{name}'{Suggest(name, spec.Args.Select(a => a.Name))}" +
                          (spec.Args.Count == 0 ? " (it takes no values)" : $"; it takes {string.Join(", ", spec.Args.Select(a => a.Name))}"),
                          value.Position);
            if (values.ContainsKey(arg.Name))
                throw new PipelineSyntaxException($"'{arg.Name}' is given twice", value.Position);
            values[arg.Name] = Convert(spec, arg, value);
        }

        if (spec.RequiresOneOf.Count > 0 && !spec.RequiresOneOf.Any(values.ContainsKey))
            throw new PipelineSyntaxException(
                $"{spec.Name} needs {(spec.RequiresOneOf.Count == 1 ? spec.RequiresOneOf[0] : "one of " + string.Join(", ", spec.RequiresOneOf))}, e.g. {spec.Example}",
                step.Position);
        // A fork's steps are checked now unless they're just a saved pipeline's name (looked up when it runs).
        if (spec.Name == "fork" && values["steps"] is string forked && (forked.Contains('(') || forked.Contains("->")))
        {
            try
            {
                Compile(forked);
            }
            catch (PipelineSyntaxException e)
            {
                throw new PipelineSyntaxException($"In fork: {e.Message}", step.Position);
            }
        }

        if (spec.Category == StepCategory.Filter && values.Count == 0)
            throw new PipelineSyntaxException($"{spec.Name} needs at least one condition, e.g. {spec.Example}", step.Position);

        return new CompiledStep(spec, values, step);
    }

    private static object Convert(StepSpec step, ArgSpec arg, PipelineValue value)
    {
        var text = value.Text.Trim();
        PipelineSyntaxException Bad(string expected) =>
            new($"{step.Name}({arg.Name}) should be {expected}, not '{value.Text}'", value.Position);

        object result;
        switch (arg.Type)
        {
            case ArgType.Text:
                return value.Text;
            case ArgType.Integer:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    throw Bad("a whole number");
                result = integer;
                break;
            case ArgType.Number:
                if (!double.TryParse(text.TrimEnd('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw Bad("a number");
                result = number;
                break;
            case ArgType.Fraction:
                var percent = text.EndsWith('%');
                if (!double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
                    throw Bad("a fraction like 0.5 or 50%");
                result = percent || fraction > 1 ? fraction / 100 : fraction;
                break;
            case ArgType.Size:
                if (!SizeText.TryParse(text, out var bytes))
                    throw Bad("a size like 10MB or 500KB");
                return bytes;
            case ArgType.Boolean:
                return text.ToLowerInvariant() switch
                {
                    "true" or "yes" or "on" => true,
                    "false" or "no" or "off" => false,
                    _ => throw Bad("true or false"),
                };
            case ArgType.Choice:
                // Formats by any of their extensions: jpeg = jpg, tif = tiff.
                var asFormat = arg.Choices == ConvertTargets
                    ? FileFormats.FromExtension("x." + text.TrimStart('.')).Extension().TrimStart('.')
                    : text;
                return arg.Choices.FirstOrDefault(c => c.Equals(asFormat, StringComparison.OrdinalIgnoreCase))
                       ?? throw Bad("one of " + string.Join(", ", arg.Choices));
            case ArgType.Regex:
                try
                {
                    // Case-insensitive unless it contains a capital letter that isn't an escape like \S or \W.
                    var caseSensitive = Regex.IsMatch(Regex.Replace(value.Text, @"\\.", ""), "[A-Z]");
                    return new Regex(value.Text, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException e)
                {
                    throw new PipelineSyntaxException($"Invalid regex: {e.Message}", value.Position);
                }
            case ArgType.Ratio:
                if (!Cropping.CropSpec.TryParseRatio(text, out var ratio))
                    throw Bad("a ratio like \"16:9\"");
                return ratio;
            default:
                throw new ArgumentOutOfRangeException(nameof(arg));
        }

        var numeric = System.Convert.ToDouble(result, CultureInfo.InvariantCulture);
        if (numeric < arg.Min || numeric > arg.Max)
            throw Bad($"between {arg.Min?.ToString(CultureInfo.InvariantCulture) ?? "…"} and {arg.Max?.ToString(CultureInfo.InvariantCulture) ?? "…"}");
        return result;
    }

    private static string Suggest(string name, IEnumerable<string> options)
    {
        var best = options
            .Select(o => (o, d: Distance(name.ToLowerInvariant(), o.ToLowerInvariant())))
            .OrderBy(p => p.d).FirstOrDefault();
        return best.o is not null && best.d <= Math.Max(2, name.Length / 3) ? $" (did you mean {best.o}?)" : "";
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>
    /// A complete reference of the language for an AI assistant (project.md §19.5): <c>wclop pipeline prompt</c>.
    /// </summary>
    public static string Prompt()
    {
        var text = new StringBuilder("""
            # WClop pipeline language

            WClop (a Windows image/video/PDF/audio optimiser) runs *pipelines*: steps joined by `->`, applied to one file at a time.
            Syntax: `step(name: value, other: value) -> step2 -> step3`. A step with no values can drop the parentheses.
            A single unnamed value goes to the step's main argument: `convert(webp)` = `convert(to: webp)`.
            Values: numbers (`0.5`, `1920`), sizes (`10MB`, `500KB`), percentages (`50%`), words (`webp`, `true`) and
            quoted text (`"~/Pictures/%y/"`; use quotes for anything with spaces, slashes, colons or commas).
            Newlines are whitespace; `#` starts a comment.

            Behaviour:
            - Unless the pipeline has "skip optimisation" set, the file is optimised first; a pipeline that re-encodes
              (downscale, convert, crop, …) usually wants "skip optimisation" to avoid encoding twice.
            - Processing steps replace the working file in place; the original is backed up first and can be restored.
            - A step that doesn't apply to the file's type (e.g. removeAudio on an image) is skipped.
            - `if` / `ifNot` stop the pipeline when their conditions fail. Conditions in one filter are all required.
            - Regex capture groups from `if(regex: …)` can be used later as `$1`, `$2` or `${name}` in text values.
            - Paths: `~` is the user folder; name tokens: %f name without extension, %e extension, %P parent folder,
              %y year, %m month, %d day, %H %M %S time, %r random, %i counter. A `to` ending in / or \ is a folder.

            ## Steps

            """);

        foreach (var group in Steps.GroupBy(s => s.Category))
        {
            text.AppendLine($"### {group.Key switch
            {
                StepCategory.Processing => "Processing",
                StepCategory.FileOperation => "File operations",
                StepCategory.Filter => "Filters",
                _ => "Actions",
            }}").AppendLine();
            foreach (var step in group)
            {
                var kinds = step.Kinds.Count == 0 ? "" : $" _({string.Join(", ", step.Kinds.Select(k => k.ToString().ToLowerInvariant() + "s"))} only)_";
                var aliases = step.Aliases.Count == 0 ? "" : $" (also `{string.Join("`, `", step.Aliases)}`)";
                text.AppendLine($"- **{step.Name}**{aliases}: {step.Description}{kinds}. Example: `{step.Example}`");
                foreach (var arg in step.Args)
                {
                    var choices = arg.Choices.Count > 0 ? $" [{string.Join(" | ", arg.Choices)}]" : "";
                    var main = arg.Name == step.DefaultArg ? " (main)" : "";
                    text.AppendLine($"  - `{arg.Name}`{main}: {arg.Type.ToString().ToLowerInvariant()}{choices}. {arg.Description}");
                }
            }

            text.AppendLine();
        }

        text.Append("""
            ## Examples

            ```
            if(regex: "^screenshot") -> downscale(longEdge: 1920) -> move(to: "~/Pictures/Screenshots/%y/%m/")
            convert(to: webp) -> copyToClipboard(as: path)
            if(type: video, sizeGreaterThan: 25MB) -> targetSize(25MB)
            changeSpeed(2) -> removeAudio -> optimise
            if(copiedBy: chrome) -> crop(aspectRatio: "16:9") -> stripExif
            downscale(longEdge: 1600) -> watermark(position: bottomRight, opacity: 70%)
            if(regex: "^IMG_(\d+)") -> rename(to: "photo-$1")
            ```
            """);
        return text.ToString();
    }
}
