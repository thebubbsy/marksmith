"""Shared, safe handling of the desktop app and the user's settings file.

Every capture script drives the real Marksmith app against the real user profile,
so two things have to be true and were not:

* **Never touch a process this script did not start.** A whole-image
  ``taskkill /IM Marksmith.exe`` also force-kills an instance the user has open,
  bypassing its close path and discarding unsaved work. Shutdown is scoped to the
  launched pid, and a run refuses to start while another instance is up — the
  local API would otherwise answer from *that* instance and the script would
  drive, then kill, the wrong app.

* **Never widen the settings backup.** A backup kept at a fixed path and written
  only when absent goes stale: the first run's snapshot is restored after every
  later run, silently reverting anything changed in between. Each run takes its
  own backup and removes it once restored.
"""

import contextlib
import json
import os
import shutil
import subprocess
import tempfile
import time
import urllib.request

SETTINGS = os.path.join(os.environ["LOCALAPPDATA"], "MarkSmith", "settings.json")
CONFIG_DIR = os.path.dirname(SETTINGS)
IMAGE_NAME = "Marksmith.exe"


class AppAlreadyRunningError(RuntimeError):
    """Raised when Marksmith is open, so the script would drive the wrong process."""


def is_app_running():
    out = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq {IMAGE_NAME}", "/NH"],
        capture_output=True, text=True, check=False,
    ).stdout
    return IMAGE_NAME.lower() in out.lower()


def require_app_closed():
    if is_app_running():
        raise AppAlreadyRunningError(
            f"{IMAGE_NAME} is already running. Close it first — this script drives the app "
            "through its local API and would otherwise attach to (and shut down) your session."
        )


@contextlib.contextmanager
def settings_profile(values, *, from_defaults=False):
    """Apply settings for the duration of the block, then restore them exactly.

    ``from_defaults=True`` writes only ``values``, so every other setting falls back to the
    application's own defaults. That is what makes a run reproducible: merging into the user's
    settings would let their theme, fonts, ToC or layout preferences leak into the output.
    ``SettingsVersion`` is carried through because its *absence* triggers a one-time migration.
    """
    os.makedirs(CONFIG_DIR, exist_ok=True)
    existing = {}
    if os.path.exists(SETTINGS):
        with open(SETTINGS, encoding="utf-8") as fh:
            existing = json.load(fh)

    handle, backup = tempfile.mkstemp(prefix="marksmith-settings-", suffix=".bak")
    os.close(handle)
    if os.path.exists(SETTINGS):
        shutil.copy2(SETTINGS, backup)
    else:
        os.remove(backup)
        backup = None

    if from_defaults:
        profile = dict(values)
        # A settings file with no SettingsVersion key is treated as pre-schema and migrated.
        profile.setdefault("SettingsVersion", existing.get("SettingsVersion", 2))
    else:
        profile = dict(existing)
        profile.update(values)

    with open(SETTINGS, "w", encoding="utf-8") as fh:
        json.dump(profile, fh, indent=2)

    try:
        yield
    finally:
        if backup:
            shutil.copy2(backup, SETTINGS)
            os.remove(backup)
        elif os.path.exists(SETTINGS):
            os.remove(SETTINGS)


def clear_recovery_state():
    """Drop crash-recovery state so the next launch has no 'Recover unsaved document' modal.

    Two separate mechanisms write recovery data and only one of them is the vault.
    The modal is driven by ``autosave_recovery.md`` — the autosave mirror of an unsaved
    paste buffer — which every API-driven run creates as a side effect of ingesting a
    document. Clearing only ``recovery_vault/`` left that file behind, so each capture
    run armed the modal for the *next* one and the following run filmed a dialog box
    over the window.
    """
    shutil.rmtree(os.path.join(CONFIG_DIR, "recovery_vault"), ignore_errors=True)
    with contextlib.suppress(OSError):
        os.remove(os.path.join(CONFIG_DIR, "autosave_recovery.md"))


def launch(exe, args=(), *, api_url=None, timeout=60):
    """Start the app (refusing to run alongside an existing instance) and wait for its API."""
    require_app_closed()
    clear_recovery_state()
    proc = subprocess.Popen([exe, *args])

    if api_url:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if proc.poll() is not None:
                raise RuntimeError(f"{IMAGE_NAME} exited before its API came up")
            try:
                with urllib.request.urlopen(api_url, timeout=2) as resp:
                    if resp.status == 200:
                        return proc
            except Exception:
                time.sleep(0.5)
        shutdown(proc)
        raise TimeoutError("the app's local REST API never came up")

    return proc


def shutdown(proc, *, grace=8):
    """Close the launched process (and only it), escalating to its own pid if it will not exit."""
    if proc is None or proc.poll() is not None:
        return
    proc.terminate()
    try:
        proc.wait(timeout=grace)
        return
    except subprocess.TimeoutExpired:
        pass
    # /T covers the WebView2 children the app spawns; /PID keeps it to this process tree.
    subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                   capture_output=True, check=False)
    with contextlib.suppress(subprocess.TimeoutExpired):
        proc.wait(timeout=grace)
