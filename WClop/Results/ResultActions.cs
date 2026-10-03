using System.Diagnostics;
using System.IO;
using WClop.Clipboard;
using WClop.Core.Cropping;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Video;
using WClop.Core.Logging;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Settings;

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
                MediaKind.Audio => FileOptimisationService.AudioConversionTargets,
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

        /// <summary>
        /// The format bar's chips for a result: what its kind converts to, never what it already is (for video and
        /// audio, by codec: <paramref name="codec"/> as ffprobe names it, or null if unknown).
        /// </summary>
        public static IReadOnlyList<FormatChoice> FormatChoicesFor(OptimisationJob job, string? codec)
        {
            if (!CanConvert(job))
                return [];
            var path = job.CurrentPath is { } current && File.Exists(current) ? current : job.SourcePath;
            var format = path is null ? job.Result?.OutputFormat ?? FileFormat.Unknown : FileTypeSniffer.Detect(path);
            // Conversions start from the original, so its kind decides (a video made into a GIF offers WebM and MP4).
            return FormatChoices.For(KindOf(job), format, codec);
        }

        /// <summary>The kind of file a job is about.</summary>
        public static MediaKind KindOf(OptimisationJob job) =>
            job.Result?.Kind ?? (job.CurrentPath ?? job.SourcePath) switch
            {
                { } path when File.Exists(path) => FileTypeSniffer.Detect(path).Kind(),
                { } path => FileFormats.FromExtension(path).Kind(),
                _ => MediaKind.Unknown,
            };

        /// <summary>
        /// Whether <see cref="Adjust"/> (the card's downscale and compression sliders) can redo this result: finished,
        /// not a conversion or crop (those start from the original and would lose their work), with an original to use.
        /// </summary>
        public static bool CanAdjust(OptimisationJob job) =>
            job.IsFinished && job.State is not (JobState.Failed or JobState.Cancelled or JobState.Skipped)
            && job.Result is not { IsConversion: true }
            && (job.Result is { BackupPath: var backup } && File.Exists(backup)
                || job.Source is JobSource.File or JobSource.DropZone && job.SourcePath is { } source && File.Exists(source)
                || job.Source == JobSource.Clipboard);

        /// <summary>The compression factor a result was made with (restoring resets it to the settings').</summary>
        public static int CurrentFactor(OptimisationJob job, AppSettings settings)
        {
            var fallback = KindOf(job) switch
            {
                MediaKind.Video => settings.Compression.VideoFactor,
                MediaKind.Audio => settings.Compression.AudioFactor,
                _ => settings.Compression.ImageFactor,
            };
            return job.State == JobState.Restored ? fallback : job.Result?.Factor ?? fallback;
        }

        /// <summary>Images and videos can be cropped, always from the original.</summary>
        public static bool CanCrop(OptimisationJob job) =>
            job.IsFinished && job.State is not (JobState.Failed or JobState.Cancelled or JobState.Skipped)
            && KindOf(job) is MediaKind.Image or MediaKind.Video
            && (job.Result is { BackupPath: var backup } && File.Exists(backup) || job.SourcePath is { } source && File.Exists(source));

        /// <summary>Crops a result from its original (the pipeline's crop, project.md §8.9), as a new job on the same card.</summary>
        public void Crop(OptimisationJob job, CropSpec crop)
        {
            manager.Start(job.Key, job.Source, job.DisplayName, job.SourcePath, async (newJob, cancellationToken) =>
            {
                var basis = job.Result is { } result && File.Exists(result.BackupPath)
                    ? result with { OutputPath = job.CurrentPath ?? result.OutputPath }
                    : await service.DescribeAsync(job.SourcePath!, cancellationToken);
                var cropped = await service.CropAsync(basis, crop, null, p => newJob.Progress = p, cancellationToken);
                if (job.Source == JobSource.Clipboard)
                    await clipboard.PutResultAsync(cropped.OutputPath);
                return cropped;
            }, $"Cropping to {crop}");
        }

        /// <summary>Renames a result's file (from the card's title). Returns false, with the reason on the card, if it can't.</summary>
        public async Task<bool> RenameAsync(OptimisationJob job, string name)
        {
            if (job.CurrentPath is not { } path || !File.Exists(path) || !job.IsFinished)
                return false;
            try
            {
                var renamed = await Task.Run(() => service.RenameFile(path, name));
                if (string.Equals(renamed, path, StringComparison.Ordinal))
                    return true;
                job.MarkMoved(path, renamed);
                // The clipboard still points at the old name.
                if (job.Source == JobSource.Clipboard)
                    await clipboard.PutResultAsync(renamed);
                Log.Info($"Renamed {Path.GetFileName(path)} to {Path.GetFileName(renamed)}");
                return true;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                job.Status = "Couldn't rename: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// "Edit with…" (project.md §16.4): opens the result in the editor set for its kind in Settings → Results, or
        /// asks with Windows' "Open with" dialog.
        /// </summary>
        public static void EditWith(OptimisationJob job, IntPtr owner, AppSettings settings)
        {
            if (job.CurrentPath is not { } path || !File.Exists(path))
                return;
            try
            {
                if (settings.ResultCards.EditorFor(KindOf(job)) is { } editor)
                    Process.Start(new ProcessStartInfo(editor, $"\"{path}\"") { UseShellExecute = true })?.Dispose();
                else
                    WindowInterop.ShowOpenWith(owner, path);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            {
                Log.Error($"Edit with failed for {Path.GetFileName(path)}", e);
                job.Status = "Couldn't open the editor: " + e.Message;
            }
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
