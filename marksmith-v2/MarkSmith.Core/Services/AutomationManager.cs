using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.ViewModels;

namespace MarkSmith.Services;

/// <summary>
/// AutomationManager (IngestionCoordinator) handles lifecycle management of background automation services:
/// ClipboardIngestService, FolderIngestService, and local REST ApiServer.
/// </summary>
public sealed class AutomationManager : IDisposable
{
    private readonly ApiServer _apiServer;

    public ApiServer ApiServer => _apiServer;
    public bool IsApiRunning => _apiServer.IsRunning;
    public int ApiPort => _apiServer.Port;

    public AutomationManager(
        LlmSourceService llm,
        Func<IReadOnlyList<string>> themeNames,
        Action<string, string, OutputOverride?> ingest,
        Func<string, OutputOverride?, Task<byte[]>> convert,
        GovernanceService governance,
        Func<string> allowedExtensionId,
        Func<AppSettings> getSettings,
        Action<AppSettings> saveSettings,
        Func<string, string, OutputOverride?, Task<object>> batchConvert)
    {
        _apiServer = new ApiServer(
            llm,
            themeNames,
            ingest,
            convert,
            governance,
            allowedExtensionId,
            getSettings,
            saveSettings,
            batchConvert);
    }

    /// <summary>
    /// Synchronizes automation background services (Clipboard watcher, Watch folder watcher, REST API server)
    /// based on current settings in MainViewModel.
    /// </summary>
    public void ApplyAutomationSettings(
        MainViewModel vm,
        Action startClipboard,
        Action stopClipboard,
        bool isClipboardRunning,
        Action<string> startFolder,
        Action stopFolder,
        bool isFolderRunning,
        Action<string>? onApiStatusChanged = null)
    {
        // Automation is a PRO feature: the watchers must never run for a free user, no matter how
        // the settings got flipped (the VM gates the toggles too, but this is the enforcement layer).
        var automationAllowed = AppServices.License.CanAutomate;

        if (automationAllowed && vm.AutoClipboardIngest && !isClipboardRunning) startClipboard();
        else if (!vm.AutoClipboardIngest && isClipboardRunning) stopClipboard();

        if (automationAllowed && vm.WatchFolderEnabled && Directory.Exists(vm.WatchFolder)) startFolder(vm.WatchFolder);
        else stopFolder();

        try
        {
            if (vm.ApiEnabled && (!_apiServer.IsRunning || _apiServer.Port != vm.ApiPort))
            {
                _apiServer.Start(vm.ApiPort);
            }
            else if (!vm.ApiEnabled)
            {
                _apiServer.Stop();
            }

            var statusText = _apiServer.IsRunning ? $"http://127.0.0.1:{_apiServer.Port}/api/health" : "";
            onApiStatusChanged?.Invoke(statusText);
        }
        catch (Exception ex)
        {
            onApiStatusChanged?.Invoke($"API failed to start: {ex.Message}");
        }
    }

    public void StopAll()
    {
        _apiServer.Stop();
    }

    public void Dispose()
    {
        _apiServer.Dispose();
    }
}
