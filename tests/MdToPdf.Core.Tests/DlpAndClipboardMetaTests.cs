using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// Coverage for two pure, previously-untested services flagged by static review:
//  - DlpScanService.Scan: the regex/classification/masking core of the DLP scanner.
//  - ClipboardSourceMeta.Extract: clipboard-marker parsing (JSON + regex + URL-decoding).
// Both are string-in / object-out with no dependencies, so they unit-test directly.
public class DlpScanServiceTests
{
    private static DlpScanService.DlpResult Scan(string text) => new DlpScanService().Scan(text);

    // ---- clean text: zero capture --------------------------------------------------------------
    [Fact]
    public void Clean_text_captures_nothing()
    {
        var r = Scan("just a normal sentence about the quarterly roadmap");
        Assert.Equal(0, r.HitCount);
        Assert.Empty(r.Flags);
        Assert.Empty(r.Matches);
        Assert.Equal("", r.RedactedContext);
        Assert.Equal(0, r.SecretDensity);
    }

    // ---- AWS access key is an identifier -> revealed in full (mask + context) ------------------
    [Fact]
    public void Aws_access_key_is_revealed_in_full()
    {
        var r = Scan("deploy key AKIAIOSFODNN7EXAMPLE is live");
        Assert.Contains("AWS access key", r.Flags);
        var m = Assert.Single(r.Matches, m => m.Category == "AWS access key");
        Assert.Equal("AKIAIOSFODNN7EXAMPLE", m.Masked);          // revealed, not masked
        Assert.Contains("AKIAIOSFODNN7EXAMPLE", r.RedactedContext); // and kept in context too
    }

    // ---- email: local part masked, domain kept -------------------------------------------------
    [Fact]
    public void Email_is_local_masked_domain_kept()
    {
        var r = Scan("ping alice.smith@example.com please");
        var m = Assert.Single(r.Matches, m => m.Category == "Email address");
        Assert.Contains("@example.com", m.Masked);
        Assert.DoesNotContain("alice", m.Masked);
        Assert.DoesNotContain("alice.smith@example.com", r.RedactedContext);
    }

    // ---- credit card: PCI last-4 only ----------------------------------------------------------
    [Fact]
    public void Credit_card_keeps_last4_only()
    {
        var r = Scan("card 4111 1111 1111 1111 on file");
        Assert.Contains("Credit-card-like number", r.Flags);
        var m = Assert.Single(r.Matches, m => m.Category == "Credit-card-like number");
        Assert.Contains("1111", m.Masked);
        Assert.DoesNotContain("4111", m.Masked);
    }

    // ---- credential: fully redacted, raw value never reaches stored context --------------------
    [Fact]
    public void Credential_is_fully_redacted_and_absent_from_context()
    {
        var r = Scan("login password=hunter2 now");
        Assert.Contains("Credential", r.Flags);
        var m = Assert.Single(r.Matches, m => m.Category == "Credential");
        Assert.Contains("redacted", m.Masked);
        Assert.DoesNotContain("hunter2", m.Masked);
        Assert.DoesNotContain("hunter2", r.RedactedContext);
    }

    // ---- secret density: whole-message vs buried ----------------------------------------------
    [Fact]
    public void Density_is_high_when_secret_is_entire_message()
    {
        var r = Scan("AKIAIOSFODNN7EXAMPLE");
        Assert.True(r.SecretDensity >= 0.99, $"expected ~1.0, got {r.SecretDensity}");
    }

    [Fact]
    public void Density_is_low_when_secret_buried_in_text()
    {
        var buried = "The deployment config follows. AKIAIOSFODNN7EXAMPLE " + new string('x', 200);
        var r = Scan(buried);
        Assert.True(r.SecretDensity < 0.5, $"expected low density, got {r.SecretDensity}");
    }

    // ---- multiple categories are all flagged ---------------------------------------------------
    [Fact]
    public void Multiple_categories_all_flagged()
    {
        var r = Scan("key AKIAIOSFODNN7EXAMPLE and mail bob@corp.io together");
        Assert.Contains("AWS access key", r.Flags);
        Assert.Contains("Email address", r.Flags);
        Assert.Equal(2, r.HitCount);
    }

