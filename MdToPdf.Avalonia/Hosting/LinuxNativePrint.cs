using System.Runtime.InteropServices;
using MdToPdf.Services;

namespace MdToPdf.Avalonia.Hosting;

// P/Invoke bridge into WebKitGTK's own native print pipeline, mirroring the philosophy of the
// Windows WebView2 bridge in MainWindow.axaml.cs's PrintToPdfAsync: Avalonia.Controls.WebView's own
// cross-platform WebViewPrintSettings has no PageWidth/PageHeight on ANY platform, so real page-size
// control means reaching past it to the platform's own native print API instead. On Linux that's
// WebKitGTK's WebKitPrintOperation plus GTK's GtkPrintSettings/GtkPageSetup — a completely different
// C/GObject API from Windows' WebView2 or Apple's WKWebView. All three engines share WebKit
// rendering lineage, but there is no single "WebKit" embedding API that runs unmodified across
// them — each platform's host toolkit (Win32/COM, GObject/GTK, Objective-C/AppKit) has its own.
//
// GtkPrintSettings' "output-uri" + "output-file-format"="pdf" drives GTK's own built-in
// print-to-file backend headlessly — no dialog, no real printer needed.
// webkit_print_operation_print() then runs the whole pipeline (pagination, rendering, PDF writing)
// asynchronously on the GLib main loop already pumping this process (the same one driving the
// embedded WebKitWebView), completing via the "finished" or "failed" signal.
//
// STATUS: written against documented WebKitGTK/GTK behavior, not yet empirically verified the way
// the Windows WebView2 bridge was (measured against two distinct page widths via an isolated
// harness). Plan is to verify the same way inside WSL2 Ubuntu (WSLg provides a real Wayland/X11
// display, so the embedded WebKitWebView genuinely renders) once libwebkit2gtk-4.1 is installed
// there. Update this comment with the measured result once that's done — don't trust it blind.
internal static class LinuxNativePrint
{
    private const string Gtk = "libgtk-3.so.0";
    private const string WebKit = "libwebkit2gtk-4.1.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    // GtkUnit enum (gtk/gtkenums.h): Pixel=0, Points=1, Inch=2, Mm=3. PdfPageSetup is already in
    // inches (see MdToPdf.Core/Rendering/IWebRenderHost.cs), so no unit conversion is needed here.
    private const int GtkUnitInch = 2;

    [DllImport(Gtk)] private static extern nint gtk_print_settings_new();
    [DllImport(Gtk)] private static extern void gtk_print_settings_set(nint settings, string key, string value);
    [DllImport(Gtk)] private static extern nint gtk_page_setup_new();
    [DllImport(Gtk)] private static extern nint gtk_paper_size_new_custom(string name, string displayName, double width, double height, int unit);
    [DllImport(Gtk)] private static extern void gtk_paper_size_free(nint paperSize);
    [DllImport(Gtk)] private static extern void gtk_page_setup_set_paper_size(nint pageSetup, nint paperSize);
    [DllImport(Gtk)] private static extern void gtk_page_setup_set_top_margin(nint pageSetup, double margin, int unit);
    [DllImport(Gtk)] private static extern void gtk_page_setup_set_bottom_margin(nint pageSetup, double margin, int unit);
    [DllImport(Gtk)] private static extern void gtk_page_setup_set_left_margin(nint pageSetup, double margin, int unit);
    [DllImport(Gtk)] private static extern void gtk_page_setup_set_right_margin(nint pageSetup, double margin, int unit);

    [DllImport(WebKit)] private static extern nint webkit_print_operation_new(nint webView);
    [DllImport(WebKit)] private static extern void webkit_print_operation_set_print_settings(nint op, nint settings);
    [DllImport(WebKit)] private static extern void webkit_print_operation_set_page_setup(nint op, nint pageSetup);
    [DllImport(WebKit)] private static extern void webkit_print_operation_print(nint op);

    [DllImport(GObject)] private static extern ulong g_signal_connect_data(nint instance, string detailedSignal, nint cHandler, nint data, nint destroyData, int connectFlags);
    [DllImport(GObject)] private static extern void g_object_unref(nint obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FinishedHandler(nint instance, nint userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FailedHandler(nint instance, nint error, nint userData);

    // `webKitWebView` is the raw native WebKitWebView* from IGtkWebViewPlatformHandle.WebKitWebView.
    // Must be called on the same thread pumping the GLib main loop that owns the embedded webview
    // (the Avalonia UI thread on Linux), matching the same single-thread constraint WebView2 has on
    // Windows via its COM apartment.
    public static async Task<bool> PrintToPdfAsync(nint webKitWebView, string outputPath, PdfPageSetup setup)
    {
        if (webKitWebView == 0) return false;

        var op = webkit_print_operation_new(webKitWebView);
        if (op == 0) return false;

        var settings = gtk_print_settings_new();
        gtk_print_settings_set(settings, "output-uri", new Uri(outputPath).AbsoluteUri);
        gtk_print_settings_set(settings, "output-file-format", "pdf");
        webkit_print_operation_set_print_settings(op, settings); // takes its own ref
        g_object_unref(settings);

        var pageSetup = gtk_page_setup_new();
        var paper = gtk_paper_size_new_custom("MarkSmith-custom", "MarkSmith Custom", setup.PageWidthIn, setup.PageHeightIn, GtkUnitInch);
        gtk_page_setup_set_paper_size(pageSetup, paper); // copies the paper size
        gtk_paper_size_free(paper);
        gtk_page_setup_set_top_margin(pageSetup, setup.MarginTopIn, GtkUnitInch);
        gtk_page_setup_set_bottom_margin(pageSetup, setup.MarginBottomIn, GtkUnitInch);
        gtk_page_setup_set_left_margin(pageSetup, setup.MarginLeftIn, GtkUnitInch);
        gtk_page_setup_set_right_margin(pageSetup, setup.MarginRightIn, GtkUnitInch);
        webkit_print_operation_set_page_setup(op, pageSetup); // takes its own ref
        g_object_unref(pageSetup);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Backgrounds/colors aren't a WebKitPrintOperation setting (unlike WebView2's
        // ShouldPrintBackgrounds) — WebKit always renders backgrounds when printing; the
        // print-color-adjust CSS PdfExportService.cs injects is what actually governs that, same as
        // every other engine, so setup.PrintBackgrounds needs no native counterpart here.
        FinishedHandler onFinished = (_, _) => tcs.TrySetResult(true);
        FailedHandler onFailed = (_, _, _) => tcs.TrySetResult(false);

        // Delegates must stay alive (via GC.KeepAlive below) until the signal fires — GC could
        // otherwise collect them between g_signal_connect_data and the callback actually firing.
        g_signal_connect_data(op, "finished", Marshal.GetFunctionPointerForDelegate(onFinished), 0, 0, 0);
        g_signal_connect_data(op, "failed", Marshal.GetFunctionPointerForDelegate(onFailed), 0, 0, 0);

        webkit_print_operation_print(op);
        var ok = await tcs.Task;
        GC.KeepAlive(onFinished);
        GC.KeepAlive(onFailed);
        g_object_unref(op);
        return ok;
    }
}

