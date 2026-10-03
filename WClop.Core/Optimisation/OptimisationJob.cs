using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WClop.Core.Optimisation;

public enum JobSource
{
    Clipboard,
    File,
    DropZone,
    Cli,
}

public enum JobState
{
    Running,
    Succeeded,

    /// <summary>Nothing to gain: the output wasn't smaller.</summary>
    NotSmaller,

    /// <summary>Nothing done and nothing worth showing (duplicate event, already optimised).</summary>
    Skipped,

    Failed,
    Cancelled,
    Restored,
}

/// <summary>
/// One item being processed (Clop's Optimiser, project.md §1). Observable so the result UI can bind to it;
/// change notifications may arrive on any thread.
/// </summary>
public sealed class OptimisationJob : INotifyPropertyChanged
{
    private readonly CancellationTokenSource _cancellation = new();
    private JobState _state = JobState.Running;
    private string _status;
    private double? _progress;
    private FileOptimisationResult? _result;
    private string? _currentPath;
    private string _displayName;
    private string? _sourcePath;
    private bool _isVisible;

    internal OptimisationJob(string key, JobSource source, string displayName, string? sourcePath, string status, bool visible)
    {
        _isVisible = visible;
        Key = key;
        Source = source;
        _displayName = displayName;
        _sourcePath = sourcePath;
        _currentPath = sourcePath;
        _status = status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Jobs with the same key never run concurrently (a file path, or "clipboard").</summary>
    public string Key { get; }

    public JobSource Source { get; }
    public string DisplayName
    {
        get => _displayName;
        private set => Set(ref _displayName, value);
    }

    public string? SourcePath
    {
        get => _sourcePath;
        private set => Set(ref _sourcePath, value);
    }
    public DateTime Created { get; } = DateTime.Now;

    public JobState State
    {
        get => _state;
        private set => Set(ref _state, value, alsoNotify: nameof(IsFinished));
    }

    public bool IsFinished => State != JobState.Running;

    /// <summary>
    /// False while a job is still deciding whether there's anything to do (e.g. a watched file that's still
    /// being written and may turn out too small), so the UI doesn't flash a card that then disappears.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    /// <summary>What's happening, for the UI: "Optimising", "Already fully compressed", an error…</summary>
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    /// <summary>0–1, or null for indeterminate.</summary>
    public double? Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public FileOptimisationResult? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    /// <summary>The file the result refers to right now (the output, or the restored original).</summary>
    public string? CurrentPath
    {
        get => _currentPath;
        private set => Set(ref _currentPath, value);
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void Succeed(FileOptimisationResult result)
    {
        Result = result;
        CurrentPath = result.OutputPath;
        Progress = 1;
        Status = result switch
        {
            { TargetBytes: { } target, MissedTarget: true } => $"Couldn't get under {FormatBytes(target)}: this is the smallest",
            { TargetBytes: { } target } => $"Fits under {FormatBytes(target)}",
            { Crop: { } crop } => $"Cropped to {crop}",
            { IsConversion: true } => "Converted",
            _ => "Optimised",
        };
        State = JobState.Succeeded;
    }

    internal void Finish(JobState state, string status)
    {
        Progress = null;
        Status = status;
        State = state;
    }

    public void MarkRestored(string restoredPath)
    {
        CurrentPath = restoredPath;
        Status = "Restored original";
        State = JobState.Restored;
    }

    /// <summary>
    /// The result's file was renamed (from the card): the job, its result and its name follow, so later
    /// adjustments and restores act on the file where it is now.
    /// </summary>
    public void MarkMoved(string oldPath, string newPath)
    {
        static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        if (Result is { } result && Same(result.OutputPath, oldPath))
            Result = result with { OutputPath = newPath };
        if (Same(SourcePath, oldPath))
            SourcePath = newPath;
        DisplayName = Path.GetFileName(newPath);
        if (Same(CurrentPath, oldPath))
            CurrentPath = newPath;
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.##} MB",
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "", string? alsoNotify = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (alsoNotify is not null)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(alsoNotify));
    }
}
