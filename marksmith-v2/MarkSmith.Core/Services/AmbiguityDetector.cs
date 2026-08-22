namespace MarkSmith.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Markdig.Syntax;
using MarkSmith.Models;

public static class AmbiguityDetector
{
    private static readonly HashSet<string> KnownLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "mermaid", "plantuml", "graphviz", "dot",
        "csharp", "cs", "javascript", "js", "typescript", "ts",
        "html", "css", "xml", "json", "yaml", "yml", "sql", 
        "python", "py", "java", "c", "cpp", "c++",
        "bash", "sh", "powershell", "ps1", "php", "ruby", "rb", 
        "go", "rust", "rs", "swift", "kotlin", "kt", "fsharp", "fs",
        "plaintext", "text", "txt", "md", "markdown"
    };

    private static readonly Regex AsciiGridRegex = new(@"^\+[-=+]+\+$", RegexOptions.Compiled);

        public static List<AmbiguityCase> Detect(MarkdownDocument doc, string rawMarkdown)
    {
        var cases = new List<AmbiguityCase>();
        var lines = TextNormalizer.Newlines(rawMarkdown).Split('\n');
        var prefs = AppServices.Settings.Current.AmbiguityPreferences;
        
        var codeBlockRanges = doc.Descendants<FencedCodeBlock>()
            .Select(b => (Start: b.Line, End: b.Line + b.Lines.Count + 1))
            .ToList();

        bool IsInCodeBlock(int lineIndex) => codeBlockRanges.Any(r => lineIndex >= r.Start && lineIndex <= r.End);

        // 1 & 3: FencedCodeBlock checks
        foreach (var block in doc.Descendants<FencedCodeBlock>())
        {
            var info = block.Info?.Trim();
            
            // 1. DiagramSize
            if (string.Equals(info, "mermaid", StringComparison.OrdinalIgnoreCase))
            {
                if (prefs.Any(p => p.Kind == AmbiguityKind.DiagramSize)) continue;
                var content = block.Lines.ToString();
                try
                {
                    bool success = MermaidDocxRenderer.TryRender(
                        content, 
                        new ThemeDefinition("Test", "#FFFFFF", "#000000", "#000000", "#CCCCCC", "#DDDDDD", "#0000FF", "#00FF00", "#333333"), 
                        new AppSettings(), 
                        1, 
                        out var _, 
                        out var oversized, 
                        forceFit: false);

                    if (success && oversized)
                    {
                        cases.Add(new AmbiguityCase
                        {
                            Kind = AmbiguityKind.DiagramSize,
                            Description = "Mermaid diagram may be too large to fit on a printed page.",
                            SourceLine = block.Line,
                            SourceMarkdown = string.Join("\n", TextNormalizer.Newlines(content).Split('\n').Take(3)) + "...",
                            Options = new List<RenderOption>
                            {
                                new RenderOption { Label = "Scale to fit", Priority = 1 },
                                new RenderOption { Label = "Keep exact layout (Web View)", Priority = 2 }
                            }
                        });
                    }
                }
                catch
                {
                    // Ignore render errors for ambiguity detection
                }
            }
            // 3. UnknownFenceLanguage
            else if (!string.IsNullOrWhiteSpace(info) && !KnownLanguages.Contains(info.Split(' ')[0]))
            {
                if (prefs.Any(p => p.Kind == AmbiguityKind.UnknownFenceLanguage)) continue;
                cases.Add(new AmbiguityCase
                {
                    Kind = AmbiguityKind.UnknownFenceLanguage,
                    Description = $"Unrecognized code block language: {info}",
                    SourceLine = block.Line,
                    SourceMarkdown = $"```{info}",
                    Options = new List<RenderOption>
                    {
                        new RenderOption { Label = "Render as plain text", Priority = 1 },
                        new RenderOption { Label = "Attempt to highlight anyway", Priority = 2 }
                    }
                });
            }
        }

        // 2. GridTableOrAscii
        for (int i = 0; i < lines.Length; i++)
        {
            if (AsciiGridRegex.IsMatch(lines[i].Trim()))
            {
                if (!IsInCodeBlock(i) && !prefs.Any(p => p.Kind == AmbiguityKind.GridTableOrAscii))
                {
                    cases.Add(new AmbiguityCase
                    {
                        Kind = AmbiguityKind.GridTableOrAscii,
                        Description = "Ambiguous ASCII grid detected outside a code block.",
                        SourceLine = i,
                        SourceMarkdown = lines[i],
                        Options = new List<RenderOption>
                        {
                            new RenderOption { Label = "Render as Word Table", Priority = 1 },
                            new RenderOption { Label = "Render as plain text block", Priority = 2 }
                        }
                    });
                }
            }
        }

        return cases;
    }
}

