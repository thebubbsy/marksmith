using System;
using System.IO;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

// Run #315:
// - SmartArt Studio's audit and report lines say "1 node", "1 line", "1 word", "1 child" (121 raw
//   "{n} nouns" counts now go through CountText), and "1 line exceeds" agrees with its count;
// - every Insert dialog copies a field's or button's tooltip into its help text, so "(optional)",
//   Browse's "Ctrl+O, Alt+B" and the like reach keyboard and screen reader users;
// - SmartArt Insert's outline box carries its live count in its help text, and its "Reset all
//   indentations" / "Copy outline text" items are disabled when they would do nothing;
// - the Bibliography dialog explains its four fields, and an empty year fills in this year (the
//   placeholder the Year box shows), not a fixed 2026;
// - Image's copy bubble says "Copied image address" for a web address.
public class Run315InsertHelpAndSmartArtCountsTests
{
    private const string Dialogs = "MarkSmith.Desktop/Views/InsertDialogControls.cs";
    private const string SmartArtInsert = "MarkSmith.Desktop/Views/SmartArtInsertControl.cs";
    private const string ImageInsert = "MarkSmith.Desktop/Views/ImageInsertControl.cs";
    private const string SmartArtVm = "MarkSmith.Core/ViewModels/SmartArtStudio/SmartArtDesignStudioViewModel.cs";

    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoDir(), relative)).Replace("\r\n", "\n");

    private static string Between(string code, string start, string end)
    {
        var a = code.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, start);
        var b = code.IndexOf(end, a, StringComparison.Ordinal);
        Assert.True(b > a, end);
        return code[a..b];
    }

    // ---- SmartArt Studio counts ----

    [Fact]
    public void HierarchyBreakdown_OneLine_SaysOneItem()
    {
        var summary = SmartArtDesignStudioViewModel.FormatOutlineHierarchyBreakdown("- Only");
        Assert.StartsWith("Outline: 1 item across 1 level", summary);
        Assert.DoesNotContain("1 items", summary);
    }

    [Fact]
    public void DepthSummary_OneLine_SaysOneNode()
    {
        var summary = SmartArtDesignStudioViewModel.FormatOutlineDepthDistributionSummary("- Only");
        Assert.Contains("1 node ", summary);
        Assert.DoesNotContain("1 nodes", summary);
    }

    [Fact]
    public void LineLengthAudit_OneLongLine_SaysOneLineExceeds()
    {
        var summary = SmartArtDesignStudioViewModel.FormatOutlineLineLengthAuditSummary(
            "- " + new string('x', 60), 40);
        Assert.StartsWith("1 line ", summary);
        Assert.Contains("1 line exceeds 40 chars", summary);
        Assert.DoesNotContain("1 lines", summary);
    }

    [Fact]
    public void LineLengthAudit_TwoLongLines_StaysPlural()
    {
        var summary = SmartArtDesignStudioViewModel.FormatOutlineLineLengthAuditSummary(
            "- " + new string('x', 60) + "\n- " + new string('y', 60), 40);
        Assert.Contains("2 lines exceed 40 chars", summary);
    }

    [Fact]
    public void SmartArtVm_HasNoRawCountPluralsLeft()
    {
        var code = Read(SmartArtVm);
        Assert.Contains("using MarkSmith.Services;", code);
        // "{x} nodes" with a bare identifier inside an interpolated string: each one said
        // "1 nodes" for a single item. Lines that branch on "== 1" first and "a vs b" pairs are fine.
        var raw = new Regex(@"\{[A-Za-z_][A-Za-z0-9_.]*\} (nodes|items|lines|steps|stages|words|chars|children|quadrants|sub-items|substeps)\b");
        foreach (var line in code.Split('\n'))
        {
            if (!line.Contains('$') || line.TrimStart().StartsWith("//")) continue;
            foreach (Match m in raw.Matches(line))
            {
                var expr = m.Value[1..m.Value.IndexOf('}')];
                if (line.Contains($"{expr} == 1") || line[..m.Index].EndsWith("vs ")) continue;
                Assert.Fail($"raw plural: {line.Trim()}");
            }
        }
    }

    [Fact]
    public void CountText_IrregularPlurals_UsedForChildrenAndBranches()
    {
        var code = Read(SmartArtVm);
        Assert.Contains("CountText.Of(popped.ChildCount, \"child\", \"children\")", code);
        Assert.Equal("1 child", CountText.Of(1, "child", "children"));
        Assert.Equal("3 children", CountText.Of(3, "child", "children"));
    }

    // ---- Insert dialogs: tooltips become help text ----

    [Fact]
    public void InsertDialogBody_CopiesToolTipsIntoHelpText_OnLoad()
    {
        var code = Read(Dialogs);
        var loaded = Between(code, "Loaded += (_, _) =>", "};");
        Assert.Contains("HelpFromToolTips(_fields);", loaded);
        // After Refresh, so a count already put in a box's help text wins over its tooltip.
        Assert.True(loaded.IndexOf("Refresh();", StringComparison.Ordinal) < loaded.IndexOf("HelpFromToolTips", StringComparison.Ordinal));

        var helper = Between(code, "internal static void HelpFromToolTips(", "private static Control? FirstInput");
        Assert.Contains("case Panel panel:", helper);
        Assert.Contains("case Border border:", helper);
        Assert.Contains("ToolTipService.GetToolTip(control) is string tip", helper);
        Assert.Contains("string.IsNullOrEmpty(AutomationProperties.GetHelpText(control))", helper);
    }

    [Fact]
    public void BibliographyFields_HaveToolTips()
    {
        var code = Read(Dialogs);
        var refs = Between(code, "public sealed class ReferencesInsertControl", "\n}\n");
        foreach (var box in new[] { "_author", "_title", "_year", "_id" })
            Assert.Contains($"ToolTipService.SetToolTip({box}, ", refs);
        Assert.Contains("Empty gets this year", refs);
    }

    [Fact]
    public void BibliographySnippet_EmptyYear_IsThisYear()
    {
        var snippet = InsertSnippetBuilder.References("", "", "", "");
        Assert.Contains($"year: {DateTime.Now.Year}\n", snippet);
        Assert.Contains("year: 1999\n", InsertSnippetBuilder.References("a", "b", "c", "1999"));
    }

    // ---- SmartArt Insert ----

    [Fact]
    public void SmartArtOutlineBox_HelpText_CarriesTheCount()
    {
        var code = Read(SmartArtInsert);
        Assert.Contains("public const string OutlineHelp = \"One item per line. Alt+Shift+Right indents the line, Alt+Shift+Left outdents it.\";", code);
        var problem = Between(code, "protected override string? Problem", "private void Update()");
        Assert.Contains("_count.Text = SmartArt.Describe(_layout, rows);", problem);
        Assert.Contains("AutomationProperties.SetHelpText(_box, $\"{_count.Text}. {OutlineHelp}\");", problem);
    }

    [Fact]
    public void SmartArtOutlineMenus_DisableItemsThatWouldDoNothing()
    {
        var code = Read(SmartArtInsert);
        var indentMenu = Between(code, "private void ShowIndentContextMenu(", "private void OnExamplesButtonRightTapped(");
        Assert.Contains("IsEnabled = lines.Any(l => l.Length > 0 && char.IsWhiteSpace(l[0])),", indentMenu);
        Assert.Equal(2, Regex.Matches(code, Regex.Escape("IsEnabled = !string.IsNullOrWhiteSpace(_box.Text),")).Count);
    }

    // ---- Image ----

    [Fact]
    public void ImageCopyBubble_NamesAWebAddressAsAnAddress()
    {
        var code = Read(ImageInsert);
        Assert.Contains("IsWeb(Source) ? \"Copied image address\" : \"Copied image path\"", code);
    }
}
