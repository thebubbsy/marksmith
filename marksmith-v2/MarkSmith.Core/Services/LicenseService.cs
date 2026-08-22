using System.Text.Json;
using MarkSmith.Models;

namespace MarkSmith.Services;

// Resolves and persists the app's licensing state. Resolution order: a valid signed Pro key wins;
// otherwise a user-started trial (FULL Pro capped at 3 DOCX exports, consumed by successful exports);
// otherwise Free. There is NO automatic trial — the user starts it explicitly from Settings or the
// upgrade banner. State lives in %LOCALAPPDATA%\MarkSmith\license.json.
public sealed class LicenseService
{
    // Your Lemon Squeezy checkout link (the "Buy" button opens this). Replace with your product's
    // buy URL from Lemon Squeezy → Product → Share. See packaging/lemonsqueezy-setup.md.
    public const string StoreUrl = "https://YOUR-STORE.lemonsqueezy.com/buy/YOUR-PRODUCT-ID";

    // Go-live guard: until StoreUrl carries a real checkout link the UI's "Buy" buttons show a
    // "store not configured" status instead of launching the placeholder URL.
    public static bool IsStoreConfigured =>
        !StoreUrl.Contains("YOUR-STORE", StringComparison.OrdinalIgnoreCase)
        && !StoreUrl.Contains("YOUR-PRODUCT-ID", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly string _shadowPath;
    private StoredLicense _stored = new();

    // Serializes every state mutation (activate/start-trial/consume/reset/dev-toggle). The
    // DocxExportService chokepoint can call ConsumeDocxExport from concurrent exports (batch +
    // watch-folder + local API overlap); without this gate the -- on TrialExportsRemaining could
    // lose updates (a free export) and the primary/shadow file writes could interleave.
    private readonly object _gate = new();

    public LicenseState State { get; private set; } = new();
    public event Action? Changed;

    public bool IsPro => State.IsPro;
    public bool CanExportDocx => State.CanExportDocx;
    public bool CanExportPptx => State.CanExportPptx;
    public bool CanAutomate => State.CanAutomate;
    public bool ShowFooter => State.ShowFooter;

    public LicenseService()
    {
        var dir = AppPaths.ConfigDir;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "license.json");
        _shadowPath = Path.Combine(dir, "trial.state");
    }

    public void Load()
    {
        lock (_gate)
        {
            _stored = ReadStored();
            // Go-live hardening: deleting/editing license.json used to refund the 3-export trial. A
            // small shadow record of trial consumption survives that tampering and is adopted on load
            // as the authoritative record, so a deleted/tampered file can't mint fresh exports (and a
            // merely deleted file doesn't steal exports the user legitimately still has).
            ReconcileWithShadow();
            // No automatic trial: a fresh install (or a legacy file from the old auto-trial) resolves
            // to Free until the user explicitly starts the 3-export trial or activates Pro.
            Recompute();
        }
    }

