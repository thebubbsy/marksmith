using MdToPdf.Plugins;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// XSS / active-content stripping. HtmlSanitizer guards pasted HTML in the preview; SvgSanitizer
// guards plugin-produced SVG (which is injected raw, post-HtmlSanitizer). Several of these encode
// the exact bypasses a three-part audit found and that the rewrite closes.
public class HtmlSanitizerTests
{
    private static string S(string html) => HtmlSanitizer.Apply(html);

    // ---- the two audit bypasses ----------------------------------------------------------------
    [Fact] public void Handler_after_gt_in_attribute_value_is_stripped() =>
        Assert.DoesNotContain("onerror", S("<img src=\"x\" alt=\"a>b\" onerror=\"alert(1)\">"));
    [Fact] public void Js_url_with_inner_single_quote_in_double_quoted_href_is_stripped() =>
        Assert.DoesNotContain("javascript:", S("<a href=\"javascript:alert('x')\">c</a>"));
    [Fact] public void Js_url_with_inner_double_quote_in_single_quoted_href_is_stripped() =>
        Assert.DoesNotContain("javascript:", S("<a href='javascript:alert(\"x\")'>c</a>"));

    // ---- slash-delimiter bypasses (Sentinel PR #8): `/` is a valid HTML attribute delimiter ----
    // `\s` does not match `/`, so `<img/onerror=…>` / `<a/href="javascript:…">` used to evade the
    // stripper while browsers still parse `/` like a space and execute the payload.
    [Fact] public void Handler_with_slash_delimiter_is_stripped() =>
        Assert.DoesNotContain("onerror", S("<img/onerror=alert(1)>"));
    [Fact] public void Js_url_with_slash_delimiter_is_stripped() =>
        Assert.DoesNotContain("javascript:", S("<a/href=\"javascript:alert(1)\">x</a>"));
    [Fact] public void Js_url_with_slash_delimiter_on_src_is_stripped() =>
        Assert.DoesNotContain("javascript:", S("<img/src=\"javascript:alert(1)\">"));
    [Fact] public void Normal_link_with_slash_delimiter_survives() =>
        Assert.Contains("https://example.com", S("<a/href=\"https://example.com\">link</a>"));

    // ---- script / embed elements ---------------------------------------------------------------
    [Fact] public void Script_element_removed() => Assert.DoesNotContain("alert", S("<script>alert(1)</script>"));
    [Fact] public void Script_uppercase_removed() => Assert.DoesNotContain("alert", S("<SCRIPT>alert(1)</SCRIPT>"));
    [Fact] public void Script_with_attributes_removed() => Assert.DoesNotContain("alert", S("<script type=\"text/javascript\">alert(1)</script>"));
    [Fact] public void Self_closing_script_removed() => Assert.DoesNotContain("<script", S("<script src=\"evil.js\"/>"));
    [Fact] public void Iframe_removed() => Assert.DoesNotContain("<iframe", S("<iframe src=\"evil\"></iframe>"));
    [Fact] public void Object_removed() => Assert.DoesNotContain("<object", S("<object data=\"x\"></object>"));
    [Fact] public void Embed_removed() => Assert.DoesNotContain("<embed", S("<embed src=\"x\">"));

    // ---- event handlers, various quoting -------------------------------------------------------
    [Fact] public void Onclick_double_quoted_stripped() => Assert.DoesNotContain("onclick", S("<div onclick=\"x()\">hi</div>"));
    [Fact] public void Onload_single_quoted_stripped() => Assert.DoesNotContain("onload", S("<svg onload='x()'>"));
    [Fact] public void Onmouseover_bare_stripped() => Assert.DoesNotContain("onmouseover", S("<a onmouseover=x()>hi</a>"));
    [Fact] public void Onerror_on_img_stripped() => Assert.DoesNotContain("onerror", S("<img src=x onerror=alert(1)>"));
    [Fact] public void Multiple_handlers_all_stripped()
    {
        var o = S("<a onclick=\"a()\" onmouseover=\"b()\" href=\"#\">x</a>");
        Assert.DoesNotContain("onclick", o);
        Assert.DoesNotContain("onmouseover", o);
    }

