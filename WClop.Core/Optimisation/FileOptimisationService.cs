using System.Globalization;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Placement;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Video;
using WClop.Core.Pdf;
using WClop.Core.Audio;
using WClop.Core.Watching;

namespace WClop.Core.Optimisation;

public sealed record FileOptimisationRequest
{
    /// <summary>Compression factor; null uses the settings (or, for an adjustment, the previous factor).</summary>
    public int? Factor { get; init; }

    /// <summary>Downscale to this fraction of the original size (0–1); null means full size (or the previous scale).</summary>
    public double? Scale { get; init; }

    /// <summary>Video or audio playback speed (2 = twice as fast); null means unchanged (or the previous speed).</summary>
    public double? Speed { get; init; }

    /// <summary>Video, with <see cref="Speed"/>: stay at the source frame rate instead of keeping every frame.</summary>
    public bool DropFrames { get; init; }

    /// <summary>PDF: compress images to this DPI instead of the settings (the − key steps it down).</summary>
    public int? PdfDpi { get; init; }

    /// <summary>Make the file fit under this many bytes (project.md §12), trying harder settings until it does.</summary>
    public long? TargetBytes { get; init; }

    /// <summary>Where the output goes; null uses the settings for optimised (or converted) outputs.</summary>
    public OutputBehaviour? Behaviour { get; init; }

    public bool AllowLarger { get; init; }

    /// <summary>Optimise even if the file is marked as already optimised or restored (user-initiated jobs).</summary>
    public bool Force { get; init; }
}

public sealed record FileOptimisationResult(
    string InputPath,
    string OutputPath,
    string BackupPath,
    FileFormat InputFormat,
    FileFormat OutputFormat,
    long OldSize,
    long NewSize,
    bool FromCache,
    FileTimes? OriginalTimes = null)
{
    public double SavedFraction => OldSize == 0 ? 0 : 1 - (double)NewSize / OldSize;

    /// <summary>Fraction of the original size this output was scaled to (1 = full size).</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Video or audio playback speed of this output (1 = unchanged).</summary>
    public double Speed { get; init; } = 1;

    /// <summary>The compression factor used, so adjustments can keep it.</summary>
    public int? Factor { get; init; }

    /// <summary>A manual conversion ("Convert to…"); these can be restored but not adjusted further.</summary>
    public bool IsConversion { get; init; }

    /// <summary>PDF: the DPI images were compressed to, and the highest image DPI in the original.</summary>
    public int? Dpi { get; init; }

    public double? SourceDpi { get; init; }

    /// <summary>Audio: the output bitrate.</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>"Fit under X": the size asked for, and whether it couldn't be reached (the smallest result is kept).</summary>
    public long? TargetBytes { get; init; }

    public bool MissedTarget { get; init; }

    /// <summary>The pipeline that produced this result, if any.</summary>
    public string? Pipeline { get; init; }

    /// <summary>A pipeline asked for no result card.</summary>
    public bool HideResult { get; init; }

    /// <summary>Cropped from the original to this size ("1280×720"); like a conversion, it can be restored but not adjusted.</summary>
    public string? Crop { get; init; }

    public MediaKind Kind => InputFormat.Kind();
}

/// <summary>
/// Optimises a file on disk end to end: skip already-optimised files, back up the original,
/// reuse a cached result for identical content, optimise (and optionally downscale or speed up), place the output,
/// and mark it. Adjustments (downscale steps, aggressive, speed) always start again from the original backup
/// (project.md §24.8).
/// </summary>
public sealed partial class FileOptimisationService
{
    private readonly AppSettings _settings;
    private readonly OptimisationDatabase _database;
    private readonly OptimisationMarkers _markers;
    private readonly BackupStore _backups;
    private readonly ImageOptimiser _images;
    private readonly ImageResizer _resizer;
    private readonly VideoOptimiser _videos;
    private readonly ImageConverter _imageConverter;
    private readonly VideoConverter _videoConverter;
    private readonly PdfOptimiser _pdfs;
    private readonly AudioOptimiser _audio;
    private readonly FilePlacer _placer;
    private readonly RecentWrites? _recentWrites;

