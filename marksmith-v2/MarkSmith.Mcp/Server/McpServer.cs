using System;
using System.Threading;
using System.Threading.Tasks;
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
        dispatcher.RegisterTool(new RenderMarkdownTool());
        dispatcher.RegisterTool(new InspectDocxTool());
        dispatcher.RegisterTool(new PatchDocxTool());
        dispatcher.RegisterTool(new ConvertDocxTool());
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
