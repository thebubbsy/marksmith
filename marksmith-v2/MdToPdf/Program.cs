using System;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MdToPdf;

// Custom entry point (replaces the SDK-generated one — see DisableXamlGeneratedMain in the
// .csproj) so that any exception during startup — including ones from WinRT activation that
// otherwise surface only as an opaque STATUS_STOWED_EXCEPTION process exit — gets written to a
// plain-text log next to the exe instead of vanishing silently.
public static class Program
{
    [System.Runtime.InteropServices.DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    // Per-user, always-writable app data root. The install lives under Program Files (read-only for
    // standard users), so neither WebView2's data folder nor the crash log may live next to the exe.
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith");

    private static readonly string LogPath = Path.Combine(AppDataDir, "startup-crash.log");

    private static void LogFatal(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(LogPath, $"[{source}] {DateTime.Now:O}{Environment.NewLine}{ex}");
        }
        catch
        {
            // If we can't even write the log, there's nothing more we can do.
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogFatal("AppDomain.UnhandledException", ex);
        };

        // WebView2 defaults its user-data folder to "<exe>.WebView2\" beside the exe. Under Program
        // Files that's not writable by a standard user, so the WebView fails to start ("Microsoft
        // Edge can't read and write to its data directory"). Point it at the per-user app data root
        // BEFORE any WebView2 initialization. Must be set here, before the window is created.
        try
        {
            var wv2 = Path.Combine(AppDataDir, "WebView2");
            Directory.CreateDirectory(wv2);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", wv2);
        }
        catch (Exception ex) { LogFatal("WebView2 data folder setup", ex); }

        try
        {
            XamlCheckProcessRequirements();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                try
                {
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                }
                catch (Exception ex)
                {
                    LogFatal("Application.Start callback", ex);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            LogFatal("Main", ex);
            throw;
        }
    }
}

