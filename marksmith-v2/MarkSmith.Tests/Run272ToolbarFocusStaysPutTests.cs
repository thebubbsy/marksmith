using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #272: toolbar buttons that disable or hide themselves when pressed no longer throw keyboard
// focus to the window's first tab stop.
// - Services/FocusRescue: a group helper that moves focus to the next still-available button, else
//   a fallback (canvas / outline), and leaves focus the press moved on purpose alone;
// - Document Galaxy: Delete, Undo / Redo, and the link inspector's Reverse / Delete link;
// - SmartArt Studio: Undo / Redo, and the outline toolbar (Move up at the top, Promote at the root, Delete);
// - Shape Studio: Undo / Redo, the selection tools (Delete, Duplicate, align...), and Clear;
// - Main window: preview zoom in / out at the end of the ladder, and Auto-fix from the Markdown
//   issues list's menu (it rebuilt the list under the focused row).
public class Run272ToolbarFocusStaysPutTests
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

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative)).Replace("\r\n", "\n");

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing {signature}");
        int lineStart = source.LastIndexOf('\n', start) + 1;
        int indent = start - lineStart - (source.Substring(lineStart, start - lineStart).TrimStart().Length);
        var m = Regex.Match(source.Substring(start + signature.Length),
            @"\n {" + indent + @"}(?! )(private|public|internal|protected|//|\[)");
        return m.Success ? source.Substring(start, signature.Length + m.Index) : source.Substring(start);
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ");

    // ---- FocusRescue ----

    [Fact]
    public void FocusRescue_ActsOnLostFocus_Deferred_OnlyWhenTheMemberIsGone()
    {
        var src = Read("Services/FocusRescue.cs");
        var attach = Body(src, "public static void Attach(");
        Assert.Contains("member.LostFocus +=", attach);
        Assert.Contains("DispatcherQueue.TryEnqueue", attach);
        // A normal Tab away (member still available) and focus moved on purpose are left alone.
        Assert.Contains("if (IsAvailable(me) || me.XamlRoot is null || !FocusWasLost(me)) return;", attach);
        Assert.True(attach.IndexOf("NextAvailable(members, me)", StringComparison.Ordinal)
            < attach.IndexOf("fallback?.Invoke()", StringComparison.Ordinal));
    }

    [Fact]
    public void FocusRescue_PointerFocus_GetsNoFocusRectangle()
    {
        var attach = Body(Read("Services/FocusRescue.cs"), "public static void Attach(");
        Assert.Contains("FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard", attach);
    }

    [Fact]
    public void FocusRescue_LostMeansNothing_DisabledOrTheWindowsFirstTabStop()
    {
        var lost = Body(Read("Services/FocusRescue.cs"), "private static bool FocusWasLost(");
        Assert.Contains("focused is null || ReferenceEquals(focused, me)", lost);
        Assert.Contains("focused is Control { IsEnabled: false }", lost);
        Assert.Contains("FocusManager.FindFirstFocusableElement(content)", lost);
    }

    [Fact]
    public void FocusRescue_Available_ChecksEnabledTabStopAndCollapsedAncestors()
    {
        var available = Body(Read("Services/FocusRescue.cs"), "private static bool IsAvailable(");
        Assert.Contains("!c.IsEnabled || !c.IsTabStop", available);
        Assert.Contains("VisualTreeHelper.GetParent(d)", available);
        Assert.Contains("Visibility: Visibility.Collapsed", available);
    }

    [Fact]
    public void FocusRescue_NextAvailable_PrefersTheNextMemberThenThePrevious()
    {
        var next = Body(Read("Services/FocusRescue.cs"), "internal static Control? NextAvailable(");
        Assert.True(next.IndexOf("k = i + 1", StringComparison.Ordinal) < next.IndexOf("k = i - 1", StringComparison.Ordinal));
    }

    // ---- Studios ----

    [Fact]
    public void Galaxy_DeleteUndoRedoAndLinkInspector_AreRescued()
    {
        var src = Squash(Read("Views/MindMap/MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("FocusRescue.Attach(() => GalaxyCanvas, DeleteToolButton);", src);
        Assert.Contains("FocusRescue.Attach(() => GalaxyCanvas, UndoToolButton, RedoToolButton);", src);
        Assert.Contains("FocusRescue.Attach(() => GalaxyCanvas, ReverseLinkButton, DeleteLinkButton);", src);
    }

    [Fact]
    public void SmartArt_UndoRedoAndOutlineToolbar_AreRescued_InToolbarOrder()
    {
        var src = Squash(Read("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("FocusRescue.Attach(() => OutlineScroll, UndoStudioButton, RedoStudioButton);", src);
        Assert.Contains("FocusRescue.Attach(() => OutlineScroll, RenameButton, DuplicateButton, DeleteButton, "
            + "MoveUpButton, MoveDownButton, PromoteButton, DemoteButton);", src);
    }

    [Fact]
    public void ShapeStudio_UndoRedoSelectionToolsAndClear_AreRescued()
    {
        var src = Squash(Read("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("FocusRescue.Attach(() => CanvasScroller, ToolUndoButton, ToolRedoButton);", src);
        Assert.Matches(@"FocusRescue\.Attach\(\(\) => CanvasScroller, AlignLeftButton,[^;]*DuplicateToolButton, DeleteSelectionToolButton\);", src);
        Assert.Contains("FocusRescue.Attach(() => CanvasScroller, ClearCanvasButton);", src);
    }

    [Fact]
    public void Studios_RescueIsWiredInTheConstructor()
    {
        foreach (var (file, ctor) in new[]
        {
            ("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "public MindMapGalaxyWindow("),
            ("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs", "public SmartArtDesignStudioWindow("),
            ("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "public ShapeDesignStudioWindow("),
        })
        {
            Assert.Contains("FocusRescue.Attach(", Body(Read(file), ctor));
        }
    }

    // ---- Main window ----

    [Fact]
    public void PreviewZoom_MovesFocusBeforeDisablingTheButtonThatHasIt()
    {
        var body = Body(Read("MainWindow.xaml.cs"), "private void UpdatePreviewZoomReadout(");
        int rescueIn = body.IndexOf("if (!canIn && PreviewZoomInButton.FocusState != FocusState.Unfocused)", StringComparison.Ordinal);
        int rescueOut = body.IndexOf("if (!canOut && PreviewZoomOutButton.FocusState != FocusState.Unfocused)", StringComparison.Ordinal);
        int disableIn = body.IndexOf("PreviewZoomInButton.IsEnabled = canIn;", StringComparison.Ordinal);
        int disableOut = body.IndexOf("PreviewZoomOutButton.IsEnabled = canOut;", StringComparison.Ordinal);
        Assert.True(rescueIn >= 0 && rescueOut >= 0 && disableIn >= 0 && disableOut >= 0);
        Assert.True(rescueIn < disableIn && rescueOut < disableIn && rescueIn < disableOut && rescueOut < disableOut);
        // The other zoom button first, the readout when both ends are reached.
        Assert.Contains("(canOut ? PreviewZoomOutButton : (Control)PreviewZoomResetButton).Focus(PreviewZoomInButton.FocusState)", body);
        Assert.Contains("(canIn ? PreviewZoomInButton : (Control)PreviewZoomResetButton).Focus(PreviewZoomOutButton.FocusState)", body);
    }

    [Fact]
    public void LintIssueMenu_AutoFix_ClosesTheListAndFocusesTheEditor_OnlyWhenSomethingWasFixed()
    {
        var src = Read("MainWindow.xaml.cs");
        var menu = Body(src, "private void ShowLintIssueContextMenu(");
        var fix = menu.Substring(menu.IndexOf("fixSafeLintItem.Click +=", StringComparison.Ordinal));
        int unchanged = fix.IndexOf("if (!ApplySafeMarkdownLintFixes()) return;", StringComparison.Ordinal);
        int hide = fix.IndexOf("LintFlyout?.Hide();", StringComparison.Ordinal);
        int focus = fix.IndexOf("PasteTextBox?.Focus(FocusState.Keyboard);", StringComparison.Ordinal);
        Assert.True(unchanged >= 0 && hide > unchanged && focus > hide);

        // Unfolding for the fix changes the editor text even when nothing is fixed, so the result
        // comes from the fix count, not a text comparison.
        var apply = Body(src, "private bool ApplySafeMarkdownLintFixes(");
        Assert.Contains("if (totalFixes == 0)", apply);
        Assert.Contains("return false;", apply);
        Assert.Contains("return true;", apply);
    }

    [Fact]
    public void FindCountMenu_AutoFix_IsUnchanged()
    {
        // The Find bar's menu isn't inside the issues flyout, so it keeps the plain call.
        var src = Read("MainWindow.xaml.cs");
        Assert.Contains("IsEnabled = !string.IsNullOrEmpty(PasteTextBox?.Text)\n        };\n        fixSafeLintItem.Click += (_, _) => ApplySafeMarkdownLintFixes();", src);
    }
}
