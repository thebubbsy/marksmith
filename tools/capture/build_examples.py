r"""Regenerate the rendered outputs in examples/ from their Markdown sources.

Both formats come from the desktop app, driven over its loopback REST API — not from the CLI. That
matters: the CLI hands the Markdown straight to the exporter, so it applies no AI-quirk
normalization and leaves `\(...\)` / `\[...\]` math as literal text, while the app's pipeline strips
the quirks and compiles the same math into native OMML. PDF likewise only exists in the WinUI host,
because it is produced by WebView2's print pipeline.

The rendering settings are a complete, fixed profile rather than an overlay on whatever the user has
configured — otherwise a local theme, font or table-of-contents preference would leak into the
committed artifacts and the "reproduce the set" claim would not hold.

    python tools/capture/build_examples.py [name ...]
"""

import json
import os
import shutil
import sys
import time
import urllib.request

import fitz  # PyMuPDF — used to verify a rendered PDF is not a blank page

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import appsession  # noqa: E402  (path set up above)

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
EXAMPLES = os.path.join(ROOT, "examples")
EXE = os.path.join(ROOT, "marksmith-v2", "MarkSmith.Desktop", "bin", "x64", "Release",
                   "net8.0-windows10.0.19041.0", "win-x64", "Marksmith.exe")
API = "http://127.0.0.1:47821"
STAGE = r"C:\Users\Public\Marksmith Examples"

# The complete rendering profile. Everything not named here falls back to the application's own
# defaults, so two machines produce the same bytes. Keep it explicit rather than inherited.
RENDER_SETTINGS = {
    "Theme": "GitHub Light",
    "ApiEnabled": True,
    "ApiPort": 47821,
    "UnlimitedHeight": True,
    "A4FixedWidth": True,
    "ContentWidth": 794,
    "IncludeToc": True,
    "NormalizeLlm": True,
    "MermaidEnabled": True,
    "TargetFormat": "pdf",
    "FileNameTemplate": "{title}",
    "OutputFolder": STAGE,
}

DOCS = ["chatgpt-export", "math-cheatsheet", "product-spec"]


def post(path, payload, timeout=240):
    req = urllib.request.Request(API + path, data=json.dumps(payload).encode(),
                                 headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=timeout)


def verify_pdf(path):
    """A PDF that renders nothing is worse than no PDF — never ship one unchecked.

    Running a /api/convert straight after a /api/batch in the same app session has been observed to
    return a blank page, so every render is proved to carry text or vector content before it
    replaces the committed file.
    """
    with fitz.open(path) as doc:
        chars = sum(len(page.get_text()) for page in doc)
        vectors = sum(len(page.get_drawings()) for page in doc)
    if chars == 0 and vectors == 0:
        raise RuntimeError(f"{os.path.basename(path)} rendered as a blank page")
    return chars, vectors


def stage_sources(names):
    """Copy the requested sources into the staging folder, validating names first."""
    missing = [n for n in names if not os.path.exists(os.path.join(EXAMPLES, f"{n}.md"))]
    if missing:
        raise FileNotFoundError(
            f"no such example(s): {', '.join(missing)}. Available: "
            + ", ".join(sorted(f[:-3] for f in os.listdir(EXAMPLES) if f.endswith('.md'))))

    shutil.rmtree(STAGE, ignore_errors=True)
    os.makedirs(STAGE, exist_ok=True)
    for name in names:
        shutil.copy2(os.path.join(EXAMPLES, f"{name}.md"), STAGE)


def build_docx(names):
    """/api/batch runs the app's own export pipeline over the staged folder.

    (/api/convert with format=docx currently 500s — see the PR notes.)
    """
    proc = appsession.launch(EXE, api_url=f"{API}/api/health")
    try:
        with post("/api/batch", {"folder": STAGE, "format": "docx"}) as resp:
            result = json.load(resp)
        if result.get("failed"):
            raise RuntimeError(f"batch reported failures: {result}")
        for name in names:
            target = os.path.join(EXAMPLES, f"{name}.docx")
            shutil.copy2(os.path.join(STAGE, f"{name}.docx"), target)
            print(f"[docx] {target}  {os.path.getsize(target):,} bytes")
    finally:
        appsession.shutdown(proc)


def build_pdf(name):
    """Rendered in its own app session, so the WebView is not still busy from the batch."""
    proc = appsession.launch(EXE, api_url=f"{API}/api/health")
    try:
        with open(os.path.join(EXAMPLES, f"{name}.md"), encoding="utf-8") as fh:
            markdown = fh.read()
        time.sleep(3)
        with post("/api/convert", {"markdown": markdown,
                                   "theme": RENDER_SETTINGS["Theme"],
                                   "normalize": True,
                                   "format": "pdf"}) as resp:
            if resp.headers.get("Content-Type") != "application/pdf":
                raise RuntimeError(
                    f"expected PDF for {name}, got {resp.headers.get('Content-Type')}")
            data = resp.read()
    finally:
        appsession.shutdown(proc)

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
    print(f"[pdf ] {pdf}  {os.path.getsize(pdf):,} bytes  ({chars:,} chars, {vectors:,} vectors)")


def main():
    names = sys.argv[1:] or DOCS
    if not os.path.exists(EXE):
        sys.exit(f"Build the desktop app first — not found: {EXE}")

    try:
        appsession.require_app_closed()
    except appsession.AppAlreadyRunningError as exc:
        sys.exit(str(exc))

    # Everything that touches the user's machine happens inside the profile guard, staging and
    # source validation included: a bad example name used to raise after the settings had already
    # been rewritten, leaving them pointed at the staging folder.
    try:
        with appsession.settings_profile(RENDER_SETTINGS, from_defaults=True):
            stage_sources(names)
            build_docx(names)
            for name in names:
                build_pdf(name)
    finally:
        shutil.rmtree(STAGE, ignore_errors=True)
    print("[+] settings restored")


if __name__ == "__main__":
    main()
