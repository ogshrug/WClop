using System.IO;
using System.Text;
using System.Windows.Forms;
using WClop.Core;
using WClop.Core.Clipboard;
using WClop.Core.Images;
using WClop.Core.Logging;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Watching;
using static WClop.Clipboard.NativeMethods;

namespace WClop.Clipboard
{
    /// <summary>
    /// The clipboard engine (project.md §3, §26.1).
    /// <list type="bullet">
    /// <item>All clipboard access happens on one dedicated STA thread with a message-only window,
    /// because clipboard reads can block for a long time while the source app renders its data.</item>
    /// <item>Change notifications come from <c>AddClipboardFormatListener</c>, debounced because one copy
    /// often produces several updates.</item>
    /// <item>Optimisation runs as a job under the "clipboard" key, so a newer copy supersedes an older one.
    /// The result is written back only if the clipboard hasn't changed since it was read.</item>
    /// </list>
    /// </summary>
    internal sealed class ClipboardWatcher : IDisposable
    {
        public const string JobKey = "clipboard";

        private readonly AppSettings _settings;
        private readonly FileOptimisationService _service;
        private readonly OptimisationManager _manager;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new();

        private WindowsFormsSynchronizationContext? _context;
        private MessageWindow? _window;
        private System.Windows.Forms.Timer? _debounce;

        private uint _markerFormat;
        private uint _lastOwnWriteSequence;
        private volatile bool _pauseNextEvent;

        // Clipboard-thread state.
        private PendingJob? _pending;
        private string? _lastOutputHash;

        /// <summary>Results collected for "Collect clipboard results"; thread-safe, cleared from the UI too.</summary>
        private readonly ClipboardCollection _collection = new();

        /// <summary>
        /// The clipboard content currently being optimised. Apps often write the same copy several times
        /// (the Snipping Tool does); those repeats update <see cref="Sequence"/> instead of restarting the job,
        /// so the result is still written back over the latest identical copy.
        /// </summary>
        private sealed class PendingJob(string hash, uint sequence)
        {
            public string Hash { get; } = hash;
            public uint Sequence { get; set; } = sequence;
        }

        private readonly RecentCopies? _recentCopies;

        /// <summary>Pipelines attached to the clipboard; set once at startup.</summary>
        public PipelineLibrary? Pipelines { get; set; }

        public ClipboardWatcher(
            AppSettings settings, FileOptimisationService service, OptimisationManager manager, RecentCopies? recentCopies = null)
        {
            _recentCopies = recentCopies;
            _settings = settings;
            _service = service;
            _manager = manager;
            _thread = new Thread(Run) { IsBackground = true, Name = "WClop clipboard" };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        /// <summary>Raised on the clipboard thread when a "skip the next copy" request has been used up.</summary>
        public event Action? PauseConsumed;

        public bool IsPausedForNextEvent => _pauseNextEvent;

        public void Start()
        {
            _thread.Start();
            _started.Wait();
        }

        /// <summary>Leave the next clipboard change alone (the user wants to copy something raw once).</summary>
        public void PauseNextEvent() => _pauseNextEvent = true;

        public void CancelPause() => _pauseNextEvent = false;

        /// <summary>
        /// Starts the clipboard collection again (tray "Clear results", the Escape hotkey). What's on the clipboard
        /// now stays there; the next optimised image starts a new collection.
        /// </summary>
        public void ClearCollection() => _collection.Clear();

        /// <summary>Puts an image file on the clipboard (used by restore). Marked so it isn't optimised again.</summary>
        public Task PutImageFileAsync(string path)
        {
            var payload = BuildPayload(path, FileTypeSniffer.Detect(path));
            return InvokeAsync(() => Write(payload));
        }

        /// <summary>
        /// Puts a result on the clipboard: image data plus the file for PNG / JPEG / GIF, just the file for formats
        /// other apps can't paste as pixels (WebP, AVIF, videos). Marked so it isn't optimised again.
        /// </summary>
        public Task PutResultAsync(string path) =>
            FileTypeSniffer.Detect(path) is FileFormat.Png or FileFormat.Jpeg or FileFormat.Gif
                ? PutImageFileAsync(path)
                : InvokeAsync(() => WriteFileOnly(path));

        private void WriteFileOnly(string path, IReadOnlyList<string>? collected = null)
        {
            if (!ClipboardAccess.TryOpen(_window!.Handle))
                throw new IOException("Couldn't open the clipboard (held by another app)");
            try
            {
                EmptyClipboard();
                ClipboardAccess.Write(_markerFormat, Encoding.ASCII.GetBytes("true"));
                ClipboardAccess.Write(CF_HDROP, ClipboardAccess.DropFiles(collected is { Count: > 0 } ? collected : [path]));
                ClipboardAccess.Write(RegisterClipboardFormat(ClipboardFormatNames.PreferredDropEffect), ClipboardAccess.Dword(1));
            }
            finally
            {
                ClipboardAccess.Close();
                _lastOwnWriteSequence = GetClipboardSequenceNumber();
            }
        }

        public void Dispose()
        {
            _context?.Post(_ => System.Windows.Forms.Application.ExitThread(), null);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private void Run()
        {
            _context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_context);

            _markerFormat = RegisterClipboardFormat(ClipboardFormatNames.OwnMarker);
            _window = new MessageWindow(OnClipboardUpdate);
            _debounce = new System.Windows.Forms.Timer { Interval = 100 };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                HandleChange();
            };

            if (!AddClipboardFormatListener(_window.Handle))
                Log.Error("AddClipboardFormatListener failed");
            _started.Set();

            System.Windows.Forms.Application.Run();

            RemoveClipboardFormatListener(_window.Handle);
            _debounce.Dispose();
            _window.DestroyHandle();
        }

