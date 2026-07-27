using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MdToPdf.Services;

public sealed record FrontmatterResult
{
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public string Content { get; init; } = "";
}

/// <summary>
/// Markdown Frontmatter Metadata Parser Engine (Task 28). Extracts YAML frontmatter blocks
/// delimited by --- at document start and returns key-value pairs along with stripped markdown.
/// </summary>
public static class FrontmatterService
{
    private static readonly Regex FrontmatterRe = new(
        @"^\s*---\r?\n([\s\S]*?)\r?\n---\r?\n?",
        RegexOptions.Compiled);

    public static FrontmatterResult Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new FrontmatterResult { Content = markdown ?? "" };
        }

        var match = FrontmatterRe.Match(markdown);
        if (!match.Success)
        {
            return new FrontmatterResult { Content = markdown };
        }

        var yamlBlock = match.Groups[1].Value;
        var strippedContent = markdown.Substring(match.Length);
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = yamlBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = line.Substring(0, colonIdx).Trim();
                var val = line.Substring(colonIdx + 1).Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(key))
                {
                    meta[key] = val;
                }
            }
        }

        return new FrontmatterResult
        {
            Metadata = meta,
            Content = strippedContent
        };
    }
}
