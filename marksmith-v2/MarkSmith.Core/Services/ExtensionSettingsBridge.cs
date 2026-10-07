using System.Reflection;
using System.Text.Json;
using MarkSmith.Models;

namespace MarkSmith.Services;

/// <summary>
/// The app's own options, as the browser extension sees them (GET/POST /api/extension/settings).
/// The extension draws its "Live app settings" page from <see cref="Describe"/>, so every option
/// listed here shows up there with the app's own label, help text and choices; adding a row here
/// is all it takes to make a new desktop option controllable from the browser.
///
/// Writes go through the view model's property setters on the UI thread, exactly as a click in the
/// desktop app would: the panels update at once, the change is saved, the preview re-renders and
/// the view model's own licence gates still apply. Only user-facing options are listed — never a
/// password, token, licence detail, or a folder/file path (a path from a browser could point the
/// watchers at any folder on the PC).
/// </summary>
public sealed class ExtensionSettingsBridge
{
    public sealed record Choice(string Value, string Label);

    /// <param name="Key">camelCase id the extension sends back.</param>
    /// <param name="Property">The view-model property it reads and writes.</param>
    /// <param name="Kind">"toggle", "number", "choice" or "text".</param>
    /// <param name="Pro">"automation" (refused on Free when switching on) or "info" (badge only).</param>
    public sealed record Field(
        string Key, string Property, string Label, string Description, string Kind,
        IReadOnlyList<Choice>? Choices = null, int? Min = null, int? Max = null,
        string? Pro = null, string? Placeholder = null, int MaxLength = 200,
        Func<object?, object?>? Read = null, Func<object?, object?>? Write = null);

    public sealed record Group(string Id, string Title, string Description, IReadOnlyList<Field> Fields);

    private readonly object _target;
    private readonly Func<IReadOnlyList<string>> _themeNames;
    private readonly Func<Func<Task>, Task> _onUiThread;
    private readonly Func<LicenseService> _license;

    public ExtensionSettingsBridge(object target, Func<IReadOnlyList<string>> themeNames,
        Func<Func<Task>, Task>? onUiThread = null, Func<LicenseService>? license = null)
    {
        _target = target;
        _themeNames = themeNames;
        _onUiThread = onUiThread ?? (work => work());
        _license = license ?? (() => AppServices.License);
    }

    private static readonly Choice[] Formats =
    {
        new("pdf", "PDF (.pdf)"), new("docx", "Word document (.docx)"),
        new("pptx", "PowerPoint (.pptx)"), new("epub", "EPUB e-book (.epub)"),
    };

