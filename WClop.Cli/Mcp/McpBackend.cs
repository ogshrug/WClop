using System.Text.Json;
using WClop.Core;
using WClop.Core.DropZone;
using WClop.Core.Images;
using WClop.Core.Ipc;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Cli.Mcp;

/// <summary>Where the MCP server finds WClop: the settings file and database, and which app instance's pipes (tests use their own).</summary>
internal sealed record McpEnvironment(string SettingsFile, string DatabasePath, string? IpcInstance = null)
{
    public static McpEnvironment Default => new(AppPaths.SettingsFile, OptimisationDatabase.DefaultPath);
}

/// <summary>
/// How the MCP tools reach WClop: the running app over the local API, marked as coming from an assistant so the app
/// applies <see cref="AssistantPolicy"/>; when the app isn't running, the work is done here under the same policy,
/// like the rest of the command line. Nothing here writes to the console: stdout carries the protocol.
/// </summary>
internal sealed class McpBackend(McpEnvironment environment)
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(800);

    private SettingsStore Store => new(environment.SettingsFile);

    // optimise, convert, run a pipeline

    public async Task<OptimiseReply> OptimiseAsync(OptimiseCommand command, CancellationToken cancellationToken)
    {
        command = command with { Source = IpcSource.Mcp, Wait = true, Batch = false };
        var builder = new OptimisationRequestBuilder(command);
        if (builder.Error is { } error)
            return new OptimiseReply([], error);

        var reply = await IpcClient.SendAsync<OptimiseCommand, OptimiseReply>(
            IpcChannel.Optimise, command, ConnectTimeout, cancellationToken, environment.IpcInstance).ConfigureAwait(false);
        return reply ?? await OptimiseLocallyAsync(command, builder, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OptimiseReply> OptimiseLocallyAsync(OptimiseCommand command, OptimisationRequestBuilder builder, CancellationToken cancellationToken)
    {
        var settings = Store.Load();
        var paths = new AppPaths(settings.Files.ResolveWorkDir());
        paths.EnsureCreated();
        var service = new FileOptimisationService(settings, paths, ToolLocator.CreateDefault(), new OptimisationDatabase(environment.DatabasePath));

        ResolvedPipeline? pipeline = null;
        PipelineLibrary? library = null;
        if (command.Pipeline is { } name)
        {
            library = new PipelineLibrary(settings, new PipelineRunner(service, settings));
            try
            {
                pipeline = library.Resolve(name);
            }
            catch (PipelineException e)
            {
                return new OptimiseReply([], e.Message);
            }

            if (AssistantPolicy.CheckRun(pipeline.Compiled, settings.Pipelines) is { } refused)
                return new OptimiseReply([], refused);
        }

        var existing = command.Paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0)
            return new OptimiseReply([], "None of those files exist");

        var request = builder.Build();
        var outcomes = new List<FileOutcome>();
        foreach (var file in DropInputs.ExpandMediaPaths(existing, DropInputs.OptimisableMedia))
        {
            var oldSize = new FileInfo(file).Length;
            try
            {
                FileOptimisationResult result;
                if (pipeline is not null)
                {
                    var outcome = await library!.Runner.RunFileAsync(pipeline.Name, pipeline.Compiled, file,
                        command.SkipOptimisation ?? pipeline.SkipOptimisation,
                        new PipelineContext { Origin = PipelineOrigin.Cli, AllowScripts = settings.Pipelines.AllowScriptsFromAssistants },
                        cancellationToken).ConfigureAwait(false);
                    if (outcome.Deleted)
                    {
                        outcomes.Add(new FileOutcome(file, null, oldSize, 0, "Sent to the Recycle Bin", true));
                        continue;
                    }

                    result = outcome.Result;
                }
                else
                {
                    result = builder.ConvertTo is { } target
                        ? await service.ConvertAsync(await service.DescribeAsync(file, cancellationToken).ConfigureAwait(false), target, null, cancellationToken)
                            .ConfigureAwait(false)
                        : await service.OptimiseAsync(file, request, null, cancellationToken).ConfigureAwait(false);
                }

                var status = result.MissedTarget ? "Couldn't reach the target; the smallest attempt was kept" : "Done";
                outcomes.Add(new FileOutcome(file, result.OutputPath, result.OldSize, result.NewSize, status, true));
            }
            catch (OptimisationException e)
            {
                // Expected outcomes like "already fully compressed": not failures.
                outcomes.Add(new FileOutcome(file, null, oldSize, 0, e.Message, false));
            }
            catch (Exception e) when (e is ToolFailedException or ToolNotFoundException or IOException or UnauthorizedAccessException)
            {
                outcomes.Add(new FileOutcome(file, null, oldSize, 0, e.Message, false, Failed: true));
            }
        }

        var missing = command.Paths.Count - existing.Count;
        return new OptimiseReply(outcomes, missing > 0 ? $"{missing} path(s) didn't exist" : "WClop isn't running, so this was done without it (no result cards)");
    }

    // stop

    /// <summary>False if the app isn't running.</summary>
    public async Task<bool> StopAsync(CancellationToken cancellationToken) =>
        await IpcClient.SendAsync<object, StopReply>(IpcChannel.Stop, new { }, ConnectTimeout, cancellationToken, environment.IpcInstance)
            .ConfigureAwait(false) is not null;

    // settings

    /// <summary><c>get</c> (all settings without a key), <c>set</c> or <c>setJson</c>; through the app, else the settings file.</summary>
    public async Task<SettingsReply> SettingsAsync(string action, string? key, string? value, CancellationToken cancellationToken)
    {
        var command = new SettingsCommand(action, key, value, IpcSource.Mcp);
        var reply = await IpcClient.SendAsync<SettingsCommand, SettingsReply>(
            IpcChannel.Settings, command, ConnectTimeout, cancellationToken, environment.IpcInstance).ConfigureAwait(false);
        return reply ?? SettingsOffline(command);
    }

    /// <summary>With the app closed: the settings file, under the same rules the app applies.</summary>
    private SettingsReply SettingsOffline(SettingsCommand command)
    {
        var store = Store;
        var settings = store.Load();
        try
        {
            if (command.Action is "set" or "setJson")
            {
                if (AssistantPolicy.CheckSettingsWrite(settings, command.Action, command.Key!, command.Value!) is { } refused)
                    return new SettingsReply(false, Error: refused);
                if (command.Action == "set")
                    SettingsPath.Set(settings, command.Key!, command.Value!);
                else
                    SettingsPath.SetJson(settings, command.Key!, command.Value!);
                store.Save(settings);
            }

            return new SettingsReply(true, command.Key is null ? SettingsStore.Snapshot(settings) : SettingsPath.Get(settings, command.Key));
        }
        catch (ArgumentException e)
        {
            return new SettingsReply(false, Error: e.Message);
        }
    }

    public async Task<PipelineSettings> LoadPipelinesAsync(CancellationToken cancellationToken)
    {
        var reply = await SettingsAsync("get", "pipelines", null, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok || reply.Value is null)
            throw new InvalidOperationException(reply.Error ?? "Couldn't read the pipelines");
        return JsonSerializer.Deserialize<PipelineSettings>(reply.Value, SettingsStore.JsonOptions) ?? new PipelineSettings();
    }

    /// <summary>Saves the pipelines; the app (or, without it, this server) refuses changes assistants may not make.</summary>
    public async Task SavePipelinesAsync(PipelineSettings pipelines, CancellationToken cancellationToken)
    {
        var reply = await SettingsAsync("setJson", "pipelines", JsonSerializer.Serialize(pipelines, SettingsStore.JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        if (!reply.Ok)
            throw new InvalidOperationException(reply.Error ?? "Couldn't save the pipelines");
    }
}
