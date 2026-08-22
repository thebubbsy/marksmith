using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Chemistry;

public record Atom3D(string Element, string Id, double X, double Y, double Z);
public record MolecularBond(string FromId, string ToId, string Order);

public class MoleculeModel
{
    public string Name { get; set; } = "Molecule";
    public List<Atom3D> Atoms { get; } = new();
    public List<MolecularBond> Bonds { get; } = new();
}

/// <summary>
/// Service for parsing 3D atomic coordinates and rendering shaded pseudo-3D SVG ball-and-stick molecular models.
/// </summary>
public static class MolecularBallAndStickRendererService
{
    private static readonly Regex MoleculeFenceRegex = new(
        @":::molecule(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex AtomRegex = new(
        @"atom\s+([A-Za-z]+)(\d*)\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BondRegex = new(
        @"bond\s+([A-Za-z0-9]+)\s*-\s*([A-Za-z0-9]+)(?:\s+(single|double|triple))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a molecule block into atoms and bonds.
    /// </summary>
    public static MoleculeModel ParseMolecule(string blockText, string defaultName = "Molecule")
    {
        var model = new MoleculeModel { Name = defaultName };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = MoleculeFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Name = fence.Groups[1].Value.Trim();
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        int atomIndex = 1;

        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var am = AtomRegex.Match(l);
            if (am.Success)
            {
                string elem = am.Groups[1].Value.ToUpperInvariant();
                string id = !string.IsNullOrEmpty(am.Groups[2].Value) ? elem + am.Groups[2].Value : elem + atomIndex;
                double x = double.TryParse(am.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ax) ? ax : 0.0;
                double y = double.TryParse(am.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ay) ? ay : 0.0;
                double z = double.TryParse(am.Groups[5].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double az) ? az : 0.0;
                model.Atoms.Add(new Atom3D(elem, id, x, y, z));
                atomIndex++;
                continue;
            }

            var bm = BondRegex.Match(l);
            if (bm.Success)
            {
                string from = bm.Groups[1].Value.ToUpperInvariant();
                string to = bm.Groups[2].Value.ToUpperInvariant();
                string ord = bm.Groups[3].Success ? bm.Groups[3].Value.ToLowerInvariant() : "single";
                model.Bonds.Add(new MolecularBond(from, to, ord));
                continue;
            }
        }

        return model;
    }

    /// <summary>
    /// Renders an SVG ball-and-stick model with CPK element coloring.
    /// </summary>
    public static string RenderMoleculeSvg(MoleculeModel model)
    {
        double width = 420;
        double height = 260;
        double cx = width / 2;
        double cy = height / 2 + 15;
        double scale = 65;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-molecule-svg\">");
        sb.AppendLine("""
            <defs>
              <radialGradient id="cpk-C" cx="35%" cy="35%" r="65%">
                <stop offset="0%" stop-color="#94a3b8" />
                <stop offset="100%" stop-color="#334155" />
              </radialGradient>
              <radialGradient id="cpk-O" cx="35%" cy="35%" r="65%">
                <stop offset="0%" stop-color="#f87171" />
                <stop offset="100%" stop-color="#dc2626" />
              </radialGradient>
              <radialGradient id="cpk-H" cx="35%" cy="35%" r="65%">
                <stop offset="0%" stop-color="#ffffff" />
                <stop offset="100%" stop-color="#cbd5e1" />
              </radialGradient>
              <radialGradient id="cpk-N" cx="35%" cy="35%" r="65%">
                <stop offset="0%" stop-color="#60a5fa" />
                <stop offset="100%" stop-color="#2563eb" />
              </radialGradient>
            </defs>
            <style>
              .mol-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .mol-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .mol-bond { stroke: #64748b; stroke-width: 6; stroke-linecap: round; }
              .atom-sym { font-family: Segoe UI, sans-serif; font-size: 11px; font-weight: 700; fill: #ffffff; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"mol-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"mol-title\">{System.Net.WebUtility.HtmlEncode(model.Name)}</text>");

        var atomCoords = new Dictionary<string, (double X, double Y, double R, string Grad, string Elem)>();
        foreach (var a in model.Atoms)
        {
            double px = cx + a.X * scale;
            double py = cy - a.Y * scale;
            string grad = a.Element switch
            {
                "O" => "url(#cpk-O)",
                "H" => "url(#cpk-H)",
                "N" => "url(#cpk-N)",
                _ => "url(#cpk-C)"
            };
            double r = a.Element == "H" ? 14 : 20;
            atomCoords[a.Id] = (px, py, r, grad, a.Element);
        }

        // Draw Bonds
        foreach (var b in model.Bonds)
        {
            if (atomCoords.TryGetValue(b.FromId, out var a1) && atomCoords.TryGetValue(b.ToId, out var a2))
            {
                sb.AppendLine($"  <line x1=\"{a1.X}\" y1=\"{a1.Y}\" x2=\"{a2.X}\" y2=\"{a2.Y}\" class=\"mol-bond\" />");
            }
        }

        // Draw Atoms
        foreach (var kv in atomCoords)
        {
            var (px, py, r, grad, elem) = kv.Value;
            sb.AppendLine($"  <circle cx=\"{px}\" cy=\"{py}\" r=\"{r}\" fill=\"{grad}\" stroke=\"#0f172a\" stroke-width=\"1.5\" />");
            if (elem != "H")
            {
                sb.AppendLine($"  <text x=\"{px}\" y=\"{py + 4}\" class=\"atom-sym\">{elem}</text>");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
