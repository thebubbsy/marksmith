using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #279: status lines screen readers can hear, and busy buttons that keep keyboard focus.
// - LiveSetting alone never announces anything in WinUI; the peer must raise LiveRegionChanged.
//   The main status message, the Find bar's match count, the Shape Studio / Document Galaxy /
//   OCR status lines, Suite Hub's template search, Settings' house-style status, the command
//   palette's count and the update check's answer are now raised (coalesced over 250 ms);
// - Rescan, Check for updates, Remove license and Activate disabled the focused control for the
//   whole wait, throwing focus to the window's first tab stop; focus now waits next door and
//   comes back (FocusRescue.Park / Parked.Return), and the key box goes read-only, not disabled;
// - OCR studio copies say "(1 character)" instead of "(1 characters)".
public class Run279LiveStatusAndBusyFocusTests
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
    private const string Main = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string Settings = "MarkSmith.Desktop/Views/SettingsView.xaml.cs";
    private const string Ocr = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string Overlay = "MarkSmith.Desktop/Controls/OcrExperienceOverlay.xaml.cs";

    // ---- LiveAnnounce ----

    [Fact]
    public void LiveAnnounce_RaisesLiveRegionChanged_NotJustTheSetting()
    {
        var src = Read(Live);
        Assert.Contains("RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)", src);
        Assert.Contains("SetLiveSetting(target, AutomationLiveSetting.Polite)", src);
        Assert.Contains("RegisterPropertyChangedCallback(TextBlock.TextProperty", src);
    }

    [Fact]
    public void LiveAnnounce_CoalescesBursts_AndSkipsWithoutListeners()
    {
        var src = Read(Live);
        Assert.Contains("AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)", src);
        Assert.Contains("timer.Stop();", src);
        Assert.Contains("timer.IsRepeating = false;", src);
        Assert.Matches(@"TimeSpan\.FromMilliseconds\(250\)", src);
    }

    [Fact]
    public void LiveAnnounce_IgnoresBlankText_AndHiddenElements()
    {
        var src = Read(Live);
        Assert.Contains("!string.IsNullOrWhiteSpace(text)", src);
        Assert.Contains("element.Visibility != Visibility.Visible || element.XamlRoot is null", src);
    }

    [Theory]
    [InlineData(Main, "LiveAnnounce.Track(StatusMsgText)")]
    [InlineData(Main, "LiveAnnounce.Track(FindCountText, FindCountButton)")]
    [InlineData("MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "LiveAnnounce.Track(StatusBarText)")]
    [InlineData("MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml.cs", "LiveAnnounce.Track(StatusBarText)")]
    [InlineData(Ocr, "LiveAnnounce.Track(StatusBannerText)")]
    [InlineData(Ocr, "LiveAnnounce.Track(FooterStatusText)")]
    [InlineData("MarkSmith.Desktop/Views/SuiteHubView.xaml.cs", "LiveAnnounce.Track(TemplateSearchStatusText)")]
    [InlineData(Settings, "LiveAnnounce.Track(HouseStyleStatusText)")]
    [InlineData("MarkSmith.Desktop/Controls/CommandPalette.xaml.cs", "LiveAnnounce.Track(CountText)")]
    [InlineData(Settings, "LiveAnnounce.Raise(UpdateStatusBar)")]
    public void StatusLines_AreAnnounced(string file, string call)
    {
        Assert.Contains(call, Read(file));
    }

    [Theory]
    [InlineData(Main, "Services.LiveAnnounce.Track(")]
    [InlineData("MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "LiveAnnounce.Track(")]
    [InlineData("MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml.cs", "LiveAnnounce.Track(")]
    [InlineData(Ocr, "LiveAnnounce.Track(")]
    public void Tracking_StartsRightAfterInitializeComponent(string file, string call)
    {
        // Named elements exist only after InitializeComponent; tracking must follow it closely.
        var src = Read(file);
        var init = src.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        var track = src.IndexOf(call, StringComparison.Ordinal);
        Assert.True(init >= 0 && track > init && track - init < 400, $"{file}: Track at {track}, init at {init}");
    }

    // ---- FocusRescue.Park ----

    [Fact]
    public void Park_DoesNothingWhenTheButtonHadNoFocus()
    {
        var src = Read(Rescue);
        Assert.Contains("if (state == FocusState.Unfocused) return default;", src);
        Assert.Contains("public bool HadFocus => Spot is not null;", src);
    }

    [Fact]
    public void Park_TriesEachSpotInTurn_OnlyAvailableOnes()
    {
        var src = Read(Rescue);
        Assert.Contains("foreach (var spot in parkOn)", src);
        Assert.Contains("spot is not null && IsAvailable(spot) && spot.Focus(how)", src);
    }

    [Fact]
    public void Return_LeavesFocusAloneIfTheUserMovedOn()
    {
        var src = Read(Rescue);
        Assert.Contains("if (ReferenceEquals(focused, Spot) || FocusWasLost(Button)) Button.Focus(How);", src);
        Assert.Contains("!IsAvailable(Button)) return;", src);
    }

    [Fact]
    public void Park_KeepsPointerUsersFreeOfFocusRectangles()
    {
        Assert.Contains("state == FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard", Read(Rescue));
    }

    [Fact]
    public void Rescan_ParksOnTheFileList_BeforeDisabling_AndReturns()
    {
        var src = Read(Main);
        var park = src.IndexOf("FocusRescue.Park(RescanButton, MarkdownFilesCombo)", StringComparison.Ordinal);
        var disable = src.IndexOf("RescanButton.IsEnabled = false;", StringComparison.Ordinal);
        var enable = src.IndexOf("RescanButton.IsEnabled = true;", StringComparison.Ordinal);
        var ret = src.IndexOf("parked.Return();", enable, StringComparison.Ordinal);
        Assert.True(park > 0 && park < disable && disable < enable && ret > enable && ret - enable < 80);
    }

    [Theory]
    [InlineData("FocusRescue.Park(CheckButton, VersionButton)", "CheckButton.IsEnabled = false;")]
    [InlineData("FocusRescue.Park(DeactivateButton, BuyButton, StartTrialButton)", "DeactivateButton.IsEnabled = false;")]
    [InlineData("FocusRescue.Park(ActivateButton, KeyBox)", "ActivateButton.IsEnabled = false;")]
    public void SettingsBusyButtons_ParkBeforeDisabling(string park, string disable)
    {
        var src = Read(Settings);
        var p = src.IndexOf(park, StringComparison.Ordinal);
        var d = src.IndexOf(disable, p < 0 ? 0 : p, StringComparison.Ordinal);
        Assert.True(p > 0 && d > p && d - p < 120, $"{park} then {disable}");
    }

    [Fact]
    public void CheckForUpdates_NoLongerGrabsFocusAfterAMouseClick()
    {
        var src = Read(Settings);
        Assert.DoesNotContain("CheckButton.Focus(FocusState.Programmatic);", src);
        var enable = src.IndexOf("CheckButton.IsEnabled = true;", StringComparison.Ordinal);
        Assert.True(src.IndexOf("parked.Return();", enable, StringComparison.Ordinal) > enable);
    }

    [Fact]
    public void LicenseKeyBox_GoesReadOnly_NotDisabled_WhileActivating()
    {
        var src = Read(Settings);
        Assert.DoesNotContain("KeyBox.IsEnabled = false;", src);
        Assert.Contains("KeyBox.IsReadOnly = true;", src);
        Assert.Contains("KeyBox.IsReadOnly = false;", src);
        // Enter in the read-only box can't start a second activation: it checks the button.
        Assert.Contains("if (e.Key != Windows.System.VirtualKey.Enter || !ActivateButton.IsEnabled) return;", src);
    }

    // ---- OCR counts ----

    [Theory]
    [InlineData(Ocr)]
    [InlineData(Overlay)]
    public void OcrCopies_NoRawCharacterPlural(string file)
    {
        var src = Read(file);
        Assert.DoesNotMatch(new Regex(@"\{[A-Za-z]+\.Length:N0\} characters"), src);
    }

    [Fact]
    public void OcrCopies_UseCountText()
    {
        Assert.Equal(5, Regex.Matches(Read(Ocr), @"CountText\.Of\(\w+\.Length, ""character""\)").Count);
        Assert.Contains("CountText.Of(text.Length, \"character\")", Read(Overlay));
    }

    [Theory]
    [InlineData(1, "1 character")]
    [InlineData(2, "2 characters")]
    [InlineData(0, "0 characters")]
    public void CharacterCounts_ReadRight(int n, string expected)
    {
        Assert.Equal(expected, CountText.Of(n, "character"));
    }
}
