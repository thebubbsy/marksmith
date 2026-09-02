using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkSmith.WebApp.Server.Ot;

/// <summary>
/// The v1 operation vocabulary. Every operation a client can send, plus the two server-side
/// meta-operations (Undo / Redo) which are protocol messages that *generate* real operations.
///
/// Design rule: an operation must be applicable through the OpenXml SDK's public API with no
/// knowledge of OOXML internals (see <see cref="Documents.OpApplier"/>). Anything Word cannot
/// apply safely is out of scope, not papered over.
/// </summary>
public enum OpType
{
    InsertText,          // payload: block, offset, text
    DeleteText,          // payload: block, offset, length
    ApplyFormatting,     // payload: block, offset, length, format{ bold?, italic?, underline?, strikethrough?, color? }
    InsertParagraph,     // payload: block (insert before this index), style ("Normal"|"Heading1".."Heading6")
    DeleteParagraph,     // payload: block
    InsertTable,         // payload: block, rows, cols
    DeleteTable,         // payload: block
    InsertTableRow,      // payload: block, row
    DeleteTableRow,      // payload: block, row
    InsertImage,         // payload: block, offset, alt, dataUri (base64 data: URI) | url, width?, height?
    DeleteImage,         // payload: imageId
    InsertHyperlink,     // payload: block, offset, text, url
    DeleteHyperlink,     // payload: block, offset, length
    AddComment,          // payload: commentId, block, offset, length, author, text
    ResolveComment,      // payload: commentId
    ApplyTrackChange,    // payload: changeId, block, offset, length, kind ("insert"|"delete"|"format"), author
    AcceptTrackChange,   // payload: changeId
    RejectTrackChange,   // payload: changeId

    // Server-side meta operations (never sent by clients as ops):
    Undo,                // payload: uptoSeq (client asks server to inverse its own ops down to this seq)
    Redo,                // payload: fromSeq
}

/// <summary>
/// A single operation in the wire protocol. Serialized as a flat discriminated JSON object:
/// <c>{ "id": "op-…", "clientId": "u-…", "type": "insertText", "block": 2, "offset": 5, "text": "hi" }</c>
/// The custom converter maps the <see cref="OpType"/> to the JSON <c>type</c> field and packs the
/// type-specific payload fields inline (no nesting), so the TypeScript client is a thin mirror.
/// </summary>
public sealed class Operation
{
    public required string Id { get; init; }                 // client-generated, unique per session
    public required string ClientId { get; init; }           // authenticated user id
    public required OpType Type { get; init; }

    // Positional fields (text-level ops)
    public int? Block { get; init; }                         // 0-based index into the body block list
    public int? Offset { get; init; }                        // 0-based char offset within the block
    public int? Length { get; init; }

    // Content fields
    public string? Text { get; init; }
    public string? Style { get; init; }                      // paragraph style id ("Normal","Heading1",…)
    public int? Rows { get; init; }
    public int? Cols { get; init; }
    public int? Row { get; init; }

    // Formatting payload (ApplyFormatting)
    public Formatting? Format { get; init; }

    // Image payload
    public string? Alt { get; init; }
    public string? DataUri { get; init; }
    public string? Url { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public string? ImageId { get; init; }

    // Hyperlink payload
    public string? Href { get; init; }

    // Comment / track change payload
    public string? CommentId { get; init; }
    public string? Author { get; init; }
    public string? ChangeId { get; init; }
    public string? Kind { get; init; }                       // "insert"|"delete"|"format" (ApplyTrackChange)

    // Undo/redo payload
    public long? UptoSeq { get; init; }
    public long? FromSeq { get; init; }

    /// <summary>Human-readable form for logs and rejection messages.</summary>
    public override string ToString() =>
        Type == OpType.Undo ? $"undo#{UptoSeq}" :
        Type == OpType.Redo ? $"redo#{FromSeq}" :
        $"{Type}({DescribeArgs()})";

    private string DescribeArgs() => Type switch
    {
        OpType.InsertText      => $"b{Block}@{Offset} +{Text?.Length}",
        OpType.DeleteText      => $"b{Block}@{Offset} -{Length}",
        OpType.ApplyFormatting => $"b{Block}@{Offset} x{Length}",
        OpType.InsertParagraph => $"b{Block} style={Style}",
        OpType.DeleteParagraph => $"b{Block}",
        OpType.InsertTable     => $"b{Block} {Rows}x{Cols}",
        OpType.DeleteTable     => $"b{Block}",
        OpType.InsertTableRow  => $"b{Block} row={Row}",
        OpType.DeleteTableRow  => $"b{Block} row={Row}",
        OpType.InsertImage     => $"b{Block}@{Offset} img={ImageId}",
        OpType.DeleteImage     => $"img={ImageId}",
        OpType.InsertHyperlink => $"b{Block}@{Offset} -> {Href}",
        OpType.DeleteHyperlink => $"b{Block}@{Offset} x{Length}",
        OpType.AddComment      => $"b{Block}@{Offset} x{Length} c={CommentId}",
        OpType.ResolveComment  => $"c={CommentId}",
        OpType.ApplyTrackChange => $"b{Block}@{Offset} x{Length} ch={ChangeId} kind={Kind}",
        OpType.AcceptTrackChange => $"ch={ChangeId}",
        OpType.RejectTrackChange => $"ch={ChangeId}",
        _ => ""
    };
}

/// <summary>Character formatting payload for <see cref="OpType.ApplyFormatting"/>.</summary>
public sealed class Formatting
{
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public bool? Underline { get; init; }
    public bool? Strikethrough { get; init; }
    public string? Color { get; init; }   // "#RRGGBB" or named color; null = leave unchanged
}

/// <summary>Serialization: flat discriminated JSON. All payload fields are optional at the
/// serializer level; semantic validation happens in the session before sequencing.</summary>
public sealed class OperationJsonConverter : JsonConverter<Operation>
{
    public override Operation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var id = GetString(root, "id") ?? throw new JsonException("operation missing 'id'");
        var clientId = GetString(root, "clientId") ?? "";
        var typeName = GetString(root, "type") ?? throw new JsonException("operation missing 'type'");
        var type = ParseType(typeName);

