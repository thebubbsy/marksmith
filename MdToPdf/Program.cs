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

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "startup-crash.log");

    private static void LogFatal(string source, Exception ex)
    {
        try
        {
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
