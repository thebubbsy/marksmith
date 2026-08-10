using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarkSmith.Models;

namespace MarkSmith.Services;

public sealed record GoogleExportResult(string DocumentId, string Url);

/// <summary>
/// Creates a REAL Google Doc from Markdown using the Google Docs + Drive APIs (native headings,
/// tables, images, lists — see GoogleDocsDocumentBuilder). Pipeline:
///   1. create the doc (Docs API) with the source title;
///   2. one batchUpdate that inserts all text + applies all styles (builder output);
///   3. replace each image token with a native inline image (upload the bytes to Drive, share
///      them with anyone-with-link so shared docs render, then insertInlineImage);
///   4. replace each table token with a native table and fill its cells.
/// Requires an access token from GoogleAuthService (device sign-in) with the docs + drive scopes.
/// </summary>
public sealed class GoogleDocsExportService
{
    private const string DocsBase = "https://docs.googleapis.com/v1/documents";
    private const string DriveUploadBase = "https://www.googleapis.com/upload/drive/v3/files";
    private const string DriveBase = "https://www.googleapis.com/drive/v3/files";

    private readonly HttpClient _http;

    public GoogleDocsExportService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
    }

    public async Task<GoogleExportResult> ExportAsync(
        string markdown,
        AppSettings settings,
        ThemeDefinition? theme,
        string accessToken,
        IReadOnlyList<byte[]?>? mermaidImages = null,
        string? title = null,
        Func<string, Task<byte[]?>>? fetchRemoteImage = null,
        CancellationToken ct = default)
    {
        var build = GoogleDocsDocumentBuilder.Build(markdown, settings, theme);
        var docTitle = string.IsNullOrWhiteSpace(title)
            ? HistoryEntry.ExtractTitle(markdown) ?? "Marksmith Export"
            : title;
        var docId = await CreateDocumentAsync(accessToken, docTitle, ct);

        await BatchUpdateAsync(docId, accessToken, build.Requests, ct);
        await InsertImagesAsync(docId, accessToken, build, mermaidImages, fetchRemoteImage, ct);
        await InsertTablesAsync(docId, accessToken, build, ct);

        return new GoogleExportResult(docId, $"https://docs.google.com/document/d/{docId}/edit");
    }

    // ---- phase 2: images ----------------------------------------------------------------------

    private async Task InsertImagesAsync(string docId, string accessToken, GoogleDocsDocumentBuilder.GoogleDocsBuildResult build,
        IReadOnlyList<byte[]?>? mermaidImages, Func<string, Task<byte[]?>>? fetchRemoteImage, CancellationToken ct)
    {
        foreach (var img in build.Images)
        {
            var token = $"[[IMG_{img.Order}]]";
            var bytes = await ResolveImageBytesAsync(img, mermaidImages, fetchRemoteImage, ct);
            var index = await FindTokenIndexAsync(docId, accessToken, token, ct);
            if (index < 0) continue;

            if (bytes is null or { Length: 0 })
            {
                // Unresolvable image — drop the token instead of leaving [[IMG_0]] garbage.
                await BatchUpdateAsync(docId, accessToken, new object[] { DeleteRange(index, index + token.Length) }, ct);
                continue;
            }

            var uri = await UploadAndShareImageAsync(accessToken, bytes, "image/png", ct);
            await BatchUpdateAsync(docId, accessToken, new object[]
            {
                DeleteRange(index, index + token.Length),
                new { insertInlineImage = new { location = new { index }, uri } },
            }, ct);
        }
    }

    private static async Task<byte[]?> ResolveImageBytesAsync(GoogleDocsDocumentBuilder.GoogleImage img,
        IReadOnlyList<byte[]?>? mermaidImages, Func<string, Task<byte[]?>>? fetchRemoteImage, CancellationToken ct)
    {
        if (img.Source.StartsWith("mermaid:", StringComparison.Ordinal))
        {
            var idx = int.TryParse(img.Source.AsSpan("mermaid:".Length), out var n) ? n : -1;
            return mermaidImages is not null && idx >= 0 && idx < mermaidImages.Count ? mermaidImages[idx] : null;
        }
        if (img.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = img.Source.IndexOf(',');
            if (comma < 0) return null;
            try { return Convert.FromBase64String(img.Source[(comma + 1)..]); } catch { return null; }
        }
        if (img.Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || img.Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (fetchRemoteImage is not null)
            {
                try { return await fetchRemoteImage(img.Source); } catch { return null; }
            }
        }
        return null;
    }

    private async Task<string> UploadAndShareImageAsync(string accessToken, byte[] bytes, string mimeType, CancellationToken ct)
    {
        using var upload = new HttpRequestMessage(HttpMethod.Post, DriveUploadBase + "?uploadType=media")
        {
            Content = new ByteArrayContent(bytes),
        };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var upResp = await _http.SendAsync(upload, ct);
        var upJson = await ReadJsonAsync(upResp, ct);
        if (!upResp.IsSuccessStatusCode)
            throw new GoogleExportException($"Image upload to Drive failed ({upResp.StatusCode}): {upJson}");
        var fileId = upJson.GetProperty("id").GetString() ?? throw new GoogleExportException("Drive upload returned no file id.");

        // Share anyone-with-link (reader) so the image renders even when the doc is shared.
        using var perm = new HttpRequestMessage(HttpMethod.Post, $"{DriveBase}/{fileId}/permissions")
        {
            Content = Json(new { role = "reader", type = "anyone" }),
        };
        perm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var permResp = await _http.SendAsync(perm, ct);
        if (!permResp.IsSuccessStatusCode)
        {
            // Sharing is best-effort — the image still renders for the owner.
        }

        return "https://drive.google.com/uc?export=view&id=" + fileId;
    }

    // ---- phase 3: tables ----------------------------------------------------------------------

    private async Task InsertTablesAsync(string docId, string accessToken, GoogleDocsDocumentBuilder.GoogleDocsBuildResult build, CancellationToken ct)
    {
        // 1. Replace each token paragraph with a real native table (created at its position).
        foreach (var table in build.Tables)
        {
            var token = $"[[TBL_{table.Order}]]";
            var index = await FindTokenIndexAsync(docId, accessToken, token, ct);
            if (index < 0) continue;
            var rows = Math.Max(table.Rows.Count, 1);
            var cols = table.Rows.Count > 0 ? Math.Max(table.Rows[0].Count, 1) : 1;
            await BatchUpdateAsync(docId, accessToken, new object[]
            {
                DeleteRange(index, index + token.Length),
                new { insertTable = new { rows, columns = cols, location = new { index } } },
            }, ct);
        }

        // 2. One read of the final doc to learn every cell's real start index, then fill all cells
        //    (highest index first so inserts never shift an unfilled cell).
        if (build.Tables.Count == 0) return;
        var doc = await GetDocumentAsync(docId, accessToken, ct);
        var fills = new List<(int Start, string Text, bool Header)>();
        foreach (var (table, n) in build.Tables.Select((t, n) => (t, n)))
        {
            var tableEl = FindNthTable(doc, n);
            if (tableEl is null) continue;
            var rows = tableEl.Value.GetProperty("tableRows").EnumerateArray().ToList();
            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r].GetProperty("tableCells").EnumerateArray().ToList();
                for (int c = 0; c < cells.Count; c++)
                {
                    var start = cells[c].GetProperty("startIndex").GetInt32();
                    var text = r < table.Rows.Count && c < table.Rows[r].Count ? table.Rows[r][c] : "";
                    fills.Add((start, text, r == 0 && table.Rows.Count > 1));
                }
            }
        }

        var requests = new List<object>();
        foreach (var f in fills.OrderByDescending(f => f.Start))
        {
            requests.Add(new { insertText = new { location = new { index = f.Start }, text = f.Text + "\n" } });
            if (f.Header && f.Text.Length > 0)
                requests.Add(new { updateTextStyle = new { range = new { startIndex = f.Start, endIndex = f.Start + f.Text.Length }, textStyle = new { bold = true } } });
        }
        if (requests.Count > 0) await BatchUpdateAsync(docId, accessToken, requests, ct);
    }

    private static JsonElement? FindNthTable(JsonElement doc, int n)
    {
        int seen = -1;
        foreach (var el in doc.GetProperty("body").GetProperty("content").EnumerateArray())
        {
            if (!el.TryGetProperty("table", out var table)) continue;
            if (++seen == n) return table.Clone();
        }
        return null;
    }

    // ---- Docs API plumbing --------------------------------------------------------------------

    private async Task<string> CreateDocumentAsync(string accessToken, string title, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, DocsBase) { Content = Json(new { title }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        var json = await ReadJsonAsync(resp, ct);
        if (!resp.IsSuccessStatusCode)
            throw new GoogleExportException($"Couldn't create the Google Doc ({resp.StatusCode}): {json}");
        return json.GetProperty("documentId").GetString() ?? throw new GoogleExportException("Google returned no document id.");
    }

    private async Task BatchUpdateAsync(string docId, string accessToken, IEnumerable<object> requests, CancellationToken ct)
    {
        var list = requests.ToList();
        if (list.Count == 0) return;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{DocsBase}/{docId}:batchUpdate")
        {
            Content = Json(new { requests = list }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var json = await ReadJsonAsync(resp, ct);
            throw new GoogleExportException($"Google Docs update failed ({resp.StatusCode}): {json}");
        }
    }

    private async Task<JsonElement> GetDocumentAsync(string docId, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{DocsBase}/{docId}?fields=body/content");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        var json = await ReadJsonAsync(resp, ct);
        if (!resp.IsSuccessStatusCode) throw new GoogleExportException($"Couldn't read the Google Doc ({resp.StatusCode}).");
        return json;
    }

    private async Task<int> FindTokenIndexAsync(string docId, string accessToken, string token, CancellationToken ct)
    {
        var doc = await GetDocumentAsync(docId, accessToken, ct);
        foreach (var el in doc.GetProperty("body").GetProperty("content").EnumerateArray())
        {
            if (!el.TryGetProperty("paragraph", out var para)) continue;
            if (!para.TryGetProperty("elements", out var els)) continue;
            foreach (var e in els.EnumerateArray())
            {
                if (!e.TryGetProperty("textRun", out var tr)) continue;
                var content = tr.GetProperty("content").GetString() ?? "";
                var i = content.IndexOf(token, StringComparison.Ordinal);
                if (i >= 0) return tr.GetProperty("startIndex").GetInt32() + i;
            }
        }
        return -1;
    }

    private static object DeleteRange(int start, int end) =>
        new { deleteContentRange = new { range = new { startIndex = start, endIndex = end } } };

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body, GoogleDocsDocumentBuilder.JsonOpts), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        try { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }
}

public sealed class GoogleExportException : Exception
{
    public GoogleExportException(string message) : base(message) { }
}
