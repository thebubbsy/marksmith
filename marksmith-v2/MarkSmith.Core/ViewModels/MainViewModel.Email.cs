using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Email;

namespace MarkSmith.ViewModels;

// Email (Outlook drafts). Every path here is free on every plan (FeatureId.EmailDraft): no licence
// check, no trial use, no footer. The one exception is a Word *attachment*, which follows the DOCX
// licence like any other Word export.
public sealed partial class MainViewModel
{
    // Above this the status line warns: many mail servers bounce messages over ~20 MB once base64
    // has grown every part by a third, and new Outlook won't open a .msg over 14 MB.
    internal const long LargeEmailBytes = 10L * 1024 * 1024;

    [ObservableProperty] private string _emailTo = "";
    [ObservableProperty] private string _emailCc = "";
    [ObservableProperty] private string _emailSubjectTemplate = EmailComposer.DefaultSubjectTemplate;
    [ObservableProperty] private bool _emailRepeatTitleInBody;
    [ObservableProperty] private bool _emailAttachPdf;
    [ObservableProperty] private bool _emailAttachDocx;

    /// <summary>"Not an address: bob" under the To box while it holds something that isn't one;
    /// empty when every entry is fine.</summary>
    [ObservableProperty] private string _emailToProblem = "";
    [ObservableProperty] private string _emailCcProblem = "";

    public bool HasEmailToProblem => EmailToProblem.Length > 0;
    public bool HasEmailCcProblem => EmailCcProblem.Length > 0;

    /// <summary>What the subject template gives for the open document, shown under the box.</summary>
    public string EmailSubjectPreview
    {
        get
        {
            var markdown = CurrentMarkdown;
            var title = HistoryEntry.ExtractTitle(markdown ?? "");
            var subject = EmailComposer.BuildSubject(EmailSubjectTemplate, title, EmailSourceLabel(), markdown, DateTime.Now);
            return subject.Length == 0 ? "Subject: (taken from the document once it has text)" : $"Subject: {subject}";
        }
    }

    /// <summary>The preview follows the document, which changes on every keystroke; the view calls
    /// this when the Email options come into sight rather than recomputing it per key press.</summary>
    public void RefreshEmailSubjectPreview() => OnPropertyChanged(nameof(EmailSubjectPreview));

    private void LoadEmailSettings(AppSettings settings)
    {
        _emailTo = settings.EmailTo ?? "";
        _emailCc = settings.EmailCc ?? "";
        _emailSubjectTemplate = string.IsNullOrWhiteSpace(settings.EmailSubjectTemplate) ? EmailComposer.DefaultSubjectTemplate : settings.EmailSubjectTemplate;
        _emailRepeatTitleInBody = settings.EmailRepeatTitleInBody;
        _emailAttachPdf = settings.EmailAttachPdf;
        _emailAttachDocx = settings.EmailAttachDocx;
        _emailToProblem = DescribeBadAddresses(_emailTo);
        _emailCcProblem = DescribeBadAddresses(_emailCc);
    }

    partial void OnEmailToChanged(string value)
    {
        _settingsService.Current.EmailTo = value ?? "";
        EmailToProblem = DescribeBadAddresses(value);
        SaveSettingsDebounced();
    }

    partial void OnEmailCcChanged(string value)
    {
        _settingsService.Current.EmailCc = value ?? "";
        EmailCcProblem = DescribeBadAddresses(value);
        SaveSettingsDebounced();
    }

    partial void OnEmailToProblemChanged(string value) => OnPropertyChanged(nameof(HasEmailToProblem));
    partial void OnEmailCcProblemChanged(string value) => OnPropertyChanged(nameof(HasEmailCcProblem));

    partial void OnEmailSubjectTemplateChanged(string value)
    {
        _settingsService.Current.EmailSubjectTemplate = value ?? "";
        OnPropertyChanged(nameof(EmailSubjectPreview));
        SaveSettingsDebounced();
    }

    partial void OnEmailRepeatTitleInBodyChanged(bool value) { _settingsService.Current.EmailRepeatTitleInBody = value; SaveSettingsDebounced(); }
    partial void OnEmailAttachPdfChanged(bool value) { _settingsService.Current.EmailAttachPdf = value; SaveSettingsDebounced(); }
    partial void OnEmailAttachDocxChanged(bool value) { _settingsService.Current.EmailAttachDocx = value; SaveSettingsDebounced(); }

