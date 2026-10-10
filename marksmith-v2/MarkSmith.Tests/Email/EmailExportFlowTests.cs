using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services.Email;
using MarkSmith.ViewModels;
using MimeKit;
using Xunit;

namespace MarkSmith.Tests.Email;

/// <summary>
/// Run #22, email Phase 2: the desktop flow behind Export ▸ Email draft / Save as email. The mail
/// app is never launched: <see cref="EmailOutbox.Open"/> is swapped for a recorder.
/// </summary>
[Collection("EmailOutbox")]
public class EmailExportFlowTests
{
    private static (MainViewModel Vm, string Dir) FileBackedVm(string markdown = "# Report\n\nBody text.\n")
    {
        var dir = Directory.CreateTempSubdirectory("ms_email_").FullName;
        var input = Path.Combine(dir, "Report.md");
        File.WriteAllText(input, markdown);
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
            EmailFormat = "eml",
        };
        return (vm, dir);
    }

    private static async Task<(List<string> Opened, T Result)> WithOpener<T>(bool succeeds, Func<Task<T>> run)
    {
        var opened = new List<string>();
        var previous = EmailOutbox.Open;
        var lookup = MailApps.Lookup;
        EmailOutbox.Open = p => { opened.Add(p); return succeeds; };
        // Whatever this PC has set for .eml files: the status line names the app.
        MailApps.Lookup = _ => new MailHandler(MailAppKind.None, "");
        try { return (opened, await run()); }
        finally { EmailOutbox.Open = previous; MailApps.Lookup = lookup; }
    }

    [Fact]
    public async Task Email_draft_lands_in_the_outbox_and_opens_in_the_mail_app()
    {
        var (vm, dir) = FileBackedVm();
        try
        {
            var (opened, _) = await WithOpener(true, async () => { await vm.CreateEmailDraftAsync(); return 0; });

            var path = Assert.Single(opened);
            Assert.StartsWith(EmailOutbox.Directory, path);
            Assert.EndsWith(".eml", path);
            Assert.Equal("Email draft \"Report\" opened in your mail app", vm.StatusText);
            Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
            Assert.Equal(path, vm.StatusOutputPath);
            Assert.False(vm.IsBusy);

            var msg = MimeMessage.Load(path);
            Assert.Equal("Report", msg.Subject);
            Assert.Equal("1", msg.Headers["X-Unsent"]);
            Assert.Contains("Body text.", msg.HtmlBody);
            // The title is the subject, so the body doesn't shout it again.
            Assert.DoesNotContain("<h1", msg.HtmlBody);
            File.Delete(path);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Without_a_mail_app_the_draft_is_kept_and_the_status_says_what_to_do()
    {
        var (vm, dir) = FileBackedVm();
        try
        {
            var (opened, _) = await WithOpener(false, async () => { await vm.CreateEmailDraftAsync(); return 0; });

            var path = Assert.Single(opened);
            Assert.True(File.Exists(path));
            Assert.Contains("no app set to open .eml files", vm.StatusText);
            Assert.Equal(StatusSeverity.Warning, vm.StatusSeverity);
            Assert.Equal(path, vm.StatusOutputPath); // "Show in folder" still finds it
            File.Delete(path);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Save_as_email_writes_beside_the_other_exports_without_opening_anything()
    {
        var (vm, dir) = FileBackedVm();
        try
        {
            var (opened, _) = await WithOpener(true, async () => { await vm.SaveEmailAsync(); return 0; });

            Assert.Empty(opened);
            var expected = Path.Combine(dir, "Report.eml");
            Assert.True(File.Exists(expected), vm.StatusText);
            Assert.StartsWith("Email saved: Report.eml", vm.StatusText);
            Assert.Equal(expected, vm.LastOutputPath);
            Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Bad_default_recipients_are_flagged_in_the_panel_and_left_out_of_the_draft()
    {
        var (vm, dir) = FileBackedVm();
        try
        {
            vm.EmailTo = "ann@example.com; nobody";
            Assert.True(vm.HasEmailToProblem);
            Assert.Equal("\"nobody\" isn't an email address, so drafts leave it out.", vm.EmailToProblem);

            await WithOpener(true, async () => { await vm.SaveEmailAsync(); return 0; });

            Assert.Contains("Left out \"nobody\"", vm.StatusText);
            // Before the folder, so a long path can't trim it out of the status bar.
            Assert.True(vm.StatusText.IndexOf("Left out", StringComparison.Ordinal) < vm.StatusText.IndexOf(" · in ", StringComparison.Ordinal), vm.StatusText);
            Assert.Equal(StatusSeverity.Warning, vm.StatusSeverity);
            var msg = MimeMessage.Load(Path.Combine(dir, "Report.eml"));
            Assert.Equal("ann@example.com", Assert.Single(msg.To.Mailboxes).Address);

            vm.EmailTo = "ann@example.com";
            Assert.False(vm.HasEmailToProblem);
        }
        finally
        {
            vm.EmailTo = "";
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Subject_preview_follows_the_template()
    {
        var (vm, dir) = FileBackedVm();
        try
        {
            await vm.LastFileReadTask;
            vm.EmailSubjectTemplate = "Weekly: {title}";
            Assert.Equal("Subject: Weekly: Report", vm.EmailSubjectPreview);
        }
        finally
        {
            vm.EmailSubjectTemplate = "{title}";
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Preview_as_email_shows_the_message_the_export_writes()
    {
        var (vm, dir) = FileBackedVm("# Report\n\nBody text.\n\n```mermaid\nflowchart LR\n  A --> B\n```\n\n:::smartart type=\"process\"\n- Plan\n- Ship\n:::\n");
        try
        {
            vm.EmailTo = "ann@example.com";
            vm.EmailAttachPdf = true;
            var page = vm.BuildEmailPreviewHtml(vm.PrepareMarkdown(File.ReadAllText(vm.InputFilePath)));

            Assert.Contains("<div class=\"subject\">Report</div>", page);
            Assert.Contains("ann@example.com", page);
            Assert.Contains("Report.pdf", page); // the attachment chip
            // The body is the email HTML, isolated in a frame, with pictures inlined for the preview.
            Assert.Contains("<iframe id=\"mailbody\"", page);
            Assert.Contains("data:image/png;base64,", page);
            Assert.DoesNotContain("cid:", page);
            // Diagrams are drawn live, not reported as missing.
            Assert.Contains("data-ms-mermaid", page);
            Assert.Contains(MarkSmith.Services.WebAssets.Mermaid, page);
            Assert.DoesNotContain("couldn't be drawn", page);
        }
        finally
        {
            vm.EmailTo = "";
            vm.EmailAttachPdf = false;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Live_diagram_placeholders_never_reach_an_exported_email()
    {
        var doc = EmailComposer.Compose(new EmailComposeRequest { Markdown = "```mermaid\nflowchart LR\n  A --> B\n```" },
            new AppSettings(), new ThemeDefinition("Light", "#ffffff", "#222222", "#0b3d91", "#f5f5f5", "#dddddd", "#0b5cad", "#eeeeee", "#333333"));
        Assert.DoesNotContain("data-ms-mermaid", doc.HtmlBody);
        Assert.Contains("flowchart LR", doc.HtmlBody);
    }

    [Fact]
    public void Preview_without_recipients_says_where_to_add_them()
    {
        var doc = new EmailDocument { Subject = "Hi", HtmlBody = "<html><head></head><body><p>x</p></body></html>" };
        var page = EmailPreviewPage.Build(doc, EmailPalette.Clean);
        Assert.Contains("No recipients yet", page);
        Assert.DoesNotContain("mermaid", page);
    }

    [Fact]
    public void Outbox_names_never_collide_and_old_drafts_are_cleared()
    {
        var first = EmailOutbox.PathFor("Plan: Q3?", "eml");
        Assert.Equal("Plan  Q3.eml", Path.GetFileName(first));
        File.WriteAllText(first, "x");
        var second = EmailOutbox.PathFor("Plan: Q3?", "eml");
        Assert.Equal("Plan  Q3 (2).eml", Path.GetFileName(second));
        File.WriteAllText(second, "y");
        File.SetLastWriteTime(first, DateTime.Now.AddDays(-8));

        EmailOutbox.Clean();

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        File.Delete(second);
        Assert.Equal("Email draft", EmailOutbox.SafeStem("  ...  "));
    }
}
