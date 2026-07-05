namespace MdToPdf.Models;

// One AI-usage governance event reported by a browser extension in org mode. Deliberately
// records METADATA + policy flags, not full conversation text — the governance product answers
// "who is using which AI tool and is sensitive data leaking", not "read my employees' chats".
// Full-text capture would make this covert surveillance; storing counts + redaction-safe flags
// keeps it a defensible DLP/compliance tool. See docs/GOVERNANCE.md.
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

    // DLP: which sensitive-data categories the scan matched, and how many hits. Never the matched
    // values themselves — those are redacted before the event leaves the browser.
    public List<string> DlpFlags { get; set; } = new();
    public int DlpHitCount { get; set; }

    public bool ConsentAcknowledged { get; set; } // extension asserts the user saw the notice

    public string RiskLevel => DlpHitCount switch
    {
        0 => "None",
        <= 2 => "Low",
        <= 5 => "Medium",
        _ => "High",
    };
}
