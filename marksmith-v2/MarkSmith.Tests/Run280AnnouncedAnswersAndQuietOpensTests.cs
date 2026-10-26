using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #280: the rest of the status lines screen readers couldn't hear, and quieter opens.
// - Settings' license result, cloud rescan result, Google sign-in status, plugin load warnings
//   and per-plugin install status, the file-name preview, OCR engine description and API status;
//   the Extension setup status; the Welcome tour's step; Suite Hub's notification; the SmartArt
//   insert dialog's layout caption and advice; and the extension hint bar / tip now raise
//   LiveRegionChanged (LiveSetting alone announces nothing in WinUI);
// - LiveAnnounce.Track ignores text filled in before the view has loaded, and RaiseIfChanged
//   skips a re-rendered message that hasn't changed;
// - Remove license no longer pulls focus into the key box after a mouse click (Parked.Waiting);
// - History's diff pill counts each side: "+1 word / -3 words".
public class Run280AnnouncedAnswersAndQuietOpensTests
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

    private const string Live = "MarkSmith.Desktop/Services/LiveAnnounce.cs";
    private const string Rescue = "MarkSmith.Desktop/Services/FocusRescue.cs";
    private const string Settings = "MarkSmith.Desktop/Views/SettingsView.xaml.cs";
    private const string History = "MarkSmith.Desktop/Views/History/HistoryWindow.xaml.cs";

    // The method body starting at signature (brace-matched).
    private static string Body(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[start..(i + 1)];
        }
        return src[start..];
    }

    // ---- CountText.Delta ----

    [Theory]
    [InlineData(1, 0, "+1 word / -0 words")]
    [InlineData(0, 1, "+0 words / -1 word")]
    [InlineData(12, 3, "+12 words / -3 words")]
    [InlineData(1, 1, "+1 word / -1 word")]
    [InlineData(1204, 0, "+1,204 words / -0 words")]
    public void Delta_GivesEachSideItsOwnNoun(int added, int removed, string expected)
    {
        Assert.Equal(expected, CountText.Delta(added, removed, "word"));
    }

    [Fact]
    public void Delta_UsesTheGivenPlural()
    {
        Assert.Equal("+2 entries / -1 entry", CountText.Delta(2, 1, "entry", "entries"));
    }

    [Fact]
    public void History_WordDeltaPillAndCopies_UseDelta_NotOnePluralForTwoCounts()
    {
        var src = Read(History);
        Assert.DoesNotContain("/ -{removedWords:N0} words", src);
        Assert.DoesNotContain("/ -{_lastRemovedWords:N0} words", src);
        Assert.Contains("DiffWordDeltaText.Text = CountText.Delta(addedWords, removedWords, \"word\");", src);
        Assert.Equal(2, CountOf(src, "CountText.Delta(_lastAddedWords, _lastRemovedWords, \"word\")"));
    }

    private static int CountOf(string src, string needle)
    {
        int n = 0, i = 0;
        while ((i = src.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // ---- LiveAnnounce ----

    [Fact]
    public void Track_IgnoresTextFilledInBeforeTheViewLoads()
    {
        var track = Body(Read(Live), "public static void Track(");
        var loaded = track.IndexOf("if (!source.IsLoaded) return;", StringComparison.Ordinal);
        var schedule = track.IndexOf("Schedule(target);", StringComparison.Ordinal);
        Assert.True(loaded > 0 && loaded < schedule);
    }

    [Fact]
    public void RaiseIfChanged_SkipsARepeat_AndForgetsOnBlank()
    {
        var body = Body(Read(Live), "public static void RaiseIfChanged(");
        Assert.Contains("LastSaid.Remove(element); return;", body);
        Assert.Contains("last.Text == message) return;", body);
        Assert.Contains("Raise(element);", body);
        // Weak table: a closed view's last message goes with it.
        Assert.Contains("ConditionalWeakTable<UIElement, Said> LastSaid", Read(Live));
    }

    // ---- Every answer that was marked live but never raised ----

    [Theory]
    [InlineData(Settings, "LicenseStatusBar.IsOpen = true;", "LiveAnnounce.Raise(LicenseStatusBar);")]
    [InlineData(Settings, "SetLiveSetting(CloudRescanStatusText", "LiveAnnounce.Raise(CloudRescanStatusText);")]
    [InlineData(Settings, "SetLiveSetting(GoogleStatusBar", "LiveAnnounce.RaiseIfChanged(GoogleStatusBar, vm.GoogleAuthStatus);")]
    [InlineData(Settings, "PluginWarningsBar.IsOpen = true;", "LiveAnnounce.RaiseIfChanged(PluginWarningsBar, PluginWarningsBar.Message);")]
    [InlineData("MarkSmith.Desktop/Views/ExtensionSetupView.xaml.cs", "SetLiveSetting(StatusText", "LiveAnnounce.RaiseIfChanged(StatusText, message);")]
    [InlineData("MarkSmith.Desktop/Views/WelcomeTour.xaml.cs", "SetLiveSetting(PagesHost", "LiveAnnounce.RaiseIfChanged(PagesHost, announcement);")]
    [InlineData("MarkSmith.Desktop/Views/SuiteHubView.xaml.cs", "Notification.IsOpen = true;", "LiveAnnounce.Raise(Notification);")]
    [InlineData("MarkSmith.Desktop/Controls/ExtensionHintBar.xaml.cs", "SetLiveSetting(control", "LiveAnnounce.Raise(control);")]
    [InlineData("MarkSmith.Desktop/Controls/ExtensionTip.xaml.cs", "SetLiveSetting(control", "LiveAnnounce.Raise(control);")]
    public void LiveStatus_IsRaisedRightAfterItIsShown(string file, string anchor, string raise)
    {
        var src = Read(file);
        var a = src.IndexOf(anchor, StringComparison.Ordinal);
        var r = src.IndexOf(raise, a < 0 ? 0 : a, StringComparison.Ordinal);
        Assert.True(a > 0 && r > a && r - a < 260, $"{raise} after {anchor}");
    }

    [Theory]
    [InlineData(Settings, "LiveAnnounce.Track(FileNamePreviewText);")]
    [InlineData(Settings, "LiveAnnounce.Track(OcrEngineDescriptionText);")]
    [InlineData(Settings, "LiveAnnounce.Track(ApiStatusNormalText);")]
    [InlineData(Settings, "LiveAnnounce.Track(ApiStatusErrorText);")]
    [InlineData(Settings, "LiveAnnounce.Track(status);")]
    [InlineData("MarkSmith.Desktop/Views/SmartArtInsertControl.cs", "LiveAnnounce.Track(_layoutCaption);")]
    [InlineData("MarkSmith.Desktop/Views/SmartArtInsertControl.cs", "LiveAnnounce.Track(_adviceText);")]
    public void ChangingTextLines_AreTracked(string file, string track)
    {
        Assert.Contains(track, Read(file));
    }

    [Fact]
    public void SettingsTrackers_RunAfterInitializeComponent()
    {
        var ctor = Body(Read(Settings), "public SettingsView()");
        var init = ctor.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        Assert.True(init > 0 && init < ctor.IndexOf("LiveAnnounce.Track(FileNamePreviewText);", StringComparison.Ordinal));
    }

    [Fact]
    public void SmartArtInsert_NoLongerSetsLiveSettingWithoutARaise()
    {
        var src = Read("MarkSmith.Desktop/Views/SmartArtInsertControl.cs");
        Assert.DoesNotContain("SetLiveSetting(_layoutCaption", src);
        Assert.DoesNotContain("SetLiveSetting(_adviceText", src);
    }

    // ---- Remove license focus ----

    [Fact]
    public void Parked_Waiting_IsTheSharedCheck()
    {
        var src = Read(Rescue);
        var waiting = Body(src, "public bool Waiting");
        Assert.Contains("Spot is null", waiting);
        Assert.Contains("return ReferenceEquals(focused, Spot) || FocusWasLost(Button);", waiting);
        Assert.Contains("if (Waiting) Button.Focus(How);", Body(src, "public void Return()"));
    }

    [Fact]
    public void RemoveLicense_MovesFocusOnlyWhileItIsStillParked()
    {
        var body = Body(Read(Settings), "private async void OnDeactivateLicense(");
        Assert.Contains("var parked = FocusRescue.Park(DeactivateButton, BuyButton, StartTrialButton);", body);
        var fin = body.IndexOf("finally", StringComparison.Ordinal);
        var tail = body[fin..];
        var guard = tail.IndexOf("if (parked.Waiting)", StringComparison.Ordinal);
        var key = tail.IndexOf("KeyBox.Focus(parked.How);", StringComparison.Ordinal);
        Assert.True(guard > 0 && key > guard);
        Assert.Contains("parked.Return();", tail);
        Assert.DoesNotContain("KeyBox.Focus(FocusState.Programmatic);", tail);
        Assert.DoesNotContain("DeactivateButton.Focus(FocusState.Programmatic);", tail);
    }
}
