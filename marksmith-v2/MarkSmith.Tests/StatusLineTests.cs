using MarkSmith.Models;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// The status bar: a message that never set a severity inherited the last one ("SmartArt Studio
/// opened." in success green), and routine notes stayed for the rest of the session.
/// </summary>
public class StatusLineTests
{
    [Fact]
    public void A_new_message_starts_neutral()
    {
        var vm = new MainViewModel();
        vm.StatusText = "Export failed.";
        vm.StatusSeverity = StatusSeverity.Error;

        vm.StatusText = "SmartArt Studio opened.";

        Assert.Equal(StatusSeverity.Informational, vm.StatusSeverity);
    }

    [Fact]
    public void Severity_set_after_the_text_sticks()
    {
        var vm = new MainViewModel();
        vm.StatusText = "Saved.";
        vm.StatusSeverity = StatusSeverity.Success;

        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
    }

    [Fact]
    public void Routine_notes_fade_but_warnings_and_errors_stay()
    {
        var vm = new MainViewModel();
        vm.StatusText = "Copied as email.";
        vm.StatusSeverity = StatusSeverity.Success;
        Assert.True(vm.StatusFadesAway);

        vm.StatusText = "No text found in scan.png.";
        vm.StatusSeverity = StatusSeverity.Warning;
        Assert.False(vm.StatusFadesAway);

        vm.StatusText = "Couldn't open a.md: access denied";
        vm.StatusSeverity = StatusSeverity.Error;
        Assert.False(vm.StatusFadesAway);
    }

    [Theory]
    [InlineData("Importing report.pdf…")]
    [InlineData("Reading page 3 of 9...")]
    [InlineData(MainViewModel.ReadyStatus)]
    public void Progress_lines_and_ready_never_fade(string text)
    {
        var vm = new MainViewModel();
        vm.StatusText = text;
        Assert.False(vm.StatusFadesAway);
    }

    [Fact]
    public void A_finished_export_keeps_its_open_links()
    {
        var vm = new MainViewModel();
        vm.AnnounceExport("PDF saved: a.pdf · in C:\\out", @"C:\out\a.pdf");
        vm.StatusSeverity = StatusSeverity.Success;

        Assert.True(vm.HasStatusOutput);
        Assert.False(vm.StatusFadesAway);
    }

    [Fact]
    public void Fading_only_replaces_the_message_it_was_armed_for()
    {
        var vm = new MainViewModel();
        vm.StatusText = "Theme applied.";
        vm.StatusText = "Added 2 images to the document.";

        vm.FadeStatus("Theme applied.");
        Assert.Equal("Added 2 images to the document.", vm.StatusText);

        vm.FadeStatus("Added 2 images to the document.");
        Assert.Equal(MainViewModel.ReadyStatus, vm.StatusText);
    }
}
