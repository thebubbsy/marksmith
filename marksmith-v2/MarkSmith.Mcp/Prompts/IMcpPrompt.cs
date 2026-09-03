using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Prompts;

public sealed record McpPromptArgument
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; init; }
}

public sealed record McpPromptMessageContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";
}

public sealed record McpPromptMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("content")]
    public McpPromptMessageContent Content { get; init; } = new();
}

public sealed record McpPromptResult
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<McpPromptMessage> Messages { get; init; } = new List<McpPromptMessage>();

    public static McpPromptResult SingleMessage(string text, string role = "user", string? description = null) =>
        new()
        {
            Description = description,
            Messages = new List<McpPromptMessage>
            {
                new() { Role = role, Content = new McpPromptMessageContent { Type = "text", Text = text } }
            }
        };
}

public interface IMcpPrompt
{
    string Name { get; }
    string Description { get; }
    IReadOnlyList<McpPromptArgument> Arguments { get; }
    Task<McpPromptResult> GetAsync(JsonElement arguments, CancellationToken ct = default);
}
