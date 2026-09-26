using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using WClop.Core.DropZone;
using WClop.Core.Ipc;
using WClop.Core.Logging;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Settings;

namespace WClop.Integration
{
    /// <summary>
    /// The app side of the local API (project.md §20.1): three named pipes for optimise, stop and settings requests,
    /// used by the <c>wclop</c> command line, the Explorer right-click menu, Send To, and a second launch of WClop.exe.
    /// </summary>
    internal sealed class IpcHost : IDisposable
    {
        /// <summary>Explorer starts one process per selected file; requests this close together are one selection.</summary>
        private static readonly TimeSpan GatherWindow = TimeSpan.FromMilliseconds(600);

        private readonly FileOptimisationService _service;
        private readonly OptimisationManager _manager;
        private readonly AppSettings _settings;
        private readonly PipelineLibrary _pipelines;
        private readonly Clipboard.ClipboardWatcher _clipboard;
        private readonly Action<IReadOnlyList<string>, DropPreset, bool> _openBatch;
        private readonly Action _applySettings;
        private readonly Action _showSettings;
        private readonly List<IpcServer> _servers = [];
        private readonly ConcurrentDictionary<OptimisationJob, TaskCompletionSource> _waiting = new();

        private readonly object _gatherGate = new();
        private readonly List<string> _gathered = [];
        private Timer? _gatherTimer;

        public IpcHost(
            FileOptimisationService service, OptimisationManager manager, AppSettings settings, PipelineLibrary pipelines,
            Clipboard.ClipboardWatcher clipboard, Action<IReadOnlyList<string>, DropPreset, bool> openBatch, Action applySettings,
            Action showSettings)
        {
            _pipelines = pipelines;
            _clipboard = clipboard;
            _service = service;
            _manager = manager;
            _settings = settings;
            _openBatch = openBatch;
            _applySettings = applySettings;
            _showSettings = showSettings;
            _manager.JobFinished += job =>
            {
                if (_waiting.TryRemove(job, out var done))
                    done.TrySetResult();
            };
        }

        public void Start()
        {
            _servers.Add(new IpcServer(IpcChannel.Optimise, HandleOptimiseAsync));
            _servers.Add(new IpcServer(IpcChannel.Stop, (_, _) =>
            {
                _manager.CancelAll();
                return Task.FromResult(JsonSerializer.Serialize(new StopReply(0), IpcNames.Json));
            }));
            _servers.Add(new IpcServer(IpcChannel.Settings, HandleSettingsAsync));
            foreach (var server in _servers)
                server.Start();
        }

        public void Dispose()
        {
            foreach (var server in _servers)
                server.Dispose();
            _gatherTimer?.Dispose();
        }

        /// <summary>
        /// Files from Explorer or Send To: gathered for a moment, then a folder or more files than the batch threshold
        /// open the batch window, and anything else is optimised with result cards.
        /// </summary>
        public void Gather(IEnumerable<string> paths)
        {
            lock (_gatherGate)
            {
                _gathered.AddRange(paths.Select(Path.GetFullPath));
                _gatherTimer ??= new Timer(_ => FlushGathered());
                _gatherTimer.Change(GatherWindow, Timeout.InfiniteTimeSpan);
            }
        }

        private void FlushGathered()
        {
            List<string> paths;
            lock (_gatherGate)
            {
                paths = _gathered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _gathered.Clear();
            }

            if (paths.Count == 0)
                return;

            var files = DropInputs.ExpandMediaPaths(paths, DropInputs.OptimisableMedia);
            if ((_settings.Files.BatchModeForFolders && paths.Any(Directory.Exists)) || files.Count > _settings.Files.BatchModeFileCountThreshold)
            {
                Log.Info($"IPC: {paths.Count} item(s) from Explorer → batch window");
                _openBatch(paths, DropPresets.All[0], false);
                return;
            }

            Log.Info($"IPC: {files.Count} file(s) from Explorer");
            StartJobs(files, new OptimisationRequestBuilder(new OptimiseCommand { Paths = files }).Build(), null);
        }

        private async Task<string> HandleOptimiseAsync(string json, CancellationToken cancellationToken)
        {
            OptimiseCommand command;
            try
            {
                command = JsonSerializer.Deserialize<OptimiseCommand>(json, IpcNames.Json) ?? throw new JsonException("Empty request");
            }
            catch (JsonException e)
            {
                return Reply(new OptimiseReply([], "Bad request: " + e.Message));
            }

            var paths = command.Paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).ToList();
            var missing = command.Paths.Count - paths.Count;
            if (paths.Count == 0)
                return Reply(new OptimiseReply([], "None of those files exist"));

            var builder = new OptimisationRequestBuilder(command);
            if (builder.Error is { } error)
                return Reply(new OptimiseReply([], error));

            if (command.Batch)
            {
                _openBatch(paths, builder.Preset, command.KeepOriginals);
                return Reply(new OptimiseReply([], "Opened the batch window"));
            }

            if (command.Source is IpcSource.Explorer or IpcSource.SendTo && !builder.HasOptions)
            {
                Gather(paths);
                return Reply(new OptimiseReply([], "Queued"));
            }

            ResolvedPipeline? pipeline = null;
            if (command.Pipeline is { } pipelineName)
            {
                try
                {
                    pipeline = _pipelines.Resolve(pipelineName);
                }
                catch (PipelineException e)
                {
                    return Reply(new OptimiseReply([], e.Message));
                }
            }

