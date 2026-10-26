using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #280: sweeping remaining InfoBars & status lines for LiveAnnounce, preserving focus on license deactivation,
// and natural word delta pluralization in History.
public class Run280LiveStatusAndPluralsTests
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

    private const string Settings = "MarkSmith.Desktop/Views/SettingsView.xaml.cs";
    private const string SuiteHub = "MarkSmith.Desktop/Views/SuiteHubView.xaml.cs";
    private const string History = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml.cs";
    private const string OcrOverlay = "MarkSmith.Desktop/Controls/OcrExperienceOverlay.xaml.cs";
    private const string ExtensionSetup = "MarkSmith.Desktop/Views/ExtensionSetupView.xaml.cs";

    // ---- LiveAnnounce Coverage ----

    [Theory]
    [InlineData(Settings, "LiveAnnounce.Track(HouseStyleStatusText)")]
    [InlineData(Settings, "LiveAnnounce.Track(CloudRescanStatusText)")]
    [InlineData(Settings, "LiveAnnounce.Raise(LicenseStatusBar)")]
    [InlineData(Settings, "LiveAnnounce.Raise(CloudRescanStatusText)")]
    [InlineData(Settings, "LiveAnnounce.Raise(UpdateStatusBar)")]
    [InlineData(Settings, "LiveAnnounce.Raise(GoogleStatusBar)")]
    [InlineData(Settings, "LiveAnnounce.Raise(PluginWarningsBar)")]
    [InlineData(SuiteHub, "LiveAnnounce.Raise(Notification)")]
    [InlineData(History, "LiveAnnounce.Raise(NoticeBar)")]
    [InlineData(OcrOverlay, "Services.LiveAnnounce.Track(StageStatusText)")]
    [InlineData(ExtensionSetup, "LiveAnnounce.Track(StatusText)")]
    [InlineData(ExtensionSetup, "LiveAnnounce.Raise(StatusText)")]
    public void StatusLinesAndInfoBars_AreAnnounced(string file, string expectedCall)
    {
        var content = Read(file);
        Assert.Contains(expectedCall, content);
    }

    // ---- Deactivate License Focus Retention ----

    [Fact]
    public void DeactivateLicense_OnlyRefocusesIfButtonHeldFocus()
    {
        var src = Read(Settings);
        Assert.Contains("var parked = FocusRescue.Park(DeactivateButton, BuyButton, StartTrialButton);", src);
        Assert.Contains("if (parked.HadFocus)", src);
        Assert.Contains("KeyBox.Focus(parked.How);", src);
        Assert.Contains("DeactivateButton.Focus(parked.How);", src);
    }

    // ---- History Word Delta Grammar ----

    [Theory]
    [InlineData(1, 0, "word")]
    [InlineData(0, 1, "word")]
    [InlineData(1, 1, "words")]
    [InlineData(0, 0, "words")]
    [InlineData(3, 0, "words")]
    [InlineData(0, 5, "words")]
    public void WordDelta_Grammar_IsSingularAtOneTotalWord(int added, int removed, string expectedNoun)
    {
        var total = Math.Abs(added) + Math.Abs(removed);
        var actualNoun = CountText.Noun(total, "word");
        Assert.Equal(expectedNoun, actualNoun);
    }

    [Fact]
    public void HistoryWindow_UsesCountTextNoun_ForWordDelta()
    {
        var src = Read(History);
        Assert.Contains("CountText.Noun(Math.Abs(addedWords) + Math.Abs(removedWords), \"word\")", src);
        Assert.Contains("CountText.Noun(Math.Abs(_lastAddedWords) + Math.Abs(_lastRemovedWords), \"word\")", src);
    }
}
