using System.IO;
using WClop.Clipboard;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Video;
using WClop.Core.Logging;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;

namespace WClop.Results
{
    /// <summary>
    /// Things you can do to a result: restore it, or redo it from the original at a different compression level
    /// or size. Shared by the card buttons and the hotkeys.
    /// </summary>
    internal sealed class ResultActions(
        FileOptimisationService service, ClipboardWatcher clipboard, OptimisationManager manager, PipelineLibrary pipelines)
    {
        public bool HasPipelines => pipelines.Names.Count > 0;

        /// <summary>Saved pipelines to offer for this result (shown on cards, and meant for this kind of file).</summary>
        public IReadOnlyList<string> PipelinesFor(OptimisationJob job)
        {
            var path = job.CurrentPath is { } current && File.Exists(current) ? current : job.SourcePath;
            return path is null ? [] : pipelines.NamesFor(FileTypeSniffer.Detect(path));
        }

        public static bool CanRunPipeline(OptimisationJob job) =>
            job.IsFinished && job.State is not (JobState.Failed or JobState.Cancelled)
            && (job.Result is not null && job.CurrentPath is { } current && File.Exists(current)
                || job.SourcePath is { } source && File.Exists(source));

        /// <summary>Runs a saved pipeline on a file, with a result card (Settings → Pipelines → Try it).</summary>
        public void RunPipelineOnFile(string name, string path) =>
            manager.Start(Path.GetFullPath(path), JobSource.File, Path.GetFileName(path), path, async (job, cancellationToken) =>
            {
                var pipeline = pipelines.Resolve(name);
                var outcome = await pipelines.Runner.RunFileAsync(pipeline.Name, pipeline.Compiled, path, pipeline.SkipOptimisation,
                    new PipelineContext
                    {
                        OnStatus = status => job.Status = status,
                        OnProgress = progress => job.Progress = progress,
                        CopyToClipboard = clipboard.PutForPipelineAsync,
                    }, cancellationToken);
                if (outcome.Deleted)
                    throw new JobSkippedException($"Deleted by the {pipeline.Name} pipeline");
                if (outcome.StoppedBy is not null && !outcome.Changed)
                    throw new PipelineException($"{pipeline.Name}: {outcome.StoppedBy} didn't match, nothing done");
                return outcome.Result with { HideResult = false };
            }, $"Running {name}");

        /// <summary>Runs a saved pipeline on a result (or on the file, if there was nothing to optimise), on the same card.</summary>
        public void RunPipeline(OptimisationJob job, string name)
        {
            manager.Start(job.Key, job.Source, job.DisplayName, job.SourcePath, async (newJob, cancellationToken) =>
            {
                var pipeline = pipelines.Resolve(name);
                var basis = job.Result is { } result && File.Exists(result.BackupPath) && job.CurrentPath is { } current && File.Exists(current)
                    ? result with { OutputPath = current }
                    : await pipelines.Runner.StartAsync(job.SourcePath!, !pipeline.SkipOptimisation, p => newJob.Progress = p, cancellationToken);

                var outcome = await pipelines.Runner.RunAsync(pipeline.Name, pipeline.Compiled, basis, new PipelineContext
                {
                    Origin = job.Source == JobSource.Clipboard ? PipelineOrigin.Clipboard : PipelineOrigin.Manual,
                    OnStatus = status => newJob.Status = status,
                    OnProgress = progress => newJob.Progress = progress,
                    CopyToClipboard = clipboard.PutForPipelineAsync,
                }, cancellationToken);
                if (outcome.Deleted)
                    throw new JobSkippedException($"Deleted by the {pipeline.Name} pipeline");
                if (outcome.StoppedBy is not null && !outcome.Changed)
                    throw new PipelineException($"{pipeline.Name}: {outcome.StoppedBy} didn't match, nothing done");
                if (job.Source == JobSource.Clipboard && !outcome.Log.Any(l => l.StartsWith("copied to the clipboard", StringComparison.Ordinal)))
                    await clipboard.PutResultAsync(outcome.Result.OutputPath);
                return outcome.Result with { HideResult = false };
            }, $"Running {name}");
        }

        public async Task RestoreAsync(OptimisationJob job)
        {
            if (job.Result is not { } result || job.State == JobState.Restored)
                return;

            try
            {
                var restored = await Task.Run(() => service.Restore(result));
                if (job.Source == JobSource.Clipboard)
                    await clipboard.PutImageFileAsync(restored);
                job.MarkRestored(restored);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error("Restore failed", e);
                job.Status = "Restore failed: " + e.Message;
            }
        }

        /// <summary>The scale a job's result is at (restoring resets it to full size, §14.3).</summary>
        public static double CurrentScale(OptimisationJob job) =>
            job.State == JobState.Restored ? 1 : job.Result?.Scale ?? 1;

        /// <summary>
        /// Redoes <paramref name="job"/> from its original with a new factor, scale and/or speed (nulls keep the
        /// current ones). Results without a backup to work from (e.g. "already fully compressed") start again from
        /// their source.
        /// </summary>
        public void Adjust(OptimisationJob job, int? factor, double? scale, string status, double? speed = null)
        {
            var request = new FileOptimisationRequest { Factor = factor, Scale = scale, Speed = speed };

            if (job.Result is { } result && File.Exists(result.BackupPath))
            {
                // After a restore the file on disk is the original again, at full size, normal speed and default settings.
                var basis = job.State == JobState.Restored
                    ? result with { OutputPath = job.CurrentPath ?? result.OutputPath, Scale = 1, Speed = 1, Factor = null }
                    : result;

                manager.Start(job.Key, job.Source, job.DisplayName, job.SourcePath, async (newJob, cancellationToken) =>
                {
                    var adjusted = await service.AdjustAsync(basis, request, p => newJob.Progress = p, cancellationToken);
                    if (job.Source == JobSource.Clipboard)
                        await clipboard.PutImageFileAsync(adjusted.OutputPath);
                    return adjusted;
                }, status);
                return;
            }

            if (job.Source is JobSource.File or JobSource.DropZone && job.SourcePath is { } source && File.Exists(source))
            {
                manager.Start(job.Key, job.Source, job.DisplayName, source, (newJob, cancellationToken) =>
                    service.OptimiseAsync(source, request with { Force = true }, p => newJob.Progress = p, cancellationToken), status);
                return;
            }

            if (job.Source == JobSource.Clipboard)
                _ = clipboard.OptimiseNowAsync(request, status);
        }

        /// <summary>Re-renders a PDF result from its original at another DPI.</summary>
        public void AdjustPdf(OptimisationJob job, int dpi)
        {
            if (job.Result is not { } result || !File.Exists(result.BackupPath))
                return;
            var basis = job.State == JobState.Restored ? result with { OutputPath = job.CurrentPath ?? result.OutputPath } : result;
            manager.Start(job.Key, job.Source, job.DisplayName, job.SourcePath, (newJob, cancellationToken) =>
                service.AdjustAsync(basis, new FileOptimisationRequest { PdfDpi = dpi }, p => newJob.Progress = p, cancellationToken),
                dpi >= Core.Pdf.PdfAnalysis.LosslessDpi ? "Compressing losslessly" : $"Compressing images to {dpi} DPI");
        }

        /// <summary>Sizes offered by "Fit under…": common limits (Discord, email…) smaller than the original.</summary>
        public static IReadOnlyList<long> FitTargets(OptimisationJob job)
        {
            if (!job.IsFinished || job.State == JobState.Failed || job.Result is { IsConversion: true })
                return [];
            var original = job.Result?.OldSize
                           ?? (job.SourcePath is { } source && File.Exists(source) ? new FileInfo(source).Length : 0);
            long[] sizes = [512 * 1024, 1024 * 1024, 2L << 20, 5L << 20, 8L << 20, 10L << 20, 25L << 20, 50L << 20, 100L << 20];
            return sizes.Where(s => s < original).ToList();
        }

        /// <summary>Re-does a result from its original so it fits under <paramref name="bytes"/> (§12).</summary>
        public void Fit(OptimisationJob job, long bytes)
        {
            manager.Start(job.Key, job.Source, job.DisplayName, job.SourcePath, async (newJob, cancellationToken) =>
            {
                var basis = job.Result is { } result && File.Exists(result.BackupPath)
                    ? result with { OutputPath = job.CurrentPath ?? result.OutputPath }
                    : await service.DescribeAsync(job.SourcePath!, cancellationToken);
                var fitted = await service.AdjustAsync(
                    basis, new FileOptimisationRequest { TargetBytes = bytes }, p => newJob.Progress = p, cancellationToken);
                if (job.Source == JobSource.Clipboard)
                    await clipboard.PutResultAsync(fitted.OutputPath);
                return fitted;
            }, $"Fitting under {OptimisationJob.FormatBytes(bytes)}");
        }

        /// <summary>Formats a job's result can be converted to (not its own current format).</summary>
        public static IReadOnlyList<FileFormat> ConversionTargets(OptimisationJob job)
        {
            var kind = job.Result?.Kind ?? (job.SourcePath is { } source ? FileTypeSniffer.Detect(source).Kind() : MediaKind.Unknown);
            var current = job.Result?.OutputFormat;
            var targets = kind switch
            {
                MediaKind.Image => ImageConverter.Targets,
                MediaKind.Video => VideoConverter.Targets,
                _ => [],
            };
            // For a video, "MP4" means HEVC, so it's offered even when the result is already an (H.264) MP4.
            return targets.Where(t => t != current || (kind == MediaKind.Video && t == FileFormat.Mp4)).ToList();
        }

        public static bool CanConvert(OptimisationJob job) =>
            job.IsFinished && job.State != JobState.Failed && ConversionTargets(job).Count > 0
            && (job.Result is { BackupPath: var backup } && File.Exists(backup) || job.SourcePath is { } source && File.Exists(source));

        /// <summary>Converts a result (from its original) to another format, as a new job on the same card.</summary>
        public void Convert(OptimisationJob job, FileFormat target)
        {
            var label = target == FileFormat.Mp4 && job.Result?.Kind == MediaKind.Video
                ? "Converting to HEVC"
                : $"Converting to {target.ToString().ToUpperInvariant()}";

            manager.Start(job.Key, job.Source, job.DisplayName, job.SourcePath, async (newJob, cancellationToken) =>
            {
                // A result that was "already fully compressed" has no backup yet: start from the file as it is.
                var basis = job.Result is { } result && File.Exists(result.BackupPath)
                    ? result with { OutputPath = job.CurrentPath ?? result.OutputPath }
                    : await service.DescribeAsync(job.SourcePath!, cancellationToken);

                var converted = await service.ConvertAsync(basis, target, p => newJob.Progress = p, cancellationToken);
                if (job.Source == JobSource.Clipboard)
                    await clipboard.PutResultAsync(converted.OutputPath);
                return converted;
            }, label);
        }

        /// <summary>The next speed step: +0.25× below 2×, then +1×, up to 10× (project.md §9.4).</summary>
        public static double NextSpeedStep(double speed) => Math.Min(10, speed < 2 - 1e-9 ? speed + 0.25 : Math.Floor(speed) + 1);

        public static double CurrentSpeed(OptimisationJob job) =>
            job.State == JobState.Restored ? 1 : job.Result?.Speed ?? 1;

        /// <summary>"Scaling to 75% (1440×810)" for a job, when the original's size is known.</summary>
        public static string ScalingLabel(OptimisationJob? job, double scale)
        {
            var label = $"Scaling to {scale:P0}";
            if (job?.Result?.BackupPath is { } backup && ImageDecoding.TryReadSize(backup) is { } size)
                label += $" ({ImageResizer.ScaledSize(size, scale)})";
            return label;
        }
    }
}
