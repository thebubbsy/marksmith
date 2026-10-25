using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #252, checked live against an unmodified build first.
// - The main window's KeyboardAccelerators fire for keys pressed inside an open menu: with the word-count
//   menu open, F11 toggled focus mode behind it, and Ctrl+F opened the find bar, took focus and left the
//   menu stranded open.
// - The editor's own accelerators fire inside its text menu: Shift+F10 then Ctrl+B inserted "****" behind
//   the menu (and closed it), and Ctrl+1 turned the line into a heading.
// - Diagram Studio's Ctrl+[ / Ctrl+] z-order accelerators are built in code and missed run #251's guard.
public class Run252WindowAcceleratorsInPopupsTests
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

    private static string FirstStatement(string body) => body.TrimStart('{', ' ', '\r', '\n').Split('\n')[0].Trim();

    [Fact]
    public void MainWindowAccelerators_AllReturnFirstWhenAPopupOwnsTheKeys()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("private bool PopupOwnsKeys() => PopupKeys.FocusIsOutside(RootGrid);", code);
        var handlers = Regex.Matches(Read("MainWindow.xaml"), @"Invoked=""(\w+)""")
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.True(handlers.Length >= 20, $"found {handlers.Length} accelerator handlers");
        // The window-wide ones and the editor's own (undo / redo / format) are all covered.
        Assert.Contains("OnFocusModeAcceleratorInvoked", handlers);
        Assert.Contains("OnFindAcceleratorInvoked", handlers);
        Assert.Contains("OnFormatAcceleratorInvoked", handlers);
        Assert.Contains("OnUndoAcceleratorInvoked", handlers);
        foreach (var name in handlers.Where(h => h != "OnCommandPaletteInvoked"))
        {
            Assert.Equal("if (PopupOwnsKeys()) return;", FirstStatement(Method(code, name)));
        }
    }

    [Fact]
    public void CtrlK_StillClosesTheOpenCommandPalette()
    {
        // The palette is itself a popup, so its own Ctrl+K toggle must get past the guard.
        var body = Method(Read("MainWindow.xaml.cs"), "OnCommandPaletteInvoked");
        Assert.Equal("if (!Controls.CommandPalette.IsOpen && PopupOwnsKeys()) return;", FirstStatement(body));
    }

    [Fact]
    public void CtrlComma_SettingsAccelerator_IsGuardedToo()
    {
        var code = Read("MainWindow.xaml.cs");
        var start = code.IndexOf("settingsAccelerator.Invoked += (_, args) =>", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var lambda = code[start..code.IndexOf("RootGrid.KeyboardAccelerators.Add(settingsAccelerator);", start, StringComparison.Ordinal)];
        var guard = lambda.IndexOf("if (PopupOwnsKeys()) return;", StringComparison.Ordinal);
        Assert.True(guard >= 0, "Ctrl+, has no popup guard");
        Assert.True(guard < lambda.IndexOf("OnSettingsClick", StringComparison.Ordinal));
    }

    [Fact]
    public void Guards_NeverMarkTheKeyHandled()
    {
        // Returning without Handled leaves the key to the menu (Down, Esc and access keys keep working).
        var code = Read("MainWindow.xaml.cs");
        foreach (Match m in Regex.Matches(code, @"if \((!Controls\.CommandPalette\.IsOpen && )?PopupOwnsKeys\(\)\) return;"))
        {
            var line = code[code.LastIndexOf('\n', m.Index)..code.IndexOf('\n', m.Index)];
            Assert.DoesNotContain("Handled", line);
        }
    }

    [Fact]
    public void DiagramStudio_ZOrderAccelerators_ReturnFirstWhenAMenuOwnsTheKeys()
    {
        var code = Read(Path.Combine("Views", "Mermaid", "MermaidDiagramStudioControl.xaml.cs"));
        var start = code.IndexOf("// Ctrl+] / Ctrl+[ / Ctrl+Shift+] / Ctrl+Shift+[ z-ordering shortcuts", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var block = code[start..code.IndexOf("KeyboardAccelerators.Add(accel);", start, StringComparison.Ordinal)];
        var guard = block.IndexOf("if (MenuOwnsKeys()) return;", StringComparison.Ordinal);
        Assert.True(guard >= 0, "z-order accelerators have no popup guard");
        Assert.True(guard < block.IndexOf("BringToFront", StringComparison.Ordinal));
        // Every Invoked lambda in the control is guarded, not only the named handlers.
        foreach (Match m in Regex.Matches(code, @"accel\.Invoked \+= \(_, args\) =>\s*\{\s*(.+)"))
        {
            Assert.Equal("if (MenuOwnsKeys()) return;", m.Groups[1].Value.Trim());
        }
    }
}
