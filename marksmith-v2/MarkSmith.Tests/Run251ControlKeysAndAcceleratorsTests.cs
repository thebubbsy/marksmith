using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Models;
using Xunit;

namespace MarkSmith.Tests;

// Run #251, checked live against an unmodified build first.
// - Diagram Studio's KeyboardAccelerators fire for keys pressed inside an open menu: with the Templates
//   menu open, Delete deleted the selected node (3 nodes / 2 connectors -> 2 / 0) and Ctrl+Z undid behind it.
// - MainWindow's root PreviewKeyDown tunnels keys from open menus: Ctrl+G inside a status-bar menu opened
//   Go to line on top of it, and the menu was left open behind the dialog.
// - Screen readers announced every Recent exports row as "MarkSmith.Models.HistoryEntry". List and combo
//   rows take their UIA name from the item's ToString(); ExportPreset and FolderPreset had the same gap.
public class Run251ControlKeysAndAcceleratorsTests
{
    private static string DesktopDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MarkSmith.Desktop");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative));

    private static string Method(string code, string name)
    {
        var decl = Regex.Match(code, $@"^\s*(private|protected|public|internal)\b[^\n;=]*\s{Regex.Escape(name)}\(", RegexOptions.Multiline);
        Assert.True(decl.Success, $"{name} not found");
        var open = code.IndexOf('{', decl.Index);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        throw new InvalidOperationException($"{name} is unbalanced");
    }

    [Fact]
    public void MermaidAccelerators_AllReturnFirstWhenAPopupOwnsTheKeys()
    {
        var code = Read(Path.Combine("Views", "Mermaid", "MermaidDiagramStudioControl.xaml.cs"));
        Assert.Contains("private bool MenuOwnsKeys() => PopupKeys.FocusIsOutside(RootGrid);", code);
        var names = Regex.Matches(code, @"private void (On\w+AcceleratorInvoked)\(KeyboardAccelerator sender")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.True(names.Length >= 20, $"found {names.Length} accelerator handlers");
        foreach (var name in names)
        {
            var body = Method(code, name).TrimStart('{', ' ', '\r', '\n');
            Assert.True(body.StartsWith("if (MenuOwnsKeys()) return;", StringComparison.Ordinal),
                $"{name} does not start with the popup guard");
        }
        // Every accelerator declared in the XAML goes to one of those guarded handlers.
        var xaml = Read(Path.Combine("Views", "Mermaid", "MermaidDiagramStudioControl.xaml"));
        var invoked = Regex.Matches(xaml, @"Invoked=""(\w+)""").Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.NotEmpty(invoked);
        foreach (var handler in invoked) Assert.Contains(handler, names);
    }

    [Fact]
    public void MainWindowRootChords_LeaveKeysPressedInsideAnOpenMenuAlone()
    {
        var body = Method(Read("MainWindow.xaml.cs"), "OnRootPreviewKeyDown");
        var guard = body.IndexOf("if (PopupKeys.FocusIsOutside(RootGrid)) return;", StringComparison.Ordinal);
        Assert.True(guard >= 0, "OnRootPreviewKeyDown has no popup guard");
        Assert.True(guard < body.IndexOf("e.Key", StringComparison.Ordinal));
        Assert.True(guard < body.IndexOf("ShowGoToLineDialogAsync()", StringComparison.Ordinal));
        // Guard only: it never marks a key handled, so the menu still gets every key.
        Assert.DoesNotContain("e.Handled = true;", body[..guard]);
    }

    [Fact]
    public void HistoryEntry_ReadsAsItsTitleAndDetails()
    {
        var entry = new HistoryEntry
        {
            DocumentTitle = "Quarterly report",
            Kind = "PDF",
            Detected = "Markdown",
            Theme = "GitHub Light",
            Timestamp = new DateTime(2026, 10, 25, 15, 2, 0),
        };
        var text = entry.ToString();
        Assert.StartsWith("Quarterly report, PDF · Markdown · GitHub Light", text);
        Assert.DoesNotContain("MarkSmith.", text);
        Assert.StartsWith("Untitled document, ", new HistoryEntry().ToString());
        Assert.StartsWith("notes.md, ", new HistoryEntry { SourceLabel = "notes.md" }.ToString());
    }

    [Fact]
    public void ExportPreset_ReadsAsItsName()
    {
        Assert.Equal("Board pack", new ExportPreset { Name = "Board pack" }.ToString());
    }

    [Fact]
    public void FolderPreset_ReadsAsItsNameAndPath()
    {
        var code = Read(Path.Combine("Services", "FolderIngestService.cs"));
        Assert.Contains("public override string ToString() => $\"{Name}, {Path}\";", code);
        // An override inside the record body, not the compiler's "FolderPreset { Name = ... }" debug text.
        Assert.Matches(@"public record FolderPreset\(string Name, string Description, string Path\)\s*\{", code);
    }
}
