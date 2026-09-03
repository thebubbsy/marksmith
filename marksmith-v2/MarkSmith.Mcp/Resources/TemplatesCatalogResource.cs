using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Resources;

public sealed class TemplatesCatalogResource : IMcpResource
{
    public string Uri => "marksmith://templates/catalog";
    public string Name => "MarkSmith Templates and Themes Catalog";
    public string Description => "Built-in Word styling themes, typography scales, palettes, and page geometry templates.";
    public string MimeType => "application/json";

    public Task<McpResourceResult> ReadAsync(CancellationToken ct = default)
    {
        var catalog = new
        {
            themes = new[]
            {
                new
                {
                    id = "github-light",
                    name = "GitHub Light",
                    description = "Clean modern developer aesthetic with blue accents and Consolas code blocks.",
                    primaryColor = "#0969DA",
                    fontBody = "Calibri",
                    fontHeading = "Segoe UI Semibold",
                    fontCode = "Consolas"
                },
                new
                {
                    id = "modern-corporate",
                    name = "Modern Corporate",
                    description = "Polished enterprise presentation theme with navy headers and crisp serif headings.",
                    primaryColor = "#1F4E79",
                    fontBody = "Aptos",
                    fontHeading = "Aptos Display",
                    fontCode = "Cascadia Code"
                },
                new
                {
                    id = "academic-paper",
                    name = "Academic Paper",
                    description = "Formal scholarly layout with serif body, precise line spacing, and centered equations.",
                    primaryColor = "#333333",
                    fontBody = "Times New Roman",
                    fontHeading = "Times New Roman Bold",
                    fontCode = "Courier New"
                },
                new
                {
                    id = "executive-slate",
                    name = "Executive Slate",
                    description = "High-contrast slate gray executive summary with subtle warm tinting.",
                    primaryColor = "#2F3E46",
                    fontBody = "Georgia",
                    fontHeading = "Arial",
                    fontCode = "Consolas"
                },
                new
                {
                    id = "nordic-blue",
                    name = "Nordic Blue",
                    description = "Minimalist Scandinavian design with icy cyan accents and spacious margins.",
                    primaryColor = "#2B6CB0",
                    fontBody = "Segoe UI",
                    fontHeading = "Segoe UI Light",
                    fontCode = "Consolas"
                }
            },
            pageGeometries = new[]
            {
                new { name = "Standard Letter", widthPt = 612.0, heightPt = 792.0, marginPt = 72.0 },
                new { name = "A4 Fixed Width", widthPt = 595.3, heightPt = 841.9, marginPt = 54.0 },
                new { name = "Executive", widthPt = 522.0, heightPt = 756.0, marginPt = 54.0 }
            }
        };

        string json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(McpResourceResult.FromText(Uri, json, MimeType));
    }
}
