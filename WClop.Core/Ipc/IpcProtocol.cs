using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WClop.Core.Settings;

namespace WClop.Core.Ipc;

/// <summary>
/// The three channels, kept separate so a malformed message of one kind can never be read as another
/// (project.md §20.1, §26.1).
/// </summary>
public enum IpcChannel
{
    Optimise,
    Stop,
    Settings,
}

/// <summary>Where a request came from; Explorer and Send To requests are gathered so many files become a batch.</summary>
public enum IpcSource
{
    Cli,
    Explorer,
    SendTo,
}

public sealed record OptimiseCommand
{
    public required IReadOnlyList<string> Paths { get; init; }
    public IpcSource Source { get; init; } = IpcSource.Cli;

    /// <summary>A drop preset by name ("Aggressive", "Under 10 MB", …); null = the settings.</summary>
    public string? Preset { get; init; }

    public int? Factor { get; init; }
    public double? Scale { get; init; }
    public long? TargetBytes { get; init; }

    /// <summary>Convert to this format (by extension, e.g. "webp") instead of optimising.</summary>
    public string? ConvertTo { get; init; }

    public bool KeepOriginals { get; init; }

    /// <summary>Where outputs go; null = the settings (or next to the original with <see cref="KeepOriginals"/>).</summary>
    public OutputBehaviour? Output { get; init; }

    public bool AllowLarger { get; init; }

    /// <summary>Run this pipeline (a saved name, or pipeline text) on each file.</summary>
    public string? Pipeline { get; init; }

    /// <summary>With <see cref="Pipeline"/>: don't optimise first; null = the saved pipeline's setting.</summary>
    public bool? SkipOptimisation { get; init; }

    /// <summary>Open the batch window for these paths.</summary>
    public bool Batch { get; init; }

    /// <summary>Reply only when every file is done (the CLI waits; Explorer doesn't).</summary>
    public bool Wait { get; init; }
}

public sealed record FileOutcome(string Path, string? Output, long OldSize, long NewSize, string Status, bool Succeeded, bool Failed = false);

public sealed record OptimiseReply(IReadOnlyList<FileOutcome> Files, string? Message = null);

public sealed record StopReply(int Cancelled);

public sealed record SettingsCommand(string Action, string? Key = null, string? Value = null);

public sealed record SettingsReply(bool Ok, string? Value = null, string? Error = null);

public static class IpcNames
{
    /// <summary>
    /// Per-user pipe names (a short hash of the account SID), so two people signed in to the same machine never reach
    /// each other's WClop. The pipes are also created with <see cref="PipeOptions.CurrentUserOnly"/>.
    /// </summary>
    public static string PipeName(IpcChannel channel, string? instance = null)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..12];
        return $"WClop.{hash}{(instance is null ? "" : "." + instance)}.{channel.ToString().ToLowerInvariant()}";
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>Sends one JSON line and reads one JSON line back.</summary>
public static class IpcClient
{
    /// <summary>
    /// Returns null if the app isn't running (nothing listening within <paramref name="connectTimeout"/>).
    /// </summary>
    public static async Task<TReply?> SendAsync<TCommand, TReply>(
        IpcChannel channel, TCommand command, TimeSpan connectTimeout, CancellationToken cancellationToken = default,
        string? instance = null)
        where TReply : class
    {
        await using var pipe = new NamedPipeClientStream(".", IpcNames.PipeName(channel, instance), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }

        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(command, IpcNames.Json)).ConfigureAwait(false);
        var line = await new StreamReader(pipe, Encoding.UTF8).ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line is null ? null : JsonSerializer.Deserialize<TReply>(line, IpcNames.Json);
    }
}

/// <summary>
/// Serves one channel: every connection gets its own pipe instance, so many clients (e.g. one Explorer process per
/// selected file) can connect at once. One JSON line in, one out.
/// </summary>
public sealed class IpcServer(IpcChannel channel, Func<string, CancellationToken, Task<string>> handle, string? instance = null) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    public void Dispose() => _stop.Cancel();

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(IpcNames.PipeName(channel, instance), PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException e)
            {
                Logging.Log.Warn($"IPC ({channel}): {e.Message}");
                await Task.Delay(500).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                var line = await new StreamReader(pipe, Encoding.UTF8).ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is null)
                    return;
                var reply = await handle(line, _stop.Token).ConfigureAwait(false);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync(reply).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException)
            {
                Logging.Log.Warn($"IPC ({channel}): {e.Message}");
            }
        }
    }
}