    public FileOptimisationService(
        AppSettings settings, AppPaths paths, ToolLocator tools, OptimisationDatabase database, RecentWrites? recentWrites = null)
    {
        _settings = settings;
        _database = database;
        _markers = new OptimisationMarkers(database);
        _backups = new BackupStore(paths);
        _images = new ImageOptimiser(tools, paths);
        _resizer = new ImageResizer(tools, paths);
        _videos = new VideoOptimiser(tools, paths);
        _imageConverter = new ImageConverter(tools, paths);
        _videoConverter = new VideoConverter(tools, paths);
        _pdfs = new PdfOptimiser(tools, paths);
        _audio = new AudioOptimiser(tools, paths);
        Cropper = new Cropping.MediaCropper(tools, paths);
        _recentWrites = recentWrites;
        _placer = new FilePlacer(_markers, recentWrites);
        Paths = paths;
        Tools = tools;
    }

    public AppPaths Paths { get; }
    public ToolLocator Tools { get; }

    public OptimisationMarkers Markers => _markers;

    /// <summary>Crops images and videos; shared with the <c>crop</c> pipeline step.</summary>
    public Cropping.MediaCropper Cropper { get; }

    /// <summary>Optimises an image or a video, whichever <paramref name="path"/> really is.</summary>
    public Task<FileOptimisationResult> OptimiseAsync(
        string path, FileOptimisationRequest request, Action<double>? onProgress = null, CancellationToken cancellationToken = default) =>
        FileTypeSniffer.Detect(path).Kind() switch
        {
            MediaKind.Image => RunAsync(path, request, MediaKind.Image, null, cancellationToken),
            MediaKind.Video => RunAsync(path, request, MediaKind.Video, onProgress, cancellationToken),
            MediaKind.Pdf => RunAsync(path, request, MediaKind.Pdf, onProgress, cancellationToken),
            MediaKind.Audio => RunAsync(path, request, MediaKind.Audio, onProgress, cancellationToken),
            _ => throw new UnsupportedFormatException($"{Path.GetFileName(path)} isn't an image or video WClop can optimise yet"),
        };

