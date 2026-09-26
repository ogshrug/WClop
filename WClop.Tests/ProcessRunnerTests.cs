using System.Diagnostics;
using WClop.Core.Processes;

namespace WClop.Tests;

public class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static string[] CmdArgs(string command) => ["/d", "/c", command];

    [Fact]
    public async Task CapturesStdOutAndStdErr()
    {
        var result = await ProcessRunner.RunAsync(Cmd, CmdArgs("echo out& echo err 1>&2"));

        Assert.True(result.Succeeded);
        Assert.Equal("out", result.StdOut.Trim());
        Assert.Equal("err", result.StdErr.Trim());
    }

    [Fact]
    public async Task NonZeroExitIsReturnedNotThrown()
    {
        var result = await ProcessRunner.RunAsync(Cmd, CmdArgs("exit 3"));

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task OutputLargerThanPipeBufferDoesNotDeadlock()
    {
        // ~200 KB, well past the 4 KB–64 KB pipe buffer.
        var result = await ProcessRunner.RunAsync(
            Cmd,
            CmdArgs("for /L %i in (1,1,20000) do @echo line %i"),
            new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(30) });

        Assert.True(result.Succeeded);
        Assert.Contains("line 20000", result.StdOut);
    }

    [Fact]
    public async Task LineCallbacksReceiveOutput()
    {
        var lines = new List<string>();
        await ProcessRunner.RunAsync(
            Cmd,
            CmdArgs("echo one 1>&2& echo two 1>&2"),
            new ProcessRunOptions { OnStdErrLine = line => { lock (lines) lines.Add(line.Trim()); } });

        Assert.Equal(["one", "two"], lines);
    }

    [Fact]
    public async Task CancellationKillsProcessAndThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync(Cmd, CmdArgs("ping -n 30 127.0.0.1 >nul"), cancellationToken: cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TimeoutThrowsTimeoutException()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRunner.RunAsync(
                Cmd,
                CmdArgs("ping -n 30 127.0.0.1 >nul"),
                new ProcessRunOptions { Timeout = TimeSpan.FromMilliseconds(300) }));
    }

    [Fact]
    public async Task RetriesUntilSuccess()
    {
        // Fails until the marker file exists; the first run creates it.
        var markerName = "wclop-retry-" + Guid.NewGuid().ToString("N");
        var marker = Path.Combine(Path.GetTempPath(), markerName);
        try
        {
            var result = await ProcessRunner.RunWithRetriesAsync(
                Cmd,
                CmdArgs($"if exist {markerName} (exit 0) else (type nul > {markerName} & exit 1)"),
                attempts: 3,
                new ProcessRunOptions { WorkingDirectory = Path.GetTempPath() });

            Assert.True(result.Succeeded);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task RetriesExhaustedThrowsToolFailed()
    {
        var error = await Assert.ThrowsAsync<ToolFailedException>(() =>
            ProcessRunner.RunWithRetriesAsync(Cmd, CmdArgs("echo broken 1>&2& exit 2"), attempts: 2));

        Assert.Equal(2, error.Result.ExitCode);
        Assert.Contains("broken", error.Message);
    }

    [Fact]
    public async Task FirstSuccessfulArgumentSetWins()
    {
        var result = await ProcessRunner.RunFirstSuccessfulAsync(
            Cmd,
            [CmdArgs("exit 1"), CmdArgs("echo second"), CmdArgs("echo third")]);

        Assert.Equal("second", result.StdOut.Trim());
    }
}
