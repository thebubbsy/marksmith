using System;
using System.Collections.Generic;
using System.Linq;
using ColorCode;
using ColorCode.Parsing;
using ColorCode.Styling;
using DocumentFormat.OpenXml.Wordprocessing;
using MdToPdf.Models;

namespace MdToPdf.Services;

public class OpenXmlSyntaxHighlighter
{
    private readonly ILanguageParser _parser;

    public OpenXmlSyntaxHighlighter()
    {
        var dict = System.Linq.Enumerable.ToDictionary(ColorCode.Languages.All, l => l.Id);
        _parser = new LanguageParser(
            new ColorCode.Compilation.LanguageCompiler(
                new System.Collections.Generic.Dictionary<string, ColorCode.Compilation.CompiledLanguage>(),
                new System.Threading.ReaderWriterLockSlim()),
            new ColorCode.Common.LanguageRepository(dict));
    }

    private static string NormalizeLanguageId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        id = id.Trim().ToLowerInvariant();
        return id switch
        {
            "cs" or "c#" or "csharp" => "c#",
            "rs" or "rust" => "c#", // ColorCode fallback tokenizer for Rust / C-like code
            "bib" or "bibtex" or "latex" or "tex" => "html", // ColorCode tokenizer for tag/key-value blocks
            "sh" or "bash" or "zsh" or "shell" or "powershell" or "ps1" => "c#",
            "yaml" or "yml" or "toml" or "ini" => "html",
            "js" or "javascript" or "jsx" => "javascript",
            "ts" or "typescript" or "tsx" => "typescript",
            "py" or "python" => "python",
            "cpp" or "c++" or "c" => "cpp",
            "html" or "htm" => "html",
            "css" => "css",
            "xml" => "xml",
            "json" => "json",
            "sql" => "sql",
            "php" => "php",
            "java" => "java",
            _ => id
        };
    }

    public IEnumerable<Run> GetHighlightedRuns(string sourceCode, string languageId, ThemeDefinition theme)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            yield break;
        }

        var normalizedId = NormalizeLanguageId(languageId);
        var language = ColorCode.Languages.FindById(normalizedId);
        if (language == null)
        {
            yield return new Run(new Text(sourceCode) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
            yield break;
        }

        var scopes = new List<Scope>();
        _parser.Parse(sourceCode, language, (parsedCode, captures) =>
        {
            scopes.AddRange(captures);
        });

        if (scopes == null || scopes.Count == 0)
        {
            yield return new Run(new Text(sourceCode) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
            yield break;
        }

        var boundaries = new HashSet<int>();
        boundaries.Add(0);
        boundaries.Add(sourceCode.Length);

        void AddBoundaries(Scope s)
        {
            boundaries.Add(s.Index);
            boundaries.Add(s.Index + s.Length);
            foreach (var child in s.Children)
            {
                AddBoundaries(child);
            }
        }

        foreach (var s in scopes)
        {
            AddBoundaries(s);
        }

        var sortedBoundaries = boundaries.OrderBy(x => x).ToList();

        Scope? FindDeepest(int pos, IList<Scope> searchScopes)
        {
            Scope? deepest = null;
            foreach (var s in searchScopes)
            {
                if (pos >= s.Index && pos < s.Index + s.Length)
                {
                    deepest = s;
                    var childMatch = FindDeepest(pos, s.Children);
                    if (childMatch != null)
                    {
                        deepest = childMatch;
                    }
                }
            }
            return deepest;
        }

        var styles = GetStyleDictionary(theme);
        var isDarkCode = !ThemeDefinition.IsLight(theme.Code);
        var fallbackHex = isDarkCode ? "D4D4D4" : "24292E";

        for (int i = 0; i < sortedBoundaries.Count - 1; i++)
        {
            int start = sortedBoundaries[i];
            int end = sortedBoundaries[i + 1];
            if (start == end) continue;

            string textSegment = sourceCode.Substring(start, end - start);
            var deepestScope = FindDeepest(start, scopes);

            var run = new Run(new Text(textSegment) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
            var rPr = new DocumentFormat.OpenXml.Wordprocessing.RunProperties();
            rPr.Append(new DocumentFormat.OpenXml.Wordprocessing.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });

            if (deepestScope != null && styles.Contains(deepestScope.Name))
            {
                var style = styles[deepestScope.Name];
                if (!string.IsNullOrEmpty(style.Foreground))
                {
                    var hex = style.Foreground.TrimStart('#');
                    if (hex.Length == 8 && hex.StartsWith("FF", StringComparison.OrdinalIgnoreCase))
                    {
                        hex = hex.Substring(2);
                    }
                    // HARD RULE: ContrastGuard ensures token color is high-contrast readable against code background!
                    hex = ContrastGuard.EnsureLegibleText(hex, theme.Code.TrimStart('#'), isDarkCode ? "E6EDF3" : "1F2328");
                    rPr.Append(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = hex });
                }
                else
                {
                    rPr.Append(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = fallbackHex });
                }

                if (style.Italic)
                {
                    rPr.Append(new DocumentFormat.OpenXml.Wordprocessing.Italic());
                }
                if (style.Bold)
                {
                    rPr.Append(new DocumentFormat.OpenXml.Wordprocessing.Bold());
                }
            }
            else
            {
                fallbackHex = ContrastGuard.EnsureLegibleText(fallbackHex, theme.Code.TrimStart('#'));
                rPr.Append(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = fallbackHex });
            }
            
            run.RunProperties = rPr;
            yield return run;
        }
    }

    private StyleDictionary GetStyleDictionary(ThemeDefinition theme)
    {
        bool isDark = !ThemeDefinition.IsLight(theme.Code);
        return isDark ? StyleDictionary.DefaultDark : StyleDictionary.DefaultLight;
    }
}
