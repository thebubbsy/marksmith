using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Legal;

public record LegalClause(string ClauseType, string ClauseTitle, string Body, int LineNumber);
public record SignatoryBlock(string Name, string Title, string Date, int LineNumber);

public class ContractValidationResult
{
    public List<LegalClause> DetectedClauses { get; } = new();
    public List<SignatoryBlock> DetectedSignatories { get; } = new();
    public List<string> MissingMandatoryClauses { get; } = new();
    public bool IsContractExecutionReady => MissingMandatoryClauses.Count == 0 && DetectedSignatories.Count >= 2;
}

/// <summary>
/// Service that scans legal Markdown contracts, validates boilerplate clauses, and audits execution signature blocks.
/// </summary>
public static class ContractClauseValidatorService
{
    private static readonly string[] StandardMandatoryClauses = new[]
    {
        "confidentiality", "indemnity", "governing-law", "termination"
    };

    private static readonly Regex ClauseFenceRegex = new(
        @":::clause:([a-zA-Z0-9_\-]+)(?:\s+title=""([^""]+)"")?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex SignatoryRegex = new(
        @"\[Signatory:\s*([^,\]]+),\s*Title:\s*([^,\]]+),\s*Date:\s*([^\]]+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Validates a Markdown legal contract against standard mandatory clauses and signature requirements.
    /// </summary>
    public static ContractValidationResult ValidateContract(string markdown, IEnumerable<string>? customMandatory = null)
    {
        var result = new ContractValidationResult();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            result.MissingMandatoryClauses.AddRange(customMandatory ?? StandardMandatoryClauses);
            return result;
        }

        var mandatorySet = new HashSet<string>(customMandatory ?? StandardMandatoryClauses, StringComparer.OrdinalIgnoreCase);

        // 1. Detect Clauses
        foreach (Match m in ClauseFenceRegex.Matches(markdown))
        {
            string type = m.Groups[1].Value.ToLowerInvariant();
            string title = m.Groups[2].Success ? m.Groups[2].Value : type;
            string body = m.Groups[3].Value.Trim();

            int lineNum = 1 + (m.Index > 0 ? markdown.Substring(0, m.Index).Split('\n').Length - 1 : 0);
            result.DetectedClauses.Add(new LegalClause(type, title, body, lineNum));
            mandatorySet.Remove(type);
        }

        result.MissingMandatoryClauses.AddRange(mandatorySet);

        // 2. Detect Signatories
        foreach (Match m in SignatoryRegex.Matches(markdown))
        {
            string name = m.Groups[1].Value.Trim();
            string title = m.Groups[2].Value.Trim();
            string date = m.Groups[3].Value.Trim();

            int lineNum = 1 + (m.Index > 0 ? markdown.Substring(0, m.Index).Split('\n').Length - 1 : 0);
            result.DetectedSignatories.Add(new SignatoryBlock(name, title, date, lineNum));
        }

        return result;
    }
}
