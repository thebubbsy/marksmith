using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using MarkSmith.Services;

namespace MarkSmith.Views;

public sealed partial class SuiteHubView : UserControl
{
    public event Action? OpenMermaidStudioRequested;
    public event Action? OpenShapeStudioRequested;
    public event Action? OpenGalaxyRequested;

    public SuiteHubView()
    {
        InitializeComponent();
        PopulateMetadata();
    }

    private void PopulateMetadata()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version?.ToString(3) ?? "3.0.0";
            VersionText.Text = $"v{ver} · x64 · .NET 8";

            AppServices.License.Load();
            if (AppServices.License.CanExportDocx)
            {
                LicenseText.Text = "Pro Entitled";
            }
            else
            {
                LicenseText.Text = "Free / Trial";
            }
        }
        catch { }
    }

    private void SetNotification(string message)
    {
        NotificationText.Text = message;
    }

    private void CopyToClipboard(string text, string successMessage)
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
            SetNotification($"✓ {successMessage}");
        }
        catch (Exception ex)
        {
            SetNotification($"Clipboard copy failed: {ex.Message}");
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
            SetNotification($"Opened configuration folder: {path}");
        }
        catch (Exception ex)
        {
            SetNotification($"Failed to open folder: {ex.Message}");
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
        CopyToClipboard(json, "Copied MCP configuration JSON to clipboard! Paste into claude_desktop_config.json.");
    }

    private void OnCopyMcpPathClick(object sender, RoutedEventArgs e)
    {
        var exePath = GetMcpServerPath();
        CopyToClipboard(exePath, "Copied marksmith-mcp binary path to clipboard.");
    }

    private void OnCopyApiUrlClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard("http://127.0.0.1:47821", "Copied local REST API URL (http://127.0.0.1:47821) to clipboard.");
    }

    private void OnOpenExtensionDocsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var extDocs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extension", "README.md"));
            if (File.Exists(extDocs))
            {
                Process.Start(new ProcessStartInfo { FileName = extDocs, UseShellExecute = true });
                SetNotification("Opened extension documentation.");
            }
            else
            {
                SetNotification("Browser extension connects locally to port 47821.");
            }
        }
        catch (Exception ex)
        {
            SetNotification($"Error opening docs: {ex.Message}");
        }
    }

    private void OnCopyCliCommandClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard("marksmith suite", "Copied 'marksmith suite' command to clipboard. Run in PowerShell/Terminal.");
    }

    private void OnCopyCliPathClick(object sender, RoutedEventArgs e)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "marksmith.exe");
        CopyToClipboard(cliPath, "Copied MarkSmith CLI path to clipboard.");
    }

    private void OnLaunchExpressClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://localhost:5000",
                UseShellExecute = true
            });
            SetNotification("Opened MarkSmith Express (http://localhost:5000) in default browser.");
        }
        catch (Exception ex)
        {
            SetNotification($"Failed to launch browser: {ex.Message}");
        }
    }
}