            var files = DropInputs.ExpandMediaPaths(paths, DropInputs.OptimisableMedia);
            var jobs = pipeline is null
                ? StartJobs(files, builder.Build(), builder.ConvertTo)
                : StartPipelineJobs(files, pipeline, command.SkipOptimisation ?? pipeline.SkipOptimisation);
            if (!command.Wait)
                return Reply(new OptimiseReply([], $"Started {jobs.Count} job(s)"));

            await Task.WhenAll(jobs.Select(j => j.Done)).WaitAsync(cancellationToken).ConfigureAwait(false);
            var outcomes = jobs.Select(j => new FileOutcome(
                j.Path,
                j.Job.Result?.OutputPath,
                j.Job.Result?.OldSize ?? new FileInfo(j.Path).Length,
                j.Job.Result?.NewSize ?? 0,
                j.Job.Status,
                j.Job.State == JobState.Succeeded,
                j.Job.State == JobState.Failed)).ToList();
            return Reply(new OptimiseReply(outcomes, missing > 0 ? $"{missing} path(s) didn't exist" : null));
        }

        private sealed record StartedJob(string Path, OptimisationJob Job, Task Done);

        private List<StartedJob> StartJobs(IReadOnlyList<string> files, FileOptimisationRequest request, FileFormat? convertTo)
        {
            var started = new List<StartedJob>();
            foreach (var file in files)
            {
                var job = _manager.Start(file, JobSource.Cli, Path.GetFileName(file), file, async (j, cancellationToken) =>
                {
                    if (convertTo is not { } target)
                        return await _service.OptimiseAsync(file, request, p => j.Progress = p, cancellationToken);
                    var described = await _service.DescribeAsync(file, cancellationToken);
                    return await _service.ConvertAsync(described, target, p => j.Progress = p, cancellationToken);
                }, convertTo is { } t ? $"Converting to {t.ToString().ToUpperInvariant()}" : "Optimising");

                started.Add(Track(file, job));
            }

            return started;
        }

        private List<StartedJob> StartPipelineJobs(IReadOnlyList<string> files, ResolvedPipeline pipeline, bool skipOptimisation)
        {
            var started = new List<StartedJob>();
            foreach (var file in files)
            {
                var job = _manager.Start(file, JobSource.Cli, Path.GetFileName(file), file, async (j, cancellationToken) =>
                {
                    var outcome = await _pipelines.Runner.RunFileAsync(pipeline.Name, pipeline.Compiled, file, skipOptimisation,
                        new PipelineContext
                        {
                            Origin = PipelineOrigin.Cli,
                            OnStatus = status => j.Status = status,
                            OnProgress = progress => j.Progress = progress,
                            CopyToClipboard = _clipboard.PutForPipelineAsync,
                        }, cancellationToken);
                    if (outcome.Deleted)
                        throw new JobSkippedException($"Deleted by the {pipeline.Name} pipeline");
                    if (outcome.StoppedBy is not null && !outcome.Changed)
                        throw new JobSkippedException($"{outcome.StoppedBy} didn't match");
                    return outcome.Result with { HideResult = false };
                }, $"Running {pipeline.Name}");
                started.Add(Track(file, job));
            }

            return started;
        }

        private StartedJob Track(string file, OptimisationJob job)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[job] = done;
            if (job.IsFinished && _waiting.TryRemove(job, out _))
                done.TrySetResult(); // finished before we started listening
            return new StartedJob(file, job, done.Task);
        }

        private Task<string> HandleSettingsAsync(string json, CancellationToken cancellationToken)
        {
            try
            {
                var command = JsonSerializer.Deserialize<SettingsCommand>(json, IpcNames.Json) ?? throw new JsonException("Empty request");
                switch (command.Action.ToLowerInvariant())
                {
                    case "get":
                        return Task.FromResult(Reply(new SettingsReply(true, command.Key is null
                            ? SettingsStore.Snapshot(_settings)
                            : SettingsPath.Get(_settings, command.Key))));

                    case "set" when command.Key is not null && command.Value is not null:
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            SettingsPath.Set(_settings, command.Key, command.Value);
                            _applySettings();
                        });
                        return Task.FromResult(Reply(new SettingsReply(true, SettingsPath.Get(_settings, command.Key))));

                    case "setjson" when command.Key is not null && command.Value is not null:
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            SettingsPath.SetJson(_settings, command.Key, command.Value);
                            _applySettings();
                        });
                        return Task.FromResult(Reply(new SettingsReply(true, SettingsPath.Get(_settings, command.Key))));

                    case "quit":
                        Log.Info("IPC: asked to quit");
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
                        return Task.FromResult(Reply(new SettingsReply(true)));

                    case "show":
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(_showSettings);
                        return Task.FromResult(Reply(new SettingsReply(true)));

                    default:
                        return Task.FromResult(Reply(new SettingsReply(false, Error: $"Unknown settings action '{command.Action}'")));
                }
            }
            catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException)
            {
                return Task.FromResult(Reply(new SettingsReply(false, Error: e.Message)));
            }
        }

        private static string Reply<T>(T reply) => JsonSerializer.Serialize(reply, IpcNames.Json);
    }
}
