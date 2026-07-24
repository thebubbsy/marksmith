using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdToPdf.Models;
using MdToPdf.Plugins;
using MdToPdf.Services;

namespace MdToPdf.ViewModels;

public partial class PluginViewModel : ObservableObject
{
    private readonly IMarksmithPlugin _plugin;
    private readonly Action _onPluginsChanged;

    public PluginViewModel(IMarksmithPlugin plugin, Action onPluginsChanged)
    {
        _plugin = plugin;
        _onPluginsChanged = onPluginsChanged;
        RefreshState();
    }

    public string Name => _plugin.Name;
    public string Description => _plugin.Description;
    
    public string CodeBlocks => _plugin is IDiagramPlugin diagram 
        ? "Code blocks: " + string.Join(", ", diagram.FenceLanguages.Select(l => "```" + l)) 
        : string.Empty;
        
    public bool HasCodeBlocks => _plugin is IDiagramPlugin;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private bool _showTick;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private bool _isInstalled;

    public bool CanInstall => !IsInstalled && !IsInstalling;
    public bool CanRemove => IsInstalled && !IsInstalling;

    private void RefreshState()
    {
        IsInstalled = _plugin.State == PluginInstallState.Installed;
        if (IsInstalled && string.IsNullOrEmpty(StatusText)) 
        {
            StatusText = "Installed.";
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        IsInstalling = true;
        ShowTick = false;
        StatusText = "Downloading…";

        var lastPercent = -1;
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            if (percent == lastPercent) return;
            lastPercent = percent;
            // The dispatcher context is captured by IProgress
            StatusText = $"Downloading… {percent}%";
        });

        bool ok = false;
        try
        {
            await _plugin.InstallAsync(progress, CancellationToken.None);
            StatusText = "Downloading… 100%";
            ok = true;
        }
        catch (Exception ex)
        {
            StatusText = $"Install failed: {ex.Message}";
        }

        IsInstalling = false;
        RefreshState();

        if (ok)
        {
            StatusText = "Done — installed.";
            ShowTick = true;
            _onPluginsChanged?.Invoke();
        }
    }

    [RelayCommand]
    private void Remove()
    {
        _plugin.Uninstall();
        ShowTick = false;
        StatusText = "Removed.";
        RefreshState();
        _onPluginsChanged?.Invoke();
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly LicenseService _licenseService = AppServices.License;
    private readonly UpdateService _updateService = AppServices.Updates;
    private readonly PluginManager _pluginManager = AppServices.Plugins;

    public ObservableCollection<PluginViewModel> Plugins { get; } = new();

    [ObservableProperty]
    private string _licenseKey = string.Empty;

    [ObservableProperty]
    private string _licenseStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isActivating;

    [ObservableProperty]
    private string _updateStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string _downloadUrl = string.Empty;

    [ObservableProperty]
    private string _pluginWarnings = string.Empty;

    public string VersionText => $"Version {_updateService.CurrentVersion}";
    
    public bool HasDownloadUrl => !string.IsNullOrEmpty(DownloadUrl);
    public bool HasLicenseStatus => !string.IsNullOrEmpty(LicenseStatusMessage);
    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatusMessage);
    public bool HasPluginWarnings => !string.IsNullOrEmpty(PluginWarnings);

    public bool CanRemoveLicense => _licenseService.State.Edition == Edition.Pro;
    public string EditionStatus => _licenseService.State.Status ?? "Free";

    public event Action? PluginsChanged;

    public SettingsViewModel()
    {
        _licenseService.Changed += OnLicenseChanged;
        LoadPlugins();
    }

    private void OnLicenseChanged()
    {
        OnPropertyChanged(nameof(CanRemoveLicense));
        OnPropertyChanged(nameof(EditionStatus));
    }

    private void LoadPlugins()
    {
        Plugins.Clear();
        foreach (var plugin in _pluginManager.All)
        {
            Plugins.Add(new PluginViewModel(plugin, () => PluginsChanged?.Invoke()));
        }

        if (_pluginManager.LoadWarnings.Count > 0)
        {
            PluginWarnings = "Some plugin folders were skipped:\n" + string.Join("\n", _pluginManager.LoadWarnings);
        }
    }

    [RelayCommand]
    private async Task ActivateLicenseAsync()
    {
        if (string.IsNullOrWhiteSpace(LicenseKey)) return;
        
        IsActivating = true;
        var (ok, message) = await _licenseService.ActivateAsync(LicenseKey);
        LicenseStatusMessage = message;
        if (ok) LicenseKey = string.Empty;
        IsActivating = false;
        OnPropertyChanged(nameof(HasLicenseStatus));
    }

    [RelayCommand]
    private async Task BuyProAsync()
    {
        try 
        { 
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LicenseService.StoreUrl,
                UseShellExecute = true
            }); 
            await Task.CompletedTask;
        }
        catch { }
    }

    [RelayCommand]
    private void DeactivateLicense()
    {
        _licenseService.Deactivate();
        LicenseStatusMessage = "License removed from this device.";
        OnPropertyChanged(nameof(HasLicenseStatus));
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatusMessage = "Checking…";
        OnPropertyChanged(nameof(HasUpdateStatus));
        DownloadUrl = string.Empty;
        OnPropertyChanged(nameof(HasDownloadUrl));

        var result = await _updateService.CheckAsync();

        UpdateStatusMessage = result.Message;
        if (result.UpdateAvailable && !string.IsNullOrEmpty(result.ReleaseUrl))
        {
            DownloadUrl = result.ReleaseUrl;
        }

        IsCheckingForUpdates = false;
        OnPropertyChanged(nameof(HasUpdateStatus));
        OnPropertyChanged(nameof(HasDownloadUrl));
    }
}
