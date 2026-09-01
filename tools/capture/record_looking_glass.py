"""Record the Looking Glass portal — the one feature a screenshot cannot explain.

The document is streamed in over the app's own loopback REST API, exactly like
``record_desktop.py``. The difference is the second half: a portal is a *pointer*
gesture (click the rendered preview, the source behind it opens through an
aperture), so this recorder deliberately drives real mouse and keyboard input
where the other one deliberately does not. That is why it is a separate script
rather than a flag on ``record_desktop.py``: that module promises in its own
docstring that nothing steals focus and nothing moves the pointer, and a
recording that quietly broke the promise would be worse than a second file.

It records in **split view** on purpose. The portal's whole point is that one
edit lands in three places at once — the aperture, the rendered page behind it,
and the Markdown editor in the other pane — and a preview-only recording can
only ever show two of them.

Two safeguards make the input acceptable:

* **The window must be foreground before a single click is sent.** Otherwise the
  clicks and keystrokes would land in whatever the user happens to have focused.
  If it cannot be brought forward, the run aborts rather than typing blind.
* **The pointer is put back.** The cursor position is saved on entry and restored
  in a ``finally``, so the recording does not leave the mouse parked mid-screen.

Frames come from PrintWindow, so the system cursor is not in them; the in-page
ring that tracks the pointer is, which is the indicator worth showing anyway.

    python tools/capture/record_looking_glass.py [out.mp4]
    python tools/capture/record_looking_glass.py --probe   # one still, to aim the clicks
"""

import ctypes
import os
import shutil
import subprocess
import sys
import threading
import time
from ctypes import wintypes

import win32con
import win32gui
import win32process

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import appsession  # noqa: E402  (path set up above)
import capture_desktop  # noqa: E402  — foreground() and the DPI setup
import record_desktop  # noqa: E402  — find_window/grab/ingest/api_ready and the Q4 document

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004

ROOT = record_desktop.ROOT
EXE = record_desktop.EXE
DEMO_DIR = record_desktop.DEMO_DIR
FRAMES = os.path.join(os.environ["TEMP"], "marksmith-portal-frames")
FPS = 6

# A complete profile, so the recording looks the same on any machine.
#
# The portal dials are named explicitly because their defaults are tuned for reading,
# not for filming. Two in particular:
#   * PreviewZoom sits well under the default — the page has to read as a *page*, not
#     as one zoomed-in paragraph, or the portal has nothing recognisable to cut through.
#   * PortalSurroundBlurRadius is a hint, not a curtain. Enough to push the surrounding
#     page back; not so much that you cannot see it re-rendering as the portal is typed
#     into, which is the entire point of the shot.
RENDER_SETTINGS = {
    "HasSeenWelcome": True,
    "HasSeenCoffeeReminder": True,
    "EditorViewMode": "Split",
    "Theme": "GitHub Light",
    "PreviewZoom": 0.7,
    "EditorFontSize": 13,
    "IncludeToc": False,
    "ShowExtensionTip": False,
    "ApiEnabled": True,
    "ApiPort": 47821,
    "OutputFolder": DEMO_DIR,

    "LookingGlassMode": True,
    "PortalShape": "circle",
    "PortalRevealScope": 52,
    "PortalFocusBlur": True,
    "PortalSurroundBlurRadius": 2.0,   # a hint of defocus, not a curtain
    "PortalInsideBlur": False,         # keep the revealed source crisp on camera
    "PortalInsideBlurRadius": 0.0,
}

# Where to click, as a fraction of the window. In split view the preview is the right
# half of the centre column, so these sit well right of centre. Verify with --probe
# after any layout change rather than guessing.
PORTAL_BEATS = [
    (0.58, 0.36),
    (0.58, 0.60),
]

# Typed through the aperture. Short, and visibly Markdown, so the payoff is legible at
# video bitrates: the editor on the left gains the same characters, and the page behind
# the glass grows a real bold run and a real bullet.
TYPED = "\n\n**Typed through the glass** — and it lands in all three panes at once.\n"


# --- real keyboard input -----------------------------------------------------------
# SendInput with KEYEVENTF_UNICODE rather than keybd_event + virtual key codes: the
# text is typed by codepoint, so it does not depend on the active keyboard layout.

INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004


class _KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", wintypes.WPARAM)]


class _MOUSEINPUT(ctypes.Structure):
    """Unused, but the union must be laid out for it.

    SendInput rejects any cbSize that is not sizeof(INPUT) — and INPUT is sized by its
    *largest* arm, MOUSEINPUT. A union declaring only KEYBDINPUT is 32 bytes on x64
    instead of 40, and every call fails with ERROR_INVALID_PARAMETER (87).
    """
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG),
                ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", wintypes.WPARAM)]


class _INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("ki", _KEYBDINPUT), ("mi", _MOUSEINPUT)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]


