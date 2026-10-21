using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Core.Tests;

// Guards the menu de-clutter: read-only "Audit ..." reports live in one "Diagnostics" submenu per
// menu, never stacked among the real actions.
public class MenuDiagnosticsPolishTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string DesktopPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName, "MarkSmith.Desktop" }.Concat(parts).ToArray());
    }

    private static bool IsAudit(XElement item)
    {
        var name = (string?)item.Attribute(X + "Name") ?? "";
        var text = (string?)item.Attribute("Text") ?? "";
        return name.Contains("Audit", StringComparison.Ordinal) ||
               text.StartsWith("Audit", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(" audit", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Views", "MindMap", "MindMapGalaxyWindow.xaml")]
    [InlineData("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml")]
    [InlineData("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml")]
    [InlineData("Views", "SuiteHubView.xaml")]
    public void AuditItems_LiveInsideOneSortedDiagnosticsSubmenu(params string[] path)
    {
        var doc = XDocument.Load(DesktopPath(path));

        foreach (var flyout in doc.Descendants(Ui + "MenuFlyout"))
        {
            var direct = flyout.Elements(Ui + "MenuFlyoutItem").ToList();
            var looseAudits = direct.Count(IsAudit);
            Assert.True(looseAudits < 3,
                $"{string.Join('/', path)}: {looseAudits} audit items sit loose in a menu; fold them into the Diagnostics submenu.");

            var diagnostics = flyout.Elements(Ui + "MenuFlyoutSubItem")
                .Where(s => (string?)s.Attribute("Text") == "Diagnostics").ToList();
            Assert.True(diagnostics.Count <= 1, "A menu has more than one Diagnostics submenu.");
            if (diagnostics.Count == 0) continue;

            // Last in the menu, behind a separator, alphabetised so a long list stays scannable.
            var children = flyout.Elements().Where(e => e.Name.LocalName != "MenuFlyout.MenuFlyoutPresenterStyle").ToList();
            Assert.Same(diagnostics[0], children[^1]);
            Assert.Equal("MenuFlyoutSeparator", children[^2].Name.LocalName);

            var texts = diagnostics[0].Elements(Ui + "MenuFlyoutItem").Select(i => (string)i.Attribute("Text")!).ToList();
            Assert.True(texts.Count >= 3);
            Assert.Equal(texts.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), texts);
        }
    }

    [Fact]
    public void CodeBuiltAuditMenus_FoldBeforeShowing()
    {
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.True(CountOf(mainCs, "MenuDiagnostics.Fold(menu);") >= 3);
        Assert.True(CountOf(historyCs, "MenuDiagnostics.Fold(menu);") >= 1);
        Assert.True(File.Exists(DesktopPath("Services", "MenuDiagnostics.cs")));
    }

    [Fact]
    public void TextSearch_HasNoMojibake()
    {
        var core = Path.Combine(Path.GetDirectoryName(DesktopPath())!, "MarkSmith.Core", "Services", "TextSearch.cs");
        var source = File.ReadAllText(core);
        foreach (var signature in new[] { "Â·", "â€", "Ã—", "ðŸ", "�" })
            Assert.DoesNotContain(signature, source);
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
