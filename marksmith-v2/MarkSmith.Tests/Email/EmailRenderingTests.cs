using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Email;
using MimeKit;
using Xunit;

namespace MarkSmith.Tests.Email;

/// <summary>
/// Run #22: email support, Phase 1 (Core). The renderer writes what classic Outlook's Word engine
/// can show — tables, inline styles, CID PNGs — and nothing it can't.
/// </summary>
public class EmailRenderingTests
{
    private const string KitchenSink = """
        # Q3 rollout plan

        Hi team, the **migration** is *on track*. See the [runbook](https://example.com/runbook).

        | Workstream | Owner | Progress |
        |:-----------|:-----:|---------:|
        | Schema | Priya | 80% |

        - [x] Freeze schema
        - [ ] Load test

        1. Enable the shim
        2. Watch errors
           - p95 under 120 ms

        > [!WARNING]
        > Doubles the write load.

        ```csharp
        var x = 1; // note
        ```

        ```mermaid
        flowchart LR
          A --> B
        ```

        Cost is $E = mc^2$ and $$\frac{a}{b}$$

        :::smartart type="process"
        - Plan
        - Ship
        :::

        Thanks[^1]. Line<br>break & "quotes" <script>alert(1)</script>

        [^1]: The on-call team.
        """;

    private static readonly ThemeDefinition Light = new("Light", "#ffffff", "#222222", "#0b3d91", "#f5f5f5", "#dddddd", "#0b5cad", "#eeeeee", "#333333");
    private static readonly ThemeDefinition Dark = new("Dark", "#1e1e2e", "#f8f8f2", "#bd93f9", "#282a36", "#44475a", "#ff79c6", "#44475a", "#6272a4");

    private static EmailRenderResult Render(string md, ThemeDefinition? theme = null, EmailRenderOptions? options = null, AppSettings? settings = null) =>
        new EmailHtmlRenderer(settings ?? new AppSettings(), theme ?? Light, options).Render(md);

