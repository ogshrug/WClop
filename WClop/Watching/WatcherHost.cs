using System.IO;
using WClop.Core;
using WClop.Core.Images;
using WClop.Core.Logging;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Watching;
using WClop.Core.Processes;
using WClop.Core.Video;
using WClop.Core.DropZone;
using WClop.Core.Pipelines;

namespace WClop.Watching
{
    /// <summary>
    /// Owns the folder watchers: images, videos (screen recordings), PDFs and audio. PDFs have no folders by default
    /// and audio watching is off (as in Clop, project.md §4.1).
    /// </summary>
    internal sealed class WatcherHost : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly SettingsStore _settingsStore;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly List<FolderWatcher> _watchers = [];

        public WatcherHost(
            AppSettings settings,
            SettingsStore settingsStore,
            AppPaths paths,
            OptimisationManager manager,
            FileOptimisationService service,
            RecentWrites recentWrites,
            RecentCopies recentCopies,
            PipelineLibrary pipelines,
            System.Windows.Threading.Dispatcher dispatcher)
        {
            // Pipelines attached to a watched folder run after (or, if they all say so, instead of) the optimisation.
            async Task<FileOptimisationResult> WithPipelines(
                string path, Action<double> progress, CancellationToken cancellationToken, Func<Task<FileOptimisationResult>> optimise)
            {
                var attached = pipelines.AttachedTo(PipelineTrigger.Folder, path);
                if (attached.Count == 0)
                    return await optimise();

                FileOptimisationResult start;
                if (PipelineLibrary.SkipsOptimisation(attached))
                {
                    start = await service.DescribeAsync(path, cancellationToken);
                }
                else
                {
                    try
                    {
                        start = await optimise();
                    }
                    catch (NotSmallerException)
                    {
                        start = await service.DescribeAsync(path, cancellationToken);
                    }
                }

                var context = new PipelineContext { Origin = PipelineOrigin.Folder, OnProgress = progress };
                return (await pipelines.RunAllAsync(attached, start, context, cancellationToken)).Result;
            }

            _settings = settings;
            _settingsStore = settingsStore;
            _dispatcher = dispatcher;

            var images = new WatchKind
            {
                Name = "image",
                Formats = new HashSet<FileFormat>(DropInputs.OptimisableImages),
                IsValid = ImageDecoding.IsValid,
                ReadSize = ImageDecoding.TryReadSize,
                Process = (path, progress, cancellationToken) => WithPipelines(path, progress, cancellationToken, async () =>
                {
                    var result = await service.OptimiseImageAsync(path, new FileOptimisationRequest(), cancellationToken);
                    Log.Info($"Watcher (image): {path}: {result.OldSize} → {result.NewSize} bytes" +
                             $"{(result.FromCache ? " [cached]" : "")} → {result.OutputPath}");
                    return result;
                }),
                SettleTimeout = TimeSpan.FromMinutes(1),
                Copies = recentCopies,
            };

            // Screen recordings (Snipping Tool, Game Bar) are written for as long as the recording runs; a file isn't
            // valid until ffprobe can read its duration, which an MP4 only has once the recorder finalises it.
            var ffprobe = service.Tools.Find(Tool.Ffprobe);
            VideoInfo? Probe(string path) =>
                ffprobe is null ? null : VideoInfo.ProbeAsync(ffprobe, path).GetAwaiter().GetResult();

            var videos = new WatchKind
            {
                Name = "video",
                Formats = VideoFormats(settings.Compression),
                IsValid = path => Probe(path) is { Duration.TotalSeconds: > 0 },
                ReadSize = path => Probe(path) is { } info ? new ImageSize(info.Width, info.Height) : null,
                Process = (path, progress, cancellationToken) => WithPipelines(path, progress, cancellationToken, async () =>
                {
                    var result = await service.OptimiseVideoAsync(path, new FileOptimisationRequest(), progress, cancellationToken);
                    Log.Info($"Watcher (video): {path}: {result.OldSize} → {result.NewSize} bytes → {result.OutputPath}");
                    return result;
                }),
                SettleTimeout = TimeSpan.FromMinutes(30),
            };

            var pdfs = new WatchKind
            {
                Name = "pdf",
                Formats = new HashSet<FileFormat> { FileFormat.Pdf },
                // A PDF being written isn't valid until its trailer is there; PdfPig refuses it until then.
                IsValid = path =>
                {
                    try
                    {
                        return Core.Pdf.PdfAnalysis.Analyse(path).Pages > 0;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                },
                Process = (path, progress, cancellationToken) => WithPipelines(path, progress, cancellationToken, async () =>
                {
                    var result = await service.OptimiseAsync(path, new FileOptimisationRequest(), progress, cancellationToken);
                    Log.Info($"Watcher (pdf): {path}: {result.OldSize} → {result.NewSize} bytes at {result.Dpi} DPI");
                    return result;
                }),
                SettleTimeout = TimeSpan.FromMinutes(5),
            };

            var audio = new WatchKind
            {
                Name = "audio",
                Formats = new HashSet<FileFormat>(DropInputs.OptimisableAudio),
                IsValid = path => ffprobe is not null
                                  && Core.Audio.AudioInfo.ProbeAsync(ffprobe, path).GetAwaiter().GetResult() is { Duration.TotalSeconds: > 0 },
                Process = (path, progress, cancellationToken) => WithPipelines(path, progress, cancellationToken, async () =>
                {
                    var result = await service.OptimiseAsync(path, new FileOptimisationRequest(), progress, cancellationToken);
                    Log.Info($"Watcher (audio): {path}: {result.OldSize} → {result.NewSize} bytes at {result.BitrateKbps} kbps");
                    return result;
                }),
                SettleTimeout = TimeSpan.FromMinutes(10),
            };

            Add(images, settings.Watching.Images);
            Add(videos, settings.Watching.Videos);
            Add(pdfs, settings.Watching.Pdfs);
            Add(audio, settings.Watching.Audio);

            void Add(WatchKind kind, WatcherSettings watcherSettings)
            {
                var watcher = new FolderWatcher(
                    kind, watcherSettings, settings.Watching, paths, manager, service.Markers, recentWrites,
                    settings.Watching.FirstLaunchUtc);
                watcher.Notice += message => Notice?.Invoke(message);
                watcher.StormDetected += OnStormDetected;
                _watchers.Add(watcher);
            }
        }

        /// <summary>MP4 plus the formats set to be converted to MP4 (MOV, MPEG, WebM by default; §9.2).</summary>
        private static HashSet<FileFormat> VideoFormats(CompressionSettings compression)
        {
            var formats = new HashSet<FileFormat> { FileFormat.Mp4 };
            foreach (var extension in compression.ConvertToMp4)
            {
                var format = FileFormats.FromExtension("x." + extension);
                if (format.Kind() == MediaKind.Video)
                    formats.Add(format);
            }

            return formats;
        }

        /// <summary>Something to tell the user (raised on a thread-pool thread).</summary>
        public event Action<string>? Notice;

        public void Start()
        {
            foreach (var watcher in _watchers)
                _ = watcher.StartAsync();
        }

        /// <summary>Apply changed settings (folders, enabled flags).</summary>
        public void Restart() => Start();

        public void Dispose()
        {
            foreach (var watcher in _watchers)
                watcher.Dispose();
        }

        private void OnStormDetected(WatchKind kind)
        {
            try
            {
                _settingsStore.Save(_settings);
            }
            catch (IOException e)
            {
                Log.Error("Couldn't save settings", e);
            }

            // Clop shows an alert here (project.md §4.5); the tray menu can turn watching back on.
            _dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(
                $"WClop has stopped watching for new {kind.Name}s.\n\n" +
                "Right after WClop first started, more than 5 files changed in a watched folder within a few seconds. " +
                "That usually means another app (a sync tool, an editor, a game) is rewriting files there constantly, " +
                "and WClop optimising them would fight with it.\n\n" +
                $"You can turn it back on from the tray menu (\"Optimise new {kind.Name}s\"), or remove that folder " +
                "in the settings file.",
                "WClop",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning));
        }
    }
}
