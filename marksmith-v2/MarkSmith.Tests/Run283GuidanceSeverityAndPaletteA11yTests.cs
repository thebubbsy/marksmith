using System;
using System.IO;
using Xunit;

namespace MarkSmith.Tests;

// Run #283:
// - Version History diff line jump and no-content notices use ShowInfo (informational, polite, auto-dismissing)
//   rather than error notices that pin red banners until manually dismissed;
// - Command Palette XAML explicitly declares AutomationProperties.AutomationId on SearchBox, ClearSearchButton,
//   EscButton, and Results for reliable UI automation and screen-reader discovery.
public class Run283GuidanceSeverityAndPaletteA11yTests
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

    private const string History = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml.cs";
    private const string PaletteXaml = "MarkSmith.Desktop/Controls/CommandPalette.xaml";

    [Theory]
    [InlineData("_vm.ShowInfo($\"Line {targetLine} was not found in the active diff.\");")]
    [InlineData("_vm.ShowInfo(\"Couldn't copy that version's text: no content stored.\");")]
    public void HistoryDiffGuidanceNotices_UseShowInfo(string call)
    {
        var src = Read(History);
        Assert.Contains(call, src);
    }

    [Fact]
    public void HistoryDiffLineNotFound_IsNotErrorNotice()
    {
        var src = Read(History);
        Assert.DoesNotContain("_vm.ShowNotice($\"Line {targetLine} was not found in the active diff.\", isError: true);", src);
        Assert.DoesNotContain("_vm.ShowNotice(\"Couldn't copy that version's text: no content stored.\", isError: true);", src);
    }

    [Fact]
    public void CommandPalette_SearchBoxAndControls_HaveAutomationIds()
    {
        var src = Read(PaletteXaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SearchBox\"", src);
        Assert.Contains("AutomationProperties.AutomationId=\"ClearSearchButton\"", src);
        Assert.Contains("AutomationProperties.AutomationId=\"EscButton\"", src);
        Assert.Contains("AutomationProperties.AutomationId=\"Results\"", src);
    }
}
