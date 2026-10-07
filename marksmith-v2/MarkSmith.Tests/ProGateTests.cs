using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

// Run #26: one set of words for every Pro gate, the trial offered wherever it unlocks something,
// the main export button following the license, and the gated action carrying on once unlocked.
public class ProGateCopyTests
{
    private static readonly LicenseState FreshFree = new() { Edition = Edition.Free };
    private static readonly LicenseState SpentFree = new() { Edition = Edition.Free, TrialUsed = true };
    private static readonly LicenseState Trial = new() { Edition = Edition.Trial, TrialExportsRemaining = 2 };
    private static readonly LicenseState Pro = new() { Edition = Edition.Pro };

    private static readonly FeatureId[] Gated = Enum.GetValues<FeatureId>()
        .Where(id => !FeatureClassifier.IsFree(id) && id != FeatureId.AdvancedStyling).ToArray();

    [Fact]
    public void The_Trial_Is_Offered_For_Every_Gated_Feature_Not_Just_Word()
    {
        // The trial is full Pro. The old dialog offered it for Word only, so a free user who
        // reached PowerPoint first was told to pay for something the trial would have unlocked.
        foreach (var id in Gated)
        {
            Assert.True(ProGate.OffersTrial(FreshFree));
            Assert.Contains("free trial", ProGate.StatusLine(id, FreshFree));
            Assert.Contains(ProGate.TrialSummary, string.Join(" ", ProGate.DialogParagraphs(id, FreshFree)));
        }
        Assert.False(ProGate.OffersTrial(SpentFree));
        Assert.DoesNotContain("Start the free trial", ProGate.StatusLine(FeatureId.PptxExport, SpentFree));
        Assert.Contains("trial has been used", string.Join(" ", ProGate.DialogParagraphs(FeatureId.PptxExport, SpentFree)));
    }

    [Fact]
    public void Every_Gated_Feature_Has_A_Plain_Name_And_A_Pitch()
    {
        foreach (var id in Gated)
        {
            var name = ProGate.FeatureName(id);
            Assert.DoesNotContain("DOCX", name);
            Assert.DoesNotContain("PPTX", name);
            Assert.False(string.IsNullOrWhiteSpace(ProGate.Pitch(id)), $"{id} has no pitch");
            Assert.StartsWith(name + " is part of MarkSmith Pro", ProGate.DialogTitle(id));
        }
    }

    [Fact]
    public void The_Free_Plan_Line_Names_Every_Free_Export()
    {
        // The old copy said "Markdown, PDF and HTML", although EPUB and email were free too.
        foreach (var format in new[] { "PDF", "web page", "EPUB", "Markdown", "email" })
            Assert.Contains(format, ProGate.FreePlanIncludes);
        foreach (var id in Gated)
            Assert.Equal(ProGate.FreePlanIncludes, ProGate.DialogParagraphs(id, FreshFree)[^1]);
    }

    [Fact]
    public void Status_Lines_Use_Full_Stops_Not_Hyphen_Dashes()
    {
        foreach (var id in Gated)
        foreach (var st in new[] { FreshFree, SpentFree })
        {
            var line = ProGate.StatusLine(id, st);
            Assert.Contains("MarkSmith Pro feature", line); // pinned by the batch licensing tests too
            Assert.DoesNotContain(" - ", line);
            Assert.EndsWith(".", line);
        }
    }

    [Fact]
    public void The_Main_Export_Button_Is_Pdf_On_Free_And_Word_Otherwise()
    {
        Assert.False(ProGate.PrimaryExportIsWord(FreshFree));
        Assert.False(ProGate.PrimaryExportIsWord(SpentFree));
        Assert.True(ProGate.PrimaryExportIsWord(Trial));
        Assert.True(ProGate.PrimaryExportIsWord(Pro));
        Assert.Equal("Generate PDF (.pdf)", ProGate.PrimaryExportLabel(FreshFree));
        Assert.Equal("Generate Word (.docx)", ProGate.PrimaryExportLabel(Pro));
    }

    [Fact]
    public void Menu_Tags_Mark_Only_What_The_License_Cant_Run()
    {
        Assert.Equal("Pro · Ctrl+Shift+D", ProGate.MenuTag(FeatureId.DocxExport, FreshFree, "Ctrl+Shift+D"));
        Assert.Equal("Pro", ProGate.MenuTag(FeatureId.DocxExport, FreshFree, ""));
        Assert.Equal("Ctrl+Shift+D", ProGate.MenuTag(FeatureId.DocxExport, Trial, "Ctrl+Shift+D"));
        Assert.Equal("Ctrl+Shift+T", ProGate.MenuTag(FeatureId.PptxExport, Pro, "Ctrl+Shift+T"));
        Assert.Equal("", ProGate.MenuTag(FeatureId.EmailDraft, FreshFree, ""));
    }

