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
    //
    // THIS IS THE ONE VALUE THAT HAS TO BE FILLED IN BEFORE ANYONE CAN PAY. While it still says
    // YOUR-STORE the Buy buttons are inert by design (IsStoreConfigured below).
    public const string DefaultStoreUrl = "https://YOUR-STORE.lemonsqueezy.com/buy/YOUR-PRODUCT-ID";

    // MARKSMITH_STORE_URL overrides the baked-in link at runtime. That exists for the test-mode
    // rehearsal in the go-live checklist: Lemon Squeezy's test checkout is a different URL, and
    // rebuilding and reshipping the app just to point at it (and again to point back) is how a
    // test link ends up in a production build.
    public static string StoreUrl =>
        Environment.GetEnvironmentVariable("MARKSMITH_STORE_URL") is { Length: > 0 } u ? u.Trim() : DefaultStoreUrl;

    // Go-live guard: until StoreUrl carries a real checkout link the UI's "Buy" buttons show a
    // "store not configured" status instead of launching the placeholder URL.
    public static bool IsStoreConfigured =>
        !StoreUrl.Contains("YOUR-STORE", StringComparison.OrdinalIgnoreCase)
        && !StoreUrl.Contains("YOUR-PRODUCT-ID", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The checkout link to actually open, with the buyer's email prefilled when we know it.
    /// Lemon Squeezy emails the license key to whatever address goes through checkout, so a typo
    /// there sends someone else's key into the void and lands us a support ticket we cannot
    /// resolve without a refund.
    /// </summary>
    public static string CheckoutUrl(string? email = null)
    {
        var url = StoreUrl;
        if (string.IsNullOrWhiteSpace(email)) return url;
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}checkout[email]={Uri.EscapeDataString(email.Trim())}";
    }

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
    public bool CanStartTrial => State.CanStartTrial;

    // The trust root this instance verifies keys against. Production passes nothing and gets the
    // key embedded in LicenseValidator; the test suite passes a throwaway public key so it can
    // exercise the real activation path with keys it signed itself. Without this seam the
    // activation tests had to sign with a random keypair and verify against the production public
    // key — which can never succeed, so they failed permanently and licensing went untested.
    private readonly string? _publicKeyPem;

    public LicenseService(string? publicKeyPem = null)
    {
        _publicKeyPem = publicKeyPem;
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

        var p = LicenseValidator.Verify(key, _publicKeyPem);
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
            var r = await LemonSqueezyClient.ActivateDetailedAsync(key);
            if (r.Ok)
            {
                lock (_gate)
                {
                    _stored.Key = key; _stored.Email = r.Email; _stored.InstanceId = r.InstanceId;
                    _stored.LicenseStatus = r.Status;
                    _stored.LicenseExpiresUtc = r.ExpiresUtc;
                    _stored.LastValidatedUtc = DateTimeOffset.UtcNow;
                    WriteStored(); Recompute();
                }
                return (true, "Activated — thank you! MarkSmith Pro is unlocked.");
            }

            // "You have already activated this machine" is not a failure from the customer's point
            // of view — they own the key and they are sitting at a machine it covers. Reinstalling
            // (or clearing the config folder) loses our instance id while Lemon Squeezy still holds
            // the seat, so a plain activate comes back at the limit. Ask LS whether the key itself
            // is good and, if it is, honour it rather than making a paying customer email support.
            if (r.Reachable)
            {
                var v = await LemonSqueezyClient.ValidateAsync(key);
                if (v.Ok)
                {
                    lock (_gate)
                    {
                        _stored.Key = key; _stored.Email = v.Email ?? r.Email; _stored.InstanceId = null;
                        _stored.LicenseStatus = v.Status;
                        _stored.LicenseExpiresUtc = v.ExpiresUtc;
                        _stored.LastValidatedUtc = DateTimeOffset.UtcNow;
                        WriteStored(); Recompute();
                    }
                    return (true, "Activated — thank you! MarkSmith Pro is unlocked.");
                }
            }

            return (false, r.Reachable
                ? r.Message
                : "Couldn't reach the licensing server to check that key. Check your connection and try again.");
        }

        return (false, "That license key isn't valid.");
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            ClearKeyState();
            WriteStored();
            Recompute();
        }
    }

    /// <summary>
    /// Remove the license from this device AND release its Lemon Squeezy activation seat.
    /// </summary>
    /// <remarks>
    /// The sync <see cref="Deactivate"/> only forgets the key locally. On the online path that
    /// leaks a seat every time: with an activation limit of 3, a customer who reinstalls three
    /// times is locked out of the product they bought and has to email us to get it back. Anything
    /// user-facing should call this instead.
    ///
    /// If Lemon Squeezy cannot be reached we still remove the license locally — the user asked to
    /// deactivate, and refusing to do so because a server is down is worse than a stranded seat —
    /// but we say so, because the seat is still held at their end.
    /// </remarks>
    public async Task<(bool ok, string message)> DeactivateAsync()
    {
        string? key, instanceId;
        lock (_gate) { key = _stored.Key; instanceId = _stored.InstanceId; }

        var released = false;
        string? problem = null;

        if (LemonSqueezyClient.Enabled
            && !string.IsNullOrWhiteSpace(key)
            && !string.IsNullOrWhiteSpace(instanceId))
        {
            var r = await LemonSqueezyClient.DeactivateDetailedAsync(key!, instanceId!);
            released = r.Ok;
            if (!r.Ok) problem = r.Message;
        }

        lock (_gate)
        {
            ClearKeyState();
            WriteStored();
            Recompute();
        }

        if (problem is null)
            return (true, "License removed from this device.");

        return (true, released
            ? "License removed from this device."
            : $"License removed from this device, but the activation may still be held: {problem}");
    }

    /// <summary>
    /// Re-check an online license against Lemon Squeezy. Safe to call on startup and periodically.
    /// </summary>
    /// <remarks>
    /// Activation is a one-off event; entitlement is not. Between the two sit refunds, chargebacks
    /// and disabled orders, none of which the app can notice on its own — without this, a refunded
    /// customer keeps Pro permanently.
    ///
    /// The two failure modes pull in opposite directions and both have to be handled, or the
    /// feature does more harm than good:
    ///   * Check too eagerly and a customer with no internet loses the product they paid for.
    ///   * Never re-check and a refund never takes effect.
    /// So: re-check at most every <see cref="RevalidateEveryDays"/> days, and only downgrade on an
    /// answer we actually received. An unreachable server is not a "no" — it buys the customer the
    /// full <see cref="OfflineGraceDays"/> window before Pro lapses, which is the difference
    /// between a plane trip and a permanent lockout.
    /// </remarks>
    public const int RevalidateEveryDays = 3;
    public const int OfflineGraceDays = 30;

    public async Task<bool> RevalidateAsync(bool force = false)
    {
        string? key, instanceId;
        DateTimeOffset? last;
        lock (_gate)
        {
            if (!LemonSqueezyClient.Enabled) return State.IsPro;
            key = _stored.Key;
            instanceId = _stored.InstanceId;
            last = _stored.LastValidatedUtc;
            // Nothing activated online, or an offline signed key (which needs no server at all).
            if (string.IsNullOrWhiteSpace(key) || last is null) return State.IsPro;
        }

        if (!force && last is { } l && DateTimeOffset.UtcNow - l < TimeSpan.FromDays(RevalidateEveryDays))
            return State.IsPro;

        var r = await LemonSqueezyClient.ValidateAsync(key!, instanceId);

        if (!r.Reachable)
        {
            // Could not ask, so nothing is written: LastValidatedUtc deliberately stays where it
            // was. Recompute enforces the grace window off that timestamp, so Pro keeps working
            // now and lapses on its own if the check never succeeds again — and one successful
            // check at any point restores it.
            return State.IsPro;
        }

        lock (_gate)
        {
            _stored.LicenseStatus = r.Status ?? (r.Ok ? "active" : "disabled");
            _stored.LicenseExpiresUtc = r.ExpiresUtc;
            if (r.Ok)
            {
                _stored.LastValidatedUtc = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(r.Email)) _stored.Email = r.Email;
            }
            WriteStored();
            Recompute();
        }
        return State.IsPro;
    }

    // Clears every field that makes up "this device holds a license", leaving trial state alone.
    private void ClearKeyState()
    {
        _stored.Key = null;
        _stored.Email = null;
        _stored.InstanceId = null;
        _stored.LicenseStatus = null;
        _stored.LicenseExpiresUtc = null;
        _stored.LastValidatedUtc = null;
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
#if !DEV_TOOLS
        // The dev-key backdoor is compiled out of SHIPPED builds (see LicenseValidator), so this
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
        var p = LicenseValidator.Verify(_stored.Key, _publicKeyPem);
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

        // 1b) previously activated online via Lemon Squeezy. An LS key is an opaque UUID, not a
        //     signed token, so it can never pass the offline check above — what we trust instead is
        //     the record of a successful activation. That record is only good while Lemon Squeezy
        //     still says it is: a key that has expired or been disabled (refund, chargeback) stops
        //     unlocking Pro here, and RevalidateAsync is what keeps the record honest.
        // (An InstanceId with no LastValidatedUtc is an activation written by a build that predates
        // these fields — still honoured, so an update never silently un-Pros someone.)
        if (LemonSqueezyClient.Enabled
            && !string.IsNullOrWhiteSpace(_stored.Key)
            && (_stored.LastValidatedUtc is not null || !string.IsNullOrWhiteSpace(_stored.InstanceId)))
        {
            var expired = _stored.LicenseExpiresUtc is { } exp2 && exp2 <= DateTimeOffset.UtcNow;
            var dead = _stored.LicenseStatus is { } st
                       && (st.Equals("expired", StringComparison.OrdinalIgnoreCase)
                           || st.Equals("disabled", StringComparison.OrdinalIgnoreCase));

            // The offline grace window, enforced in one place so it actually ends. RevalidateAsync
            // refreshes LastValidatedUtc on every successful check; if checks have been failing for
            // longer than the window (machine permanently offline, or someone blocking the API to
            // dodge a revocation) the activation stops counting. Legacy records with no timestamp
            // are exempt — they were written before the field existed and have nothing to be stale
            // against.
            var stale = _stored.LastValidatedUtc is { } lv
                        && DateTimeOffset.UtcNow - lv > TimeSpan.FromDays(OfflineGraceDays);

            if (!expired && !dead && !stale)
            {
                State = new LicenseState
                {
                    Edition = Edition.Pro,
                    Key = _stored.Key,
                    Email = _stored.Email,
                    ExpiresUtc = _stored.LicenseExpiresUtc,
                    Status = _stored.LicenseExpiresUtc is { } e2
                        ? $"MarkSmith Pro — active until {e2:d MMM yyyy}"
                        : "MarkSmith Pro — activated",
                };
                Changed?.Invoke();
                return;
            }
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
            TrialUsed = _stored.TrialUsed,
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