    // ---- stored masked previews are capped per category, hits still count all ------------------
    [Fact]
    public void Stored_previews_capped_at_five_per_category_but_hits_count_all()
    {
        var seven = string.Join(" ", Enumerable.Range(1, 7).Select(i => $"user{i}@x.io"));
        var r = Scan(seven);
        Assert.Equal(7, r.HitCount);
        Assert.Equal(5, r.Matches.Count(m => m.Category == "Email address"));
    }

    // ---- ContainsUnmaskedSecret: the server-side safety net ------------------------------------
    [Fact]
    public void ContainsUnmaskedSecret_detects_credential_but_not_revealed_aws_key()
    {
        Assert.True(DlpScanService.ContainsUnmaskedSecret("set password=hunter2 here"));
        Assert.False(DlpScanService.ContainsUnmaskedSecret("key AKIAIOSFODNN7EXAMPLE")); // Reveal-style
        Assert.False(DlpScanService.ContainsUnmaskedSecret("perfectly clean text"));
    }

    // ---- RedactResidual blanks secrets in arbitrary context ------------------------------------
    [Fact]
    public void RedactResidual_blanks_secrets_in_arbitrary_context()
    {
        var result = DlpScanService.RedactResidual("contact alice@example.com asap");
        Assert.DoesNotContain("alice@example.com", result);
        Assert.Contains("[Email address]", result);
    }
}

public class ClipboardSourceMetaTests
{
    private static string MetaMarker(string json) => "<!--marksmith-meta:" + Uri.EscapeDataString(json) + "-->";

    // ---- nothing to parse ----------------------------------------------------------------------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_html_returns_null(string? html) => Assert.Null(ClipboardSourceMeta.Extract(html));

    [Fact]
    public void Html_without_marker_returns_null() => Assert.Null(ClipboardSourceMeta.Extract("<p>plain rich text</p>"));

    // ---- full metadata marker populates every Source* field ------------------------------------
    [Fact]
    public void Full_meta_marker_populates_all_source_fields()
    {
        var json = "{\"font\":\"Segoe UI\",\"source\":\"chatgpt\",\"model\":\"gpt-4o\",\"title\":\"Quarterly Report\",\"lang\":\"en\",\"dir\":\"ltr\",\"accent\":\"#1B9AAA\"}";
        var o = ClipboardSourceMeta.Extract(MetaMarker(json));
        Assert.NotNull(o);
        Assert.Equal("Segoe UI", o!.SourceFontFamily);
        Assert.Equal("chatgpt", o.SourceId);
        Assert.Equal("gpt-4o", o.SourceModel);
        Assert.Equal("Quarterly Report", o.SourceTitle);
        Assert.Equal("en", o.SourceLanguage);
        Assert.Equal("ltr", o.SourceDirection);
        Assert.Equal("#1B9AAA", o.SourceAccentColor);
    }

    // ---- legacy font-only marker ---------------------------------------------------------------
    [Fact]
    public void Legacy_font_marker_populates_font_only()
    {
        var o = ClipboardSourceMeta.Extract("<!--marksmith-font:Segoe%20UI-->");
        Assert.NotNull(o);
        Assert.Equal("Segoe UI", o!.SourceFontFamily);
        Assert.Null(o.SourceId);
    }

    // ---- malformed meta JSON falls back to the legacy font marker ------------------------------
    [Fact]
    public void Malformed_meta_json_falls_back_to_font_marker()
    {
        var html = "<!--marksmith-meta:oops-not-json--><!--marksmith-font:Arial-->";
        var o = ClipboardSourceMeta.Extract(html);
        Assert.NotNull(o);
        Assert.Equal("Arial", o!.SourceFontFamily);
    }

    [Fact]
    public void Malformed_meta_without_font_returns_null() =>
        Assert.Null(ClipboardSourceMeta.Extract("<!--marksmith-meta:oops-not-json-->"));

    // ---- meta object with no usable fields is treated as absent --------------------------------
    [Fact]
    public void Empty_meta_object_returns_null() => Assert.Null(ClipboardSourceMeta.Extract(MetaMarker("{}")));

    // ---- marker embedded in real clipboard HTML is still found ---------------------------------
    [Fact]
    public void Marker_embedded_in_rich_html_is_extracted()
    {
        var html = "<html><body>" + MetaMarker("{\"source\":\"gemini\"}") + "<p>content</p></body></html>";
        var o = ClipboardSourceMeta.Extract(html);
        Assert.NotNull(o);
        Assert.Equal("gemini", o!.SourceId);
    }
}
