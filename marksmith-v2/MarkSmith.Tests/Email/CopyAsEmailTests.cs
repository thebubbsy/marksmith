using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services.Email;
using MarkSmith.ViewModels;
using SkiaSharp;
using Xunit;

namespace MarkSmith.Tests.Email;

/// <summary>
/// "Copy as email": the document as an email body on the clipboard, for pasting into a message
/// that's already open. Planned since the email work began (run #19) and never built until now.
/// </summary>
[Collection("EmailOutbox")]
public class CopyAsEmailTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ms_copyemail_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static EmailDocument Doc()
    {
        var doc = new EmailDocument
        {
            Subject = "Report",
            HtmlBody = "<!DOCTYPE html><html><head><style>p{}</style></head><body style=\"margin:0\">"
                     + "<p>Hi</p><img src=\"cid:pic1@ms\" width=\"2\" height=\"2\" /><img src='cid:missing@ms' /></body></html>",
            TextBody = "Hi",
        };
        doc.InlineImages.Add(new EmailInlineImage("pic1@ms", new byte[] { 1, 2, 3 }, "image/png", "chart.png", 2, 2));
        return doc;
    }

    [Fact]
    public void Data_uri_mode_inlines_the_pictures_and_keeps_only_the_body()
    {
        var c = EmailClipboard.Build(Doc(), ClipboardImageMode.DataUri);
        Assert.StartsWith("<p>Hi</p>", c.Html);
        Assert.DoesNotContain("<head", c.Html);
        Assert.Contains("src=\"data:image/png;base64,AQID\"", c.Html);
        // A cid with no picture behind it is left as it was rather than guessed at.
        Assert.Contains("src='cid:missing@ms'", c.Html);
        Assert.Equal("Hi", c.Text);
        Assert.Equal("Report", c.Subject);
    }

    [Fact]
    public void File_mode_writes_each_picture_and_links_it_by_file_uri()
    {
        var c = EmailClipboard.Build(Doc(), ClipboardImageMode.File, _dir);
        var file = Assert.Single(Directory.GetFiles(_dir, "*.png", SearchOption.AllDirectories));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
        Assert.Contains($"src=\"{new Uri(file).AbsoluteUri}\"", c.Html);
        Assert.StartsWith("file:///", new Uri(file).AbsoluteUri);
    }

    [Fact]
    public void Classic_Outlook_gets_files_and_everything_else_data_uris()
    {
        Assert.Equal(ClipboardImageMode.File, EmailClipboard.ModeFor(new MailHandler(MailAppKind.ClassicOutlook, "Outlook (classic)")));
        Assert.Equal(ClipboardImageMode.DataUri, EmailClipboard.ModeFor(new MailHandler(MailAppKind.NewOutlook, "Outlook")));
        Assert.Equal(ClipboardImageMode.DataUri, EmailClipboard.ModeFor(new MailHandler(MailAppKind.None, "")));
        Assert.Equal(ClipboardImageMode.DataUri, EmailClipboard.ModeFor(new MailHandler(MailAppKind.Other, "Thunderbird")));
    }

    [Fact]
    public void Old_copied_pictures_are_cleared()
    {
        var old = Directory.CreateDirectory(Path.Combine(_dir, "old")).FullName;
        var fresh = Directory.CreateDirectory(Path.Combine(_dir, "fresh")).FullName;
        Directory.SetLastWriteTime(old, DateTime.Now.AddDays(-2));
        Assert.Equal(1, EmailClipboard.Clean(DateTime.Now, _dir));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Theory]
    [InlineData(MailAppKind.NewOutlook, "data:image/png;base64,")]
    [InlineData(MailAppKind.ClassicOutlook, "file:///")]
    public async Task The_app_copies_the_document_with_its_picture(MailAppKind app, string expectedSrc)
    {
        var input = Path.Combine(_dir, "Report.md");
        using (var bmp = new SKBitmap(8, 8))
        {
            using (var canvas = new SKCanvas(bmp)) canvas.Clear(SKColors.Teal);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_dir, "chart.png"), data.ToArray());
        }
        File.WriteAllText(input, "# Report\n\nThe numbers are in.\n\n![Chart](chart.png)\n");
        var vm = new MainViewModel { UsePasteSource = false, InputFilePath = input, EmailSubjectTemplate = "{title}" };
        EmailClipboardContent? copied = null;
        vm.PutEmailOnClipboard = c => copied = c;

        var lookup = MailApps.Lookup;
        MailApps.Lookup = _ => new MailHandler(app, "Outlook");
        try { await vm.CopyAsEmailAsync(); }
        finally { MailApps.Lookup = lookup; }

        Assert.NotNull(copied);
        Assert.Contains("The numbers are in.", copied!.Html);
        Assert.DoesNotContain("cid:", copied.Html);
        Assert.Contains("src=\"" + expectedSrc, copied.Html);
        Assert.Contains("The numbers are in.", copied.Text);
        Assert.Equal("Report", copied.Subject);
        Assert.Equal(app == MailAppKind.ClassicOutlook
            ? "Copied as email with its picture. Paste it into Outlook; a web mail app won't show the pictures (use Email draft for those)."
            : "Copied as email with its picture. Paste it into a new message or a reply.", vm.StatusText);
        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
    }
}