        var builder = new OperationBuilder(id, clientId, type);
        builder.Block = GetInt(root, "block");
        builder.Offset = GetInt(root, "offset");
        builder.Length = GetInt(root, "length");
        builder.Text = GetString(root, "text");
        builder.Style = GetString(root, "style");
        builder.Rows = GetInt(root, "rows");
        builder.Cols = GetInt(root, "cols");
        builder.Row = GetInt(root, "row");
        builder.Alt = GetString(root, "alt");
        builder.DataUri = GetString(root, "dataUri");
        builder.Url = GetString(root, "url");
        builder.Width = GetDouble(root, "width");
        builder.Height = GetDouble(root, "height");
        builder.ImageId = GetString(root, "imageId");
        builder.Href = GetString(root, "url") ?? GetString(root, "href");
        builder.CommentId = GetString(root, "commentId");
        builder.Author = GetString(root, "author");
        builder.ChangeId = GetString(root, "changeId");
        builder.Kind = GetString(root, "kind");
        builder.UptoSeq = GetLong(root, "uptoSeq");
        builder.FromSeq = GetLong(root, "fromSeq");

        if (root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.Object)
        {
            builder.Format = new Formatting
            {
                Bold = GetBool(fmt, "bold"),
                Italic = GetBool(fmt, "italic"),
                Underline = GetBool(fmt, "underline"),
                Strikethrough = GetBool(fmt, "strikethrough"),
                Color = GetString(fmt, "color"),
            };
        }

        return builder.Build();
    }

