using MdToPdf.Services;
using MdToPdf.ViewModels;
using Microsoft.UI.Xaml;

namespace MdToPdf;

public partial class App : Application
{
    // Small hand-rolled composition root. A DI container (Microsoft.Extensions.DependencyInjection)
    // is the natural next step once the app grows past a couple of pages/services.
    public static SettingsService Settings { get; } = new();
    public static ThemeCatalog Themes { get; } = new();
    public static RecentFilesService RecentFiles { get; } = new();
    public static MarkdownHtmlService MarkdownHtml { get; } = new();
    public static LlmSourceService LlmSource { get; } = new();
    public static HistoryService History { get; } = new();
    public static GovernanceService Governance { get; } = new();

    // Constructed lazily (after the services above exist) since MainViewModel reads them in its constructor.
    public static MainViewModel ViewModel { get; } = new();

    public static Window MainAppWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
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

            MainAppWindow = new MainWindow();
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
