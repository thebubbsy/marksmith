using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Resources;

public sealed class SyntaxContractResource : IMcpResource
{
    public string Uri => "marksmith://governance/syntax-contract";
    public string Name => "MarkSmith Syntax Contract & Governance";
    public string Description => "Markdown engine governance and syntax contract specification (docs/MD_ENGINE_GOVERNANCE.md)";
    public string MimeType => "text/markdown";

    public async Task<McpResourceResult> ReadAsync(CancellationToken ct = default)
    {
        string? contractPath = FindGovernanceDoc();
        string content;
        if (contractPath != null && File.Exists(contractPath))
        {
            content = await File.ReadAllTextAsync(contractPath, Encoding.UTF8, ct);
        }
        else
        {
            content = GetFallbackContract();
        }

        return McpResourceResult.FromText(Uri, content, MimeType);
    }

    private static string? FindGovernanceDoc()
    {
        string[] candidatePaths =
        {
            "docs/MD_ENGINE_GOVERNANCE.md",
            "../docs/MD_ENGINE_GOVERNANCE.md",
            "../../docs/MD_ENGINE_GOVERNANCE.md",
            "../../../docs/MD_ENGINE_GOVERNANCE.md",
            @"C:\Users\Tony\.gemini\antigravity\scratch\marksmith\docs\MD_ENGINE_GOVERNANCE.md"
        };

        foreach (var p in candidatePaths)
        {
            if (File.Exists(p)) return Path.GetFullPath(p);
        }
        return null;
    }

    private static string GetFallbackContract() =>
@"# Markdown Engine Governance & Syntax Contract

## Core Principles
1. Two pipelines, one contract: DOCX/OpenXML and HTML Preview paths must always agree.
2. Wrapper catalog: `:::smartart`, `:::workflow`, `:::tabs`, `:::chart`, `:::columns`, `:::timeline`, `:::canvas`, `:::shapes`, `:::datagrid`.
3. Mathematics: `$..$` inline, `$$..$$` display blocks.
4. Admonitions: `> [!NOTE]`, `> [!TIP]`, `> [!WARNING]`, `> [!IMPORTANT]`, `> [!CAUTION]`.
5. OpenXML governance: No hardcoded `rId`, enforce root namespaces, streaming SAX writing.";
}
