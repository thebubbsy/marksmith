using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.AdvancedFeatures
{
    // ───────────────────────────────────────────────────────────────────
    // Shared helper used by all detectors
    // ───────────────────────────────────────────────────────────────────
    internal static class DetectorHelpers
    {
        // The pipeline tests every detector against one block at a time (sequentially), so a
        // single-slot memo lets all detectors — plus the pipeline's own ParseBlock — share ONE
        // Split('\n') of the raw block instead of re-splitting the same text up to a dozen times.
        // The cache is a single immutable tuple swapped atomically, so it stays correct even if
        // the pipeline is ever reused concurrently (worst case: a harmless redundant re-split).
        private static (string Block, string[] Lines)? _splitCache;

        /// <summary>
        /// Splits a raw block into lines, memoized for the most recently seen block.
        /// </summary>
        public static string[] SplitLines(string rawBlock)
        {
            var cache = _splitCache;
            if (cache is { } c && string.Equals(c.Block, rawBlock, StringComparison.Ordinal))
                return c.Lines;
            var lines = rawBlock.TrimStart('\r', '\n').Split('\n');
            _splitCache = (rawBlock, lines);
            return lines;
        }

        /// <summary>
        /// Extracts the inner lines of a ::: block (everything between the opening marker and
        /// the closing :::), stripping the first and last lines.
        /// </summary>
        public static List<string> GetInnerLines(string rawBlock)
        {
            var lines = SplitLines(rawBlock);
            var result = new List<string>();
            for (int i = 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimEnd('\r');
                if (i == lines.Length - 1 && trimmed.Trim() == ":::") break;
                if (i == lines.Length - 2 && trimmed.Trim() == ":::" && string.IsNullOrWhiteSpace(lines[^1])) break;
                result.Add(trimmed);
            }
            return result;
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 1. AI Context — highest precedence.  Validates YAML-like key: value pairs.
    // ───────────────────────────────────────────────────────────────────
    public class AiContextDetector : IFeatureDetector
    {
        public string FeatureName => "AI Context";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::ai-context", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var lines = DetectorHelpers.GetInnerLines(rawBlock);

            // Must have at least one key: value pair
            var kvLines = lines.Where(l => l.Contains(':')).ToList();
            if (kvLines.Count == 0)
                errors.Add("No YAML key:value pairs found");

            // Check for expected keys
            var keys = kvLines.Select(l => l.Split(':')[0].Trim().ToLowerInvariant()).ToHashSet();
            if (!keys.Contains("model") && !keys.Contains("timestamp") && !keys.Contains("prompthash"))
                errors.Add("Expected at least one of: model, timestamp, promptHash");

            double conf = errors.Count == 0 ? 0.95 : 0.5;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. Datagrid — validates that the inner content has a markdown table or CSV-like data.
    // ───────────────────────────────────────────────────────────────────
    public class DatagridDetector : IFeatureDetector
    {
        public string FeatureName => "Datagrid";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::datagrid", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var lines = DetectorHelpers.GetInnerLines(rawBlock);

            // Look for pipe-delimited table rows or comma-separated data
            var tableRows = lines.Where(l => l.Contains('|')).ToList();
            var csvRows = lines.Where(l => l.Contains(',')).ToList();

            if (tableRows.Count < 2 && csvRows.Count < 2)
                errors.Add("No markdown table or CSV data found (need header + at least 1 row)");

            // Check column consistency for tables
            if (tableRows.Count >= 2)
            {
                var colCounts = tableRows
                    .Where(l => !Regex.IsMatch(l.Trim(), @"^\|[\s\-:|]+\|$")) // skip separator rows
                    .Select(l => l.Split('|', StringSplitOptions.RemoveEmptyEntries).Length)
                    .Distinct().ToList();
                if (colCounts.Count > 1)
                    errors.Add($"Inconsistent column counts: {string.Join(", ", colCounts)}");
            }

            double conf = errors.Count == 0 ? 0.92 : 0.4;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. Chart — validates JSON spec or data block presence.
    // ───────────────────────────────────────────────────────────────────
    public class ChartDetector : IFeatureDetector
    {
        public string FeatureName => "Chart";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::chart", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var inner = string.Join('\n', DetectorHelpers.GetInnerLines(rawBlock));

            // Must contain either a JSON block (starts with {) or explicit spec:/data: keys
            bool hasJson = inner.TrimStart().StartsWith("{") || inner.TrimStart().StartsWith("[");
            bool hasSpec = inner.Contains("spec:", StringComparison.OrdinalIgnoreCase) ||
                           inner.Contains("data:", StringComparison.OrdinalIgnoreCase);

            if (!hasJson && !hasSpec)
                errors.Add("No JSON spec or data: block found");

            // Quick JSON brace balance check
            if (hasJson)
            {
                int braces = inner.Count(c => c == '{') - inner.Count(c => c == '}');
                int brackets = inner.Count(c => c == '[') - inner.Count(c => c == ']');
                if (braces != 0) errors.Add($"Unbalanced JSON braces (diff: {braces})");
                if (brackets != 0) errors.Add($"Unbalanced JSON brackets (diff: {brackets})");
            }

            double conf = errors.Count == 0 ? 0.90 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 4. SmartArt — validates type attribute and node list.
    // ───────────────────────────────────────────────────────────────────
    public class SmartArtDetector : IFeatureDetector
    {
        public string FeatureName => "SmartArt";
        public double Threshold => 0.85;

        private static readonly HashSet<string> ValidTypes = new(StringComparer.OrdinalIgnoreCase)
            { "process", "hierarchy", "cycle", "list", "relationship", "matrix", "pyramid", "venn", "picturelist", "mosaic", "org", "tree", "workflow", "target", "grid", "basic", "default" };

        // Compiled once — the type attribute check ran interpreted on every validated block.
        private static readonly Regex TypeAttrRegex = new(@"type=[""']?([^""'\s>]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::smartart", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var firstLine = DetectorHelpers.SplitLines(rawBlock)[0];

            // Check for type attribute — OPTIONAL: type-less blocks are auto-classified from
            // content shape (SmartArtLayoutSuggester), so "make it SmartArt" never has to
            // force a hierarchy. When given, it must be a known family or layout alias.
            var typeMatch = TypeAttrRegex.Match(firstLine);
            if (typeMatch.Success)
            {
                var val = typeMatch.Groups[1].Value;
                if (!ValidTypes.Contains(val) && Glox.SmartArtLayoutCatalog.Shared.TryResolve(val) == null)
                    errors.Add($"Unknown SmartArt type: '{val}'. Valid: {string.Join(", ", ValidTypes)} or any layout URN/alias");
            }

            // Must have at least 2 nodes (list items or non-empty lines)
            var lines = DetectorHelpers.GetInnerLines(rawBlock);
            var nodeLines = lines.Where(l => l.TrimStart().StartsWith("-") || l.TrimStart().StartsWith("*")).ToList();
            if (nodeLines.Count < 2)
            {
                // Fallback: count non-empty lines as nodes
                var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                if (nonEmpty.Count < 2)
                    errors.Add($"Need at least 2 nodes, found {nonEmpty.Count}");
            }

            double conf = errors.Count == 0 ? 0.92 : 0.4;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // Shapes — validates :::shapes container blocks (MLShape compositions).
    // ───────────────────────────────────────────────────────────────────
    public class ShapesDetector : IFeatureDetector
    {
        public string FeatureName => "Shapes";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::shapes", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var inner = string.Join("\n", DetectorHelpers.GetInnerLines(rawBlock));
            try
            {
                var shapes = MarkSmith.Core.Composer.ShapeMarkdownCodec.Parse(inner);
                if (shapes.Count == 0)
                {
                    errors.Add("No valid shape lines found.");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Malformed shapes block: {ex.Message}");
            }

            double conf = errors.Count == 0 ? 0.95 : 0.4;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // Kanban — validates :::kanban container blocks.
    // ───────────────────────────────────────────────────────────────────
    public class KanbanDetector : IFeatureDetector
    {
        public string FeatureName => "Kanban";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            Regex.IsMatch(rawBlock, @"^\s*:::+\s*kanban\b", RegexOptions.IgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var lines = DetectorHelpers.GetInnerLines(rawBlock);

            var headerLines = lines.Where(l => l.TrimStart().StartsWith("#")).ToList();
            var bulletLines = lines.Where(l =>
            {
                var t = l.TrimStart();
                return t.StartsWith("-") || t.StartsWith("*") || t.StartsWith("+") || Regex.IsMatch(t, @"^\d+[\.\)]");
            }).ToList();

            if (headerLines.Count == 0 && bulletLines.Count == 0)
            {
                errors.Add("No column headers (# Column) or card items (- Task) found inside :::kanban block");
            }

            double conf = errors.Count == 0 ? 0.92 : 0.4;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 5. Workflow — validates unique step IDs/titles.
    // ───────────────────────────────────────────────────────────────────
    public class WorkflowDetector : IFeatureDetector
    {
        public string FeatureName => "Workflow";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::workflow", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var lines = DetectorHelpers.GetInnerLines(rawBlock);

            // Look for step: entries or list items
            var stepLines = lines.Where(l =>
                l.TrimStart().StartsWith("step:", StringComparison.OrdinalIgnoreCase) ||
                l.TrimStart().StartsWith("- ")).ToList();

            if (stepLines.Count < 2)
                errors.Add($"Need at least 2 workflow steps, found {stepLines.Count}");

            // Check for duplicate step names
            var stepNames = stepLines.Select(l => l.Trim().TrimStart('-', '*').Trim()).ToList();
            var dupes = stepNames.GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0)
                errors.Add($"Duplicate step names: {string.Join(", ", dupes)}");

            double conf = errors.Count == 0 ? 0.90 : 0.4;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 6. Timeline — validates ISO date fields.
    // ───────────────────────────────────────────────────────────────────
    public class TimelineDetector : IFeatureDetector
    {
        public string FeatureName => "Timeline";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::timeline", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var lines = DetectorHelpers.GetInnerLines(rawBlock);

            // Look for date: fields
            var dateLines = lines.Where(l => l.Contains("date:", StringComparison.OrdinalIgnoreCase)).ToList();
            if (dateLines.Count == 0)
                errors.Add("No date: fields found");

            // Validate ISO date format
            foreach (var dl in dateLines)
            {
                var parts = dl.Split(':', 2);
                var datePart = parts.Length > 1 ? parts[1].Trim() : "";
                if (!DateTime.TryParse(datePart, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _))
                    errors.Add($"Invalid date format: '{datePart}' (expected ISO 8601)");
            }

            double conf = errors.Count == 0 ? 0.92 : 0.4;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 7. Tabs — validates tab title attributes and == / === separators.
    // ───────────────────────────────────────────────────────────────────
    public class TabsDetector : IFeatureDetector
    {
        public string FeatureName => "Tabs";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::tabs", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var lines = DetectorHelpers.GetInnerLines(rawBlock);

            bool inCodeFence = false;
            string? codeFenceMarker = null;
            var tabLines = new List<string>();
            var tabContainers = new List<string>();

            foreach (var l in lines)
            {
                var trimmed = l.TrimStart();
                if (!inCodeFence)
                {
                    if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                    {
                        inCodeFence = true;
                        codeFenceMarker = trimmed.StartsWith("```") ? "```" : "~~~";
                        continue;
                    }
                }
                else
                {
                    if (trimmed.StartsWith(codeFenceMarker!))
                    {
                        inCodeFence = false;
                        codeFenceMarker = null;
                    }
                    continue;
                }

                if (trimmed.StartsWith(":::tab", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(trimmed, @"^={2,3}\s+\S"))
                {
                    tabLines.Add(trimmed);
                }

                if (trimmed.StartsWith(":::tab", StringComparison.OrdinalIgnoreCase))
                {
                    tabContainers.Add(trimmed);
                }
            }

            if (tabLines.Count == 0)
                errors.Add("No :::tab or == tab header children found inside :::tabs block");

            foreach (var tl in tabContainers)
            {
                var trimmed = tl.Trim();
                if (!trimmed.Contains("title=", StringComparison.OrdinalIgnoreCase) && trimmed.Equals(":::tab", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Tab missing title attribute: '{trimmed}'");
            }

            double conf = errors.Count == 0 ? 0.92 : 0.5;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 8. Embed — validates provider and src URL.
    // ───────────────────────────────────────────────────────────────────
    public class EmbedDetector : IFeatureDetector
    {
        public string FeatureName => "Embed";
        public double Threshold => 0.6;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::embed", StringComparison.OrdinalIgnoreCase);

        // Compiled once — interpreted per validated embed block.
        private static readonly Regex SrcAttrRegex = new(@"src=""?([^""\s]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var firstLine = DetectorHelpers.SplitLines(rawBlock)[0];

            // Check for src attribute
            var srcMatch = SrcAttrRegex.Match(firstLine);
            if (!srcMatch.Success)
                errors.Add("Missing src attribute (e.g., src=\"https://youtube.com/...\")");
            else if (!Uri.TryCreate(srcMatch.Groups[1].Value, UriKind.Absolute, out _))
                errors.Add($"Invalid URL in src: '{srcMatch.Groups[1].Value}'");

            double conf = errors.Count == 0 ? 0.90 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 9. Canvas — validates SVG content or path commands.
    // ───────────────────────────────────────────────────────────────────
    public class CanvasDetector : IFeatureDetector
    {
        public string FeatureName => "Canvas";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::canvas", StringComparison.OrdinalIgnoreCase);

        // Compiled once — interpreted per validated canvas block.
        private static readonly Regex PathCmdRegex = new(@"[MLHVCSQTAZmlhvcsqtaz]\s*[\d.-]", RegexOptions.Compiled);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var inner = string.Join('\n', DetectorHelpers.GetInnerLines(rawBlock));

            // Must contain SVG markup or path commands
            bool hasSvg = inner.Contains("<svg", StringComparison.OrdinalIgnoreCase) ||
                          inner.Contains("<path", StringComparison.OrdinalIgnoreCase);
            bool hasPathCmds = PathCmdRegex.IsMatch(inner);

            if (!hasSvg && !hasPathCmds)
                errors.Add("No SVG markup or path commands found");

            double conf = errors.Count == 0 ? 0.90 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 10. Columns — validates count attribute (2..6).
    // ───────────────────────────────────────────────────────────────────
    public class ColumnsDetector : IFeatureDetector
    {
        public string FeatureName => "Columns";
        public double Threshold => 0.6;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::columns", StringComparison.OrdinalIgnoreCase);

        // Compiled once — interpreted per validated columns block.
        private static readonly Regex CountAttrRegex = new(@"count=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var firstLine = DetectorHelpers.SplitLines(rawBlock)[0];

            // Parse count attribute (default to 2 if missing — that's fine)
            var countMatch = CountAttrRegex.Match(firstLine);
            if (countMatch.Success)
            {
                int count = int.Parse(countMatch.Groups[1].Value);
                if (count < 2 || count > 6)
                    errors.Add($"Column count must be 2-6, got {count}");
            }

            // Must have some inner content
            var lines = DetectorHelpers.GetInnerLines(rawBlock);
            if (lines.All(l => string.IsNullOrWhiteSpace(l)))
                errors.Add("No content inside :::columns block");

            double conf = errors.Count == 0 ? 0.95 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 11. References — validates @id citation entries.
    // ───────────────────────────────────────────────────────────────────
    public class ReferencesDetector : IFeatureDetector
    {
        public string FeatureName => "References";
        public double Threshold => 0.85;

        public bool Matches(string rawBlock) =>
            rawBlock.StartsWith(":::references", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var lines = DetectorHelpers.GetInnerLines(rawBlock);

            // Look for @id entries or BibTeX-like entries
            var refLines = lines.Where(l =>
                l.TrimStart().StartsWith("@") ||
                l.Contains("author:", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("title:", StringComparison.OrdinalIgnoreCase)).ToList();

            if (refLines.Count == 0)
                errors.Add("No @id references or BibTeX-like entries found");

            double conf = errors.Count == 0 ? 0.90 : 0.4;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 14. EngineeringDiagram — the Cycle 22–29 engineering/science fences (49 visualizers:
    // :::doppler, :::smith-chart, :::bode, …). The fence-name table lives in
    // MarkdownHtmlService.EngineeringDiagrams (the preview's dispatch table) so both pipelines
    // share ONE definition of which fences exist — the detector never keeps its own copy.
    // ───────────────────────────────────────────────────────────────────
    public class EngineeringDiagramDetector : IFeatureDetector
    {
        public string FeatureName => "EngineeringDiagram";
        public double Threshold => 0.9;

        public bool Matches(string rawBlock) =>
            MarkSmith.Services.MarkdownHtmlService.TryGetEngineeringFenceName(rawBlock, out _);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            // The fence name alone is authoritative (the preview lift pass makes no further
            // demands); rendering failures fall back to the source code block at render time,
            // exactly like the mermaid/plugin last-resort convention.
            return (true, 0.95, Array.Empty<string>());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 15. Watermark — validates vector watermark text, color, opacity, diagonal
    // ───────────────────────────────────────────────────────────────────
    public class WatermarkDetector : IFeatureDetector
    {
        public string FeatureName => "Watermark";
        public double Threshold => 0.75;

        public bool Matches(string rawBlock) =>
            rawBlock.TrimStart().StartsWith(":::watermark", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var firstLine = DetectorHelpers.SplitLines(rawBlock)[0];
            var innerLines = DetectorHelpers.GetInnerLines(rawBlock);

            string? text = null;
            var quotedMatch = Regex.Match(firstLine, @"^:::watermark\s+""([^""]+)""", RegexOptions.IgnoreCase);
            if (quotedMatch.Success)
            {
                text = quotedMatch.Groups[1].Value;
            }
            else
            {
                var textAttrMatch = Regex.Match(firstLine, @"(?:text=|--text\s+)(?:""([^""]*)""|(\S+))", RegexOptions.IgnoreCase);
                if (textAttrMatch.Success)
                {
                    text = textAttrMatch.Groups[1].Success ? textAttrMatch.Groups[1].Value : textAttrMatch.Groups[2].Value;
                }
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                var firstNonEmpty = innerLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(firstNonEmpty))
                {
                    text = firstNonEmpty.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                text = "CONFIDENTIAL";
            }

            var opacityMatch = Regex.Match(firstLine, @"(?:opacity=|--opacity\s+)(?:""([^""]*)""|([0-9.]+))", RegexOptions.IgnoreCase);
            if (opacityMatch.Success)
            {
                var opStr = opacityMatch.Groups[1].Success ? opacityMatch.Groups[1].Value : opacityMatch.Groups[2].Value;
                if (double.TryParse(opStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var opVal))
                {
                    if (opVal <= 0.0 || opVal > 1.0)
                        errors.Add($"Watermark opacity must be between 0.01 and 1.0, got {opVal}");
                }
            }

            double conf = errors.Count == 0 ? 0.95 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 16. LineNumbers — validates legal and academic line numbering options
    // ───────────────────────────────────────────────────────────────────
    public class LineNumbersDetector : IFeatureDetector
    {
        public string FeatureName => "LineNumbers";
        public double Threshold => 0.75;

        public bool Matches(string rawBlock) =>
            rawBlock.TrimStart().StartsWith(":::line-numbers", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var firstLine = DetectorHelpers.SplitLines(rawBlock)[0];

            var countMatch = Regex.Match(firstLine, @"(?:count-by=|countBy=|count=|--count-by\s+|--countBy\s+|--count\s+)(?:""([^""]*)""|(\d+))", RegexOptions.IgnoreCase);
            if (countMatch.Success)
            {
                var valStr = countMatch.Groups[1].Success ? countMatch.Groups[1].Value : countMatch.Groups[2].Value;
                if (int.TryParse(valStr, out int countVal) && countVal < 1)
                {
                    errors.Add($"Line number count-by must be at least 1, got {countVal}");
                }
            }

            double conf = errors.Count == 0 ? 0.95 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 17. CoverPage — validates executive cover page gallery metadata
    // ───────────────────────────────────────────────────────────────────
    public class CoverPageDetector : IFeatureDetector
    {
        public string FeatureName => "CoverPage";
        public double Threshold => 0.75;

        public bool Matches(string rawBlock) =>
            rawBlock.TrimStart().StartsWith(":::cover-page", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var firstLine = DetectorHelpers.SplitLines(rawBlock)[0];
            var innerLines = DetectorHelpers.GetInnerLines(rawBlock);

            bool hasTitle = Regex.IsMatch(firstLine, @"title=", RegexOptions.IgnoreCase) ||
                            innerLines.Any(l => Regex.IsMatch(l, @"^\s*title\s*[:=]", RegexOptions.IgnoreCase));
            bool hasAnyMeta = innerLines.Any(l => l.Contains(':') || l.Contains('=') || !string.IsNullOrWhiteSpace(l));

            if (!hasTitle && !hasAnyMeta && !firstLine.Contains('='))
            {
                errors.Add("Cover page must contain a title or metadata fields");
            }

            double conf = errors.Count == 0 ? 0.95 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 18. Parallel — validates bilingual and parallel synchronized columns
    // ───────────────────────────────────────────────────────────────────
    public class ParallelDetector : IFeatureDetector
    {
        public string FeatureName => "Parallel";
        public double Threshold => 0.75;

        public bool Matches(string rawBlock) =>
            rawBlock.TrimStart().StartsWith(":::parallel", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var innerLines = DetectorHelpers.GetInnerLines(rawBlock);
            if (innerLines.All(l => string.IsNullOrWhiteSpace(l)))
                errors.Add("No content inside :::parallel block");

            double conf = errors.Count == 0 ? 0.95 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 19. DropCap — validates editorial drop cap blocks
    // ───────────────────────────────────────────────────────────────────
    public class DropCapDetector : IFeatureDetector
    {
        public string FeatureName => "DropCap";
        public double Threshold => 0.75;

        public bool Matches(string rawBlock) =>
            rawBlock.TrimStart().StartsWith(":::dropcap", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var firstLine = DetectorHelpers.SplitLines(rawBlock)[0];
            var innerLines = DetectorHelpers.GetInnerLines(rawBlock);

            if (innerLines.All(l => string.IsNullOrWhiteSpace(l)))
                errors.Add("No paragraph content found inside :::dropcap block");

            var numMatch = Regex.Match(firstLine, @"^:::dropcap\s+(\d+)", RegexOptions.IgnoreCase);
            if (numMatch.Success)
            {
                if (int.TryParse(numMatch.Groups[1].Value, out int lVal) && lVal < 1)
                    errors.Add($"Drop cap line count must be at least 1, got {lVal}");
            }
            else
            {
                var linesMatch = Regex.Match(firstLine, @"(?:lines=|--lines\s+)(?:""([^""]*)""|(\d+))", RegexOptions.IgnoreCase);
                if (linesMatch.Success)
                {
                    var valStr = linesMatch.Groups[1].Success ? linesMatch.Groups[1].Value : linesMatch.Groups[2].Value;
                    if (int.TryParse(valStr, out int lVal) && lVal < 1)
                        errors.Add($"Drop cap line count must be at least 1, got {lVal}");
                }
            }

            double conf = errors.Count == 0 ? 0.95 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 20. Index — validates concordance & subject index generator blocks
    // ───────────────────────────────────────────────────────────────────
    public class IndexDetector : IFeatureDetector
    {
        public string FeatureName => "Index";
        public double Threshold => 0.75;

        public bool Matches(string rawBlock) =>
            rawBlock.TrimStart().StartsWith(":::index", StringComparison.OrdinalIgnoreCase);

        public (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock)
        {
            var errors = new List<string>();
            var firstLine = DetectorHelpers.SplitLines(rawBlock)[0];

            var colMatch = Regex.Match(firstLine, @"(?:columns=|count=|--columns\s+|--count\s+)(?:""([^""]*)""|(\d+))", RegexOptions.IgnoreCase);
            if (colMatch.Success)
            {
                var valStr = colMatch.Groups[1].Success ? colMatch.Groups[1].Value : colMatch.Groups[2].Value;
                if (int.TryParse(valStr, out int cVal) && cVal < 1)
                    errors.Add($"Index column count must be at least 1, got {cVal}");
            }

            double conf = errors.Count == 0 ? 0.95 : 0.3;
            return (errors.Count == 0, conf, errors.ToArray());
        }
    }
}
