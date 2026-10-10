namespace MarkSmith.Services;

/// <summary>
/// One key combination, in the same terms as a XAML <c>KeyboardAccelerator</c>:
/// <see cref="Modifiers"/> is "Control,Shift,Menu" style and <see cref="Key"/> a VirtualKey name.
/// </summary>
public readonly record struct KeyChord(string Modifiers, string Key)
{
    /// <summary>The individual keys a person presses, in order: e.g. ["Ctrl", "Shift", "P"].</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            var keys = new List<string>();
            foreach (var m in (Modifiers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                keys.Add(m switch
                {
                    "Control" => "Ctrl",
                    "Menu" => "Alt",
                    "Windows" => "Win",
                    _ => m,
                });
            }
            keys.Add(Key switch
            {
                "Up" => "↑",
                "Down" => "↓",
                "Left" => "←",
                "Right" => "→",
                "188" => ",",
                _ when Key.StartsWith("Number", StringComparison.Ordinal) && Key.Length == 7 => Key[6..],
                _ => Key,
            });
            return keys;
        }
    }

    /// <summary>"Ctrl+Shift+P": the form used in tooltips, menus and the command palette.</summary>
    public string Display => string.Join("+", Keys);
}

/// <summary>A row of the shortcut sheet: an action and every chord that triggers it.</summary>
/// <param name="Id">Stable key used by the command palette and tooltips to look the keys up.</param>
/// <param name="Section">Heading the row sits under on the F1 sheet.</param>
/// <param name="Action">What the shortcut does, in sentence case.</param>
/// <param name="Chords">Every chord that runs it. Empty only for a <paramref name="Gesture"/> row.</param>
/// <param name="Gesture">A non-key gesture shown instead of chords, e.g. "Ctrl + mouse wheel".</param>
/// <param name="EditorOnly">Works only while the Markdown editor has focus.</param>
/// <param name="Hidden">A developer/test shortcut: registered, but never shown to users.</param>
/// <param name="HandledInCode">Registered in code-behind rather than as a XAML accelerator
/// (Ctrl+, has no VirtualKey name, so WinUI's accelerator parser can't take it).</param>
public sealed record KeyboardShortcut(
    string Id,
    string Section,
    string Action,
    IReadOnlyList<KeyChord> Chords,
    string? Gesture = null,
    bool EditorOnly = false,
    bool Hidden = false,
    bool HandledInCode = false)
{
    /// <summary>The first chord's display text ("Ctrl+E"), or the gesture, or "".</summary>
    public string PrimaryDisplay => Chords.Count > 0 ? Chords[0].Display : Gesture ?? "";
}

/// <summary>
/// The single list of the desktop app's keyboard shortcuts. The F1 sheet is generated from it, the
/// command palette and tooltips read their key labels from it, and a test checks it against the
/// accelerators registered in MainWindow.xaml, so the three can no longer drift apart.
/// </summary>
public static class KeyboardShortcuts
{
    public const string FileSection = "File and export";
    public const string EditingSection = "Editing";
    public const string FormattingSection = "Formatting";
    public const string ViewSection = "View and tools";

    /// <summary>Sheet order.</summary>
    public static IReadOnlyList<string> Sections { get; } = new[] { FileSection, EditingSection, FormattingSection, ViewSection };

    private static KeyChord Ctrl(string key) => new("Control", key);
    private static KeyChord CtrlShift(string key) => new("Control,Shift", key);
    private static KeyChord CtrlAlt(string key) => new("Control,Menu", key);
    private static KeyChord Alt(string key) => new("Menu", key);
    private static KeyChord Bare(string key) => new("", key);

