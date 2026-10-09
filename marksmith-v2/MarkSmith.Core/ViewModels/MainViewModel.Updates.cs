using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Services;

namespace MarkSmith.ViewModels;

/// <summary>Where an in-app update has got to. The update banner and Settings ▸ About both draw
/// themselves from this one value, so they can't disagree about what is happening.</summary>
public enum UpdatePhase
{
    /// <summary>Nothing to show: up to date, not checked, or dismissed.</summary>
    None,
    Available,
    Downloading,
    /// <summary>Downloaded and verified. Installing means closing MarkSmith, so it waits for a
    /// restart (or goes straight on when nobody has started working yet).</summary>
    ReadyToInstall,
    /// <summary>Saving and handing over to the installer; MarkSmith is about to close.</summary>
    Installing,
    Failed,
    /// <summary>Shown after the restart: the update went in.</summary>
    Installed,
}

/// <summary>The words an update banner shows for one phase.</summary>
public sealed record UpdateBannerText(string Title, string Message, string? ActionLabel);

// The in-app updater. The window does the drawing (MainWindow's UpdateBanner and SettingsView's
// About page, both through UpdateBannerPresenter); this decides what happens and what it's called.
//
// How an update goes in: the installer can't replace the files of a running app, so MarkSmith
// downloads and checks it while you work, then saves everything, starts the installer and closes.
// The installer reopens MarkSmith (/RELAUNCH=1), which finds the UpdateResumeNote it left, puts the
// document back without asking, and says whether the new version is the one running. The rule it
// keeps: MarkSmith never closes while work is only in memory, and never closes under someone who
// is using it unless they asked.
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private double _updateDownloadProgress;
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private string _latestUpdateTag = "";
    [ObservableProperty] private string _updateDownloadUrl = "";
    [ObservableProperty] private string _updateReleaseUrl = "";
    [ObservableProperty] private UpdatePhase _updatePhase;

    /// <summary>The failure sentence for <see cref="UpdatePhase.Failed"/>.</summary>
    public string? UpdateFailure { get; private set; }

    /// <summary>Set by the main window: writes everything that would be lost if the process ended
    /// now (the recovery copy of the editor, the undo history, settings).</summary>
    public Action? SaveWorkBeforeExit { get; set; }

    /// <summary>Set by the main window: closes the app for the installer, past the minimise-to-tray
    /// and session-log prompts that a normal close would stop at.</summary>
    public Action? ExitForUpdate { get; set; }

    /// <summary>Set by the main window: true while no other MarkSmith window (a studio, version
    /// history) is open, so an unattended restart wouldn't close something under someone.</summary>
    public Func<bool>? NothingElseOpen { get; set; }

    /// <summary>True once the editor text has changed in this session, typed or loaded.</summary>
    public bool HasEditedSinceLaunch { get; private set; }

    private CancellationTokenSource? _updateCts;
    private string? _stagedInstaller;
    private string? _stagedInstallerUrl;

    /// <summary>The release's version without the tag's "v", for sentences.</summary>
    public string UpdateVersionLabel => LatestUpdateTag.TrimStart('v', 'V');

    /// <summary>What the banner says in each phase. Pure, so it can be tested.</summary>
    /// <param name="restartsWhenDownloaded">The "Restart to install as soon as it downloads"
    /// setting: decides whether the Available button promises a restart.</param>
    public static UpdateBannerText DescribeUpdate(UpdatePhase phase, string version, string currentVersion,
        double percent, string? failure, bool restartsWhenDownloaded) => phase switch
    {
        UpdatePhase.Available => new($"MarkSmith {version} is available",
            restartsWhenDownloaded
                ? $"You have {currentVersion}. It downloads while you work, then MarkSmith restarts to install it and your document comes back."
                : $"You have {currentVersion}. It downloads while you work, then you choose when to restart and install it.",
            restartsWhenDownloaded ? "Install and restart" : "Download"),
        UpdatePhase.Downloading => new($"Downloading MarkSmith {version}",
            percent >= 1 ? $"{Math.Min(percent, 100):F0}% downloaded. You can keep working." : "Starting the download…", "Cancel"),
        UpdatePhase.ReadyToInstall => new($"MarkSmith {version} is ready to install",
            "Restart to install it. It takes about a minute, Windows asks for permission, and your document comes back when MarkSmith reopens.",
            "Restart and install"),
        UpdatePhase.Installing => new($"Installing MarkSmith {version}",
            "Saving your work and starting the installer. Choose Yes if Windows asks for permission.", null),
        UpdatePhase.Failed => new("The update didn't install",
            string.IsNullOrWhiteSpace(failure) ? "Something went wrong. Try again, or download it from the releases page." : failure!,
            "Try again"),
        UpdatePhase.Installed => new($"Updated to MarkSmith {version}",
            "Everything you had open is back. See what's new in this version.", null),
        _ => new("", "", null),
    };

    /// <summary>Whether an update that downloaded by itself at launch may go straight on and
    /// restart: only when the setting allows it, nobody has typed or opened anything, and no other
    /// MarkSmith window is open.</summary>
    public static bool MayRestartUnattended(bool restartSetting, bool hasEdited, bool nothingElseOpen) =>
        restartSetting && !hasEdited && nothingElseOpen;

    /// <summary>The banner text for the current state.</summary>
    public UpdateBannerText CurrentUpdateText => DescribeUpdate(UpdatePhase, UpdateVersionLabel,
        AppServices.Updates.CurrentDisplayVersion, UpdateDownloadProgress, UpdateFailure, AutoRestartAfterUpdate);

    private void ShowUpdatePhase(UpdatePhase phase, string? failure = null)
    {
        UpdateFailure = phase == UpdatePhase.Failed ? failure : null;
        IsDownloadingUpdate = phase is UpdatePhase.Downloading or UpdatePhase.Installing;
        IsUpdateReady = phase == UpdatePhase.ReadyToInstall;
        IsUpdateAvailable = phase != UpdatePhase.None;
        UpdateStatusText = DescribeUpdate(phase, UpdateVersionLabel, AppServices.Updates.CurrentDisplayVersion,
            UpdateDownloadProgress, failure, AutoRestartAfterUpdate).Message;
        // Raised last and always, so a redraw sees every field above already updated (a progress
        // tick re-shows the same phase and still needs the banner redrawn).
        if (UpdatePhase == phase) OnPropertyChanged(nameof(UpdatePhase));
        else UpdatePhase = phase;
    }

    /// <summary>Takes the result of a check (startup or Settings ▸ Check for updates) and, when it
    /// found something, opens the banner. Doesn't step back over a download already under way.</summary>
    public void OfferUpdate(UpdateService.Result result)
    {
        if (!result.UpdateAvailable) return;
        if (UpdatePhase is UpdatePhase.Downloading or UpdatePhase.Installing) return;
        if (UpdatePhase == UpdatePhase.ReadyToInstall && result.DownloadUrl == _stagedInstallerUrl) return;
        LatestUpdateTag = result.LatestTag;
        UpdateDownloadUrl = result.DownloadUrl;
        UpdateReleaseUrl = result.ReleaseUrl;
        ShowUpdatePhase(UpdatePhase.Available);
    }

    /// <summary>Settings ▸ About's button: asks the releases feed now and opens the banner when
    /// there's something newer.</summary>
    public async Task<UpdateService.Result> CheckForUpdatesNowAsync()
    {
        var result = await AppServices.Updates.CheckAsync();
        OfferUpdate(result);
        return result;
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var res = await AppServices.Updates.CheckAsync();
            if (!res.UpdateAvailable) return;
            OfferUpdate(res);
            if (AutoInstallUpdatesOnLaunch && !string.IsNullOrEmpty(UpdateDownloadUrl))
                await InstallUpdateAsync(unattended: true);
        }
        catch { /* a failed background check stays silent; Settings ▸ About reports its own */ }
    }

    /// <summary>The banner's action button: whatever the current phase offers.</summary>
    [RelayCommand]
    public async Task UpdateBannerActionAsync()
    {
        switch (UpdatePhase)
        {
            case UpdatePhase.Available:
            case UpdatePhase.Failed:
                await InstallUpdateAsync(unattended: false);
                break;
            case UpdatePhase.Downloading:
                _updateCts?.Cancel();
                break;
            case UpdatePhase.ReadyToInstall:
                HandOverToInstaller();
                break;
        }
    }

    /// <summary>Kept for older bindings: install the offered update.</summary>
    [RelayCommand]
    public Task DownloadAndApplyUpdateAsync() => InstallUpdateAsync(unattended: false);

    private async Task InstallUpdateAsync(bool unattended)
    {
        if (UpdatePhase is UpdatePhase.Downloading or UpdatePhase.Installing) return;
        if (string.IsNullOrEmpty(UpdateDownloadUrl))
        {
            ShowUpdatePhase(UpdatePhase.Failed, "This release has no installer for this PC. Download it from the releases page.");
            return;
        }

        // A retry after a declined permission prompt doesn't download the same installer again.
        if (_stagedInstaller is null || _stagedInstallerUrl != UpdateDownloadUrl || !File.Exists(_stagedInstaller))
        {
            _updateCts?.Dispose();
            _updateCts = new CancellationTokenSource();
            var token = _updateCts.Token;

            UpdateDownloadProgress = 0;
            ShowUpdatePhase(UpdatePhase.Downloading);
            var lastShown = -1;
            var progress = new Progress<double>(p =>
            {
                if (UpdatePhase != UpdatePhase.Downloading) return;
                // Whole percents only: the download reports every 64 KB, far faster than anyone reads.
                if ((int)p == lastShown) return;
                lastShown = (int)p;
                UpdateDownloadProgress = p;
                ShowUpdatePhase(UpdatePhase.Downloading);
            });

            string? setupPath;
            try
            {
                setupPath = await AppServices.Updates.DownloadInstallerAsync(UpdateDownloadUrl, progress, token);
            }
            catch (Exception ex)
            {
                ShowUpdatePhase(UpdatePhase.Failed, $"The update couldn't download: {ex.Message}");
                return;
            }

            if (setupPath is null)
            {
                // Cancel puts the offer back as it was; anything else says what went wrong.
                if (token.IsCancellationRequested) ShowUpdatePhase(UpdatePhase.Available);
                else ShowUpdatePhase(UpdatePhase.Failed, AppServices.Updates.LastFailureReason);
                return;
            }
            _stagedInstaller = setupPath;
            _stagedInstallerUrl = UpdateDownloadUrl;
        }

        UpdateDownloadProgress = 100;
        ShowUpdatePhase(UpdatePhase.ReadyToInstall);

        // Someone who pressed "Install and restart" asked for the restart. One that downloaded by
        // itself at launch only goes on if nobody has started working yet; otherwise it waits in
        // the banner for "Restart and install".
        var goNow = unattended
            ? MayRestartUnattended(AutoRestartAfterUpdate, HasEditedSinceLaunch, NothingElseOpen?.Invoke() ?? true)
            : AutoRestartAfterUpdate;
        if (goNow) HandOverToInstaller();
    }

    /// <summary>Saves everything, starts the installer (which reopens MarkSmith when it's done)
    /// and closes. If the installer never starts (permission declined), nothing closes.</summary>
    private void HandOverToInstaller()
    {
        if (_stagedInstaller is null || !File.Exists(_stagedInstaller))
        {
            _stagedInstaller = null;
            ShowUpdatePhase(UpdatePhase.Failed, "The downloaded installer has gone missing. Try again to download it.");
            return;
        }

        ShowUpdatePhase(UpdatePhase.Installing);
        try { SaveWorkBeforeExit?.Invoke(); }
        catch { /* the recovery copy is written continuously too; never strand someone on an old version */ }

        var configDir = AppPaths.ConfigDir;
        var note = new UpdateResumeNote(AppServices.Updates.CurrentVersion, UpdateVersionLabel,
            UsePasteSource ? null : InputFilePath, UpdateReleaseUrl, DateTime.UtcNow);
        try { note.Save(configDir); } catch { /* without it the next launch just asks about the draft */ }

        if (!AppServices.Updates.StartInstaller(_stagedInstaller, relaunch: true, UpdateResumeNote.InstallLogIn(configDir)))
        {
            try { File.Delete(UpdateResumeNote.PathIn(configDir)); } catch { }
            ShowUpdatePhase(UpdatePhase.Failed, AppServices.Updates.LastFailureReason);
            return;
        }

        if (ExitForUpdate is { } exit) exit();
        else Environment.Exit(0);
    }

    /// <summary>Called once at launch, before the window asks about recovered drafts. Takes the
    /// note an update restart left and shows how it went. Returns the note so the window can put
    /// the document back without asking; null when this launch isn't the end of an update.</summary>
    public UpdateResumeNote? ResumeAfterUpdate()
    {
        var note = UpdateResumeNote.Take(AppPaths.ConfigDir, DateTime.UtcNow);
        if (note is null) return null;
        LatestUpdateTag = note.ToVersion;
        UpdateReleaseUrl = note.ReleaseUrl;
        if (note.InstalledIn(AppServices.Updates.CurrentVersion))
        {
            ShowUpdatePhase(UpdatePhase.Installed);
        }
        else
        {
            var log = UpdateResumeNote.InstallLogIn(AppPaths.ConfigDir);
            ShowUpdatePhase(UpdatePhase.Failed,
                $"The installer didn't finish, so you still have {AppServices.Updates.CurrentDisplayVersion}. " +
                (File.Exists(log) ? $"Its log is at {log}. " : "") +
                "Try again, or download it from the releases page.");
        }
        return note;
    }

    /// <summary>Restarts MarkSmith after saving what would otherwise be lost.</summary>
    [RelayCommand]
    public void RelaunchNow()
    {
        try { SaveWorkBeforeExit?.Invoke(); }
        catch { /* never let a failed save keep someone from restarting */ }
        UpdateService.RelaunchApplication();
    }

    /// <summary>The banner's close button. A running download or install carries on; closing
    /// only hides what can safely be hidden. A downloaded update stays downloaded and is offered
    /// again by the next check.</summary>
    [RelayCommand]
    public void DismissUpdateBanner()
    {
        if (UpdatePhase is UpdatePhase.Downloading or UpdatePhase.Installing) return;
        ShowUpdatePhase(UpdatePhase.None);
    }

    private void NoteEditorChangedForUpdates() => HasEditedSinceLaunch = true;
}
