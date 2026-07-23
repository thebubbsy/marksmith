namespace MdToPdf.Plugins;

public enum PluginInstallState { NotInstalled, Installing, Installed, Failed }

// Optional, separately-downloadable capability. The base app never bundles a plugin's payload —
// each plugin decides for itself what "installed" means (files on disk under
// PluginManager.PluginsRoot(Id)) and how to fetch it, so heavy dependencies (e.g. a JRE for the
// PlantUML plugin) only cost disk/bandwidth for users who actually opt in.
public interface IMarksmithPlugin
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Version { get; }

    // Re-checks disk state; call after InstallAsync/Uninstall or at startup, not on every access.
    PluginInstallState State { get; }

    Task InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken);
    void Uninstall();
}
