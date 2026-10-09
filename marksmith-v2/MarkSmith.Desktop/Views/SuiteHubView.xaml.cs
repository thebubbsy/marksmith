using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using MarkSmith.Services;

namespace MarkSmith.Views;

public sealed partial class SuiteHubView : UserControl
{
    public event Action? OpenMermaidStudioRequested;
    public event Action? OpenShapeStudioRequested;
    public event Action? OpenGalaxyRequested;
    // The host closes the hub and opens the extension setup guide (one ContentDialog at a time).
    public event Action? OpenExtensionSetupRequested;

    private int _apiPort;
    private readonly Func<System.Threading.Tasks.Task<(bool Running, int Port, string? Error)>>? _turnOnApi;
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private Storyboard? _notificationFade;

    // apiRunning/apiPort come from the live AutomationManager: the Browser Companion badge used to
    // say "REST API Listening" in green whether or not the API was enabled, and the copied URL
    // ignored a custom port.
    // turnOnApi switches the local API on (the Settings › Automation setting) and reports whether it
    // came up, so "Not connected" is one click from fixed instead of a dead end.
    public SuiteHubView(bool apiRunning = false, int apiPort = 47821,
        Func<System.Threading.Tasks.Task<(bool Running, int Port, string? Error)>>? turnOnApi = null)
    {
        InitializeComponent();
        _apiPort = apiPort > 0 ? apiPort : 47821;
        _turnOnApi = turnOnApi;
        PopulateMetadata();
        ShowApiState(apiRunning);

        // "CLI Installed" was hard-coded too. The CLI ships beside the app; say so only if it's there.
        var cliPresent = File.Exists(CliPath);
        SetBadge(CliStatusText, cliPresent ? "Bundled" : "Not in this build", cliPresent);
        CopyCliPathButton.IsEnabled = cliPresent;
        if (!cliPresent)
            ToolTipService.SetToolTip(CopyCliPathButton, "marksmith.exe isn't installed alongside this copy of MarkSmith");

        _notificationTimer.Tick += (_, _) => { _notificationTimer.Stop(); FadeNotification(to: 0); };
        Unloaded += (_, _) => _notificationTimer.Stop();
        HoverPolish.Track(this);
    }

    private static string CliPath => Path.Combine(AppContext.BaseDirectory, "marksmith.exe");

    private void ShowApiState(bool running)
    {
        SetBadge(ApiStatusText, running ? "Connected" : "Not connected", running);
        ToolTipService.SetToolTip(ApiBadge, running
            ? $"MarkSmith is listening on http://127.0.0.1:{_apiPort}, so the extension can send chats here."
            : "The extension can't reach MarkSmith until the local connection is on.");
        TurnOnApiButton.Visibility = running || _turnOnApi is null ? Visibility.Collapsed : Visibility.Visible;
        CopyApiUrlButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnTurnOnApiClick(object sender, RoutedEventArgs e)
    {
        if (_turnOnApi is null) return;
        TurnOnApiButton.IsEnabled = false;
        try
        {
            var (running, port, error) = await _turnOnApi();
            if (port > 0) _apiPort = port;
            ShowApiState(running);
            if (running) SetNotification("Connected. The browser extension can now send chats to MarkSmith.");
            else SetNotification(error ?? "MarkSmith couldn't open its local connection. Check Settings › Automation.", success: false);
        }
        finally
        {
            TurnOnApiButton.IsEnabled = true;
        }
    }

    private static void SetBadge(TextBlock badge, string text, bool positive)
    {
        badge.Text = text;
        ThemeBrush.Set(badge, TextBlock.ForegroundProperty,
            positive ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
    }

    private void PopulateMetadata()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version?.ToString(3) ?? "3.0.0";
            VersionText.Text = $"Version {ver}";

            AppServices.License.Load();
            LicenseText.Text = MarkSmith.Models.ProGate.PlanBadge(AppServices.License.State);
        }
        catch { }
    }

