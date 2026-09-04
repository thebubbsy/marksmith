using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Resources;

public sealed record McpResourceContent
{
    [JsonPropertyName("uri")]
    public string Uri { get; init; } = "";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; init; } = "text/plain";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";
}

public sealed record McpResourceResult
{
    [JsonPropertyName("contents")]
    public IReadOnlyList<McpResourceContent> Contents { get; init; } = new List<McpResourceContent>();

    public static McpResourceResult FromText(string uri, string text, string mimeType = "text/plain") =>
        new()
        {
            Contents = new List<McpResourceContent>
            {
                new() { Uri = uri, MimeType = mimeType, Text = text }
            }
        };
}

public interface IMcpResource
{
    string Uri { get; }
    string Name { get; }
    string Description { get; }
    string MimeType { get; }
    Task<McpResourceResult> ReadAsync(CancellationToken ct = default);
}
