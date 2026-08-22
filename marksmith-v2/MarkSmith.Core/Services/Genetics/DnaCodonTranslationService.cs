using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Genetics;

public class DnaSequenceModel
{
    public string Title { get; set; } = "DNA Translation";
    public string SenseStrand { get; set; } = "ATGGCC";
    public string AntiSenseStrand { get; set; } = "TACCGG";
    public List<string> Codons { get; } = new();
    public List<string> AminoAcids { get; } = new();
}

/// <summary>
/// Service for parsing DNA/RNA nucleotide sequences and rendering codon translation reading frames in SVG.
/// </summary>
public static class DnaCodonTranslationService
{
    private static readonly Regex DnaFenceRegex = new(
        @":::dna(?:\s+""([^""]+)"")?(?:\s+([^\r\n]+))?\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex SeqRegex = new(
        @"seq\s*=\s*""([A-Za-z]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> GeneticCode = new()
    {
        { "TTT", "Phe" }, { "TTC", "Phe" }, { "TTA", "Leu" }, { "TTG", "Leu" },
        { "CTT", "Leu" }, { "CTC", "Leu" }, { "CTA", "Leu" }, { "CTG", "Leu" },
        { "ATT", "Ile" }, { "ATC", "Ile" }, { "ATA", "Ile" }, { "ATG", "Met" },
        { "GTT", "Val" }, { "GTC", "Val" }, { "GTA", "Val" }, { "GTG", "Val" },
        { "TCT", "Ser" }, { "TCC", "Ser" }, { "TCA", "Ser" }, { "TCG", "Ser" },
        { "CCT", "Pro" }, { "CCC", "Pro" }, { "CCA", "Pro" }, { "CCG", "Pro" },
        { "ACT", "Thr" }, { "ACC", "Thr" }, { "ACA", "Thr" }, { "ACG", "Thr" },
        { "GCT", "Ala" }, { "GCC", "Ala" }, { "GCA", "Ala" }, { "GCG", "Ala" },
        { "TAT", "Tyr" }, { "TAC", "Tyr" }, { "TAA", "STOP" }, { "TAG", "STOP" },
        { "CAT", "His" }, { "CAC", "His" }, { "CAA", "Gln" }, { "CAG", "Gln" },
        { "AAT", "Asn" }, { "AAC", "Asn" }, { "AAA", "Lys" }, { "AAG", "Lys" },
        { "GAT", "Asp" }, { "GAC", "Asp" }, { "GAA", "Glu" }, { "GAG", "Glu" },
        { "TGT", "Cys" }, { "TGC", "Cys" }, { "TGA", "STOP" }, { "TGG", "Trp" },
        { "CGT", "Arg" }, { "CGC", "Arg" }, { "CGA", "Arg" }, { "CGG", "Arg" },
        { "AGT", "Ser" }, { "AGC", "Ser" }, { "AGA", "Arg" }, { "AGG", "Arg" },
        { "GGT", "Gly" }, { "GGC", "Gly" }, { "GGA", "Gly" }, { "GGG", "Gly" }
    };

    public static DnaSequenceModel ParseDna(string blockText, string defaultTitle = "DNA Translation")
    {
        var model = new DnaSequenceModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            TranslateModel(model);
            return model;
        }

        var fence = DnaFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Success ? fence.Groups[1].Value : fence.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(header)) model.Title = header.Trim();
            text = (fence.Groups[2].Value + " " + fence.Groups[3].Value);
        }

        var sm = SeqRegex.Match(text);
        if (sm.Success)
        {
            model.SenseStrand = sm.Groups[1].Value.ToUpperInvariant().Trim();
        }

        TranslateModel(model);
        return model;
    }

    private static void TranslateModel(DnaSequenceModel model)
    {
        var sbComp = new StringBuilder();
        foreach (char c in model.SenseStrand)
        {
            sbComp.Append(c switch
            {
                'A' => 'T',
                'T' => 'A',
                'C' => 'G',
                'G' => 'C',
                _ => 'N'
            });
        }
        model.AntiSenseStrand = sbComp.ToString();

        for (int i = 0; i + 2 < model.SenseStrand.Length; i += 3)
        {
            string codon = model.SenseStrand.Substring(i, 3);
            model.Codons.Add(codon);
            if (GeneticCode.TryGetValue(codon, out string? aa))
            {
                model.AminoAcids.Add(aa);
            }
            else
            {
                model.AminoAcids.Add("?");
            }
        }
    }

    public static string RenderDnaSvg(DnaSequenceModel model)
    {
        double baseW = 18;
        double width = Math.Max(380, model.SenseStrand.Length * baseW + 60);
        double height = 200;
        double ox = 30;
        double cy = 90;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-dna-svg\">");
        sb.AppendLine("""
            <style>
              .dna-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .dna-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .dna-backbone { stroke: #475569; stroke-width: 2; }
              .dna-base-a { fill: #ef4444; }
              .dna-base-t { fill: #3b82f6; }
              .dna-base-c { fill: #eab308; }
              .dna-base-g { fill: #22c55e; }
              .dna-txt { font-family: monospace; font-size: 11px; font-weight: 700; fill: #ffffff; text-anchor: middle; }
              .dna-aa { font-family: Segoe UI, sans-serif; font-size: 10px; font-weight: 700; fill: #38bdf8; text-anchor: middle; }
              .dna-hbond { stroke: #94a3b8; stroke-width: 1; stroke-dasharray: 2 2; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"dna-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"dna-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Backbones
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{cy - 20}\" x2=\"{ox + model.SenseStrand.Length * baseW}\" y2=\"{cy - 20}\" class=\"dna-backbone\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{cy + 20}\" x2=\"{ox + model.SenseStrand.Length * baseW}\" y2=\"{cy + 20}\" class=\"dna-backbone\" />");

        for (int i = 0; i < model.SenseStrand.Length; i++)
        {
            double bx = ox + i * baseW + baseW / 2;
            char sChar = model.SenseStrand[i];
            char asChar = model.AntiSenseStrand[i];

            string sClass = sChar switch { 'A' => "dna-base-a", 'T' => "dna-base-t", 'C' => "dna-base-c", _ => "dna-base-g" };
            string asClass = asChar switch { 'A' => "dna-base-a", 'T' => "dna-base-t", 'C' => "dna-base-c", _ => "dna-base-g" };

            // Sense Base
            sb.AppendLine($"  <rect x=\"{bx - 7}\" y=\"{cy - 28}\" width=\"14\" height=\"16\" rx=\"2\" class=\"{sClass}\" />");
            sb.AppendLine($"  <text x=\"{bx}\" y=\"{cy - 16}\" class=\"dna-txt\">{sChar}</text>");

            // H-bond
            sb.AppendLine($"  <line x1=\"{bx}\" y1=\"{cy - 10}\" x2=\"{bx}\" y2=\"{cy + 10}\" class=\"dna-hbond\" />");

            // AntiSense Base
            sb.AppendLine($"  <rect x=\"{bx - 7}\" y=\"{cy + 12}\" width=\"14\" height=\"16\" rx=\"2\" class=\"{asClass}\" />");
            sb.AppendLine($"  <text x=\"{bx}\" y=\"{cy + 24}\" class=\"dna-txt\">{asChar}</text>");
        }

        // Codon Translation Labels
        for (int i = 0; i < model.AminoAcids.Count; i++)
        {
            double aaX = ox + i * 3 * baseW + (1.5 * baseW);
            sb.AppendLine($"  <text x=\"{aaX}\" y=\"{cy + 52}\" class=\"dna-aa\">{model.AminoAcids[i]}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
