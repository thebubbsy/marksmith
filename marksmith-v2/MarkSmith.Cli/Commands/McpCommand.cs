using System;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Mcp.Server;

namespace MarkSmith.Cli.Commands;

public static class McpCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string transport = "stdio";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--transport" && i + 1 < args.Length)
            {
                transport = args[++i].ToLowerInvariant();
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var server = new McpServer();
        await server.RunAsync(cts.Token);
        return 0;
    }
}
