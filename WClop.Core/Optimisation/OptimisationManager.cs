using WClop.Core.Images;
using WClop.Core.Logging;

namespace WClop.Core.Optimisation;

/// <summary>A job decided there was nothing to do, and nothing worth showing (e.g. a duplicate clipboard event).</summary>
public sealed class JobSkippedException(string reason) : OptimisationException(reason);

/// <summary>
/// Runs and tracks jobs (Clop's Optimisation Manager, project.md §1, §5).
/// Work for the same key is serialised: a new request cancels the running one and waits for it to stop,
/// so two passes never race on the same files.
/// </summary>
public sealed class OptimisationManager
{
    private const int MaxRecentlyRemoved = 10;

    private readonly object _gate = new();
    private readonly Dictionary<string, (OptimisationJob Job, Task Task)> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<OptimisationJob> _recentlyRemoved = new();

    /// <summary>
    /// File and drop-zone jobs share a limited number of slots, so dropping a big folder doesn't start dozens of
    /// tools at once. Clipboard jobs never wait behind them.
    /// </summary>
    private readonly SemaphoreSlim _workSlots;

    public OptimisationManager(int? maxConcurrentFileJobs = null)
    {
        MaxConcurrentFileJobs = Math.Max(1, maxConcurrentFileJobs ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
        _workSlots = new SemaphoreSlim(MaxConcurrentFileJobs);
    }

    public int MaxConcurrentFileJobs { get; }

    private static bool UsesWorkSlot(OptimisationJob job) => job.Source is JobSource.File or JobSource.DropZone;

    /// <summary>Raised on the calling thread when a job is created, before it starts working.</summary>
    public event Action<OptimisationJob>? JobStarted;

    /// <summary>Raised on a thread-pool thread when a job reaches a final state.</summary>
    public event Action<OptimisationJob>? JobFinished;

    /// <summary>The most recent job; hotkeys act on it (Phase 5).</summary>
    public OptimisationJob? Current { get; private set; }

    /// <summary>The job whose result the mouse is over, set by the UI.</summary>
    public OptimisationJob? Hovered { get; set; }

    public OptimisationJob Start(
        string key,
        JobSource source,
        string displayName,
        string? sourcePath,
        Func<OptimisationJob, CancellationToken, Task<FileOptimisationResult>> work,
        string status = "Optimising",
        bool visible = true)
    {
        var job = new OptimisationJob(key, source, displayName, sourcePath, status, visible);
        lock (_gate)
        {
            Task? previous = null;
            if (_inFlight.TryGetValue(key, out var running))
            {
                running.Job.Cancel();
                previous = running.Task;
            }

            var task = RunAsync(job, previous, work);
            _inFlight[key] = (job, task);
            Current = job;
        }

        JobStarted?.Invoke(job);
        return job;
    }

    /// <summary>Whether any job is still working (an automatic update waits for this).</summary>
    public bool HasRunningJobs
    {
        get
        {
            lock (_gate)
                return _inFlight.Count > 0;
        }
    }

    public bool IsRunning(string key)
    {
        lock (_gate)
            return _inFlight.ContainsKey(key);
    }

    /// <summary>Records a dismissed job so it can be brought back.</summary>
    public void Removed(OptimisationJob job)
    {
        lock (_gate)
        {
            _recentlyRemoved.AddFirst(job);
            while (_recentlyRemoved.Count > MaxRecentlyRemoved)
                _recentlyRemoved.RemoveLast();
            if (Hovered == job)
                Hovered = null;
        }
    }

    public OptimisationJob? TakeLastRemoved()
    {
        lock (_gate)
        {
            var job = _recentlyRemoved.First?.Value;
            if (job is not null)
                _recentlyRemoved.RemoveFirst();
            return job;
        }
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (var (job, _) in _inFlight.Values)
                job.Cancel();
        }
    }

    private async Task RunAsync(
        OptimisationJob job, Task? previous, Func<OptimisationJob, CancellationToken, Task<FileOptimisationResult>> work)
    {
        // Leave the caller's thread before doing anything, and let a superseded job finish stopping first.
        await Task.Yield();
        if (previous is not null)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
                // Its outcome is recorded on its own job.
            }
        }

        var holdsSlot = false;
        try
        {
            job.CancellationToken.ThrowIfCancellationRequested();
            if (UsesWorkSlot(job))
            {
                if (!_workSlots.Wait(0))
                {
                    var status = job.Status;
                    job.Status = "Queued";
                    await _workSlots.WaitAsync(job.CancellationToken).ConfigureAwait(false);
                    job.Status = status;
                }

                holdsSlot = true;
            }

            var result = await work(job, job.CancellationToken).ConfigureAwait(false);
            job.Succeed(result);
        }
        catch (OperationCanceledException)
        {
            job.Finish(JobState.Cancelled, "Cancelled");
        }
        catch (NotSmallerException)
        {
            job.Finish(JobState.NotSmaller, "Already fully compressed");
        }
        catch (Exception e) when (e is JobSkippedException or AlreadyOptimisedException)
        {
            job.Finish(JobState.Skipped, e.Message);
        }
        catch (OptimisationException e)
        {
            job.Finish(JobState.Failed, e.Message);
        }
        catch (Exception e)
        {
            Log.Error($"Job {job.DisplayName} failed", e);
            job.Finish(JobState.Failed, e.Message);
        }
        finally
        {
            if (holdsSlot)
                _workSlots.Release();
            lock (_gate)
            {
                if (_inFlight.TryGetValue(job.Key, out var entry) && entry.Job == job)
                    _inFlight.Remove(job.Key);
            }
        }

        if (job.State != JobState.Succeeded)
            Log.Info($"Job {job.DisplayName} [{job.Key}]: {job.State} ({job.Status})");
        JobFinished?.Invoke(job);
    }
}
