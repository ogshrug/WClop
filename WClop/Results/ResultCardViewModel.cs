using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WClop.Core.Audio;
using WClop.Core.Cropping;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Video;

namespace WClop.Results
{
    /// <summary>One floating result card (project.md §16.1), or one row of the compact list (§16.2).</summary>
    internal sealed class ResultCardViewModel : INotifyPropertyChanged
    {
        private const int ThumbnailPixels = 144;

        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _autoHide;
        private ImageSource? _thumbnail;
        private string? _dimensions;
        private bool _isHovered;
        private string? _loadedThumbnailPath;
        private string? _codec;
        private ImageSize? _pixelSize;
        private IReadOnlyList<FormatChoice> _formatChoices = [];
        private bool _isSelected;
        private bool _isRenaming;
        private string _renameText = "";
        private bool _isCropOpen;
        private bool _cropSmart;
        private string _cropWidthText = "";
        private string _cropHeightText = "";

        public ResultCardViewModel(OptimisationJob job, Dispatcher dispatcher, TimeSpan autoHideAfter, Action<ResultCardViewModel> dismiss)
        {
            Job = job;
            _dispatcher = dispatcher;
            _autoHide = new DispatcherTimer(autoHideAfter, DispatcherPriority.Background, (_, _) => dismiss(this), dispatcher);
            _autoHide.Stop();
            job.PropertyChanged += OnJobChanged;
            LoadThumbnail(job.CurrentPath);
            UpdateFormatChoices();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public OptimisationJob Job { get; }

        /// <summary>Set once the card has been added to the window (it waits briefly so skipped jobs never flash up).</summary>
        public bool IsShown { get; set; }

        public bool AutoHideEnabled { get; set; } = true;

        public string Title => Job.DisplayName;
        public string Status => Job.Status;
        public bool IsRunning => Job.State == JobState.Running;

        /// <summary>Videos report real progress; everything else shows an indeterminate bar.</summary>
        public bool IsProgressIndeterminate => Job.Progress is null or <= 0;

        public double ProgressPercent => (Job.Progress ?? 0) * 100;
        public bool IsFailed => Job.State == JobState.Failed;
        public bool IsSucceeded => Job.State == JobState.Succeeded;
        public bool CanRestore => Job.State == JobState.Succeeded && Job.Result is not null;
        public bool CanShowFile => Job.CurrentPath is { } path && File.Exists(path);

        public bool CanConvert => ResultActions.CanConvert(Job);

        public bool CanFit => ResultActions.FitTargets(Job).Count > 0;

        /// <summary>Set when the card is made: whether there are saved pipelines at all (shows the button).</summary>
        public bool HasPipelines { get; init; }

        /// <summary>The pipelines that fit this result.</summary>
        public Func<OptimisationJob, IReadOnlyList<string>>? PipelinesFor { get; init; }

        public bool CanRunPipeline => HasPipelines && ResultActions.CanRunPipeline(Job) && PipelinesFor?.Invoke(Job).Count > 0;

        // Clop 3 card actions

        /// <summary>Set when the card is made (Settings → Results): the format bar instead of the Convert button.</summary>
        public bool ShowFormatBar { get; init; } = true;

        /// <summary>Without the format bar, conversions stay one button (and the right-click menu) away.</summary>
        public bool ShowConvertButton => !ShowFormatBar;

        public IReadOnlyList<FormatChoice> FormatChoices => _formatChoices;

        public bool HasFormatBar => ShowFormatBar && _formatChoices.Count > 0;

        public MediaKind Kind => ResultActions.KindOf(Job);

        public bool CanDownscale => Kind is MediaKind.Image or MediaKind.Video && ResultActions.CanAdjust(Job);

        /// <summary>The compression slider: a factor for images, video and audio, the DPI for PDFs.</summary>
        public bool CanCompress => Kind == MediaKind.Pdf
            ? Job.IsFinished && Job.Result is { IsConversion: false, BackupPath: var backup } && File.Exists(backup)
            : ResultActions.CanAdjust(Job);

        public bool CanCrop => ResultActions.CanCrop(Job);
        public bool CanShare => Job.IsFinished && CanShowFile;
        public bool CanEdit => Job.IsFinished && CanShowFile;
        public bool CanRename => Job.IsFinished && CanShowFile;

        /// <summary>The second line of a compact-list row.</summary>
        public string CompactDetail => SizeText ?? Status;

        /// <summary>The codec ffprobe reports for a video or audio file (e.g. "h264"), once known.</summary>
        public string? Codec => _codec;

        /// <summary>The file's pixel size, once read (images and videos).</summary>
        public ImageSize? PixelSize => _pixelSize;

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                if (Set(ref _isRenaming, value))
                    RestartAutoHide();
            }
        }

        public string RenameText
        {
            get => _renameText;
            set => Set(ref _renameText, value);
        }