    [Fact]
    public void Body_uses_nothing_outlook_cannot_show()
    {
        var html = Render(KitchenSink).Html;
        foreach (var banned in new[] { "<script", "<svg", "<input", "data:", "display:flex", "display:grid", "<style", "class=\"" })
            Assert.DoesNotContain(banned, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Body_is_well_formed_xhtml()
    {
        var html = Render(KitchenSink).Html;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(new StringReader(html), settings);
        while (reader.Read()) { }
    }

    [Fact]
    public void Every_picture_is_a_cid_part_with_size_and_alt()
    {
        var png = SvgRasterizer.ToPng("<svg xmlns='http://www.w3.org/2000/svg' width='300' height='100'><rect width='300' height='100' fill='red'/></svg>")!;
        var result = Render(KitchenSink, options: new EmailRenderOptions { MermaidPngs = new byte[]?[] { png } });
        var imgs = Regex.Matches(result.Html, "<img [^>]*>").Select(m => m.Value).ToList();
        Assert.Equal(2, imgs.Count); // mermaid + smartart
        Assert.All(imgs, i =>
        {
            Assert.Matches("src=\"cid:[^\"]+\"", i);
            Assert.Matches("width=\"\\d+\"", i);
            Assert.Matches("alt=\"[^\"]+\"", i);
        });
        foreach (Match m in Regex.Matches(result.Html, "cid:([^\"]+)"))
            Assert.Contains(result.Images, x => x.ContentId == m.Groups[1].Value);
        // The 2x render shows at its natural CSS size.
        var mermaid = result.Images.First(i => i.FileName.StartsWith("diagram"));
        Assert.Equal(300, mermaid.Width);
    }

    [Fact]
    public void Missing_diagram_render_falls_back_to_its_source_and_says_so()
    {
        var result = Render(KitchenSink);
        Assert.Contains("flowchart LR", result.Html);
        Assert.Contains(result.Notes, n => n.Contains("diagram", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Body_sits_in_one_fixed_width_table_for_outlook()
    {
        var html = Render(KitchenSink).Html;
        Assert.Contains($"<table role=\"presentation\" width=\"{EmailHtmlRenderer.ContentWidth}\"", html);
        Assert.InRange(EmailHtmlRenderer.ContentWidth, 600, 680);
    }

    [Fact]
    public void Table_alignment_survives_as_attribute_and_style()
    {
        var html = Render(KitchenSink).Html;
        Assert.Contains("align=\"center\"", html);
        Assert.Contains("align=\"right\"", html);
        Assert.Contains("text-align:right", html);
    }

    [Fact]
    public void Task_boxes_are_characters_not_form_controls()
    {
        var result = Render(KitchenSink);
        Assert.Contains("&#9745;", result.Html);
        Assert.Contains("&#9744;", result.Html);
        Assert.Contains("[x] Freeze schema", result.Text);
        Assert.Contains("[ ] Load test", result.Text);
    }

    [Fact]
    public void Title_becomes_the_subject_and_leaves_the_body_unless_asked()
    {
        var result = Render(KitchenSink);
        Assert.Equal("Q3 rollout plan", result.Title);
        Assert.DoesNotContain("<h1", result.Html);
        var kept = Render(KitchenSink, options: new EmailRenderOptions { RepeatTitleInBody = true });
        Assert.Contains("<h1", kept.Html);
    }

    [Fact]
    public void Dark_theme_still_produces_a_light_readable_email()
    {
        var html = Render(KitchenSink, Dark).Html;
        Assert.Contains("background-color:#ffffff", html);
        Assert.DoesNotContain("#1e1e2e", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("color:#f8f8f2", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Code_is_highlighted_with_inline_colours()
    {
        var html = Render(KitchenSink).Html;
        Assert.Matches("<span style=\"color:#[0-9a-fA-F]{6};font-weight:bold;\">var</span>", html);
        Assert.Contains("<pre style=", html);
    }

    [Fact]
    public void Maths_reads_as_unicode_when_it_cannot_be_typeset()
    {
        var result = Render(KitchenSink);
        Assert.Contains("E = mc²", result.Html);
        Assert.Contains("a/b", result.Html);
        Assert.Equal("α ≤ √(x+1)", LatexText.ToReadable(@"\alpha \le \sqrt{x+1}"));
        Assert.Equal("x₁²", LatexText.ToReadable("x_1^2"));
    }

    [Fact]
    public void Plain_text_part_reads_like_an_email_not_markdown()
    {
        var text = Render(KitchenSink).Text;
        Assert.DoesNotContain("**", text);
        Assert.Contains("runbook (https://example.com/runbook)", text);
        Assert.Contains("Workstream", text);
        Assert.Contains("[1] The on-call team.", text);
    }

    [Fact]
    public void Script_and_javascript_links_never_reach_the_email()
    {
        var html = Render("[x](javascript:alert(1)) <script>bad()</script>\n\n<div onclick=\"x()\">ok</div>").Html;
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("bad()", html);
    }

    [Fact]
    public void Rule_before_footnotes_is_drawn_once()
    {
        var result = Render("Thanks[^1].\n\n---\n\n[^1]: The on-call team.");
        Assert.Single(Regex.Matches(result.Html, "<hr "));
        Assert.Single(Regex.Matches(result.Text, "----------"));
    }

    [Fact]
    public void Pipe_table_alignment_uses_the_column_position()
    {
        var html = Render("| a | b | c |\n|:--|:-:|--:|\n| 1 | 2 | 3 |").Html;
        Assert.Matches("<td align=\"left\"[^>]*>1</td><td align=\"center\"[^>]*>2</td><td align=\"right\"[^>]*>3</td>", html);
    }

    [Fact]
    public void Local_images_are_embedded_and_missing_ones_are_reported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-email-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "logo.svg"), "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='20'><rect width='40' height='20'/></svg>");
            var result = Render("![Logo](logo.svg) ![Gone](nope.png) ![Remote](https://example.com/a.png)",
                options: new EmailRenderOptions { BaseDirectory = dir });
            var img = Assert.Single(result.Images);
            Assert.Equal("image/png", img.MimeType);
            Assert.Equal(40, img.Width);
            Assert.Contains("src=\"https://example.com/a.png\"", result.Html);
            Assert.Contains(result.Notes, n => n.Contains("Gone"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── composer ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{title}", "Plan", "notes.md", "Plan")]
    [InlineData("{source}: {title}", "Plan", "notes", "notes: Plan")]
    [InlineData("{source}: {title}", "Plan", null, "Plan")]
    [InlineData("{title}", null, "notes", "notes")]
    [InlineData("", "Plan", null, "Plan")]
    public void Subject_template_fills_and_never_dangles(string template, string? title, string? source, string expected)
    {
        Assert.Equal(expected, EmailComposer.BuildSubject(template, title, source, "", new DateTime(2026, 10, 8)));
    }

    [Fact]
    public void Untitled_document_uses_its_opening_words_as_subject()
    {
        var subject = EmailComposer.BuildSubject("{title}", null, null,
            "Quick update on the vendor contract renewal that legal flagged last week, plus next steps for procurement.", DateTime.Now);
        Assert.StartsWith("Quick update on the vendor contract", subject);
        Assert.EndsWith("…", subject);
        Assert.True(subject.Length <= 71);
    }

    [Fact]
    public void Recipients_are_validated_and_rejects_reported()
    {
        var (valid, invalid) = EmailComposer.ParseAddresses("\"Lee, Ann\" <ann@example.com>; bob; carl@example.org\n, dana@");
        Assert.Equal(2, valid.Count);
        Assert.Contains(valid, v => v.Contains("ann@example.com"));
        Assert.Equal(new[] { "bob", "dana@" }, invalid);
    }

    [Fact]
    public void Composer_uses_settings_recipients_and_notes_bad_ones()
    {
        var settings = new AppSettings { EmailTo = "ann@example.com; nobody", EmailCc = "cc@example.com" };
        var doc = EmailComposer.Compose(new EmailComposeRequest { Markdown = "# Hello\n\nBody" }, settings, Light);
        Assert.Equal("Hello", doc.Subject);
        Assert.Equal(new[] { "ann@example.com" }, doc.To);
        Assert.Single(doc.Cc);
        Assert.Contains(doc.Notes, n => n.Contains("nobody"));
        Assert.True(doc.IsDraft);
    }

    // ── .eml writer ───────────────────────────────────────────────────────────

    [Fact]
    public void Eml_round_trips_through_mimekit()
    {
        var png = SvgRasterizer.ToPng("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>")!;
        var settings = new AppSettings { EmailTo = "Ann <ann@example.com>" };
        var doc = EmailComposer.Compose(new EmailComposeRequest
        {
            Markdown = KitchenSink.Replace("Q3 rollout plan", "Q3 plan ✅ für Ärzte"),
            MermaidPngs = new byte[]?[] { png },
            Attachments = new[] { new EmailAttachment("plan.pdf", new byte[] { 1, 2, 3 }, "application/pdf") },
        }, settings, Light);

        var msg = MimeMessage.Load(new MemoryStream(EmlWriter.ToBytes(doc)));
        Assert.Equal("Q3 plan ✅ für Ärzte", msg.Subject);
        Assert.Equal("1", msg.Headers["X-Unsent"]);
        Assert.Equal("ann@example.com", msg.To.Mailboxes.Single().Address);
        Assert.EndsWith("@marksmith.local", msg.MessageId);
        Assert.Contains("Workstream", msg.TextBody);
        Assert.Contains("Workstream", msg.HtmlBody);

        var linked = msg.BodyParts.OfType<MimePart>().Where(p => p.ContentId is not null).ToList();
        foreach (Match m in Regex.Matches(msg.HtmlBody, "cid:([^\"]+)"))
            Assert.Contains(linked, p => p.ContentId == m.Groups[1].Value);
        var att = Assert.Single(msg.Attachments.OfType<MimePart>());
        Assert.Equal("plan.pdf", att.FileName);
        Assert.Equal("application/pdf", att.ContentType.MimeType);
    }

    [Fact]
    public void Sent_style_eml_has_no_draft_flag()
    {
        var doc = EmailComposer.Compose(new EmailComposeRequest { Markdown = "Hi", IsDraft = false }, new AppSettings(), Light);
        var msg = MimeMessage.Load(new MemoryStream(EmlWriter.ToBytes(doc)));
        Assert.Null(msg.Headers["X-Unsent"]);
    }

    // ── licensing: email is free for everyone ─────────────────────────────────

    [Fact]
    public void Email_is_a_free_feature_on_every_plan()
    {
        Assert.True(FeatureClassifier.IsFree(FeatureId.EmailDraft));
        Assert.True(FeatureClassifier.LicenseAllows(FeatureId.EmailDraft, new LicenseState()));
    }
    [Fact]
    public void A_tag_mentioned_in_prose_doesnt_empty_the_rest_of_the_email()
    {
        var r = Render("Use a <select> element for the picker.\n\nSecond paragraph.\n\n## Later\n\nThird.");
        Assert.Contains("Second paragraph.", r.Html);
        Assert.Contains("Third.", r.Html);
        Assert.Contains("&lt;select&gt;", r.Html);
    }

    [Fact]
    public void A_closed_style_is_still_hidden_in_both_the_html_and_the_text_part()
    {
        var r = Render("Before <style>p{color:red}</style> after.");
        Assert.DoesNotContain("color:red", r.Html);
        Assert.DoesNotContain("color:red", r.Text);
        Assert.Contains("after.", r.Text);
    }

    [Theory]
    [InlineData("```mermaid\ngraph TD\nA-->B\n```")]
    [InlineData("~~~mermaid\ngraph TD\nA-->B\n~~~")]
    [InlineData("``` mermaid\ngraph TD\nA-->B\n```")]
    public void Every_mermaid_fence_style_counts_as_a_diagram(string md)
    {
        Assert.True(EmailHtmlRenderer.HasMermaid(md));
        Assert.False(EmailHtmlRenderer.HasMermaid("```bash\n# not mermaid\n```"));
    }

    [Fact]
    public void The_subject_preview_uses_the_same_title_as_the_draft()
    {
        Assert.Null(EmailHtmlRenderer.TitleOf("## Weekly update\n\nBody", new AppSettings(), Light));
        Assert.Null(EmailHtmlRenderer.TitleOf("```bash\n# a comment\n```", new AppSettings(), Light));
        Assert.Equal("Plan", EmailHtmlRenderer.TitleOf("# Plan\n\nBody", new AppSettings(), Light));
    }
}