def _key_event(vk, scan, flags):
    """Build one INPUT. The union is anonymous, so ``ki`` is assigned, not constructed."""
    ev = _INPUT()
    ev.type = INPUT_KEYBOARD
    ev.ki = _KEYBDINPUT(wVk=vk, wScan=scan, dwFlags=flags, time=0, dwExtraInfo=0)
    return ev


def _send_unicode(ch):
    # Enter is not a printable codepoint — a unicode "\n" event does nothing in a
    # textarea, so newlines go through the VK_RETURN path instead.
    if ch == "\n":
        pair = (_key_event(win32con.VK_RETURN, 0, 0),
                _key_event(win32con.VK_RETURN, 0, KEYEVENTF_KEYUP))
    else:
        code = ord(ch)
        pair = (_key_event(0, code, KEYEVENTF_UNICODE),
                _key_event(0, code, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP))
    for ev in pair:
        sent = ctypes.windll.user32.SendInput(1, ctypes.byref(ev), ctypes.sizeof(_INPUT))
        # A silently-dropped keystroke is the difference between a demo and a dud, and
        # SendInput reports the drop only through its return value.
        if sent != 1:
            raise OSError(f"SendInput dropped a keystroke "
                          f"(GetLastError={ctypes.windll.kernel32.GetLastError()})")


def app_is_foreground(pid):
    """True when the foreground window belongs to the app.

    Deliberately checked by process, not by handle. WinUI 3 hands the foreground to
    whichever of its own top-level windows owns input — which is not always the frame
    window ``find_window`` returned — so an identity check against that one handle
    reports "lost the foreground" while the app is plainly focused and typing would
    have worked. What matters is only that the keystrokes cannot land in someone
    else's window.
    """
    fg = win32gui.GetForegroundWindow()
    if not fg:
        return False
    return win32process.GetWindowThreadProcessId(fg)[1] == pid


def type_text(pid, hwnd, text, cps=14):
    """Type at a readable pace — fast enough not to drag, slow enough to watch land."""
    # Keystrokes go to whatever has focus, not to a window handle. Ten seconds of
    # ingest happen before this point, so re-assert the foreground rather than trusting
    # the one taken at launch — and refuse to type into someone else's window.
    if not app_is_foreground(pid):
        capture_desktop.foreground(hwnd)
        time.sleep(0.5)
    if not app_is_foreground(pid):
        raise RuntimeError("Marksmith lost the foreground; refusing to type into another window")

    delay = 1.0 / cps
    for ch in text:
        _send_unicode(ch)
        time.sleep(delay)


def press(vk):
    ctypes.windll.user32.keybd_event(vk, 0, 0, 0)
    ctypes.windll.user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


# --- pointer -----------------------------------------------------------------------

def click_at(hwnd, fx, fy):
    """Click a point given as a fraction of the window rect."""
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    x = int(left + (right - left) * fx)
    y = int(top + (bottom - top) * fy)
    ctypes.windll.user32.SetCursorPos(x, y)
    time.sleep(0.35)
    ctypes.windll.user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.06)
    ctypes.windll.user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)


def glide(hwnd, start, end, steps=12):
    """Sweep the pointer between two fractional points so the tracking ring is visible."""
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    w, h = right - left, bottom - top
    for i in range(steps + 1):
        t = i / steps
        x = int(left + w * (start[0] + (end[0] - start[0]) * t))
        y = int(top + h * (start[1] + (end[1] - start[1]) * t))
        ctypes.windll.user32.SetCursorPos(x, y)
        time.sleep(0.05)


# --- run ---------------------------------------------------------------------------

def start_app():
    proc = appsession.launch(EXE, [os.path.join(DEMO_DIR, "q4-platform-review.md")])
    hwnd = None
    for _ in range(60):
        time.sleep(0.5)
        hwnd = record_desktop.find_window(proc.pid)
        if hwnd:
            break
    if not hwnd:
        appsession.shutdown(proc)
        sys.exit("Marksmith window never appeared")

    win32gui.ShowWindow(hwnd, win32con.SW_MAXIMIZE)
    time.sleep(6)

    # Input is only safe once the app owns the foreground; otherwise clicks and
    # keystrokes land in whatever the user was doing. capture_desktop.foreground()
    # reports success only when the *frame* window is foreground, which WinUI 3 often
    # is not even when the app plainly has focus — so its return value is a hint and
    # app_is_foreground() is the decision.
    capture_desktop.foreground(hwnd)
    for _ in range(5):
        if app_is_foreground(proc.pid):
            return proc, hwnd
        time.sleep(0.5)
        capture_desktop.foreground(hwnd)

    appsession.shutdown(proc)
    sys.exit("could not bring Marksmith to the foreground; refusing to send blind input")


def probe(out_png):
    """Launch, ingest the document, save one still, quit. Used to aim PORTAL_BEATS."""
    proc, hwnd = start_app()
    if record_desktop.api_ready():
        record_desktop.ingest(record_desktop.DOC)
    time.sleep(6)
    record_desktop.grab(hwnd).save(out_png)
    print(f"[ok] {out_png}")
    appsession.shutdown(proc)


