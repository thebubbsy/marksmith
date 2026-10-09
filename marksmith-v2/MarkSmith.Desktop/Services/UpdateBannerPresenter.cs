using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MarkSmith.ViewModels;

namespace MarkSmith.Services;

/// <summary>
/// Draws the in-app update state onto an InfoBar: the main window's banner and Settings ▸ About
/// both go through here, so the two always say the same thing. The words come from
/// <see cref="MainViewModel.DescribeUpdate"/>; this only decides how they look.
/// </summary>
public static class UpdateBannerPresenter
{
    public static void Apply(InfoBar bar, Button action, ProgressBar progress, HyperlinkButton notes, MainViewModel vm)
    {
        var phase = vm.UpdatePhase;
        var text = vm.CurrentUpdateText;
        bar.Title = text.Title;
        bar.Message = text.Message;
        bar.Severity = phase switch
        {
            UpdatePhase.Failed => InfoBarSeverity.Error,
            UpdatePhase.ReadyToInstall or UpdatePhase.Installed => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational,
        };
        // A download or install in flight carries on whatever happens to the banner, so it can't
        // be closed and come back later looking like nothing is happening.
        bar.IsClosable = phase is not (UpdatePhase.Downloading or UpdatePhase.Installing);

        action.Content = text.ActionLabel;
        action.Visibility = text.ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        // Cancel is a way out, not the thing to do next, so it isn't drawn as the accent button.
        action.Style = (Style)Application.Current.Resources[phase == UpdatePhase.Downloading ? "DefaultButtonStyle" : "AccentButtonStyle"];
        ToolTipService.SetToolTip(action, phase switch
        {
            UpdatePhase.Available => vm.AutoRestartAfterUpdate
                ? "Download the update, then restart MarkSmith to install it"
                : "Download the update; you choose when to restart",
            UpdatePhase.Downloading => "Stop the download",
            UpdatePhase.ReadyToInstall => "Save everything, close MarkSmith and install. It reopens with your document.",
            UpdatePhase.Failed => "Try the update again",
            _ => null,
        });

        progress.Visibility = phase is UpdatePhase.Downloading or UpdatePhase.Installing ? Visibility.Visible : Visibility.Collapsed;
        // No percentage until the first bytes arrive, and none while the installer starts.
        progress.IsIndeterminate = phase == UpdatePhase.Installing || vm.UpdateDownloadProgress < 1;
        progress.Value = Math.Clamp(vm.UpdateDownloadProgress, 0, 100);

        var url = vm.UpdateReleaseUrl;
        var showNotes = !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _)
            && phase is UpdatePhase.Available or UpdatePhase.ReadyToInstall or UpdatePhase.Installed or UpdatePhase.Failed;
        notes.Visibility = showNotes ? Visibility.Visible : Visibility.Collapsed;
        if (showNotes)
        {
            notes.NavigateUri = new Uri(url);
            notes.Content = phase switch
            {
                UpdatePhase.Installed => $"What's new in {vm.UpdateVersionLabel}",
                UpdatePhase.Failed => "Download from the releases page",
                _ => "Read the release notes",
            };
        }

        bar.IsOpen = phase != UpdatePhase.None;
    }
}
