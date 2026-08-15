namespace MarkSmith.Services;

// Single source of truth for the app's per-user config root (%LOCALAPPDATA%\MarkSmith). Every
// service that persists state derives its path from ConfigDir, so nothing hardcodes the folder
// twice. MARKSMITH_CONFIG_DIR redirects the whole config surface to a scratch directory — the test
// suite sets it in a module initializer so test runs never read or poison the real user's state
// (a persisted A4/width combo once made an authoritative-lock test short-circuit on the MVVM
// setter's equality check). The variable must exist before any consuming type initializes.
public static class AppPaths
{
    public static string ConfigDir { get; } =
        Environment.GetEnvironmentVariable("MARKSMITH_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith");
}
