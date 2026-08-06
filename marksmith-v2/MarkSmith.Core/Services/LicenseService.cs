using System.Text.Json;
using MarkSmith.Models;

namespace MarkSmith.Services;

// Resolves and persists the app's licensing state. Resolution order: a valid signed Pro key wins;
// otherwise a user-started trial (exactly ONE DOCX export, consumed by a successful export);
// otherwise Free. There is NO automatic trial — the user starts it explicitly from Settings or the
// upgrade banner. State lives in %LOCALAPPDATA%\MarkSmith\license.json.
public sealed class LicenseService
{
    // Your Lemon Squeezy checkout link (the "Buy" button opens this). Replace with your product's
    // buy URL from Lemon Squeezy → Product → Share. See packaging/lemonsqueezy-setup.md.
    public const string StoreUrl = "https://YOUR-STORE.lemonsqueezy.com/buy/YOUR-PRODUCT-ID";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private StoredLicense _stored = new();

    public LicenseState State { get; private set; } = new();
    public event Action? Changed;

    public bool IsPro => State.IsPro;
    public bool CanExportDocx => State.CanExportDocx;
    public bool CanExportPptx => State.CanExportPptx;
    public bool CanAutomate => State.CanAutomate;
    public bool ShowFooter => State.ShowFooter;

    public LicenseService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "license.json");
    }

    public void Load()
    {
        _stored = ReadStored();
        // No automatic trial: a fresh install (or a legacy file from the old auto-trial) resolves
        // to Free until the user explicitly starts the one-export trial or activates Pro.
        Recompute();
    }

    // Activate a pasted key. Offline signature check first; optional Lemon Squeezy online fallback.
    public async Task<(bool ok, string message)> ActivateAsync(string? key)
    {
        key = (key ?? string.Empty).Trim();
        if (key.Length == 0) return (false, "Enter a license key.");

        var p = LicenseValidator.Verify(key);
        if (IsValidPro(p))
        {
            _stored.Key = key;
            _stored.Email = p!.Email;
            WriteStored();
            Recompute();
            return (true, "Activated — thank you! MarkSmith Pro is unlocked.");
        }

        if (LemonSqueezyClient.Enabled)
        {
            var r = await LemonSqueezyClient.ActivateAsync(key);
            if (r.ok)
            {
                _stored.Key = key; _stored.Email = r.email; _stored.InstanceId = r.instanceId;
                WriteStored(); Recompute();
                return (true, "Activated — thank you! MarkSmith Pro is unlocked.");
            }
            return (false, r.message);
        }

        return (false, "That license key isn't valid.");
    }

    public void Deactivate()
    {
        _stored.Key = null; _stored.Email = null; _stored.InstanceId = null;
        WriteStored();
        Recompute();
    }

    // Start the one-export trial. Only available to Free users who haven't used it up.
    public (bool ok, string message) StartTrial()
    {
        if (State.Edition == Edition.Pro)
            return (false, "You already have Pro — no trial needed.");
        if (_stored.TrialExportsRemaining > 0)
            return (false, "Your trial is already active — one DOCX export remaining. Use it, then it's gone.");
        if (_stored.TrialUsed)
            return (false, "Your trial has already been used — one DOCX export was the whole trial.");

        _stored.TrialExportsRemaining = 1;
        WriteStored();
        Recompute();
        return (true, "Trial started — you have exactly ONE DOCX export. Spend it wisely.");
    }

    // Consume the trial's single DOCX export after a successful export. Once it hits 0 the user is
    // back on Free (this is what makes the trial REAL — one export, then the paywall returns).
    public void ConsumeDocxExport()
    {
        if (_stored.TrialExportsRemaining <= 0) return;
        _stored.TrialExportsRemaining--;
        _stored.TrialUsed = true;
        _stored.TrialExportUsedUtc = DateTimeOffset.UtcNow;
        WriteStored();
        Recompute();
    }

    // Testing/verification affordance: force the app back to Free (clears any key AND any trial),
    // so the free-tier limits can be exercised end-to-end on demand.
    public void ResetToFree()
    {
        _stored.Key = null; _stored.Email = null; _stored.InstanceId = null;
        _stored.TrialExportsRemaining = 0;
        _stored.TrialUsed = false;
        _stored.TrialExportUsedUtc = null;
        WriteStored();
        Recompute();
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

        // 2) the user-started one-export trial
        if (_stored.TrialExportsRemaining > 0)
        {
            State = new LicenseState
            {
                Edition = Edition.Trial,
                Status = _stored.TrialExportsRemaining == 1
                    ? "Trial — ONE DOCX export remaining"
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
}
