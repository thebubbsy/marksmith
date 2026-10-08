using System.Linq;
using MarkSmith.Services;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// Diagram Studio boxes take the theme's heading colour on an always-dark canvas (#1E1E2E). For
/// every bundled theme the box must stand off the canvas and its label must read on the box
/// (WCAG AA, 4.5:1). Checked by numbers in run #37: the lowest were Cyberpunk (box 4.2:1, label
/// 4.5:1) and GitHub Light (a white box, label 17.7:1).
/// </summary>
public class DiagramStudioThemeContrastTests
{
    public static TheoryData<string> BuiltinThemes()
    {
        var data = new TheoryData<string>();
        foreach (var t in AppServices.Themes.All.Where(t => AppServices.Themes.IsBuiltin(t.Name))) data.Add(t.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltinThemes))]
    public void Node_boxes_stand_off_the_canvas_and_their_labels_read(string theme)
    {
        var heading = AppServices.Themes.GetOrDefault(theme).Heading;
        var fill = "#" + ContrastGuard.EnsureVisibleFill(heading, "1E1E2E");
        Assert.True(ContrastGuard.GetContrastRatio(fill, "#1E1E2E") >= 3.0, $"{theme}: box {fill} vs canvas");
        Assert.True(ContrastGuard.GetContrastRatio(DiagramNodeViewModel.ReadableLabelOn(fill), fill) >= 4.5, $"{theme}: label on {fill}");
    }
}