    /// <summary>Status line for a draft the browser extension asked for (POST /api/email), so the
    /// app says what just appeared in Outlook and links the saved copy.</summary>
    public void AnnounceEmailFromApi(string subject, string path, bool opened)
    {
        var name = subject.Length > 0 ? $"\"{subject}\"" : Path.GetFileName(path);
        AnnounceExport(opened
            ? $"Email draft {name} from the browser extension opened in your mail app"
            : $"Saved the email draft {name} from the browser extension, but Windows has no app set to open .eml files", path);
        StatusSeverity = opened ? StatusSeverity.Success : StatusSeverity.Warning;
    }

    [RelayCommand]
    private void ClearEmailOutbox()
    {
        var n = EmailOutbox.Clear();
        StatusText = n switch
        {
            0 => "There are no saved email drafts to clear.",
            1 => "Deleted 1 saved email draft.",
            _ => $"Deleted {n} saved email drafts.",
        };
        StatusSeverity = StatusSeverity.Informational;
    }

    internal static string DescribeBadAddresses(string? raw)
    {
        var (_, invalid) = EmailComposer.ParseAddresses(raw);
        return invalid.Count switch
        {
            0 => "",
            1 => $"\"{invalid[0]}\" isn't an email address, so drafts leave it out.",
            _ => $"These aren't email addresses, so drafts leave them out: {string.Join(", ", invalid.Select(i => $"\"{i}\""))}.",
        };
    }

    private string? EmailSourceLabel() =>
        !UsePasteSource && !string.IsNullOrWhiteSpace(InputFilePath) ? Path.GetFileNameWithoutExtension(InputFilePath) : null;

    /// <summary>Preview tab shows the document as the email Outlook will open (header, recipients,
    /// attachments, the email-safe body) instead of the themed page.</summary>
    [ObservableProperty] private bool _previewAsEmail;

    /// <summary>The "Preview as email" page for <paramref name="preparedMarkdown"/> (already through
    /// <see cref="PrepareMarkdown"/>). Built from the same composer as the export, so the preview
    /// can't drift from the file; only the diagrams are drawn live instead of harvested.</summary>
    public string BuildEmailPreviewHtml(string preparedMarkdown)
    {
        var settings = _settingsService.Current;
        var doc = EmailComposer.Compose(new EmailComposeRequest
        {
            Markdown = preparedMarkdown ?? "",
            SourceLabel = EmailSourceLabel(),
            BaseDirectory = UsePasteSource || string.IsNullOrWhiteSpace(InputFilePath) ? null : Path.GetDirectoryName(InputFilePath),
            LiveMermaidPlaceholders = true,
        }, settings, CurrentTheme);

        var stem = EmailOutbox.SafeStem(EmailSourceLabel() ?? SanitizeFileName(HistoryEntry.ExtractTitle(preparedMarkdown ?? "")));
        var attachments = new List<string>();
        if (settings.EmailAttachPdf) attachments.Add(stem + ".pdf");
        if (settings.EmailAttachDocx)
        {
            if (AppServices.License.CanExportDocx) attachments.Add(stem + ".docx");
            else doc.Notes.Add("The Word copy will be left off: attaching a .docx is a Pro feature (the email itself is free).");
        }
        return EmailPreviewPage.Build(doc, EmailPalette.From(CurrentTheme), attachments);
    }

    /// <summary>Writes the document as an Outlook draft to the outbox and opens it in the default
    /// mail app (classic or new Outlook, or whatever handles .eml), ready to edit and Send.</summary>
    public Task CreateEmailDraftAsync() => ExportEmailAsync(openInMailApp: true);

    /// <summary>Saves the document as an .eml draft next to the other exports.</summary>
    public Task SaveEmailAsync() => ExportEmailAsync(openInMailApp: false);

    private async Task ExportEmailAsync(bool openInMailApp)
    {
        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync(openInMailApp ? "an email draft" : "an email", async ct =>
        {
            var settings = _settingsService.Current;
            var outPath = openInMailApp ? EmailOutbox.PathFor(sourceLabel, "eml") : PrepareOutputPath(sourceLabel, "eml");
            if (openInMailApp) EmailOutbox.Clean();

            var palette = EmailPalette.From(CurrentTheme);
            List<byte[]?>? mermaid = null;
            if (markdown.Contains("```mermaid", StringComparison.Ordinal) && Host is not null)
            {
                StatusText = "Drawing diagrams for the email…";
                var prepared = EmailHtmlRenderer.Prepare(markdown, settings, CurrentTheme);
                mermaid = await _mermaidHarvest.RenderMermaidPngsAsync(Host, prepared, settings, palette.DiagramTheme());
            }
            ct.ThrowIfCancellationRequested();

            var notes = new List<string>();
            var attachments = await BuildEmailAttachmentsAsync(markdown, sourceLabel, settings, notes, ct);

            var doc = EmailComposer.Compose(new EmailComposeRequest
            {
                Markdown = markdown,
                SourceLabel = EmailSourceLabel(),
                BaseDirectory = UsePasteSource || string.IsNullOrWhiteSpace(InputFilePath) ? null : Path.GetDirectoryName(InputFilePath),
                MermaidPngs = mermaid,
                Attachments = attachments,
            }, settings, CurrentTheme);
            notes.InsertRange(0, doc.Notes);
            if (doc.PayloadBytes > LargeEmailBytes)
                notes.Add($"This email is {doc.PayloadBytes / (1024.0 * 1024):0.#} MB; many mail servers refuse messages over 20 MB.");

            ct.ThrowIfCancellationRequested();
            EmlWriter.Write(doc, outPath);
            LastOutputPath = outPath;
            if (!UsePasteSource) TrackRecent(InputFilePath);
            RecordExport(openInMailApp ? "Email draft" : "Email", outPath, markdown);

            var subject = doc.Subject.Length > 0 ? $"\"{doc.Subject}\"" : Path.GetFileName(outPath);
            // Caveats go before the folder: the status bar trims from the end, and a long path
            // used to push "Left out "bob"" out of sight.
            var caveat = notes.Count == 0 ? ""
                : $" · {notes[0].TrimEnd('.')}" + (notes.Count > 1 ? $" (+{notes.Count - 1} more)" : "");
            if (caveat.Length > 0) StatusSeverityOverride = StatusSeverity.Warning;
            string message;
            if (!openInMailApp)
            {
                RaiseExportCompleted("Email", outPath);
                message = $"Email saved: {Path.GetFileName(outPath)}{caveat} · in {Path.GetDirectoryName(outPath)}";
            }
            else if (EmailOutbox.Open(outPath))
            {
                message = $"Email draft {subject} opened in your mail app{caveat}";
            }
            else
            {
                message = $"Saved the email draft {subject}, but Windows has no app set to open .eml files. Pick Outlook under Settings > Apps > Default apps";
                StatusSeverityOverride = StatusSeverity.Warning;
            }
            AnnounceExport(message, outPath);
        });

        if (StatusSeverityOverride is { } severity)
        {
            StatusSeverity = severity;
            StatusSeverityOverride = null;
        }
    }

    // RunConversionAsync marks a finished run as Success; a draft that went out with a caveat
    // (a picture missing, no mail app) should read as a warning instead.
    private StatusSeverity? StatusSeverityOverride { get; set; }

    private async Task<List<EmailAttachment>> BuildEmailAttachmentsAsync(
        string markdown, string sourceLabel, AppSettings settings, List<string> notes, CancellationToken ct)
    {
        var list = new List<EmailAttachment>();
        if (!settings.EmailAttachPdf && !settings.EmailAttachDocx) return list;

        var temp = Path.Combine(Path.GetTempPath(), "MarkSmith-email-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var stem = EmailOutbox.SafeStem(sourceLabel);
        try
        {
            if (settings.EmailAttachPdf)
            {
                if (Host is null)
                {
                    notes.Add("The PDF copy was left off: the preview engine isn't ready yet.");
                }
                else
                {
                    StatusText = "Making the PDF copy…";
                    var pdf = Path.Combine(temp, stem + ".pdf");
                    await _pdfExport.ExportAsync(Host, BuildPreviewHtml(markdown), pdf, settings, markdown);
                    list.Add(new EmailAttachment(stem + ".pdf", await File.ReadAllBytesAsync(pdf, ct), "application/pdf"));
                }
            }
            ct.ThrowIfCancellationRequested();

            if (settings.EmailAttachDocx)
            {
                if (!AppServices.License.CanExportDocx)
                {
                    notes.Add("The Word copy was left off: attaching a .docx is a Pro feature (the email itself is free).");
                }
                else
                {
                    StatusText = "Making the Word copy…";
                    List<byte[]?>? mermaid = null;
                    if (markdown.Contains("```mermaid", StringComparison.Ordinal) && Host is not null)
                        mermaid = await _mermaidHarvest.RenderMermaidPngsAsync(Host, markdown, settings, CurrentTheme);
                    var docx = Path.Combine(temp, stem + ".docx");
                    await _docxExport.ExportAsync(markdown, docx, settings, mermaid);
                    list.Add(new EmailAttachment(stem + ".docx", await File.ReadAllBytesAsync(docx, ct),
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
                }
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
        return list;
    }
}
