using Microsoft.Web.WebView2.Core;

namespace MarkSmith.Services;

// Builds the CoreWebView2Environment that every WebView2 in the app is initialized from (the main
// window preview AND the Diagram Studio preview). Browser-process switches — notably GPU/hardware
// acceleration — are locked in when the environment is created, and the FIRST environment created
// in the process wins for the shared browser process. Routing all WebView2 controls through this
// one factory guarantees they agree, so the user's "hardware acceleration" preference is honoured
// consistently no matter which surface spins up a WebView first.
internal static class WebView2EnvironmentFactory
{
    public static async Task<CoreWebView2Environment> CreateAsync()
    {
        var options = new CoreWebView2EnvironmentOptions();

        // Hardware acceleration is ON by default (Chromium composites on the GPU). When the user
        // turns it off we hand Chromium the same switches Chrome/VS Code use for their "disable
        // hardware acceleration" escape hatch, forcing software rendering to sidestep GPU-driver
        // bugs (black preview, flickering, crashes) on affected hardware, remote desktop and VMs.
        // The toggle is a startup-time setting (see AppSettings.HardwareAcceleration) — changing it
        // takes effect on the next launch, which the Settings UI states.
        if (!AppServices.Settings.Current.HardwareAcceleration)
        {
            options.AdditionalBrowserArguments = "--disable-gpu --disable-gpu-compositing";
        }

        // null browser/user-data folders => WebView2's own defaults (the same folders the
        // parameterless EnsureCoreWebView2Async() would have picked), so this changes nothing about
        // where the runtime or profile live — only the browser arguments.
        //
        // NOTE: WinUI 3 hands us the CoreWebView2 types through the C#/WinRT projection, which does
        // NOT expose the classic CoreWebView2Environment.CreateAsync(folder, folder, options)
        // overload (compile error CS1501). The projection's sanctioned entry point for "environment
        // + options" is CreateWithOptionsAsync — the exact API Microsoft's WinUI 3 WebView2 docs use
        // for their disable-SmartScreen example. Same result, projection-compatible signature.
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, null, options);
    }
}
