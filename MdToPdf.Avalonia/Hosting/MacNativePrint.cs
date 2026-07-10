using System.Runtime.InteropServices;
using MdToPdf.Services;

namespace MdToPdf.Avalonia.Hosting;

// Raw Objective-C runtime bridge into AppKit's print pipeline for WKWebView, mirroring the same
// philosophy as the Windows (WebView2) and Linux (WebKitGTK) native bridges: Avalonia.Controls
// .WebView's own cross-platform WebViewPrintSettings has no PageWidth/PageHeight on ANY platform,
// so real page-size control means going around it to the platform's own native print API.
//
// WHY THIS SPECIFIC APPROACH (NSPrintOperation/NSPrintInfo, not WKWebView's own PDF API): WKWebView
// exposes a newer, more direct `-createPDFWithConfiguration:completionHandler:` (WKPDFConfiguration
// has a `.rect` giving exact width/height control, macOS 11+) — but its completionHandler is an
// Objective-C block, and hand-building a block from raw P/Invoke means constructing the
// `__block_literal` memory layout (isa/flags/reserved/invoke/descriptor) by hand with no compiler
// or runtime safety net; a wrong layout doesn't throw a catchable .NET exception, it corrupts memory
// or crashes the process outright. NSPrintOperation/NSPrintInfo is older, plain synchronous
// Objective-C message sends only (no blocks) — WKWebView has supported standard AppKit printing via
// `-printOperationWithPrintInfo:` since it shipped, because Safari's own File > Print goes through
// exactly this path. Lower ceiling on capability (no async progress callbacks) but a much smaller
// blast radius for a mistake made without the ability to test against real AppKit.
//
// STATUS: written against documented AppKit/WKWebView behavior, NOT independently tested — there is
// no macOS hardware available in this environment. Per explicit instruction, this ships once the
// structurally-analogous Linux bridge (Hosting/LinuxNativePrint.cs) is verified working, on the
// reasoning that the same "reach past Avalonia's wrapper to the platform's real print API" approach
// succeeding on one platform is reasonable (not certain) evidence the approach is sound generally.
// If this throws on real hardware, it falls back to the existing @page CSS path like the other
// bridges — EXCEPT for a genuinely wrong selector name/type signature, which Objective-C surfaces as
// doesNotRecognizeSelector: and is NOT guaranteed to be catchable as a normal .NET exception the way
// a bad P/Invoke library/entry-point lookup is. Selectors used here are long-stable, widely
// documented AppKit APIs (decades old) specifically to minimize that risk, but this still needs a
// real run on real Apple hardware before being trusted the way Windows/Linux now are (or will be).
internal static class MacNativePrint
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint IntPtr_send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint IntPtr_send_IntPtr(nint receiver, nint selector, nint arg1);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint IntPtr_send_IntPtr_IntPtr(nint receiver, nint selector, nint arg1, nint arg2);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void void_send_NSSize(nint receiver, nint selector, NSSize size);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void void_send_double(nint receiver, nint selector, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void void_send_IntPtr(nint receiver, nint selector, nint arg1);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void void_send_bool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool bool_send(nint receiver, nint selector);

    // CGFloat is 8 bytes (double) on every 64-bit macOS target (Intel and Apple Silicon).
    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize { public double Width, Height; }

    private static nint NSStr(string s)
    {
        var cls = objc_getClass("NSString");
        var sel = sel_registerName("stringWithUTF8String:");
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        try { return IntPtr_send_IntPtr(cls, sel, utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    // `wkWebView` is the raw native WKWebView* from IAppleWKWebViewPlatformHandle.WKWebView. Must be
    // called on the main thread — AppKit's print machinery (like WebView2's COM apartment and
    // WebKitGTK's GLib main loop) is not safe to drive from a background thread.
    public static Task<bool> PrintToPdf(nint wkWebView, string outputPath, PdfPageSetup setup)
    {
        if (wkWebView == 0) return Task.FromResult(false);

        const double ptPerIn = 72.0;

        var infoClass = objc_getClass("NSPrintInfo");
        var info = IntPtr_send(IntPtr_send(infoClass, sel_registerName("alloc")), sel_registerName("init"));

        void_send_NSSize(info, sel_registerName("setPaperSize:"),
            new NSSize { Width = setup.PageWidthIn * ptPerIn, Height = setup.PageHeightIn * ptPerIn });
        void_send_IntPtr(info, sel_registerName("setOrientation:"), 0); // NSPaperOrientationPortrait
        void_send_double(info, sel_registerName("setTopMargin:"), setup.MarginTopIn * ptPerIn);
        void_send_double(info, sel_registerName("setBottomMargin:"), setup.MarginBottomIn * ptPerIn);
        void_send_double(info, sel_registerName("setLeftMargin:"), setup.MarginLeftIn * ptPerIn);
        void_send_double(info, sel_registerName("setRightMargin:"), setup.MarginRightIn * ptPerIn);
        void_send_IntPtr(info, sel_registerName("setJobDisposition:"), NSStr("NSPrintSaveJob"));

        var dict = IntPtr_send(info, sel_registerName("dictionary"));
        var urlClass = objc_getClass("NSURL");
        var fileUrl = IntPtr_send_IntPtr(urlClass, sel_registerName("fileURLWithPath:"), NSStr(outputPath));
        IntPtr_send_IntPtr_IntPtr(dict, sel_registerName("setObject:forKey:"), fileUrl, NSStr("NSPrintJobSavingURL"));

        // WKWebView overrides -printOperationWithPrintInfo: to produce a print operation aware of
        // its (out-of-process) web content — the same path Safari's own File > Print uses, unlike
        // the plain-NSView -dataWithPDFInsideRect:, which does not work on WKWebView's layer-hosted,
        // GPU-composited, separate-process content.
        var op = IntPtr_send_IntPtr(wkWebView, sel_registerName("printOperationWithPrintInfo:"), info);
        if (op == 0) return Task.FromResult(false);

        void_send_bool(op, sel_registerName("setShowsPrintPanel:"), false);
        void_send_bool(op, sel_registerName("setShowsProgressPanel:"), false);

        // -runOperation pumps its own run loop internally and blocks the calling thread until the
        // (out-of-process) rendering/pagination/writing finishes — matching Windows/Linux's
        // synchronous-from-the-caller's-view native print calls, just without an async/await point.
        var ok = bool_send(op, sel_registerName("runOperation"));
        return Task.FromResult(ok);
    }
}
