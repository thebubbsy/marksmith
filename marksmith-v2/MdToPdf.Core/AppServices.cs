namespace MdToPdf;

// Portable composition root (moved out of the WinUI App.xaml.cs so Core has no dependency on any
// UI framework). Each UI project's own Application-equivalent class calls AppServices.License.Load()
// once at startup and otherwise just forwards to these singletons — see MdToPdf/App.xaml.cs for the
// WinUI side. A DI container (Microsoft.Extensions.DependencyInjection) is the natural next step
// once the app grows past a couple of UI shells; this hand-rolled version is what let the WinUI ->
// Avalonia port happen without restructuring every service constructor.
public static class AppServices
{
    public static Services.SettingsService Settings { get; } = new();
    public static Services.ThemeCatalog Themes { get; } = new();
    public static Services.RecentFilesService RecentFiles { get; } = new();
    public static Services.MarkdownHtmlService MarkdownHtml { get; } = new();
    public static Services.LlmSourceService LlmSource { get; } = new();
    public static Services.HistoryService History { get; } = new();
    public static Services.GovernanceService Governance { get; } = new();
    public static Services.UpdateService Updates { get; } = new();
    public static Services.LicenseService License { get; } = new();
    public static Services.BatchConvertService BatchConvert { get; } = new();
    public static Services.CloudStorageService CloudStorage { get; } = new();
    public static Plugins.PluginManager Plugins { get; } = new();
    public static Services.ExportCoordinator ExportCoordinator { get; } = new();

    // Constructed lazily (after the services above exist) since MainViewModel reads them in its
    // constructor. Each UI project sets ViewModel.Host / ViewModel.Prompts once its main window
    // exists (see IWebRenderHost/IUiPrompts) — MainViewModel no longer reaches into a static
    // "the app's main window" reference the way the old WinUI-only code did.
    public static ViewModels.MainViewModel ViewModel { get; } = new();
}