    public Task<FileOptimisationResult> OptimiseImageAsync(
        string path, FileOptimisationRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(path, request, MediaKind.Image, null, cancellationToken);

    public Task<FileOptimisationResult> OptimiseVideoAsync(
        string path, FileOptimisationRequest request, Action<double>? onProgress = null, CancellationToken cancellationToken = default) =>
        RunAsync(path, request, MediaKind.Video, onProgress, cancellationToken);

    private async Task<FileOptimisationResult> RunAsync(
        string path, FileOptimisationRequest request, MediaKind kind, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found", path);

        if (!request.Force && _markers.Get(path) != MarkerStatus.None)
            throw new AlreadyOptimisedException(path);

        var times = FileTimes.Of(path);
        var hash = await ContentHash.OfFileAsync(path, cancellationToken).ConfigureAwait(false);
        var backup = _backups.Backup(path, hash);
        var adjustments = Adjustments.For(kind, request, previous: null, _settings);

        _markers.Set(path, MarkerStatus.Pending);
        try
        {
            var inputFormat = FileTypeSniffer.Detect(backup);
            var produced = request.TargetBytes is { } target
                ? await FitAsync(kind, backup, target, onProgress, cancellationToken).ConfigureAwait(false)
                : await ProduceAsync(kind, backup, hash, adjustments, request.AllowLarger, onProgress, cancellationToken)
                .ConfigureAwait(false);

            var placement = kind switch
            {
                MediaKind.Video => _settings.Files.Videos,
                MediaKind.Pdf => _settings.Files.Pdfs,
                MediaKind.Audio => _settings.Files.Audio,
                _ => _settings.Files.Images,
            };
            var converted = produced.Format != inputFormat;
            var behaviour = request.Behaviour ?? (converted ? placement.AutoConverted : placement.Optimised);
            var finalPath = _placer.Place(new PlacementRequest
            {
                OutputPath = produced.Output,
                OriginalPath = path,
                Behaviour = behaviour,
                Template = behaviour == OutputBehaviour.SpecificFolder
                    ? converted ? placement.ConvertedSpecificFolderTemplate : placement.SpecificFolderTemplate
                    : converted ? placement.ConvertedSameFolderTemplate : placement.SameFolderTemplate,
                PreserveTimes = _settings.Files.PreserveDates ? times : null,
            });

            if (!string.Equals(path, finalPath, StringComparison.OrdinalIgnoreCase))
            {
                // Source of a separate output: don't pick it up again (§4.8). A temporary output leaves it untouched.
                // If it was replaced by a new format, this just drops its stale "pending" record.
                _markers.Set(path, behaviour is OutputBehaviour.SameFolder or OutputBehaviour.SpecificFolder
                    ? MarkerStatus.OriginalProcessed
                    : MarkerStatus.None);
            }

            _database.PutCachedOutput(hash, produced.Variant, finalPath);

            return new FileOptimisationResult(
                path, finalPath, backup, inputFormat, produced.Format,
                new FileInfo(backup).Length, new FileInfo(finalPath).Length, produced.FromCache, times)
            {
                Scale = adjustments.Scale,
                Speed = adjustments.Speed,
                Dpi = produced.Dpi,
                SourceDpi = produced.SourceDpi,
                BitrateKbps = produced.BitrateKbps,
                TargetBytes = request.TargetBytes,
                MissedTarget = produced.MissedTarget,
                Factor = adjustments.Factor,
            };
        }
        catch (NotSmallerException) when (adjustments.IsPlain)
        {
            // Nothing to gain: remember that so watchers don't retry it.
            _markers.Set(path, MarkerStatus.Optimised);
            throw;
        }
        catch
        {
            if (File.Exists(path))
                _markers.Clear(path);
            throw;
        }
    }

    /// <summary>
    /// Re-does a previous result from its original backup with a different factor, scale and/or speed, replacing the
    /// previous output (project.md §8.10: each step works from the original, not the previous result).
    /// A result in the working directory (clipboard) stays there; a result in a user folder is replaced in place.
    /// </summary>
    public async Task<FileOptimisationResult> AdjustAsync(
        FileOptimisationResult previous,
        FileOptimisationRequest request,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (previous.IsConversion)
            throw new UnsupportedFormatException("A converted file can't be adjusted; restore it and adjust the original instead");
        if (!File.Exists(previous.BackupPath))
            throw new FileNotFoundException("The backup of the original is gone (cleaned up?)", previous.BackupPath);

        var adjustments = Adjustments.For(previous.Kind, request, previous, _settings);
        var hash = await ContentHash.OfFileAsync(previous.BackupPath, cancellationToken).ConfigureAwait(false);
        var produced = request.TargetBytes is { } adjustTarget
            ? await FitAsync(previous.Kind, previous.BackupPath, adjustTarget, onProgress, cancellationToken).ConfigureAwait(false)
            : await ProduceAsync(previous.Kind, previous.BackupPath, hash, adjustments, request.AllowLarger, onProgress, cancellationToken)
            .ConfigureAwait(false);

        var finalPath = WatchFilters.IsInside(previous.OutputPath, Paths.WorkDir)
            ? produced.Output
            : _placer.Place(new PlacementRequest
            {
                OutputPath = produced.Output,
                OriginalPath = previous.OutputPath,
                Behaviour = OutputBehaviour.InPlace,
                PreserveTimes = _settings.Files.PreserveDates ? previous.OriginalTimes : null,
            });

        _database.PutCachedOutput(hash, produced.Variant, finalPath);
        return previous with
        {
            OutputPath = finalPath,
            OutputFormat = produced.Format,
            NewSize = new FileInfo(finalPath).Length,
            FromCache = produced.FromCache,
            Scale = adjustments.Scale,
            Speed = adjustments.Speed,
            Dpi = produced.Dpi ?? previous.Dpi,
            SourceDpi = produced.SourceDpi ?? previous.SourceDpi,
            BitrateKbps = produced.BitrateKbps ?? previous.BitrateKbps,
            TargetBytes = request.TargetBytes,
            MissedTarget = produced.MissedTarget,
            Factor = adjustments.Factor,
        };
    }

    /// <summary>
    /// Puts the original back where the result is (project.md §14.3): the file the user sees becomes the original
    /// again, under its original extension. A converted output (photo.jpg for photo.png) is removed.
    /// The restored file is marked <see cref="MarkerStatus.Original"/> so it isn't re-optimised automatically.
    /// Returns the restored path.
    /// </summary>
    public string Restore(FileOptimisationResult result)
    {
        if (!File.Exists(result.BackupPath))
            throw new FileNotFoundException("The backup of the original is gone (cleaned up?)", result.BackupPath);

        var target = Path.ChangeExtension(result.OutputPath, Path.GetExtension(result.InputPath));
        _recentWrites?.Register(target);
        _recentWrites?.Register(result.OutputPath);
        File.Copy(result.BackupPath, target, overwrite: true);

        if (!string.Equals(target, result.OutputPath, StringComparison.OrdinalIgnoreCase) && File.Exists(result.OutputPath))
        {
            File.Delete(result.OutputPath);
            _markers.Clear(result.OutputPath);
        }

        result.OriginalTimes?.ApplyTo(target);
        _markers.Set(target, MarkerStatus.Original);
        return target;
    }

    /// <summary>
    /// What a job runs with, resolved from the request, the previous result and settings: compression factor, scale
    /// and speed (images / video / audio), and PDF DPI (null = the settings' mode).
    /// </summary>
    private sealed record Adjustments(int Factor, double Scale, double Speed, int? Dpi)
    {
        /// <summary>Video: a speed change keeps the source frame rate (see <see cref="FileOptimisationRequest.DropFrames"/>).</summary>
        public bool DropFrames { get; init; }

        /// <summary>A plain optimisation (no resize, speed or DPI change), where "not smaller" means "already compressed".</summary>
        public bool IsPlain => Scale >= 1 && Math.Abs(Speed - 1) < 0.001 && Dpi is null;

        public static Adjustments For(MediaKind kind, FileOptimisationRequest request, FileOptimisationResult? previous, AppSettings settings) =>
            new(
                request.Factor ?? previous?.Factor ?? kind switch
                {
                    MediaKind.Video => settings.Compression.VideoFactor,
                    MediaKind.Audio => settings.Compression.AudioFactor,
                    _ => settings.Compression.ImageFactor,
                },
                kind is MediaKind.Image or MediaKind.Video ? request.Scale ?? previous?.Scale ?? 1 : 1,
                kind is MediaKind.Video or MediaKind.Audio ? request.Speed ?? previous?.Speed ?? 1 : 1,
                kind == MediaKind.Pdf ? request.PdfDpi ?? previous?.Dpi : null)
            {
                DropFrames = request.DropFrames,
            };
    }

    private sealed record Produced(string Output, FileFormat Format, bool FromCache, string Variant)
    {
        public int? Dpi { get; init; }
        public double? SourceDpi { get; init; }
        public int? BitrateKbps { get; init; }
        public bool MissedTarget { get; init; }
    }

    private async Task<Produced> ProduceAsync(
        MediaKind kind, string original, string hash, Adjustments adjustments, bool allowLarger,
        Action<double>? onProgress, CancellationToken cancellationToken)
    {
        var variant = kind switch
        {
            MediaKind.Video => VideoVariant(adjustments),
            MediaKind.Pdf => PdfVariant(adjustments),
            MediaKind.Audio => string.Create(CultureInfo.InvariantCulture,
                $"audio:f{adjustments.Factor}:x{adjustments.Speed:0.##}:cover{_settings.Compression.AudioCoverArt}"),
            _ => ImageVariant(adjustments, AutoConversionTarget(original)),
        };
        // PDF and audio results carry details (DPI, bitrate) that a cached copy wouldn't, so only images and video reuse.
        if (kind is MediaKind.Image or MediaKind.Video && _database.GetCachedOutput(hash, variant) is { } cached)
        {
            var format = FileTypeSniffer.Detect(cached);
            var copy = AppPaths.NewTempPath(kind == MediaKind.Video ? Paths.Videos : Paths.Images, format.Extension());
            File.Copy(cached, copy);
            return new Produced(copy, format, true, variant);
        }

        switch (kind)
        {
            case MediaKind.Video:
                return await ProduceVideoAsync(original, adjustments, allowLarger, variant, onProgress, cancellationToken).ConfigureAwait(false);

            case MediaKind.Pdf:
                var compression = _settings.Compression;
                var fixedDpi = adjustments.Dpi ?? (compression.PdfDpiMode == PdfDpiMode.Fixed ? compression.PdfFixedDpi : null);
                var pdf = await _pdfs.OptimiseAsync(original, fixedDpi, compression.PdfFixedDpi, allowLarger, onProgress, cancellationToken)
                    .ConfigureAwait(false);
                return new Produced(pdf.Path, FileFormat.Pdf, false, variant) { Dpi = pdf.Dpi, SourceDpi = pdf.SourceMaxDpi };

            case MediaKind.Audio:
                var audio = await _audio.OptimiseAsync(original, adjustments.Factor, allowLarger, onProgress, cancellationToken,
                    speed: adjustments.Speed, coverArt: _settings.Compression.AudioCoverArt).ConfigureAwait(false);
                return new Produced(audio.Path, audio.Format, false, variant) { BitrateKbps = audio.BitrateKbps };

            default:
                return await ProduceImageAsync(original, adjustments, allowLarger, variant, AutoConversionTarget(original), cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private string PdfVariant(Adjustments a)
    {
        var c = _settings.Compression;
        return string.Create(CultureInfo.InvariantCulture, $"pdf:{a.Dpi?.ToString(CultureInfo.InvariantCulture) ?? c.PdfDpiMode.ToString()}:{c.PdfFixedDpi}");
    }


    private string VideoVariant(Adjustments a)
    {
        var c = _settings.Compression;
        return string.Create(CultureInfo.InvariantCulture,
            $"video:f{a.Factor}:t{c.VideoTier}:s{a.Scale:0.##}:x{a.Speed:0.##}{(a.DropFrames ? "d" : "")}:fps{(c.CapVideoFps ? c.VideoFpsTarget : 0)}:noaudio{(c.RemoveAudioFromVideos ? 1 : 0)}");
    }

    private async Task<Produced> ProduceVideoAsync(
        string original, Adjustments adjustments, bool allowLarger, string variant, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        var compression = _settings.Compression;
        var output = await _videos.OptimiseAsync(original, new VideoOptimiseOptions
        {
            Factor = adjustments.Factor,
            Tier = compression.VideoTier,
            Scale = adjustments.Scale,
            Speed = adjustments.Speed,
            DropFrames = adjustments.DropFrames,
            FpsCap = compression.CapVideoFps ? compression.VideoFpsTarget : null,
            RemoveAudio = compression.RemoveAudioFromVideos,
            AllowLarger = allowLarger,
        }, onProgress, cancellationToken).ConfigureAwait(false);
        return new Produced(output.Path, FileFormat.Mp4, false, variant);
    }

    private string ImageVariant(Adjustments a, FileFormat? convertTo) => string.Create(CultureInfo.InvariantCulture,
        $"image:f{a.Factor}:s{a.Scale:0.##}:to{convertTo}:adaptive{(_settings.Compression.AdaptiveImageFormat ? 1 : 0)}" +
        $":strip{(_settings.Files.StripMetadata ? 1 : 0)}:color{(_settings.Files.PreserveColorMetadata ? 1 : 0)}");

    /// <summary>
    /// The format an image is automatically converted to before optimising (§8.7): by default WebP, AVIF, HEIC and
    /// BMP → JPEG and TIFF → PNG. Animated images are never converted to a still format.
    /// </summary>
    public FileFormat? AutoConversionTarget(string path)
    {
        var format = FileTypeSniffer.Detect(path);
        if (format.Kind() != MediaKind.Image || ImageConverter.IsAnimated(path, format))
            return null;

        static bool Lists(IEnumerable<string> extensions, FileFormat format) =>
            extensions.Any(e => FileFormats.FromExtension("x." + e.TrimStart('.')) == format);

        var compression = _settings.Compression;
        if (Lists(compression.ConvertToJpeg, format))
            return FileFormat.Jpeg;
        if (Lists(compression.ConvertToPng, format))
            return FileFormat.Png;
        return null;
    }

    /// <summary>
    /// (Converted,) (downscaled and) optimised image. A converted image may end up bigger than the original
    /// (HEIC to JPEG usually does; compatibility is the point). When downscaling, the result only has to beat the
    /// original's size, not the resized intermediate's (§8.9).
    /// </summary>
    private async Task<Produced> ProduceImageAsync(
        string original, Adjustments adjustments, bool allowLarger, string variant, FileFormat? convertTo, CancellationToken cancellationToken)
    {
        var options = new ImageOptimiseOptions
        {
            Factor = adjustments.Factor,
            AllowLarger = allowLarger || convertTo is not null,
            StripMetadata = _settings.Files.StripMetadata,
            PreserveColorMetadata = _settings.Files.PreserveColorMetadata,
            Adaptive = _settings.Compression.AdaptiveImageFormat,
            MetadataSource = original,
        };

        string? converted = null;
        var source = original;
        if (convertTo is { } target)
        {
            converted = await _imageConverter.ConvertAsync(original, target, adjustments.Factor, cancellationToken).ConfigureAwait(false);
            source = converted;
        }

        try
        {
            if (adjustments.Scale >= 1)
            {
                var optimised = await _images.OptimiseAsync(source, options, cancellationToken).ConfigureAwait(false);
                return new Produced(optimised.Path, optimised.OutputFormat, false, variant);
            }

            var (resized, _) = await _resizer.DownscaleAsync(source, adjustments.Scale, cancellationToken).ConfigureAwait(false);
            try
            {
                var optimised = await _images.OptimiseAsync(resized, options with { AllowLarger = true }, cancellationToken)
                    .ConfigureAwait(false);

                var originalSize = new FileInfo(original).Length;
                if (options.AllowLarger || optimised.OutputSize < originalSize)
                    return new Produced(optimised.Path, optimised.OutputFormat, false, variant);

                File.Delete(optimised.Path);
                if (await RequantiseAsync(original, resized, options, originalSize, cancellationToken).ConfigureAwait(false) is { } requantised)
                    return new Produced(requantised.Path, requantised.OutputFormat, false, variant);

                throw new NotSmallerException(originalSize, optimised.OutputSize);
            }
            finally
            {
                File.Delete(resized);
            }
        }
        finally
        {
            if (converted is not null)
                File.Delete(converted);
        }
    }

    /// <summary>
    /// Downscale-grows-file guard (§8.9): smooth resampling turns a flat, low-colour PNG's few crisp colours into many
    /// anti-aliased ones, so the smaller image can be bigger. Re-quantise it to the original's colour count, falling
    /// back to 128, 64, 32 and 16 colours, and take the first result smaller than the original.
    /// </summary>
    private async Task<ImageOptimiseOutput?> RequantiseAsync(
        string original, string resized, ImageOptimiseOptions options, long originalSize, CancellationToken cancellationToken)
    {
        if (FileTypeSniffer.Detect(original) != FileFormat.Png)
            return null;

        var colors = ImageAnalysis.CountColors(await Task.Run(() => ImageAnalysis.Load(original), cancellationToken).ConfigureAwait(false));
        if (colors > 256)
            return null;

        foreach (var palette in new[] { colors, 128, 64, 32, 16 }.Where(c => c <= colors).Select(c => Math.Max(2, c)).Distinct())
        {
            var attempt = await _images.OptimiseAsync(
                resized, options with { AllowLarger = true, Adaptive = false, MaxColors = palette }, cancellationToken).ConfigureAwait(false);
            if (attempt.OutputSize < originalSize)
                return attempt;
            File.Delete(attempt.Path);
        }

        return null;
    }

    /// <summary>
    /// A result describing a file as it is (backed up, unchanged), so it can be converted or restored like any result.
    /// Used for files that were "already fully compressed".
    /// </summary>
    public async Task<FileOptimisationResult> DescribeAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var hash = await ContentHash.OfFileAsync(path, cancellationToken).ConfigureAwait(false);
        var backup = _backups.Backup(path, hash);
        var format = FileTypeSniffer.Detect(path);
        var size = new FileInfo(path).Length;
        return new FileOptimisationResult(path, path, backup, format, format, size, size, false, FileTimes.Of(path));
    }

    /// <summary>
    /// "Convert to…" (§8.8, §9.4): converts a result from its original backup, so quality isn't lost twice.
    /// Images: JPEG, PNG, GIF (optimised afterwards), WebP, AVIF. Videos: GIF (optimised afterwards), WebM, HEVC MP4.
    /// Audio: MP3, AAC (M4A), Opus (Ogg).
    /// The converted file is saved next to the result (or stays in the working folder for clipboard results);
    /// restoring it removes the conversion and puts the original back.
    /// </summary>
    public async Task<FileOptimisationResult> ConvertAsync(
        FileOptimisationResult previous, FileFormat target, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(previous.BackupPath))
            throw new FileNotFoundException("The backup of the original is gone (cleaned up?)", previous.BackupPath);

        var kind = previous.Kind;
        var output = await ConvertFileAsync(previous.BackupPath, target, onProgress, cancellationToken).ConfigureAwait(false);

        string finalPath;
        if (WatchFilters.IsInside(previous.OutputPath, Paths.WorkDir))
        {
            finalPath = output;
        }
        else
        {
            var placement = kind switch
            {
                MediaKind.Video => _settings.Files.Videos,
                MediaKind.Audio => _settings.Files.Audio,
                _ => _settings.Files.Images,
            };
            var behaviour = placement.ManuallyConverted;
            // Converting clip.mp4 to HEVC would otherwise be named clip.mp4 again, replacing the source.
            var sameFolderTemplate = FileFormats.FromExtension(previous.OutputPath) == target
                ? "%f-" + (target == FileFormat.Mp4 ? "hevc" : target.Extension().TrimStart('.'))
                : placement.ConvertedSameFolderTemplate;
            finalPath = _placer.Place(new PlacementRequest
            {
                OutputPath = output,
                OriginalPath = previous.OutputPath,
                Behaviour = behaviour == OutputBehaviour.InPlace ? OutputBehaviour.SameFolder : behaviour,
                Template = behaviour == OutputBehaviour.SpecificFolder ? placement.ConvertedSpecificFolderTemplate : sameFolderTemplate,
                PreserveTimes = _settings.Files.PreserveDates ? previous.OriginalTimes : null,
            });
        }

        return previous with
        {
            OutputPath = finalPath,
            OutputFormat = target,
            NewSize = new FileInfo(finalPath).Length,
            FromCache = false,
            Scale = 1,
            Speed = 1,
            IsConversion = true,
            Crop = null,
        };
    }

    /// <summary>
    /// Converts <paramref name="input"/> into a new file in the working directory (JPEG, PNG and GIF outputs are
    /// optimised afterwards). The caller places it.
    /// </summary>
    public async Task<string> ConvertFileAsync(
        string input, FileFormat target, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var kind = FileTypeSniffer.Detect(input).Kind();
        var factor = kind == MediaKind.Video ? _settings.Compression.VideoFactor : _settings.Compression.ImageFactor;
        var converted = kind switch
        {
            MediaKind.Image => await _imageConverter.ConvertAsync(input, target, factor, cancellationToken).ConfigureAwait(false),
            MediaKind.Video => await _videoConverter.ConvertAsync(
                input, target, factor, _settings.Compression.VideoTier, onProgress, cancellationToken).ConfigureAwait(false),
            MediaKind.Audio => await ConvertAudioAsync(input, target, onProgress, cancellationToken).ConfigureAwait(false),
            _ => throw new UnsupportedFormatException("Only images, videos and audio can be converted"),
        };

        if (target is not (FileFormat.Jpeg or FileFormat.Png or FileFormat.Gif))
            return converted;

        try
        {
            var optimised = await _images.OptimiseAsync(converted, new ImageOptimiseOptions
            {
                Factor = _settings.Compression.ImageFactor,
                AllowLarger = true,
                StripMetadata = _settings.Files.StripMetadata,
                PreserveColorMetadata = _settings.Files.PreserveColorMetadata,
                MetadataSource = kind == MediaKind.Image ? input : null,
            }, cancellationToken).ConfigureAwait(false);
            return optimised.Path;
        }
        finally
        {
            File.Delete(converted);
        }
    }

    /// <summary>
    /// Replaces <paramref name="current"/> with <paramref name="produced"/> (moved), taking the produced file's
    /// extension if the format changed. <paramref name="current"/> must already be backed up. Used by pipelines.
    /// </summary>
    public string ReplaceInPlace(string produced, string current, FileTimes? times = null) =>
        _placer.Place(new PlacementRequest
        {
            OutputPath = produced,
            OriginalPath = current,
            Behaviour = OutputBehaviour.InPlace,
            PreserveTimes = _settings.Files.PreserveDates ? times : null,
        });

    /// <summary>Tells the folder watchers that WClop itself is about to write <paramref name="path"/>.</summary>
    public void NoteWrite(string path) => _recentWrites?.Register(path);
}
