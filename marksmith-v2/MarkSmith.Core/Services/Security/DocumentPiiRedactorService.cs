using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Security;

public enum RedactionMode
{
    PseudonymTokens,
    BlackBarMask
}

public record RedactionOccurrence(string OriginalValue, string RedactedValue, string PiiCategory, int Index);

public class RedactionResult
{
    public string SanitizedMarkdown { get; set; } = string.Empty;
    public List<RedactionOccurrence> Redactions { get; } = new();
    public Dictionary<string, string> PseudonymLookup { get; } = new();
}

/// <summary>
/// Service that scans Markdown documents for sensitive PII (emails, phones, IPs, credit cards, API keys) and applies redactions.
/// </summary>
public static class DocumentPiiRedactorService
{
    private static readonly Regex EmailRegex = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(@"(?:\+?\d{1,3}[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex IpRegex = new(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b", RegexOptions.Compiled);
    private static readonly Regex CreditCardRegex = new(@"\b(?:\d{4}[-\s]?){3}\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex ApiKeyRegex = new(@"\b(?:AKIA[0-9A-Z]{16}|ghp_[a-zA-Z0-9]{36}|sk_[a-zA-Z0-9]{32,})\b", RegexOptions.Compiled);

    /// <summary>
    /// Scans Markdown and redacts sensitive PII patterns.
    /// </summary>
    public static RedactionResult Redact(string markdown, RedactionMode mode = RedactionMode.PseudonymTokens)
    {
        var result = new RedactionResult();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            result.SanitizedMarkdown = markdown ?? string.Empty;
            return result;
        }

        string text = markdown;
        int emailIdx = 1, phoneIdx = 1, ipIdx = 1, ccIdx = 1, keyIdx = 1;

        // 1. API Keys
        text = ApiKeyRegex.Replace(text, m =>
        {
            string orig = m.Value;
            string repl = mode == RedactionMode.BlackBarMask ? "██████████" : $"[SECRET_KEY_{keyIdx++}]";
            result.Redactions.Add(new RedactionOccurrence(orig, repl, "API Key", m.Index));
            result.PseudonymLookup[repl] = orig;
            return repl;
        });

        // 2. Credit Cards
        text = CreditCardRegex.Replace(text, m =>
        {
            string orig = m.Value;
            string repl = mode == RedactionMode.BlackBarMask ? "████-████-████-████" : $"[CARD_{ccIdx++}]";
            result.Redactions.Add(new RedactionOccurrence(orig, repl, "Credit Card", m.Index));
            result.PseudonymLookup[repl] = orig;
            return repl;
        });

        // 3. Emails
        text = EmailRegex.Replace(text, m =>
        {
            string orig = m.Value;
            string repl = mode == RedactionMode.BlackBarMask ? "████@████.███" : $"[EMAIL_{emailIdx++}]";
            result.Redactions.Add(new RedactionOccurrence(orig, repl, "Email", m.Index));
            result.PseudonymLookup[repl] = orig;
            return repl;
        });

        // 4. Phone numbers
        text = PhoneRegex.Replace(text, m =>
        {
            string orig = m.Value;
            string repl = mode == RedactionMode.BlackBarMask ? "███-███-████" : $"[PHONE_{phoneIdx++}]";
            result.Redactions.Add(new RedactionOccurrence(orig, repl, "Phone Number", m.Index));
            result.PseudonymLookup[repl] = orig;
            return repl;
        });

        // 5. IP Addresses
        text = IpRegex.Replace(text, m =>
        {
            string orig = m.Value;
            string repl = mode == RedactionMode.BlackBarMask ? "███.███.███.███" : $"[IP_{ipIdx++}]";
            result.Redactions.Add(new RedactionOccurrence(orig, repl, "IP Address", m.Index));
            result.PseudonymLookup[repl] = orig;
            return repl;
        });

        result.SanitizedMarkdown = text;
        return result;
    }
}