    // ---- javascript: obfuscation ---------------------------------------------------------------
    [Fact] public void Js_url_plain_stripped() => Assert.DoesNotContain("javascript:", S("<a href=\"javascript:alert(1)\">x</a>"));
    [Fact] public void Js_url_with_leading_spaces_stripped() => Assert.DoesNotContain("javascript:", S("<a href=\"  javascript:alert(1)\">x</a>"));
    [Fact] public void Js_url_with_tab_between_scheme_stripped() => Assert.DoesNotContain("javascript:", S("<a href=\"java\tscript:alert(1)\">x</a>"));
    [Fact] public void Js_url_entity_encoded_stripped() => Assert.DoesNotContain("javascript", System.Net.WebUtility.HtmlDecode(S("<a href=\"java&#115;cript:alert(1)\">x</a>")));
    [Fact] public void Js_url_on_src_stripped() => Assert.DoesNotContain("javascript:", S("<img src=\"javascript:alert(1)\">"));
    [Fact] public void Js_url_on_xlink_href_stripped() => Assert.DoesNotContain("javascript:", S("<a xlink:href=\"javascript:alert(1)\">x</a>"));

    // ---- things that must SURVIVE (no over-stripping) ------------------------------------------
    [Fact] public void Normal_link_survives() => Assert.Contains("https://example.com", S("<a href=\"https://example.com\">link</a>"));
    [Fact] public void Prose_with_online_word_survives() => Assert.Contains("online", S("<p>we are online now</p>"));
    [Fact] public void Table_markup_survives() => Assert.Contains("<td", S("<table><tr><td>cell</td></tr></table>"));
    [Fact] public void Details_survives() => Assert.Contains("<details", S("<details><summary>s</summary>body</details>"));
    [Fact] public void Image_src_data_uri_survives() => Assert.Contains("data:image/png", S("<img src=\"data:image/png;base64,AAAA\">"));
    [Fact] public void Attribute_with_gt_and_no_handler_survives() => Assert.Contains("a>b", S("<img alt=\"a>b\" src=\"x.png\">"));
    [Fact] public void Empty_input_returns_empty() => Assert.Equal("", S(""));
    [Fact] public void Plain_text_unchanged() => Assert.Equal("just words", S("just words"));
    [Fact] public void Mailto_link_survives() => Assert.Contains("mailto:", S("<a href=\"mailto:a@b.com\">mail</a>"));
    [Fact] public void Relative_link_survives() => Assert.Contains("./page", S("<a href=\"./page\">rel</a>"));
}

public class SvgSanitizerTests
{
    private static string S(string svg) => SvgSanitizer.Sanitize(svg);

    [Fact] public void Script_element_removed() => Assert.DoesNotContain("<script", S("<svg><script>x()</script><rect/></svg>"));
    [Fact] public void Self_closing_script_removed() => Assert.DoesNotContain("<script", S("<svg><script href=\"evil.js\"/></svg>"));
    [Fact] public void Onload_on_svg_root_removed() => Assert.DoesNotContain("onload", S("<svg onload=\"x()\"><rect/></svg>"));
    [Fact] public void Onclick_on_element_removed() => Assert.DoesNotContain("onclick", S("<svg><rect onclick=\"x()\"/></svg>"));
    [Fact] public void ForeignObject_removed() => Assert.DoesNotContain("foreignObject", S("<svg><foreignObject><img onerror=\"x()\"></foreignObject></svg>"));
    [Fact] public void Javascript_xlink_href_removed() => Assert.DoesNotContain("javascript:", S("<svg><a xlink:href=\"javascript:x()\"><rect/></a></svg>"));
    // legitimate diagram content survives
    [Fact] public void Rect_survives() => Assert.Contains("<rect", S("<svg><rect x=\"1\"/></svg>"));
    [Fact] public void Path_survives() => Assert.Contains("<path", S("<svg><path d=\"M0 0\"/></svg>"));
    [Fact] public void Text_survives() => Assert.Contains("Alice", S("<svg><text>Alice</text></svg>"));
    [Fact] public void Https_link_survives() => Assert.Contains("https://plantuml.com", S("<svg><a xlink:href=\"https://plantuml.com\"><rect/></a></svg>"));
}
