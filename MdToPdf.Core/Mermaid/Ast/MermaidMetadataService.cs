namespace MdToPdf.Mermaid.Ast;

using System.Globalization;
using System.Text.Json;

public static class MermaidMetadataService
{
    /// <summary>
    /// Extracts node position metadata from comments (e.g. ast.Comments or raw comment lines).
    /// Comments can be either raw "%% {"id":"A", "x":500, "y":200}" or trimmed "{"id":"A", "x":500, "y":200}".
    /// </summary>
    public static Dictionary<string, NodePositionMetadata> ExtractPositions(IEnumerable<string>? comments)
    {
        var result = new Dictionary<string, NodePositionMetadata>(StringComparer.OrdinalIgnoreCase);
        if (comments == null) return result;

        foreach (var comment in comments)
        {
            if (string.IsNullOrWhiteSpace(comment)) continue;

            string jsonCandidate = CleanCommentString(comment);
            if (!jsonCandidate.StartsWith("{") || !jsonCandidate.EndsWith("}")) continue;

            try
            {
                using var doc = JsonDocument.Parse(jsonCandidate);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                string? id = null;
                if (root.TryGetProperty("id", out var idProp) || root.TryGetProperty("Id", out idProp))
                {
                    id = idProp.GetString();
                }

                if (string.IsNullOrWhiteSpace(id)) continue;

                double x = 0;
                if (root.TryGetProperty("x", out var xProp) || root.TryGetProperty("X", out xProp))
                {
                    if (xProp.ValueKind == JsonValueKind.Number)
                        x = xProp.GetDouble();
                    else if (xProp.ValueKind == JsonValueKind.String && double.TryParse(xProp.GetString(), CultureInfo.InvariantCulture, out var px))
                        x = px;
                }

                double y = 0;
                if (root.TryGetProperty("y", out var yProp) || root.TryGetProperty("Y", out yProp))
                {
                    if (yProp.ValueKind == JsonValueKind.Number)
                        y = yProp.GetDouble();
                    else if (yProp.ValueKind == JsonValueKind.String && double.TryParse(yProp.GetString(), CultureInfo.InvariantCulture, out var py))
                        y = py;
                }

                double? width = null;
                if (root.TryGetProperty("width", out var wProp) || root.TryGetProperty("Width", out wProp) ||
                    root.TryGetProperty("w", out wProp) || root.TryGetProperty("W", out wProp))
                {
                    if (wProp.ValueKind == JsonValueKind.Number)
                        width = wProp.GetDouble();
                    else if (wProp.ValueKind == JsonValueKind.String && double.TryParse(wProp.GetString(), CultureInfo.InvariantCulture, out var pw))
                        width = pw;
                }

                double? height = null;
                if (root.TryGetProperty("height", out var hProp) || root.TryGetProperty("Height", out hProp) ||
                    root.TryGetProperty("h", out hProp) || root.TryGetProperty("H", out hProp))
                {
                    if (hProp.ValueKind == JsonValueKind.Number)
                        height = hProp.GetDouble();
                    else if (hProp.ValueKind == JsonValueKind.String && double.TryParse(hProp.GetString(), CultureInfo.InvariantCulture, out var ph))
                        height = ph;
                }

                result[id] = new NodePositionMetadata
                {
                    Id = id,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height
                };
            }
            catch
            {
                // Non-JSON or malformed comment - ignore
            }
        }

        return result;
    }

    /// <summary>
    /// Replaces existing position metadata comments in ast.Comments with clean JSON position comments.
    /// Non-position comments are preserved.
    /// </summary>
    public static void InjectPositions(MermaidDiagramAst ast, IEnumerable<NodePositionMetadata>? positions)
    {
        if (ast == null) return;

        // Preserve non-position comments
        var nonPositionComments = ast.Comments.Where(c => !IsPositionMetadataComment(c)).ToList();
        ast.Comments.Clear();

        if (positions != null)
        {
            foreach (var pos in positions)
            {
                if (pos == null || string.IsNullOrWhiteSpace(pos.Id)) continue;
                ast.Comments.Add(SerializePosition(pos));
            }
        }

        ast.Comments.AddRange(nonPositionComments);
    }

    /// <summary>
    /// Checks if a comment string represents position metadata JSON.
    /// </summary>
    public static bool IsPositionMetadataComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return false;

        string jsonCandidate = CleanCommentString(comment);
        if (!jsonCandidate.StartsWith("{") || !jsonCandidate.EndsWith("}")) return false;

        try
        {
            using var doc = JsonDocument.Parse(jsonCandidate);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   (root.TryGetProperty("id", out _) || root.TryGetProperty("Id", out _)) &&
                   (root.TryGetProperty("x", out _) || root.TryGetProperty("X", out _)) &&
                   (root.TryGetProperty("y", out _) || root.TryGetProperty("Y", out _));
        }
        catch
        {
            return false;
        }
    }

    private static string CleanCommentString(string comment)
    {
        string trimmed = comment.Trim();
        if (trimmed.StartsWith("%%"))
        {
            trimmed = trimmed.Substring(2).Trim();
        }
        return trimmed;
    }

    private static string SerializePosition(NodePositionMetadata pos)
    {
        string xStr = FormatNumber(pos.X);
        string yStr = FormatNumber(pos.Y);

        if (pos.Width.HasValue && pos.Width.Value > 0 && pos.Height.HasValue && pos.Height.Value > 0)
        {
            string wStr = FormatNumber(pos.Width.Value);
            string hStr = FormatNumber(pos.Height.Value);
            return $"{{\"id\":\"{pos.Id}\", \"x\":{xStr}, \"y\":{yStr}, \"width\":{wStr}, \"height\":{hStr}}}";
        }

        return $"{{\"id\":\"{pos.Id}\", \"x\":{xStr}, \"y\":{yStr}}}";
    }

    private static string FormatNumber(double val)
    {
        if (Math.Abs(val % 1) < 1e-9)
        {
            return ((long)Math.Round(val)).ToString(CultureInfo.InvariantCulture);
        }
        return val.ToString(CultureInfo.InvariantCulture);
    }
}
