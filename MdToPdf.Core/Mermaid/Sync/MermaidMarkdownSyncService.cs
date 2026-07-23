namespace MdToPdf.Mermaid.Sync;

using Markdig;
using Markdig.Syntax;
using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;

public sealed class MermaidBlockInfo
{
    public int BlockIndex { get; set; }
    public string Code { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
}

public static class MermaidMarkdownSyncService
{
    public static List<MermaidBlockInfo> ExtractMermaidBlocks(string markdown)
    {
        var result = new List<MermaidBlockInfo>();
        if (string.IsNullOrEmpty(markdown))
            return result;

        var pipeline = new MarkdownPipelineBuilder().Build();
        var doc = Markdown.Parse(markdown, pipeline);

        int index = 0;
        foreach (var block in doc.Descendants<FencedCodeBlock>())
        {
            string info = block.Info?.Trim() ?? string.Empty;
            if (info.Equals("mermaid", StringComparison.OrdinalIgnoreCase) || info.Equals("language-mermaid", StringComparison.OrdinalIgnoreCase))
            {
                string code = GetCodeBlockText(markdown, block);
                result.Add(new MermaidBlockInfo
                {
                    BlockIndex = index++,
                    Code = code,
                    StartOffset = block.Span.Start,
                    EndOffset = block.Span.End
                });
            }
        }

        return result;
    }

    public static string ReplaceMermaidBlock(string markdown, int blockIndex, string newMermaidCode)
    {
        var blocks = ExtractMermaidBlocks(markdown);
        if (blockIndex < 0 || blockIndex >= blocks.Count)
            throw new ArgumentOutOfRangeException(nameof(blockIndex), $"Block index {blockIndex} is out of range. Found {blocks.Count} mermaid blocks.");

        var target = blocks[blockIndex];
        string formattedFence = $"```mermaid\n{newMermaidCode.Trim()}\n```";

        return markdown[..target.StartOffset] + formattedFence + markdown[(target.EndOffset + 1)..];
    }

    public static string SyncAstToMarkdown(string markdown, int blockIndex, MermaidDiagramAst ast, GeneratorOptions? options = null)
    {
        string generatedCode = MermaidCodeGenerator.Generate(ast, options);
        return ReplaceMermaidBlock(markdown, blockIndex, generatedCode);
    }

    private static string GetCodeBlockText(string markdown, FencedCodeBlock block)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in block.Lines.Lines)
        {
            if (line.Slice.Text != null)
            {
                sb.AppendLine(line.Slice.ToString());
            }
        }
        return sb.ToString().TrimEnd();
    }
}
