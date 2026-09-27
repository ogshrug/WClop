using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using WClop.Core.Audio;
using WClop.Core.Compression;
using WClop.Core.Images;
using WClop.Core.Logging;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Placement;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Video;
using WClop.Core.Watching;

namespace WClop.Core.Pipelines;

/// <summary>Where a file came from, for <c>if(source: …)</c>.</summary>
public enum PipelineOrigin
{
    Clipboard,
    DropZone,
    Folder,
    Cli,
    Manual,
}

public sealed record PipelineContext
{
    public PipelineOrigin Origin { get; init; } = PipelineOrigin.Manual;

    /// <summary>The app a clipboard item came from (exe name), for <c>if(copiedBy: …)</c>.</summary>
    public string? CopiedBy { get; init; }

    public bool AllowScripts { get; init; } = true;

    /// <summary>Puts a file on the clipboard (path, "file" / "image" / "path" / "markdown"); null where there's no clipboard.</summary>
    public Func<string, string, Task>? CopyToClipboard { get; init; }

    public Action<string>? OnStatus { get; init; }
    public Action<double>? OnProgress { get; init; }
}

/// <summary>A step failed in a way worth telling the user (the original is always backed up).</summary>
public sealed class PipelineException(string message) : OptimisationException(message);

/// <summary>What a run did. <see cref="StoppedBy"/> is set when a filter ended it early; the result is still valid.</summary>
public sealed record PipelineOutcome(FileOptimisationResult Result, bool Deleted, string? StoppedBy, IReadOnlyList<string> Log)
{
    public bool Changed => !Deleted && (Result.OutputPath != Result.InputPath || Result.NewSize != Result.OldSize);
}

