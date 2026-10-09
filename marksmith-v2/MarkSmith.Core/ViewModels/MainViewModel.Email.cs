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
    // Opening an .eml: "collapse" | "remove" | "keep" the quoted earlier messages.
    [ObservableProperty] private string _emailImportHistory = "collapse";
    [ObservableProperty] private bool _emailAttachPdf;
    [ObservableProperty] private bool _emailAttachDocx;
    // "auto" | "eml" | "msg": the file "Email draft" writes. Automatic picks what Outlook opens here.
    [ObservableProperty] private string _emailFormat = MailApps.Auto;

    /// <summary>Under the format choice: what Automatic picks on this PC and why, or what the
    /// fixed choice means. Re-read when the Email options come into sight (associations change
    /// outside the app).</summary>
    public string EmailFormatDescription => EmailFormat switch
    {
        MailApps.Eml => "Every mail app opens .eml files; Outlook opens them as a draft you can send.",
        MailApps.Msg => "Outlook's own format. Outlook opens it as a draft you can send; most other mail apps can't open it.",
        _ => MailApps.DescribeAutomatic(),
    };

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
            var title = string.IsNullOrWhiteSpace(markdown) ? null : EmailHtmlRenderer.TitleOf(markdown, _settingsService.Current, CurrentTheme);
            var subject = EmailComposer.BuildSubject(EmailSubjectTemplate, title, EmailSourceLabel(), markdown, DateTime.Now);
            return subject.Length == 0 ? "Subject: (taken from the document once it has text)" : $"Subject: {subject}";
        }
    }

    /// <summary>The preview follows the document, which changes on every keystroke; the view calls
    /// this when the Email options come into sight rather than recomputing it per key press.</summary>
    public void RefreshEmailSubjectPreview()
    {
        OnPropertyChanged(nameof(EmailSubjectPreview));
        OnPropertyChanged(nameof(EmailFormatDescription));
    }

    // Writes the backing fields on purpose: loading must not run the change handlers, which
    // would save the settings straight back and re-validate on every launch.
#pragma warning disable MVVMTK0034
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
        _emailImportHistory = settings.EmailImportHistory is "remove" or "keep" ? settings.EmailImportHistory : "collapse";
        _emailFormat = NormalizeEmailFormat(settings.EmailFormat);
    }
