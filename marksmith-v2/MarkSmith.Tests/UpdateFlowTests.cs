using System.Runtime.CompilerServices;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

// The in-app update flow (MainViewModel.Updates.cs): download while working, save, hand over to the
// installer and close, come back with the document. The parts that decide what happens and what
// it's called are pure and tested here; the installer itself only runs in a real update.
public sealed class UpdateFlowTests
{
    private static UpdateBannerText Say(UpdatePhase phase, double percent = 0, string? failure = null, bool restarts = true) =>
        MainViewModel.DescribeUpdate(phase, "3.13.0", "3.12.0", percent, failure, restarts);

    [Fact]
    public void Every_phase_but_none_has_a_title_and_a_message()
    {
        foreach (var phase in Enum.GetValues<UpdatePhase>().Where(p => p != UpdatePhase.None))
        {
            var text = Say(phase);
            Assert.False(string.IsNullOrWhiteSpace(text.Title), phase.ToString());
            Assert.False(string.IsNullOrWhiteSpace(text.Message), phase.ToString());
        }
        Assert.Equal("", Say(UpdatePhase.None).Title);
    }

    [Fact]
    public void The_offer_promises_a_restart_only_when_the_setting_does()
    {
        Assert.Equal("Install and restart", Say(UpdatePhase.Available, restarts: true).ActionLabel);
        Assert.Contains("restarts", Say(UpdatePhase.Available, restarts: true).Message);
        Assert.Equal("Download", Say(UpdatePhase.Available, restarts: false).ActionLabel);
        Assert.Contains("you choose when", Say(UpdatePhase.Available, restarts: false).Message);
        Assert.Contains("3.12.0", Say(UpdatePhase.Available).Message); // what you have now
        Assert.Contains("3.13.0", Say(UpdatePhase.Available).Title);
    }

    [Theory]
    [InlineData(0, "Starting the download…")]
    [InlineData(0.4, "Starting the download…")]
    [InlineData(42.7, "43% downloaded. You can keep working.")]
    [InlineData(100, "100% downloaded. You can keep working.")]
    [InlineData(130, "100% downloaded. You can keep working.")]
    public void Download_progress_reads_in_whole_percents(double percent, string message)
    {
        var text = Say(UpdatePhase.Downloading, percent);
        Assert.Equal(message, text.Message);
        Assert.Equal("Cancel", text.ActionLabel);
    }

    [Fact]
    public void Installing_offers_nothing_to_press_and_failures_say_why()
    {
        Assert.Null(Say(UpdatePhase.Installing).ActionLabel);
        Assert.Null(Say(UpdatePhase.Installed).ActionLabel);
        Assert.Equal("Restart and install", Say(UpdatePhase.ReadyToInstall).ActionLabel);
        Assert.Equal("The signature didn't match.", Say(UpdatePhase.Failed, failure: "The signature didn't match.").Message);
        Assert.Contains("releases page", Say(UpdatePhase.Failed, failure: null).Message);
        Assert.Equal("Try again", Say(UpdatePhase.Failed).ActionLabel);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)] // the setting is off: wait for "Restart and install"
    [InlineData(true, true, true, false)]   // someone has started typing
    [InlineData(true, false, false, false)] // a studio or Version History is open
    public void An_update_found_at_launch_restarts_only_if_nobody_is_working(bool setting, bool edited, bool nothingElse, bool expected) =>
        Assert.Equal(expected, MainViewModel.MayRestartUnattended(setting, edited, nothingElse));

    [Fact]
    public void Installer_arguments_are_silent_and_only_relaunch_when_asked()
    {
        var plain = UpdateService.InstallerArguments(relaunch: false, logPath: null);
        Assert.Contains("/VERYSILENT", plain);
        Assert.Contains("/SUPPRESSMSGBOXES", plain);
        Assert.Contains("/CLOSEAPPLICATIONS", plain);
        Assert.DoesNotContain("/RELAUNCH", plain);
        Assert.DoesNotContain("/LOG", plain);

        var app = UpdateService.InstallerArguments(relaunch: true, logPath: @"C:\Users\A B\update-install.log");
        Assert.Contains("/RELAUNCH=1", app);
        Assert.Contains("/LOG=\"C:\\Users\\A B\\update-install.log\"", app); // quoted: the path has a space
    }

    [Fact]
    public void Starting_a_missing_installer_fails_with_a_reason_and_starts_nothing()
    {
        var updates = new UpdateService();
        Assert.False(updates.StartInstaller(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), relaunch: true, logPath: null));
        Assert.Contains("gone missing", updates.LastFailureReason);
    }

    [Fact]
    public void The_installer_reopens_the_app_only_for_the_in_app_updater()
    {
        var iss = File.ReadAllText(InstallerScript());
        // The [Run] entry that reopens MarkSmith checks /RELAUNCH=1 and drops the installer's
        // elevation; the interactive "Launch Marksmith" checkbox still skips silent installs.
        Assert.Matches(@"Flags: nowait runasoriginaluser; Check: RelaunchRequested", iss);
        Assert.Contains("{param:RELAUNCH|0}", iss);
        Assert.Contains("WizardSilent", iss);
        Assert.Contains("postinstall skipifsilent", iss);
    }

    private static string InstallerScript([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "packaging", "installer", "marksmith.iss");

    // ---- UpdateResumeNote ----

    private static string TempConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-update-note-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_resume_note_is_read_once_then_gone()
    {
        var dir = TempConfig();
        var now = new DateTime(2026, 10, 10, 1, 0, 0, DateTimeKind.Utc);
        new UpdateResumeNote("3.12.0.0", "3.13.0", @"C:\docs\plan.md", "https://example.test/r", now).Save(dir);

        var note = UpdateResumeNote.Take(dir, now.AddMinutes(2));
        Assert.NotNull(note);
        Assert.Equal("3.13.0", note!.ToVersion);
        Assert.Equal(@"C:\docs\plan.md", note.DocumentPath);
        Assert.False(File.Exists(UpdateResumeNote.PathIn(dir)));
        Assert.Null(UpdateResumeNote.Take(dir, now.AddMinutes(3)));
    }

    [Fact]
    public void Stale_or_damaged_notes_are_ignored_and_removed()
    {
        var dir = TempConfig();
        var then = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        new UpdateResumeNote("3.12.0", "3.13.0", null, "", then).Save(dir);
        Assert.Null(UpdateResumeNote.Take(dir, then + UpdateResumeNote.MaxAge + TimeSpan.FromMinutes(1)));
        Assert.False(File.Exists(UpdateResumeNote.PathIn(dir)));

        File.WriteAllText(UpdateResumeNote.PathIn(dir), "{ not json");
        Assert.Null(UpdateResumeNote.Take(dir, then));
        Assert.False(File.Exists(UpdateResumeNote.PathIn(dir)));
    }

    [Theory]
    [InlineData("3.13.0", "3.13.0.0", true)]
    [InlineData("v3.13.0", "3.13.0", true)]
    [InlineData("3.13.0", "3.14.0", true)]
    [InlineData("3.13.0", "3.12.0.0", false)] // the installer didn't finish: still on the old one
    public void The_note_knows_whether_the_new_version_is_running(string to, string running, bool installed) =>
        Assert.Equal(installed, new UpdateResumeNote("3.12.0", to, null, "", DateTime.UtcNow).InstalledIn(running));
}
