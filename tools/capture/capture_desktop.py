"""Marksmith desktop screenshot capture.

Launches the built WinUI 3 app against a document, maximizes it, waits for the
WebView2 preview to settle, and writes a window-cropped PNG. Used to regenerate
the README media from a real build rather than hand-cropped stills.

    python tools/capture/capture_desktop.py [shot-name ...]
"""

import ctypes
import os
import shutil
import subprocess
import sys
import time

import win32con
import win32gui
import win32process
import win32ui
from PIL import Image, ImageGrab

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import appsession  # noqa: E402  (path set up above)

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

MOUSEEVENTF_WHEEL = 0x0800

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
EXE = os.path.join(
    ROOT, "marksmith-v2", "MarkSmith.Desktop", "bin", "x64", "Release",
    "net8.0-windows10.0.19041.0", "win-x64", "Marksmith.exe",
)
OUT = os.path.join(ROOT, "docs", "media")
# Demo documents are staged under a neutral public path, and exports point
# there too, so no screenshot ships the capturing machine's user name.
DEMO_DIR = os.path.join("C:\\Users", "Public", "Marksmith Demo")

# name -> (source document, settings overrides, seconds to let the preview render)
SHOTS = {
    "desktop-split":  ("examples/product-spec.md",   {"EditorViewMode": "Split"},   16, -18, 0.62),
    "desktop-editor": ("examples/chatgpt-export.md", {"EditorViewMode": "Code"},    10, 0),
    "desktop-math":   ("examples/math-cheatsheet.md", {"EditorViewMode": "Preview"}, 14, -8),
}

BASE_OVERRIDES = {
    "OutputFolder": DEMO_DIR,
    "Theme": "GitHub Light",
    "PreviewZoom": 0.8,
    "EditorFontSize": 14,
    "ShowExtensionTip": False,
    "IncludeToc": False,
    "LookingGlassMode": False,
}


def stage(doc):
    """Copy a demo document into the neutral public folder and return its path."""
    os.makedirs(DEMO_DIR, exist_ok=True)
    dest = os.path.join(DEMO_DIR, os.path.basename(doc))
    shutil.copy2(os.path.join(ROOT, doc.replace("/", os.sep)), dest)
    return dest



def find_window(pid):
    """Find the app's own top-level window.

    Matched on the launched process id (and the WinUI 3 window class), never on
    the title: a browser tab named "Marksmith ..." would otherwise match and the
    capture would grab the user's screen instead of the app.
    """
    hits = []

    def cb(hwnd, _):
        if not win32gui.IsWindowVisible(hwnd):
            return True
        _, owner = win32process.GetWindowThreadProcessId(hwnd)
        if owner == pid and win32gui.GetClassName(hwnd) == "WinUIDesktopWin32WindowClass":
            hits.append(hwnd)
        return True

    win32gui.EnumWindows(cb, None)
    return hits[0] if hits else None


def grab_window(hwnd):
    """Render the window's own pixels via PrintWindow.

    Deliberately not a screen grab: PrintWindow can only ever return this
    window's content, so an occluding window (or anything else on the user's
    desktop) can never leak into a published screenshot.
    """
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    w, h = right - left, bottom - top

    win_dc = win32gui.GetWindowDC(hwnd)
    src = win32ui.CreateDCFromHandle(win_dc)
    mem = src.CreateCompatibleDC()
    bmp = win32ui.CreateBitmap()
    bmp.CreateCompatibleBitmap(src, w, h)
    mem.SelectObject(bmp)

    # PW_RENDERFULLCONTENT (2) — required for WebView2 / DirectComposition surfaces.
    ok = ctypes.windll.user32.PrintWindow(hwnd, mem.GetSafeHdc(), 2)

    info = bmp.GetInfo()
    img = Image.frombuffer(
        "RGB", (info["bmWidth"], info["bmHeight"]),
        bmp.GetBitmapBits(True), "raw", "BGRX", 0, 1,
    )

    win32gui.DeleteObject(bmp.GetHandle())
    mem.DeleteDC()
    src.DeleteDC()
    win32gui.ReleaseDC(hwnd, win_dc)

    if not ok:
        raise RuntimeError("PrintWindow failed")

    # WinUI leaves an invisible resize border around a maximized window.
    return img.crop((8, 8, img.width - 8, img.height - 8))


def child_windows(hwnd):
    kids = []
    win32gui.EnumChildWindows(hwnd, lambda h, _: kids.append(h) or True, None)
    return kids


