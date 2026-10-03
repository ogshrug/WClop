using System.Text.Json;
using WClop.Core.Pipelines;
using WClop.Core.Settings;

namespace WClop.Core.Ipc;

/// <summary>
/// What AI assistants (the MCP server, <c>wclop mcp</c>) may do (project.md §20.3). They can optimise, convert, run
/// pipelines and change settings like the command line, except:
/// <list type="bullet">
/// <item>pipelines that run scripts or programs only run, and can only be saved, if the user turned on
/// <see cref="PipelineSettings.AllowScriptsFromAssistants"/>;</item>
/// <item>they can't turn that on themselves, or make a pipeline run automatically (attach it).</item>
/// </list>
/// Checked by the app for every request marked <see cref="IpcSource.Mcp"/>, and by the MCP server itself when it
/// works without the app.
/// </summary>
public static class AssistantPolicy
{
    public const string ScriptsNotAllowed =
        "This pipeline runs a script or a program, and AI assistants can't run those unless you allow it in WClop's " +
        "Settings → Pipelines (\"Let AI assistants run pipelines that include runScript steps\")";

    /// <summary>Null if an assistant may run <paramref name="pipeline"/>, else why not.</summary>
    public static string? CheckRun(CompiledPipeline pipeline, PipelineSettings settings) =>
        pipeline.RunsScripts && !settings.AllowScriptsFromAssistants ? ScriptsNotAllowed : null;

    /// <summary>
    /// Null if an assistant may make this settings change (<c>set</c> or <c>setJson</c> of <paramref name="key"/>),
    /// else why not. The change is tried on a copy, so nothing is touched either way.
    /// </summary>
    public static string? CheckSettingsWrite(AppSettings settings, string action, string key, string value)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
        if (action.Equals("setJson", StringComparison.OrdinalIgnoreCase))
            SettingsPath.SetJson(copy, key, value);
        else
            SettingsPath.Set(copy, key, value);
        return CheckPipelinesChange(settings.Pipelines, copy.Pipelines);
    }

    /// <summary>Null if the pipelines can go from <paramref name="before"/> to <paramref name="after"/> at an assistant's request.</summary>
    public static string? CheckPipelinesChange(PipelineSettings before, PipelineSettings after)
    {
        if (after.AllowScriptsFromAssistants != before.AllowScriptsFromAssistants)
            return "Only you can change whether AI assistants may run scripts (WClop's Settings → Pipelines)";

        static string Key(PipelineAttachment a) =>
            $"{a.Pipeline.Trim().ToLowerInvariant()}|{a.Trigger}|{a.Folder?.ToLowerInvariant()}|{string.Join(',', a.Kinds.Order()).ToLowerInvariant()}";
        var attached = before.Attached.Select(Key).ToHashSet();
        if (after.Attached.Any(a => !attached.Contains(Key(a))))
            return "AI assistants can't make pipelines run automatically; attach it yourself in Settings → Pipelines " +
                   "or with wclop pipeline attach";

        foreach (var saved in after.Saved)
        {
            // Only new or edited pipelines: one the user wrote may run scripts when the user runs it.
            if (before.Find(saved.Name) is { } old && old.Script == saved.Script)
                continue;
            if (!PipelineCatalog.TryCompile(saved.Script, out var compiled, out var error))
                return $"{saved.Name}: {error}";
            if (CheckRun(compiled!, before) is { } refused)
                return $"{saved.Name}: {refused}";
        }

        return null;
    }
}
