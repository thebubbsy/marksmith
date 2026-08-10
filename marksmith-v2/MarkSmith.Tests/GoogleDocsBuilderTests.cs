using System.Text.Json;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// The Google Docs builder is the pure, network-free half of the native Google Docs export: it must
// map Markdown to correct Docs API batchUpdate requests (native headings, tables, images, lists,
// code shading, page setup) and keep every style range inside the inserted text.
public class GoogleDocsBuilderTests
{
    private static string Serialize(object request) =>
        JsonSerializer.Serialize(request, GoogleDocsDocumentBuilder.JsonOpts);

    private static GoogleDocsDocumentBuilder.GoogleDocsBuildResult Build(string md, AppSettings? settings = null) =>
        GoogleDocsDocumentBuilder.Build(md, settings ?? new AppSettings(), new ThemeDefinition("Test", "#ffffff", "#1a1a1a", "#1a1a1a", "#1a1a1a", "#1a1a1a", "#1a1a1a", "#1a1a1a", "#1a1a1a"));

    [Fact]
    public void Headings_Become_Native_Heading_Styles()
    {
        var r = Build("# Title\n\n## Sub\n\nBody text");
        var json = string.Join("\n", r.Requests.Select(Serialize));

        Assert.Contains("\"namedStyleType\":\"HEADING_1\"", json);
        Assert.Contains("\"namedStyleType\":\"HEADING_2\"", json);
        Assert.Contains("\"insertText\"", json);
        Assert.Contains("Title", json);
    }

    [Fact]
    public void Inline_Formatting_Emits_Styled_Runs_With_Correct_Ranges()
    {
        var r = Build("Hello **bold** and *italic* and `code` and [link](https://example.com).");
        var json = string.Join("\n", r.Requests.Select(Serialize));

        Assert.Contains("\"bold\":true", json);
        Assert.Contains("\"italic\":true", json);
        Assert.Contains("\"fontFamily\":\"Courier New\"", json);   // inline code
        Assert.Contains("\"link\":{\"url\":\"https://example.com\"}", json);

        // Ranges must stay inside the inserted text.
        var s = r.Requests.Select(Serialize).ToList();
        foreach (var line in s.Where(l => l.Contains("startIndex")))
        {
            using var d = JsonDocument.Parse(line);
            var el = d.RootElement.EnumerateObject().First().Value;
            var range = el.GetProperty("range");
            Assert.True(range.GetProperty("startIndex").GetInt32() >= 1);
            Assert.True(range.GetProperty("endIndex").GetInt32() <= r.FinalIndex);
            Assert.True(range.GetProperty("startIndex").GetInt32() < range.GetProperty("endIndex").GetInt32());
        }
    }

    [Fact]
    public void Lists_Use_Native_Bullets_Ordered_And_Unordered()
    {
        var r = Build("- one\n- two\n\n1. first\n2. second");
        var json = string.Join("\n", r.Requests.Select(Serialize));

        Assert.Contains("\"createParagraphBullets\"", json);
        Assert.Contains("BULLET_DISC_CIRCLE_SQUARE", json);
        Assert.Contains("NUMBERED_DECIMAL_ALPHA_ROMAN", json);
    }

    [Fact]
    public void Code_Block_Becomes_Monospace_Shaded_Paragraphs()
    {
        var r = Build("```csharp\nvar x = 1;\nvar y = 2;\n```");
        var json = string.Join("\n", r.Requests.Select(Serialize));

        Assert.Contains("\"fontFamily\":\"Courier New\"", json);
        Assert.Contains("\"shading\"", json);
        Assert.Contains("var x = 1;", json);
    }

    [Fact]
    public void Tables_And_Images_Are_Tokened_For_Real_Insertion()
    {
        var r = Build("# T\n\n![alt](https://example.com/img.png)\n\n| A | B |\n|---|---|\n| 1 | 2 |");
        var json = string.Join("\n", r.Requests.Select(Serialize));

        Assert.Single(r.Images);
        Assert.Equal("https://example.com/img.png", r.Images[0].Source);
        Assert.Equal("alt", r.Images[0].AltText);
        Assert.Contains("[[IMG_0]]", json);

        Assert.Single(r.Tables);
        Assert.Equal(new[] { "A", "B" }, r.Tables[0].Rows[0]);
        Assert.Equal(new[] { "1", "2" }, r.Tables[0].Rows[1]);
        Assert.Contains("[[TBL_0]]", json);
    }

    [Fact]
    public void Mermaid_Fence_Becomes_Image_Token_Paired_With_Harvested_Png()
    {
        // Interleaved with a URL image: the mermaid pairing index must stay 0 (it indexes the
        // mermaid-only harvested-PNG list) even though its global token order is 1.
        var r = Build("![pic](https://example.com/a.png)\n\n```mermaid\ngraph TD;\nA-->B;\n```");
        Assert.Equal(2, r.Images.Count);
        Assert.Equal("https://example.com/a.png", r.Images[0].Source);
        Assert.Equal("mermaid:0", r.Images[1].Source);
    }

    [Fact]
    public void Horizontal_Rule_And_Page_Setup_Are_Emitted()
    {
        var r = Build("# T\n\n---\n\nBody.");
        var json = string.Join("\n", r.Requests.Select(Serialize));

        Assert.Contains("\"insertHorizontalRule\"", json);
        Assert.Contains("\"updateDocumentStyle\"", json);
        Assert.Contains("\"pageSize\"", json);
    }

    [Fact]
    public void A4_Lock_Sets_Standard_Page_Width()
    {
        var r = Build("# T\n\nBody.", new AppSettings { A4FixedWidth = true, ContentWidth = 1200 });
        var json = string.Join("\n", r.Requests.Select(Serialize));
        Assert.Contains("\"width\":{\"magnitude\":595.3", json); // 8.27in in pt
    }

    [Fact]
    public void Running_Index_Matches_Inserted_Text_Length()
    {
        var md = "# Title\n\nA paragraph with **bold** and *italic*.\n\n- one\n- two\n\n```js\ncode line\n```\n\n| H1 | H2 |\n|---|---|\n| a | b |\n\n![x](https://example.com/i.png)\n\n---\n\n> quote";
        var r = Build(md);

        // Simulate applying every insertText (they append at the end): the tracked final index must
        // equal 1 + total inserted characters — the exact contract the service relies on.
        long inserted = 0;
        foreach (var req in r.Requests)
        {
            var json = Serialize(req);
            if (!json.Contains("\"insertText\"")) continue;
            using var d = JsonDocument.Parse(json);
            var text = d.RootElement.GetProperty("insertText").GetProperty("text").GetString() ?? "";
            inserted += text.Length;
        }

        Assert.Equal(1 + inserted, r.FinalIndex);
    }
}
