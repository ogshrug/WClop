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

namespace WClop.Cli;

/// <summary><c>wclop pipeline …</c> (project.md §20.2): manage, check and run pipelines.</summary>
internal static class PipelineCommands
{
    public const string Usage = """
          wclop pipeline list                     Saved pipelines and where they're attached
          wclop pipeline show <name>
          wclop pipeline add <name> "<pipeline>" [--skip-optimise] [--hide] [--no-dropzone]
                                                  Save (or replace) a pipeline
          wclop pipeline delete <name>
          wclop pipeline check "<pipeline>"       Check a pipeline for mistakes
          wclop pipeline run <name or "<pipeline>"> <file or folder>... [--skip-optimise | --optimise-first] [--local]
          wclop pipeline attach <name> clipboard|dropzone|folder <path> [--kind image,video,pdf,audio]
                                                  Run it automatically for files from there
          wclop pipeline detach <name> [clipboard|dropzone|folder <path>]
          wclop pipeline prompt                   The whole language, for an AI assistant to write pipelines
        """;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(800);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var action = args.FirstOrDefault()?.ToLowerInvariant();
        var rest = args.Length > 1 ? args[1..] : [];
        switch (action)
        {
            case "prompt":
                Console.WriteLine(PipelineCatalog.Prompt());
                return 0;

            case "check" when rest.Length >= 1:
                if (!PipelineCatalog.TryCompile(string.Join(' ', rest), out var compiled, out var error))
                    return Fail(error!);
                Console.WriteLine("OK: " + string.Join(" -> ", compiled!.Steps));
                return 0;

            case "list":
            {
                var (pipelines, _) = await LoadAsync(cancellationToken);
                if (pipelines.Saved.Count == 0)
                {
                    Console.WriteLine("No saved pipelines. Add one with: wclop pipeline add <name> \"<pipeline>\"");
                    return 0;
                }

                foreach (var saved in pipelines.Saved)
                {
                    var flags = new List<string>();
                    if (saved.SkipOptimisation) flags.Add("skip optimisation");
                    if (saved.HideResult) flags.Add("hide result");
                    if (!PipelineCatalog.TryCompile(saved.Script, out _, out var problem)) flags.Add("ERROR: " + problem);
                    Console.WriteLine($"{saved.Name}{(flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : "")}");
                    Console.WriteLine("  " + saved.Script.ReplaceLineEndings(" "));
                    foreach (var attachment in pipelines.Attached.Where(a => a.Pipeline.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine("  runs on: " + Describe(attachment));
                }

                return 0;
            }

            case "show" when rest.Length >= 1:
            {
                var (pipelines, _) = await LoadAsync(cancellationToken);
                if (pipelines.Find(rest[0]) is not { } saved)
                    return Fail($"No pipeline called '{rest[0]}'");
                Console.WriteLine(saved.Script);
                return 0;
            }

            case "add" when rest.Length >= 2:
            {
                var name = rest[0];
                var script = string.Join(' ', rest[1..].Where(a => !a.StartsWith("--", StringComparison.Ordinal)));
                if (!PipelineCatalog.TryCompile(script, out _, out var problem))
                    return Fail(problem!);

                var (pipelines, viaApp) = await LoadAsync(cancellationToken);
                var saved = pipelines.Find(name) ?? new SavedPipeline { Name = name };
                if (!pipelines.Saved.Contains(saved))
                    pipelines.Saved.Add(saved);
                saved.Script = script;
                saved.SkipOptimisation = rest.Contains("--skip-optimise") || rest.Contains("--skip-optimize");
                saved.HideResult = rest.Contains("--hide");
                saved.ShowInDropZone = !rest.Contains("--no-dropzone");
                await SaveAsync(pipelines, viaApp, cancellationToken);
                Console.WriteLine($"Saved {saved.Name}.");
                return 0;
            }

            case "delete" when rest.Length >= 1:
            {
                var (pipelines, viaApp) = await LoadAsync(cancellationToken);
                if (pipelines.Find(rest[0]) is not { } saved)
                    return Fail($"No pipeline called '{rest[0]}'");
                pipelines.Saved.Remove(saved);
                pipelines.Attached.RemoveAll(a => a.Pipeline.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
                await SaveAsync(pipelines, viaApp, cancellationToken);
                Console.WriteLine($"Deleted {saved.Name}.");
                return 0;
            }

            case "attach" when rest.Length >= 2:
            case "detach" when rest.Length >= 1:
                return await AttachAsync(action == "attach", rest, cancellationToken);

            case "run" when rest.Length >= 2:
                return await RunPipelineAsync(rest, cancellationToken);

            default:
                return Fail("Usage:\n" + Usage);
        }
    }

    private static async Task<int> AttachAsync(bool attach, string[] args, CancellationToken cancellationToken)
    {
        var (pipelines, viaApp) = await LoadAsync(cancellationToken);
        if (pipelines.Find(args[0]) is not { } saved)
            return Fail($"No pipeline called '{args[0]}'");

        PipelineTrigger? trigger = args.ElementAtOrDefault(1)?.ToLowerInvariant() switch
        {
            null => null,
            "clipboard" => PipelineTrigger.Clipboard,
            "dropzone" or "drop-zone" => PipelineTrigger.DropZone,
            "folder" => PipelineTrigger.Folder,
            var other => throw new ArgumentException($"'{other}' isn't clipboard, dropzone or folder"),
        };
        var folder = trigger == PipelineTrigger.Folder ? args.ElementAtOrDefault(2) : null;
        if (trigger == PipelineTrigger.Folder && folder is null)
            return Fail("Which folder? e.g. wclop pipeline attach web folder \"%USERPROFILE%\\Pictures\\Screenshots\"");
        var portableFolder = folder is null ? null : PortablePath.Contract(Path.GetFullPath(folder));

        bool Same(PipelineAttachment a) =>
            a.Pipeline.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)
            && (trigger is null || a.Trigger == trigger)
            && (portableFolder is null || string.Equals(a.Folder, portableFolder, StringComparison.OrdinalIgnoreCase));

        if (!attach)
        {
            var removed = pipelines.Attached.RemoveAll(Same);
            await SaveAsync(pipelines, viaApp, cancellationToken);
            Console.WriteLine(removed == 0 ? "It wasn't attached there." : $"Detached {saved.Name} ({removed}).");
            return 0;
        }

        if (trigger is null)
            return Fail("Attach to what? clipboard, dropzone or folder <path>");
        var kindsIndex = Array.FindIndex(args, a => a.Equals("--kind", StringComparison.OrdinalIgnoreCase));
        var kinds = kindsIndex >= 0 && kindsIndex + 1 < args.Length
            ? args[kindsIndex + 1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];

        pipelines.Attached.RemoveAll(Same);
        var attachment = new PipelineAttachment { Pipeline = saved.Name, Trigger = trigger.Value, Folder = portableFolder, Kinds = kinds };
        pipelines.Attached.Add(attachment);
        await SaveAsync(pipelines, viaApp, cancellationToken);
        Console.WriteLine($"{saved.Name} now runs on {Describe(attachment)}.");
        if (trigger == PipelineTrigger.Folder)
            Console.WriteLine("(It only runs for folders WClop is watching; add the folder under Settings → Watched folders.)");
        return 0;
    }

    private static string Describe(PipelineAttachment a)
    {
        var kinds = a.Kinds.Count > 0 ? $" ({string.Join(", ", a.Kinds)} only)" : "";
        return a.Trigger switch
        {
            PipelineTrigger.Clipboard => "the clipboard",
            PipelineTrigger.DropZone => "the drop zone",
            _ => $"folder {a.Folder}",
        } + kinds;
    }

    private static async Task<int> RunPipelineAsync(string[] args, CancellationToken cancellationToken)
    {
        var pipeline = args[0];
        var local = args.Contains("--local");
        bool? skip = args.Contains("--skip-optimise") || args.Contains("--skip-optimize") ? true
            : args.Contains("--optimise-first") || args.Contains("--optimize-first") ? false
            : null;
        var files = args[1..].Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(Path.GetFullPath).ToList();

        if (!local)
        {
            var reply = await IpcClient.SendAsync<OptimiseCommand, OptimiseReply>(IpcChannel.Optimise,
                new OptimiseCommand { Paths = files, Pipeline = pipeline, SkipOptimisation = skip, Wait = true }, ConnectTimeout, cancellationToken);
            if (reply is not null)
                return Program.Print(reply);
        }

        // Not running: do it here.
        var settings = new SettingsStore(AppPaths.SettingsFile).Load();
        var paths = new AppPaths(settings.Files.ResolveWorkDir());
        paths.EnsureCreated();
        var service = new FileOptimisationService(
            settings, paths, ToolLocator.CreateDefault(), new OptimisationDatabase(OptimisationDatabase.DefaultPath));
        var library = new PipelineLibrary(settings, new PipelineRunner(service, settings));

        ResolvedPipeline resolved;
        try
        {
            resolved = library.Resolve(pipeline);
        }
        catch (PipelineException e)
        {
            return Fail(e.Message);
        }

        var failures = 0;
        foreach (var file in DropInputs.ExpandMediaPaths(files, DropInputs.OptimisableMedia))
        {
            try
            {
                var outcome = await library.Runner.RunFileAsync(resolved.Name, resolved.Compiled, file, skip ?? resolved.SkipOptimisation,
                    new PipelineContext { Origin = PipelineOrigin.Cli }, cancellationToken);
                var result = outcome.Result;
                Console.WriteLine(outcome.Deleted
                    ? $"{file}: sent to the Recycle Bin"
                    : $"{file}: {Program.FormatBytes(result.OldSize)} → {Program.FormatBytes(result.NewSize)}\n  → {result.OutputPath}");
                foreach (var line in outcome.Log)
                    Console.WriteLine("    " + line);
            }
            catch (OptimisationException e)
            {
                Console.WriteLine($"{file}: {e.Message}");
            }
            catch (Exception e) when (e is ToolFailedException or ToolNotFoundException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{file}: {e.Message}");
                failures++;
            }
        }

        return failures == 0 ? 0 : 1;
    }

    // The pipelines live in the settings: through the app when it's running, else the settings file.

    private static async Task<(PipelineSettings Pipelines, bool ViaApp)> LoadAsync(CancellationToken cancellationToken)
    {
        var reply = await IpcClient.SendAsync<SettingsCommand, SettingsReply>(
            IpcChannel.Settings, new SettingsCommand("get", "pipelines"), ConnectTimeout, cancellationToken);
        if (reply is { Ok: true, Value: { } json })
            return (JsonSerializer.Deserialize<PipelineSettings>(json, SettingsStore.JsonOptions) ?? new PipelineSettings(), true);
        return (new SettingsStore(AppPaths.SettingsFile).Load().Pipelines, false);
    }

    private static async Task SaveAsync(PipelineSettings pipelines, bool viaApp, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(pipelines, SettingsStore.JsonOptions);
        if (viaApp)
        {
            var reply = await IpcClient.SendAsync<SettingsCommand, SettingsReply>(
                IpcChannel.Settings, new SettingsCommand("setJson", "pipelines", json), ConnectTimeout, cancellationToken);
            if (reply is { Ok: true })
                return;
            if (reply is not null)
                throw new InvalidOperationException(reply.Error);
        }

        var store = new SettingsStore(AppPaths.SettingsFile);
        var settings = store.Load();
        settings.Pipelines = pipelines;
        store.Save(settings);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
