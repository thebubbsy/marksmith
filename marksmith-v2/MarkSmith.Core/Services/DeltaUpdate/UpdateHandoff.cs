using System.Text;

namespace MarkSmith.Services.DeltaUpdate;

/// <summary>Builds the detached "apply on exit" handoff: a hidden .cmd that waits for the current
/// Marksmith process to exit, copies the staged files over the install dir, deletes removed files,
/// cleans the staging dir, and restarts the app. Self-elevates (UAC) when the install dir is
/// read-only (e.g. Program Files under a standard user) — with a marker arg so it does not loop.</summary>
public static class UpdateHandoff
{
    public static string BuildCmd(ApplyManifest apply, string stagingDir, string installDir, int pid, string exeName)
    {
        // Removed-file list is written as data next to the staged files; the cmd consumes it with
        // `for /f` so paths with spaces survive.
        var removedList = Path.Combine(stagingDir, "removed.txt");
        File.WriteAllLines(removedList, apply.Removed);

        // Unpredictable name: a predictable %TEMP%\marksmith-apply-{pid}.cmd would let a same-user
        // process race the spawn and swap in their own script (TOCTOU).
        var cmdPath = Path.Combine(Path.GetTempPath(), $"marksmith-apply-{Guid.NewGuid():N}.cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"STAGE={stagingDir}\"");
        sb.AppendLine($"set \"INSTALL={installDir}\"");
        sb.AppendLine($"set \"EXE={exeName}\"");
        sb.AppendLine($"set \"PID={pid}\"");
        sb.AppendLine();
        sb.AppendLine("if /i \"%~1\"==\"elevated\" goto waitloop");
        // Non-elevated: if we cannot write to the install dir, relaunch elevated (single hop).
        sb.AppendLine("net session >nul 2>&1");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("  powershell -NoProfile -Command \"Start-Process -FilePath '%0' -ArgumentList 'elevated' -Verb RunAs\" >nul 2>&1");
        sb.AppendLine("  exit /b");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine(":waitloop");
        sb.AppendLine("tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine("  goto waitloop");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("robocopy \"%STAGE%\\files\" \"%INSTALL%\" /E /NFL /NDL /NJH /NJS /NP >nul");
        sb.AppendLine("if exist \"%STAGE%\\removed.txt\" (");
        sb.AppendLine("  for /f \"usebackq delims=\" %%F in (\"%STAGE%\\removed.txt\") do (");
        sb.AppendLine("    if exist \"%INSTALL%\\%%F\" del /q /f \"%INSTALL%\\%%F\" >nul 2>&1");
        sb.AppendLine("  )");
        sb.AppendLine(")");
        sb.AppendLine("rmdir /s /q \"%STAGE%\" >nul 2>&1");
        sb.AppendLine("start \"\" \"%INSTALL%\\%EXE%\"");
        sb.AppendLine("exit /b");
        File.WriteAllText(cmdPath, sb.ToString());
        return cmdPath;
    }
}
