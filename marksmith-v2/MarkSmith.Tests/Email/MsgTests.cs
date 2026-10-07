using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Plugins;
using MarkSmith.Services.Email;
using MarkSmith.ViewModels;
using Xunit;
using OutlookMessage = MsgReader.Outlook.Storage.Message;
using OutlookAttachment = MsgReader.Outlook.Storage.Attachment;
using OutlookRecipientType = MsgReader.Outlook.RecipientType;

namespace MarkSmith.Tests.Email;

/// <summary>
/// Run #27, email Phase 3: Outlook .msg drafts (written by hand on OpenMcdf, read back with
/// MSGReader as an independent check), .msg import through the shared email importer, and how
/// "Automatic" picks .eml or .msg from the PC's default apps. Outlook is never launched.
/// </summary>
[Collection("EmailOutbox")]
public class MsgTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static readonly ThemeDefinition Theme =
        new("Light", "#ffffff", "#222222", "#0b3d91", "#f5f5f5", "#dddddd", "#0b5cad", "#eeeeee", "#333333");

    private static string NewDir() => Directory.CreateTempSubdirectory("ms_msg_").FullName;

    private static EmailDocument SampleDraft()
    {
        var doc = new EmailDocument
        {
            Subject = "Quarterly plan: café ✓ 🚀",
            HtmlBody = "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\" /></head>"
                     + "<body><h2>Résumé</h2><p>Numbers are in.</p><img src=\"cid:chart@ms\" width=\"10\" alt=\"Chart\" /></body></html>",
            TextBody = "Résumé\n\nNumbers are in.",
        };
        doc.To.Add("Bob Smith <bob@example.com>");
        doc.To.Add("ann@example.com");
        doc.Cc.Add("team@example.com");
        doc.Bcc.Add("audit@example.com");
        doc.InlineImages.Add(new EmailInlineImage("chart@ms", Png, "image/png", "diagram-1.png", 10, 10));
        doc.Attachments.Add(new EmailAttachment("Plan.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 fake"), "application/pdf"));
        return doc;
    }

    private static T WithHandlers<T>(MailHandler eml, MailHandler msg, Func<T> run)
    {
        var previous = MailApps.Lookup;
        MailApps.Lookup = ext => ext == ".msg" ? msg : eml;
        try { return run(); }
        finally { MailApps.Lookup = previous; }
    }

    private static readonly MailHandler Classic = new(MailAppKind.ClassicOutlook, "Outlook (classic)");
    private static readonly MailHandler NewOutlook = new(MailAppKind.NewOutlook, "the new Outlook");
    private static readonly MailHandler Thunderbird = new(MailAppKind.Other, "Thunderbird");
    private static readonly MailHandler Ask = new(MailAppKind.AskEachTime, "the Windows app picker");
    private static readonly MailHandler Nothing = new(MailAppKind.None, "");

    // ---------- writer ----------

    [Fact]
    public void Msg_draft_reads_back_in_an_independent_reader()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "draft.msg");
            MsgWriter.Write(SampleDraft(), path);

            using var m = new OutlookMessage(path, FileAccess.Read);
            Assert.Equal("Quarterly plan: café ✓ 🚀", m.Subject);
            Assert.Contains("<h2>Résumé</h2>", m.BodyHtml);
            Assert.Contains("cid:chart@ms", m.BodyHtml);
            Assert.Contains("Numbers are in.", m.BodyText);

            var to = m.Recipients.Where(r => r.Type == OutlookRecipientType.To).ToList();
            Assert.Equal(new[] { "bob@example.com", "ann@example.com" }, to.Select(r => r.Email));
            Assert.Equal("Bob Smith", to[0].DisplayName);
            Assert.Equal("team@example.com", Assert.Single(m.Recipients, r => r.Type == OutlookRecipientType.Cc).Email);
            Assert.Equal("audit@example.com", Assert.Single(m.Recipients, r => r.Type == OutlookRecipientType.Bcc).Email);

            var files = m.Attachments.OfType<OutlookAttachment>().ToList();
            var chart = Assert.Single(files, a => a.FileName == "diagram-1.png");
            Assert.Equal("chart@ms", chart.ContentId);
            Assert.True(chart.Hidden);   // a picture in the body, not a paperclip attachment
            Assert.Equal(Png, chart.Data);
            var pdf = Assert.Single(files, a => a.FileName == "Plan.pdf");
            Assert.False(pdf.Hidden);
            Assert.Equal("application/pdf", pdf.MimeType);

            Assert.True(MsgImporter.IsUnsent(path)); // opens in Outlook as a compose window
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_sent_message_has_no_unsent_flag_and_keeps_its_sender()
    {
        var doc = SampleDraft();
        doc.IsDraft = false;
        doc.From = "Ann Lee <ann@example.com>";
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "sent.msg");
            MsgWriter.Write(doc, path);
            Assert.False(MsgImporter.IsUnsent(path));
            using var m = new OutlookMessage(path, FileAccess.Read);
            Assert.Equal("ann@example.com", m.Sender.Email);
            Assert.Equal("Ann Lee", m.Sender.DisplayName);
            Assert.NotNull(m.SentOn);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Bad_addresses_are_left_out_and_the_bytes_api_matches_the_file()
    {
        var doc = new EmailDocument { Subject = "Hi", HtmlBody = "<p>x</p>", TextBody = "x" };
        doc.To.Add("not an address");
        doc.To.Add("ok@example.com");
        var bytes = MsgWriter.ToBytes(doc);
        Assert.Equal(0xD0, bytes[0]); // a compound file ("D0 CF 11 E0")
        Assert.Equal("Hi", MsgImporter.SubjectOf(bytes));
        using var m = new OutlookMessage(new MemoryStream(bytes), FileAccess.Read, false);
        Assert.Equal("ok@example.com", Assert.Single(m.Recipients).Email);
        Assert.Empty(m.Attachments);
    }

    // ---------- import ----------

    [Fact]
    public void A_MarkSmith_msg_draft_round_trips_like_the_eml_one()
    {
        var doc = EmailComposer.Compose(new EmailComposeRequest
        {
            Markdown = "# Weekly update\n\nShipped **three** things:\n\n- Import\n- Export\n\n| A | B |\n|---|--:|\n| 1 | 2 |\n\n- [x] Draft\n- [ ] Send\n",
            To = "ann@example.com",
        }, new AppSettings(), Theme);
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "update.msg");
            MsgWriter.Write(doc, path);
            var r = MsgImporter.Import(path, QuotedHistoryMode.Collapse, Path.Combine(dir, "media"));
            Assert.True(r.IsDraft);
            Assert.StartsWith("# Weekly update\n\n", r.Markdown);
            Assert.DoesNotContain("**From:**", r.Markdown);
            Assert.Contains("Shipped **three** things:", r.Markdown);
            Assert.Contains("- Import\n- Export", r.Markdown);
            Assert.Contains("| 1 | 2 |", r.Markdown);
            Assert.Contains("- [x] Draft\n- [ ] Send", r.Markdown);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_received_msg_gets_the_header_block_its_pictures_and_a_folded_thread()
    {
        var doc = new EmailDocument
        {
            Subject = "RE: Q3 rollout",
            IsDraft = false,
            From = "Ann Lee <ann@example.com>",
            HtmlBody = "<html><body><div><p>Hi Bob,</p><p>Chart below.</p><p><img src=\"cid:chart01@x\" alt=\"Chart\"></p></div>"
                     + "<div id=\"divRplyFwdMsg\"><b>From:</b> Bob<br><b>Sent:</b> Thursday</div><div><p>Can you send the numbers?</p></div></body></html>",
            TextBody = "Hi Bob,",
        };
        doc.To.Add("Bob <bob@example.com>");
        doc.InlineImages.Add(new EmailInlineImage("chart01@x", Png, "image/png", "chart.png", 1, 1));
        doc.Attachments.Add(new EmailAttachment("Budget.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 fake"), "application/pdf"));
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "reply.msg");
            MsgWriter.Write(doc, path);
            var r = MsgImporter.Import(path);

            Assert.False(r.IsDraft);
            Assert.Equal("Ann Lee", r.From);
            Assert.StartsWith("# RE: Q3 rollout\n\n", r.Markdown);
            Assert.Contains("**From:** Ann Lee (ann@example.com)", r.Markdown);
            Assert.Contains("**To:** Bob (bob@example.com)", r.Markdown);
            Assert.Contains("**Attachments:** [Budget.pdf](reply_media/Budget.pdf)", r.Markdown);
            Assert.Contains("![Chart](reply_media/chart.png)", r.Markdown);
            Assert.True(File.Exists(Path.Combine(dir, "reply_media", "chart.png")));
            Assert.True(r.HadQuotedHistory);
            Assert.Contains("<details>", r.Markdown);
            Assert.Contains("Can you send the numbers?", r.Markdown[r.Markdown.IndexOf("<details>", StringComparison.Ordinal)..]);
            Assert.Equal(1, r.InlineImages);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task The_file_reader_opens_msg_files_as_email()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "note.msg");
            var doc = new EmailDocument { Subject = "Lunch", HtmlBody = "<p>Tomorrow at <b>noon</b>?</p>", TextBody = "Tomorrow at noon?" };
            MsgWriter.Write(doc, path);

            Assert.Contains("msg", PluginFileReader.NativeExtensions);
            Assert.True(PluginFileReader.CanOpen(path));
            Assert.False(PluginFileReader.IsMarkdownFile(path));
            var imported = await PluginFileReader.ImportAsync(path);
            Assert.Equal("Email", imported.Kind);
            Assert.Contains("# Lunch", imported.Markdown);
            Assert.Contains("Tomorrow at **noon**?", imported.Markdown);
            Assert.StartsWith("Opened email draft", imported.Summary);
        }
        finally { PluginFileReader.InvalidateCache(); Directory.Delete(dir, true); }
    }

    // ---------- Automatic format ----------

    [Theory]
    [InlineData("eml", "eml")]
    [InlineData("msg", "msg")]
    [InlineData(" MSG ", "msg")]
    public void A_fixed_choice_is_kept_whatever_the_PC_has(string setting, string expected) =>
        Assert.Equal(expected, WithHandlers(Thunderbird, Nothing, () => MailApps.Resolve(setting)));

    [Fact]
    public void Automatic_prefers_eml_and_takes_msg_only_when_that_is_what_reaches_Outlook()
    {
        Assert.Equal("eml", WithHandlers(Classic, Classic, () => MailApps.Resolve("auto")));
        Assert.Equal("eml", WithHandlers(NewOutlook, Classic, () => MailApps.Resolve("auto")));
        Assert.Equal("msg", WithHandlers(Thunderbird, Classic, () => MailApps.Resolve("auto")));
        Assert.Equal("msg", WithHandlers(Nothing, NewOutlook, () => MailApps.Resolve("auto")));
        Assert.Equal("msg", WithHandlers(Ask, Classic, () => MailApps.Resolve(null)));
        Assert.Equal("eml", WithHandlers(Thunderbird, Thunderbird, () => MailApps.Resolve("auto")));
        Assert.Equal("eml", WithHandlers(Ask, Ask, () => MailApps.Resolve("anything")));
    }

    [Fact]
    public void Automatic_explains_itself_in_plain_words()
    {
        Assert.Equal("Automatic uses .eml here: your .eml files open in Outlook (classic).",
            WithHandlers(Classic, Classic, MailApps.DescribeAutomatic));
        Assert.Equal("Automatic uses .msg here: .eml files open in Thunderbird, but .msg files open in Outlook (classic).",
            WithHandlers(Thunderbird, Classic, MailApps.DescribeAutomatic));
        Assert.Equal("Automatic uses .msg here: Windows asks which app opens .eml files, but .msg files open in the new Outlook.",
            WithHandlers(Ask, NewOutlook, MailApps.DescribeAutomatic));
        Assert.Contains("pick Outlook and tick \"Always\"", WithHandlers(Ask, Ask, MailApps.DescribeAutomatic));
        Assert.Contains("Default apps", WithHandlers(Nothing, Nothing, MailApps.DescribeAutomatic));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Microsoft Office\Root\Office16\OUTLOOK.EXE", "Outlook", MailAppKind.ClassicOutlook)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.OutlookForWindows_1.2026.812.0_x64__8wekyb3d8bbwe\olk.exe", "Outlook (new)", MailAppKind.NewOutlook)]
    [InlineData("", "Outlook (new)", MailAppKind.NewOutlook)]
    [InlineData(@"C:\windows\system32\OpenWith.exe", "Pick an application", MailAppKind.AskEachTime)]
    [InlineData(@"C:\Program Files\Mozilla Thunderbird\thunderbird.exe", "Thunderbird", MailAppKind.Other)]
    [InlineData("", "", MailAppKind.None)]
    public void Associations_are_recognised(string exe, string friendly, MailAppKind expected) =>
        Assert.Equal(expected, MailApps.Classify(exe, friendly).Kind);

    // ---------- the desktop flow ----------

    private static (MainViewModel Vm, string Dir) FileBackedVm(string format)
    {
        var dir = Directory.CreateTempSubdirectory("ms_msgvm_").FullName;
        var input = Path.Combine(dir, "Report.md");
        File.WriteAllText(input, "# Report\n\nBody text.\n");
        var vm = new MainViewModel
        {
            FileNameTemplate = "{title}",
            OutputFolder = "",
            UsePasteSource = false,
            InputFilePath = input,
            EmailTo = "",
            EmailCc = "",
            EmailSubjectTemplate = "{title}",
            EmailAttachPdf = false,
            EmailAttachDocx = false,
            EmailFormat = format,
        };
        return (vm, dir);
    }

    private static async Task<List<string>> Draft(MainViewModel vm)
    {
        var opened = new List<string>();
        var previous = EmailOutbox.Open;
        EmailOutbox.Open = p => { opened.Add(p); return true; };
        try { await vm.CreateEmailDraftAsync(); }
        finally { EmailOutbox.Open = previous; }
        return opened;
    }

    [Fact]
    public async Task Email_draft_follows_the_format_setting_and_names_the_app()
    {
        var (vm, dir) = FileBackedVm("msg");
        var previous = MailApps.Lookup;
        MailApps.Lookup = _ => Classic;
        try
        {
            var path = Assert.Single(await Draft(vm));
            Assert.EndsWith(".msg", path);
            Assert.StartsWith(EmailOutbox.Directory, path);
            Assert.Equal("Email draft \"Report\" opened in Outlook (classic)", vm.StatusText);
            Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
            using (var m = new OutlookMessage(path, FileAccess.Read)) Assert.Equal("Report", m.Subject);
            Assert.True(MsgImporter.IsUnsent(path));
            File.Delete(path);
        }
        finally { MailApps.Lookup = previous; Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task When_Windows_will_ask_which_app_the_status_says_what_to_pick()
    {
        var (vm, dir) = FileBackedVm("auto");
        var previous = MailApps.Lookup;
        MailApps.Lookup = _ => Ask;
        try
        {
            var path = Assert.Single(await Draft(vm));
            Assert.EndsWith(".eml", path);
            Assert.Equal("Email draft \"Report\" is ready. Windows is asking which app opens .eml files: pick Outlook and tick \"Always\"", vm.StatusText);
            File.Delete(path);
        }
        finally { MailApps.Lookup = previous; Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Save_as_Outlook_message_writes_a_msg_beside_the_other_exports()
    {
        var (vm, dir) = FileBackedVm("eml");
        try
        {
            var opened = new List<string>();
            var previous = EmailOutbox.Open;
            EmailOutbox.Open = p => { opened.Add(p); return true; };
            try { await vm.SaveOutlookMessageAsync(); }
            finally { EmailOutbox.Open = previous; }

            Assert.Empty(opened);
            var path = Path.Combine(dir, "Report.msg");
            Assert.True(File.Exists(path), vm.StatusText);
            Assert.StartsWith("Outlook message saved: Report.msg", vm.StatusText);
            Assert.Equal(path, vm.StatusOutputPath);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("msg", "msg")]
    [InlineData("EML", "eml")]
    [InlineData("pdf", "auto")]
    [InlineData("", "auto")]
    public void The_format_setting_only_takes_the_three_choices(string value, string expected)
    {
        var vm = new MainViewModel { UsePasteSource = true, EmailFormat = "auto" };
        vm.EmailFormat = value;
        Assert.Equal(expected, vm.EmailFormat);
        Assert.False(string.IsNullOrWhiteSpace(vm.EmailFormatDescription));
        vm.EmailFormat = "auto";
    }
}
