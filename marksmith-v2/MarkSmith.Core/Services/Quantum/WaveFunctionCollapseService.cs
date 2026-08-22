using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Quantum;

public class QuantumState
{
    public string Label { get; set; } = "|0⟩";
    public double Amplitude { get; set; } = 1.0;
    public double PhaseRad { get; set; } = 0.0;
    public double Probability => Amplitude * Amplitude;
}

public class WfcTile
{
    public string Id { get; set; } = "Tile";
    public string Name { get; set; } = "Ground";
    public string ColorHex { get; set; } = "#38bdf8";
    public List<string> AllowedNeighbors { get; } = new();
}

public class WfcGridCell
{
    public int X { get; set; }
    public int Y { get; set; }
    public List<WfcTile> PossibleTiles { get; } = new();
    public WfcTile? CollapsedTile { get; set; }
    public bool IsCollapsed => CollapsedTile != null;
    public int Entropy => IsCollapsed ? 0 : PossibleTiles.Count;
}

public class WaveFunctionModel
{
    public string Title { get; set; } = "Wave Function Collapse";
    public string Mode { get; set; } = "quantum"; // "quantum" or "procedural" / "wfc"
    
    // Quantum properties
    public List<QuantumState> States { get; } = new();
    public string PotentialWell { get; set; } = "Infinite Square Well";
    public int QuantumNumberN { get; set; } = 2;
    public string? CollapsedStateLabel { get; set; }

    // Procedural WFC properties
    public int GridWidth { get; set; } = 5;
    public int GridHeight { get; set; } = 5;
    public List<WfcTile> Palette { get; } = new();
    public List<WfcGridCell> GridCells { get; } = new();
}