    // Activate a pasted key. Offline signature check first; optional Lemon Squeezy online fallback.
    public async Task<(bool ok, string message)> ActivateAsync(string? key)
    {
        key = (key ?? string.Empty).Trim();
        if (key.Length == 0) return (false, "Enter a license key.");

        var p = LicenseValidator.Verify(key);
        if (IsValidPro(p))
        {
            lock (_gate)
            {
                _stored.Key = key;
                _stored.Email = p!.Email;
                WriteStored();
                Recompute();
            }
            return (true, "Activated — thank you! MarkSmith Pro is unlocked.");
        }

        if (LemonSqueezyClient.Enabled)
        {
            var r = await LemonSqueezyClient.ActivateAsync(key);
            if (r.ok)
            {
                lock (_gate)
                {
                    _stored.Key = key; _stored.Email = r.email; _stored.InstanceId = r.instanceId;
                    WriteStored(); Recompute();
                }
                return (true, "Activated — thank you! MarkSmith Pro is unlocked.");
            }
            return (false, r.message);
        }

        return (false, "That license key isn't valid.");
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _stored.Key = null; _stored.Email = null; _stored.InstanceId = null;
            WriteStored();
            Recompute();
        }
    }

    // Start the trial: FULL Pro for everything, exactly 3 DOCX exports, then Free. Only available
    // to Free users who haven't spent their trial.
    public (bool ok, string message) StartTrial()
    {
        lock (_gate)
        {
            if (State.Edition == Edition.Pro)
                return (false, "You already have Pro — no trial needed.");
            if (_stored.TrialExportsRemaining > 0)
                return (false, $"Your trial is already active — {_stored.TrialExportsRemaining} DOCX export(s) remaining. Spend them, then it's gone.");
            if (_stored.TrialUsed)
                return (false, "Your trial has already been spent — all 3 DOCX exports are used.");

            _stored.TrialExportsRemaining = 3;
            WriteStored();
            WriteShadow(); // the trial's existence is shadowed too — deleting license.json right after starting must not mint a second one
            Recompute();
            return (true, "Trial started — full Pro for 3 DOCX exports. Spend them wisely.");
        }
    }

    // Consume one of the trial's DOCX exports after a successful export. Once the 3rd is used the
    // user drops back to Free and the paywall returns. Locked: concurrent exports (batch + API +
    // watch folder) must not lose a decrement or interleave the primary/shadow writes.
    public void ConsumeDocxExport()
    {
        lock (_gate)
        {
            if (_stored.TrialExportsRemaining <= 0) return;
            _stored.TrialExportsRemaining--;
            _stored.TrialExportUsedUtc = DateTimeOffset.UtcNow;
            if (_stored.TrialExportsRemaining == 0) _stored.TrialUsed = true;
            WriteStored();
            WriteShadow();
            Recompute();
        }
    }

    // Testing/verification affordance: force the app back to Free (clears any key AND any trial),
    // so the free-tier limits can be exercised end-to-end on demand.
    // Hidden developer command (Ctrl+Shift+Alt+P): flip straight into Pro and back. ON writes a
    // dev license key into the real license file (survives restarts); OFF DELETES the license file
    // entirely, returning to Free. A real activated Pro key is never touched.
    public (bool pro, string message) ToggleDevPro()
    {
#if !DEBUG
        // The dev-key backdoor is compiled out of Release builds (see LicenseValidator), so this
        // command is a harmless no-op in anything we ship.
        return (State.Edition == Edition.Pro, "Pro dev mode isn't available in release builds.");
#else
        lock (_gate)
        {
            if (State.Edition == Edition.Pro)
            {
                if (!string.Equals(_stored.Key, LicenseValidator.DevProKey, StringComparison.Ordinal))
                    return (true, "Already Pro with a real key — not touching it. Reset to Free with Ctrl+Shift+Alt+L if needed.");
                _stored.Key = null; _stored.Email = null; _stored.InstanceId = null;
                _stored.TrialExportsRemaining = 0; _stored.TrialUsed = false; _stored.TrialExportUsedUtc = null;
                try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort */ }
                DeleteShadow();
                Recompute();
                return (false, "Pro dev mode OFF — license file deleted, back to Free.");
            }

            _stored.Key = LicenseValidator.DevProKey;
            _stored.Email = "dev@marksmith.local";
            _stored.InstanceId = null;
            _stored.TrialExportsRemaining = 0; _stored.TrialUsed = false; _stored.TrialExportUsedUtc = null;
            WriteStored();
            Recompute();
            return (true, "Pro dev mode ON — dev license key written to the license file.");
        }
