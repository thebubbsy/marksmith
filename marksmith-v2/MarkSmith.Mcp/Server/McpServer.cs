using System;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Mcp.Prompts;
using MarkSmith.Mcp.Resources;
using MarkSmith.Mcp.Tools;

namespace MarkSmith.Mcp.Server;

public sealed class McpServer
{
    private readonly JsonRpcDispatcher _dispatcher;
    private readonly StdioTransport _transport;

    public McpServer(JsonRpcDispatcher? dispatcher = null, StdioTransport? transport = null)
    {
        _dispatcher = dispatcher ?? CreateDefaultDispatcher();
        _transport = transport ?? new StdioTransport();
    }

    public static JsonRpcDispatcher CreateDefaultDispatcher()
    {
        var dispatcher = new JsonRpcDispatcher();

        // 9 Tools
        dispatcher.RegisterTool(new RenderMarkdownTool());
        dispatcher.RegisterTool(new InspectDocxTool());
        dispatcher.RegisterTool(new PatchDocxTool());
        dispatcher.RegisterTool(new ConvertDocxTool());
        dispatcher.RegisterTool(new PatchMarkdownTool());
        dispatcher.RegisterTool(new ValidateMarkdownTool());
        dispatcher.RegisterTool(new DiffMarkdownTool());
        dispatcher.RegisterTool(new DiffDocxTool());
        dispatcher.RegisterTool(new Manage3BlockCycleTool());

        // 3 Prompts
        dispatcher.RegisterPrompt(new AuthorDocumentGemini38Prompt());
        dispatcher.RegisterPrompt(new ThreeBlockCycleGemini38Prompt());
        dispatcher.RegisterPrompt(new ReviewAndPatchGemini38Prompt());

        // 3 Resources
        dispatcher.RegisterResource(new SyntaxContractResource());
        dispatcher.RegisterResource(new TemplatesCatalogResource());
        dispatcher.RegisterResource(new PatchSpecResource());

        return dispatcher;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        StdioTransport.LogDiagnostic("MarkSmith MCP Server starting (stdio transport, protocol 2024-11-05)...");

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _transport.ReadMessageAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StdioTransport.LogDiagnostic($"Read error: {ex.Message}");
                break;
            }

            if (line == null)
            {
                // EOF reached
                StdioTransport.LogDiagnostic("Client closed standard input. Shutting down.");
                break;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                string? response = await _dispatcher.DispatchAsync(line, ct);
                if (response != null)
                {
                    await _transport.SendMessageAsync(response, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StdioTransport.LogDiagnostic($"Dispatch error: {ex.Message}");
            }
        }

        StdioTransport.LogDiagnostic("MarkSmith MCP Server stopped.");
    }
}
