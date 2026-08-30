"""Verify every relative link and image in the published docs resolves to a *tracked* file.

Checking the working tree is not enough: `docs/MD_ENGINE_GOVERNANCE.md` and `FIVE-YEAR-PLAN.md`
both exist locally but are gitignored as internal documents, so links to them passed a filesystem
check and 404'd on GitHub. Only what git actually publishes counts.

    python tools/check_links.py            # check tracked markdown
    python tools/check_links.py README.md  # check specific files
"""

import re
import subprocess
import sys

FENCE_RE = re.compile(r"^```.*?^```", re.S | re.M)
CODE_SPAN_RE = re.compile(r"`[^`\n]*`")
LINK_RE = re.compile(r"\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
SRC_RE = re.compile(r'\b(?:src|href)="([^"]+)"')


def tracked_files():
    out = subprocess.run(["git", "ls-files"], capture_output=True, text=True, check=True)
    return {line.replace("\\", "/") for line in out.stdout.splitlines() if line}


def strip_code(text):
    """Drop fenced blocks and inline code: a link written there documents syntax, not a target."""
    text = FENCE_RE.sub("", text)
    return CODE_SPAN_RE.sub("", text)


def targets(text):
    for match in LINK_RE.finditer(text):
        yield match.group(1)
    for match in SRC_RE.finditer(text):
        yield match.group(1)


def main():
    files = sys.argv[1:] or [f for f in sorted(tracked_files()) if f.endswith(".md")]
    tracked = tracked_files()
    dirs = {d for f in tracked for d in _parents(f)}

    broken = []
    for path in files:
        try:
            with open(path, encoding="utf-8") as fh:
                text = fh.read()
        except OSError as exc:
            broken.append((path, str(exc), "unreadable"))
            continue

        base = path.replace("\\", "/").rsplit("/", 1)[0] if "/" in path.replace("\\", "/") else ""
        for target in targets(strip_code(text)):
            if target.startswith(("http://", "https://", "mailto:", "#", "data:")):
                continue
            clean = target.split("#")[0].split("?")[0]
            if not clean:
                continue
            resolved = _normalize(f"{base}/{clean}" if base else clean)
            if resolved in tracked or resolved.rstrip("/") in dirs:
                continue
            broken.append((path, target, "not tracked by git"))

    for path, target, why in broken:
        print(f"BROKEN  {path} -> {target}  ({why})")
    print(f"\n{len(files)} file(s) checked, {len(broken)} broken link(s)")
    return 1 if broken else 0


def _parents(path):
    parts = path.split("/")
    for i in range(1, len(parts)):
        yield "/".join(parts[:i])


def _normalize(path):
    out = []
    for part in path.replace("\\", "/").split("/"):
        if part in ("", "."):
            continue
        if part == "..":
            if out:
                out.pop()
            continue
        out.append(part)
    return "/".join(out)


if __name__ == "__main__":
    sys.exit(main())
