using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WClop.Core.DropZone;
using WClop.Core.Images;
using WClop.Core.Logging;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Core.Batch;

public enum BatchItemState
{
    Waiting,
    Working,
    Done,
    Skipped,
    Failed,
    Cancelled,
    Restored,
}

/// <summary>One file in a batch; observable so the batch window can bind to it (changes arrive on any thread).</summary>
public sealed class BatchItem(string path, string relativePath, long size) : INotifyPropertyChanged
{
    private BatchItemState _state = BatchItemState.Waiting;
    private string _status = "Waiting";
    private long? _newSize;
    private double _progress;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; } = path;
    public string RelativePath { get; } = relativePath;
    public long OldSize { get; } = size;
    public FileFormat Format { get; } = FileFormats.FromExtension(path);
    public string Type => Format.ToString().ToUpperInvariant();
    public string? BackupPath { get; internal set; }
    public string? OutputPath { get; internal set; }

    public BatchItemState State
    {
        get => _state;
        internal set => Set(ref _state, value);
    }

    public string Status
    {
        get => _status;
        internal set => Set(ref _status, value);
    }

    public long? NewSize
    {
        get => _newSize;
        internal set
        {
            if (Set(ref _newSize, value))
            {
                Notify(nameof(Saved));
                Notify(nameof(SavedText));
                Notify(nameof(NewSizeText));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        internal set => Set(ref _progress, value);
    }

    public long Saved => NewSize is { } size ? OldSize - size : 0;
    public string OldSizeText => OptimisationJob.FormatBytes(OldSize);
    public string NewSizeText => NewSize is { } size ? OptimisationJob.FormatBytes(size) : "";
    public string SavedText => NewSize is { } size && OldSize > 0 ? $"−{1 - (double)size / OldSize:P0}" : "";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record BatchOptions
{
    public DropPreset Preset { get; init; } = DropPresets.All[0];

    /// <summary>Save optimised copies next to the originals instead of replacing them.</summary>
    public bool KeepOriginals { get; init; }

    public int MaxConcurrent { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
}

/// <summary>
/// Optimises many files at once (project.md §18.4): everything is backed up up front into its own
/// <c>batch-backups/batch-…</c> folder (never cleaned up automatically), then processed with bounded concurrency
/// (videos one at a time, since each already uses the whole CPU or GPU). Can be stopped and restored as a whole.
/// </summary>
public sealed class BatchJob
{
    private readonly FileOptimisationService _service;
    private readonly CancellationTokenSource _cancellation = new();

    public BatchJob(FileOptimisationService service, IReadOnlyList<BatchItem> items)
    {
        _service = service;
        Items = items;
        Id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        BackupFolder = Path.Combine(service.Paths.BatchBackups, "batch-" + Id);
    }

    public string Id { get; }
    public IReadOnlyList<BatchItem> Items { get; }
    public string BackupFolder { get; }

    /// <summary>
    /// The media files in what was dropped: files as they are, folders searched (subfolders too if asked).
    /// Relative paths start with the dropped folder's name, so backups keep the structure.
    /// </summary>
    public static IReadOnlyList<BatchItem> Collect(IEnumerable<string> dropped, bool includeSubfolders, IReadOnlySet<FileFormat>? formats = null)
    {
        formats ??= DropInputs.OptimisableMedia;
        var items = new List<BatchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string file, string relative)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.') || !formats.Contains(FileFormats.FromExtension(file)) || !seen.Add(Path.GetFullPath(file)))
                return;
            try
            {
                var info = new FileInfo(file);
                if (!Watching.WatchFilters.HasSkippableAttributes(info.Attributes))
                    items.Add(new BatchItem(info.FullName, relative, info.Length));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var path in dropped)
        {
            if (Directory.Exists(path))
            {
                var root = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\')) ?? path;
                try
                {
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = includeSubfolders,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                    };
                    foreach (var file in Directory.EnumerateFiles(path, "*", options).Order(StringComparer.OrdinalIgnoreCase))
                        Add(file, Path.GetRelativePath(root, file));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
            else if (File.Exists(path))
            {
                Add(path, Path.GetFileName(path));
            }
        }

        return items;
    }

    /// <summary>Copies every original into the batch backup folder before anything is changed.</summary>
    public async Task BackUpAsync(Action<int>? onBackedUp = null)
    {
        var done = 0;
        await Task.Run(() =>
        {
            foreach (var item in Items)
            {
                var target = Path.Combine(BackupFolder, item.RelativePath);
                // Two dropped folders with the same name would collide: keep both.
                for (var n = 2; File.Exists(target); n++)
                    target = Path.Combine(BackupFolder, $"{n}", item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(item.Path, target);
                item.BackupPath = target;
                onBackedUp?.Invoke(++done);
            }
        }).ConfigureAwait(false);
    }

    public void Stop() => _cancellation.Cancel();

    public async Task RunAsync(BatchOptions options)
    {
        var slots = new SemaphoreSlim(options.MaxConcurrent);
        var videoSlot = new SemaphoreSlim(1);
        var request = options.Preset.Apply(new FileOptimisationRequest
        {
            Force = true,
            Behaviour = options.KeepOriginals ? OutputBehaviour.SameFolder : OutputBehaviour.InPlace,
        });

        await Task.WhenAll(Items.Select(async item =>
        {
            var isVideo = item.Format.Kind() == MediaKind.Video;
            try
            {
                await slots.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                item.State = BatchItemState.Cancelled;
                item.Status = "Stopped";
                return;
            }

            try
            {
                if (isVideo)
                    await videoSlot.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                try
                {
                    await ProcessAsync(item, request).ConfigureAwait(false);
                }
                finally
                {
                    if (isVideo)
                        videoSlot.Release();
                }
            }
            catch (OperationCanceledException)
            {
                item.State = BatchItemState.Cancelled;
                item.Status = "Stopped";
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
    }

    private async Task ProcessAsync(BatchItem item, FileOptimisationRequest request)
    {
        item.State = BatchItemState.Working;
        item.Status = "Optimising";
        try
        {
            var result = await _service.OptimiseAsync(item.Path, request, p => item.Progress = p, _cancellation.Token).ConfigureAwait(false);
            item.OutputPath = result.OutputPath;
            item.NewSize = result.NewSize;
            item.Progress = 1;
            item.State = BatchItemState.Done;
            item.Status = result switch
            {
                { MissedTarget: true } => "Smallest possible",
                { OutputFormat: var f } when f != result.InputFormat => $"Done ({f.ToString().ToUpperInvariant()})",
                _ => "Done",
            };
        }
        catch (NotSmallerException)
        {
            item.State = BatchItemState.Skipped;
            item.Status = "Already compressed";
        }
        catch (OptimisationException e)
        {
            item.State = BatchItemState.Skipped;
            item.Status = e.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Error($"Batch {Id}: {item.Path} failed", e);
            item.State = BatchItemState.Failed;
            item.Status = e.Message;
        }
    }

    /// <summary>
    /// Puts every original back from the batch backup, removing converted or copied outputs; restored files are marked
    /// so they aren't optimised again automatically. The batch backup folder itself is kept.
    /// </summary>
    public async Task<int> RestoreAllAsync()
    {
        var restored = 0;
        await Task.Run(() =>
        {
            foreach (var item in Items.Where(i => i.BackupPath is not null && File.Exists(i.BackupPath)))
            {
                try
                {
                    if (item.OutputPath is { } output && !string.Equals(output, item.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(output))
                    {
                        File.Delete(output);
                        _service.Markers.Clear(output);
                    }

                    File.Copy(item.BackupPath!, item.Path, overwrite: true);
                    _service.Markers.Set(item.Path, MarkerStatus.Original);
                    item.NewSize = null;
                    item.State = BatchItemState.Restored;
                    item.Status = "Restored";
                    restored++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    item.Status = "Restore failed: " + e.Message;
                }
            }
        }).ConfigureAwait(false);
        return restored;
    }
}
