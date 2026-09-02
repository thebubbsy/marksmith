using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Tools;

public sealed record McpContentItem
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";
}

public sealed record McpToolResult
{
    [JsonPropertyName("content")]
    public IReadOnlyList<McpContentItem> Content { get; init; } = new List<McpContentItem>();

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static McpToolResult Success(string text) =>
        new()
        {
            IsError = false,
            Content = new List<McpContentItem> { new() { Type = "text", Text = text } }
        };

    public static McpToolResult SuccessJson(object obj) =>
        new()
        {
            IsError = false,
            Content = new List<McpContentItem>
            {
                new() { Type = "text", Text = JsonSerializer.Serialize(obj, JsonOptions) }
            }
        };

    public static McpToolResult Error(string message) =>
        new()
        {
            IsError = true,
            Content = new List<McpContentItem> { new() { Type = "text", Text = message } }
        };
}

public interface IMcpTool
{
    string Name { get; }
    string Description { get; }
    object InputSchema { get; }
    Task<McpToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default);
}
