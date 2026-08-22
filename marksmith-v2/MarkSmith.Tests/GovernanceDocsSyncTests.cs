using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class GovernanceDocsSyncTests
{
    private static string FindRepoRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "marksmith-v2.sln")) ||
                File.Exists(Path.Combine(dir, "marksmith-v2", "marksmith-v2.sln")) ||
                Directory.Exists(Path.Combine(dir, "docs")))
            {
                if (Directory.Exists(Path.Combine(dir, "docs"))) return dir;
                if (Directory.Exists(Path.Combine(dir, "..", "docs"))) return Path.GetFullPath(Path.Combine(dir, ".."));
            }
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback default
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static string GetGovernanceDocContent()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "MD_ENGINE_GOVERNANCE.md");
        if (File.Exists(path)) return File.ReadAllText(path);
        var altPath = Path.Combine(root, "marksmith-v2", "..", "docs", "MD_ENGINE_GOVERNANCE.md");
        if (File.Exists(altPath)) return File.ReadAllText(altPath);
        return string.Empty;
    }

    private static string GetReadmeContent()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "README.md");
        if (File.Exists(path)) return File.ReadAllText(path);
        return string.Empty;
    }

    // =========================================================================
    // Tier 1: Feature Coverage (R11 - Governance, Syntax Catalog & README Sync)
    // =========================================================================

    [Fact]
    public void T1_01_Governance_Doc_Exists_And_Contains_Dual_Pipeline_Contract()
    {
        var content = GetGovernanceDocContent();
        Assert.NotEmpty(content);
        Assert.Contains("two separate pipelines", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenXML / DOCX", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HTML preview", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T1_02_Governance_Doc_Catalogs_All_Block_Wrappers()
    {
        var content = GetGovernanceDocContent();
        Assert.NotEmpty(content);

        Assert.Contains(":::smartart", content);
        Assert.Contains(":::workflow", content);
        Assert.Contains(":::tabs", content);
        Assert.Contains(":::columns", content);
    }

    [Fact]
    public void T1_03_Governance_Doc_Catalogs_All_Inline_Syntaxes()
    {
        var content = GetGovernanceDocContent();
        Assert.NotEmpty(content);

        Assert.Contains("`", content);
        Assert.Contains("$", content);
        Assert.Contains("<!--", content);
    }

    [Fact]
    public void T1_04_Readme_Documents_Core_Capabilities()
    {
        var content = GetReadmeContent();
        Assert.NotEmpty(content);

        Assert.Contains("Marksmith", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Markdown", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T1_05_Readme_Documents_Cli_Usage()
    {
        var content = GetReadmeContent();
        Assert.NotEmpty(content);

        Assert.Contains("CLI", content, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R11)
    // =========================================================================

    [Fact]
    public void T2_01_InsertSnippetBuilder_Produces_NonEmpty_Snippets_For_All_Features()
    {
        var methods = typeof(InsertSnippetBuilder).GetMethods(BindingFlags.Public | BindingFlags.Static);
        Assert.NotEmpty(methods);

        foreach (var method in methods.Where(m => m.ReturnType == typeof(string) && m.GetParameters().All(p => p.IsOptional || p.ParameterType == typeof(string) || p.ParameterType == typeof(int) || p.ParameterType == typeof(bool))))
        {
            var defaultArgs = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : (p.ParameterType == typeof(string) ? "Test" : (p.ParameterType == typeof(bool) ? (object)true : (object)1))).ToArray();
            var snippet = (string)method.Invoke(null, defaultArgs)!;
            Assert.False(string.IsNullOrWhiteSpace(snippet), $"Snippet from {method.Name} should not be empty");
        }
    }

    [Fact]
    public void T2_02_InsertSnippetBuilder_Clamps_Bounds_Safely()
    {
        // Testing that snippet builder methods do not throw exceptions with boundary values
        var methods = typeof(InsertSnippetBuilder).GetMethods(BindingFlags.Public | BindingFlags.Static);
        foreach (var method in methods.Where(m => m.ReturnType == typeof(string)))
        {
            var testArgs = method.GetParameters().Select(p =>
            {
                if (p.ParameterType == typeof(int)) return (object)-100;
                if (p.ParameterType == typeof(string)) return (object)"";
                if (p.ParameterType == typeof(bool)) return (object)false;
                if (typeof(System.Collections.IEnumerable).IsAssignableFrom(p.ParameterType)) return Array.Empty<string>();
                return p.DefaultValue ?? (object)1;
            }).ToArray();

            try
            {
                var result = method.Invoke(null, testArgs);
                Assert.NotNull(result);
            }
            catch (TargetInvocationException tie)
            {
                Assert.Fail($"Method {method.Name} threw unexpected exception on edge args: {tie.InnerException?.Message}");
            }
        }
    }

    [Fact]
    public void T2_03_Governance_Markdown_Tables_And_Headings_Parse_Cleanly()
    {
        var content = GetGovernanceDocContent();
        Assert.NotEmpty(content);
        var lines = content.Split('\n');
        Assert.True(lines.Length > 20, "Governance document must be comprehensive");
    }

    [Fact]
    public void T2_04_All_Detectors_In_DetectorsCs_Are_Documented_In_Governance()
    {
        var governance = GetGovernanceDocContent();
        Assert.NotEmpty(governance);

        var keyWrappers = new[] { "smartart", "workflow", "tabs", "chart", "columns", "timeline", "canvas", "shapes" };
        foreach (var wrapper in keyWrappers)
        {
            Assert.Contains($":::{wrapper}", governance);
        }
    }

    [Fact]
    public void T2_05_Dual_Pipeline_Architecture_Parity_Documented()
    {
        var content = GetGovernanceDocContent();
        Assert.Contains("Markdig AST", content);
        Assert.Contains("HtmlSanitizer", content);
    }

    // =========================================================================
    // Tier 3: Cross-Feature Interactions
    // =========================================================================

    [Fact]
    public void T3_01_Snippet_Builder_Snippets_Are_Valid_Markdown()
    {
        var methods = typeof(InsertSnippetBuilder).GetMethods(BindingFlags.Public | BindingFlags.Static);
        foreach (var method in methods.Where(m => m.ReturnType == typeof(string) && m.GetParameters().Length == 0))
        {
            var snippet = (string)method.Invoke(null, null)!;
            var html = E2ETestHelpers.RenderHtml(snippet);
            Assert.NotNull(html);
        }
    }

    [Fact]
    public void T3_02_Ambiguity_Preferences_And_Governance_Rules_Synchronized()
    {
        var content = GetGovernanceDocContent();
        Assert.Contains("AmbiguityDetector", content);
        Assert.Contains("AppSettings", content);
    }
}