/// <summary>
/// Service for parsing and simulating Quantum Wave Function Superposition &amp; Collapse,
/// as well as Procedural 2D Wave Function Collapse (WFC) entropy grid generation and rendering SVG diagrams.
/// </summary>
public static class WaveFunctionCollapseService
{
    private static readonly Regex WaveFunctionFenceRegex = new(
        @":::(wavefunction|wfc|collapse|quantum-wave)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ModeRegex = new(
        @"(?:mode|type)\s*[:=]\s*""?([A-Za-z0-9_-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SizeRegex = new(
        @"(?:size|grid)\s*[:=]\s*""?(\d+)\s*[xX,]\s*(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StateRegex = new(
        @"\|?([A-Za-z0-9_]+)⟩?\s*:\s*(-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    private static readonly Regex CollapseToRegex = new(
        @"collapse(?:_to)?\s*[:=]\s*\|?([A-Za-z0-9_]+)⟩?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static WaveFunctionModel ParseWaveFunction(string blockText, string defaultTitle = "Wave Function Collapse")
    {
        var model = new WaveFunctionModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            SetupDefaultQuantumModel(model);
            return model;
        }

        var fence = WaveFunctionFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string tag = fence.Groups[1].Value.ToLowerInvariant();
            if (tag is "wfc" or "collapse") model.Mode = "wfc";
            else model.Mode = "quantum";

            string header = fence.Groups[2].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var mm = ModeRegex.Match(header);
            if (mm.Success) model.Mode = mm.Groups[1].Value.ToLowerInvariant();

            text = fence.Groups[3].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        bool hasExplicitStates = false;

        foreach (var raw in lines)
        {
            string l = raw.Trim();
            if (l.StartsWith("mode:", StringComparison.OrdinalIgnoreCase))
            {
                model.Mode = l.Substring(5).Trim().ToLowerInvariant();
            }
            else if (l.StartsWith("size:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("grid:", StringComparison.OrdinalIgnoreCase))
            {
                var sm = SizeRegex.Match(l);
                if (sm.Success)
                {
                    if (int.TryParse(sm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gw))
                        model.GridWidth = Math.Clamp(gw, 2, 20);
                    if (int.TryParse(sm.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gh))
                        model.GridHeight = Math.Clamp(gh, 2, 20);
                }
            }
            else if (l.StartsWith("states:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("superposition:", StringComparison.OrdinalIgnoreCase))
            {
                var matches = StateRegex.Matches(l);
                foreach (Match m in matches)
                {
                    string label = "|" + m.Groups[1].Value.Trim('|', '⟩') + "⟩";
                    double amp = double.TryParse(m.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pAmp) ? pAmp : 1.0;
                    model.States.Add(new QuantumState { Label = label, Amplitude = amp });
                    hasExplicitStates = true;
                }
            }
            else if (l.StartsWith("collapse:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("collapse_to:", StringComparison.OrdinalIgnoreCase))
            {
                var cm = CollapseToRegex.Match(l);
                if (cm.Success)
                {
                    model.CollapsedStateLabel = "|" + cm.Groups[1].Value.Trim('|', '⟩') + "⟩";
                }
            }
        }

        if (model.Mode.Contains("wfc") || model.Mode.Contains("tile") || model.Mode.Contains("grid"))
        {
            SetupProceduralWfcGrid(model);
        }
        else
        {
            if (!hasExplicitStates)
            {
                SetupDefaultQuantumModel(model);
            }
            else
            {
                NormalizeQuantumAmplitudes(model);
            }
        }

        return model;
    }

    private static void SetupDefaultQuantumModel(WaveFunctionModel model)
    {
        model.States.Clear();
        model.States.Add(new QuantumState { Label = "|0⟩", Amplitude = 0.6, PhaseRad = 0.0 });
        model.States.Add(new QuantumState { Label = "|1⟩", Amplitude = 0.8, PhaseRad = Math.PI / 3.0 });
        model.CollapsedStateLabel = "|1⟩";
    }

    private static void NormalizeQuantumAmplitudes(WaveFunctionModel model)
    {
        double sumSq = model.States.Sum(s => s.Amplitude * s.Amplitude);
        if (sumSq > 0.0001)
        {
            double norm = Math.Sqrt(sumSq);
            foreach (var s in model.States)
            {
                s.Amplitude /= norm;
            }
        }
    }

    private static void SetupProceduralWfcGrid(WaveFunctionModel model)
    {
        model.Palette.Clear();
        var tGrass = new WfcTile { Id = "grass", Name = "Grass", ColorHex = "#22c55e" };
        var tRoad = new WfcTile { Id = "road", Name = "Road", ColorHex = "#64748b" };
        var tWater = new WfcTile { Id = "water", Name = "Water", ColorHex = "#38bdf8" };
        var tWall = new WfcTile { Id = "wall", Name = "Wall", ColorHex = "#a855f7" };

        tGrass.AllowedNeighbors.AddRange(new[] { "grass", "road", "water" });
        tRoad.AllowedNeighbors.AddRange(new[] { "road", "grass", "wall" });
        tWater.AllowedNeighbors.AddRange(new[] { "water", "grass" });
        tWall.AllowedNeighbors.AddRange(new[] { "wall", "road" });

        model.Palette.Add(tGrass);
        model.Palette.Add(tRoad);
        model.Palette.Add(tWater);
        model.Palette.Add(tWall);

        model.GridCells.Clear();
        var rand = new Random(42);

        for (int y = 0; y < model.GridHeight; y++)
        {
            for (int x = 0; x < model.GridWidth; x++)
            {
                var cell = new WfcGridCell { X = x, Y = y };
                cell.PossibleTiles.AddRange(model.Palette);

                // Partially collapse grid for visual demonstration of WFC superposition vs collapse
                if ((x + y) % 2 == 0 || (x == y))
                {
                    cell.CollapsedTile = model.Palette[rand.Next(model.Palette.Count)];
                    cell.PossibleTiles.Clear();
                    cell.PossibleTiles.Add(cell.CollapsedTile);
                }

                model.GridCells.Add(cell);
            }
        }
    }

    public static string RenderWaveFunctionSvg(WaveFunctionModel model)
    {
        if (model.Mode.Contains("wfc") || model.Mode.Contains("tile") || model.Mode.Contains("grid"))
        {
            return RenderWfcGridSvg(model);
        }

        return RenderQuantumWaveSvg(model);
    }

    private static string RenderQuantumWaveSvg(WaveFunctionModel model)
    {
        double width = 480;
        double height = 280;
        double ox = 50;
        double baseWaveY = 175;
        double waveW = 380;
        double waveH = 75;
        int samples = 180;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-wavefunction-svg\">");
        sb.AppendLine("""
            <style>
              .wf-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .wf-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .wf-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .wf-well { stroke: #334155; stroke-width: 2; stroke-dasharray: 4 3; }
              .wf-psi { fill: none; stroke: #38bdf8; stroke-width: 2.5; }
              .wf-prob { fill: #38bdf8; fill-opacity: 0.18; stroke: #0284c7; stroke-width: 1; }
              .wf-collapsed { stroke: #ec4899; stroke-width: 3; stroke-linecap: round; }
              .wf-badge { font-family: monospace; font-size: 11px; font-weight: 700; fill: #f43f5e; }
              .wf-state { font-family: monospace; font-size: 10px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"wf-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"wf-title\">⚛ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        var stateSummary = string.Join(" + ", model.States.Select(s => $"{s.Amplitude:F2}{s.Label}"));
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"wf-meta\">Superposition: |Ψ⟩ = {System.Net.WebUtility.HtmlEncode(stateSummary)}</text>");

        // Quantum Potential Well Boundaries
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"65\" x2=\"{ox}\" y2=\"{baseWaveY + 25}\" class=\"wf-well\" />");
        sb.AppendLine($"  <line x1=\"{ox + waveW}\" y1=\"65\" x2=\"{ox + waveW}\" y2=\"{baseWaveY + 25}\" class=\"wf-well\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{baseWaveY}\" x2=\"{ox + waveW}\" y2=\"{baseWaveY}\" stroke=\"#1e293b\" stroke-width=\"1.5\" />");

        // Superposition Waveform ψ(x) and Probability Density |ψ(x)|^2
        var psiPath = new StringBuilder();
        var probPath = new StringBuilder();

        probPath.Append($"M {ox.ToString("F1", CultureInfo.InvariantCulture)} {baseWaveY.ToString("F1", CultureInfo.InvariantCulture)}");

        for (int i = 0; i <= samples; i++)
        {
            double t = i / (double)samples;
            double x = ox + t * waveW;

            // Synthesis of quantum eigenstates in 1D box: ψ_n(x) = sqrt(2/L) * sin(n*pi*x/L)
            double psiVal = 0;
            for (int s = 0; s < model.States.Count; s++)
            {
                int n = s + 1;
                psiVal += model.States[s].Amplitude * Math.Sin(n * Math.PI * t);
            }

            double yPsi = baseWaveY - psiVal * (waveH * 0.75);
            double yProb = baseWaveY - (psiVal * psiVal) * (waveH * 0.85);

            if (i == 0)
                psiPath.Append($"M {x.ToString("F1", CultureInfo.InvariantCulture)} {yPsi.ToString("F1", CultureInfo.InvariantCulture)}");
            else
                psiPath.Append($" L {x.ToString("F1", CultureInfo.InvariantCulture)} {yPsi.ToString("F1", CultureInfo.InvariantCulture)}");

            probPath.Append($" L {x.ToString("F1", CultureInfo.InvariantCulture)} {yProb.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        probPath.Append($" L {(ox + waveW).ToString("F1", CultureInfo.InvariantCulture)} {baseWaveY.ToString("F1", CultureInfo.InvariantCulture)} Z");

        // Render Probability density area & Wave function curve
        sb.AppendLine($"  <path d=\"{probPath}\" class=\"wf-prob\" />");
        sb.AppendLine($"  <path d=\"{psiPath}\" class=\"wf-psi\" />");

        // Render Collapse Eigenstate Pin (if specified)
        if (!string.IsNullOrEmpty(model.CollapsedStateLabel))
        {
            double collapseX = ox + waveW * 0.68;
            double collapsePeakY = baseWaveY - waveH * 1.1;
            sb.AppendLine($"  <line x1=\"{collapseX}\" y1=\"{baseWaveY}\" x2=\"{collapseX}\" y2=\"{collapsePeakY}\" class=\"wf-collapsed\" />");
            sb.AppendLine($"  <circle cx=\"{collapseX}\" cy=\"{collapsePeakY}\" r=\"4.5\" fill=\"#ec4899\" />");
            sb.AppendLine($"  <text x=\"{collapseX + 8}\" y=\"{collapsePeakY + 4}\" class=\"wf-badge\">Collapsed → {System.Net.WebUtility.HtmlEncode(model.CollapsedStateLabel)}</text>");
        }

        // Footer Legend
        sb.AppendLine($"  <text x=\"{ox}\" y=\"{height - 18}\" class=\"wf-state\">— ψ(x) Wave Function Amplitude   ▓ |ψ(x)|² Probability Density</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string RenderWfcGridSvg(WaveFunctionModel model)
    {
        double cellSize = 38;
        double ox = 40;
        double oy = 55;
        double width = Math.Max(380, model.GridWidth * cellSize + ox * 2 + 120);
        double height = model.GridHeight * cellSize + oy + 50;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-wfc-grid-svg\">");
        sb.AppendLine("""
            <style>
              .wfc-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .wfc-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .wfc-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .wfc-cell { stroke: #334155; stroke-width: 1; }
              .wfc-superposed { fill: #1e293b; fill-opacity: 0.6; }
              .wfc-entropy { font-family: monospace; font-size: 10px; fill: #94a3b8; text-anchor: middle; }
              .wfc-label { font-family: Segoe UI, sans-serif; font-size: 9px; font-weight: 700; fill: #ffffff; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"wfc-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"wfc-title\">🎲 {System.Net.WebUtility.HtmlEncode(model.Title)} (WFC Grid)</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"40\" class=\"wfc-meta\">Grid: {model.GridWidth}x{model.GridHeight} • Shannon Entropy Constraint Propagation</text>");

        // Grid Cells
        foreach (var cell in model.GridCells)
        {
            double cx = ox + cell.X * cellSize;
            double cy = oy + cell.Y * cellSize;

            if (cell.IsCollapsed && cell.CollapsedTile != null)
            {
                sb.AppendLine($"  <rect x=\"{cx}\" y=\"{cy}\" width=\"{cellSize - 2}\" height=\"{cellSize - 2}\" rx=\"3\" fill=\"{cell.CollapsedTile.ColorHex}\" class=\"wfc-cell\" />");
                sb.AppendLine($"  <text x=\"{cx + cellSize / 2 - 1}\" y=\"{cy + cellSize / 2 + 3}\" class=\"wfc-label\">{cell.CollapsedTile.Name.Substring(0, 1)}</text>");
            }
            else
            {
                sb.AppendLine($"  <rect x=\"{cx}\" y=\"{cy}\" width=\"{cellSize - 2}\" height=\"{cellSize - 2}\" rx=\"3\" class=\"wfc-cell wfc-superposed\" />");
                sb.AppendLine($"  <text x=\"{cx + cellSize / 2 - 1}\" y=\"{cy + cellSize / 2 + 3}\" class=\"wfc-entropy\">H={cell.Entropy}</text>");
            }
        }

        // Palette Legend on right
        double legX = ox + model.GridWidth * cellSize + 24;
        double legY = oy + 4;
        sb.AppendLine($"  <text x=\"{legX}\" y=\"{legY}\" class=\"wfc-title\" font-size=\"11\">Tiles (Superposition):</text>");
        for (int p = 0; p < model.Palette.Count; p++)
        {
            var tile = model.Palette[p];
            double py = legY + 14 + p * 20;
            sb.AppendLine($"  <rect x=\"{legX}\" y=\"{py - 9}\" width=\"10\" height=\"10\" rx=\"2\" fill=\"{tile.ColorHex}\" />");
            sb.AppendLine($"  <text x=\"{legX + 16}\" y=\"{py}\" class=\"wfc-entropy\" text-anchor=\"start\">{tile.Name}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// Transpiles :::wavefunction and :::wfc markdown code fences into responsive SVG visualizations.
    /// </summary>
    public static string TranspileWaveFunctionBlocks(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        return WaveFunctionFenceRegex.Replace(markdown, match =>
        {
            var model = ParseWaveFunction(match.Value);
            return RenderWaveFunctionSvg(model);
        });
    }
}
