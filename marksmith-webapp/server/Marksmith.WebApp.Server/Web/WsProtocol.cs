using System.Text.Json;
using System.Text.Json.Serialization;
using MarkSmith.WebApp.Server.Ot;

namespace MarkSmith.WebApp.Server.Web;

/// <summary>
/// WebSocket message contracts (v1). Wire format is JSON with a discriminator field `type`.
/// Full protocol description: docs/03-websocket-protocol.md.
///
/// Client -> server:
///   batch    { type:"batch", baseSeq, batchId, ops:[Operation] }
///   undo     { type:"undo", uptoSeq }
///   presence { type:"presence", caret?, selection? }        // throttled by the client
///   resync   { type:"resync" }                              // request full state (drift recovery)
///   ping     { type:"ping" }
///
/// Server -> client:
///   welcome  { type:"welcome", sessionId, clientId, seq, html, docUrl }
///   ack      { type:"ack", batchId, baseSeq, entries:[{seq, op, noOp}] }        // to origin only
///   ops      { type:"ops", entries:[{seq, clientId, op}], html? }               // broadcast
///   error    { type:"error", code, message }
///   pong     { type:"pong" }
///   kicked   { type:"kicked", reason }                                          // backpressure drop
/// </summary>
public abstract record WsMessageBase([property: JsonPropertyName("type")] string Type);

public sealed record WelcomeMessage(
    string SessionId, string ClientId, long Seq, string Html, string? DocUrl)
    : WsMessageBase("welcome");

public sealed record AckMessage(
    string BatchId, long BaseSeq, IReadOnlyList<AckEntry> Entries)
    : WsMessageBase("ack");

public sealed record AckEntry(long Seq, Operation Op, bool NoOp);

public sealed record OpsMessage(
    IReadOnlyList<OpsEntry> Entries, string? Html = null)
    : WsMessageBase("ops");

public sealed record OpsEntry(long Seq, string ClientId, Operation Op);

public sealed record ErrorMessage(string Code, string Message) : WsMessageBase("error");

public sealed record PongMessage() : WsMessageBase("pong");

public sealed record KickedMessage(string Reason) : WsMessageBase("kicked");

// ---------------- client -> server ----------------

public sealed record BatchMessage(
    [property: JsonPropertyName("batchId")] string BatchId,
    [property: JsonPropertyName("baseSeq")] long BaseSeq,
    IReadOnlyList<Operation> Ops)
    : WsMessageBase("batch");

public sealed record UndoMessage(
    [property: JsonPropertyName("uptoSeq")] long UptoSeq)
    : WsMessageBase("undo");

public sealed record PresenceMessage(
    CaretPosition? Caret, SelectionSpan? Selection)
    : WsMessageBase("presence");

public sealed record CaretPosition(
    [property: JsonPropertyName("block")] int Block,
    [property: JsonPropertyName("offset")] int Offset);

public sealed record SelectionSpan(CaretPosition Start, CaretPosition End);

public sealed record ResyncMessage() : WsMessageBase("resync");

public sealed record PingMessage() : WsMessageBase("ping");

/// <summary>Parses an inbound frame into a typed message. Returns null for malformed frames.</summary>
public static class WsProtocol
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new OperationJsonConverter() },
    };

    public static JsonSerializerOptions JsonOptions => JsonOpts;

    public static string Serialize<T>(T message) where T : WsMessageBase =>
        JsonSerializer.Serialize(message, JsonOpts);

    public static WsMessageBase? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();
            return type switch
            {
                "batch" => JsonSerializer.Deserialize<BatchMessage>(json, JsonOpts),
                "undo" => JsonSerializer.Deserialize<UndoMessage>(json, JsonOpts),
                "presence" => JsonSerializer.Deserialize<PresenceMessage>(json, JsonOpts),
                "resync" => JsonSerializer.Deserialize<ResyncMessage>(json, JsonOpts),
                "ping" => JsonSerializer.Deserialize<PingMessage>(json, JsonOpts),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
