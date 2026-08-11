namespace MarkSmith;

// Portable composition root (moved out of the WinUI App.xaml.cs so Core has no dependency on any
// UI framework). Each UI project's own Application-equivalent class calls AppServices.License.Load()
// once at startup and otherwise just forwards to these singletons — see MarkSmith/App.xaml.cs for the
// WinUI side. A DI container (Microsoft.Extensions.DependencyInjection) is the natural next step
// once the app grows past a couple of UI shells; this hand-rolled version is what let the WinUI ->
// Avalonia port happen without restructuring every service constructor.
public static class AppServices
{
    // Eager only for the cheap services needed before the first window exists: settings drive the
    // splash-video decision, themes/license/recent-files are read by the first frame.
    public static Services.SettingsService Settings { get; } = new();
    public static Services.ThemeCatalog Themes { get; } = new();
    public static Services.RecentFilesService RecentFiles { get; } = new();
    public static Services.LicenseService License { get; } = new();

    // Everything else is lazy: a couple of these are genuinely expensive to construct
    // (VersionHistory opens its SQLite database; MainViewModel copies the settings surface and
    // kicks off the update check; PluginManager scans the plugins directory) and none of them are
    // needed before the UI is up. Deferring them is what lets the splash video play immediately
    // and the main window paint without waiting for that graph — the classic WinUI "window first,
    // services after" launch shape. Lazy<T> default mode (ExecutionAndPublication) is
    // thread-safe: the first accessor wins and everyone else blocks on it. The factory overload
    // (() => new T()) is required, NOT Lazy<T>() — several services (CloudStorage, GoogleAuth,
    // GoogleDocs, VersionHistory, …) have no PUBLIC parameterless ctor, which the default-ctor
    // activator would reject with MissingMemberException at first access.
    private static readonly Lazy<Services.MarkdownHtmlService> _markdownHtml = new(() => new());
    public static Services.MarkdownHtmlService MarkdownHtml => _markdownHtml.Value;

    private static readonly Lazy<Services.LlmSourceService> _llmSource = new(() => new());
    public static Services.LlmSourceService LlmSource => _llmSource.Value;

    private static readonly Lazy<Services.HistoryService> _history = new(() => new());
    public static Services.HistoryService History => _history.Value;

    private static readonly Lazy<Services.GovernanceService> _governance = new(() => new());
    public static Services.GovernanceService Governance => _governance.Value;

    private static readonly Lazy<Services.UpdateService> _updates = new(() => new());
    public static Services.UpdateService Updates => _updates.Value;

    private static readonly Lazy<Services.BatchConvertService> _batchConvert = new(() => new());
    public static Services.BatchConvertService BatchConvert => _batchConvert.Value;

    private static readonly Lazy<Services.CloudStorageService> _cloudStorage = new(() => new());
    public static Services.CloudStorageService CloudStorage => _cloudStorage.Value;

    private static readonly Lazy<Plugins.PluginManager> _plugins = new(() => new());
    public static Plugins.PluginManager Plugins => _plugins.Value;

    private static readonly Lazy<Services.ExportCoordinator> _exportCoordinator = new(() => new());
    public static Services.ExportCoordinator ExportCoordinator => _exportCoordinator.Value;

    private static readonly Lazy<Services.VersionHistoryService> _versionHistory = new(() => new());
    public static Services.VersionHistoryService VersionHistory => _versionHistory.Value;

    private static readonly Lazy<Services.GoogleAuthService> _googleAuth = new(() => new());
    public static Services.GoogleAuthService GoogleAuth => _googleAuth.Value;

    private static readonly Lazy<Services.GoogleDocsExportService> _googleDocs = new(() => new());
    public static Services.GoogleDocsExportService GoogleDocs => _googleDocs.Value;

    // Constructed lazily (after the services above exist) since MainViewModel reads them in its
    // constructor. Each UI project sets ViewModel.Host / ViewModel.Prompts once its main window
    // exists (see IWebRenderHost/IUiPrompts) — MainViewModel no longer reaches into a static
    // "the app's main window" reference the way the old WinUI-only code did.
    private static readonly Lazy<ViewModels.MainViewModel> _viewModel = new(() => new());
    public static ViewModels.MainViewModel ViewModel => _viewModel.Value;
}
