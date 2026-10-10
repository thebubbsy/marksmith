using System.Text;
using MarkSmith.Models;

namespace MarkSmith.Services.Email;

/// <summary>"Preview as email": the message as a mail client will show it — a header with the
/// subject, recipients and attachments over the exact email HTML the export writes. The body sits
/// in an <c>iframe srcdoc</c> so the preview's own styles can't leak in (as in a real mail client),
/// pictures are inlined as data URIs (the export uses CID parts), and diagrams the export would
/// harvest as PNGs are drawn live with the bundled mermaid.js in the same email palette.</summary>
public static class EmailPreviewPage
{
    public static string Build(EmailDocument doc, EmailPalette palette, IReadOnlyList<string>? attachmentNames = null)
    {
        var body = InlineImages(doc);
        body = AddPreviewChrome(body, palette);

        var sb = new StringBuilder(body.Length * 2 + 4000);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\" />");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        sb.Append("<style>").Append(PageCss).Append("</style></head><body><div id=\"stage\">");
        sb.Append("<p class=\"cap\">Email preview: this is the message Outlook opens. Diagrams become pictures when you send it.</p>");
        sb.Append("<div class=\"msg\"><div class=\"hdr\">");
        sb.Append("<div class=\"subject\">")
          .Append(doc.Subject.Length > 0 ? E(doc.Subject) : "<span class=\"empty\">(no subject)</span>")
          .Append("</div>");
        Row(sb, "To", doc.To, "No recipients yet. Add defaults in Style &amp; Export ▸ Email, or type them in Outlook.");
        if (doc.Cc.Count > 0) Row(sb, "Cc", doc.Cc, "");
        var attachments = attachmentNames ?? doc.Attachments.Select(a => a.FileName).ToList();
        if (attachments.Count > 0)
        {
            sb.Append("<div class=\"atts\">");
            foreach (var name in attachments)
                sb.Append("<span class=\"chip\">&#128206; ").Append(E(name)).Append("</span>");
            sb.Append("</div>");
        }
        sb.Append("</div>");
        if (doc.Notes.Count > 0)
        {
            sb.Append("<div class=\"notes\">");
            foreach (var n in doc.Notes) sb.Append("<div>").Append(E(n)).Append("</div>");
            sb.Append("</div>");
        }
        sb.Append("<iframe id=\"mailbody\" title=\"Email body\" srcdoc=\"").Append(E(body)).Append("\"></iframe>");
        sb.Append("</div></div>");
        sb.Append("<script>").Append(FitScript).Append("</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, List<string> people, string emptyText)
    {
        sb.Append("<div class=\"row\"><span class=\"k\">").Append(label).Append("</span><span class=\"v\">");
        if (people.Count == 0) sb.Append("<span class=\"empty\">").Append(emptyText).Append("</span>");
        else sb.Append(E(string.Join("; ", people)));
        sb.Append("</span></div>");
    }

    private static string InlineImages(EmailDocument doc)
    {
        var html = doc.HtmlBody;
        foreach (var img in doc.InlineImages)
            html = html.Replace("cid:" + img.ContentId, $"data:{img.MimeType};base64,{Convert.ToBase64String(img.Bytes)}", StringComparison.Ordinal);
        return html;
    }

    // Inside the frame: a reading margin like a mail client's, and the live diagram drawing.
    private static string AddPreviewChrome(string html, EmailPalette palette)
    {
        const string margin = "<style>body{margin:0 !important;padding:18px 22px 22px !important;}</style>";
        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        html = head >= 0 ? html.Insert(head, margin) : margin + html;
        if (!html.Contains("data-ms-mermaid", StringComparison.Ordinal)) return html;

        var t = palette.DiagramTheme();
        var script = $$"""
            <script src="{{WebAssets.Mermaid}}"></script>
            <script>
            (async function () {
              var boxes = Array.prototype.slice.call(document.querySelectorAll('[data-ms-mermaid]'));
              function plain(box, src) {
                var pre = document.createElement('pre');
                pre.textContent = src;
                pre.setAttribute('style', 'margin:0;padding:10px 14px;background:{{palette.CodeBackground}};border:1px solid {{palette.Border}};font:13px Consolas,monospace;white-space:pre-wrap;color:{{palette.CodeText}};');
                box.innerHTML = ''; box.appendChild(pre);
              }
              if (typeof mermaid === 'undefined') { boxes.forEach(function (b) { plain(b, b.textContent); }); return; }
              mermaid.initialize({ startOnLoad: false, theme: 'base', securityLevel: 'strict',
                themeVariables: { primaryColor: '{{t.Background}}', primaryTextColor: '{{t.Primary}}',
                  primaryBorderColor: '{{t.Line}}', lineColor: '{{t.Line}}', secondaryColor: '{{t.Secondary}}',
                  tertiaryColor: '{{t.Background}}', edgeLabelBackground: '{{t.Background}}'{{MermaidLabelStyle.ChartVariables(t)}} },
                themeCSS: {{MermaidLabelStyle.ThemeCss(t.Background)}},
                flowchart: { useMaxWidth: true, htmlLabels: false } });
              for (var i = 0; i < boxes.length; i++) {
                var src = boxes[i].textContent;
                try { boxes[i].innerHTML = (await mermaid.render('msemail' + i, src)).svg; }
                catch (e) { plain(boxes[i], src); }
              }
              if (window.parent && window.parent.__msFit) window.parent.__msFit();
            })();
            </script>
            """;
        var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? html.Insert(end, script) : html + script;
    }

    private static string E(string s) => EmailHtmlRenderer.Enc(s);

    private const string PageCss = """
        :root { color-scheme: light dark; }
        html, body { margin: 0; }
        body { padding: 14px 16px 32px; background: #eceff3; font-family: 'Segoe UI Variable Text', 'Segoe UI', sans-serif; }
        @media (prefers-color-scheme: dark) { body { background: #1f1f1f; } .cap { color: #a8a8a8 !important; } }
        #stage { width: 720px; max-width: none; margin: 0 auto; }
        .cap { max-width: 720px; margin: 0 auto 10px; font-size: 12px; color: #59636e; }
        .msg { max-width: 720px; margin: 0 auto; background: #ffffff; border-radius: 8px; overflow: hidden;
               box-shadow: 0 1px 3px rgba(0,0,0,.14), 0 8px 24px rgba(0,0,0,.10); }
        .hdr { padding: 16px 22px 12px; border-bottom: 1px solid #e3e6ea; color: #1f2328; font-size: 13px; }
        .subject { font-size: 19px; font-weight: 600; line-height: 1.3; margin: 0 0 10px; overflow-wrap: anywhere; }
        .row { display: flex; gap: 10px; margin: 3px 0; }
        .k { width: 28px; flex: none; color: #59636e; }
        .v { min-width: 0; overflow-wrap: anywhere; }
        .empty { color: #8a6100; }
        .atts { margin-top: 6px; }
        .chip { display: inline-block; padding: 2px 10px; margin: 4px 6px 0 0; border: 1px solid #d0d7de;
                border-radius: 12px; font-size: 12px; color: #1f2328; background: #f6f8fa; }
        .notes { padding: 8px 22px; background: #fff8e5; color: #6b4e00; font-size: 12px; border-bottom: 1px solid #f0e2b6; }
        iframe { display: block; width: 100%; height: 240px; border: 0; background: #ffffff; }
        """;

    // srcdoc frames share the page's origin, so the page can size the frame to its content: no
    // inner scrollbar, the preview pane scrolls the whole message like a mail client does.
    private const string FitScript = """
        (function () {
          var f = document.getElementById('mailbody');
          var stage = document.getElementById('stage');
          function fit() {
            // A narrow pane shows the real 720 px message scaled down, like a print preview,
            // rather than reflowing it into a column no mail client would draw.
            stage.style.zoom = Math.max(0.4, Math.min(1, (document.documentElement.clientWidth - 32) / 720));
            try { var d = f.contentDocument; if (!d) return;
                  f.style.height = Math.max(120, d.documentElement.scrollHeight) + 'px'; } catch (e) { }
          }
          window.__msFit = fit;
          fit();
          f.addEventListener('load', function () {
            fit();
            try { new ResizeObserver(fit).observe(f.contentDocument.body); } catch (e) { }
            try { f.contentDocument.querySelectorAll('img').forEach(function (i) { i.addEventListener('load', fit); }); } catch (e) { }
          });
          window.addEventListener('resize', fit);
        })();
        """;
}
