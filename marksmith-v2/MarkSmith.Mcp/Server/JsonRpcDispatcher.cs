using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Mcp.Prompts;
using MarkSmith.Mcp.Resources;
using MarkSmith.Mcp.Tools;

namespace MarkSmith.Mcp.Server;

public sealed class JsonRpcDispatcher
{
    private readonly Dictionary<string, IMcpTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IMcpPrompt> _prompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IMcpResource> _resources = new(StringComparer.OrdinalIgnoreCase);

    public JsonRpcDispatcher(
        IEnumerable<IMcpTool>? tools = null,
        IEnumerable<IMcpPrompt>? prompts = null,
        IEnumerable<IMcpResource>? resources = null)
    {
        if (tools != null)
        {
            foreach (var tool in tools)
            {
                _tools[tool.Name] = tool;
            }
        }
        if (prompts != null)
        {
            foreach (var prompt in prompts)
            {
                _prompts[prompt.Name] = prompt;
            }
        }
        if (resources != null)
        {
            foreach (var resource in resources)
            {
                _resources[resource.Uri] = resource;
            }
        }
    }

    public void RegisterTool(IMcpTool tool) => _tools[tool.Name] = tool;
    public void RegisterPrompt(IMcpPrompt prompt) => _prompts[prompt.Name] = prompt;
    public void RegisterResource(IMcpResource resource) => _resources[resource.Uri] = resource;

    public IReadOnlyCollection<IMcpTool> Tools => _tools.Values;
    public IReadOnlyCollection<IMcpPrompt> Prompts => _prompts.Values;
    public IReadOnlyCollection<IMcpResource> Resources => _resources.Values;

