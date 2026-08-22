import os
import sys
import time
import subprocess
import ctypes
import win32gui
import win32con
from PIL import ImageGrab

# Set DPI awareness for pixel-perfect 1:1 screen captures
try:
    user32 = ctypes.windll.user32
    user32.SetProcessDPIAware()
except Exception:
    pass

EXE_PATH = os.path.abspath(r"marksmith-v2\MarkSmith.Desktop\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Marksmith.exe")
DOCS_DIR = os.path.abspath(r"docs\images")
NEW_DOCS_DIR = os.path.abspath(r"docs\images\_new")

os.makedirs(DOCS_DIR, exist_ok=True)
os.makedirs(NEW_DOCS_DIR, exist_ok=True)

def find_marksmith_hwnd():
    found_hwnds = []
    def enum_cb(hwnd, extra):
        if win32gui.IsWindowVisible(hwnd):
            title = win32gui.GetWindowText(hwnd)
            if "marksmith" in title.lower():
                found_hwnds.append((hwnd, title))
        return True
    win32gui.EnumWindows(enum_cb, None)
    return found_hwnds

def capture_for_doc(doc_path, output_filename):
    print(f"\n[+] Launching Marksmith with: {os.path.basename(doc_path)}")
    proc = subprocess.Popen([EXE_PATH, doc_path])
    time.sleep(5)  # Wait for WinUI 3 + WebView2 to render

    hwnds = find_marksmith_hwnd()
    if hwnds:
        hwnd, title = hwnds[0]
        print(f"[+] Found window: '{title}' (HWND: {hwnd})")
        win32gui.ShowWindow(hwnd, win32con.SW_MAXIMIZE)
        try:
            win32gui.SetForegroundWindow(hwnd)
        except Exception:
            pass
        time.sleep(3)  # Wait for maximize transition & layout redraw
    else:
        print("[!] Warning: Marksmith window not found in enumeration")

    print(f"[+] Capturing maximized desktop screenshot...")
    try:
        screenshot = ImageGrab.grab()
        if screenshot:
            out_path1 = os.path.join(DOCS_DIR, output_filename)
            screenshot.save(out_path1)
            print(f"[OK] Saved: {out_path1}")

            if output_filename == "hero.png":
                out_path2 = os.path.join(NEW_DOCS_DIR, output_filename)
                screenshot.save(out_path2)
                print(f"[OK] Saved: {out_path2}")
    except Exception as e:
        print(f"[!] ImageGrab error: {e}")

    try:
        proc.terminate()
    except Exception:
        pass
    time.sleep(1)

def main():
    print("=" * 65)
    print("  Marksmith Automated Desktop Screenshot Capture Tool")
    print("=" * 65)

    if not os.path.exists(EXE_PATH):
        print(f"[!] Error: Executable not found at:\n    {EXE_PATH}")
        sys.exit(1)

    # 1. Capture Hero (Gauntlet document with equations, SmartArt & tables)
    gauntlet_path = os.path.abspath(r"examples\gauntlet.md")
    capture_for_doc(gauntlet_path, "hero.png")

    # 2. Capture SmartArt / Architecture Spec
    prod_spec_path = os.path.abspath(r"examples\product-spec.md")
    capture_for_doc(prod_spec_path, "smartart-visualizer.png")

    # 3. Capture Math & Formulas
    math_path = os.path.abspath(r"examples\math-cheatsheet.md")
    capture_for_doc(math_path, "math-omml-preview.png")

    print("\n" + "=" * 65)
    print("  [SUCCESS] All desktop screenshots saved to docs/images/")
    print("=" * 65)

if __name__ == "__main__":
    main()
