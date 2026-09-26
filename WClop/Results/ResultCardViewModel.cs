using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Video;

namespace WClop.Results
{
    /// <summary>One floating result card (project.md §16.1).</summary>
    internal sealed class ResultCardViewModel : INotifyPropertyChanged
    {
        private const int ThumbnailPixels = 144;

        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _autoHide;
        private ImageSource? _thumbnail;
        private string? _dimensions;
        private bool _isHovered;
        private string? _loadedThumbnailPath;

        public ResultCardViewModel(OptimisationJob job, Dispatcher dispatcher, TimeSpan autoHideAfter, Action<ResultCardViewModel> dismiss)
        {
            Job = job;
            _dispatcher = dispatcher;
            _autoHide = new DispatcherTimer(autoHideAfter, DispatcherPriority.Background, (_, _) => dismiss(this), dispatcher);
            _autoHide.Stop();
            job.PropertyChanged += OnJobChanged;
            LoadThumbnail(job.CurrentPath);
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
            if (AutoHideEnabled && Job.IsFinished && !IsHovered)
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
            Notify(nameof(Status));
            Notify(nameof(IsRunning));
            Notify(nameof(IsProgressIndeterminate));
            Notify(nameof(ProgressPercent));
            Notify(nameof(IsFailed));
            Notify(nameof(IsSucceeded));
            Notify(nameof(CanRestore));
            Notify(nameof(SizeText));
            Notify(nameof(HasSizeText));
            Notify(nameof(CanShowFile));
            Notify(nameof(CanConvert));
            Notify(nameof(CanFit));
            Notify(nameof(CanRunPipeline));

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
                            var (videoFrame, width, height) = DecodeVideoThumbnail(path);
                            Thumbnail = videoFrame;
                            Dimensions = $"{width} × {height}";
                            break;

                        case MediaKind.Pdf:
                            Thumbnail = DecodePdfThumbnail(path);
                            Dimensions = PdfDetail();
                            break;

                        case MediaKind.Audio:
                            // No picture; show what matters for audio instead.
                            Dimensions = Job.Result?.BitrateKbps is { } kbps ? $"{kbps} kbps" : null;
                            break;

                        default:
                            var (image, imageWidth, imageHeight) = DecodeThumbnail(path);
                            Thumbnail = image;
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

        /// <summary>A frame from the video, via ffmpeg; the reported size is the video's (the frame is scaled down).</summary>
        private static (ImageSource Thumbnail, int Width, int Height) DecodeVideoThumbnail(string path)
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
                return (thumbnail, info?.Width ?? 0, info?.Height ?? 0);
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
