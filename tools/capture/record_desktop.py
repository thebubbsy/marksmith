"""Record a Marksmith desktop demo without touching the user's input.

The app is driven over its own loopback REST API (`POST /api/ingest`, the same
channel the browser extension uses), and frames are pulled straight off the
window with PrintWindow. Nothing steals focus, nothing moves the pointer, and
no pixel outside the app's own window can end up in the recording.

    python tools/capture/record_desktop.py [out.mp4]
"""

import ctypes
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.request

import win32con
import win32gui
import win32process
import win32ui
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import appsession  # noqa: E402  (path set up above)

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
EXE = os.path.join(ROOT, "marksmith-v2", "MarkSmith.Desktop", "bin", "x64", "Release",
                   "net8.0-windows10.0.19041.0", "win-x64", "Marksmith.exe")
DEMO_DIR = r"C:\Users\Public\Marksmith Demo"
FRAMES = os.path.join(os.environ["TEMP"], "marksmith-frames")
API = "http://127.0.0.1:47821"

# A complete profile: everything unnamed falls back to the app's defaults, so the recording
# looks the same on any machine instead of inheriting local preferences.
RENDER_SETTINGS = {
    "EditorViewMode": "Split",
    "Theme": "GitHub Light",
    "PreviewZoom": 0.8,
    "EditorFontSize": 14,
    "IncludeToc": False,
    "ShowExtensionTip": False,
    "LookingGlassMode": False,
    "ApiEnabled": True,
    "ApiPort": 47821,
    "OutputFolder": DEMO_DIR,
}
FPS = 6

DOC = r"""# Q4 Platform Review

> [!IMPORTANT]
> Pasted from a chat. Exported as a real Word document.

## Availability

| Region | Uptime | Error budget |
| :--- | ---: | ---: |
| ap-southeast-2 | 99.98% | 41% remaining |
| us-east-1 | 99.95% | 12% remaining |
| eu-west-1 | 99.99% | 78% remaining |

The burn rate follows $B = \frac{1 - S_{obs}}{1 - S_{target}}$ over a rolling 28-day window,
so a single regional incident cannot exhaust the quarter.

$$
\text{SLO}_{\text{composite}} = \prod_{i=1}^{n} S_i^{\,w_i}, \qquad \sum_{i=1}^{n} w_i = 1
$$

## Ingest pipeline

```mermaid
flowchart LR
  Chat[AI chat reply] --> Detect[Detect source]
  Detect --> Clean[Normalize quirks]
  Clean --> AST[Markdig AST]
  AST --> OOXML[Native OOXML]
  OOXML --> Word[(Word .docx)]
  AST --> PDF[(PDF)]
```

## Actions

- [x] Publish the regional breakdown
- [ ] Re-baseline the composite SLO
- [ ] Hand the signed spec to compliance
"""



def api_ready(timeout=40):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"{API}/api/health", timeout=2) as r:
                if r.status == 200:
                    return True
        except Exception:
            time.sleep(0.5)
    return False


def find_window(pid):
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


def grab(hwnd):
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    w, h = right - left, bottom - top
    dc = win32gui.GetWindowDC(hwnd)
    src = win32ui.CreateDCFromHandle(dc)
    mem = src.CreateCompatibleDC()
    bmp = win32ui.CreateBitmap()
    bmp.CreateCompatibleBitmap(src, w, h)
    mem.SelectObject(bmp)
    ctypes.windll.user32.PrintWindow(hwnd, mem.GetSafeHdc(), 2)
    info = bmp.GetInfo()
    img = Image.frombuffer("RGB", (info["bmWidth"], info["bmHeight"]),
                           bmp.GetBitmapBits(True), "raw", "BGRX", 0, 1)
    win32gui.DeleteObject(bmp.GetHandle())
    mem.DeleteDC()
    src.DeleteDC()
    win32gui.ReleaseDC(hwnd, dc)
    return img.crop((8, 8, img.width - 8, img.height - 8))


def ingest(markdown):
    body = json.dumps({"markdown": markdown}).encode()
    req = urllib.request.Request(f"{API}/api/ingest", data=body,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        return resp.status



def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "docs/media/desktop-demo.mp4")
    if not os.path.exists(EXE):
        sys.exit(f"Build the desktop app first — not found: {EXE}")

    try:
        appsession.require_app_closed()
    except appsession.AppAlreadyRunningError as exc:
        sys.exit(str(exc))

    shutil.rmtree(FRAMES, ignore_errors=True)
    os.makedirs(FRAMES, exist_ok=True)
    os.makedirs(DEMO_DIR, exist_ok=True)

    starter = os.path.join(DEMO_DIR, "q4-platform-review.md")
    with open(starter, "w", encoding="utf-8") as fh:
        fh.write("# Q4 Platform Review\n")

    with appsession.settings_profile(RENDER_SETTINGS, from_defaults=True):
        record(starter, out)


def record(starter, out):
    proc = appsession.launch(EXE, [starter])
    hwnd = None
    for _ in range(60):
        time.sleep(0.5)
        hwnd = find_window(proc.pid)
        if hwnd:
            break
    if not hwnd:
        appsession.shutdown(proc)
        sys.exit("Marksmith window never appeared")

    win32gui.ShowWindow(hwnd, win32con.SW_MAXIMIZE)
    time.sleep(8)

    if not api_ready():
        print("[!] local REST API never came up; recording the idle window instead")

    stop = threading.Event()
    count = [0]

    def recorder():
        interval = 1 / FPS
        while not stop.is_set():
            started = time.time()
            try:
                grab(hwnd).save(os.path.join(FRAMES, f"f{count[0]:05d}.png"))
                count[0] += 1
            except Exception:
                pass
            time.sleep(max(0, interval - (time.time() - started)))

    thread = threading.Thread(target=recorder, daemon=True)
    thread.start()

    # Reveal the document section by section, the way a reply streams in.
    time.sleep(2)
    lines = DOC.split("\n")
    checkpoints = [i for i, ln in enumerate(lines) if ln.startswith("#")] + [len(lines)]
    for stop_at in checkpoints[1:]:
        try:
            ingest("\n".join(lines[:stop_at]))
        except Exception as exc:
            print(f"[!] ingest failed: {exc}")
        time.sleep(2.6)

    time.sleep(6)
    stop.set()
    thread.join(timeout=5)

    # Scoped to the process launched here — never a whole-image kill, which would
    # also destroy an instance the user has open.
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


if __name__ == "__main__":
    main()
