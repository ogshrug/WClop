using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using WClop.Cli.Mcp;
using WClop.Core.Ipc;
using WClop.Core.Pipelines;
using WClop.Core.Settings;

namespace WClop.Tests;

/// <summary>What AI assistants may do (<see cref="AssistantPolicy"/>), checked by the app and by <c>wclop mcp</c>.</summary>
public sealed class AssistantPolicyTests
{
    private static PipelineSettings Pipelines(bool allowScripts = false) => new() { AllowScriptsFromAssistants = allowScripts };

    [Theory]
    [InlineData("runScript(code: \"echo hi\")")]
    [InlineData("downscale(50%) -> openWith(app: \"C:\\\\Tools\\\\thing.exe\")")]
    [InlineData("fork(\"runScript(code: 'echo hi') -> move(to: '~/x/')\")")]
    public void PipelinesThatRunProgramsNeedPermission(string script)
    {
        var pipeline = PipelineCatalog.Compile(script);

        Assert.True(pipeline.RunsScripts);
        Assert.Equal(AssistantPolicy.ScriptsNotAllowed, AssistantPolicy.CheckRun(pipeline, Pipelines()));
        Assert.Null(AssistantPolicy.CheckRun(pipeline, Pipelines(allowScripts: true)));
    }

    [Theory]
    [InlineData("downscale(longEdge: 1920) -> convert(webp)")]
    [InlineData("openWith")]
    [InlineData("fork(\"convert(webp) -> move(to: '~/Web/')\")")]
    public void OrdinaryPipelinesRun(string script) => Assert.Null(AssistantPolicy.CheckRun(PipelineCatalog.Compile(script), Pipelines()));

    [Fact]
    public void AssistantsCantAllowThemselvesScripts()
    {
        var settings = new AppSettings();

        Assert.NotNull(AssistantPolicy.CheckSettingsWrite(settings, "set", "pipelines.allowScriptsFromAssistants", "true"));
        Assert.Null(AssistantPolicy.CheckSettingsWrite(settings, "set", "compression.imageFactor", "45"));
        Assert.Equal(30, settings.Compression.ImageFactor); // only tried on a copy
        Assert.False(settings.Pipelines.AllowScriptsFromAssistants);
    }

