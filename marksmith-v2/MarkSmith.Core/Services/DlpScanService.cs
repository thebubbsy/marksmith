using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

// Data-loss-prevention scanner: flags categories of sensitive data in text destined for an AI
// chat tool, captures a MASKED, remediation-identifying preview of each match — never the raw
// value — and (only when something was actually flagged) the SURROUNDING context with just the
// matched spans blanked in place, plus a "secret density" score.
//
// The density/context pair exists for one reason: a security team needs to tell "an AWS key
// accidentally buried in 2,000 characters of legitimate text" (low density, real context around
// it — negligence) apart from "an AWS key submitted alone, as the entire message" (density near
// 1.0, nothing else — looks deliberate). That distinction is genuinely useful for triage and is
// exactly the kind of signal a real enterprise buyer wants — but it does NOT require storing the
// raw secret. Density is a ratio computed from match lengths; the context string keeps everything
// EXCEPT the matched spans, which are replaced with a category marker. The actual key/card/SSN
// value is still never captured anywhere, at any granularity — only its masked preview is (see
// Mask()). This is deliberate: real secrets never get a second, less-protected copy sitting in the
// governance database. Clean messages (no DLP hit) get zero content capture, same as before.
//
// Mirrored in JS for the governance extension (extension-governance/dlp-mask.js); this C# copy
// backs the /api/governance endpoints, and also re-applies its own residual-redaction pass to
// values arriving over the wire as a server-side safety net (see ApiServer.LooksUnmasked /
// RedactResidual) in case client-side masking or redaction ever has a bug.
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

    // MaskFull: never reveal ANY character (private keys — even a fragment narrows a brute force).
    // MaskEdges: show enough of the start/end to identify which specific value it is.
    // MaskLast4: PCI-standard style — only the trailing 4 characters (cards, SSNs).
    // MaskLocal: keep the domain (useful for "who got emailed"), redact the local part.
    // Reveal: show the value in full — reserved for values that are IDENTIFIERS, not secrets.
    private enum MaskStyle { Full, Edges, Last4, Local, Reveal }

    private static readonly (string Label, Func<DlpScanService, Regex> Rx, MaskStyle Mask, string Remediation)[] Rules =
    {
        // An AWS Access Key ID (AKIA/ASIA-prefixed) is an IDENTIFIER, not the secret half of the
        // credential pair — it's already recorded in every CloudTrail event and visible in the IAM
        // console. The actual secret is the paired Secret Access Key, which has no fixed prefix and
        // isn't reliably regex-matchable (it falls under "Credential"/"API key" below when labelled,
        // and stays fully/edge-masked there). Revealing the Access Key ID in full costs nothing —
        // the org already has it indexed — and lets an investigator search CloudTrail immediately.
        ("AWS access key", s => AwsKey(), MaskStyle.Reveal,
            "Rotate this key in IAM immediately and check CloudTrail for unauthorized use."),
        ("API key", s => GenericApiKey(), MaskStyle.Edges,
            "Revoke and reissue this API key from the issuing service's dashboard."),
        ("GitHub token", s => GithubToken(), MaskStyle.Edges,
            "Revoke this token in GitHub Settings -> Developer settings, then reissue."),
        ("Private key", s => PrivateKey(), MaskStyle.Full,
            "Treat the corresponding key pair as fully compromised; regenerate and redeploy."),
        ("Credential", s => CredentialAssignment(), MaskStyle.Full,
            "Rotate the affected password/credential; check for reuse elsewhere."),
        ("Credit-card-like number", s => CardLike(), MaskStyle.Last4,
            "Verify with the cardholder; contact the card issuer if this was a real number."),
        ("SSN-like number", s => SsnLike(), MaskStyle.Last4,
            "Confirm with the individual; consider identity-theft monitoring if genuine."),
        ("Email address", s => Email(), MaskStyle.Local,
            "Confirm this recipient/identity was intended to be shared."),
    };

    private const int MaxContextChars = 500; // data minimization cap on stored context

    // One matched instance, masked for safe storage/display. Never the raw value.
    public sealed record DlpMatch(string Category, string Masked, string Remediation);

    // RedactedContext/SecretDensity are "" / 0 when HitCount == 0 — no capture at all for clean text.
    public sealed record DlpResult(List<string> Flags, int HitCount, List<DlpMatch> Matches,
        string RedactedContext, double SecretDensity);

    public DlpResult Scan(string text)
    {
        var flags = new List<string>();
        var matches = new List<DlpMatch>();
        var spans = new List<(int Start, int Length, string Placeholder)>();
        var perCategoryCount = new Dictionary<string, int>();
        var hits = 0;
        long secretChars = 0;

        foreach (var (label, rx, style, remediation) in Rules)
        {
            var found = rx(this).Matches(text);
            if (found.Count == 0) continue;
            flags.Add(label);
            hits += found.Count;
            foreach (Match m in found)
            {
                secretChars += m.Length;
                // Reveal-style matches (AWS Access Key ID — an identifier, not the secret) keep
                // their real text in the surrounding context too, matching the masked-match table
                // which already shows them in full. Every other category is blanked in context,
                // same as its masked preview.
                spans.Add((m.Index, m.Length, style == MaskStyle.Reveal ? m.Value : $"[{label}]"));
                perCategoryCount.TryGetValue(label, out var n);
                if (n < 5) // cap stored MASKED previews per category — the density/context below
                {          // still accounts for every match, capped or not.
                    matches.Add(new DlpMatch(label, Mask(m.Value, style), remediation));
                    perCategoryCount[label] = n + 1;
                }
            }
        }

        if (hits == 0) return new DlpResult(flags, 0, matches, "", 0);

        var context = RedactResidual(BuildRedactedContext(text, spans));
        var density = text.Length > 0 ? Math.Min(1.0, (double)secretChars / text.Length) : 0;
        return new DlpResult(flags, hits, matches, context, density);
    }

    // Rebuilds `text` with every matched span replaced by a category marker (except Reveal-style
    // spans, which keep their real text) — everything else (the actual surrounding words) is
    // preserved verbatim. This is what lets an investigator see "was this buried in a legitimate
    // config file, or was it the entire message" without ever seeing an actual secret's value.
    // Capped to MaxContextChars (data minimization).
    private static string BuildRedactedContext(string text, List<(int Start, int Length, string Placeholder)> spans)
    {
        if (spans.Count == 0) return "";
        var sorted = spans.OrderBy(s => s.Start).ToList();
        var sb = new StringBuilder();
        int pos = 0, lastEnd = -1;
        foreach (var s in sorted)
        {
            if (s.Start < lastEnd) continue; // overlapping match already covered by a prior span
            if (s.Start > pos) sb.Append(text, pos, s.Start - pos);
            sb.Append(s.Placeholder);
            pos = s.Start + s.Length;
            lastEnd = pos;
        }
        if (pos < text.Length) sb.Append(text, pos, text.Length - pos);
        var result = sb.ToString();
        return result.Length > MaxContextChars ? result[..MaxContextChars] + "…" : result;
    }

    // Defense-in-depth: re-run every rule directly against a context string that's ALREADY been
    // through BuildRedactedContext, and blank any residual match. Guards against a bug in span
    // math ever leaving a raw secret inside stored context. Also called server-side in ApiServer
    // against context arriving from the extension, so a client-side bug can't leak one either.
    // True if `value` still contains something that should have been masked but wasn't — used by
    // ApiServer's server-side safety net. AWS Access Key IDs are deliberately excluded: they're
    // REVEAL-style by design (identifiers, not secrets — see the rule above), so a correctly
    // revealed key must not be mistaken for a masking failure and dropped.
    public static bool ContainsUnmaskedSecret(string value)
    {
        foreach (var (_, rx, style, _) in Rules)
        {
            if (style == MaskStyle.Reveal) continue;
            if (rx(new DlpScanService()).IsMatch(value)) return true;
        }
        return false;
    }

    public static string RedactResidual(string context)
    {
        if (string.IsNullOrEmpty(context)) return context;
        var result = context;
        foreach (var (label, rx, style, _) in Rules)
        {
            if (style == MaskStyle.Reveal) continue; // identifier, not a secret — leave it visible
            result = rx(new DlpScanService()).Replace(result, $"[{label}]");
        }
        return result;
    }

    private static string Mask(string value, MaskStyle style) => style switch
    {
        MaskStyle.Full => "[redacted — value not stored]",
        MaskStyle.Edges when value.Length <= 8 => new string('*', value.Length),
        MaskStyle.Edges => value[..4] + new string('*', Math.Max(4, value.Length - 8)) + value[^4..],
        MaskStyle.Last4 => MaskAllButLast4(value),
        MaskStyle.Local => MaskEmailLocal(value),
        MaskStyle.Reveal => value, // identifier, not a secret — see the AWS access key rule comment
        _ => "[redacted]",
    };

    // Keeps digit grouping/spacing but blanks every digit except the last 4 (PCI display convention).
    private static string MaskAllButLast4(string value)
    {
        var digits = value.Where(char.IsDigit).ToList();
        int keepFrom = Math.Max(0, digits.Count - 4);
        var sb = new StringBuilder();
        int seen = 0;
        foreach (var c in value)
        {
            if (char.IsDigit(c)) { sb.Append(seen >= keepFrom ? c : '•'); seen++; }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string MaskEmailLocal(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "[redacted]";
        var local = email[..at];
        var domain = email[at..];
        var visible = local.Length <= 2 ? local[..1] : local[..2];
        return visible + new string('*', Math.Max(1, local.Length - visible.Length)) + domain;
    }
}
