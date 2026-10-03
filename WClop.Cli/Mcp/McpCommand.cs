using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WClop.Core;
using WClop.Core.Pipelines;

namespace WClop.Cli.Mcp;

/// <summary>
/// <c>wclop mcp</c>: a Model Context Protocol server over stdio (project.md §20.3), so AI assistants such as Claude
/// can optimise files, run and write pipelines, and change settings. Settings → Pipelines shows how to connect one.
/// </summary>
internal static class McpCommand
{
    public const string LanguageUri = "wclop://pipelines/language";

    private const string Instructions = """
        WClop optimises images, videos, PDFs and audio files on this Windows PC (smaller files, same look), converts them,
        and runs "pipelines": chains of steps like downscale(longEdge: 1920) -> convert(webp). Always pass absolute paths.
        When the WClop app is running, results also show as cards there, and originals are backed up so the user can
        restore them. Before writing a pipeline, read the language reference (pipeline_language, or the
        wclop://pipelines/language resource) and check it with check_pipeline.
        """;

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var options = CreateOptions(new WClopTools(new McpBackend(McpEnvironment.Default)));
        await using var server = McpServer.Create(new StdioServerTransport(options), options);
        try
        {
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return 0;
    }

    internal static McpServerOptions CreateOptions(WClopTools tools)
    {
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(WClopTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                     .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
            toolCollection.Add(McpServerTool.Create(method, method.IsStatic ? null : tools));

        var resources = new McpServerResourceCollection
        {
            McpServerResource.Create(PipelineCatalog.Prompt, new McpServerResourceCreateOptions
            {
                UriTemplate = LanguageUri,
                Name = "pipeline-language",
                Title = "WClop pipeline language",
                Description = "Every step, argument and example of WClop's pipeline language (the same as wclop pipeline prompt)",
                MimeType = "text/markdown",
            }),
        };

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "wclop", Title = "WClop", Version = AppPaths.Version },
            ServerInstructions = Instructions,
            ToolCollection = toolCollection,
            ResourceCollection = resources,
        };
    }
}
