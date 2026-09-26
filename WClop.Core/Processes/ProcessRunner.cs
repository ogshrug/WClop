using System.Diagnostics;
using System.Text;

namespace WClop.Core.Processes;

public sealed record ProcessResult(
    string FileName,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Elapsed)
{
    public bool Succeeded => ExitCode == 0;

    public override string ToString() =>
        $"{System.IO.Path.GetFileName(FileName)} exited with {ExitCode} after {Elapsed.TotalMilliseconds:0} ms";
}

public sealed record ProcessRunOptions
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Kill the process (and its children) if it runs longer than this.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Called for each stdout line as it arrives, on a thread-pool thread.</summary>
    public Action<string>? OnStdOutLine { get; init; }

    /// <summary>Called for each stderr line as it arrives, on a thread-pool thread (ffmpeg reports progress here).</summary>
    public Action<string>? OnStdErrLine { get; init; }
}

/// <summary>A tool ran to completion but reported failure.</summary>
public sealed class ToolFailedException(ProcessResult result)
    : Exception($"{result}: {Tail(result.StdErr)}")
{
    public ProcessResult Result { get; } = result;

    private static string Tail(string text) => text.Length <= 500 ? text.Trim() : "…" + text[^500..].Trim();
}

/// <summary>
/// Runs external tools (project.md §21.3, §24).
/// <list type="bullet">
/// <item>stdout and stderr are read while the process runs, so a full pipe can't deadlock it.</item>
/// <item>Cancellation kills the whole process tree and throws <see cref="OperationCanceledException"/>,
/// so callers can tell "the user stopped it" apart from a failure.</item>
/// <item>A timeout kills the tree and throws <see cref="TimeoutException"/>.</item>
/// <item>A non-zero exit code is returned in the result, not thrown, by <see cref="RunAsync"/>.</item>
/// </list>
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProcessRunOptions();
        var args = arguments.ToList();

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        if (options.WorkingDirectory is not null)
            startInfo.WorkingDirectory = options.WorkingDirectory;
        if (options.Environment is not null)
        {
            foreach (var (key, value) in options.Environment)
                startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) => OnLine(e.Data, stdout, options.OnStdOutLine, stdoutClosed);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data, stderr, options.OnStdErrLine, stderrClosed);

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = options.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{System.IO.Path.GetFileName(fileName)} timed out after {options.Timeout!.Value.TotalSeconds:0.#} s");
        }

        await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);
        stopwatch.Stop();

        return new ProcessResult(fileName, args, process.ExitCode, Read(stdout), Read(stderr), stopwatch.Elapsed);
    }

    /// <summary>
    /// Runs the tool up to <paramref name="attempts"/> times until it succeeds.
    /// Throws the last <see cref="ToolFailedException"/> or <see cref="TimeoutException"/> if every attempt fails.
    /// Cancellation is never retried.
    /// </summary>
    public static async Task<ProcessResult> RunWithRetriesAsync(
        string fileName,
        IEnumerable<string> arguments,
        int attempts = 3,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        var args = arguments.ToList();

        Exception? lastError = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await RunAsync(fileName, args, options, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                    return result;
                lastError = new ToolFailedException(result);
            }
            catch (TimeoutException e)
            {
                lastError = e;
            }
        }

        throw lastError!;
    }

    /// <summary>
    /// Tries each argument set in order and returns the first that succeeds
    /// (used for ffmpeg's audio-mapping fallbacks, project.md §9.3 step 8).
    /// Throws the last failure if none succeed.
    /// </summary>
    public static async Task<ProcessResult> RunFirstSuccessfulAsync(
        string fileName,
        IReadOnlyList<IReadOnlyList<string>> argumentSets,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(argumentSets.Count);

        Exception? lastError = null;
        foreach (var args in argumentSets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await RunAsync(fileName, args, options, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                    return result;
                lastError = new ToolFailedException(result);
            }
            catch (TimeoutException e)
            {
                lastError = e;
            }
        }

        throw lastError!;
    }

    private static void OnLine(string? line, StringBuilder buffer, Action<string>? callback, TaskCompletionSource closed)
    {
        if (line is null)
        {
            closed.TrySetResult();
            return;
        }

        lock (buffer)
            buffer.AppendLine(line);
        callback?.Invoke(line);
    }

    private static string Read(StringBuilder buffer)
    {
        lock (buffer)
            return buffer.ToString();
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