        private Task InvokeAsync(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _context!.Post(_ =>
            {
                try
                {
                    action();
                    done.SetResult();
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            }, null);
            return done.Task;
        }

        private void OnClipboardUpdate()
        {
            _debounce!.Stop();
            _debounce.Start();
        }

        /// <summary>
        /// What was read from the clipboard: the decision, the format actually read (images may fall back to a
        /// lower-priority format) and its bytes.
        /// </summary>
        private sealed record ClipboardRead(
            uint Sequence, string? Owner, ClipboardSnapshot Snapshot, ClipboardDecision Decision, ClipboardDecision Job, byte[]? Data);

        /// <summary>Clipboard thread: an automatic clipboard change. Read just what's needed, then hand off.</summary>
        private void HandleChange()
        {
            var sequence = GetClipboardSequenceNumber();
            if (sequence == _lastOwnWriteSequence)
                return;
            if (!_settings.Clipboard.Enabled || _settings.Watching.Paused)
                return;
            if (_pauseNextEvent)
            {
                _pauseNextEvent = false;
                Log.Info("Clipboard: skipped this copy as requested");
                PauseConsumed?.Invoke();
                return;
            }

            if (Read(onDemand: false) is not { } read)
                return;

            if (read.Decision is ClipboardDecision.Ignore ignore)
            {
                if (_settings.Clipboard.LogFormats)
                    Log.Info($"Clipboard: ignored ({ignore.Reason})");
                return;
            }

            StartJob(read, new FileOptimisationRequest(), "Optimising", dedupe: true);
        }

        /// <summary>
        /// The "optimise the clipboard now" hotkey (project.md §3.8, §17): runs even when automatic clipboard
        /// optimisation is off or paused, bypasses the deny-lists, and also understands copied file paths,
        /// base64 images and image URLs. <paramref name="request"/> carries the factor / scale.
        /// Returns false (and raises <see cref="Notice"/>) if there's nothing usable on the clipboard.
        /// </summary>
        public Task<bool> OptimiseNowAsync(FileOptimisationRequest request, string status)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _context!.Post(_ =>
            {
                try
                {
                    var read = Read(onDemand: true);
                    if (read is null || read.Decision is ClipboardDecision.Ignore)
                    {
                        var reason = (read?.Decision as ClipboardDecision.Ignore)?.Reason ?? "couldn't read the clipboard";
                        Notice?.Invoke($"Nothing to optimise: {reason}");
                        done.SetResult(false);
                        return;
                    }

                    StartJob(read, request with { Force = true }, status, dedupe: false);
                    done.SetResult(true);
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            }, null);
            return done.Task;
        }

        /// <summary>Raised when the user asked for something that couldn't be done (e.g. nothing on the clipboard).</summary>
        public event Action<string>? Notice;

        /// <summary>Clipboard thread: snapshot the clipboard, decide, and read the chosen data. Null if it couldn't be opened.</summary>
        private ClipboardRead? Read(bool onDemand)
        {
            var sequence = GetClipboardSequenceNumber();
            var owner = ClipboardAccess.OwnerProcess();
            if (!ClipboardAccess.TryOpen(_window!.Handle))
            {
                Log.Warn("Clipboard: couldn't open the clipboard (held by another app)");
                return null;
            }

            ClipboardSnapshot snapshot;
            ClipboardDecision decision;
            ClipboardDecision? job = null;
            byte[]? data = null;
            try
            {
                var formats = ClipboardAccess.EnumerateFormats();
                var ids = formats.ToDictionary(f => f.Name, f => f.Id, StringComparer.OrdinalIgnoreCase);
                if (_settings.Clipboard.LogFormats)
                    Log.Info($"Clipboard formats from {owner ?? "unknown"}: {string.Join(" | ", formats.Select(f => f.Name))}");

                uint? Dword(string name) => ids.TryGetValue(name, out var id) ? ClipboardAccess.ReadDword(id) : null;

                snapshot = new ClipboardSnapshot
                {
                    Formats = formats.Select(f => f.Name).ToList(),
                    OwnerProcess = owner,
                    CanIncludeInHistory = Dword(ClipboardFormatNames.CanIncludeInHistory),
                    CanUploadToCloud = Dword(ClipboardFormatNames.CanUploadToCloud),
                    PreferredDropEffect = Dword(ClipboardFormatNames.PreferredDropEffect),
                    Files = ids.ContainsKey(ClipboardFormatNames.HDrop) ? ClipboardAccess.ReadFiles() : [],
                };

                // The marker check for image paths does disk I/O, so it happens later, off this thread.
                decision = onDemand ? ClipboardPolicy.DecideOnDemand(snapshot) : ClipboardPolicy.Decide(snapshot, _settings.Clipboard);
                switch (decision)
                {
                    case ClipboardDecision.ReadImage:
                        // A listed format isn't always readable; take the best one that is.
                        foreach (var candidate in ClipboardPolicy.ImageCandidates(snapshot))
                        {
                            data = ClipboardAccess.ReadBytes(ids[candidate.Format]);
                            if (data is { Length: > 0 })
                            {
                                job = candidate;
                                break;
                            }
                        }

                        break;

                    case ClipboardDecision.ReadText text:
                        data = ClipboardAccess.ReadBytes(ids[text.Format]);
                        job = data is { Length: > 0 } ? text : null;
                        break;

                    default:
                        job = decision;
                        break;
                }
            }
            finally
            {
                ClipboardAccess.Close();
            }

            if (decision is ClipboardDecision.ReadImage && job is null)
                (job, data) = ReadThroughOle(snapshot, sequence);

            if (decision is ClipboardDecision.ReadImage or ClipboardDecision.ReadText && job is null)
            {
                Log.Warn($"Clipboard: no readable data from {owner ?? "unknown"} ({string.Join(" | ", snapshot.Formats)})");
                decision = new ClipboardDecision.Ignore("the source app didn't hand over its data");
            }

            return new ClipboardRead(sequence, owner, snapshot, decision, job ?? decision, data);
        }

        /// <summary>Clipboard thread: start the job for what was read, unless the same content is already in flight.</summary>
        private void StartJob(ClipboardRead read, FileOptimisationRequest request, string status, bool dedupe)
        {
            var hash = read.Job is ClipboardDecision.ImageFile imageFile
                ? ContentHash.OfBytes(Encoding.UTF8.GetBytes(imageFile.Path))
                : ContentHash.OfBytes(read.Data);
            if (read.Job is not ClipboardDecision.ImageFile)
                _recentCopies?.Add(hash);

            if (dedupe)
            {
                if (_pending is { } pending && pending.Hash == hash)
                {
                    // Same content written again while it's still being optimised: keep that job, just follow the latest write.
                    pending.Sequence = read.Sequence;
                    return;
                }

                if (hash == _lastOutputHash)
                {
                    // Another app (e.g. a clipboard manager) re-copied WClop's output without the marker.
                    return;
                }
            }

            var current = _pending = new PendingJob(hash, read.Sequence);
            var displayName = read.Job is ClipboardDecision.ImageFile file ? Path.GetFileName(file.Path) : "Copied image";
            var job = read.Job;
            var data = read.Data;
            var owner = read.Owner;
            _manager.Start(JobKey, JobSource.Clipboard, displayName, null,
                (optimisationJob, cancellationToken) => ProcessAsync(optimisationJob, current, job, data, owner, request, cancellationToken), status);
        }

        /// <summary>
        /// Fallback for formats that are only available as OLE streams (Win32 <c>GetClipboardData</c> returns
        /// nothing for them). Runs after the Win32 clipboard handle is closed, since OLE opens it itself.
        /// </summary>
        private static (ClipboardDecision.ReadImage?, byte[]?) ReadThroughOle(ClipboardSnapshot snapshot, uint sequence)
        {
            foreach (var candidate in ClipboardPolicy.ImageCandidates(snapshot).Where(c => !c.IsDib))
            {
                try
                {
                    var bytes = System.Windows.Forms.Clipboard.GetDataObject()?.GetData(candidate.Format) switch
                    {
                        byte[] array => array,
                        MemoryStream memory => memory.ToArray(),
                        Stream stream => ReadAll(stream),
                        _ => null,
                    };
                    if (GetClipboardSequenceNumber() != sequence)
                        return (null, null); // changed underneath us; the next update handles it
                    if (bytes is { Length: > 0 })
                        return (candidate, bytes);
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ThreadStateException)
                {
                    Log.Warn($"Clipboard: OLE read of {candidate.Format} failed: {e.Message}");
                }
            }

            return (null, null);
        }

        private static byte[] ReadAll(Stream stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        /// <summary>
        /// Thread pool: turn the clipboard content into a file, optimise it, run any pipelines attached to the
        /// clipboard, and write the result back.
        /// </summary>
        private async Task<FileOptimisationResult> ProcessAsync(
            OptimisationJob optimisationJob, PendingJob pending, ClipboardDecision decision, byte[]? data, string? owner,
            FileOptimisationRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var input = await SaveInputAsync(decision, data, request.Force, cancellationToken);
                FileOptimisationResult? result = null;
                try
                {
                    var pipelines = Pipelines?.AttachedTo(PipelineTrigger.Clipboard, input) ?? [];
                    if (PipelineLibrary.SkipsOptimisation(pipelines))
                    {
                        result = await _service.DescribeAsync(input, cancellationToken);
                    }
                    else
                    {
                        try
                        {
                            result = await _service.OptimiseImageAsync(
                                input, request with { Behaviour = OutputBehaviour.Temporary }, cancellationToken);
                            Log.Info($"Clipboard: {result.OldSize} → {result.NewSize} bytes ({result.OutputFormat}){(result.FromCache ? " [cached]" : "")}");
                        }
                        catch (NotSmallerException) when (pipelines.Count > 0)
                        {
                            // Already fully compressed: the pipelines still run.
                            result = await _service.DescribeAsync(input, cancellationToken);
                        }
                    }

                    var copiedByPipeline = false;
                    if (pipelines.Count > 0)
                    {
                        var (final, _) = await Pipelines!.RunAllAsync(pipelines, result, new PipelineContext
                        {
                            Origin = PipelineOrigin.Clipboard,
                            CopiedBy = owner,
                            OnStatus = status => optimisationJob.Status = status,
                            OnProgress = progress => optimisationJob.Progress = progress,
                            CopyToClipboard = async (path, mode) =>
                            {
                                copiedByPipeline = true;
                                await PutForPipelineAsync(path, mode);
                            },
                        }, cancellationToken);
                        result = final;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!copiedByPipeline)
                    {
                        var outputPath = result.OutputPath;
                        var pixels = result.OutputFormat is FileFormat.Png or FileFormat.Jpeg or FileFormat.Gif;
                        var payload = pixels ? BuildPayload(outputPath, result.OutputFormat) : null;
                        await InvokeAsync(() =>
                        {
                            // Compare against the latest write of this same content, not just the first one.
                            if (GetClipboardSequenceNumber() == pending.Sequence)
                            {
                                var collected = Collect(outputPath);
                                if (payload is not null)
                                {
                                    Write(payload, collected);
                                    _lastOutputHash = ContentHash.OfBytes(payload.ImageBytes);
                                }
                                else
                                {
                                    WriteFileOnly(outputPath, collected);
                                }
                            }
                            else
                            {
                                Log.Info("Clipboard: changed while optimising; not replacing the newer content");
                            }
                        });
                    }

                    return result;
                }
                finally
                {
                    // The service keeps a hash-named backup of the input; the temp copy isn't needed
                    // (unless it is the result, e.g. a pipeline worked on it in place).
                    if (!string.Equals(result?.OutputPath, input, StringComparison.OrdinalIgnoreCase))
                        TryDelete(input);
                }
            }
            finally
            {
                _ = InvokeAsync(() =>
                {
                    if (_pending == pending)
                        _pending = null;
                });
            }
        }

        /// <summary>
        /// Clipboard thread, just before a result is written back: with "Collect clipboard results" on, adds it to the
        /// collection and returns every collected file (oldest first) to put on the clipboard; otherwise null.
        /// Only results that really reach the clipboard are collected.
        /// </summary>
        private IReadOnlyList<string>? Collect(string path)
        {
            if (!_settings.Clipboard.CollectResults)
            {
                _collection.Clear();
                return null;
            }

            var all = _collection.Add(path, TimeSpan.FromSeconds(_settings.Clipboard.CollectResultsIdleSeconds));
            // Working-folder cleanup may have removed an older one, and one missing file fails the whole paste.
            return all.Where(File.Exists).ToList();
        }

        /// <summary>A pipeline's copyToClipboard step: the file, its pixels, its path, or a Markdown image link.</summary>
        public Task PutForPipelineAsync(string path, string mode) => mode switch
        {
            "path" => PutTextAsync(path),
            "markdown" => PutTextAsync($"![{Path.GetFileNameWithoutExtension(path)}]({new Uri(path).AbsoluteUri})"),
            "image" when FileTypeSniffer.Detect(path) is FileFormat.Png or FileFormat.Jpeg or FileFormat.Gif => PutImageFileAsync(path),
            _ => PutResultAsync(path),
        };

        public Task PutTextAsync(string text) => InvokeAsync(() =>
        {
            if (!ClipboardAccess.TryOpen(_window!.Handle))
                throw new IOException("Couldn't open the clipboard (held by another app)");
            try
            {
                EmptyClipboard();
                ClipboardAccess.Write(_markerFormat, Encoding.ASCII.GetBytes("true"));
                ClipboardAccess.Write(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
            }
            finally
            {
                ClipboardAccess.Close();
                _lastOwnWriteSequence = GetClipboardSequenceNumber();
            }
        });

        private async Task<string> SaveInputAsync(
            ClipboardDecision decision, byte[]? data, bool force, CancellationToken cancellationToken)
        {
            var folder = _service.Paths.Images;
            switch (decision)
            {
                case ClipboardDecision.ReadImage read:
                    var (bytes, extension) = read.Encoding switch
                    {
                        ClipboardImageEncoding.Dib => (DibConverter.DibToPng(data!), ".png"),
                        ClipboardImageEncoding.Gif => (data!, ".gif"),
                        ClipboardImageEncoding.Jpeg => (data!, ".jpg"),
                        _ => (data!, ".png"),
                    };
                    return await WriteTempAsync(bytes, extension, cancellationToken);

                case ClipboardDecision.ImageFile file:
                    if (!force && _service.Markers.Get(file.Path) != MarkerStatus.None)
                        throw new JobSkippedException("Image file already optimised");
                    return CopyToWorkDir(file.Path);

                case ClipboardDecision.ReadText:
                    var text = Encoding.Unicode.GetString(data!).TrimEnd('\0');
                    switch (ClipboardText.Classify(text))
                    {
                        case TextTarget.Base64Image image:
                            return await WriteTempAsync(image.Data, image.Format.Extension(), cancellationToken);

                        case TextTarget.FilePath path when FileFormats.FromExtension(path.Path).Kind() == MediaKind.Image:
                            return CopyToWorkDir(path.Path);

                        case TextTarget.FilePath path:
                            throw new UnsupportedFormatException($"{Path.GetFileName(path.Path)} isn't an image");

                        case TextTarget.WebUrl url:
                            var downloaded = await new MediaDownloader(_service.Paths).DownloadAsync(url.Url, cancellationToken);
                            if (FileTypeSniffer.Detect(downloaded).Kind() != MediaKind.Image)
                            {
                                TryDelete(downloaded);
                                throw new UnsupportedFormatException("The URL isn't an image (other media comes in later phases)");
                            }

                            return downloaded;

                        default:
                            throw new UnsupportedFormatException("The copied text isn't an image, a file path or a URL");
                    }

                default:
                    throw new JobSkippedException("Nothing to optimise");
            }

            async Task<string> WriteTempAsync(byte[] bytes, string extension, CancellationToken token)
            {
                var input = AppPaths.NewTempPath(folder, extension);
                await File.WriteAllBytesAsync(input, bytes, token);
                return input;
            }

            string CopyToWorkDir(string path)
            {
                // Work on a copy so the user's file is never touched.
                var copy = AppPaths.NewTempPath(folder, Path.GetExtension(path));
                File.Copy(path, copy);
                return copy;
            }
        }

        private sealed record Payload(string Path, string ImageFormat, byte[] ImageBytes, byte[] Dib);

        private static Payload BuildPayload(string path, FileFormat format)
        {
            var imageFormat = format switch
            {
                FileFormat.Gif => "GIF",
                FileFormat.Jpeg => "JFIF",
                _ => "PNG",
            };
            return new Payload(path, imageFormat, File.ReadAllBytes(path), DibConverter.ImageFileToDib(path));
        }

        /// <summary>
        /// Clipboard thread: replace the clipboard contents with the image, marked as WClop's own write.
        /// <paramref name="collected"/> (when collecting results) is the file list, instead of just this image.
        /// </summary>
        private void Write(Payload payload, IReadOnlyList<string>? collected = null)
        {
            if (!ClipboardAccess.TryOpen(_window!.Handle))
                throw new IOException("Couldn't open the clipboard (held by another app)");

            try
            {
                EmptyClipboard();
                ClipboardAccess.Write(_markerFormat, Encoding.ASCII.GetBytes("true"));
                ClipboardAccess.Write(RegisterClipboardFormat(payload.ImageFormat), payload.ImageBytes);
                ClipboardAccess.Write(CF_DIB, payload.Dib);

                // Collected results are always offered as files: pasting them together is the point.
                if (collected is { Count: > 0 } || _settings.Clipboard.CopyImageFilePath)
                {
                    ClipboardAccess.Write(CF_HDROP, ClipboardAccess.DropFiles(collected is { Count: > 0 } ? collected : [payload.Path]));
                    ClipboardAccess.Write(
                        RegisterClipboardFormat(ClipboardFormatNames.PreferredDropEffect), ClipboardAccess.Dword(1)); // copy
                }

                if (!_settings.Clipboard.IncludeResultsInClipboardHistory)
                    ClipboardAccess.Write(
                        RegisterClipboardFormat(ClipboardFormatNames.CanIncludeInHistory), ClipboardAccess.Dword(0));
            }
            finally
            {
                ClipboardAccess.Close();
                _lastOwnWriteSequence = GetClipboardSequenceNumber();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private sealed class MessageWindow : NativeWindow
        {
            private readonly Action _onClipboardUpdate;

            public MessageWindow(Action onClipboardUpdate)
            {
                _onClipboardUpdate = onClipboardUpdate;
                CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_CLIPBOARDUPDATE)
                    _onClipboardUpdate();
                base.WndProc(ref m);
            }
        }
    }
}
