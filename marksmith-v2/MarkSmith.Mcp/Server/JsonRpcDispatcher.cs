using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Mcp.Tools;

namespace MarkSmith.Mcp.Server;

public sealed class JsonRpcDispatcher
{
    private readonly Dictionary<string, IMcpTool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public JsonRpcDispatcher(IEnumerable<IMcpTool>? tools = null)
    {
        if (tools != null)
        {
            foreach (var tool in tools)
            {
                _tools[tool.Name] = tool;
            }
        }
    }

    public void RegisterTool(IMcpTool tool)
    {
        _tools[tool.Name] = tool;
    }

    public IReadOnlyCollection<IMcpTool> Tools => _tools.Values;

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

            // Check if JSON-RPC 2.0
            if (!root.TryGetProperty("jsonrpc", out var rpcProp) || rpcProp.GetString() != "2.0")
            {
                // Accept requests without explicit 2.0 if id and method exist, but respond with 2.0
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
                                tools = new { listChanged = false }
                            },
                            serverInfo = new
                            {
                                name = "marksmith-mcp",
                                version = "3.0.0"
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