    /// <summary>The groups, in the order of the desktop app's Style &amp; Export panel, then the
    /// Settings options the extension used to override per capture, then Automation.</summary>
    public IReadOnlyList<Group> Groups() => new[]
    {
        new Group("appearance", "Appearance", "The look of the preview and every export.", new[]
        {
            new Field("theme", "SelectedThemeName", "Theme", "Colours and type for the preview, PDF and Word exports.", "choice",
                _themeNames().Select(t => new Choice(t, t)).ToArray()),
            new Field("themeLightInfluence", "ThemeLightInfluence", "Light theme influence",
                "A light centre panel with the theme's colours on the margins; text colours adjust to stay readable.", "toggle"),
            new Field("fontPreset", "FontPreset", "Fallback font", "Typeface used when the document's own font field is empty.", "choice",
                new[] { "System", "Serif", "Sans-Serif", "Monospace", "Dyslexic-friendly" }.Select(f => new Choice(f, f)).ToArray()),
        }),
        new Group("layout", "Layout & PDF", "Page size, contents and PDF page furniture.", new[]
        {
            new Field("a4FixedWidth", "A4FixedWidth", "Lock to A4 width",
                "Pins the page to A4 width (794 px), so PDFs come out true A4 and Word uses A4 paper. Off: the width below sets the page, and Word uses Letter.", "toggle"),
            new Field("contentWidth", "ContentWidth", "Page width (px)", "400–2400 px. Sets the preview and PDF page width while the A4 lock is off.", "number", Min: 400, Max: 2400),
            new Field("unlimitedHeight", "UnlimitedHeight", "Single continuous page",
                "PDF only: one long page with no page breaks. Word has no page-less print mode, so a DOCX opens in Web Layout instead.", "toggle"),
            new Field("includeToc", "IncludeToc", "Table of contents", "A linked contents list built from the document's headings, at the top of the preview and every export.", "toggle"),
            new Field("showWordCount", "ShowWordCount", "Reading time in preview", "Show the word count and reading-time pill at the top of the live preview.", "toggle"),
            new Field("pdfPageNumberPosition", "PdfPageNumberPosition", "PDF page numbers", "Where the page number goes on every page of a PDF export.", "choice",
                new[] { new Choice("None", "None"), new Choice("BottomRight", "Bottom right"), new Choice("BottomCenter", "Bottom center"), new Choice("TopRight", "Top right") }),
            new Field("pdfHeaderTemplate", "PdfHeaderTemplate", "PDF header", "Repeated at the top of every page. Tokens: {title}, {page}, {pages}, {date}.", "text", Placeholder: "e.g. {title} — {date}"),
            new Field("pdfFooterTemplate", "PdfFooterTemplate", "PDF footer", "Repeated at the bottom of every page. Same tokens as the header.", "text", Placeholder: "e.g. Page {page} of {pages}"),
        }),
        new Group("word", "Word export", "How .docx files are built.", new[]
        {
            new Field("pageBorder", "PageBorder", "Page border", "A decorative frame around every page.", "toggle"),
            new Field("trackChanges", "TrackChanges", "Track changes", "The document opens with Word's revision tracking on, so every later edit shows.", "toggle"),
            new Field("mermaidDocxMode", "MermaidDocxMode", "Diagrams", "ShapeForge rebuilds Mermaid diagrams from Word's own shapes, every box and arrow editable. Anything it can't parse falls back to a picture.", "choice",
                new[] { new Choice("0", "Picture of the diagram"), new Choice("1", "ShapeForge™ editable shapes") },
                Read: v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), Write: v => int.Parse((string)v!, System.Globalization.CultureInfo.InvariantCulture)),
            new Field("smartConnectors", "SmartConnectors", "Glued connectors", "Lines stay attached to their shapes when you drag them in Word.", "toggle"),
            new Field("connectorArrowhead", "ConnectorArrowhead", "Connector arrowheads", "Used for connectors whose diagram source doesn't pick one.", "choice",
                new[] { new Choice("default", "Default (from the diagram)"), new Choice("triangle", "Triangle"), new Choice("open", "Open arrow"), new Choice("diamond", "Diamond"),
                        new Choice("oval", "Oval"), new Choice("stealth", "Stealth"), new Choice("none", "None") }),
        }),
        new Group("email", "Email", "Outlook drafts. Every email feature is free.", new[]
        {
            new Field("emailTo", "EmailTo", "To", "Filled in on every draft. Separate addresses with ; or ,", "text", Placeholder: "name@company.com; team@company.com", MaxLength: 1000),
            new Field("emailCc", "EmailCc", "Cc", "Copied on every draft.", "text", Placeholder: "Optional", MaxLength: 1000),
            new Field("emailSubjectTemplate", "EmailSubjectTemplate", "Subject", "{title} is the document's opening heading; {source} its file name; {date} today.", "text", Placeholder: "{title}"),
            new Field("emailRepeatTitleInBody", "EmailRepeatTitleInBody", "Keep the title in the message", "The opening heading becomes the subject. Turn this on to repeat it as a heading at the top of the message too.", "toggle"),
            new Field("emailAttachPdf", "EmailAttachPdf", "Attach a PDF copy", "The same document as a PDF, for readers who want to print or file it.", "toggle"),
            new Field("emailAttachDocx", "EmailAttachDocx", "Attach a Word copy", "An editable .docx of the document. Attaching Word files is a Pro feature; on the free plan the email still goes, without the copy.", "toggle", Pro: "info"),
        }),
        new Group("content", "Content & cleanup", "What happens to AI text on the way in.", new[]
        {
            new Field("mermaidEnabled", "MermaidEnabled", "Render Mermaid diagrams", "Off: Mermaid code blocks stay as plain code in the preview and every export.", "toggle"),
            new Field("normalizeLlm", "NormalizeLlm", "Fix AI formatting quirks", "ChatGPT's LaTeX and citation pips, Gemini's pseudo-headings, Claude's leftover tags.", "toggle"),
            new Field("showAttribution", "ShowAttribution", "Source attribution", "A strip at the top naming the AI the text came from and how many fixes were applied. Only shown when the source is recognised.", "toggle"),
        }),
        new Group("formatting", "Formatting & text", "Personalise the structure of every document.", new[]
        {
            new Field("noEmoji", "NoEmoji", "No emoji", "Strips every emoji, whatever the input, from the preview and every export.", "toggle"),
            new Field("dashMode", "DashMode", "Em dashes (—)", "Swap AI-style em dashes everywhere except code blocks.", "choice",
                new[] { new Choice("0", "Keep as-is"), new Choice("1", "Replace with hyphen (-)"), new Choice("2", "Replace with spaced ( - )"), new Choice("3", "Custom…") },
                Read: v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), Write: v => int.Parse((string)v!, System.Globalization.CultureInfo.InvariantCulture)),
            new Field("dashCustom", "DashCustom", "Custom em dash", "Used when Em dashes is Custom.", "text", Placeholder: "Replace each em dash with…", MaxLength: 20),
            new Field("headingShift", "HeadingShift", "Heading level shift", "Promote (−) or demote (+) every heading: +1 turns # into ##. Levels stay within 1–6.", "number", Min: -5, Max: 5),
            new Field("boldMode", "BoldMode", "Bold text", "Keep bold, make it plain text, or turn it into italics.", "choice",
                new[] { new Choice("0", "Keep"), new Choice("1", "Plain text"), new Choice("2", "Make italic") },
                Read: v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), Write: v => int.Parse((string)v!, System.Globalization.CultureInfo.InvariantCulture)),
            new Field("italicMode", "ItalicMode", "Italic text", "Keep italics or make them plain text.", "choice",
                new[] { new Choice("0", "Keep"), new Choice("1", "Plain text") },
                Read: v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), Write: v => int.Parse((string)v!, System.Globalization.CultureInfo.InvariantCulture)),
        }),
        new Group("branding", "Branding", "A cover page and your typeface, for client-ready deliverables.", new[]
        {
            new Field("brandCoverPage", "BrandCoverPage", "Cover page", "A Word title page with the document's title, your logo and today's date.", "toggle", Pro: "info"),
            new Field("brandFontFamily", "BrandFontFamily", "Document font", "Any installed typeface, used for the body in the preview and every export. Blank: the fallback font.", "text", Placeholder: "e.g. Aptos", Pro: "info"),
            new Field("authorName", "AuthorName", "Author", "Written into the file's properties as its author, in Word, PDF and PowerPoint.", "text", Placeholder: "Your name"),
        }),
        new Group("export", "Export", "Defaults for automatic exports.", new[]
        {
            new Field("targetFormat", "TargetFormat", "Default output format", "What the browser extension, clipboard and folder automation, batch convert and the local API export to.", "choice", Formats),
            new Field("fileNameTemplate", "FileNameTemplate", "Export file name", "Template for generated file names. Tokens: {title}, {date}, {time}, {format}.", "text", Placeholder: "{title}"),
        }),
        new Group("automation", "Automation", "Hands-free conversion. Automation is a Pro feature.", new[]
        {
            new Field("autoClipboardIngest", "AutoClipboardIngest", "Watch the clipboard", "Copy a reply in ChatGPT, Gemini or Claude and it lands in MarkSmith, detected and cleaned.", "toggle", Pro: "automation"),
            new Field("autoConvertIngests", "AutoConvertIngests", "Export every ingest", "Each chat that arrives (clipboard, API or this extension) is exported straight away.", "toggle", Pro: "automation"),
            new Field("appendToRunningDoc", "AppendToRunningDoc", "Append to a running document", "When the default format is Word, add each export as a dated section of one growing .docx instead of a new file.", "toggle", Pro: "automation"),
            new Field("watchFolderAutoConvert", "WatchFolderAutoConvert", "Export watched files", "Each new file in the watched folder is exported to your output folder. Pick the folder in the app.", "toggle", Pro: "automation"),
            new Field("minimizeToTray", "MinimizeToTray", "Keep running in the tray", "Closing the window hides MarkSmith to the notification area, so the watchers and API keep working.", "toggle"),
        }),
    };

    private IEnumerable<Field> AllFields() => Groups().SelectMany(g => g.Fields);

    /// <summary>The page the extension draws: every group with each option's current value, plus
    /// what the licence allows so Pro rows can say so up front.</summary>
    public async Task<object> DescribeAsync()
    {
        object? result = null;
        await _onUiThread(() =>
        {
            var license = _license();
            result = new
            {
                license = new
                {
                    edition = license.State.Edition.ToString(),
                    canAutomate = license.CanAutomate,
                    canExportDocx = license.CanExportDocx,
                },
                groups = Groups().Select(g => new
                {
                    id = g.Id,
                    title = g.Title,
                    description = g.Description,
                    fields = g.Fields.Select(f => new
                    {
                        key = f.Key,
                        label = f.Label,
                        description = f.Description,
                        kind = f.Kind,
                        choices = f.Choices,
                        min = f.Min,
                        max = f.Max,
                        pro = f.Pro,
                        placeholder = f.Placeholder,
                        maxLength = f.MaxLength,
                        locked = f.Pro == "automation" && !license.CanAutomate,
                        value = ReadValue(f),
                    }),
                }),
            };
            return Task.CompletedTask;
        });
        return result!;
    }

    public sealed record Rejection(string Key, string Reason);
    public sealed record ApplyResult(List<string> Applied, List<Rejection> Rejected);

    /// <summary>Applies <paramref name="changes"/> (key → JSON value). Each key is checked against
    /// the list above (type, range, choices, licence) and refused with a readable reason otherwise;
    /// the rest are set through the view model on the UI thread.</summary>
    public async Task<ApplyResult> ApplyAsync(IReadOnlyDictionary<string, JsonElement> changes)
    {
        var applied = new List<string>();
        var rejected = new List<Rejection>();
        var byKey = AllFields().ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        var pending = new List<(Field Field, object Value)>();

        foreach (var (key, json) in changes)
        {
            if (!byKey.TryGetValue(key, out var field)) { rejected.Add(new(key, "Not a setting the extension can change.")); continue; }
            var (ok, value, reason) = Parse(field, json);
            if (!ok) { rejected.Add(new(field.Key, reason!)); continue; }
            if (field.Pro == "automation" && value is true && !_license().CanAutomate)
            {
                rejected.Add(new(field.Key, $"{field.Label} is a MarkSmith Pro feature. Start the trial or upgrade in the app."));
                continue;
            }
            pending.Add((field, value!));
        }

        await _onUiThread(() =>
        {
            foreach (var (field, value) in pending)
            {
                try
                {
                    var prop = Property(field);
                    var raw = field.Write is { } w ? w(value) : value;
                    prop.SetValue(_target, raw);
                    // The view model can refuse a value (a licence gate, a clamp): report what stuck.
                    if (Equals(Normalize(ReadValue(field)), Normalize(value))) applied.Add(field.Key);
                    else rejected.Add(new(field.Key, $"MarkSmith kept its own value for {field.Label}."));
                }
                catch (Exception ex)
                {
                    rejected.Add(new(field.Key, ex.InnerException?.Message ?? ex.Message));
                }
            }
            return Task.CompletedTask;
        });
        return new ApplyResult(applied, rejected);
    }

    private static object? Normalize(object? v) => v is string s ? s : v;

    private (bool Ok, object? Value, string? Reason) Parse(Field f, JsonElement json)
    {
        switch (f.Kind)
        {
            case "toggle":
                return json.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (true, json.GetBoolean(), null)
                    : (false, null, $"{f.Label} must be on or off.");
            case "number":
                if (json.ValueKind != JsonValueKind.Number || !json.TryGetInt32(out var n))
                    return (false, null, $"{f.Label} must be a whole number.");
                if ((f.Min is { } min && n < min) || (f.Max is { } max && n > max))
                    return (false, null, $"{f.Label} must be between {f.Min} and {f.Max}.");
                return (true, n, null);
            case "choice":
            {
                var s = json.ValueKind switch
                {
                    JsonValueKind.String => json.GetString(),
                    JsonValueKind.Number => json.GetRawText(),
                    _ => null,
                };
                var match = s is null ? null : f.Choices?.FirstOrDefault(c => string.Equals(c.Value, s, StringComparison.OrdinalIgnoreCase));
                return match is null
                    ? (false, null, $"\"{s}\" isn't one of the choices for {f.Label}.")
                    : (true, match.Value, null);
            }
            case "text":
            {
                if (json.ValueKind != JsonValueKind.String) return (false, null, $"{f.Label} must be text.");
                var s = json.GetString() ?? "";
                if (s.Length > f.MaxLength) return (false, null, $"{f.Label} is limited to {f.MaxLength} characters.");
                if (s.Any(c => char.IsControl(c) && c != '\n')) return (false, null, $"{f.Label} can't contain control characters.");
                return (true, s, null);
            }
        }
        return (false, null, "Unknown setting type.");
    }

    private object? ReadValue(Field f)
    {
        var raw = Property(f).GetValue(_target);
        return f.Read is { } r ? r(raw) : raw;
    }

    private PropertyInfo Property(Field f) =>
        _target.GetType().GetProperty(f.Property, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{_target.GetType().Name} has no {f.Property}.");
}
