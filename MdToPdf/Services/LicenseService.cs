using System.Text.Json;
using MdToPdf.Models;

namespace MdToPdf.Services;

// Resolves and persists the app's licensing state. Resolution order: a valid signed Pro key wins;
// otherwise a time-limited Pro trial (first N days); otherwise Free. State lives in
// %LOCALAPPDATA%\MdToPdf\license.json.
public sealed class LicenseService
{
    public const int TrialDays = 14;

    // Your checkout / product page. Buttons open this; adjust to your Lemon Squeezy (or other) store.
    public const string StoreUrl = "https://github.com/thebubbsy/marksmith#pricing";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private StoredLicense _stored = new();

    public LicenseState State { get; private set; } = new();
    public event Action? Changed;

    public bool IsPro => State.IsPro;
    public bool CanExportDocx => State.CanExportDocx;
    public bool CanAutomate => State.CanAutomate;
    public bool ShowFooter => State.ShowFooter;

    public LicenseService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdToPdf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "license.json");
    }

    public void Load()
    {
        _stored = ReadStored();
        if (_stored.TrialStartUtc is null) { _stored.TrialStartUtc = DateTimeOffset.UtcNow; WriteStored(); }
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
            return (true, "Activated — thank you! Marksmith Pro is unlocked.");
        }

        if (LemonSqueezyClient.Enabled)
        {
            var r = await LemonSqueezyClient.ActivateAsync(key);
            if (r.ok)
            {
                _stored.Key = key; _stored.Email = r.email; _stored.InstanceId = r.instanceId;
                WriteStored(); Recompute();
                return (true, "Activated — thank you! Marksmith Pro is unlocked.");
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
                    ? "Marksmith Pro — activated"
                    : $"Marksmith Pro — active until {exp:d MMM yyyy}",
            };
            Changed?.Invoke();
            return;
        }

        // 2) trial window
        var trialEnd = (_stored.TrialStartUtc ?? DateTimeOffset.UtcNow).AddDays(TrialDays);
        if (DateTimeOffset.UtcNow < trialEnd)
        {
            var daysLeft = Math.Max(1, (int)Math.Ceiling((trialEnd - DateTimeOffset.UtcNow).TotalDays));
            State = new LicenseState
            {
                Edition = Edition.Trial,
                ExpiresUtc = trialEnd,
                Status = $"Pro trial — {daysLeft} day{(daysLeft == 1 ? "" : "s")} left",
            };
            Changed?.Invoke();
            return;
        }

        // 3) free
        State = new LicenseState { Edition = Edition.Free, Status = "Free" };
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
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_stored, JsonOpts)); }
        catch { /* best-effort persistence */ }
    }
}