    public async Task<string?> DispatchAsync(string rawJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            return SerializeError(null, -32700, $"Parse error: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SerializeError(null, -32600, "Invalid Request: Expected JSON object.");
            }

            JsonElement? id = null;
            if (root.TryGetProperty("id", out var idProp))
            {
                id = idProp.Clone();
            }

            if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
            {
                if (id != null)
                {
                    return SerializeError(id, -32600, "Invalid Request: 'method' string property is required.");
                }
                return null; // Ignore invalid notifications without id
            }

            string method = methodProp.GetString() ?? "";
            JsonElement @params = default;
            if (root.TryGetProperty("params", out var paramsProp))
            {
                @params = paramsProp;
            }

            try
            {
                var responseObj = await HandleMethodAsync(method, @params, id, ct);
                if (responseObj == null)
                {
                    return null; // Notifications produce no response
                }
                return JsonSerializer.Serialize(responseObj, new JsonSerializerOptions { WriteIndented = false });
            }
            catch (Exception ex)
            {
                if (id != null)
                {
                    return SerializeError(id, -32603, $"Internal error: {ex.Message}");
                }
                return null;
            }
        }
    }

    private async Task<object?> HandleMethodAsync(string method, JsonElement @params, JsonElement? id, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                {
                    if (id == null) return null;
                    return new
                    {
                        jsonrpc = "2.0",
                        id = GetIdValue(id.Value),
                        result = new
                        {
                            protocolVersion = "2024-11-05",
                            capabilities = new
                            {
                                tools = new { listChanged = false },
                                prompts = new { listChanged = false },
                                resources = new { subscribe = false, listChanged = false }
                            },
                            serverInfo = new
                            {
                                name = "marksmith-mcp",
                                version = "3.8.0"
                            }
                        }
                    };
                }

            case "notifications/initialized":
            case "initialized":
                {
                    // Handshake confirmation notification - no response
                    return null;
                }

            case "ping":
                {
                    if (id == null) return null;
                    return new
                    {
                        jsonrpc = "2.0",
                        id = GetIdValue(id.Value),
                        result = new { }
                    };
                }

            case "tools/list":
                {
                    if (id == null) return null;
                    var toolList = _tools.Values.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchema = t.InputSchema
                    }).ToList();

                    return new
                    {
                        jsonrpc = "2.0",
                        id = GetIdValue(id.Value),
                        result = new
                        {
                            tools = toolList
                        }
                    };
                }

            case "tools/call":
                {
                    if (id == null) return null;

                    if (@params.ValueKind != JsonValueKind.Object ||
                        !@params.TryGetProperty("name", out var toolNameProp) ||
                        toolNameProp.ValueKind != JsonValueKind.String)
                    {
                        return BuildErrorObj(id.Value, -32602, "Invalid params: 'name' is required for tools/call.");
                    }

                    string toolName = toolNameProp.GetString() ?? "";
                    if (!_tools.TryGetValue(toolName, out var tool))
                    {
                        return BuildErrorObj(id.Value, -32601, $"Tool not found: '{toolName}'");
                    }

                    JsonElement args = default;
                    if (@params.TryGetProperty("arguments", out var argsProp))
                    {
                        args = argsProp;
                    }

                    var toolResult = await tool.ExecuteAsync(args, ct);

                    return new
                    {
                        jsonrpc = "2.0",
                        id = GetIdValue(id.Value),
                        result = toolResult
                    };
                }

            case "prompts/list":
                {
                    if (id == null) return null;
                    var promptList = _prompts.Values.Select(p => new
                    {
                        name = p.Name,
                        description = p.Description,
                        arguments = p.Arguments
                    }).ToList();

                    return new
                    {
                        jsonrpc = "2.0",
                        id = GetIdValue(id.Value),
                        result = new
                        {
                            prompts = promptList
                        }
                    };
                }

            case "prompts/get":
                {
                    if (id == null) return null;

                    if (@params.ValueKind != JsonValueKind.Object ||
                        !@params.TryGetProperty("name", out var promptNameProp) ||
                        promptNameProp.ValueKind != JsonValueKind.String)
                    {
                        return BuildErrorObj(id.Value, -32602, "Invalid params: 'name' is required for prompts/get.");
                    }

                    string promptName = promptNameProp.GetString() ?? "";
                    if (!_prompts.TryGetValue(promptName, out var prompt))
                    {
                        return BuildErrorObj(id.Value, -32601, $"Prompt not found: '{promptName}'");
                    }

                    JsonElement args = default;
                    if (@params.TryGetProperty("arguments", out var argsProp))
                    {
                        args = argsProp;
                    }

                    var promptResult = await prompt.GetAsync(args, ct);

                    return new
                    {
                        jsonrpc = "2.0",
                        id = GetIdValue(id.Value),
                        result = promptResult
                    };
                }

            case "resources/list":
                {
                    if (id == null) return null;
                    var resourceList = _resources.Values.Select(r => new
                    {
                        uri = r.Uri,
                        name = r.Name,
                        description = r.Description,
                        mimeType = r.MimeType
                    }).ToList();

                    return new
                    {
                        jsonrpc = "2.0",
                        id = GetIdValue(id.Value),
                        result = new
                        {
                            resources = resourceList
                        }
                    };
                }

            case "resources/read":
                {
                    if (id == null) return null;

                    if (@params.ValueKind != JsonValueKind.Object ||
                        !@params.TryGetProperty("uri", out var uriProp) ||
                        uriProp.ValueKind != JsonValueKind.String)
                    {
                        return BuildErrorObj(id.Value, -32602, "Invalid params: 'uri' is required for resources/read.");
                    }

                    string uri = uriProp.GetString() ?? "";
                    if (!_resources.TryGetValue(uri, out var resource))
                    {
                        return BuildErrorObj(id.Value, -32601, $"Resource not found: '{uri}'");
                    }

                    var resourceResult = await resource.ReadAsync(ct);

                    return new
                    {
                        jsonrpc = "2.0",
                        id = GetIdValue(id.Value),
                        result = resourceResult
                    };
                }

            default:
                {
                    if (id != null)
                    {
                        return BuildErrorObj(id.Value, -32601, $"Method not found: '{method}'");
                    }
                    return null;
                }
        }
    }

    private static object GetIdValue(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.Number => id.TryGetInt64(out long l) ? (object)l : id.GetDouble(),
            JsonValueKind.String => id.GetString() ?? "",
            _ => id.ToString()
        };
    }

    private static string SerializeError(JsonElement? id, int code, string message)
    {
        var errObj = BuildErrorObj(id, code, message);
        return JsonSerializer.Serialize(errObj);
    }

    private static object BuildErrorObj(JsonElement? id, int code, string message)
    {
        return new
        {
            jsonrpc = "2.0",
            id = id.HasValue ? GetIdValue(id.Value) : null,
            error = new
            {
                code = code,
                message = message
            }
        };
    }
}