    // A result line under the cards: success tick or warning icon, fades in, and fades away after a
    // few seconds so a stale "Copied…" never lingers into the next action.
    private void SetNotification(string message, bool success = true)
    {
        NotificationText.Text = message.TrimStart('✓', ' ');
        NotificationIcon.Glyph = success ? "\uE73E" : "\uE7BA";
        ThemeBrush.Set(NotificationIcon, FontIcon.ForegroundProperty,
            success ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        FadeNotification(to: 1);
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void FadeNotification(double to)
    {
        _notificationFade?.Stop();
        if (!HoverPolish.AnimationsEnabled)
        {
            NotificationRow.Opacity = to;
            return;
        }
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(to > 0 ? 160 : 400)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, NotificationRow);
        Storyboard.SetTargetProperty(fade, nameof(Opacity));
        _notificationFade = new Storyboard();
        _notificationFade.Children.Add(fade);
        _notificationFade.Begin();
    }

    private void CopyToClipboard(string text, string successMessage)
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
            SetNotification(successMessage);
        }
        catch (Exception ex)
        {
            SetNotification($"Clipboard copy failed: {ex.Message}", success: false);
        }
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppPaths.ConfigDir;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            SetNotification("Opened the configuration folder.");
        }
        catch (Exception ex)
        {
            SetNotification($"Couldn't open the folder: {ex.Message}", success: false);
        }
    }

    private void OnOpenMermaidStudioClick(object sender, RoutedEventArgs e)
    {
        OpenMermaidStudioRequested?.Invoke();
    }

    private void OnOpenShapeStudioClick(object sender, RoutedEventArgs e)
    {
        OpenShapeStudioRequested?.Invoke();
    }

    private void OnOpenGalaxyClick(object sender, RoutedEventArgs e)
    {
        OpenGalaxyRequested?.Invoke();
    }

    private string GetMcpServerPath()
    {
        var appDir = AppContext.BaseDirectory;
        var mcpExe = Path.Combine(appDir, "marksmith-mcp.exe");
        if (!File.Exists(mcpExe))
        {
            mcpExe = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "MarkSmith.Mcp", "bin", "Debug", "net8.0", "marksmith-mcp.exe"));
        }
        return File.Exists(mcpExe) ? mcpExe : "marksmith-mcp";
    }

    private void OnCopyClaudeConfigClick(object sender, RoutedEventArgs e)
    {
        var exePath = GetMcpServerPath();
        var configObj = new
        {
            mcpServers = new
            {
                marksmith = new
                {
                    command = exePath,
                    args = Array.Empty<string>()
                }
            }
        };

        var json = JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true });
        CopyToClipboard(json, "Copied the MCP server entry — paste it into claude_desktop_config.json.");
    }

    private void OnCopyGeminiConfigClick(object sender, RoutedEventArgs e)
    {
        var exePath = GetMcpServerPath();
        var configObj = new
        {
            mcpServers = new
            {
                marksmith = new
                {
                    command = exePath,
                    args = Array.Empty<string>()
                }
            }
        };

        var json = JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true });
        CopyToClipboard(json, "Copied the MCP server entry — paste it into your client's .mcp.json.");
    }

    private void OnCopyMcpPathClick(object sender, RoutedEventArgs e)
    {
        var exePath = GetMcpServerPath();
        CopyToClipboard(exePath, "Copied the marksmith-mcp server path.");
    }

    private void OnCopyApiUrlClick(object sender, RoutedEventArgs e)
    {
        var url = $"http://127.0.0.1:{_apiPort}";
        CopyToClipboard(url, $"Copied the local REST API address ({url}).");
    }

    private void OnOpenExtensionDocsClick(object sender, RoutedEventArgs e) => OpenExtensionSetupRequested?.Invoke();

    // Real, runnable commands (the old button copied "marksmith suite", which isn't on PATH and
    // only prints a status report). The bundled exe is quoted in full so a paste just works.
    private void OnCopyCliCommandClick(object sender, RoutedEventArgs e)
    {
        var exe = File.Exists(CliPath) ? $"& \"{CliPath}\"" : "marksmith";
        var (command, message) = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "batch" => ($"{exe} batch \"C:\\path\\to\\folder\" --format docx", "Copied a folder conversion. Paste it into PowerShell and change the folder."),
            "doctor" => ($"{exe} doctor", "Copied the installation check. Paste it into PowerShell."),
            _ => ($"{exe} \"report.md\" \"report.docx\"", "Copied a file conversion. Paste it into PowerShell and change the file names."),
        };
        CopyToClipboard(command, message);
    }

    private void OnCopyCliPathClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(CliPath, "Copied the MarkSmith CLI path.");
    }

    private async void OnLaunchExpressClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var expressExe = Path.Combine(appDir, "marksmith-express.exe");
            if (!File.Exists(expressExe))
            {
                expressExe = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "MarkSmith.Express", "bin", "Debug", "net8.0", "marksmith-express.exe"));
            }

            bool isResponding = false;
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
                var res = await client.GetAsync("http://localhost:5000/api/health");
                if (res.IsSuccessStatusCode) isResponding = true;
            }
            catch { }

            if (!isResponding && !File.Exists(expressExe))
            {
                // Opening http://localhost:5000 here only showed the browser's "can't reach" page.
                SetNotification("MarkSmith Express isn't running, and it isn't installed alongside this copy of MarkSmith.", success: false);
                return;
            }

            if (!isResponding)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = expressExe,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                await System.Threading.Tasks.Task.Delay(600);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "http://localhost:5000",
                UseShellExecute = true
            });
            SetNotification("Opened MarkSmith Express in your browser.");
        }
        catch (Exception ex)
        {
            SetNotification($"Couldn't open MarkSmith Express: {ex.Message}", success: false);
        }
    }
}