def scroll_preview(hwnd, notches, preview_x):
    """Wheel-scroll the WebView2 preview pane by posting WM_MOUSEWHEEL."""
    if not notches:
        return
    # WinUI 3 hosts WebView2 as a composition visual, not a child HWND, so there
    # is nothing to PostMessage to — the wheel has to go through real input.
    # The pointer is parked over the preview and put back where it was.
    if win32gui.GetForegroundWindow() != hwnd:
        print("    [!] app is not foreground; not scrolling")
        return

    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    x = int(left + (right - left) * preview_x)
    y = (top + bottom) // 2

    origin = win32gui.GetCursorPos()
    try:
        ctypes.windll.user32.SetCursorPos(x, y)
        time.sleep(0.2)
        for _ in range(abs(notches)):
            ctypes.windll.user32.mouse_event(MOUSEEVENTF_WHEEL, 0, 0,
                                             -120 if notches < 0 else 120, 0)
            time.sleep(0.07)
        time.sleep(1.4)
    finally:
        ctypes.windll.user32.SetCursorPos(*origin)


def foreground(hwnd):
    """Bring the app forward, working around Windows' foreground lock.

    A background console process is not allowed to steal focus outright, so the
    input queues are attached to the current foreground thread first — the same
    dance shells and installers use. Needed only so wheel input reaches the
    preview; the capture itself does not depend on it.
    """
    user32 = ctypes.windll.user32
    win32gui.ShowWindow(hwnd, win32con.SW_MAXIMIZE)

    target_thread = win32process.GetWindowThreadProcessId(hwnd)[0]
    for _ in range(5):
        fg = win32gui.GetForegroundWindow()
        fg_thread = win32process.GetWindowThreadProcessId(fg)[0] if fg else 0
        attached = fg_thread and user32.AttachThreadInput(fg_thread, target_thread, True)
        try:
            user32.BringWindowToTop(hwnd)
            user32.SetForegroundWindow(hwnd)
        finally:
            if attached:
                user32.AttachThreadInput(fg_thread, target_thread, False)

        time.sleep(0.4)
        if win32gui.GetForegroundWindow() == hwnd:
            return True
    return False


def capture(name, doc, settle, scroll=0, preview_x=0.48):
    print(f"[+] {name}: {doc}")

    appsession.clear_recovery_vault()
    proc = subprocess.Popen([EXE, stage(doc)])
    hwnd = None
    for _ in range(40):
        time.sleep(0.5)
        hwnd = find_window(proc.pid)
        if hwnd:
            break

    if not hwnd:
        print("    [!] window never appeared")
        proc.terminate()
        return

    is_foreground = foreground(hwnd)
    time.sleep(settle)
    scroll_preview(hwnd, scroll, preview_x)

    try:
        shot = grab_window(hwnd)
    except Exception as exc:
        print(f"    [!] PrintWindow failed ({exc})")
        shot = None

    # PrintWindow returns a blank surface on some WebView2 compositor paths.
    # Only fall back to a screen grab when the app is verifiably on top, so the
    # capture can never contain another application's window.
    if shot is None or is_blank(shot):
        if not is_foreground:
            print("    [!] could not foreground the window; skipping rather than "
                  "grabbing whatever is on screen")
            proc.terminate()
            return
        left, top, right, bottom = win32gui.GetWindowRect(hwnd)
        shot = ImageGrab.grab(bbox=(left + 8, top + 8, right - 8, bottom - 8), all_screens=True)

    path = os.path.join(OUT, f"{name}.png")
    shot.save(path)
    print(f"    [ok] {path}  {shot.size[0]}x{shot.size[1]}")

    # Scoped to the process this function launched: a whole-image kill would also
    # take down an instance the user has open, unsaved work included.
    appsession.shutdown(proc)


def is_blank(img):
    """True when the render came back empty (all one colour) or nearly black."""
    small = img.convert("RGB").resize((64, 64))
    colors = small.getcolors(64 * 64) or []
    if len(colors) <= 2:
        return True
    pixels = list(small.getdata())
    mean = sum(sum(p) for p in pixels) / (len(pixels) * 3)
    return mean < 12


def main():
    if not os.path.exists(EXE):
        sys.exit(f"Build the desktop app first — not found: {EXE}")
    os.makedirs(OUT, exist_ok=True)

    try:
        appsession.require_app_closed()
    except appsession.AppAlreadyRunningError as exc:
        sys.exit(str(exc))

    wanted = sys.argv[1:] or list(SHOTS)
    unknown = [n for n in wanted if n not in SHOTS]
    if unknown:
        sys.exit(f"unknown shot(s): {', '.join(unknown)}. Available: {', '.join(SHOTS)}")

    for name in wanted:
        doc, overrides, settle, *rest = SHOTS[name]
        profile = {**BASE_OVERRIDES, **overrides}
        # A fresh backup per shot, restored immediately: a single fixed backup file
        # would go stale and later revert settings changed between runs.
        with appsession.settings_profile(profile, from_defaults=True):
            capture(name, doc, settle, *rest)
    print("[+] settings restored")


if __name__ == "__main__":
    main()
