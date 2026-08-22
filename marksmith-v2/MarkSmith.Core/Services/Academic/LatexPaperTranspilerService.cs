using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Academic;

public record PaperMetadata(string Title, List<string> Authors, string Institution, string Abstract);

/// <summary>
/// Service that transpiles Markdown academic research papers into standard compilable IEEE/ACM LaTeX documents.
/// </summary>
public static class LatexPaperTranspilerService
{
    private static readonly Regex FrontmatterRegex = new(@"^---\r?\n([\s\S]*?)\r?\n---", RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(@"^(#{1,4})\s+(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex CitationRegex = new(@"\[@([a-zA-Z0-9_\-]+)\]", RegexOptions.Compiled);
    private static readonly Regex DisplayMathRegex = new(@"\$\$([\s\S]*?)\$\$", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"\*\*([^*]+)\*\*", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new(@"\*([^*]+)\*", RegexOptions.Compiled);

    /// <summary>
    /// Transpiles a Markdown paper into complete standalone LaTeX document code.
    /// </summary>
    public static string TranspileToLatex(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        // 1. Extract metadata
        var meta = ExtractMetadata(markdown, out string bodyMarkdown);

        var sb = new StringBuilder();
        sb.AppendLine(@"\documentclass[conference]{IEEEtran}");
        sb.AppendLine(@"\usepackage{amsmath,amssymb,amsfonts}");
        sb.AppendLine(@"\usepackage{algorithmic}");
        sb.AppendLine(@"\usepackage{graphicx}");
        sb.AppendLine(@"\usepackage{textcomp}");
        sb.AppendLine(@"\usepackage{xcolor}");
        sb.AppendLine(@"\usepackage{hyperref}");
        sb.AppendLine(@"\usepackage{booktabs}");
        sb.AppendLine();
        sb.AppendLine(@"\begin{document}");
        sb.AppendLine();
        sb.AppendLine($@"\title{{{EscapeLatex(meta.Title)}}}");

        if (meta.Authors.Count > 0)
        {
            sb.AppendLine(@"\author{");
            for (int i = 0; i < meta.Authors.Count; i++)
            {
                sb.AppendLine($@"  \IEEEauthorblockN{{{EscapeLatex(meta.Authors[i])}}}");
                if (!string.IsNullOrEmpty(meta.Institution))
                    sb.AppendLine($@"  \IEEEauthorblockA{{\textit{{{EscapeLatex(meta.Institution)}}}}}");
                if (i < meta.Authors.Count - 1) sb.AppendLine(@"  \and");
            }
            sb.AppendLine(@"}");
        }

        sb.AppendLine();
        sb.AppendLine(@"\maketitle");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(meta.Abstract))
        {
            sb.AppendLine(@"\begin{abstract}");
            sb.AppendLine(EscapeLatex(meta.Abstract));
            sb.AppendLine(@"\end{abstract}");
            sb.AppendLine();
        }

        // 2. Transpile Body Content
        string texBody = TranspileBody(bodyMarkdown);
        sb.AppendLine(texBody);

        sb.AppendLine();
        sb.AppendLine(@"\end{document}");
        return sb.ToString();
    }

    private static string TranspileBody(string md)
    {
        // Math blocks
        md = DisplayMathRegex.Replace(md, m =>
        {
            return $"\n\\begin{{equation}}\n{m.Groups[1].Value.Trim()}\n\\end{{equation}}\n";
        });

        // Citations
        md = CitationRegex.Replace(md, "\\cite{$1}");

        // Headings
        md = HeadingRegex.Replace(md, m =>
        {
            int level = m.Groups[1].Value.Length;
            string title = EscapeLatex(m.Groups[2].Value.Trim());
            return level switch
            {
                1 => $"\n\\section{{{title}}}\n",
                2 => $"\n\\subsection{{{title}}}\n",
                3 => $"\n\\subsubsection{{{title}}}\n",
                _ => $"\n\\paragraph{{{title}}}\n"
            };
        });

        // Bold & Italic
        md = BoldRegex.Replace(md, "\\textbf{$1}");
        md = ItalicRegex.Replace(md, "\\textit{$1}");

        return md.Trim();
    }

    private static PaperMetadata ExtractMetadata(string markdown, out string body)
    {
        string title = "Research Paper";
        var authors = new List<string>();
        string inst = "";
        string abs = "";

        body = markdown;
        var m = FrontmatterRegex.Match(markdown);
        if (m.Success)
        {
            body = markdown.Substring(m.Length).Trim();
            var lines = m.Groups[1].Value.Split('\n');
            foreach (var line in lines)
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2)
                {
                    string k = parts[0].Trim().ToLowerInvariant();
                    string v = parts[1].Trim().Trim('"', '\'');
                    if (k == "title") title = v;
                    else if (k == "institution" || k == "affiliation") inst = v;
                    else if (k == "abstract") abs = v;
                    else if (k == "author" || k == "authors")
                    {
                        authors.AddRange(v.Split(',').Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)));
                    }
                }
            }
        }

        if (authors.Count == 0) authors.Add("Author Name");
        return new PaperMetadata(title, authors, inst, abs);
    }

    private static string EscapeLatex(string text)
    {
        return text.Replace("&", "\\&")
                   .Replace("%", "\\%")
                   .Replace("$", "\\$")
                   .Replace("#", "\\#")
                   .Replace("_", "\\_");
    }
}
