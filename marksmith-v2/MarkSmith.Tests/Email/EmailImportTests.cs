using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Plugins;
using MarkSmith.Services.Email;
using MarkSmith.ViewModels;
using MimeKit;
using Xunit;

namespace MarkSmith.Tests.Email;

/// <summary>
/// Run #23, email Phase 6: opening an .eml as Markdown. Fixtures are built with MimeKit in the
/// shapes Outlook, Gmail and plain-text clients send, plus a draft MarkSmith itself wrote.
/// </summary>
public class EmailImportTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static string NewDir() => Directory.CreateTempSubdirectory("ms_emlimp_").FullName;

    // An Outlook-on-the-web style reply: safety banner, reply, phone sign-off, a CID chart, a
    // tracking pixel, a PDF attachment, and the quoted earlier message under #divRplyFwdMsg.
    private static MimeMessage OutlookReply()
    {
        var msg = new MimeMessage { Subject = "RE: Q3 rollout", Date = new DateTimeOffset(2026, 10, 3, 9, 14, 0, TimeSpan.Zero) };
        msg.From.Add(new MailboxAddress("Ann Lee", "ann@example.com"));
        msg.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        msg.Cc.Add(new MailboxAddress("", "team@example.com"));

        var builder = new BodyBuilder();
        var img = builder.LinkedResources.Add("chart.png", Png, new ContentType("image", "png"));
        img.ContentId = "chart01@x";
        builder.Attachments.Add("Budget.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 fake"), new ContentType("application", "pdf"));
        builder.HtmlBody =
            "<html><body>"
            + "<table><tr><td>You don't often get email from ann@example.com. <a href='https://aka.ms/LearnAboutSenderIdentification'>Learn why this is important</a></td></tr></table>"
            + "<div><p>Hi Bob,</p><p>Numbers are <b>in</b>:</p>"
            + "<table><tr><th>Region</th><th>Units</th></tr><tr><td>North</td><td>12</td></tr><tr><td>South</td><td>9</td></tr></table>"
            + "<p><img src='cid:chart01@x' alt='Chart'></p><p>Sent from my iPhone</p>"
            + "<img src='https://track.example/o.gif' width='1' height='1'></div>"
            + "<div id='appendonsend'></div><hr style='display:inline-block;width:98%'>"
            + "<div id='divRplyFwdMsg'><b>From:</b> Bob<br><b>Sent:</b> Thursday<br><b>Subject:</b> Q3 rollout</div>"
            + "<div><p>Can you send the numbers?</p></div>"
            + "</body></html>";
        msg.Body = builder.ToMessageBody();
        return msg;
    }

    [Fact]
    public void Outlook_Reply_Becomes_Clean_Markdown_With_Header_Images_Attachments_And_A_Folded_Thread()
    {
        var dir = NewDir();
        var eml = Path.Combine(dir, "Q3 reply.eml");
        OutlookReply().WriteTo(eml);

        var r = EmailImporter.Import(eml);
        var md = r.Markdown;

        Assert.StartsWith("# RE: Q3 rollout\n\n**From:** Ann Lee (ann@example.com)\\\n**To:** Bob (bob@example.com)\\\n**Cc:** team@example.com\\\n**Date:** ", md);
        Assert.Contains("**Attachments:** [Budget.pdf](<Q3 reply_media/Budget.pdf>) (", md);
        Assert.Contains("Hi Bob,\n\nNumbers are **in**:", md);
        Assert.Contains("| Region | Units |\n| --- | --- |\n| North | 12 |", md);
        Assert.Contains("![Chart](<Q3 reply_media/chart.png>)", md);
        Assert.DoesNotContain("often get email", md);
        Assert.DoesNotContain("iPhone", md);
        Assert.DoesNotContain("track.example", md);

        // The earlier message is folded below the reply, never mixed into it.
        var fold = md.IndexOf("<details>\n<summary>Earlier in this thread</summary>", StringComparison.Ordinal);
        Assert.True(fold > md.IndexOf("Chart", StringComparison.Ordinal));
        Assert.Contains("Can you send the numbers?", md[fold..]);
        Assert.EndsWith("</details>\n", md);

        Assert.True(File.Exists(Path.Combine(dir, "Q3 reply_media", "chart.png")));
        Assert.True(File.Exists(Path.Combine(dir, "Q3 reply_media", "Budget.pdf")));
        Assert.Equal("Ann Lee", r.From);
        Assert.Equal(1, r.InlineImages);
        Assert.Equal(new[] { "Budget.pdf" }, r.Attachments);
        Assert.StartsWith("Opened email from Ann Lee · ", r.Summary);
        Assert.EndsWith("1 image and 1 attachment saved to Q3 reply_media", r.Summary);
    }

    [Fact]
    public void Reimporting_Reuses_The_Media_Instead_Of_Duplicating_It()
    {
        var dir = NewDir();
        var eml = Path.Combine(dir, "a.eml");
        OutlookReply().WriteTo(eml);
        var first = EmailImporter.Import(eml).Markdown;
        var second = EmailImporter.Import(eml).Markdown;
        Assert.Equal(first, second);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, "a_media")).Length);
    }

    [Theory]
    [InlineData(QuotedHistoryMode.Remove)]
    [InlineData(QuotedHistoryMode.Keep)]
    public void Quoted_History_Can_Be_Removed_Or_Kept_Inline(QuotedHistoryMode mode)
    {
        var r = EmailImporter.Import(OutlookReply(), mode, Path.Combine(NewDir(), "m"), null);
        Assert.DoesNotContain("<details>", r.Markdown);
        Assert.Equal(mode == QuotedHistoryMode.Keep, r.Markdown.Contains("Can you send the numbers?"));
        Assert.True(r.HadQuotedHistory);
    }

    [Fact]
    public void Classic_Outlook_Border_Header_Starts_The_History()
    {
        var msg = new MimeMessage { Subject = "Fwd" };
        msg.From.Add(new MailboxAddress("Ann", "ann@example.com"));
        msg.Body = new TextPart("html")
        {
            Text = "<div class=WordSection1><p class=MsoNormal>Looks good.<o:p></o:p></p>"
                 + "<div><div style='border:none;border-top:solid #E1E1E1 1.0pt;padding:3.0pt 0cm 0cm 0cm'>"
                 + "<p class=MsoNormal><b>From:</b> Bob &lt;bob@example.com&gt;<br><b>Sent:</b> Monday</p></div></div>"
                 + "<p class=MsoNormal>Original text<o:p></o:p></p></div>",
        };
        var md = EmailImporter.Import(msg, QuotedHistoryMode.Collapse, null, null).Markdown;
        var fold = md.IndexOf("<details>", StringComparison.Ordinal);
        Assert.True(fold > 0);
        Assert.Contains("Looks good.", md[..fold]);
        Assert.Contains("Original text", md[fold..]);
    }

    [Fact]
    public void Plain_Text_Email_Folds_From_The_Wrote_Line()
    {
        var msg = new MimeMessage { Subject = "Lunch?" };
        msg.From.Add(new MailboxAddress("Carl", "carl@example.com"));
        msg.Body = new TextPart("plain") { Text = "Sure, 12:30.\n\nCarl\nSent from my iPhone\n\nOn Mon, 6 Oct 2026, Dana wrote:\n> Lunch tomorrow?\n> Anywhere is fine" };
        var md = EmailImporter.Import(msg, QuotedHistoryMode.Collapse, null, null).Markdown;
        var fold = md.IndexOf("<details>", StringComparison.Ordinal);
        Assert.Contains("Sure, 12:30.\n\nCarl\n", md[..fold]);
        Assert.DoesNotContain("iPhone", md);
        Assert.Contains("On Mon, 6 Oct 2026, Dana wrote:\n\n> Lunch tomorrow?\n> Anywhere is fine", md[fold..]);
    }

    [Fact]
    public void A_MarkSmith_Draft_Round_Trips_Without_A_Header_Block()
    {
        var theme = new ThemeDefinition("Light", "#ffffff", "#222222", "#0b3d91", "#f5f5f5", "#dddddd", "#0b5cad", "#eeeeee", "#333333");
        var doc = EmailComposer.Compose(new EmailComposeRequest
        {
            Markdown = "# Weekly update\n\nShipped **three** things:\n\n- Import\n- Export\n\n| A | B |\n|---|--:|\n| 1 | 2 |\n| 3 | 4 |\n\n```js\nlet x = 1;\n```\n",
            To = "ann@example.com",
        }, new AppSettings(), theme);
        var msg = MimeMessage.Load(new MemoryStream(EmlWriter.ToBytes(doc)));

        var r = EmailImporter.Import(msg, QuotedHistoryMode.Collapse, null, null);
        Assert.True(r.IsDraft);
        Assert.StartsWith("# Weekly update\n\n", r.Markdown);
        Assert.DoesNotContain("**From:**", r.Markdown);
        Assert.Contains("Shipped **three** things:", r.Markdown);
        Assert.Contains("- Import\n- Export", r.Markdown);
        Assert.Contains("| A | B |", r.Markdown);
        Assert.Contains("| 1 | 2 |", r.Markdown);
        Assert.Contains("let x = 1;", r.Markdown);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(r.Markdown, "^# ", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
    }

    [Fact]
    public void File_Reader_Imports_Email_And_Html_Once_And_Knows_What_It_Converted()
    {
        var dir = NewDir();
        var html = Path.Combine(dir, "page.html");
        File.WriteAllText(html, "<h1>Page</h1><p>Text</p>");
        var md = Path.Combine(dir, "notes.md");
        File.WriteAllText(md, "# Notes");

        Assert.True(PluginFileReader.CanOpen(Path.Combine(dir, "x.eml")));
        Assert.False(PluginFileReader.IsMarkdownFile(html));
        Assert.True(PluginFileReader.IsMarkdownFile(md));

        var a = PluginFileReader.ImportAsync(html).GetAwaiter().GetResult();
        var b = PluginFileReader.ImportAsync(html).GetAwaiter().GetResult();
        Assert.Equal("# Page\n\nText\n", a.Markdown);
        Assert.Equal("HTML", a.Kind);
        Assert.Same(a, b); // cached: the preview and the exports don't re-convert

        var plain = PluginFileReader.ImportAsync(md).GetAwaiter().GetResult();
        Assert.Null(plain.Kind);
        Assert.Equal("# Notes", plain.Markdown);
    }

    [Fact]
    public async Task Concurrent_Opens_Share_One_Import_And_Keep_The_Attachments()
    {
        // The preview and the editor both ask the moment a file opens. They used to race to write
        // the same media files, and the loser silently dropped the attachment from the header.
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "emlrace_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var eml = Path.Combine(dir, "race.eml");
        OutlookReply().WriteTo(eml);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => PluginFileReader.ImportAsync(eml))));
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Contains("**Attachments:** [Budget.pdf]", results[0].Markdown);
        Assert.Contains("1 attachment", results[0].Summary);
    }

    [Fact]
    public async Task Opening_An_Email_Puts_Markdown_In_The_Editor_Not_Raw_Mime()
    {
        var dir = NewDir();
        var eml = Path.Combine(dir, "reply.eml");
        OutlookReply().WriteTo(eml);
        var vm = new MainViewModel { UsePasteSource = false, InputFilePath = eml };

        for (int i = 0; i < 100 && vm.SourceImportKind is null; i++) await Task.Delay(50);

        Assert.Equal("Email", vm.SourceImportKind);
        Assert.StartsWith("# RE: Q3 rollout", vm.CurrentMarkdown);
        Assert.DoesNotContain("Content-Type", vm.CurrentMarkdown);
        Assert.Contains("Ctrl+S saves a Markdown copy", vm.StatusText);
    }
}
