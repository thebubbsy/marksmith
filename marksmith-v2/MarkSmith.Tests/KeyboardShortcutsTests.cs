using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// KeyboardShortcuts is the one list the F1 sheet, the command palette and the tooltips read. These
// tests hold it to what MainWindow.xaml actually registers: run #21b found the hand-written sheet
// missing six working shortcuts and the tooltips promising a Ctrl+B that did nothing.
public class KeyboardShortcutsTests
{
    private static string MainWindowXaml([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "MarkSmith.Desktop", "MainWindow.xaml");

    private static List<(string Modifiers, string Key, string Scope)> XamlAccelerators()
    {
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(MainWindowXaml());
        return doc.Descendants(ns + "KeyboardAccelerator")
            .Select(a =>
            {
                var owner = a.Parent?.Parent;
                var scope = (string?)owner?.Attribute(x + "Name") ?? owner?.Name.LocalName ?? "";
                return (Normalise((string?)a.Attribute("Modifiers") ?? ""), (string)a.Attribute("Key")!, scope);
            })
            .ToList();
    }

    private static string Normalise(string modifiers) =>
        string.Join(",", modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).OrderBy(m => m));

    [Fact]
    public void Every_Catalog_Chord_Is_Registered_In_MainWindow()
    {
        var registered = XamlAccelerators();
        foreach (var shortcut in KeyboardShortcuts.All.Where(s => !s.HandledInCode))
        {
            foreach (var chord in shortcut.Chords)
            {
                var match = registered.Where(r => r.Modifiers == Normalise(chord.Modifiers) && r.Key == chord.Key).ToList();
                Assert.True(match.Count > 0, $"{shortcut.Id}: {chord.Display} is listed but no KeyboardAccelerator registers it.");
                if (shortcut.EditorOnly)
                    Assert.Contains(match, r => r.Scope == "PasteTextBox");
                else
                    Assert.Contains(match, r => r.Scope == "RootGrid");
            }
        }
    }

    [Fact]
    public void Every_Registered_Accelerator_Is_In_The_Catalog()
    {
        var listed = KeyboardShortcuts.All.SelectMany(s => s.Chords)
            .Select(c => (Normalise(c.Modifiers), c.Key)).ToHashSet();
        foreach (var (modifiers, key, scope) in XamlAccelerators().Where(a => a.Scope is "RootGrid" or "PasteTextBox"))
            Assert.True(listed.Contains((modifiers, key)), $"{modifiers}+{key} on {scope} is missing from KeyboardShortcuts.");
    }

    [Fact]
    public void No_Chord_Is_Used_Twice()
    {
        var chords = KeyboardShortcuts.All.SelectMany(s => s.Chords.Select(c => (Normalise(c.Modifiers), c.Key, s.Id))).ToList();
        var duplicates = chords.GroupBy(c => (c.Item1, c.Key)).Where(g => g.Count() > 1).Select(g => string.Join(" & ", g.Select(c => c.Id)));
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Ids_Are_Unique_And_Sections_Are_Known()
    {
        Assert.Equal(KeyboardShortcuts.All.Count, KeyboardShortcuts.All.Select(s => s.Id).Distinct().Count());
        Assert.All(KeyboardShortcuts.All, s => Assert.Contains(s.Section, KeyboardShortcuts.Sections));
        Assert.All(KeyboardShortcuts.All, s => Assert.True(s.Chords.Count > 0 || s.Gesture is not null, s.Id));
    }

    [Fact]
    public void The_Sheet_Lists_What_Run_21b_Found_Missing_And_Hides_Developer_Commands()
    {
        var shown = KeyboardShortcuts.Sheet().SelectMany(s => s.Rows).SelectMany(r => r.Chords).Select(c => c.Display).ToHashSet();
        foreach (var keys in new[] { "Ctrl+K", "Ctrl+Shift+M", "Alt+↑", "Alt+↓", "F11", "F1", "Ctrl+,", "Ctrl+B", "Ctrl+I", "Ctrl+1" })
            Assert.Contains(keys, shown);
        Assert.DoesNotContain("Ctrl+Shift+Alt+L", shown);
        Assert.DoesNotContain("Ctrl+Shift+Alt+P", shown);
    }

    [Fact]
    public void Display_Text_Uses_Short_Key_Names()
    {
        Assert.Equal("Ctrl+Shift+P", new KeyChord("Control,Shift", "P").Display);
        Assert.Equal("Ctrl+Alt+X", new KeyChord("Control,Menu", "X").Display);
        Assert.Equal("Alt+↑", new KeyChord("Menu", "Up").Display);
        Assert.Equal("Ctrl+2", new KeyChord("Control", "Number2").Display);
        Assert.Equal("Ctrl+,", KeyboardShortcuts.KeysFor("app.settings"));
        Assert.Equal("F11", KeyboardShortcuts.KeysFor("view.focus"));
        Assert.Equal("Bold (Ctrl+B)", KeyboardShortcuts.Tip("Bold", "format.bold"));
        Assert.Equal(new[] { "Ctrl", "Shift", "O" }, new KeyChord("Control,Shift", "O").Keys);
    }

    [Fact]
    public void Unknown_Ids_Fail_Loudly()
    {
        Assert.Throws<KeyNotFoundException>(() => KeyboardShortcuts.KeysFor("format.underline"));
    }
}
