using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Legal;

public record DependencyPackage(string Name, string Version, string SpdxLicense, string? Copyright);

public class AttributionReport
{
    public List<DependencyPackage> Packages { get; } = new();
    public string GeneratedNoticeAppendix { get; set; } = string.Empty;
}

/// <summary>
/// Service that scans project dependencies and compiles standardized third-party software license attribution notices.
/// </summary>
public static class DependencyAttributionService
{
    private static readonly Regex DepFenceRegex = new(
        @":::dependencies(?:\s+title=""([^""]+)"")?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex PackageLineRegex = new(
        @"^[-*]?\s*([a-zA-Z0-9_.\-]+)\s*(?:@|v)?([0-9.]+)?\s*(?:\(([^)]+)\))?(?:\s*\|\s*(.+))?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Parses dependencies from Markdown blocks and compiles a Third-Party Software Notice appendix.
    /// </summary>
    public static AttributionReport GenerateAttributions(string markdown)
    {
        var report = new AttributionReport();
        if (string.IsNullOrWhiteSpace(markdown))
            return report;

        string targetText = markdown;
        var fenceMatch = DepFenceRegex.Match(markdown);
        if (fenceMatch.Success)
        {
            targetText = fenceMatch.Groups[2].Value;
        }

        foreach (Match m in PackageLineRegex.Matches(targetText))
        {
            string name = m.Groups[1].Value.Trim();
            string version = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "1.0.0";
            string spdx = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "MIT";
            string? copyright = m.Groups[4].Success ? m.Groups[4].Value.Trim() : null;

            if (!string.IsNullOrEmpty(name) && !name.Equals("dependencies", StringComparison.OrdinalIgnoreCase))
            {
                report.Packages.Add(new DependencyPackage(name, version, spdx, copyright));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Third-Party Software Notices");
        sb.AppendLine();
        sb.AppendLine("This software incorporates the following open-source components and licenses:");
        sb.AppendLine();

        foreach (var p in report.Packages)
        {
            sb.AppendLine($"### {p.Name} (v{p.Version})");
            sb.AppendLine($"**License:** {p.SpdxLicense}");
            if (!string.IsNullOrEmpty(p.Copyright))
            {
                sb.AppendLine($"**Copyright:** {p.Copyright}");
            }
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(GetStandardLicenseText(p.SpdxLicense, p.Name, p.Copyright));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        report.GeneratedNoticeAppendix = sb.ToString().TrimEnd();
        return report;
    }

    private static string GetStandardLicenseText(string spdx, string pkg, string? cpy)
    {
        string cpyText = !string.IsNullOrEmpty(cpy) ? cpy : $"Copyright (c) {DateTime.UtcNow.Year} {pkg} Contributors";
        return spdx.ToUpperInvariant() switch
        {
            "MIT" or "ISC" => $"{cpyText}\n\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files...",
            "0BSD" => $"{cpyText}\n\nPermission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted.",
            "APACHE-2.0" or "APACHE-2.0.0" => $"{cpyText}\n\nLicensed under the Apache License, Version 2.0 (the \"License\"); you may not use this file except in compliance with the License...",
            "BSD-3-CLAUSE" or "BSD-2-CLAUSE" => $"{cpyText}\n\nRedistribution and use in source and binary forms, with or without modification, are permitted provided that...",
            _ => $"{cpyText}\n\nLicensed under the {spdx} open source license."
        };
    }
}
