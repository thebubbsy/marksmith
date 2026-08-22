using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Api;

public record ApiParam(string Name, string Type, bool IsRequired, string Description);

public class ApiEndpointSpec
{
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public string Summary { get; set; } = string.Empty;
    public List<ApiParam> Parameters { get; } = new();
    public string? ResponseJson { get; set; }
}

/// <summary>
/// Service for parsing Markdown REST API specifications and rendering interactive endpoint cards with HTTP method badges.
/// </summary>
public static class MarkdownApiSpecService
{
    private static readonly Regex ApiFenceRegex = new(
        @":::api\s+(GET|POST|PUT|DELETE|PATCH)\s+([^\r\n]+)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ParamLineRegex = new(
        @"^Param:\s*([a-zA-Z0-9_\-]+)\s*\(([^,)]+)(?:,\s*(required|optional))?\)\s*-\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Transforms all :::api endpoint blocks in Markdown into interactive HTML documentation cards.
    /// </summary>
    public static string TransformApiSpecs(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        return ApiFenceRegex.Replace(markdown, match =>
        {
            string method = match.Groups[1].Value.ToUpperInvariant();
            string path = match.Groups[2].Value.Trim();
            string body = match.Groups[3].Value.Trim();

            var spec = ParseEndpointBody(method, path, body);
            return RenderEndpointHtml(spec);
        });
    }

    private static ApiEndpointSpec ParseEndpointBody(string method, string path, string body)
    {
        var spec = new ApiEndpointSpec { Method = method, Path = path };
        var lines = body.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                spec.Summary = line.Substring(8).Trim();
            }
            else if (line.StartsWith("Response:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Response 200:", StringComparison.OrdinalIgnoreCase))
            {
                int colonIdx = line.IndexOf(':');
                spec.ResponseJson = line.Substring(colonIdx + 1).Trim();
            }
        }

        foreach (Match pm in ParamLineRegex.Matches(body))
        {
            string name = pm.Groups[1].Value.Trim();
            string type = pm.Groups[2].Value.Trim();
            bool req = pm.Groups[3].Success && pm.Groups[3].Value.Equals("required", StringComparison.OrdinalIgnoreCase);
            string desc = pm.Groups[4].Value.Trim();
            spec.Parameters.Add(new ApiParam(name, type, req, desc));
        }

        return spec;
    }

    private static string RenderEndpointHtml(ApiEndpointSpec spec)
    {
        string badgeClass = spec.Method switch
        {
            "GET" => "ms-api-get",
            "POST" => "ms-api-post",
            "PUT" => "ms-api-put",
            "DELETE" => "ms-api-delete",
            _ => "ms-api-default"
        };

        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"ms-api-card\">");
        sb.AppendLine("  <div class=\"ms-api-header\">");
        sb.AppendLine($"    <span class=\"ms-api-badge {badgeClass}\">{spec.Method}</span>");
        sb.AppendLine($"    <code class=\"ms-api-path\">{System.Net.WebUtility.HtmlEncode(spec.Path)}</code>");
        if (!string.IsNullOrEmpty(spec.Summary))
        {
            sb.AppendLine($"    <span class=\"ms-api-summary\">{System.Net.WebUtility.HtmlEncode(spec.Summary)}</span>");
        }
        sb.AppendLine("  </div>");

        if (spec.Parameters.Count > 0)
        {
            sb.AppendLine("  <div class=\"ms-api-params\">");
            sb.AppendLine("    <table class=\"ms-api-table\">");
            sb.AppendLine("      <thead><tr><th>Parameter</th><th>Type</th><th>Required</th><th>Description</th></tr></thead>");
            sb.AppendLine("      <tbody>");
            foreach (var p in spec.Parameters)
            {
                string reqBadge = p.IsRequired ? "<span class=\"ms-req-badge\">required</span>" : "<span class=\"ms-opt-badge\">optional</span>";
                sb.AppendLine($"        <tr><td><code>{p.Name}</code></td><td><em>{p.Type}</em></td><td>{reqBadge}</td><td>{System.Net.WebUtility.HtmlEncode(p.Description)}</td></tr>");
            }
            sb.AppendLine("      </tbody>");
            sb.AppendLine("    </table>");
            sb.AppendLine("  </div>");
        }

        sb.AppendLine("</div>");
        return sb.ToString();
    }
}
