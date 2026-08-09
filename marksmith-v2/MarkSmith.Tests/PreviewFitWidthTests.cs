using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Fit-to-width live preview: when the left drawer auto-closes (or the window widens), the preview
// page zooms to FILL the reclaimed space instead of leaving empty margins. The zoom is a
// transform (page fidelity preserved — the DOCX/PDF output is a re-layout, never the zoom) and
// exists ONLY in the live preview, never in export renders.
public class PreviewFitWidthTests
{
    private static readonly ThemeDefinition LightTheme = new(
        "Light", "#ffffff", "#1a1a1a", "#111111", "#f4f4f4", "#d9d9d9", "#0078d4", "#e8f4fd", "#bfbfbf");

    [Fact]
    public void LivePreview_IncludesFitToWidthScript()
    {
        var html = new MarkdownHtmlService().Render(
            "# Hi\n\nSome text.", new AppSettings(), LightTheme, interactive: true);

        Assert.Contains("marksmith-fit-width", html);
        Assert.Contains("canvas.style.transform = 'scale('", html);
        Assert.Contains("MutationObserver", html); // re-fits when mermaid/images land late
    }

    [Fact]
    public void ExportRender_HasNoFitScript_AndPrintNeverZooms()
    {
        var html = new MarkdownHtmlService().Render(
            "# Hi\n\nSome text.", new AppSettings(), LightTheme);

        Assert.DoesNotContain("marksmith-fit-width", html);
        Assert.Contains("transform: none !important", html); // print/PDF output is never scaled
    }
}