#pragma warning restore MVVMTK0034

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

    partial void OnEmailFormatChanged(string value)
    {
        var normalized = NormalizeEmailFormat(value);
        if (normalized != value) { EmailFormat = normalized; return; }
        _settingsService.Current.EmailFormat = value;
        OnPropertyChanged(nameof(EmailFormatDescription));
        SaveSettingsDebounced();
    }

    internal static string NormalizeEmailFormat(string? value) =>
        value?.Trim().ToLowerInvariant() is MailApps.Eml or MailApps.Msg ? value.Trim().ToLowerInvariant() : MailApps.Auto;

    partial void OnEmailImportHistoryChanged(string value)
    {
        _settingsService.Current.EmailImportHistory = value;
        SaveSettingsDebounced();
        // An email that is open and unedited re-imports at once, so the choice shows immediately.
        Plugins.PluginFileReader.InvalidateCache();
        if (!UsePasteSource && SourceImportKind == "Email" && HasInputFile) OnInputFilePathChanged(InputFilePath);
    }

    /// <summary>Status line for a draft the browser extension asked for (POST /api/email), so the
    /// app says what just appeared in Outlook and links the saved copy.</summary>
    public void AnnounceEmailFromApi(string subject, string path, bool opened)
    {
        var name = subject.Length > 0 ? $"\"{subject}\"" : Path.GetFileName(path);
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        AnnounceExport(opened
            ? DraftOpenedMessage($"Email draft {name} from the browser extension", ext)
            : $"Saved the email draft {name} from the browser extension, but Windows has no app set to open .{ext} files", path);
        StatusSeverity = opened ? StatusSeverity.Success : StatusSeverity.Warning;
    }

    /// <summary>"Email draft "Plan" opened in Outlook (classic)", naming the app Windows hands
    /// the file to; when Windows will ask which app to use, it says what to pick.</summary>
    internal static string DraftOpenedMessage(string what, string format)
    {
        var handler = MailApps.Lookup("." + format);
        return handler.Kind switch
        {
            MailAppKind.AskEachTime => $"{what} is ready. Windows is asking which app opens .{format} files: pick Outlook and tick \"Always\"",
            MailAppKind.None => $"{what} opened in your mail app",
            _ => $"{what} opened in {handler.Name}",
        };
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
            BaseDirectory = DocumentFolder,
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

    /// <summary>Set by the desktop app: puts a copied email on the Windows clipboard (CF_HTML plus
    /// plain text). Tests replace it to see what would be copied.</summary>
    public Action<EmailClipboardContent>? PutEmailOnClipboard { get; set; }

    /// <summary>"Copy as email": the document as the email body, on the clipboard, ready to paste
    /// into a compose window (a reply, a webmail tab, a thread that's already open). Pictures and
    /// diagrams come along: as files for classic Outlook, inline for everything else.</summary>
    public async Task CopyAsEmailAsync()
    {
        // Busy like any export: diagrams are drawn in the one shared export page, and two runs at
        // once would read each other's pictures.
        if (PutEmailOnClipboard is null || IsBusy) return;
        var (markdown, _) = ResolveSource();
        if (markdown is null) return;
        IsBusy = true;
        try
        {
            var settings = _settingsService.Current;
            List<byte[]?>? mermaid = null;
            if (EmailHtmlRenderer.HasMermaid(markdown) && Host is not null)
            {
                StatusText = "Drawing diagrams for the email…";
                var prepared = EmailHtmlRenderer.Prepare(markdown, settings, CurrentTheme);
                mermaid = await _mermaidHarvest.RenderMermaidPngsAsync(Host, prepared, settings, EmailPalette.From(CurrentTheme).DiagramTheme());
            }

            var doc = EmailComposer.Compose(new EmailComposeRequest
            {
                Markdown = markdown,
                SourceLabel = EmailSourceLabel(),
                BaseDirectory = DocumentFolder,
                MermaidPngs = mermaid,
            }, settings, CurrentTheme);

            var mode = EmailClipboard.ModeFor(MailApps.Lookup(".eml"));
            if (mode == ClipboardImageMode.File) EmailClipboard.Clean();
            var content = EmailClipboard.Build(doc, mode);
            PutEmailOnClipboard(content);

            var pictures = doc.InlineImages.Count;
            var what = pictures == 0 ? "" : pictures == 1 ? " with its picture" : $" with its {pictures} pictures";
            // Linked pictures only show where they're pasted on this PC: say so rather than let a
            // paste into a browser come out with broken images.
            var where = pictures > 0 && mode == ClipboardImageMode.File
                ? " Paste it into Outlook; a web mail app won't show the pictures (use Email draft for those)."
                : " Paste it into a new message or a reply.";
            StatusText = $"Copied as email{what}.{where}"
                + (doc.Notes.Count > 0 ? $" · {doc.Notes[0].TrimEnd('.')}" : "");
            StatusSeverity = doc.Notes.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            StatusText = $"Copy as email failed: {ex.Message}";
            StatusSeverity = StatusSeverity.Error;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Writes the document as an Outlook draft to the outbox and opens it in the default
    /// mail app (classic or new Outlook, or whatever handles .eml), ready to edit and Send.</summary>
    public Task CreateEmailDraftAsync() => ExportEmailAsync(openInMailApp: true);

    /// <summary>Saves the document as an .eml draft next to the other exports.</summary>
    public Task SaveEmailAsync() => ExportEmailAsync(openInMailApp: false, MailApps.Eml);

    /// <summary>Saves the document as an Outlook message (.msg) draft next to the other exports.</summary>
    public Task SaveOutlookMessageAsync() => ExportEmailAsync(openInMailApp: false, MailApps.Msg);

    private async Task ExportEmailAsync(bool openInMailApp, string? format = null)
    {
        var fmt = format ?? MailApps.Resolve(EmailFormat);
        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync(openInMailApp ? "an email draft" : fmt == MailApps.Msg ? "an Outlook message" : "an email", async ct =>
        {
            var settings = _settingsService.Current;
            var outPath = openInMailApp ? EmailOutbox.PathFor(sourceLabel, fmt) : PrepareOutputPath(sourceLabel, fmt);
            if (openInMailApp) EmailOutbox.Clean();

            var palette = EmailPalette.From(CurrentTheme);
            List<byte[]?>? mermaid = null;
            if (EmailHtmlRenderer.HasMermaid(markdown) && Host is not null)
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
                BaseDirectory = DocumentFolder,
                MermaidPngs = mermaid,
                Attachments = attachments,
            }, settings, CurrentTheme);
            notes.InsertRange(0, doc.Notes);
            if (doc.PayloadBytes > LargeEmailBytes)
                notes.Add($"This email is {doc.PayloadBytes / (1024.0 * 1024):0.#} MB; many mail servers refuse messages over 20 MB.");

            ct.ThrowIfCancellationRequested();
            if (fmt == MailApps.Msg) MsgWriter.Write(doc, outPath);
            else EmlWriter.Write(doc, outPath);
            LastOutputPath = outPath;
            if (!UsePasteSource) TrackRecent(InputFilePath);
            RecordExport(openInMailApp ? "Email draft" : fmt == MailApps.Msg ? "Outlook message" : "Email", outPath, markdown);

            var subject = doc.Subject.Length > 0 ? $"\"{doc.Subject}\"" : Path.GetFileName(outPath);
            // Caveats go before the folder: the status bar trims from the end, and a long path
            // used to push "Left out "bob"" out of sight.
            var caveat = notes.Count == 0 ? ""
                : $" · {notes[0].TrimEnd('.')}" + (notes.Count > 1 ? $" (+{notes.Count - 1} more)" : "");
            if (caveat.Length > 0) StatusSeverityOverride = StatusSeverity.Warning;
            string message;
            if (!openInMailApp)
            {
                var kind = fmt == MailApps.Msg ? "Outlook message" : "Email";
                RaiseExportCompleted(kind, outPath);
                message = $"{kind} saved: {Path.GetFileName(outPath)}{caveat} · in {Path.GetDirectoryName(outPath)}";
            }
            else if (EmailOutbox.Open(outPath))
            {
                message = DraftOpenedMessage($"Email draft {subject}", fmt) + caveat;
            }
            else
            {
                message = $"Saved the email draft {subject}, but Windows has no app set to open .{fmt} files. Pick Outlook under Settings > Apps > Default apps";
                StatusSeverityOverride = StatusSeverity.Warning;
            }
            AnnounceExport(message, outPath);
        });

        // Only a run that finished: a failure keeps its Error, whatever caveat came before it.
        if (StatusSeverityOverride is { } severity && StatusSeverity == StatusSeverity.Success)
            StatusSeverity = severity;
        StatusSeverityOverride = null;
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
                    if (EmailHtmlRenderer.HasMermaid(markdown) && Host is not null)
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
