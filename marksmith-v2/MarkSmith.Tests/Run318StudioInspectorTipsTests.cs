using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #318:
// - Diagram Studio's inspector: the Shape box, X/Y/Width/Height boxes and the four connector boxes
//   (line, arrow head, sequence message, class relationship) say what they change and which keys
//   work, as a tooltip and, through ToolTipHelp, as help text;
// - Shape Studio: the eight mosaic tile-shape boxes say they apply while Smart tiles is off, the
//   Line style box, "Dark ink only" and the Shape type box explain themselves, and the fill
//   flyout's "Use this colour" is polished when the flyout opens (hover lift, help text);
// - History: each version card's help text names its keys (S, F2, Ctrl+C, Shift+F10).
public class Run318StudioInspectorTipsTests
{
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

    private const string Diagram = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml";
    private const string Shape = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml";

    // The opening tag of the element named `name`.
    private static string Tag(string xaml, string name)
    {
        var at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, name);
        var start = xaml.LastIndexOf('<', at);
        var end = xaml.IndexOf('>', at);
        return xaml[start..(end + 1)];
    }

    private static string Tip(string tag)
    {
        var m = Regex.Match(tag, "ToolTipService.ToolTip=\"([^\"]*)\"");
        Assert.True(m.Success, tag);
        return m.Groups[1].Value;
    }

    [Theory]
    [InlineData("NodeShapeCombo", "Alt+Down")]
    [InlineData("NodeXBox", "Enter applies")]
    [InlineData("NodeYBox", "Enter applies")]
    [InlineData("NodeWidthBox", "at least 20")]
    [InlineData("NodeHeightBox", "at least 20")]
    [InlineData("ConnectorLineCombo", "Esc returns to canvas")]
    [InlineData("ConnectorHeadCombo", "Esc returns to canvas")]
    [InlineData("ConnectorMessageCombo", "Esc returns to canvas")]
    [InlineData("ConnectorRelationCombo", "Esc returns to canvas")]
    public void Diagram_inspector_fields_say_what_they_change_and_their_keys(string name, string phrase)
    {
        Assert.Contains(phrase, Tip(Tag(Read(Diagram), name)));
    }

    [Fact]
    public void Diagram_inspector_tips_match_the_key_handlers_they_use()
    {
        var xaml = Read(Diagram);
        // Enter returns to canvas only in the single-line handler; Ctrl+Enter/Esc in the other.
        foreach (var n in new[] { "NodeXBox", "NodeYBox", "NodeWidthBox", "NodeHeightBox" })
            Assert.Contains("OnInspectorSingleLineKeyDown", Tag(xaml, n));
        foreach (var n in new[] { "NodeShapeCombo", "ConnectorLineCombo", "ConnectorHeadCombo", "ConnectorMessageCombo", "ConnectorRelationCombo" })
            Assert.Contains("OnInspectorInputKeyDown", Tag(xaml, n));
    }

    [Theory]
    [InlineData("CompRoundRect")]
    [InlineData("CompRect")]
    [InlineData("CompEllipse")]
    [InlineData("CompChevron")]
    [InlineData("CompDiamond")]
    [InlineData("CompHexagon")]
    [InlineData("CompTriangle")]
    [InlineData("CompCloud")]
    public void Tile_shape_boxes_say_they_apply_while_smart_tiles_is_off(string name)
    {
        var tip = Tip(Tag(Read(Shape), name));
        Assert.StartsWith("Tile the mosaic with ", tip);
        Assert.Contains("Smart tiles is off", tip);
    }

    [Fact]
    public void Line_art_and_shape_type_boxes_explain_themselves()
    {
        var xaml = Read(Shape);
        Assert.Contains("Alt+Down", Tip(Tag(xaml, "FuseModePicker")));
        Assert.Contains("colour of the picture", Tip(Tag(xaml, "FuseMonochrome")));
        Assert.Contains("keeping its size", Tip(Tag(xaml, "ShapeTypeCombo")));
        Assert.Contains("Ctrl+Z undoes it", Tip(Tag(xaml, "FillPickerApplyButton")));
    }

    [Fact]
    public void Fill_flyout_polishes_its_apply_button_when_it_opens()
    {
        var cs = Read(Shape + ".cs");
        var start = cs.IndexOf("private void OnFillFlyoutOpening(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = cs.IndexOf("\n        }\n", start, StringComparison.Ordinal);
        Assert.Contains("HoverPolish.Apply(FillPickerApplyButton);", cs[start..end]);
    }

    [Fact]
    public void History_version_cards_name_their_keys()
    {
        var xaml = Read("MarkSmith.Desktop/Views/History/HistoryWindow.xaml");
        var at = xaml.IndexOf("Click=\"OnVersionClick\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var tag = xaml[xaml.LastIndexOf('<', at)..(xaml.IndexOf('>', at) + 1)];
        var help = Regex.Match(tag, "AutomationProperties.HelpText=\"([^\"]*)\"").Groups[1].Value;
        foreach (var key in new[] { "S stars", "F2 labels", "Ctrl+C copies", "Shift+F10" })
            Assert.Contains(key, help);
    }

    // Guard: every input control in the two studio inspectors has a tooltip or help text (the
    // style preset buttons get theirs from HighlightActivePalette at runtime).
    [Theory]
    [InlineData(Diagram)]
    [InlineData(Shape)]
    public void Studio_inputs_all_have_a_tooltip_or_help_text(string file)
    {
        var xaml = Read(file);
        var runtimeTipped = new[] { "PresetCatppuccin", "PresetNord", "PresetEmerald", "PresetMono" };
        var gaps = Regex.Matches(xaml, @"<(Button|ToggleButton|ComboBox|CheckBox|SplitButton|DropDownButton|RadioButton|ToggleSwitch|Slider|NumberBox)\s([^>]*)>")
            .Where(m => !m.Groups[2].Value.Contains("ToolTipService.ToolTip") && !m.Groups[2].Value.Contains("HelpText"))
            .Where(m => !Regex.IsMatch(xaml[(m.Index + m.Length)..], @"^\s*(<!--.*?-->\s*)*<ToolTipService\.ToolTip", RegexOptions.Singleline))
            .Select(m => Regex.Match(m.Groups[2].Value, "x:Name=\"([^\"]*)\"").Groups[1].Value)
            .Where(n => !runtimeTipped.Contains(n))
            .ToList();
        Assert.Empty(gaps);
    }
}
