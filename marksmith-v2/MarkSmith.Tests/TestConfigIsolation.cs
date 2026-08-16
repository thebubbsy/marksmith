using System.Runtime.CompilerServices;

namespace MarkSmith.Tests;

// Runs before ANY MarkSmith type initializes: AppPaths.ConfigDir reads MARKSMITH_CONFIG_DIR
// exactly once, so this must land first. Redirecting the whole config surface to a per-process
// temp dir means test runs never read or poison the real user's %LOCALAPPDATA%\MarkSmith state —
// a persisted A4/width combo once made an authoritative-lock test short-circuit on the MVVM
// setter's equality check (the VM loaded the poisoned values, so the setters no-op'd).
internal static class TestConfigIsolation
{
    [ModuleInitializer]
    internal static void RedirectConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MarkSmith.Tests", Environment.ProcessId.ToString());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("MARKSMITH_CONFIG_DIR", dir);
    }
}
