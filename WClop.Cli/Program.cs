using System.Globalization;
using WClop.Core;
using WClop.Core.DropZone;
using WClop.Core.Images;
using WClop.Core.Ipc;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Cli;

/// <summary>
/// The <c>wclop</c> command (project.md §20.1). Talks to the running app over its local API, so results show up
/// in the app and share its cache and backups; when the app isn't running, optimising happens in this process.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage:
          wclop optimise <file or folder>... [options]
          wclop crop <file or folder>... --size WxH | --aspect 16:9 [--smart] [options]
          wclop batch <file or folder>...         Open the batch window (needs the app)
          wclop stop                              Stop everything the app is doing
          wclop settings list                     Every setting name
          wclop settings get [name]               e.g. wclop settings get compression.imageFactor
          wclop settings set <name> <value>       Lists are comma-separated
          wclop settings show                     Open the settings window
          wclop pipeline …                        Pipelines; see wclop pipeline help
          wclop mcp                               Serve AI assistants (MCP over stdio); see Settings → Pipelines

        Options for optimise:
          --preset <name>       Normal, Aggressive, Maximum, "Half size", "Under 10 MB", "Under 25 MB", Gentle
          --factor <1-100>      Compression factor (default: from settings)
          --scale <fraction>    Downscale, e.g. 0.5 or 50%
          --fit <size>          Make it fit under a size, e.g. 10MB
          --to <format>         Convert instead, e.g. --to webp
          --keep                Keep the original; save next to it
          --output <mode>       temp | inplace | same | specific (default: from settings)
          --allow-larger        Keep the output even if it isn't smaller
          --no-wait             Hand the files to the app and return straight away
          --local               Don't use the running app

        Options for crop (also --keep, --output, --no-wait, --local):
          --size <WxH>          Crop to this many pixels from the centre, e.g. 1920x1080 (1920x: width only, x1080: height)
          --aspect <w:h>        Crop to a shape, e.g. 16:9, 4:3, 1:1, 9:16, 1.91:1 (with --size 1280x: that wide)
          --smart               Keep the most detailed part of an image instead of the centre
        Cropping always starts from the original, and the original is backed up.
        """;

    public static async Task<int> Main(string[] args)
    {
        // Non-ASCII file names and arrows otherwise print as "?" in the default console code page.
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "optimise" or "optimize" => await OptimiseAsync(args[1..], batch: false, cts.Token),
                "crop" => await OptimiseAsync(args[1..], batch: false, cts.Token, crop: true),
                "batch" => await OptimiseAsync(args[1..], batch: true, cts.Token),
                "stop" => await StopAsync(cts.Token),
                "settings" => await SettingsAsync(args[1..], cts.Token),
                "pipeline" or "pipelines" => await PipelineCommands.RunAsync(args[1..], cts.Token),
                "mcp" => await Mcp.McpCommand.RunAsync(cts.Token),
                _ => Fail(Usage),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return Fail(e.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(800);

    // optimise / batch / crop

    /// <summary><c>crop</c> takes the same path as <c>optimise</c>: the app over IPC if it's running, else locally.</summary>
    private static async Task<int> OptimiseAsync(string[] args, bool batch, CancellationToken cancellationToken, bool crop = false)
    {
        var files = new List<string>();
        var command = new OptimiseCommand { Paths = files, Batch = batch, Wait = true };
        var local = false;
        for (var i = 0; i < args.Length; i++)
        {
            string? Value() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--preset" when Value() is { } preset:
                    command = command with { Preset = preset };
                    break;
                case "--factor" when int.TryParse(Value(), out var factor):
                    command = command with { Factor = factor };
                    break;
                case "--scale" or "--downscale" when ParseScale(Value()) is { } scale:
                    command = command with { Scale = scale };
                    break;
                case "--fit" when Value() is { } size && SizeText.TryParse(size, out var bytes):
                    command = command with { TargetBytes = bytes };
                    break;
                case "--to" or "--convert" when Value() is { } format:
                    command = command with { ConvertTo = format };
                    break;
                case "--output" when ParseBehaviour(Value()) is { } behaviour:
                    command = command with { Output = behaviour };
                    break;
                case "--keep":
                    command = command with { KeepOriginals = true };
                    break;
                case "--allow-larger":
                    command = command with { AllowLarger = true };
                    break;
                case "--no-wait":
                    command = command with { Wait = false };
                    break;
                case "--local":
                    local = true;
                    break;
                case "--size" when crop && Value() is { } size:
                    command = command with { CropSize = size };
                    break;
                case "--aspect" or "--aspect-ratio" or "--ratio" when crop && Value() is { } aspect:
                    command = command with { CropAspect = aspect };
                    break;
                case "--smart" when crop:
                    command = command with { SmartCrop = true };
                    break;
                case "--force":
                    break; // Always on for files named explicitly; kept so older scripts still work.
                case var option when option.StartsWith("--", StringComparison.Ordinal):
                    return Fail($"Unknown or incomplete option: {args[i]}\n\n{Usage}");
                default:
                    files.Add(Path.GetFullPath(args[i]));
                    break;
            }
        }

        if (files.Count == 0)
            return Fail(Usage);
        if (crop && command.CropSize is null && command.CropAspect is null)
            return Fail("Say what to crop to: --size 1920x1080 or --aspect 16:9");

        var builder = new OptimisationRequestBuilder(command);
        if (builder.Error is { } error)
            return Fail(error);

        if (!local)
        {
            var reply = await IpcClient.SendAsync<OptimiseCommand, OptimiseReply>(
                IpcChannel.Optimise, command, ConnectTimeout, cancellationToken);
            if (reply is not null)
                return Print(reply);
            if (batch)
                return Fail("The batch window needs WClop to be running.");
        }

        return await OptimiseLocallyAsync(files, builder, cancellationToken);
    }

    internal static int Print(OptimiseReply reply)
    {
        var failures = 0;
        foreach (var file in reply.Files)
        {
            if (file.Succeeded && file.Output is not null)
            {
                Console.WriteLine($"{file.Path}: {FormatBytes(file.OldSize)} → {FormatBytes(file.NewSize)} " +
                                  $"(-{1 - (double)file.NewSize / Math.Max(1, file.OldSize):P0})\n  → {file.Output}");
            }
            else
            {
                Console.WriteLine($"{file.Path}: {file.Status}");
                if (file.Failed)
                    failures++;
            }
        }

        if (reply.Message is not null)
            Console.WriteLine(reply.Message);
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> OptimiseLocallyAsync(
        IReadOnlyList<string> paths, OptimisationRequestBuilder builder, CancellationToken cancellationToken)
    {
        var settings = new SettingsStore(AppPaths.SettingsFile).Load();
        var appPaths = new AppPaths(settings.Files.ResolveWorkDir());
        appPaths.EnsureCreated();
        var service = new FileOptimisationService(
            settings, appPaths, ToolLocator.CreateDefault(), new OptimisationDatabase(OptimisationDatabase.DefaultPath));
        var request = builder.Build();

        var failures = 0;
        foreach (var file in DropInputs.ExpandMediaPaths(paths, DropInputs.OptimisableMedia))
        {
            try
            {
                var result = builder.Crop is { } crop
                    ? await service.CropAsync(await service.DescribeAsync(file, cancellationToken), crop,
                        request.Behaviour ?? OutputBehaviour.InPlace, null, cancellationToken)
                    : builder.ConvertTo is { } target
                    ? await service.ConvertAsync(await service.DescribeAsync(file, cancellationToken), target, null, cancellationToken)
                    : await service.OptimiseAsync(file, request, null, cancellationToken);
                var formatChange = result.InputFormat != result.OutputFormat ? $" ({result.InputFormat} → {result.OutputFormat})" : "";
                var cached = result.FromCache ? " [cached]" : "";
                var cropped = result.Crop is { } size ? $" (cropped to {size})" : "";
                var missed = result.MissedTarget ? " [couldn't reach the target; smallest kept]" : "";
                Console.WriteLine(
                    $"{file}: {FormatBytes(result.OldSize)} → {FormatBytes(result.NewSize)} " +
                    $"(-{result.SavedFraction:P0}){cropped}{formatChange}{cached}{missed}\n  → {result.OutputPath}");
            }
            catch (OptimisationException e)
            {
                // Expected outcomes like "already fully compressed": not failures.
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

    // stop

    private static async Task<int> StopAsync(CancellationToken cancellationToken)
    {
        var reply = await IpcClient.SendAsync<object, StopReply>(IpcChannel.Stop, new { }, ConnectTimeout, cancellationToken);
        if (reply is null)
            return Fail("WClop isn't running.");
        Console.WriteLine("Stopped.");
        return 0;
    }

    // settings

    private static async Task<int> SettingsAsync(string[] args, CancellationToken cancellationToken)
    {
        var action = args.FirstOrDefault()?.ToLowerInvariant();
        if (action == "list")
        {
            foreach (var name in SettingsPath.All())
                Console.WriteLine(name);
            return 0;
        }

        var command = action switch
        {
            "get" => new SettingsCommand("get", args.ElementAtOrDefault(1)),
            "set" when args.Length >= 3 => new SettingsCommand("set", args[1], string.Join(' ', args[2..])),
            "show" => new SettingsCommand("show"),
            _ => null,
        };
        if (command is null)
            return Fail(Usage);

        var reply = await IpcClient.SendAsync<SettingsCommand, SettingsReply>(
            IpcChannel.Settings, command, ConnectTimeout, cancellationToken);
        if (reply is null)
            return command.Action == "show" ? Fail("WClop isn't running.") : SettingsOffline(command);
        if (!reply.Ok)
            return Fail(reply.Error ?? "Failed");
        if (reply.Value is not null)
            Console.WriteLine(reply.Value);
        return 0;
    }

    /// <summary>With the app closed, read and write the settings file directly; the app picks it up next launch.</summary>
    private static int SettingsOffline(SettingsCommand command)
    {
        var store = new SettingsStore(AppPaths.SettingsFile);
        var settings = store.Load();
        try
        {
            if (command.Action == "set")
            {
                SettingsPath.Set(settings, command.Key!, command.Value!);
                store.Save(settings);
            }

            Console.WriteLine(command.Key is null ? SettingsStore.Snapshot(settings) : SettingsPath.Get(settings, command.Key));
            return 0;
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
    }

    private static double? ParseScale(string? text)
    {
        if (text is null)
            return null;
        var percent = text.EndsWith('%');
        if (!double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return percent || value > 1 ? value / 100 : value;
    }

    private static OutputBehaviour? ParseBehaviour(string? value) => value?.ToLowerInvariant() switch
    {
        "temp" or "temporary" => OutputBehaviour.Temporary,
        "inplace" or "in-place" => OutputBehaviour.InPlace,
        "same" or "samefolder" => OutputBehaviour.SameFolder,
        "specific" or "specificfolder" => OutputBehaviour.SpecificFolder,
        _ => null,
    };

    internal static string FormatBytes(long bytes) => OptimisationJob.FormatBytes(bytes);
}
