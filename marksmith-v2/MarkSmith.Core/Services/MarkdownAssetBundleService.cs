using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record ReferencedAsset(
    string AssetPath,
    string RelativeUri,
    string AssetType = "image",
    int LineNumber = 1);

public class AssetManifest
{
    public List<ReferencedAsset> Assets { get; } = new();
    public int TotalImages => Assets.Count(a => a.AssetType == "image");
    public int TotalLinks => Assets.Count(a => a.AssetType == "link");
}

/// <summary>
/// Service that scans Markdown documents for local and remote assets and creates self-contained packages.
/// </summary>
public static class MarkdownAssetBundleService
{
    private static readonly Regex ImageRegex = new(@"!\[([^\]]*)\]\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"(?<!!)\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    /// <summary>
    /// Scans Markdown and extracts all referenced media and linked file assets.
    /// </summary>
    public static AssetManifest ExtractManifest(string markdown, string? basePath = null)
    {
        var manifest = new AssetManifest();
        if (string.IsNullOrWhiteSpace(markdown))
            return manifest;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            // 1. Images
            foreach (Match m in ImageRegex.Matches(line))
            {
                string target = m.Groups[2].Value.Trim();
                string fullPath = ResolvePath(target, basePath);
                manifest.Assets.Add(new ReferencedAsset(fullPath, target, "image", lineNum));
            }

            // 2. Local File Links
            foreach (Match m in LinkRegex.Matches(line))
            {
                string target = m.Groups[2].Value.Trim();
                if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !target.StartsWith("#"))
                {
                    string fullPath = ResolvePath(target, basePath);
                    manifest.Assets.Add(new ReferencedAsset(fullPath, target, "link", lineNum));
                }
            }
        }

        return manifest;
    }

    private static string ResolvePath(string relative, string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || Path.IsPathRooted(relative))
            return relative;

        try
        {
            return Path.GetFullPath(Path.Combine(basePath, relative));
        }
        catch
        {
            return relative;
        }
    }
}
