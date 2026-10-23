using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #239: keyboard focus in the OCR studio's element cards follows the selection (Tab onto a
// card selects it, Up/Down carry focus to the next card, a page flip puts focus back on a card),
// the insert dialogs' Ctrl+C respects a selection in the preview and their count badges are
// read by their text, and the image drop zone's spoken names follow its visible text.
public class Run239CardFocusAndInsertNamesTests
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(DesktopDir(), Path.Combine(parts)));

    private static string Ocr() => Read("Views", "Ocr", "OcrInspectionStudioWindow.xaml.cs");

    private static string Method(string code, string name)
    {
        // The declaration, not a call site: an access modifier starts the line.
        var decl = Regex.Match(code, $@"^\s*(private|protected|public|internal)\b[^\n;=]*\s{Regex.Escape(name)}\(", RegexOptions.Multiline);
        Assert.True(decl.Success, $"{name} not found");
        var open = code.IndexOf('{', decl.Index);
        // Expression-bodied members (=> new() { … }) still have their body in braces.
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        throw new InvalidOperationException($"{name} is unbalanced");
    }

    [Fact]
    public void OcrCardCombo_SelectsItsElement_WhenItGetsFocus()
    {
        var card = Method(Ocr(), "CreateInspectorCard");
        Assert.Matches(@"combo\.GotFocus \+= \(_, _\) =>\s*\{\s*if \(!ReferenceEquals\(_selectedElement, elem\)\) SelectElement\(elem\);", card);
    }

    [Fact]
    public void OcrUpDown_CarryFocusToTheNewlySelectedCard()
    {
        var code = Ocr();
        Assert.Contains("SelectElementKeepingCardFocus(page.Elements[prevIdx]);", Method(code, "SelectPreviousElement"));
        Assert.Contains("SelectElementKeepingCardFocus(page.Elements[nextIdx]);", Method(code, "SelectNextElement"));

        var keep = Method(code, "SelectElementKeepingCardFocus");
        // The focus check has to happen before SelectElement, which doesn't move focus itself.
        Assert.True(keep.IndexOf("IsFocusWithinElementCards()", StringComparison.Ordinal)
                    < keep.IndexOf("SelectElement(elem)", StringComparison.Ordinal));
        Assert.Contains("if (focusWasOnCard) FocusSelectedCard();", keep);
    }

    [Fact]
    public void OcrPageRender_PutsFocusBack_WhenItRebuiltTheFocusedCard()
    {
        var render = Method(Ocr(), "RenderCurrentPage");
        int check = render.IndexOf("bool focusWasOnCard = IsFocusWithinElementCards();", StringComparison.Ordinal);
        int clear = render.IndexOf("ElementCardsPanel.Children.Clear();", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < clear, "focus must be checked before the cards are removed");
        Assert.Contains("if (focusWasOnCard && !FocusSelectedCard())", render);
        // An empty page falls back to a control that is actually enabled.
        Assert.Contains("NextPageButton is { IsEnabled: true }", render);
        Assert.Contains("ImportCtaButton", render);
    }

    [Fact]
    public void OcrFocusSelectedCard_TargetsTheCardsReclassifyBox()
    {
        var focus = Method(Ocr(), "FocusSelectedCard");
        Assert.Contains("stack.Children[1] is not ComboBox combo", focus);
        Assert.Contains("combo.Focus(FocusState.Keyboard)", focus);
        // The card layout this relies on: header grid, then the combo.
        var card = Method(Ocr(), "CreateInspectorCard");
        Assert.True(card.IndexOf("cardStack.Children.Add(headerGrid);", StringComparison.Ordinal)
                    < card.IndexOf("cardStack.Children.Add(combo);", StringComparison.Ordinal));
    }

    [Fact]
    public void InsertDialogCtrlC_LeavesASelectionInThePreviewAlone()
    {
        var code = Read("Views", "InsertDialogControls.cs");
        var keyDown = Method(code, "OnBodyKeyDown");
        Assert.Contains("TextBlock tbk => tbk.SelectedText.Length > 0", keyDown);
        // The preview really is selectable, or the branch would be dead.
        Assert.Contains("IsTextSelectionEnabled = true", code);
    }

    [Fact]
    public void InsertDialogCountBadge_IsReadByItsText()
    {
        var code = Read("Views", "InsertDialogControls.cs");
        var badge = Method(code, "CountBadge");
        Assert.DoesNotContain("AutomationProperties.SetName", badge);
        Assert.DoesNotContain("\"Item count\"", code);
    }

    [Fact]
    public void ImageDropZone_SetsTextAndSpokenNameTogether()
    {
        var code = Read("Views", "ImageInsertControl.cs");
        var showThumb = Method(code, "ShowThumb");
        // Every zone-text change in ShowThumb goes through SetZoneText.
        Assert.DoesNotMatch(@"_zoneTitle\.Text\s*=", showThumb);
        Assert.DoesNotMatch(@"_zoneHint\.Text\s*=", showThumb);
        Assert.Contains("SetZoneText(\"Drag an image here\"", showThumb);

        var set = Method(code, "SetZoneText");
        Assert.Contains("AutomationProperties.SetName(_zoneTitle, spoken);", set);
        Assert.Contains("AutomationProperties.SetName(_zoneHint, hint);", set);
        Assert.Contains("AutomationEvents.LiveRegionChanged", set);

        // The drop handler no longer names the zone after a file that may not be an image.
        Assert.DoesNotMatch(new Regex(@"SetName\(_zoneTitle, \$""Selected image: \{file\.Name\}""\)"), code);
    }

    [Fact]
    public void ImagePreviewFailure_KeepsTheChosenImagesName()
    {
        var code = Read("Views", "ImageInsertControl.cs");
        var failed = code[code.IndexOf("_thumb.ImageFailed += ", StringComparison.Ordinal)..];
        failed = failed[..failed.IndexOf("};", StringComparison.Ordinal)];
        // Source is read before ShowThumb(null) resets the zone.
        Assert.True(failed.IndexOf("var src = Source;", StringComparison.Ordinal)
                    < failed.IndexOf("ShowThumb(null);", StringComparison.Ordinal));
        Assert.Contains("SetZoneText(name, PreviewUnsupported, $\"Selected image: {name}\");", failed);

        // Dragging over a zone whose preview failed doesn't wipe the chosen image's name.
        Assert.Contains("_zoneHint.Text != PreviewUnsupported", Method(code, "Highlight"));
    }
}
