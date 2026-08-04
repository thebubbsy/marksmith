using MarkSmith.Services;
using MarkSmith.ViewModels;
using Microsoft.UI.Xaml;

namespace MarkSmith;

public partial class App : Application
{
    // Thin forwarders to the portable composition root in MarkSmith.Core — kept here (rather than
    // switching every WinUI call site to `AppServices.X`) so this refactor didn't have to touch
    // every page in one pass. MainAppWindow is the one genuinely WinUI-only piece.
    public static SettingsService Settings => AppServices.Settings;
    public static ThemeCatalog Themes => AppServices.Themes;
    public static RecentFilesService RecentFiles => AppServices.RecentFiles;
    public static MarkdownHtmlService MarkdownHtml => AppServices.MarkdownHtml;
    public static LlmSourceService LlmSource => AppServices.LlmSource;
    public static HistoryService History => AppServices.History;
    public static GovernanceService Governance => AppServices.Governance;
    public static UpdateService Updates => AppServices.Updates;
    public static LicenseService License => AppServices.License;
    public static MarkSmith.Plugins.PluginManager Plugins => AppServices.Plugins;
    public static ExportCoordinator ExportCoordinator => AppServices.ExportCoordinator;
    public static MainViewModel ViewModel => AppServices.ViewModel;

    public static Window MainAppWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();

        // UI-thread exceptions (XAML callbacks, async-void handlers) surface here, not through
        // AppDomain.UnhandledException. Log every one so a crash is diagnosable from
        // %LOCALAPPDATA%\MarkSmith\startup-crash.log instead of a mystery.
        UnhandledException += (_, e) =>
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MarkSmith");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "startup-crash.log"),
                    $"[App.UnhandledException] {DateTime.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            License.Load(); // resolve Free / Trial / Pro before any UI reads entitlements

            // Toast notifications for background auto-conversions; button args open the PDF or its folder.
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.NotificationInvoked += (_, e) =>
            {
                if (!e.Arguments.TryGetValue("path", out var path) || !System.IO.File.Exists(path)) return;
                var isFolder = e.Arguments.TryGetValue("action", out var a) && a == "folder";
                var psi = isFolder
                    ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    : new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
                try { System.Diagnostics.Process.Start(psi); } catch { }
            };
            try { Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register(); }
            catch { /* toasts unavailable (e.g. notifications disabled) — app works without them */ }

            var window = new MainWindow();
            MainAppWindow = window;
            ViewModel.Host = window;
            ViewModel.Prompts = window;
            MainAppWindow.Activate();
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "startup-crash.log"),
                    $"[App.OnLaunched] {DateTime.Now:O}{Environment.NewLine}{ex}");
            }
            catch { }
            throw;
        }
    }
}