        public bool IsCropOpen
        {
            get => _isCropOpen;
            set
            {
                if (!Set(ref _isCropOpen, value))
                    return;
                if (value && _pixelSize is { } size)
                {
                    CropWidthText = size.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    CropHeightText = size.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                RestartAutoHide();
            }
        }

        public static IReadOnlyList<string> CropPresets => CropSpec.AspectPresets;

        public bool CropSmart
        {
            get => _cropSmart;
            set => Set(ref _cropSmart, value);
        }

        public string CropWidthText
        {
            get => _cropWidthText;
            set => Set(ref _cropWidthText, value);
        }

        public string CropHeightText
        {
            get => _cropHeightText;
            set => Set(ref _cropHeightText, value);
        }

        public string? SizeText => Job.Result is { } r && Job.State == JobState.Succeeded
            ? $"{FormatBytes(r.OldSize)} → {FormatBytes(r.NewSize)}  (−{r.SavedFraction:P0})"
            : null;

        public bool HasSizeText => SizeText is not null;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            private set => Set(ref _thumbnail, value);
        }

        public string? Dimensions
        {
            get => _dimensions;
            private set => Set(ref _dimensions, value, nameof(HasDimensions));
        }

        public bool HasDimensions => Dimensions is not null;

        /// <summary>Hovering pauses auto-hide, so a result doesn't vanish while you're reading or dragging it.</summary>
        public bool IsHovered
        {
            get => _isHovered;
            set
            {
                if (!Set(ref _isHovered, value))
                    return;
                if (value)
                    _autoHide.Stop();
                else
                    RestartAutoHide();
            }
        }

        public void RestartAutoHide()
        {
            _autoHide.Stop();
            // Not while you're typing a name or choosing a crop either.
            if (AutoHideEnabled && Job.IsFinished && !IsHovered && !IsRenaming && !IsCropOpen)
                _autoHide.Start();
        }

        public void Detach()
        {
            _autoHide.Stop();
            Job.PropertyChanged -= OnJobChanged;
        }

        private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Job events arrive on worker threads; WPF marshals scalar bindings, the timer needs the UI thread.
            Notify(nameof(Title));
            Notify(nameof(Status));
            Notify(nameof(IsRunning));
            Notify(nameof(IsProgressIndeterminate));
            Notify(nameof(ProgressPercent));
            Notify(nameof(IsFailed));
            Notify(nameof(IsSucceeded));
            Notify(nameof(CanRestore));
            Notify(nameof(SizeText));
            Notify(nameof(HasSizeText));
            Notify(nameof(CompactDetail));
            Notify(nameof(CanShowFile));
            Notify(nameof(CanConvert));
            Notify(nameof(CanFit));
            Notify(nameof(CanRunPipeline));
            Notify(nameof(CanDownscale));
            Notify(nameof(CanCompress));
            Notify(nameof(CanCrop));
            Notify(nameof(CanShare));
            Notify(nameof(CanEdit));
            Notify(nameof(CanRename));

