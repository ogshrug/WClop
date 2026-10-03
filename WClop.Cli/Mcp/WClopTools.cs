using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WClop.Core.DropZone;
using WClop.Core.Ipc;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Settings;

namespace WClop.Cli.Mcp;

/// <summary>
/// The tools AI assistants get from <c>wclop mcp</c> (project.md §20.3). Each returns plain text for the assistant to
/// read; a request that can't be done at all throws <see cref="McpException"/>, which the client shows as an error.
/// </summary>
internal sealed class WClopTools(McpBackend backend)
{
    private const string FilesDescription = "Absolute paths of files or folders (a folder means the supported files directly inside it)";

    // Optimising

    [McpServerTool(Name = "optimise", Destructive = true, OpenWorld = false)]
    [Description("Optimise images, videos, PDFs and audio files with WClop. By default the result replaces the file (the original is " +
                 "backed up and can be restored from WClop's result card); set keep to save a copy next to it instead. " +
                 "A file that can't get smaller is left alone. Results show as cards in the WClop app when it's running.")]
    public async Task<string> Optimise(
        [Description(FilesDescription)] string[] files,
        [Description("A preset instead of the settings: Normal, Aggressive, Maximum, \"Half size\", \"Under 10 MB\", \"Under 25 MB\" or Gentle")]
        string? preset = null,
        [Description("Compression factor 1–100: higher is smaller with more loss (images default to 30, video 50, audio 35; 64 is aggressive)")]
        int? factor = null,
        [Description("Downscale images and videos, as a fraction or percentage: 0.5 or \"50%\"")] string? scale = null,
        [Description("Make each file fit under this size, e.g. \"10MB\" or \"500KB\"")] string? fit = null,
        [Description("Convert to this format instead of optimising: jpeg, png, webp, avif, gif (images); mp4, webm, gif (videos)")]
        string? to = null,
        [Description("Keep the original and save the result next to it")] bool keep = false,
        CancellationToken cancellationToken = default)
    {
        var command = new OptimiseCommand
        {
            Paths = Paths(files),
            Preset = preset,
            Factor = factor,
            Scale = scale is null ? null : ParseScale(scale),
            TargetBytes = fit is null ? null : SizeText.TryParse(fit, out var bytes) ? bytes : throw new McpException($"'{fit}' isn't a size like 10MB"),
            ConvertTo = to,
            KeepOriginals = keep,
        };
        return Describe(await backend.OptimiseAsync(command, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "convert", Destructive = false, OpenWorld = false)]
    [Description("Convert images or videos to another format, starting from the original. The converted file is saved next to the " +
                 "original (which is kept), unless Settings → Output says otherwise.")]
    public async Task<string> Convert(
        [Description(FilesDescription)] string[] files,
        [Description("Target format: jpeg, png, webp, avif, gif (images); mp4 (HEVC), webm, gif (videos)")] string to,
        CancellationToken cancellationToken = default) =>
        Describe(await backend.OptimiseAsync(new OptimiseCommand { Paths = Paths(files), ConvertTo = to }, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "stop", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stop everything the WClop app is doing right now (running optimisations and pipelines).")]
    public async Task<string> Stop(CancellationToken cancellationToken = default) =>
        await backend.StopAsync(cancellationToken).ConfigureAwait(false) ? "Stopped." : "WClop isn't running, so there was nothing to stop.";

    // Pipelines

    [McpServerTool(Name = "pipeline_language", ReadOnly = true, OpenWorld = false)]
    [Description("The complete reference of WClop's pipeline language (every step, argument and example). Read it before writing a pipeline.")]
    public static string PipelineLanguage() => PipelineCatalog.Prompt();

    [McpServerTool(Name = "check_pipeline", ReadOnly = true, OpenWorld = false)]
    [Description("Check a pipeline for mistakes without running or saving it. Returns OK with the steps, or the problem and where it is.")]
    public static string CheckPipeline([Description("The pipeline, e.g. downscale(longEdge: 1920) -> convert(webp)")] string steps) =>
        PipelineCatalog.TryCompile(steps, out var compiled, out var error)
            ? "OK: " + string.Join(" -> ", compiled!.Steps) + (compiled.RunsScripts ? "\n(It runs a script or program, which assistants may only do if the user allowed it.)" : "")
            : throw new McpException(error!);

    [McpServerTool(Name = "run_pipeline", Destructive = true, OpenWorld = false)]
    [Description("Run a saved pipeline (by name) or pipeline text on files. Pipelines with runScript (or openWith a named app) only " +
                 "run if the user allowed AI assistants to run scripts in WClop's Settings → Pipelines.")]
    public async Task<string> RunPipeline(
        [Description("A saved pipeline's name, or pipeline text such as \"downscale(50%) -> convert(webp)\"")] string pipeline,
        [Description(FilesDescription)] string[] files,
        [Description("Don't optimise the files first (default: the saved pipeline's setting; inline pipelines that re-encode skip it)")]
        bool? skipOptimisation = null,
        CancellationToken cancellationToken = default) =>
        Describe(await backend.OptimiseAsync(
            new OptimiseCommand { Paths = Paths(files), Pipeline = pipeline, SkipOptimisation = skipOptimisation }, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "list_pipelines", ReadOnly = true, OpenWorld = false)]
    [Description("The saved pipelines: name, options, steps, and where each runs automatically.")]
    public async Task<string> ListPipelines(CancellationToken cancellationToken = default)
    {
        var pipelines = await backend.LoadPipelinesAsync(cancellationToken).ConfigureAwait(false);
        if (pipelines.Saved.Count == 0)
            return "No saved pipelines.";

        var text = new StringBuilder();
        foreach (var saved in pipelines.Saved)
        {
            var flags = new List<string>();
            if (saved.SkipOptimisation) flags.Add("skip optimisation");
            if (saved.HideResult) flags.Add("hide result");
            if (saved.Kinds.Count > 0) flags.Add("only for " + string.Join(", ", saved.Kinds));
            if (!PipelineCatalog.TryCompile(saved.Script, out _, out var problem)) flags.Add("ERROR: " + problem);
            text.AppendLine($"{saved.Name}{(flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : "")}");
            text.AppendLine("  " + saved.Script.ReplaceLineEndings(" "));
            foreach (var attachment in pipelines.Attached.Where(a => a.Pipeline.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)))
                text.AppendLine("  runs automatically on: " + (attachment.Trigger == PipelineTrigger.Folder ? $"folder {attachment.Folder}" : attachment.Trigger.ToString()));
        }

        return text.ToString().TrimEnd();
    }

    [McpServerTool(Name = "show_pipeline", ReadOnly = true, OpenWorld = false)]
    [Description("A saved pipeline's steps.")]
    public async Task<string> ShowPipeline([Description("The pipeline's name")] string name, CancellationToken cancellationToken = default)
    {
        var pipelines = await backend.LoadPipelinesAsync(cancellationToken).ConfigureAwait(false);
        return pipelines.Find(name)?.Script ?? throw new McpException($"No pipeline called '{name}'");
    }

    [McpServerTool(Name = "add_pipeline", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Save a pipeline (or replace the one with that name) so the user can run it from result cards, the drop zone and " +
                 "the command line. Check it with check_pipeline first. Assistants can't save pipelines with runScript unless the " +
                 "user allowed it, and can't make pipelines run automatically.")]
    public async Task<string> AddPipeline(
        [Description("A short name, e.g. \"Web images\"")] string name,
        [Description("The pipeline, e.g. downscale(longEdge: 1920) -> convert(webp)")] string steps,
        [Description("Don't optimise files before the steps (for pipelines that re-encode anyway, like downscale or convert)")]
        bool skipOptimisation = false,
        [Description("No result card when it runs automatically")] bool hideResult = false,
        [Description("Offer it as a drop zone preset")] bool showInDropZone = true,
        CancellationToken cancellationToken = default)
    {
        if (name.Trim().Length == 0)
            throw new McpException("The pipeline needs a name");
        if (!PipelineCatalog.TryCompile(steps, out _, out var error))
            throw new McpException(error!);

        var pipelines = await backend.LoadPipelinesAsync(cancellationToken).ConfigureAwait(false);
        var saved = pipelines.Find(name);
        var replaced = saved is not null;
        if (saved is null)
        {
            saved = new SavedPipeline { Name = name.Trim() };
            pipelines.Saved.Add(saved);
        }

        saved.Script = steps;
        saved.SkipOptimisation = skipOptimisation;
        saved.HideResult = hideResult;
        saved.ShowInDropZone = showInDropZone;
        await SaveAsync(pipelines, cancellationToken).ConfigureAwait(false);
        return $"{(replaced ? "Replaced" : "Saved")} {saved.Name}.";
    }

    [McpServerTool(Name = "delete_pipeline", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Delete a saved pipeline (and stop it running automatically anywhere).")]
    public async Task<string> DeletePipeline([Description("The pipeline's name")] string name, CancellationToken cancellationToken = default)
    {
        var pipelines = await backend.LoadPipelinesAsync(cancellationToken).ConfigureAwait(false);
        if (pipelines.Find(name) is not { } saved)
            throw new McpException($"No pipeline called '{name}'");
        pipelines.Saved.Remove(saved);
        pipelines.Attached.RemoveAll(a => a.Pipeline.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
        await SaveAsync(pipelines, cancellationToken).ConfigureAwait(false);
        return $"Deleted {saved.Name}.";
    }

    private async Task SaveAsync(PipelineSettings pipelines, CancellationToken cancellationToken)
    {
        try
        {
            await backend.SavePipelinesAsync(pipelines, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            throw new McpException(e.Message);
        }
    }

    // Settings

    [McpServerTool(Name = "list_settings", ReadOnly = true, OpenWorld = false)]
    [Description("Every setting's name (e.g. compression.imageFactor), for get_setting and set_setting.")]
    public static string ListSettings() => string.Join('\n', SettingsPath.All());

    [McpServerTool(Name = "get_setting", ReadOnly = true, OpenWorld = false)]
    [Description("A setting's current value as JSON, or every setting when no name is given.")]
    public async Task<string> GetSetting(
        [Description("e.g. compression.imageFactor or watching.images.folders; leave out for everything")] string? name = null,
        CancellationToken cancellationToken = default) =>
        Value(await backend.SettingsAsync("get", name, null, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "set_setting", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Change a setting. Values are numbers, true/false, names (e.g. inPlace) or comma-separated lists. Changes apply " +
                 "straight away. Assistants can't allow themselves to run scripts or attach pipelines.")]
    public async Task<string> SetSetting(
        [Description("e.g. compression.imageFactor")] string name,
        [Description("e.g. 45, true, a,b,c")] string value,
        CancellationToken cancellationToken = default) =>
        $"{name} = {Value(await backend.SettingsAsync("set", name, value, cancellationToken).ConfigureAwait(false))}";

    private static string Value(SettingsReply reply) =>
        reply.Ok ? reply.Value ?? "" : throw new McpException(reply.Error ?? "Failed");

    // Helpers

    private static List<string> Paths(string[] files)
    {
        if (files.Length == 0)
            throw new McpException("No files given");
        var relative = files.FirstOrDefault(f => !Path.IsPathFullyQualified(f));
        if (relative is not null)
            throw new McpException($"'{relative}' isn't an absolute path; give full paths like C:\\Users\\me\\Pictures\\photo.png");
        return files.Select(Path.GetFullPath).ToList();
    }

    internal static double ParseScale(string text)
    {
        var percent = text.Trim().EndsWith('%');
        if (!double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new McpException($"'{text}' isn't a scale like 0.5 or 50%");
        return percent || value > 1 ? value / 100 : value;
    }

    /// <summary>One line per file, like the command line prints. Nothing done at all is an error.</summary>
    internal static string Describe(OptimiseReply reply)
    {
        if (reply.Files.Count == 0)
            throw new McpException(reply.Message ?? "Nothing to do: no supported files there");

        var text = new StringBuilder();
        foreach (var file in reply.Files)
        {
            if (file.Succeeded && file.Output is not null)
            {
                text.AppendLine($"{file.Path}: {OptimisationJob.FormatBytes(file.OldSize)} → {OptimisationJob.FormatBytes(file.NewSize)} " +
                                $"(-{1 - (double)file.NewSize / Math.Max(1, file.OldSize):P0})");
                text.AppendLine($"  → {file.Output}");
                if (file.Status is not ("Done" or "Optimised") && file.Status.Length > 0)
                    text.AppendLine($"  {file.Status}");
            }
            else
            {
                text.AppendLine($"{file.Path}: {(file.Failed ? "failed: " : "")}{file.Status}");
            }
        }

        if (reply.Message is not null)
            text.AppendLine(reply.Message);
        return text.ToString().TrimEnd();
    }
}