    [Fact]
    public void AssistantsCantSaveScriptsOrAttachPipelines()
    {
        var before = Pipelines();
        before.Saved.Add(new SavedPipeline { Name = "Backup", Script = "runScript(code: \"Copy-Item $env:WCLOP_INPUT_FILE D:\\\\\")" });
        before.Saved.Add(new SavedPipeline { Name = "Web", Script = "convert(webp)" });
        before.Attached.Add(new PipelineAttachment { Pipeline = "Web", Trigger = PipelineTrigger.Clipboard });

        PipelineSettings After(Action<PipelineSettings> change)
        {
            var after = JsonSerializer.Deserialize<PipelineSettings>(JsonSerializer.Serialize(before, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
            change(after);
            return after;
        }

        // The user's own script pipeline can stay; a safe new one is fine; deleting (and detaching) is fine.
        Assert.Null(AssistantPolicy.CheckPipelinesChange(before, After(p => p.Saved.Add(new SavedPipeline { Name = "Small", Script = "downscale(50%)" }))));
        Assert.Null(AssistantPolicy.CheckPipelinesChange(before, After(p =>
        {
            p.Saved.RemoveAll(s => s.Name == "Web");
            p.Attached.Clear();
        })));

        Assert.NotNull(AssistantPolicy.CheckPipelinesChange(before, After(p => p.Saved.Add(new SavedPipeline { Name = "Evil", Script = "runScript(code: \"x\")" }))));
        Assert.NotNull(AssistantPolicy.CheckPipelinesChange(before, After(p => p.Saved[1].Script = "convert(webp) -> runScript(code: \"x\")")));
        Assert.NotNull(AssistantPolicy.CheckPipelinesChange(before, After(p => p.Saved.Add(new SavedPipeline { Name = "Broken", Script = "downscal(50%)" }))));
        Assert.NotNull(AssistantPolicy.CheckPipelinesChange(before, After(p =>
            p.Attached.Add(new PipelineAttachment { Pipeline = "Web", Trigger = PipelineTrigger.Folder, Folder = "~/Pictures" }))));
        Assert.NotNull(AssistantPolicy.CheckPipelinesChange(before, After(p => p.AllowScriptsFromAssistants = true)));

        // Once the user allows scripts, assistants can save them too.
        before.AllowScriptsFromAssistants = true;
        Assert.Null(AssistantPolicy.CheckPipelinesChange(before, After(p => p.Saved.Add(new SavedPipeline { Name = "Ok", Script = "runScript(code: \"x\")" }))));
    }

    [Fact]
    public void ConfigSnippetStartsTheServer()
    {
        var json = JsonNode.Parse(McpConfig.Json(@"C:\Users\me\AppData\Local\Programs\WClop\wclop-cli.exe"))!;

        var server = json["mcpServers"]!["wclop"]!;
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\WClop\wclop-cli.exe", (string?)server["command"]);
        Assert.Equal("mcp", (string?)server["args"]![0]);
        Assert.Equal("claude mcp add --scope user wclop -- \"C:\\Program Files\\wclop-cli.exe\" mcp",
            McpConfig.ClaudeCodeCommand(@"C:\Program Files\wclop-cli.exe"));
        Assert.Equal("wclop", McpConfig.Command(Path.GetTempPath())); // not installed there: the command on PATH
    }
}

/// <summary>
/// The MCP tools with WClop not running (a pipe instance nobody serves), so they work on a settings file of their own,
/// under the same policy the app applies.
/// </summary>
public sealed class McpToolTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly WClopTools _tools;
    private readonly SettingsStore _store;

    public McpToolTests()
    {
        _store = new SettingsStore(_dir.File("settings.json"));
        var settings = new AppSettings();
        settings.Files.WorkDir = _dir.File("cache");
        _store.Save(settings);
        _tools = new WClopTools(new McpBackend(new McpEnvironment(_dir.File("settings.json"), _dir.File("db.sqlite"), "test-" + Guid.NewGuid().ToString("N"))));
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task SavesListsAndDeletesPipelines()
    {
        Assert.StartsWith("Saved Web", await _tools.AddPipeline("Web", "downscale(longEdge: 1920) -> convert(webp)", skipOptimisation: true));

        Assert.Contains("downscale(longEdge: 1920) -> convert(webp)", await _tools.ListPipelines());
        Assert.Equal("downscale(longEdge: 1920) -> convert(webp)", await _tools.ShowPipeline("web"));
        Assert.True(_store.Load().Pipelines.Find("Web")!.SkipOptimisation);

        Assert.StartsWith("Deleted", await _tools.DeletePipeline("Web"));
        Assert.Empty(_store.Load().Pipelines.Saved);
        await Assert.ThrowsAsync<McpException>(() => _tools.ShowPipeline("Web"));
    }

    [Fact]
    public async Task RefusesScriptsUnlessTheUserAllowedThem()
    {
        var error = await Assert.ThrowsAsync<McpException>(() => _tools.AddPipeline("Evil", "runScript(code: \"calc\")"));
        Assert.Contains("Settings → Pipelines", error.Message);
        Assert.Empty(_store.Load().Pipelines.Saved);

        var input = TestImages.SavePng(TestImages.Photo(64, 64), _dir.File("a.png"));
        await Assert.ThrowsAsync<McpException>(() => _tools.RunPipeline("runScript(code: \"calc\")", [input]));
        await Assert.ThrowsAsync<McpException>(() => _tools.SetSetting("pipelines.allowScriptsFromAssistants", "true"));
        Assert.False(_store.Load().Pipelines.AllowScriptsFromAssistants);

        var settings = _store.Load();
        settings.Pipelines.AllowScriptsFromAssistants = true;
        _store.Save(settings);
        Assert.StartsWith("Saved", await _tools.AddPipeline("Allowed", "runScript(code: \"echo hi\")"));
    }

    [Fact]
    public async Task ReadsAndChangesSettings()
    {
        Assert.Equal("30", await _tools.GetSetting("compression.imageFactor"));
        Assert.Equal("compression.imageFactor = 45", await _tools.SetSetting("compression.imageFactor", "45"));
        Assert.Equal(45, _store.Load().Compression.ImageFactor);
        Assert.Contains("compression.audioCoverArt", WClopTools.ListSettings().Split('\n'));
        await Assert.ThrowsAsync<McpException>(() => _tools.SetSetting("compression.nope", "1"));
    }

    [Fact]
    public async Task NeedsAbsolutePaths() =>
        await Assert.ThrowsAsync<McpException>(() => _tools.Optimise(["photo.png"]));

    [ToolsFact]
    public async Task OptimisesWithoutTheApp()
    {
        var input = TestImages.SavePng(TestImages.Photo(400, 300), _dir.File("photo.png"));
        var before = new FileInfo(input).Length;

        var text = await _tools.Optimise([input], scale: "50%", keep: true);

        Assert.Contains("→ " + _dir.File("photo-optimised.png"), text);
        Assert.Equal(before, new FileInfo(input).Length); // kept
        Assert.Contains("WClop isn't running", text);
    }

    [Theory]
    [InlineData("50%", 0.5)]
    [InlineData("0.25", 0.25)]
    [InlineData("75", 0.75)]
    public void Scales(string text, double scale) => Assert.Equal(scale, WClopTools.ParseScale(text), 3);

    [Fact]
    public void CheckingAPipelinePointsAtTheMistake()
    {
        Assert.StartsWith("OK: downscale(50%)", WClopTools.CheckPipeline("downscale(50%)"));
        Assert.Contains("did you mean downscale", Assert.Throws<McpException>(() => WClopTools.CheckPipeline("downscal(50%)")).Message);
    }
}

/// <summary><c>wclop mcp</c> as an assistant starts it: JSON-RPC over stdin and stdout. Read-only calls only.</summary>
public sealed class McpServerTests
{
    private static async Task<List<JsonNode>> TalkAsync(params object[] messages)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "wclop.exe");
        using var process = Process.Start(new ProcessStartInfo(exe, "mcp")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        })!;
        try
        {
            foreach (var message in messages)
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync();

            var expected = messages.Count(m => JsonSerializer.SerializeToNode(m)!["id"] is not null);
            var replies = new List<JsonNode>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (replies.Count < expected && await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                replies.Add(JsonNode.Parse(line)!);
            return replies;
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000))
                process.Kill();
        }
    }

    private static object Call(int id, string method, object? parameters = null) =>
        new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } };

    [Fact]
    public async Task ServesToolsAndTheLanguageReference()
    {
        var replies = await TalkAsync(
            Call(1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }),
            new { jsonrpc = "2.0", method = "notifications/initialized" },
            Call(2, "tools/list"),
            Call(3, "resources/read", new { uri = McpCommand.LanguageUri }),
            Call(4, "tools/call", new { name = "check_pipeline", arguments = new { steps = "convert(webp) -> rename(to: \"%f-web\")" } }),
            Call(5, "tools/call", new { name = "check_pipeline", arguments = new { steps = "conver(webp)" } }));

        JsonNode Reply(int id) => replies.Single(r => (int?)r["id"] == id)["result"]!;

        Assert.Equal("wclop", (string?)Reply(1)["serverInfo"]!["name"]);
        var tools = Reply(2)["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "optimise", "convert", "run_pipeline", "list_pipelines", "show_pipeline", "add_pipeline", "delete_pipeline",
            "check_pipeline", "pipeline_language", "list_settings", "get_setting", "set_setting", "stop",
        }, tools);
        Assert.StartsWith("# WClop pipeline language", (string?)Reply(3)["contents"]![0]!["text"]);
        Assert.StartsWith("OK: convert(webp)", (string?)Reply(4)["content"]![0]!["text"]);
        Assert.True((bool?)Reply(5)["isError"]);
    }
}
