using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Server;

public sealed class StdioTransport
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public StdioTransport(TextReader? reader = null, TextWriter? writer = null)
    {
        _reader = reader ?? Console.In;
        _writer = writer ?? Console.Out;
    }

    public async Task<string?> ReadMessageAsync(CancellationToken ct = default)
    {
        return await _reader.ReadLineAsync(ct);
    }

    public async Task SendMessageAsync(string jsonRpcMessage, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(jsonRpcMessage.AsMemory(), ct);
            await _writer.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public static void LogDiagnostic(string message)
    {
        Console.Error.WriteLine($"[marksmith-mcp] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {message}");
    }
}
