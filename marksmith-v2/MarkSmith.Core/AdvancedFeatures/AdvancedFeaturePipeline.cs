using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.AdvancedFeatures
{
    public class MarkdownBlock
    {
        public required string RawText { get; init; }
        public required int Start { get; init; }
        public required int End { get; init; }
    }

    public class FeatureNode
    {
        public required MarkdownBlock Block { get; init; }
        public required IFeatureDetector Detector { get; init; }
        public required string StableId { get; init; }
        
        /// <summary>
        /// The inner content of the ::: block (everything between the opening marker line and the closing :::).
        /// Parsed out by the tokenizer so detectors and renderers don't have to re-parse it.
        /// </summary>
        public string InnerContent { get; init; } = "";
        
        /// <summary>
        /// Parsed attributes from the opening marker line (e.g., count=3 from :::columns count=3).
        /// </summary>
        public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class AdvancedFeaturePipeline
    {
        private readonly IFeatureDetector[] _detectorsInPrecedence;

        public AdvancedFeaturePipeline()
        {
            _detectorsInPrecedence = new IFeatureDetector[]
            {
                new AiContextDetector(),
                new DatagridDetector(),
                new ChartDetector(),
                new SmartArtDetector(),
                new ShapesDetector(),
                new KanbanDetector(),
                new WorkflowDetector(),
                new TimelineDetector(),
                new TabsDetector(),
                new EmbedDetector(),
                new CanvasDetector(),
                new ColumnsDetector(),
                new ReferencesDetector()
            };
        }

        // Shared, stateless instance reused across every export. The constructor wires up the 12
        // detectors, which used to happen on every single export; Process works entirely on locals
        // plus the immutable detector array, so one instance is safe to share (even concurrently).
        public static AdvancedFeaturePipeline Shared { get; } = new();

        public List<FeatureNode> Process(string markdown, string documentId)
        {
            var nodes = new List<FeatureNode>();
            var blocks = Tokenize(markdown);
            var reservedRanges = new List<(int Start, int End)>();

            foreach (var block in blocks)
            {
                if (IsReserved(block.Start, block.End, reservedRanges))
                    continue;

                foreach (var detector in _detectorsInPrecedence)
                {
                    if (detector.Matches(block.RawText))
                    {
                        var result = detector.Validate(block.RawText);
                        if (result.IsValid && result.Confidence >= detector.Threshold)
                        {
                            reservedRanges.Add((block.Start, block.End));

                            var (inner, attrs) = ParseBlock(block.RawText);
                            nodes.Add(new FeatureNode
                            {
                                Block = block,
                                Detector = detector,
                                StableId = StableIdGenerator.Generate(documentId, block.RawText, nodes.Count),
                                InnerContent = inner,
                                Attributes = attrs
                            });

                            System.Diagnostics.Debug.WriteLine(
                                $"[FEATURE-DETECT] {detector.FeatureName} (Conf: {result.Confidence:F2}) at {block.Start}-{block.End}");
                            break; // Stop at first valid match in precedence order
                        }
                    }
                }
            }

            return nodes;
        }

        /// <summary>
        /// Generates a stable document ID from the markdown content itself, so the same text
        /// always produces the same StableIds across repeated exports.
        /// </summary>
        public static string ContentBasedDocumentId(string markdown)
        {
            // Static, thread-safe SHA256.HashData — no per-call SHA256.Create() allocation.
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(markdown));
            return Convert.ToHexString(hash[..8]).ToLowerInvariant();
        }

        private static bool IsReserved(int start, int end, List<(int Start, int End)> reservedRanges)
        {
            foreach (var range in reservedRanges)
            {
                if (start < range.End && end > range.Start)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Parse the opening line's key=value attributes and extract the inner content.
        /// E.g. ":::columns count=3\nHello\nWorld\n:::" → inner="Hello\nWorld", attrs={count: "3"}
        /// </summary>
        private static (string Inner, Dictionary<string, string> Attrs) ParseBlock(string rawBlock)
        {
            var lines = DetectorHelpers.SplitLines(rawBlock);
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Parse attributes from the first line: :::feature key=value key="quoted value"
            if (lines.Length > 0)
            {
                var firstLine = lines[0];
                var attrMatches = Regex.Matches(firstLine, @"(\w+)=(?:""([^""]*)""|(\S+))");
                foreach (Match m in attrMatches)
                {
                    var key = m.Groups[1].Value;
                    var val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                    attrs[key] = val;
                }
            }

            // Inner content: everything between the first line and the closing :::
            var innerLines = new List<string>();
            for (int i = 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimEnd('\r');
                if (trimmed == ":::" && i == lines.Length - 1)
                    break; // Skip the closing marker
                innerLines.Add(trimmed);
            }

            return (string.Join('\n', innerLines), attrs);
        }

        /// <summary>
        /// Tokenizer that finds top-level ::: blocks while properly skipping fenced code blocks
        /// (``` or ~~~) so that ::: inside code examples doesn't trigger false positives.
        /// </summary>
        private static List<MarkdownBlock> Tokenize(string markdown)
        {
            var blocks = new List<MarkdownBlock>();
            var lines = markdown.Split('\n');

            bool inCodeFence = false;
            string? codeFenceMarker = null;
            int blockStartIndex = -1;
            int blockStartLine = -1;
            string? blockOpenLine = null;

            int charIndex = 0;

            int depth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                var trimmed = line.TrimStart();

                // Track fenced code blocks (``` or ~~~) to avoid false ::: detection inside them
                if (!inCodeFence)
                {
                    if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                    {
                        inCodeFence = true;
                        codeFenceMarker = trimmed.StartsWith("```") ? "```" : "~~~";
                        charIndex += lines[i].Length + 1; // +1 for \n
                        continue;
                    }
                }
                else
                {
                    // Close the code fence only if the line starts with the same marker
                    if (trimmed.StartsWith(codeFenceMarker!))
                    {
                        inCodeFence = false;
                        codeFenceMarker = null;
                    }
                    charIndex += lines[i].Length + 1;
                    continue;
                }

                // Outside code fences: detect ::: blocks
                if (depth == 0)
                {
                    // Look for an opening ::: container marker (e.g., :::tabs, :::columns count=3)
                    if (Regex.IsMatch(trimmed, @"^:::(?!tab\b)[a-zA-Z]", RegexOptions.IgnoreCase))
                    {
                        blockStartIndex = charIndex;
                        blockStartLine = i;
                        blockOpenLine = line;
                        depth = 1;
                    }
                }
                else
                {
                    // We're inside a ::: container block — track depth for nested ::: containers
                    if (Regex.IsMatch(trimmed, @"^:::(?!tab\b)[a-zA-Z]", RegexOptions.IgnoreCase))
                    {
                        depth++;
                    }
                    else if (trimmed == ":::")
                    {
                        if (depth > 1)
                        {
                            depth--;
                        }
                        else // depth == 1
                        {
                            // Check if this ::: is closing an inner tab section before the next tab header line
                            bool isInnerTabClosing = false;
                            for (int j = i + 1; j < lines.Length; j++)
                            {
                                var nextLine = lines[j].TrimEnd('\r');
                                var nextTrimmed = nextLine.TrimStart();
                                if (string.IsNullOrWhiteSpace(nextTrimmed)) continue;
                                if (Regex.IsMatch(nextTrimmed, @"^:::tab(?:\s|$)", RegexOptions.IgnoreCase) ||
                                    Regex.IsMatch(nextTrimmed, @"^={2,3}\s+\S"))
                                {
                                    isInnerTabClosing = true;
                                }
                                break; // Check only first non-empty line
                            }

                            if (!isInnerTabClosing)
                            {
                                depth = 0;
                                var endIndex = charIndex + lines[i].Length;
                                var rawText = markdown.Substring(blockStartIndex, endIndex - blockStartIndex);

                                blocks.Add(new MarkdownBlock
                                {
                                    RawText = rawText,
                                    Start = blockStartIndex,
                                    End = endIndex
                                });

                                blockStartIndex = -1;
                                blockStartLine = -1;
                                blockOpenLine = null;
                            }
                        }
                    }
                }

                charIndex += lines[i].Length + 1; // +1 for \n
            }

            return blocks;
        }
    }
}
