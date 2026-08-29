r"""Regenerate the rendered outputs in examples/ from their Markdown sources.

Both formats come from the desktop app, driven over its loopback REST API — not
from the CLI. That matters: the CLI hands the Markdown straight to the exporter,
so it applies no AI-quirk normalization and leaves `\(...\)` / `\[...\]` math as
literal text, while the app's pipeline strips the quirks and compiles the same
math into native OMML. PDF likewise only exists in the WinUI host, because it is
produced by WebView2's print pipeline.

    python tools/capture/build_examples.py [name ...]
"""

import fitz  # PyMuPDF — used to verify a rendered PDF is not a blank page
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
EXAMPLES = os.path.join(ROOT, "examples")
EXE = os.path.join(ROOT, "marksmith-v2", "MarkSmith.Desktop", "bin", "x64", "Release",
                   "net8.0-windows10.0.19041.0", "win-x64", "Marksmith.exe")
SETTINGS = os.path.join(os.environ["LOCALAPPDATA"], "MarkSmith", "settings.json")
API = "http://127.0.0.1:47821"
STAGE = r"C:\Users\Public\Marksmith Examples"

# What examples/README.md documents: GitHub Light, one continuous page, quirks cleaned.
RENDER_SETTINGS = {
    "Theme": "GitHub Light",
    "ApiEnabled": True,
    "ApiPort": 47821,
    "UnlimitedHeight": True,
    "A4FixedWidth": True,
    "ContentWidth": 794,
    "NormalizeLlm": True,
    "MermaidEnabled": True,
    "OutputFolder": STAGE,
}

DOCS = ["chatgpt-export", "math-cheatsheet", "product-spec"]


def post(path, payload, timeout=240):
    req = urllib.request.Request(API + path, data=json.dumps(payload).encode(),
                                 headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=timeout)


def wait_for_api(timeout=60):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"{API}/api/health", timeout=2) as r:
                if r.status == 200:
                    return True
        except Exception:
            time.sleep(0.5)
    return False


def launch():
    """Start the app and wait for its local API. Returns the process."""
    # Otherwise a previous force-kill leaves a "Recover unsaved document" modal.
    shutil.rmtree(os.path.join(os.path.dirname(SETTINGS), "recovery_vault"), ignore_errors=True)
    proc = subprocess.Popen([EXE])
    if not wait_for_api():
        proc.terminate()
        sys.exit("The desktop app's local REST API never came up")
    return proc


def shutdown(proc):
    proc.terminate()
    time.sleep(1.5)
    subprocess.run(["taskkill", "/F", "/IM", "Marksmith.exe"], capture_output=True, check=False)
    time.sleep(1)


def verify_pdf(path):
    """A PDF that renders nothing is worse than no PDF — never ship one unchecked.

    Running a /api/convert straight after a /api/batch in the same app session has
    been observed to return a blank page, so every render is proved to carry text
    or vector content before it replaces the committed file.
    """
    with fitz.open(path) as doc:
        chars = sum(len(page.get_text()) for page in doc)
        vectors = sum(len(page.get_drawings()) for page in doc)
    if chars == 0 and vectors == 0:
        raise RuntimeError(f"{os.path.basename(path)} rendered as a blank page")
    return chars, vectors


def main():
    names = sys.argv[1:] or DOCS
    if not os.path.exists(EXE):
        sys.exit(f"Build the desktop app first — not found: {EXE}")

    backup = SETTINGS + ".examples-backup"
    if not os.path.exists(backup):
        shutil.copy2(SETTINGS, backup)
    with open(SETTINGS, encoding="utf-8") as fh:
        cfg = json.load(fh)
    cfg.update(RENDER_SETTINGS)
    with open(SETTINGS, "w", encoding="utf-8") as fh:
        json.dump(cfg, fh, indent=2)

    shutil.rmtree(STAGE, ignore_errors=True)
    os.makedirs(STAGE, exist_ok=True)
    for name in names:
        shutil.copy2(os.path.join(EXAMPLES, f"{name}.md"), STAGE)

    try:
        # Pass 1 — DOCX. /api/batch runs the app's own export pipeline over the
        # folder. (/api/convert with format=docx currently 500s; see the PR notes.)
        proc = launch()
        try:
            with post("/api/batch", {"folder": STAGE, "format": "docx"}) as resp:
                result = json.load(resp)
            if result.get("failed"):
                sys.exit(f"batch reported failures: {result}")
            for name in names:
                target = os.path.join(EXAMPLES, f"{name}.docx")
                shutil.copy2(os.path.join(STAGE, f"{name}.docx"), target)
                print(f"[docx] {target}  {os.path.getsize(target):,} bytes")
        finally:
            shutdown(proc)

        # Pass 2 — PDF, in a fresh session so the WebView is not still busy.
        for name in names:
            proc = launch()
            try:
                with open(os.path.join(EXAMPLES, f"{name}.md"), encoding="utf-8") as fh:
                    markdown = fh.read()
                time.sleep(3)
                with post("/api/convert", {"markdown": markdown,
                                           "theme": RENDER_SETTINGS["Theme"],
                                           "normalize": True,
                                           "format": "pdf"}) as resp:
                    if resp.headers.get("Content-Type") != "application/pdf":
                        sys.exit(f"expected PDF for {name}, got {resp.headers.get('Content-Type')}")
                    data = resp.read()
            finally:
                shutdown(proc)

            pdf = os.path.join(EXAMPLES, f"{name}.pdf")
            scratch = pdf + ".new"
            with open(scratch, "wb") as fh:
                fh.write(data)
            try:
                chars, vectors = verify_pdf(scratch)
            except Exception:
                os.remove(scratch)
                raise
            os.replace(scratch, pdf)
            print(f"[pdf ] {pdf}  {os.path.getsize(pdf):,} bytes  "
                  f"({chars:,} chars, {vectors:,} vectors)")
    finally:
        shutil.copy2(backup, SETTINGS)
        shutil.rmtree(STAGE, ignore_errors=True)
        print("[+] settings restored")


if __name__ == "__main__":
    main()
