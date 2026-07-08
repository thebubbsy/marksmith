namespace MdToPdf.Models;

// One masked, remediation-identifying DLP finding attached to a UsageEvent. `Masked` is NEVER the
// raw matched value — see DlpScanService for the masking rules per category. Storing this (instead
// of only a category count) is what lets a security team act ("rotate key ...MNOP") instead of
// just knowing something-somewhere leaked.
public sealed class DlpFinding
{
    public string Category { get; set; } = "";
    public string Masked { get; set; } = "";
    public string Remediation { get; set; } = "";
}

// One AI-usage governance event reported by a browser extension in org mode. Deliberately
// records METADATA + policy flags + MASKED findings, not full conversation text — the governance
// product answers "who is using which AI tool, how much time, and is sensitive data leaking", not
// "read my employees' chats". Full-text capture would make this covert surveillance; storing
// counts + masked, remediation-identifying previews keeps it a defensible DLP/compliance tool that
// still gives management enough to act on. See docs/GOVERNANCE.md.
public sealed class UsageEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Identity is supplied by the managed-device policy / SSO, not guessed. May be a work email
    // or an opaque device id depending on how the org configures the extension.
    public string User { get; set; } = "";
    public string Device { get; set; } = "";

    public string Assistant { get; set; } = "";   // ChatGPT / Gemini / Claude
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";        // page/conversation title — topic, not content

    public int CharCount { get; set; }
    public int WordCount { get; set; }

    // Active time spent on this AI tool in the reporting window (a "heartbeat" event reports
    // usage time with no message; a "sent a message" event may carry both).
    public int TimeSpentSeconds { get; set; }

    // DLP: which sensitive-data categories the scan matched, and how many hits (unchanged, used
    // for fast aggregation), PLUS masked, remediation-identifying previews of each match — never
    // the raw matched value. Both are populated together; Flags/HitCount stay for back-compat.
    public List<string> DlpFlags { get; set; } = new();
    public int DlpHitCount { get; set; }
    public List<DlpFinding> DlpMatches { get; set; } = new();

    // Only populated when DlpHitCount > 0 — the surrounding message with matched spans blanked
    // (never empty AND raw: see DlpScanService.BuildRedactedContext), and what SHARE of the
    // message the matches made up. This is what separates "a key buried in 2,000 characters of
    // legitimate text" (low density, real context around it) from "a key submitted alone as the
    // entire message" (density near 1.0) — without ever storing the value itself.
    public string RedactedContext { get; set; } = "";
    public double SecretDensity { get; set; }

    public bool ConsentAcknowledged { get; set; } // extension asserts the user saw the notice

    public string RiskLevel => DlpHitCount switch
    {
        0 => "None",
        <= 2 => "Low",
        <= 5 => "Medium",
        _ => "High",
    };

    // Human-readable read on intent, purely from the structural density signal — never from the
    // secret's value. 0.6 errs toward flagging "mostly-secret" messages as worth a closer look.
    public string IntentLabel => DlpHitCount == 0 ? "" : SecretDensity >= 0.6 ? "Likely deliberate" : "Likely accidental";
}
