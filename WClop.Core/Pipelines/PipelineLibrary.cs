using WClop.Core.Logging;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Settings;
using WClop.Core.Watching;

namespace WClop.Core.Pipelines;

public sealed record ResolvedPipeline(string Name, CompiledPipeline Compiled, bool SkipOptimisation, bool HideResult);

/// <summary>Saved pipelines from the settings, and the ones attached to the clipboard, drop zone and folders.</summary>
public sealed class PipelineLibrary(AppSettings settings, PipelineRunner runner)
{
    public PipelineRunner Runner => runner;

    public IReadOnlyList<string> Names => settings.Pipelines.Saved.Select(p => p.Name).Where(n => n.Length > 0).ToList();

    /// <summary>Pipelines to offer on a result card for a file of this format.</summary>
    public IReadOnlyList<string> NamesFor(FileFormat format) =>
        settings.Pipelines.Saved.Where(p => p.Name.Length > 0 && p.ShowOnResults && p.AppliesTo(format)).Select(p => p.Name).ToList();

    /// <summary>A saved pipeline by name, or else the text itself as a pipeline (<c>wclop pipeline run "convert(webp)"</c>).</summary>
    public ResolvedPipeline Resolve(string nameOrScript)
    {
        if (settings.Pipelines.Find(nameOrScript) is { } saved)
            return new ResolvedPipeline(saved.Name, PipelineCatalog.Compile(saved.Script), saved.SkipOptimisation, saved.HideResult);

        if (!PipelineCatalog.TryCompile(nameOrScript, out var compiled, out var error))
        {
            var names = settings.Pipelines.Saved.Select(p => p.Name).ToList();
            throw new PipelineException(names.Count > 0 && !nameOrScript.Contains('(')
                ? $"No pipeline called '{nameOrScript}'. Saved: {string.Join(", ", names)}"
                : error!);
        }

        // Inline pipelines that re-encode skip the implicit optimisation, like preset zones (§18.3).
        return new ResolvedPipeline("pipeline", compiled!, compiled!.Encodes, false);
    }

    /// <summary>Pipelines that run automatically for a file from <paramref name="trigger"/>, in the order they were attached.</summary>
    public IReadOnlyList<ResolvedPipeline> AttachedTo(PipelineTrigger trigger, string path)
    {
        var format = FileTypeSniffer.Detect(path);
        var found = new List<ResolvedPipeline>();
        foreach (var attachment in settings.Pipelines.Attached.Where(a => a.Trigger == trigger))
        {
            if (attachment.Kinds.Count > 0 && !attachment.Kinds.Any(k => PipelineRunner.MatchesType(k, format)))
                continue;
            if (trigger == PipelineTrigger.Folder
                && (attachment.Folder is not { Length: > 0 } folder || !WatchFilters.IsInside(path, PortablePath.Expand(folder))))
                continue;
            if (settings.Pipelines.Find(attachment.Pipeline) is not { } saved || !saved.AppliesTo(format))
                continue;
            if (!PipelineCatalog.TryCompile(saved.Script, out var compiled, out var error))
            {
                Log.Warn($"Pipeline {saved.Name} has an error and was skipped: {error}");
                continue;
            }

            found.Add(new ResolvedPipeline(saved.Name, compiled!, saved.SkipOptimisation, saved.HideResult));
        }

        return found;
    }

    /// <summary>Only when every attached pipeline says so is the normal optimisation skipped (project.md §3).</summary>
    public static bool SkipsOptimisation(IReadOnlyList<ResolvedPipeline> pipelines) =>
        pipelines.Count > 0 && pipelines.All(p => p.SkipOptimisation);

    /// <summary>
    /// Runs the attached pipelines one after another on a result. Returns the final result, and whether any of them
    /// asked to hide it. A pipeline that deletes the file ends the chain with <see cref="JobSkippedException"/>.
    /// </summary>
    public async Task<(FileOptimisationResult Result, bool Hide)> RunAllAsync(
        IReadOnlyList<ResolvedPipeline> pipelines, FileOptimisationResult start, PipelineContext context,
        CancellationToken cancellationToken = default)
    {
        var result = start;
        var hide = false;
        foreach (var pipeline in pipelines)
        {
            var outcome = await runner.RunAsync(pipeline.Name, pipeline.Compiled, result, context, cancellationToken).ConfigureAwait(false);
            hide |= pipeline.HideResult;
            if (outcome.Deleted)
                throw new JobSkippedException($"Deleted by the {pipeline.Name} pipeline");
            result = outcome.Result;
        }

        return (result with { HideResult = hide }, hide);
    }
}
