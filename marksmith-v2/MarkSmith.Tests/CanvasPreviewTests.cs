using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// <c>:::canvas</c> drew in Word only: the preview, PDF and HTML export showed its raw source.
/// It now draws everywhere, through the SVG sanitiser.
/// </summary>
public class CanvasPreviewTests
{
    private static string Html(string md) => E2ETestHelpers.RenderHtml(md);

    [Fact]
    public void An_svg_canvas_is_drawn_not_printed()
    {
        var html = Html("Before\n\n:::canvas\n<svg viewBox=\"0 0 40 40\"><circle cx=\"20\" cy=\"20\" r=\"10\"/></svg>\n:::\n\nAfter");
        Assert.Contains("class=\"ms-canvas\"", html);
        Assert.Contains("<circle", html);
        Assert.DoesNotContain(":::canvas", html);
        Assert.True(html.IndexOf("<p>Before") < html.IndexOf("class=\"ms-canvas\"") && html.IndexOf("class=\"ms-canvas\"") < html.IndexOf("<p>After"));
    }

    [Fact]
    public void Bare_path_data_is_drawn_in_a_100_box_like_Word()
    {
        var html = Html(":::canvas\nM 10 10 L 90 90 L 10 90 Z\n:::");
        Assert.Contains("viewBox=\"0 0 100 100\"", html);
        Assert.Contains("d=\"M 10 10 L 90 90 L 10 90 Z\"", html);
    }

    [Fact]
    public void Scripts_and_handlers_in_a_canvas_are_removed()
    {
        var html = Html(":::canvas\n<svg onload=\"alert(1)\"><script>alert(2)</script><path d=\"M0 0 L 5 5\"/></svg>\n:::");
        Assert.Contains("class=\"ms-canvas\"", html);
        Assert.DoesNotContain("alert(", html);
    }

    [Fact]
    public void A_canvas_with_nothing_to_draw_stays_text_and_one_in_a_code_fence_is_left_alone()
    {
        Assert.Contains("hello there", Html(":::canvas\nhello there\n:::"));
        var fenced = Html("```\n:::canvas\nM 0 0 L 1 1\n:::\n```");
        Assert.DoesNotContain("class=\"ms-canvas\"", fenced);
    }
}
