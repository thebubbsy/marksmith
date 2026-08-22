using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Crystallography;

public record CrystalAtom(string Element, double X, double Y, double Z, string ColorHex);

public class CrystalLatticeModel
{
    public string Title { get; set; } = "Crystal Lattice";
    public string LatticeType { get; set; } = "FCC";
    public double LatticeConstantA { get; set; } = 4.0;
    public List<CrystalAtom> Atoms { get; } = new();
}

/// <summary>
/// Service for parsing crystallography unit cell parameters and rendering 3D isometric SVG lattice structures.
/// </summary>
public static class CrystalLatticeRendererService
{
    private static readonly Regex CrystalFenceRegex = new(
        @":::crystal(?:\s+""([^""]+)"")?(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex TypeRegex = new(
        @"type\s*=\s*([A-Za-z0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ParamARegex = new(
        @"a\s*=\s*(-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AtomRegex = new(
        @"atom\s+([A-Za-z]+)\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static CrystalLatticeModel ParseCrystal(string blockText, string defaultTitle = "Unit Cell")
    {
        var model = new CrystalLatticeModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = CrystalFenceRegex.Match(blockText);
        string header = fence.Success ? (fence.Groups[2].Value) : "";
        string body = fence.Success ? fence.Groups[3].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var tm = TypeRegex.Match(header);
        if (tm.Success) model.LatticeType = tm.Groups[1].Value.ToUpperInvariant();

        var am = ParamARegex.Match(header);
        if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pv))
            model.LatticeConstantA = pv;

        var lines = body.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var atomMatch = AtomRegex.Match(l);
            if (atomMatch.Success)
            {
                string elem = atomMatch.Groups[1].Value.ToUpperInvariant();
                double x = double.TryParse(atomMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ax) ? ax : 0.0;
                double y = double.TryParse(atomMatch.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ay) ? ay : 0.0;
                double z = double.TryParse(atomMatch.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double az) ? az : 0.0;
                string col = elem == "NA" ? "#eab308" : elem == "CL" ? "#22c55e" : elem == "AU" ? "#eab308" : "#38bdf8";
                model.Atoms.Add(new CrystalAtom(elem, x, y, z, col));
            }
        }

        return model;
    }

    public static string RenderCrystalSvg(CrystalLatticeModel model)
    {
        double width = 420;
        double height = 280;
        double cx = width / 2;
        double cy = height / 2 + 10;
        double size = 100;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-crystal-svg\">");
        sb.AppendLine("""
            <style>
              .xtal-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .xtal-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .xtal-meta { font-family: monospace; font-size: 10px; fill: #94a3b8; }
              .xtal-wire { stroke: #475569; stroke-width: 1.5; fill: none; }
              .xtal-atom { stroke: #0f172a; stroke-width: 1.5; }
              .xtal-sym { font-family: Segoe UI, sans-serif; font-size: 8px; font-weight: 700; fill: #ffffff; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"xtal-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"xtal-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"xtal-meta\">Lattice: {model.LatticeType} • a = {model.LatticeConstantA} Å</text>");

        // Isometric projection: (x, y, z) -> 2D
        (double X, double Y) Project(double x, double y, double z)
        {
            double px = cx + (x - z) * (size * 0.707);
            double py = cy + (x + z) * (size * 0.35) - y * size;
            return (px, py);
        }

        // Draw Unit Cell Bounding Cube Wireframe
        var c000 = Project(0, 0, 0); var c100 = Project(1, 0, 0);
        var c110 = Project(1, 1, 0); var c010 = Project(0, 1, 0);
        var c001 = Project(0, 0, 1); var c101 = Project(1, 0, 1);
        var c111 = Project(1, 1, 1); var c011 = Project(0, 1, 1);

        sb.AppendLine($"  <polygon points=\"{c000.X},{c000.Y} {c100.X},{c100.Y} {c110.X},{c110.Y} {c010.X},{c010.Y}\" class=\"xtal-wire\" />");
        sb.AppendLine($"  <polygon points=\"{c001.X},{c001.Y} {c101.X},{c101.Y} {c111.X},{c111.Y} {c011.X},{c011.Y}\" class=\"xtal-wire\" />");
        sb.AppendLine($"  <line x1=\"{c000.X}\" y1=\"{c000.Y}\" x2=\"{c001.X}\" y2=\"{c001.Y}\" class=\"xtal-wire\" />");
        sb.AppendLine($"  <line x1=\"{c100.X}\" y1=\"{c100.Y}\" x2=\"{c101.X}\" y2=\"{c101.Y}\" class=\"xtal-wire\" />");
        sb.AppendLine($"  <line x1=\"{c110.X}\" y1=\"{c110.Y}\" x2=\"{c111.X}\" y2=\"{c111.Y}\" class=\"xtal-wire\" />");
        sb.AppendLine($"  <line x1=\"{c010.X}\" y1=\"{c010.Y}\" x2=\"{c011.X}\" y2=\"{c011.Y}\" class=\"xtal-wire\" />");

        // Draw Atoms
        foreach (var atom in model.Atoms)
        {
            var p = Project(atom.X, atom.Y, atom.Z);
            sb.AppendLine($"  <circle cx=\"{p.X}\" cy=\"{p.Y}\" r=\"10\" fill=\"{atom.ColorHex}\" class=\"xtal-atom\" />");
            sb.AppendLine($"  <text x=\"{p.X}\" y=\"{p.Y + 3}\" class=\"xtal-sym\">{atom.Element}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
