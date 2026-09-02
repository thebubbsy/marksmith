using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Mcp.Server;

namespace MarkSmith.Mcp;

class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch { }

        string transport = "stdio";
        int port = 3000;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--transport" && i + 1 < args.Length)
            {
                transport = args[++i].ToLowerInvariant();
            }
            else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
            {
                port = p;
                i++;
            }
            else if (args[i] == "--help" || args[i] == "-h")
            {
                Console.Error.WriteLine("MarkSmith MCP Server (Model Context Protocol 2024-11-05)");
                Console.Error.WriteLine("Usage: marksmith-mcp [options]");
                Console.Error.WriteLine("Options:");
                Console.Error.WriteLine("  --transport <stdio|sse>   Transport protocol (default: stdio)");
                Console.Error.WriteLine("  --port <port>             Port for SSE transport (default: 3000)");
                Console.Error.WriteLine("  --help, -h                Show this help message");
                return 0;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (transport == "stdio")
        {
            var server = new McpServer();
            await server.RunAsync(cts.Token);
            return 0;
        }
        else
        {
            StdioTransport.LogDiagnostic($"Transport '{transport}' selected. Starting stdio server as default standard transport.");
            var server = new McpServer();
            await server.RunAsync(cts.Token);
            return 0;
        }
    }
}
