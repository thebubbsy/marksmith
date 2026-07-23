using System.IO;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class Milestone3LiquidFillTests
{
    [Fact]
    public void WebAssets_ExposesM3AssetUrls()
    {
        Assert.Equal("https://marksmith.assets/liquid_fill.css", WebAssets.LiquidFillCss);
        Assert.Equal("https://marksmith.assets/mermaid_interop.js", WebAssets.MermaidInteropJs);
    }

    [Fact]
    public void MarkdownHtmlService_Render_IncludesLiquidFillCssAndInteropJs_WhenMermaidPresent()
    {
        var service = new MarkdownHtmlService();
        var settings = new AppSettings { MermaidEnabled = true };
        var theme = new ThemeDefinition("Default", "#ffffff", "#000000", "#111111", "#f0f0f0", "#cccccc", "#0066cc", "#e0e0e0", "#888888");
        string markdown = "```mermaid\nflowchart TD\n  A --> B\n```";

        string html = service.Render(markdown, settings, theme, interactive: true);

        Assert.Contains("liquid_fill.css", html);
        Assert.Contains("mermaid_interop.js", html);
    }

    [Fact]
    public void PhysicalAssetFiles_ExistAndContainRequiredM3Logic()
    {
        // Locate repo root based on AppContext
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var cssPath = Path.Combine(repoRoot, "MdToPdf", "Assets", "web", "liquid_fill.css");
        var jsPath = Path.Combine(repoRoot, "MdToPdf", "Assets", "web", "mermaid_interop.js");

        Assert.True(File.Exists(cssPath), $"liquid_fill.css should exist at {cssPath}");
        Assert.True(File.Exists(jsPath), $"mermaid_interop.js should exist at {jsPath}");

        var cssContent = File.ReadAllText(cssPath);
        Assert.Contains("mermaid-liquid-overlay", cssContent);
        Assert.Contains("sloshWaveFront", cssContent);
        Assert.Contains("sloshWaveBack", cssContent);
        Assert.Contains("liquidSplashFlash", cssContent);

        var jsContent = File.ReadAllText(jsPath);
        Assert.Contains("AudioContext", jsContent);
        Assert.Contains("launch-mermaid-studio", jsContent);
        Assert.Contains("HOLD_DURATION = 800", jsContent);
        Assert.Contains("MOVE_THRESHOLD = 8", jsContent);
        Assert.Contains("requestAnimationFrame", jsContent);
        Assert.Contains("pointerdown", jsContent);
    }
}