    public override void Write(Utf8JsonWriter writer, Operation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("clientId", value.ClientId);
        writer.WriteString("type", TypeName(value.Type));
        WriteInt(writer, "block", value.Block);
        WriteInt(writer, "offset", value.Offset);
        WriteInt(writer, "length", value.Length);
        WriteString(writer, "text", value.Text);
        WriteString(writer, "style", value.Style);
        WriteInt(writer, "rows", value.Rows);
        WriteInt(writer, "cols", value.Cols);
        WriteInt(writer, "row", value.Row);
        WriteString(writer, "alt", value.Alt);
        WriteString(writer, "dataUri", value.DataUri);
        WriteString(writer, "url", value.Url);
        WriteDouble(writer, "width", value.Width);
        WriteDouble(writer, "height", value.Height);
        WriteString(writer, "imageId", value.ImageId);
        WriteString(writer, "href", value.Href);
        WriteString(writer, "commentId", value.CommentId);
        WriteString(writer, "author", value.Author);
        WriteString(writer, "changeId", value.ChangeId);
        WriteString(writer, "kind", value.Kind);
        WriteLong(writer, "uptoSeq", value.UptoSeq);
        WriteLong(writer, "fromSeq", value.FromSeq);
        if (value.Format is { } f)
        {
            writer.WriteStartObject("format");
            WriteBool(writer, "bold", f.Bold);
            WriteBool(writer, "italic", f.Italic);
            WriteBool(writer, "underline", f.Underline);
            WriteBool(writer, "strikethrough", f.Strikethrough);
            WriteString(writer, "color", f.Color);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    // ---- helpers ----
    internal static OpType ParseType(string name) => name switch
    {
        "insertText" => OpType.InsertText,
        "deleteText" => OpType.DeleteText,
        "applyFormatting" => OpType.ApplyFormatting,
        "insertParagraph" => OpType.InsertParagraph,
        "deleteParagraph" => OpType.DeleteParagraph,
        "insertTable" => OpType.InsertTable,
        "deleteTable" => OpType.DeleteTable,
        "insertTableRow" => OpType.InsertTableRow,
        "deleteTableRow" => OpType.DeleteTableRow,
        "insertImage" => OpType.InsertImage,
        "deleteImage" => OpType.DeleteImage,
        "insertHyperlink" => OpType.InsertHyperlink,
        "deleteHyperlink" => OpType.DeleteHyperlink,
        "addComment" => OpType.AddComment,
        "resolveComment" => OpType.ResolveComment,
        "applyTrackChange" => OpType.ApplyTrackChange,
        "acceptTrackChange" => OpType.AcceptTrackChange,
        "rejectTrackChange" => OpType.RejectTrackChange,
        "undo" => OpType.Undo,
        "redo" => OpType.Redo,
        _ => throw new JsonException($"unknown operation type '{name}'"),
    };

    internal static string TypeName(OpType type) => type switch
    {
        OpType.InsertText => "insertText",
        OpType.DeleteText => "deleteText",
        OpType.ApplyFormatting => "applyFormatting",
        OpType.InsertParagraph => "insertParagraph",
        OpType.DeleteParagraph => "deleteParagraph",
        OpType.InsertTable => "insertTable",
        OpType.DeleteTable => "deleteTable",
        OpType.InsertTableRow => "insertTableRow",
        OpType.DeleteTableRow => "deleteTableRow",
        OpType.InsertImage => "insertImage",
        OpType.DeleteImage => "deleteImage",
        OpType.InsertHyperlink => "insertHyperlink",
        OpType.DeleteHyperlink => "deleteHyperlink",
        OpType.AddComment => "addComment",
        OpType.ResolveComment => "resolveComment",
        OpType.ApplyTrackChange => "applyTrackChange",
        OpType.AcceptTrackChange => "acceptTrackChange",
        OpType.RejectTrackChange => "rejectTrackChange",
        OpType.Undo => "undo",
        OpType.Redo => "redo",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static long? GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    private static double? GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static bool? GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
    private static void WriteString(Utf8JsonWriter w, string name, string? value) { if (value is not null) w.WriteString(name, value); }
    private static void WriteInt(Utf8JsonWriter w, string name, int? value) { if (value is not null) w.WriteNumber(name, value.Value); }
    private static void WriteLong(Utf8JsonWriter w, string name, long? value) { if (value is not null) w.WriteNumber(name, value.Value); }
    private static void WriteDouble(Utf8JsonWriter w, string name, double? value) { if (value is not null) w.WriteNumber(name, value.Value); }
    private static void WriteBool(Utf8JsonWriter w, string name, bool? value) { if (value is not null) w.WriteBoolean(name, value.Value); }
}

/// <summary>Mutable builder used by the JSON converter; keeps the wire format flat.</summary>
internal sealed class OperationBuilder
{
    public OperationBuilder(string id, string clientId, OpType type)
    {
        Id = id; ClientId = clientId; Type = type;
    }

    public string Id { get; }
    public string ClientId { get; }
    public OpType Type { get; }
    public int? Block { get; set; }
    public int? Offset { get; set; }
    public int? Length { get; set; }
    public string? Text { get; set; }
    public string? Style { get; set; }
    public int? Rows { get; set; }
    public int? Cols { get; set; }
    public int? Row { get; set; }
    public Formatting? Format { get; set; }
    public string? Alt { get; set; }
    public string? DataUri { get; set; }
    public string? Url { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string? ImageId { get; set; }
    public string? Href { get; set; }
    public string? CommentId { get; set; }
    public string? Author { get; set; }
    public string? ChangeId { get; set; }
    public string? Kind { get; set; }
    public long? UptoSeq { get; set; }
    public long? FromSeq { get; set; }

    public Operation Build() => new()
    {
        Id = Id, ClientId = ClientId, Type = Type,
        Block = Block, Offset = Offset, Length = Length,
        Text = Text, Style = Style, Rows = Rows, Cols = Cols, Row = Row,
        Format = Format, Alt = Alt, DataUri = DataUri, Url = Url, Width = Width, Height = Height,
        ImageId = ImageId, Href = Href, CommentId = CommentId, Author = Author,
        ChangeId = ChangeId, Kind = Kind, UptoSeq = UptoSeq, FromSeq = FromSeq,
    };
}