            if (e.PropertyName is nameof(OptimisationJob.State) or nameof(OptimisationJob.CurrentPath) or nameof(OptimisationJob.Result))
                UpdateFormatChoices();
            if (e.PropertyName == nameof(OptimisationJob.CurrentPath))
                LoadThumbnail(Job.CurrentPath);
            if (e.PropertyName == nameof(OptimisationJob.State))
            {
                // An in-place result has the same path as the source it was loaded from while the job ran (or while
                // a recording was still being written), so reload once the job has a final file.
                if (Job.State is JobState.Succeeded or JobState.Restored)
                    LoadThumbnail(Job.CurrentPath, force: true);
                _dispatcher.BeginInvoke(RestartAutoHide);
            }
        }

        private void UpdateFormatChoices()
        {
            var choices = ResultActions.FormatChoicesFor(Job, _codec);
            if (choices.SequenceEqual(_formatChoices))
                return;
            _formatChoices = choices;
            Notify(nameof(FormatChoices));
            Notify(nameof(HasFormatBar));
        }

        private void LoadThumbnail(string? path, bool force = false)
        {
            if (path is null || (!force && path == _loadedThumbnailPath))
                return;
            _loadedThumbnailPath = path;

            Task.Run(() =>
            {
                try
                {
                    switch (FileFormats.FromExtension(path).Kind())
                    {
                        case MediaKind.Video:
                            var (videoFrame, info) = DecodeVideoThumbnail(path);
                            Thumbnail = videoFrame;
                            SetMedia(info?.VideoCodec, info is null ? null : new ImageSize(info.Width, info.Height));
                            Dimensions = $"{info?.Width ?? 0} × {info?.Height ?? 0}" + CodecSuffix();
                            break;

                        case MediaKind.Pdf:
                            Thumbnail = DecodePdfThumbnail(path);
                            Dimensions = PdfDetail();
                            break;

                        case MediaKind.Audio:
                            // No picture; show what matters for audio instead.
                            var audio = ToolLocator.CreateDefault().Find(Tool.Ffprobe) is { } ffprobe
                                ? AudioInfo.ProbeAsync(ffprobe, path).GetAwaiter().GetResult()
                                : null;
                            SetMedia(audio?.Codec, null);
                            Dimensions = (Job.Result?.BitrateKbps ?? audio?.BitrateKbps, Core.Optimisation.FormatChoices.CodecLabel(_codec)) switch
                            {
                                ({ } kbps, { } codec) => $"{kbps} kbps · {codec}",
                                ({ } kbps, null) => $"{kbps} kbps",
                                (null, { } codec) => codec,
                                _ => null,
                            };
                            break;

                        default:
                            var (image, imageWidth, imageHeight) = DecodeThumbnail(path);
                            Thumbnail = image;
                            SetMedia(null, new ImageSize(imageWidth, imageHeight));
                            Dimensions = $"{imageWidth} × {imageHeight}";
                            break;
                    }
                }
                catch (Exception e)
                {
                    // No preview; the card still shows the text. Allow another try (e.g. once the file is complete).
                    _loadedThumbnailPath = null;
                    WClop.Core.Logging.Log.Warn($"Thumbnail for {Path.GetFileName(path)} failed: {e.GetType().Name}: {e.Message}");
                }
            });
        }

        /// <summary>The codec decides the format bar for video and audio (an HEVC MP4 isn't offered MP4 · HEVC).</summary>
        private void SetMedia(string? codec, ImageSize? size)
        {
            _pixelSize = size;
            Notify(nameof(PixelSize));
            if (codec == _codec)
                return;
            _codec = codec;
            Notify(nameof(Codec));
            UpdateFormatChoices();
        }

        private string CodecSuffix() => Core.Optimisation.FormatChoices.CodecLabel(_codec) is { } label ? " · " + label : "";

        /// <summary>"300 → 150 DPI" (or just the output DPI) for a PDF result.</summary>
        private string? PdfDetail() => Job.Result switch
        {
            { Dpi: { } dpi, SourceDpi: { } source } when source > dpi + 1 => $"{Math.Round(source):0} → {dpi} DPI",
            { Dpi: >= Core.Pdf.PdfAnalysis.LosslessDpi } => "Lossless (fonts and streams compressed)",
            { Dpi: { } dpi } => $"{dpi} DPI",
            _ => null,
        };

        /// <summary>The first page rendered small by Ghostscript.</summary>
        private static ImageSource DecodePdfThumbnail(string path)
        {
            var gs = ToolLocator.CreateDefault().Find(Tool.Ghostscript) ?? throw new IOException("Ghostscript not found");
            var png = Path.Combine(Path.GetTempPath(), "wclop-pdf-" + Guid.NewGuid().ToString("N")[..8] + ".png");
            try
            {
                var result = ProcessRunner.RunAsync(gs,
                [
                    "-dNOPAUSE", "-dBATCH", "-dSAFER", "-q", "-sDEVICE=png16m", "-r36", "-dFirstPage=1", "-dLastPage=1",
                    "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", "-o", png, path,
                ], new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
                if (!result.Succeeded || !File.Exists(png))
                    throw new IOException("The first page couldn't be rendered");
                return DecodeThumbnail(png).Thumbnail;
            }
            finally
            {
                File.Delete(png);
            }
        }

        /// <summary>A frame from the video, via ffmpeg, and what ffprobe says about it (size, codec).</summary>
        private static (ImageSource Thumbnail, VideoInfo? Info) DecodeVideoThumbnail(string path)
        {
            var tools = ToolLocator.CreateDefault();
            var ffmpeg = tools.Find(Tool.Ffmpeg) ?? throw new IOException("ffmpeg not found");
            var frame = VideoThumbnail.ExtractAsync(ffmpeg, path, Path.GetTempPath()).GetAwaiter().GetResult()
                        ?? throw new IOException("No frame could be extracted");
            try
            {
                var (thumbnail, _, _) = DecodeThumbnail(frame);
                var info = tools.Find(Tool.Ffprobe) is { } ffprobe
                    ? VideoInfo.ProbeAsync(ffprobe, path).GetAwaiter().GetResult()
                    : null;
                return (thumbnail, info);
            }
            finally
            {
                File.Delete(frame);
            }
        }

        private static (ImageSource Thumbnail, int Width, int Height) DecodeThumbnail(string path)
        {
            var bytes = File.ReadAllBytes(path);

            // Header first for the full size, then a decode scaled down at the decoder (cheap for JPEG).
            var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (width >= height)
                image.DecodePixelWidth = Math.Min(width, ThumbnailPixels);
            else
                image.DecodePixelHeight = Math.Min(height, ThumbnailPixels);
            image.EndInit();
            image.Freeze();
            return (image, width, height);
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024):0.##} MB",
        };

        private bool Set<T>(ref T field, T value, string? alsoNotify = null, [CallerMemberName] string name = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            Notify(name);
            if (alsoNotify is not null)
                Notify(alsoNotify);
            return true;
        }

        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