    public static IReadOnlyList<KeyboardShortcut> All { get; } = new KeyboardShortcut[]
    {
        new("file.open", FileSection, "Open a document: Markdown, Word, PDF, HTML or email (.eml, .msg)", new[] { Ctrl("O") }),
        new("file.save", FileSection, "Save (Word, PDF, HTML and email files save as a Markdown copy)", new[] { Ctrl("S") }),
        new("export.pdf", FileSection, "Export PDF", new[] { Ctrl("E"), CtrlShift("P") }),
        new("export.docx", FileSection, "Export Word (.docx)", new[] { CtrlShift("D"), CtrlShift("E") }),
        new("export.pptx", FileSection, "Export PowerPoint (.pptx)", new[] { CtrlShift("T") }),
        new("export.email", FileSection, "Email draft: open the document as a new Outlook message", new[] { CtrlShift("O") }),
        new("file.print", FileSection, "Print the rendered document", new[] { Ctrl("P") }),

        new("edit.undo", EditingSection, "Undo (kept across restarts)", new[] { Ctrl("Z") }, EditorOnly: true),
        new("edit.redo", EditingSection, "Redo", new[] { Ctrl("Y"), CtrlShift("Z") }, EditorOnly: true),
        new("edit.find", EditingSection, "Find in the document", new[] { Ctrl("F") }),
        new("edit.replace", EditingSection, "Find and replace", new[] { Ctrl("H") }),
        new("edit.findNext", EditingSection, "Next match of the last search", new[] { Bare("F3") }),
        new("edit.findPrevious", EditingSection, "Previous match of the last search", new[] { new KeyChord("Shift", "F3") }),
        new("edit.duplicateLine", EditingSection, "Duplicate the current line", new[] { Ctrl("D") }),
        new("edit.moveLineUp", EditingSection, "Move the current line up", new[] { Alt("Up") }),
        new("edit.moveLineDown", EditingSection, "Move the current line down", new[] { Alt("Down") }),
        new("edit.zoom", EditingSection, "Make the editor text bigger or smaller", Array.Empty<KeyChord>(), Gesture: "Ctrl+Mouse wheel"),

        new("format.bold", FormattingSection, "Bold (press again to remove)", new[] { Ctrl("B") }, EditorOnly: true),
        new("format.italic", FormattingSection, "Italic (press again to remove)", new[] { Ctrl("I") }, EditorOnly: true),
        new("format.h1", FormattingSection, "Heading 1", new[] { Ctrl("Number1") }, EditorOnly: true),
        new("format.h2", FormattingSection, "Heading 2", new[] { Ctrl("Number2") }, EditorOnly: true),
        new("format.h3", FormattingSection, "Heading 3", new[] { Ctrl("Number3") }, EditorOnly: true),
        new("format.h4", FormattingSection, "Heading 4", new[] { Ctrl("Number4") }, EditorOnly: true),

        new("app.palette", ViewSection, "Command palette: search every action", new[] { Ctrl("K") }),
        new("view.focus", ViewSection, "Focus mode: hide the side panels", new[] { Bare("F11") }),
        new("view.portalBlur", ViewSection, "Looking Glass: blur or sharpen the preview around the portal", new[] { CtrlAlt("X") }),
        new("studio.diagram", ViewSection, "Open Diagram Studio", new[] { CtrlShift("M") }),
        new("app.settings", ViewSection, "Open Settings", new[] { Ctrl("188") }, HandledInCode: true),
        new("app.shortcuts", ViewSection, "Show this list of shortcuts", new[] { Bare("F1") }),
        new("app.debug", ViewSection, "Debug mode: log every preview render", new[] { CtrlAlt("T") }),

        new("dev.resetLicense", ViewSection, "Reset the license to Free (developer)", new[] { new KeyChord("Control,Shift,Menu", "L") }, Hidden: true),
        new("dev.togglePro", ViewSection, "Toggle Pro dev mode (developer)", new[] { new KeyChord("Control,Shift,Menu", "P") }, Hidden: true),
    };

    /// <summary>The shortcut with this id. Throws for an unknown id, so a typo fails loudly.</summary>
    public static KeyboardShortcut Get(string id) =>
        All.FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException($"No keyboard shortcut '{id}'.");

    /// <summary>Display text for the id's first chord ("Ctrl+B"), or "" when it has none.</summary>
    public static string KeysFor(string id) => Get(id).PrimaryDisplay;

    /// <summary>"Bold (Ctrl+B)": a tooltip with the shortcut appended.</summary>
    public static string Tip(string label, string id)
    {
        var keys = KeysFor(id);
        return keys.Length == 0 ? label : $"{label} ({keys})";
    }

    /// <summary>The shortcuts users see on the F1 sheet, grouped in <see cref="Sections"/> order.</summary>
    public static IEnumerable<(string Section, IReadOnlyList<KeyboardShortcut> Rows)> Sheet() =>
        Sections.Select(section => (section,
            (IReadOnlyList<KeyboardShortcut>)All.Where(s => s.Section == section && !s.Hidden).ToList()));
}