#endif
    }

    public void ResetToFree()
    {
        lock (_gate)
        {
            _stored.Key = null; _stored.Email = null; _stored.InstanceId = null;
            _stored.TrialExportsRemaining = 0;
            _stored.TrialUsed = false;
            _stored.TrialExportUsedUtc = null;
            WriteStored();
            DeleteShadow(); // the dev affordance may genuinely refund the trial; shipped paths can't
            Recompute();
        }
    }

    private void Recompute()
    {
        // 1) valid signed Pro key (perpetual unless it carries an expiry)
        var p = LicenseValidator.Verify(_stored.Key);
        if (IsValidPro(p))
        {
            var exp = p!.Exp is long e ? DateTimeOffset.FromUnixTimeSeconds(e) : (DateTimeOffset?)null;
            State = new LicenseState
            {
                Edition = Edition.Pro,
                Key = _stored.Key,
                Email = p.Email,
                ExpiresUtc = exp,
                Status = exp is null
                    ? "MarkSmith Pro — activated"
                    : $"MarkSmith Pro — active until {exp:d MMM yyyy}",
            };
            Changed?.Invoke();
            return;
        }

        // 1b) previously activated online via Lemon Squeezy (an LS key is not a signed token, so it
        //     won't pass the offline check above — trust the stored activation instance instead).
        if (LemonSqueezyClient.Enabled
            && !string.IsNullOrWhiteSpace(_stored.Key)
            && !string.IsNullOrWhiteSpace(_stored.InstanceId))
        {
            State = new LicenseState
            {
                Edition = Edition.Pro,
                Key = _stored.Key,
                Email = _stored.Email,
                Status = "MarkSmith Pro — activated",
            };
            Changed?.Invoke();
            return;
        }

        // 2) the user-started 3-export trial — full Pro, so the state carries the export cap too
        if (_stored.TrialExportsRemaining > 0)
        {
            State = new LicenseState
            {
                Edition = Edition.Trial,
                TrialExportsRemaining = _stored.TrialExportsRemaining,
                Status = _stored.TrialExportsRemaining == 1
                    ? "Trial — 1 DOCX export remaining"
                    : $"Trial — {_stored.TrialExportsRemaining} DOCX exports remaining",
            };
            Changed?.Invoke();
            return;
        }

        // 3) free (the status distinguishes a never-started trial from a USED one, so the
        //    trial's one-shot consumption is visible and verifiable)
        State = new LicenseState
        {
            Edition = Edition.Free,
            Status = _stored.TrialUsed
                ? "Free — trial used (DOCX export requires Pro)"
                : "Free",
        };
        Changed?.Invoke();
    }

    private static bool IsValidPro(LicenseValidator.Payload? p)
    {
        if (p is null || !string.Equals(p.Edition, "pro", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.Exp is long e && DateTimeOffset.FromUnixTimeSeconds(e) <= DateTimeOffset.UtcNow) return false;
        return true;
    }

    private StoredLicense ReadStored()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<StoredLicense>(File.ReadAllText(_path)) ?? new();
        }
        catch { /* corrupt file → treat as fresh */ }
        return new();
    }

    private void WriteStored()
    {
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_stored, JsonOpts)); }
        catch { /* best-effort persistence */ }
    }

    // ---- Trial-consumption shadow (go-live hardening) ----
    // license.json is user-writable, so on its own it can't prove the trial was spent: deleting it
    // used to reset TrialUsed and mint a fresh 3 exports. Every consumption also lands in this tiny
    // second file; Load() reconciles the primary DOWNWARD against it. Both files live in the same
    // user profile (a determined user wiping the whole folder resets everything — accepted for a
    // local-first app), but the casual delete/edit bypass is closed.

    private sealed record ShadowTrial(int Consumed, bool Used);

    private void ReconcileWithShadow()
    {
        ShadowTrial? shadow;
        try
        {
            shadow = File.Exists(_shadowPath)
                ? JsonSerializer.Deserialize<ShadowTrial>(File.ReadAllText(_shadowPath))
                : null;
        }
        catch { shadow = null; /* corrupt shadow → treat as absent */ }
        if (shadow is null) return;

        // The shadow is the authoritative consumption record: it is only ever written by
        // StartTrial/ConsumeDocxExport and deleted by the dev reset affordances, so its presence
        // PROVES a trial happened. Adopt it wholesale — that both keeps a tampered primary file
        // from claiming exports it never earned AND restores an active trial whose file was
        // deleted (deletion is a refund attempt only when it would grant MORE than the shadow).
        _stored.TrialExportsRemaining = Math.Max(0, 3 - Math.Max(0, shadow.Consumed));
        if (shadow.Used) _stored.TrialUsed = true;
    }

    private void WriteShadow()
    {
        try
        {
            var shadow = new ShadowTrial(3 - Math.Max(0, _stored.TrialExportsRemaining), _stored.TrialUsed);
            AtomicFile.WriteAllText(_shadowPath, JsonSerializer.Serialize(shadow));
        }
        catch { /* best-effort — the primary file still records the spend */ }
    }

    private void DeleteShadow()
    {
        try { if (File.Exists(_shadowPath)) File.Delete(_shadowPath); } catch { /* best-effort */ }
    }
}
