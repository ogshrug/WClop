using System.Text.Json;
using System.Text.Json.Nodes;

namespace WClop.Core.Ipc;

/// <summary>
/// What an AI assistant needs to start WClop's MCP server (<c>wclop mcp</c>), for Settings → Pipelines to show and copy.
/// </summary>
public static class McpConfig
{
    /// <summary>
    /// The program to start. Installed copies ship the command line as wclop-cli.exe next to WClop.exe; assistants
    /// start programs directly (not through a shell), so the full path is safer than the <c>wclop</c> shim on PATH.
    /// </summary>
    public static string Command(string appFolder)
    {
        var installed = Path.Combine(appFolder, "wclop-cli.exe");
        return File.Exists(installed) ? installed : "wclop";
    }

    /// <summary>The <c>mcpServers</c> entry used by Claude Desktop (claude_desktop_config.json) and Claude Code (.mcp.json).</summary>
    public static string Json(string command)
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["wclop"] = new JsonObject
                {
                    ["command"] = command,
                    ["args"] = new JsonArray("mcp"),
                },
            },
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The same for Claude Code's command line, for every project.</summary>
    public static string ClaudeCodeCommand(string command) =>
        $"claude mcp add --scope user wclop -- {(command.Contains(' ') ? $"\"{command}\"" : command)} mcp";
}