    [Fact]
    public void Banner_Is_A_Counter_During_The_Trial_And_An_Offer_On_Free()
    {
        var (title, message, action) = ProGate.Banner(Trial);
        Assert.Equal("Pro trial · 2 Word exports left", title);
        Assert.Null(action);
        Assert.Contains("Everything in Pro", message);
        Assert.Equal("Pro trial · 1 Word export left", ProGate.Banner(new LicenseState { Edition = Edition.Trial, TrialExportsRemaining = 1 }).Title);

        Assert.Equal(ProGate.StartTrialLabel, ProGate.Banner(FreshFree).Action);
        Assert.Equal(ProGate.BuyLabel, ProGate.Banner(SpentFree).Action);
        Assert.Contains("EPUB", ProGate.Banner(FreshFree).Message);
    }
}

public class ZoomStepsTests
{
    [Theory]
    [InlineData(1.0, 1.1)]
    [InlineData(1.1, 1.25)]
    [InlineData(1.25, 1.5)]
    [InlineData(1.37, 1.5)]   // a fitted scale snaps onto the ladder
    [InlineData(0.999, 1.1)]  // rounding noise counts as the stop it's next to
    [InlineData(4.0, 4.0)]
    public void Next_Moves_Up_To_The_Next_Stop(double from, double expected) =>
        Assert.Equal(expected, ZoomSteps.Next(from));

    [Theory]
    [InlineData(1.0, 0.9)]
    [InlineData(1.37, 1.25)]
    [InlineData(1.71, 1.5)]   // run #21b: five clicks used to go 171% to 151%
    [InlineData(0.26, 0.25)]
    [InlineData(0.25, 0.25)]
    public void Previous_Moves_Down_To_The_Previous_Stop(double from, double expected) =>
        Assert.Equal(expected, ZoomSteps.Previous(from));

    [Fact]
    public void The_Ladder_Is_Sorted_And_Includes_100_Percent_And_The_Fit_Cap()
    {
        Assert.Equal(ZoomSteps.Stops.OrderBy(s => s), ZoomSteps.Stops);
        Assert.Contains(1.0, ZoomSteps.Stops);
        Assert.Contains(ZoomSteps.FitMax, ZoomSteps.Stops);
        Assert.False(ZoomSteps.CanZoomIn(ZoomSteps.Max));
        Assert.False(ZoomSteps.CanZoomOut(ZoomSteps.Min));
        Assert.True(ZoomSteps.CanZoomIn(1.0) && ZoomSteps.CanZoomOut(1.0));
    }

    [Fact]
    public void The_Preview_Script_Fits_No_Larger_Than_The_Reading_Cap()
    {
        // The fit-width script is a JS literal, so it can't read ZoomSteps; pin the two together.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "MarkSmith.Core", "Services", "MarkdownHtmlService.cs"));
        Assert.Contains($"FIT_MAX = {ZoomSteps.FitMax.ToString(System.Globalization.CultureInfo.InvariantCulture)},", src);
        Assert.Contains($"ZOOM_MIN = {ZoomSteps.Min.ToString(System.Globalization.CultureInfo.InvariantCulture)}, ZOOM_MAX = {ZoomSteps.Max.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)};", src);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here))!;
}

[Collection("LicenseState")]
public class ProGateResumeTests : IDisposable
{
    private readonly string _licensePath = Path.Combine(AppPaths.ConfigDir, "license.json");
    private readonly string? _backup;

    public ProGateResumeTests() =>
        _backup = File.Exists(_licensePath) ? File.ReadAllText(_licensePath) : null;

    public void Dispose()
    {
        try
        {
            if (_backup is null) { if (File.Exists(_licensePath)) File.Delete(_licensePath); }
            else File.WriteAllText(_licensePath, _backup);
            AppServices.License.Load();
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Unlocking_From_The_Gate_Carries_On_With_What_The_User_Was_Doing()
    {
        AppServices.License.ResetToFree();
        var vm = new MainViewModel();
        vm.WatchFolderEnabled = false;
        vm.WatchFolderEnabled = true; // refused on Free
        Assert.False(vm.WatchFolderEnabled);
        Assert.Contains("Start the free trial", vm.StatusText);

        AppServices.License.ToggleDevPro(); // stands in for "Start free trial" in the dialog
        await vm.ResumeAfterUnlockAsync();
        Assert.True(vm.WatchFolderEnabled);

        // One shot: a second call doesn't replay it.
        vm.WatchFolderEnabled = false;
        await vm.ResumeAfterUnlockAsync();
        Assert.False(vm.WatchFolderEnabled);
    }
}
