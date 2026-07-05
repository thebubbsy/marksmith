using System.Text.RegularExpressions;

namespace MdToPdf.Services;

// Data-loss-prevention scanner: flags categories of sensitive data in text destined for an AI
// chat tool. Returns CATEGORY LABELS ONLY (e.g. "AWS key", "email"), never the matched strings —
// the governance product reports that a leak risk occurred, not the secret itself. Runs in the
// browser extension's context too (mirrored in JS); this C# copy backs the /api/governance
// endpoints and lets the desktop app scan pasted/ingested content locally.
public sealed partial class DlpScanService
{
    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")] private static partial Regex AwsKey();
    [GeneratedRegex(@"\b(sk|pk)-[A-Za-z0-9]{20,}\b")] private static partial Regex GenericApiKey();
    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b")] private static partial Regex GithubToken();
    [GeneratedRegex(@"-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----")] private static partial Regex PrivateKey();
    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b")] private static partial Regex Email();
    [GeneratedRegex(@"\b(?:\d[ \-]?){13,16}\b")] private static partial Regex CardLike();
    [GeneratedRegex(@"\b\d{3}[ \-]?\d{2}[ \-]?\d{4}\b")] private static partial Regex SsnLike();
    [GeneratedRegex(@"(?i)\b(password|passwd|secret|api[_ ]?key|bearer|authorization:)\b\s*[:=]?\s*\S+")] private static partial Regex CredentialAssignment();

    private static readonly (string Label, Func<DlpScanService, Regex> Rx)[] Rules =
    {
        ("AWS access key", s => AwsKey()),
        ("API key",        s => GenericApiKey()),
        ("GitHub token",   s => GithubToken()),
        ("Private key",    s => PrivateKey()),
        ("Credential",     s => CredentialAssignment()),
        ("Credit-card-like number", s => CardLike()),
        ("SSN-like number", s => SsnLike()),
        ("Email address",  s => Email()),
    };

    public sealed record DlpResult(List<string> Flags, int HitCount);

    public DlpResult Scan(string text)
    {
        var flags = new List<string>();
        var hits = 0;
        foreach (var (label, rx) in Rules)
        {
            var count = rx(this).Matches(text).Count;
            if (count > 0)
            {
                flags.Add(label);
                hits += count;
            }
        }
        return new DlpResult(flags, hits);
    }
}
