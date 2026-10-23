using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #223: every focusable control whose tooltip says "Right-click for options" opens that menu
// from Shift+F10 / the Menu key too (RightTapped is raised only by mouse, pen and touch), the
// keyboard-opened menu sits under the focused control instead of the top-left corner of the
// card or bar it is measured against, and the tooltips say so.
public class Run223KeyboardContextMenuTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> Focusable = new(StringComparer.Ordinal)
    {
        "Button", "ToggleButton", "HyperlinkButton", "SplitButton", "DropDownButton",
    };

    // Shape Studio, SmartArt Studio and Mind Map are being reworked in another session's
    // uncommitted edit; they are bridged when that lands. The splash screen has no menus to reach.
    private static readonly string[] Excluded = { "ShapeStudio", "SmartArtStudio", "MindMap", "SplashWindow" };

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

    public static IEnumerable<object[]> Surfaces()
    {
        var root = DesktopDir();
        foreach (var xaml in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .Where(p => !Excluded.Any(p.Contains))
                     .Where(p => File.ReadAllText(p).Contains("RightTapped=\""))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return new object[] { Path.GetRelativePath(root, xaml).Replace('\\', '/') };
        }
    }

    private static IEnumerable<(string Name, string Handler, string? ToolTip)> RightTappedControls(string relativeXaml)
    {
        var doc = XDocument.Load(Path.Combine(DesktopDir(), relativeXaml));
        foreach (var e in doc.Descendants())
        {
            if (!Focusable.Contains(e.Name.LocalName)) continue;
            var handler = (string?)e.Attribute("RightTapped");
            var name = (string?)e.Attribute(X + "Name");
            if (handler is null || name is null) continue;
            yield return (name, handler, (string?)e.Attribute("ToolTipService.ToolTip"));
        }
    }

    [Fact]
    public void Surfaces_AreFound()
    {
        var surfaces = Surfaces().Select(s => (string)s[0]).ToList();
        Assert.Contains("MainWindow.xaml", surfaces);
        Assert.Contains("Views/SuiteHubView.xaml", surfaces);
        Assert.Contains("Views/History/HistoryWindow.xaml", surfaces);
        Assert.True(surfaces.Count >= 12, string.Join(", ", surfaces));
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void EveryFocusableRightTapMenu_IsBridgedToTheKeyboard(string relativeXaml)
    {
        var code = File.ReadAllText(Path.Combine(DesktopDir(), relativeXaml + ".cs"));
        var missing = RightTappedControls(relativeXaml)
            .Where(c => !Regex.IsMatch(code, $@"\(\s*{c.Name}\s*,\s*{c.Handler}\s*\)"))
            .Select(c => $"{c.Name} → {c.Handler}")
            .ToList();
        Assert.True(missing.Count == 0, $"{relativeXaml}: not reachable from Shift+F10 / Menu key: {string.Join("; ", missing)}");
        if (RightTappedControls(relativeXaml).Any())
        {
            Assert.Matches(@"KeyboardContextMenu\.(Bridge|BridgeAll)\(", code);
        }
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void BridgedTooltips_MentionShiftF10(string relativeXaml)
    {
        var stale = RightTappedControls(relativeXaml)
            .Where(c => c.ToolTip is not null && c.ToolTip.Contains("Right-click for options"))
            .Select(c => c.Name)
            .ToList();
        Assert.True(stale.Count == 0, $"{relativeXaml}: tooltips still say only right-click: {string.Join(", ", stale)}");
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void RightTapHandlers_DoNotReadThePointerDirectly(string relativeXaml)
    {
        // A keyboard request carries synthetic args with no pointer position; GetPosition on them
        // put the menu at the top-left of the whole card/bar. PointIn anchors it under the control.
        var code = File.ReadAllText(Path.Combine(DesktopDir(), relativeXaml + ".cs"));
        var offenders = new List<string>();
        foreach (Match m in Regex.Matches(code, @"void (\w+)\(\s*object\??\s+\w+\s*,\s*(?:[\w.]+\.)?RightTappedRoutedEventArgs\s+(\w+)\s*\)"))
        {
            var open = code.IndexOf('{', m.Index + m.Length);
            if (open < 0) continue;
            int depth = 0, i = open;
            for (; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) break;
            }
            var body = code.Substring(open, i - open);
            if (Regex.IsMatch(body, $@"\b{m.Groups[2].Value}\.GetPosition\(")) offenders.Add(m.Groups[1].Value);
        }
        Assert.True(offenders.Count == 0, $"{relativeXaml}: {string.Join(", ", offenders)}");
    }

    // These surfaces also have a surface-wide Shift+F10 menu that sees the key after (or, for a
    // PreviewKeyDown, before) the focused control. Without the check both menus opened at once and
    // the second dismissed the first, so nothing appeared (found live on the tour's Skip button).
    [Theory]
    [InlineData("Views/WelcomeTour.xaml.cs")]
    [InlineData("Views/SuiteHubView.xaml.cs")]
    [InlineData("Controls/CommandPalette.xaml.cs")]
    [InlineData("Controls/ExtensionHintBar.xaml.cs")]
    [InlineData("Controls/ExtensionTip.xaml.cs")]
    [InlineData("Views/Ocr/OcrInspectionStudioWindow.xaml.cs")]
    public void SurfaceWideShiftF10Menus_YieldToTheFocusedControlsOwn(string relativeCode)
    {
        var code = File.ReadAllText(Path.Combine(DesktopDir(), relativeCode));
        var key = code.IndexOf("VirtualKey.F10", StringComparison.Ordinal);
        // Run #238: the OCR studio tests the key with the shared helper instead.
        if (key < 0) key = code.IndexOf("KeyboardContextMenu.IsContextMenuKey(e.Key)", StringComparison.Ordinal);
        Assert.True(key > 0, relativeCode);
        var guard = code.IndexOf("KeyboardContextMenu.IsFromBridgedControl(e)) return;", key, StringComparison.Ordinal);
        Assert.True(guard > key && guard - key < 400, $"{relativeCode}: the Shift+F10 branch must check IsFromBridgedControl first");
    }

    [Fact]
    public void PointIn_UsesThePointerForClicks_AndTheFocusedControlForKeys()
    {
        var src = File.ReadAllText(Path.Combine(DesktopDir(), "Services", "KeyboardContextMenu.cs"));
        Assert.Contains("public static Point PointIn(RightTappedRoutedEventArgs e, UIElement? relativeTo)", src);
        Assert.Contains("if (!IsKeyboardInvocation) return e.GetPosition(relativeTo);", src);
        Assert.Contains("source.TransformToVisual(relativeTo).TransformPoint(new Point(0, source.ActualHeight))", src);
        // The source is cleared after every invocation so a later mouse click never reuses it.
        Assert.Contains("finally { IsKeyboardInvocation = false; KeyboardSource = null; }", src);
        Assert.Contains("public static void BridgeAll(", src);
    }
}