def record(out):
    proc, hwnd = start_app()

    if not record_desktop.api_ready():
        appsession.shutdown(proc)
        sys.exit("the app's local REST API never came up")

    stop = threading.Event()
    count = [0]

    def recorder():
        interval = 1 / FPS
        while not stop.is_set():
            started = time.time()
            try:
                record_desktop.grab(hwnd).save(os.path.join(FRAMES, f"f{count[0]:05d}.png"))
                count[0] += 1
            except Exception:
                pass
            time.sleep(max(0, interval - (time.time() - started)))

    thread = threading.Thread(target=recorder, daemon=True)
    thread.start()

    origin = win32gui.GetCursorPos()
    try:
        # Act one: the document arrives section by section, the way a reply streams in.
        time.sleep(2)
        lines = record_desktop.DOC.split("\n")
        checkpoints = [i for i, ln in enumerate(lines) if ln.startswith("#")] + [len(lines)]
        for stop_at in checkpoints[1:]:
            try:
                record_desktop.ingest("\n".join(lines[:stop_at]))
            except Exception as exc:
                print(f"[!] ingest failed: {exc}")
            time.sleep(2.0)

        # Long, and deliberately so. Each ingest re-navigates the preview, and a
        # re-navigation tears down any open portal — so a click taken too soon opens an
        # aperture that the in-flight render silently destroys a moment later. The whole
        # of act two then plays out against a page with no portal on it.
        time.sleep(8.0)

        # Act two: open the glass. Sweep first so the tracking ring reads on camera.
        glide(hwnd, (0.58, 0.20), PORTAL_BEATS[0])
        click_at(hwnd, *PORTAL_BEATS[0])
        time.sleep(2.0)

        # The click that *opens* a portal is handled by the page's capture-phase listener,
        # which calls preventDefault — so it never reaches the aperture's textarea and the
        # caret is not in it. A second click at the same spot is inside the now-open portal,
        # where that listener bails out early and the textarea gets a normal click: focus,
        # caret, and keystrokes that actually land. Without this the typing goes nowhere.
        click_at(hwnd, *PORTAL_BEATS[0])
        time.sleep(1.0)

        # The caret lands wherever in the matched line the click fell, which is usually
        # mid-word — typing there splits a sentence ("cannot e|xhaust") and the shot reads
        # as a typo rather than a feature. End puts it at the end of the line first.
        press(win32con.VK_END)
        time.sleep(0.4)

        # Act three: the payoff. Type through the aperture — the characters appear in
        # the glass, the page behind it re-renders in place, and the editor in the left
        # pane gains the same text, all without the portal closing.
        type_text(proc.pid, hwnd, TYPED)
        time.sleep(4.0)

        # Move the glass down the page to show it is not a one-shot overlay.
        glide(hwnd, PORTAL_BEATS[0], PORTAL_BEATS[1])
        click_at(hwnd, *PORTAL_BEATS[1])
        time.sleep(3.0)

        press(win32con.VK_ESCAPE)
        time.sleep(3.0)
    finally:
        # Shutdown belongs here, not after the block: an exception mid-beat used to
        # restore the cursor and then leak the app, so the *next* run refused to start
        # ("Marksmith.exe is already running") against a stray instance of its own making.
        ctypes.windll.user32.SetCursorPos(*origin)
        stop.set()
        thread.join(timeout=5)
        appsession.shutdown(proc)

    print(f"[+] {count[0]} frames captured")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    subprocess.run([
        "ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS),
        "-i", os.path.join(FRAMES, "f%05d.png"),
        "-vf", "scale=1400:-2:flags=lanczos", "-c:v", "libx264",
        "-pix_fmt", "yuv420p", "-crf", "22", "-movflags", "+faststart", out,
    ], check=True)
    print(f"[ok] {out}")
    print(f"[i] frames kept for stills: {FRAMES}")


def main():
    args = [a for a in sys.argv[1:] if a != "--probe"]
    probing = "--probe" in sys.argv[1:]
    out = os.path.abspath(args[0] if args else
                          ("docs/media/_probe.png" if probing
                           else "docs/media/looking-glass.mp4"))

    if not os.path.exists(EXE):
        sys.exit(f"Build the desktop app first — not found: {EXE}")

    try:
        appsession.require_app_closed()
    except appsession.AppAlreadyRunningError as exc:
        sys.exit(str(exc))

    if not probing:
        shutil.rmtree(FRAMES, ignore_errors=True)
        os.makedirs(FRAMES, exist_ok=True)
    os.makedirs(DEMO_DIR, exist_ok=True)

    with open(os.path.join(DEMO_DIR, "q4-platform-review.md"), "w", encoding="utf-8") as fh:
        fh.write("# Q4 Platform Review\n")

    with appsession.settings_profile(RENDER_SETTINGS, from_defaults=True):
        (probe if probing else record)(out)


if __name__ == "__main__":
    main()
