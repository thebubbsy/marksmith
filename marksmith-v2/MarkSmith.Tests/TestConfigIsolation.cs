using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MarkSmith.Tests;

// Runs before ANY MarkSmith type initializes: AppPaths.ConfigDir reads MARKSMITH_CONFIG_DIR
// exactly once, so this must land first. Redirecting the whole config surface to a per-process
// temp dir means test runs never read or poison the real user's %LOCALAPPDATA%\MarkSmith state —
// a persisted A4/width combo once made an authoritative-lock test short-circuit on the MVVM
// setter's equality check (the VM loaded the poisoned values, so the setters no-op'd).
//
// The run's temp folder lives there too: TMP/TEMP point at <run>\tmp, so every Path.GetTempPath()
// file a test forgets (or can't, because something still holds it open) goes with the run. Each
// suite run used to leave ~10 folders and files in %TEMP% (MarkSmith_5docs_*, ms-undo-*.json,
// patch-out-*.docx, …) plus its config folder: hundreds after a few weeks. Finished runs are
// removed on exit, and any a crashed run left behind are swept by the next one.
internal static class TestConfigIsolation
{
    [ModuleInitializer]
    internal static void RedirectConfigDir()
    {
        var runs = Path.Combine(Path.GetTempPath(), "MarkSmith.Tests");
        SweepFinishedRuns(runs);

        var run = Path.Combine(runs, Environment.ProcessId.ToString());
        var config = Path.Combine(run, "config");
        var temp = Path.Combine(run, "tmp");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("MARKSMITH_CONFIG_DIR", config);
        Environment.SetEnvironmentVariable("TMP", temp);
        Environment.SetEnvironmentVariable("TEMP", temp);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(run);
    }

    // A run folder is named after its test process; one whose process is gone is finished.
    private static void SweepFinishedRuns(string runs)
    {
        if (!Directory.Exists(runs)) return;
        foreach (var dir in Directory.EnumerateDirectories(runs))
        {
            if (int.TryParse(Path.GetFileName(dir), out var pid) && pid != Environment.ProcessId && !IsRunning(pid))
                TryDelete(dir);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; } // can't tell: leave it alone
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