/// <summary>
/// Runs a pipeline on one file (project.md §19). The run starts from a result whose original is backed up (an
/// optimisation, or the file described as it is), and each processing step replaces the working file in place, so
/// the result can always be restored to the original.
/// </summary>
public sealed class PipelineRunner(FileOptimisationService service, AppSettings settings)
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(10);

    private ToolLocator Tools => service.Tools;
    private AppPaths Paths => service.Paths;

    /// <summary>
    /// The starting point: optimised first (unless <paramref name="optimiseFirst"/> is false), or the file as it is if
    /// there's nothing to gain.
    /// </summary>
    public async Task<FileOptimisationResult> StartAsync(
        string path, bool optimiseFirst, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (optimiseFirst)
        {
            try
            {
                return await service.OptimiseAsync(path, new FileOptimisationRequest { Force = true }, onProgress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is NotSmallerException or AlreadyOptimisedException)
            {
                // "Already fully compressed" isn't an error in a pipeline: carry on with the file as it is.
            }
        }

        return await service.DescribeAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineOutcome> RunFileAsync(
        string name, CompiledPipeline pipeline, string path, bool skipOptimisation, PipelineContext context,
        CancellationToken cancellationToken = default)
    {
        var start = await StartAsync(path, !skipOptimisation, context.OnProgress, cancellationToken).ConfigureAwait(false);
        return await RunAsync(name, pipeline, start, context, cancellationToken).ConfigureAwait(false);
    }

    public Task<PipelineOutcome> RunAsync(
        string name, CompiledPipeline pipeline, FileOptimisationResult start, PipelineContext context,
        CancellationToken cancellationToken = default) =>
        RunAsync(name, pipeline, start, context, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), 0, cancellationToken);

    /// <summary>Forks inside forks are fine, a pipeline that forks itself isn't.</summary>
    private const int MaxForkDepth = 4;

    private async Task<PipelineOutcome> RunAsync(
        string name, CompiledPipeline pipeline, FileOptimisationResult start, PipelineContext context,
        Dictionary<string, string> captures, int depth, CancellationToken cancellationToken)
    {
        if (!context.AllowScripts && pipeline.RunsScripts)
            throw new PipelineException("This pipeline runs a script, and scripts aren't allowed here (see Settings → Pipelines)");

        var run = new Run(start.OutputPath, context, captures, depth);
        string? stoppedBy = null;
        var deleted = false;

        foreach (var step in pipeline.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(run.Current))
                throw new PipelineException($"{Path.GetFileName(run.Current)} disappeared before {step.Name}");

            var kind = FileTypeSniffer.Detect(run.Current).Kind();
            if (!step.Spec.AppliesTo(kind))
            {
                run.Note($"{step.Name}: skipped (not for {kind.ToString().ToLowerInvariant()} files)");
                continue;
            }

            context.OnStatus?.Invoke($"{name}: {step.Name}");
            context.OnProgress?.Invoke(0);
            switch (step.Spec.Category)
            {
                case StepCategory.Filter:
                    if (!await PassesAsync(step, run, cancellationToken).ConfigureAwait(false))
                        stoppedBy = step.ToString();
                    break;
                case StepCategory.Processing:
                    await ProcessAsync(step, run, kind, cancellationToken).ConfigureAwait(false);
                    break;
                case StepCategory.FileOperation:
                    deleted = FileOperation(step, run);
                    break;
                case StepCategory.Action:
                    await ActionAsync(step, run, kind, cancellationToken).ConfigureAwait(false);
                    break;
            }

            if (stoppedBy is not null)
            {
                run.Note($"stopped: {stoppedBy} didn't match");
                break;
            }

            if (deleted)
                break;
        }

        Log.Info($"Pipeline {name}: {Path.GetFileName(start.InputPath)}: {string.Join("; ", run.Log)}");
        var result = start with
        {
            OutputPath = run.Current,
            OutputFormat = deleted ? start.OutputFormat : FileTypeSniffer.Detect(run.Current),
            NewSize = deleted ? 0 : new FileInfo(run.Current).Length,
            FromCache = false,
            // Adjusting would redo only the optimisation from the original, losing the pipeline's work.
            IsConversion = start.IsConversion || run.Changed,
            Pipeline = name,
        };
        return new PipelineOutcome(result, deleted, stoppedBy, run.Log);
    }

    private sealed class Run(string current, PipelineContext context, Dictionary<string, string> captures, int depth)
    {
        public string Current { get; set; } = current;
        public PipelineContext Context { get; } = context;
        public bool Changed { get; set; }
        public int Depth { get; } = depth;
        public Dictionary<string, string> Captures { get; } = captures;
        public List<string> Log { get; } = [];
        public void Note(string message) => Log.Add(message);

        /// <summary>Fills in regex captures: $1, ${name}.</summary>
        public string Expand(string text) =>
            Regex.Replace(text, @"\$(?:(\d+)|\{(\w+)\})", m => Captures.GetValueOrDefault(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, ""));
    }

    // Filters

    private async Task<bool> PassesAsync(CompiledStep step, Run run, CancellationToken cancellationToken)
    {
        var path = run.Current;
        var name = Path.GetFileName(path);
        var format = FileTypeSniffer.Detect(path);
        ImageSize? dimensions = null;
        var measured = false;

        async Task<ImageSize?> Dimensions()
        {
            if (!measured)
            {
                measured = true;
                dimensions = await ReadSizeAsync(path, format.Kind(), cancellationToken).ConfigureAwait(false);
            }

            return dimensions;
        }

        var all = true;
        foreach (var (arg, value) in step.Values)
        {
            bool holds;
            switch (arg)
            {
                case "type":
                    holds = ((string)value).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Any(t => MatchesType(t, format));
                    break;
                case "regex":
                    var match = ((Regex)value).Match(name);
                    holds = match.Success;
                    if (holds)
                    {
                        foreach (Group group in match.Groups)
                        {
                            if (group.Success && group.Name != "0")
                                run.Captures[group.Name] = group.Value;
                        }
                    }

                    break;
                case "nameContains":
                    holds = name.Contains(run.Expand((string)value), StringComparison.OrdinalIgnoreCase);
                    break;
                case "nameIs":
                    var wanted = run.Expand((string)value);
                    holds = name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileNameWithoutExtension(path).Equals(wanted, StringComparison.OrdinalIgnoreCase);
                    break;
                case "sizeGreaterThan":
                    holds = new FileInfo(path).Length > (long)value;
                    break;
                case "sizeLowerThan":
                    holds = new FileInfo(path).Length < (long)value;
                    break;
                case "widthGreaterThan":
                    holds = await Dimensions().ConfigureAwait(false) is { } w1 && w1.Width > (int)value;
                    break;
                case "widthLowerThan":
                    holds = await Dimensions().ConfigureAwait(false) is { } w2 && w2.Width < (int)value;
                    break;
                case "heightGreaterThan":
                    holds = await Dimensions().ConfigureAwait(false) is { } h1 && h1.Height > (int)value;
                    break;
                case "heightLowerThan":
                    holds = await Dimensions().ConfigureAwait(false) is { } h2 && h2.Height < (int)value;
                    break;
                case "copiedBy":
                    holds = run.Context.CopiedBy is { } app && StripExe(app).Equals(StripExe((string)value), StringComparison.OrdinalIgnoreCase);
                    break;
                case "source":
                    holds = run.Context.Origin.ToString().Equals((string)value, StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    throw new PipelineException($"Unknown condition {arg}");
            }

            all &= holds;
        }

        return step.Name == "if" ? all : !all;
    }

    private static string StripExe(string app) => app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;

    internal static bool MatchesType(string type, FileFormat format) => type.ToLowerInvariant().TrimStart('.') switch
    {
        "image" or "images" => format.Kind() == MediaKind.Image,
        "video" or "videos" => format.Kind() == MediaKind.Video,
        "pdf" or "pdfs" => format.Kind() == MediaKind.Pdf,
        "audio" => format.Kind() == MediaKind.Audio,
        var extension => FileFormats.FromExtension("x." + extension) is var wanted && wanted != FileFormat.Unknown && wanted == format,
    };

    private async Task<ImageSize?> ReadSizeAsync(string path, MediaKind kind, CancellationToken cancellationToken) => kind switch
    {
        MediaKind.Image => ImageDecoding.TryReadSize(path),
        MediaKind.Video => await VideoInfo.ProbeAsync(Tools.Require(Tool.Ffprobe), path, cancellationToken).ConfigureAwait(false) is { } info
            ? new ImageSize(info.Width, info.Height)
            : null,
        _ => null,
    };

    // Processing

    private async Task ProcessAsync(CompiledStep step, Run run, MediaKind kind, CancellationToken cancellationToken)
    {
        switch (step.Name)
        {
            case "optimise":
                var factor = step.Get<int?>("factor")
                             ?? (step.Get<bool>("aggressive") ? CompressionModel.AggressiveImageFactor : null);
                await ServiceStepAsync(run, new FileOptimisationRequest { Factor = factor, PdfDpi = step.Get<int?>("dpi") }, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "downscale":
                var size = await ReadSizeAsync(run.Current, kind, cancellationToken).ConfigureAwait(false)
                           ?? throw new PipelineException("Can't read the size to downscale");
                var scale = DownscaleFactor(step, size);
                if (scale >= 0.999)
                {
                    run.Note($"downscale: already {size}");
                    break;
                }

                await ServiceStepAsync(run, new FileOptimisationRequest { Scale = scale, AllowLarger = true }, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "targetSize":
                await ServiceStepAsync(run, new FileOptimisationRequest { TargetBytes = step.Get<long>("size") }, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "changeSpeed":
                await ServiceStepAsync(run, new FileOptimisationRequest { Speed = step.Get<double>("factor"), AllowLarger = true }, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "convert":
                var target = FileFormats.FromExtension("x." + step.Get<string>("to"));
                if (FileTypeSniffer.Detect(run.Current) == target && !(kind == MediaKind.Video && target == FileFormat.Mp4))
                {
                    run.Note($"convert: already {target}");
                    break;
                }

                var converted = await service.ConvertFileAsync(run.Current, target, run.Context.OnProgress, cancellationToken)
                    .ConfigureAwait(false);
                Commit(run, converted, $"converted to {target.ToString().ToUpperInvariant()}");
                break;

            case "crop":
                await CropAsync(step, run, kind, cancellationToken).ConfigureAwait(false);
                break;

            case "watermark":
                await WatermarkAsync(step, run, kind, cancellationToken).ConfigureAwait(false);
                break;

            case "stripExif":
                await StripMetadataAsync(run, kind, cancellationToken).ConfigureAwait(false);
                break;

            case "removeAudio":
                if (await ProbeVideoAsync(run.Current, cancellationToken).ConfigureAwait(false) is { HasAudio: false })
                {
                    run.Note("removeAudio: no audio track");
                    break;
                }

                await FfmpegStepAsync(run, "removed audio", ["-map", "0:v", "-c", "copy", "-an"], null, cancellationToken).ConfigureAwait(false);
                break;

            case "capFps":
                var fps = step.Get<double>("fps");
                if (await ProbeVideoAsync(run.Current, cancellationToken).ConfigureAwait(false) is { } video && video.PeakFrameRate <= fps + 0.01)
                {
                    run.Note($"capFps: already {video.PeakFrameRate:0.##} fps");
                    break;
                }

                var fpsText = fps.ToString(CultureInfo.InvariantCulture);
                await FfmpegStepAsync(run, $"capped at {fpsText} fps",
                    ["-vf", $"fps={fpsText}", .. VideoCodec(run.Current), "-c:a", "copy"], VideoOutputExtension(run.Current), cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "lowerBitrate":
                await LowerBitrateAsync(run, kind, step.Get<int>("kbps"), cancellationToken).ConfigureAwait(false);
                break;

            case "normalize":
                await NormaliseAsync(run, kind, step.Has("lufs") ? step.Get<double>("lufs") : -16, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new PipelineException($"{step.Name} isn't implemented yet");
        }
    }

    internal static double DownscaleFactor(CompiledStep step, ImageSize size)
    {
        var scale = 1.0;
        if (step.Has("factor"))
            scale = Math.Min(scale, step.Get<double>("factor"));
        if (step.Has("width"))
            scale = Math.Min(scale, (double)step.Get<int>("width") / size.Width);
        if (step.Has("height"))
            scale = Math.Min(scale, (double)step.Get<int>("height") / size.Height);
        if (step.Has("longEdge"))
            scale = Math.Min(scale, (double)step.Get<int>("longEdge") / Math.Max(size.Width, size.Height));
        return scale;
    }

    /// <summary>A step done by the normal engine, from the current file, replacing it in place.</summary>
    private async Task ServiceStepAsync(Run run, FileOptimisationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.OptimiseAsync(
                run.Current, request with { Force = true, Behaviour = OutputBehaviour.InPlace }, run.Context.OnProgress, cancellationToken)
                .ConfigureAwait(false);
            run.Current = result.OutputPath;
            run.Changed = true;
            run.Note($"{result.OldSize} → {result.NewSize} bytes{(result.MissedTarget ? " (couldn't reach the target)" : "")}");
        }
        catch (NotSmallerException)
        {
            run.Note("already fully compressed");
        }
    }

    /// <summary>Replaces the working file with a produced one (which may have a new extension).</summary>
    private void Commit(Run run, string produced, string what)
    {
        var before = new FileInfo(run.Current).Length;
        run.Current = service.ReplaceInPlace(produced, run.Current, FileTimes.Of(run.Current));
        run.Changed = true;
        run.Note($"{what}: {before} → {new FileInfo(run.Current).Length} bytes");
    }

    internal static (int Width, int Height, int X, int Y)? CropRectangle(CompiledStep step, ImageSize size, bool even)
    {
        double width = size.Width, height = size.Height;
        if (step.Has("aspectRatio"))
        {
            var ratio = step.Get<double>("aspectRatio");
            if (step.Has("width"))
            {
                width = Math.Min(step.Get<int>("width"), size.Width);
                height = width / ratio;
            }
            else if (step.Has("height"))
            {
                height = Math.Min(step.Get<int>("height"), size.Height);
                width = height * ratio;
            }
            else if ((double)size.Width / size.Height > ratio)
            {
                width = size.Height * ratio;
            }
            else
            {
                height = size.Width / ratio;
            }

            // Shrink to fit if the other side came out too big.
            var fit = Math.Min(1, Math.Min(size.Width / width, size.Height / height));
            width *= fit;
            height *= fit;
        }
        else
        {
            width = Math.Min(step.Has("width") ? step.Get<int>("width") : size.Width, size.Width);
            height = Math.Min(step.Has("height") ? step.Get<int>("height") : size.Height, size.Height);
        }

        var w = (int)Math.Round(width);
        var h = (int)Math.Round(height);
        if (even)
        {
            w -= w % 2;
            h -= h % 2;
        }

        w = Math.Max(w, 2);
        h = Math.Max(h, 2);
        if (w >= size.Width && h >= size.Height)
            return null;
        return (w, h, (size.Width - w) / 2, (size.Height - h) / 2);
    }

    private async Task CropAsync(CompiledStep step, Run run, MediaKind kind, CancellationToken cancellationToken)
    {
        var size = await ReadSizeAsync(run.Current, kind, cancellationToken).ConfigureAwait(false)
                   ?? throw new PipelineException("Can't read the size to crop");
        if (CropRectangle(step, size, even: kind == MediaKind.Video) is not var (w, h, x, y))
        {
            run.Note($"crop: already {size}");
            return;
        }

        var format = FileTypeSniffer.Detect(run.Current);
        var what = $"cropped to {w}×{h}";
        if (step.Get<bool>("smart") && kind == MediaKind.Image)
        {
            try
            {
                (x, y) = ImageInterest.BestWindow(run.Current, size, w, h);
                what += " (smart)";
            }
            catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
            {
                // WIC can't decode it (HEIC without the extension…): the centre it is.
            }
        }
        if (format == FileFormat.Gif)
        {
            var output = TempPath(".gif");
            var result = await ProcessRunner.RunAsync(Tools.Require(Tool.Gifsicle),
                ["--crop", $"{x},{y}+{w}x{h}", "-o", output, run.Current], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
                throw new ToolFailedException(result);
            Commit(run, output, what);
            return;
        }

        var crop = $"crop={w}:{h}:{x}:{y}";
        if (kind == MediaKind.Video)
        {
            await FfmpegStepAsync(run, what, ["-vf", crop, .. VideoCodec(run.Current), "-c:a", "copy"], VideoOutputExtension(run.Current), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string[] quality = format switch
        {
            FileFormat.Jpeg => ["-q:v", "2"],
            FileFormat.WebP => ["-quality", "92"],
            FileFormat.Png or FileFormat.Bmp or FileFormat.Tiff => [],
            _ => throw new PipelineException($"crop can't write {format} images; convert them first"),
        };
        await FfmpegStepAsync(run, what, ["-vf", crop, "-frames:v", "1", "-update", "1", .. quality], null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Overlays the watermark image (project.md §8.11): scaled to a share of the file's width, at a corner or the
    /// centre, with opacity. FFmpeg composites every frame of videos and animated GIFs; stills keep their alpha.
    /// </summary>
    private async Task WatermarkAsync(CompiledStep step, Run run, MediaKind kind, CancellationToken cancellationToken)
    {
        var image = step.Get<string>("image") is { } given
            ? ResolvePath(run.Expand(given))
            : settings.Pipelines.DefaultWatermark is { Length: > 0 } fallback
                ? PortablePath.Expand(fallback)
                : throw new PipelineException("No watermark image: give one, e.g. watermark(image: \"~/Pictures/logo.png\"), or set a default in Settings → Pipelines");
        if (!File.Exists(image))
            throw new PipelineException($"Watermark image not found: {image}");
        if (FileTypeSniffer.Detect(image).Kind() != MediaKind.Image)
            throw new PipelineException($"The watermark must be an image: {Path.GetFileName(image)}");

        var size = await ReadSizeAsync(run.Current, kind, cancellationToken).ConfigureAwait(false)
                   ?? throw new PipelineException("Can't read the size to place the watermark");
        var position = step.Get<string>("position") ?? "bottomRight";
        var opacity = step.Has("opacity") ? step.Get<double>("opacity") : 1.0;
        var scale = step.Has("scale") ? step.Get<double>("scale") : 0.15;
        var margin = step.Has("margin") ? step.Get<int>("margin") : 20;

        var (x, y) = WatermarkPlacement(position, margin);
        var width = Math.Max(2, (int)Math.Round(size.Width * scale));
        var mark = string.Create(CultureInfo.InvariantCulture,
            $"[1:v]format=rgba,scale={width}:-1:flags=lanczos,colorchannelmixer=aa={opacity:0.###}[mark]");

        var format = FileTypeSniffer.Detect(run.Current);
        string filter;
        string[] output;
        string? extension = null;
        if (kind == MediaKind.Video)
        {
            filter = $"{mark};[0:v][mark]overlay={x}:{y}[out]";
            output = ["-map", "[out]", "-map", "0:a?", .. VideoCodec(run.Current), "-c:a", "copy"];
            extension = VideoOutputExtension(run.Current);
        }
        else if (format == FileFormat.Gif)
        {
            // Every frame, then a fresh palette so the logo's colours survive.
            filter = $"{mark};[0:v][mark]overlay={x}:{y},split[a][b];[a]palettegen=reserve_transparent=1[p];[b][p]paletteuse[out]";
            output = ["-map", "[out]", "-loop", "0"];
        }
        else
        {
            string[] quality = format switch
            {
                FileFormat.Jpeg => ["-q:v", "2"],
                FileFormat.WebP => ["-quality", "92"],
                FileFormat.Png or FileFormat.Bmp or FileFormat.Tiff => [],
                _ => throw new PipelineException($"watermark can't write {format} images; convert them first"),
            };
            filter = $"{mark};[0:v]format=rgba[base];[base][mark]overlay={x}:{y}:format=auto[out]";
            output = ["-map", "[out]", "-frames:v", "1", "-update", "1", .. quality];
        }

        await FfmpegStepAsync(run, $"watermarked ({position})", ["-i", image, "-filter_complex", filter, .. output], extension, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static (string X, string Y) WatermarkPlacement(string position, int margin) => position switch
    {
        "bottomLeft" => ($"{margin}", $"H-h-{margin}"),
        "topRight" => ($"W-w-{margin}", $"{margin}"),
        "topLeft" => ($"{margin}", $"{margin}"),
        "center" => ("(W-w)/2", "(H-h)/2"),
        _ => ($"W-w-{margin}", $"H-h-{margin}"),
    };

    /// <summary>A path as typed in a pipeline: <c>~</c> is the user folder, environment variables are expanded.</summary>
    private static string ResolvePath(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    private async Task StripMetadataAsync(Run run, MediaKind kind, CancellationToken cancellationToken)
    {
        if (kind is MediaKind.Video or MediaKind.Audio)
        {
            await FfmpegStepAsync(run, "stripped metadata", ["-map", "0", "-map_metadata", "-1", "-map_chapters", "-1", "-c", "copy"], null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var copy = TempPath(Path.GetExtension(run.Current));
        File.Copy(run.Current, copy);
        var result = await ProcessRunner.RunAsync(Tools.Require(Tool.Exiftool),
            ["-all=", "-overwrite_original", "-q", copy], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            File.Delete(copy);
            throw new ToolFailedException(result);
        }

        Commit(run, copy, "stripped metadata");
    }

    private async Task LowerBitrateAsync(Run run, MediaKind kind, int kbps, CancellationToken cancellationToken)
    {
        var rate = $"{kbps}k";
        if (kind == MediaKind.Video)
        {
            string[] codec = FileTypeSniffer.Detect(run.Current) == FileFormat.WebM
                ? ["-c:v", "libvpx-vp9", "-b:v", rate, "-row-mt", "1"]
                : ["-c:v", "libx264", "-preset", "medium", "-b:v", rate, "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k", "-pix_fmt", "yuv420p"];
            await FfmpegStepAsync(run, $"re-encoded at {rate}bps", [.. codec, "-c:a", "copy"], VideoOutputExtension(run.Current), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (AudioEncoder(FileTypeSniffer.Detect(run.Current)) is not { } encoder)
        {
            run.Note("lowerBitrate: lossless audio has no bitrate to lower; convert or optimise it instead");
            return;
        }

        await FfmpegStepAsync(run, $"re-encoded at {rate}bps", ["-map", "0", "-c:v", "copy", "-c:a", encoder, "-b:a", rate], null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task NormaliseAsync(Run run, MediaKind kind, double lufs, CancellationToken cancellationToken)
    {
        var filter = string.Create(CultureInfo.InvariantCulture, $"loudnorm=I={lufs}:TP=-1.5:LRA=11");
        var format = FileTypeSniffer.Detect(run.Current);
        if (kind == MediaKind.Video)
        {
            if (await ProbeVideoAsync(run.Current, cancellationToken).ConfigureAwait(false) is { HasAudio: false })
            {
                run.Note("normalize: no audio track");
                return;
            }

            string[] audio = format == FileFormat.WebM ? ["-c:a", "libopus", "-b:a", "128k"] : ["-c:a", "aac", "-b:a", "192k"];
            await FfmpegStepAsync(run, $"normalised to {lufs} LUFS", ["-c:v", "copy", "-af", filter, .. audio], null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var sourceKbps = (await AudioInfo.ProbeAsync(Tools.Require(Tool.Ffprobe), run.Current, cancellationToken).ConfigureAwait(false))
            ?.BitrateKbps;
        string[] codec = AudioEncoder(format) is { } encoder
            ? ["-c:a", encoder, "-b:a", $"{Math.Clamp(sourceKbps ?? 192, 64, 320)}k"]
            : format switch
            {
                FileFormat.Flac => ["-c:a", "flac"],
                _ => ["-c:a", "pcm_s16le"],
            };
        await FfmpegStepAsync(run, $"normalised to {lufs} LUFS", ["-map", "0", "-c:v", "copy", "-af", filter, "-ar", "48000", .. codec], null,
            cancellationToken).ConfigureAwait(false);
    }

    private static string? AudioEncoder(FileFormat format) => format switch
    {
        FileFormat.Mp3 => "libmp3lame",
        FileFormat.M4a or FileFormat.Aac => "aac",
        FileFormat.Ogg => "libopus",
        _ => null,
    };

    private static string[] VideoCodec(string path) => FileTypeSniffer.Detect(path) == FileFormat.WebM
        ? ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1"]
        : ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"];

    /// <summary>Keeps the container when re-encoding, except ones that don't suit H.264 (AVI, MPEG), which become MP4.</summary>
    private static string VideoOutputExtension(string path) => FileTypeSniffer.Detect(path) switch
    {
        FileFormat.WebM => ".webm",
        FileFormat.Mov => ".mov",
        FileFormat.Mkv => ".mkv",
        _ => ".mp4",
    };

    private Task<VideoInfo?> ProbeVideoAsync(string path, CancellationToken cancellationToken) =>
        VideoInfo.ProbeAsync(Tools.Require(Tool.Ffprobe), path, cancellationToken);

    private async Task FfmpegStepAsync(
        Run run, string what, IReadOnlyList<string> arguments, string? extension, CancellationToken cancellationToken)
    {
        var output = TempPath(extension ?? Path.GetExtension(run.Current));
        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", run.Current };
        args.AddRange(arguments);
        if (Path.GetExtension(output) is ".mp4" or ".mov")
            args.AddRange(["-movflags", "+faststart"]);
        args.Add(output);

        var result = await ProcessRunner.RunAsync(Tools.Require(Tool.Ffmpeg), args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            File.Delete(output);
            throw new ToolFailedException(result);
        }

        Commit(run, output, what);
    }

    private string TempPath(string extension)
    {
        Directory.CreateDirectory(Paths.ForFilters);
        return AppPaths.NewTempPath(Paths.ForFilters, extension);
    }

    // File operations

    /// <summary>Returns true if the file was deleted.</summary>
    private bool FileOperation(CompiledStep step, Run run)
    {
        var current = run.Current;
        switch (step.Name)
        {
            case "copy":
            case "move":
            {
                var destination = Destination(run.Expand(step.Get<string>("to")!), current);
                if (string.Equals(destination, current, StringComparison.OrdinalIgnoreCase))
                {
                    run.Note($"{step.Name}: already there");
                    return false;
                }

                if (File.Exists(destination) && !step.Get<bool>("overwrite"))
                    destination = UniquePath(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                service.NoteWrite(destination);
                service.NoteWrite(current);
                if (step.Name == "copy")
                {
                    File.Copy(current, destination, overwrite: true);
                }
                else
                {
                    FilePlacer.MoveWithRetry(current, destination);
                    service.Markers.Clear(current);
                    run.Current = destination;
                    run.Changed = true;
                }

                // So a watched destination folder doesn't optimise it again.
                service.Markers.Set(destination, MarkerStatus.Optimised);
                run.Note($"{step.Name}: {destination}");
                return false;
            }

            case "rename":
            {
                var template = run.Expand(step.Get<string>("to")!);
                if (template.IndexOfAny(['/', '\\']) >= 0)
                    throw new PipelineException("rename takes a name; use move to change folders");
                var destination = Destination(template, current);
                if (string.Equals(destination, current, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (File.Exists(destination))
                    destination = UniquePath(destination);
                service.NoteWrite(destination);
                service.NoteWrite(current);
                FilePlacer.MoveWithRetry(current, destination);
                service.Markers.Clear(current);
                service.Markers.Set(destination, MarkerStatus.Optimised);
                run.Current = destination;
                run.Changed = true;
                run.Note($"renamed to {Path.GetFileName(destination)}");
                return false;
            }

            case "delete":
                service.NoteWrite(current);
                MoveToRecycleBin(current);
                run.Note("sent to the Recycle Bin");
                return true;

            default:
                throw new PipelineException($"{step.Name} isn't implemented yet");
        }
    }

    /// <summary>
    /// Where a copy / move / rename goes: <c>~</c> is the user folder; name tokens and environment variables are
    /// expanded; a destination ending in a separator (or an existing folder) keeps the file name; a name without a
    /// known extension gets the file's.
    /// </summary>
    internal static string Destination(string template, string current, DateTime? now = null)
    {
        if (template == "~" || template.StartsWith("~/", StringComparison.Ordinal) || template.StartsWith("~\\", StringComparison.Ordinal))
            template = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + template[1..];

        var isFolder = template.EndsWith('/') || template.EndsWith('\\');
        var rendered = NameTemplate.Render(template, current, now ?? DateTime.Now);
        if (isFolder || Directory.Exists(rendered))
            return Path.Combine(rendered, Path.GetFileName(current));
        return FileFormats.FromExtension(rendered) == FileFormat.Unknown ? rendered + Path.GetExtension(current) : rendered;
    }

    private static string UniquePath(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    // Actions

    private async Task ActionAsync(CompiledStep step, Run run, MediaKind kind, CancellationToken cancellationToken)
    {
        switch (step.Name)
        {
            case "runScript":
                await RunScriptAsync(step, run, kind, cancellationToken).ConfigureAwait(false);
                break;

            case "copyToClipboard":
                var mode = step.Get<string>("as") ?? "file";
                if (run.Context.CopyToClipboard is { } copy)
                {
                    await copy(run.Current, mode).ConfigureAwait(false);
                    run.Note($"copied to the clipboard as {mode}");
                }
                else
                {
                    run.Note("copyToClipboard: no clipboard here (run it from the app)");
                }

                break;

            case "extractPagesAsImages":
                await ExtractPagesAsync(step, run, cancellationToken).ConfigureAwait(false);
                break;

            case "fork":
                await ForkAsync(step, run, cancellationToken).ConfigureAwait(false);
                break;

            case "openWith":
                var start = step.Get<string>("app") is { } app
                    ? new ProcessStartInfo(run.Expand(app), $"\"{run.Current}\"") { UseShellExecute = true }
                    : new ProcessStartInfo(run.Current) { UseShellExecute = true };
                try
                {
                    Process.Start(start)?.Dispose();
                    run.Note($"opened with {step.Get<string>("app") ?? "the default app"}");
                }
                catch (System.ComponentModel.Win32Exception e)
                {
                    throw new PipelineException($"Couldn't open it with {start.FileName}: {e.Message}");
                }

                break;

            default:
                throw new PipelineException($"{step.Name} isn't implemented yet");
        }
    }

    private async Task ExtractPagesAsync(CompiledStep step, Run run, CancellationToken cancellationToken)
    {
        var png = step.Get<string>("format") == "png";
        var (presetDpi, jpegQuality) = (step.Get<string>("quality") ?? "medium") switch
        {
            "low" => (100, 75),
            "high" => (220, 92),
            _ => (150, 85),
        };
        var dpi = step.Has("dpi") ? step.Get<int>("dpi") : presetDpi;
        var stem = Path.GetFileNameWithoutExtension(run.Current);
        // A "to" is a folder: Destination() of "<folder>/" gives "<folder>/<pdf name>", whose folder is the one wanted.
        var folder = step.Get<string>("to") is { } to
            ? Path.GetDirectoryName(Destination(run.Expand(to).TrimEnd('/', '\\') + "/", run.Current))!
            : Path.Combine(Path.GetDirectoryName(run.Current)!, $"{stem} pages");

        // Rendered into the working folder first, then moved, so a watched destination never sees half-written pages.
        var scratch = Path.Combine(Paths.ForFilters, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var extension = png ? ".png" : ".jpg";
            List<string> args = ["-dNOPAUSE", "-dBATCH", "-dSAFER", "-dQUIET", $"-r{dpi}", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4"];
            args.AddRange(png ? ["-sDEVICE=png16m"] : ["-sDEVICE=jpeg", $"-dJPEGQ={jpegQuality}"]);
            args.Add($"-sOutputFile={Path.Combine(scratch, "page-%03d" + extension)}");
            args.Add(run.Current);
            var result = await ProcessRunner.RunAsync(Tools.Require(Tool.Ghostscript), args, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var pages = Directory.GetFiles(scratch).Order(StringComparer.Ordinal).ToList();
            if (!result.Succeeded || pages.Count == 0)
                throw new PipelineException($"Ghostscript couldn't render the pages: {result.StdErr.Trim()}");

            Directory.CreateDirectory(folder);
            var moved = 0;
            foreach (var page in pages)
            {
                var target = Path.Combine(folder, $"{stem} {Path.GetFileNameWithoutExtension(page)}{extension}");
                if (File.Exists(target))
                    target = UniquePath(target);
                service.NoteWrite(target);
                if (png)
                {
                    // pngquant makes rendered pages far smaller; keep the original render if it can't.
                    try
                    {
                        var optimised = await service.OptimiseAsync(page, new FileOptimisationRequest { Force = true, Behaviour = OutputBehaviour.InPlace },
                            null, cancellationToken).ConfigureAwait(false);
                        FilePlacer.MoveWithRetry(optimised.OutputPath, target);
                    }
                    catch (OptimisationException)
                    {
                        FilePlacer.MoveWithRetry(page, target);
                    }
                }
                else
                {
                    FilePlacer.MoveWithRetry(page, target);
                }

                service.Markers.Set(target, MarkerStatus.Optimised);
                moved++;
            }

            run.Note($"{moved} page{(moved == 1 ? "" : "s")} saved as {(png ? "PNG" : "JPEG")} at {dpi} DPI in {folder}");
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Runs steps (or a saved pipeline) on a copy of the file; the main pipeline carries on with the file itself.</summary>
    private async Task ForkAsync(CompiledStep step, Run run, CancellationToken cancellationToken)
    {
        if (run.Depth >= MaxForkDepth)
            throw new PipelineException("Too many forks inside forks (does a pipeline fork itself?)");

        var steps = run.Expand(step.Get<string>("steps")!);
        var (name, text) = settings.Pipelines.Find(steps) is { } saved ? (saved.Name, saved.Script) : ("fork", steps);
        if (!PipelineCatalog.TryCompile(text, out var forked, out var error))
            throw new PipelineException($"fork: {error}");

        // Same file name in a folder of its own, so a later rename or move in the fork gives the names you'd expect.
        var folder = Path.Combine(Paths.ForFilters, "fork-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(run.Current));
        File.Copy(run.Current, copy);
        var start = await service.DescribeAsync(copy, cancellationToken).ConfigureAwait(false);

        var outcome = await RunAsync(name, forked!, start, run.Context, new Dictionary<string, string>(run.Captures, StringComparer.OrdinalIgnoreCase),
            run.Depth + 1, cancellationToken).ConfigureAwait(false);
        var where = outcome.Deleted ? "deleted"
            : WatchFilters.IsInside(outcome.Result.OutputPath, Paths.WorkDir) ? $"{outcome.Result.OutputPath} (still in WClop's working folder; end a fork with move or copy)"
            : outcome.Result.OutputPath;
        run.Note($"fork ({string.Join("; ", outcome.Log)}) → {where}");
    }

    private async Task RunScriptAsync(CompiledStep step, Run run, MediaKind kind, CancellationToken cancellationToken)
    {
        if (!run.Context.AllowScripts)
            throw new PipelineException("Scripts aren't allowed here");

        var input = run.Current;
        (string File, List<string> Args) command;
        if (step.Get<string>("code") is { } code)
        {
            code = run.Expand(code);
            command = (step.Get<string>("shell") ?? "powershell") switch
            {
                "cmd" => ("cmd.exe", ["/d", "/c", code]),
                var shell => (shell == "pwsh" ? "pwsh.exe" : "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", code]),
            };
        }
        else
        {
            var script = Destination(run.Expand(step.Get<string>("path")!), input) is var resolved && File.Exists(resolved)
                ? resolved
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(run.Expand(step.Get<string>("path")!)));
            if (!File.Exists(script))
                throw new PipelineException($"Script not found: {script}");
            command = Path.GetExtension(script).ToLowerInvariant() switch
            {
                ".ps1" => ("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, input]),
                ".bat" or ".cmd" => ("cmd.exe", ["/d", "/c", script, input]),
                ".py" => ("python", [script, input]),
                ".js" => ("node", [script, input]),
                _ => (script, [input]),
            };
        }

        var before = (File.GetLastWriteTimeUtc(input), new FileInfo(input).Length);
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(command.File, command.Args, new ProcessRunOptions
            {
                WorkingDirectory = Path.GetDirectoryName(input),
                Environment = new Dictionary<string, string> { ["WCLOP_INPUT_FILE"] = input, ["CLOP_INPUT_FILE"] = input },
                Timeout = ScriptTimeout,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new PipelineException($"Couldn't start {command.File}: {e.Message}");
        }

        if (!result.Succeeded)
        {
            Directory.CreateDirectory(Paths.ProcessLogs);
            var log = Path.Combine(Paths.ProcessLogs, $"script-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            await File.WriteAllTextAsync(log,
                $"{command.File} {string.Join(' ', command.Args)}\nexit code {result.ExitCode}\n\n--- stdout\n{result.StdOut}\n\n--- stderr\n{result.StdErr}\n",
                CancellationToken.None).ConfigureAwait(false);
            throw new PipelineException($"The script failed (exit code {result.ExitCode}); details in {log}");
        }

        // A script that prints a file of the same kind hands that file to the next steps.
        var printed = result.StdOut.Split('\n').Select(l => l.Trim().Trim('"')).LastOrDefault(l => l.Length > 0);
        if (printed is not null && Path.IsPathFullyQualified(printed) && File.Exists(printed)
            && FileTypeSniffer.Detect(printed).Kind() == kind
            && !string.Equals(printed, input, StringComparison.OrdinalIgnoreCase))
        {
            service.Markers.Set(printed, MarkerStatus.Optimised);
            run.Current = printed;
            run.Changed = true;
            run.Note($"script handed over {printed}");
        }
        else
        {
            // Scripts may also edit the file in place.
            var edited = File.Exists(input) && (File.GetLastWriteTimeUtc(input), new FileInfo(input).Length) != before;
            run.Changed |= edited;
            run.Note(edited ? "script edited the file" : "script ran");
        }
    }

    private static void MoveToRecycleBin(string path)
    {
        var operation = new ShFileOpStruct
        {
            wFunc = 3, // FO_DELETE
            pFrom = path + "\0\0",
            fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        var error = SHFileOperation(ref operation);
        if (error != 0 || File.Exists(path))
            throw new PipelineException($"Couldn't move {Path.GetFileName(path)} to the Recycle Bin (error {error})");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);
}
