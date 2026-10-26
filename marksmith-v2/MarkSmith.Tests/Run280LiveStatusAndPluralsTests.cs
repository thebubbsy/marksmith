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
    [InlineData(Settings, "LiveAnnounce.RaiseIfChanged(GoogleStatusBar, vm.GoogleAuthStatus)")]
    [InlineData(Settings, "LiveAnnounce.RaiseIfChanged(PluginWarningsBar, PluginWarningsBar.Message)")]
    [InlineData(SuiteHub, "LiveAnnounce.Raise(Notification)")]
    [InlineData(History, "LiveAnnounce.Raise(NoticeBar)")]
    [InlineData(OcrOverlay, "Services.LiveAnnounce.Track(StageStatusText)")]
    [InlineData(ExtensionSetup, "LiveAnnounce.Track(StatusText)")]
    [InlineData(ExtensionSetup, "LiveAnnounce.RaiseIfChanged(StatusText, message)")]
    public void StatusLinesAndInfoBars_AreAnnounced(string file, string expectedCall)
    {
        var content = Read(file);
        Assert.Contains(expectedCall, content);
    }

    // ---- Deactivate License Focus Retention ----

    [Fact]
    public void DeactivateLicense_OnlyRefocusesIfButtonWaiting()
    {
        var src = Read(Settings);
        Assert.Contains("var parked = FocusRescue.Park(DeactivateButton, BuyButton, StartTrialButton);", src);
        Assert.Contains("if (parked.Waiting)", src);
        Assert.Contains("KeyBox.Focus(parked.How);", src);
        Assert.Contains("parked.Return();", src);
    }

    // ---- History Word Delta Grammar ----

    [Theory]
    [InlineData(1, 0, "+1 word / -0 words")]
    [InlineData(0, 1, "+0 words / -1 word")]
    [InlineData(1, 1, "+1 word / -1 word")]
    [InlineData(0, 0, "+0 words / -0 words")]
    [InlineData(3, 0, "+3 words / -0 words")]
    [InlineData(0, 5, "+0 words / -5 words")]
    public void WordDelta_Grammar_FormatsEachSideRight(int added, int removed, string expected)
    {
        var delta = CountText.Delta(added, removed, "word");
        Assert.Equal(expected, delta);
    }

    [Fact]
    public void HistoryWindow_UsesCountTextDelta_ForWordDelta()
    {
        var src = Read(History);
        Assert.Contains("CountText.Delta(addedWords, removedWords, \"word\")", src);
        Assert.Contains("CountText.Delta(_lastAddedWords, _lastRemovedWords, \"word\")", src);
    }
}
