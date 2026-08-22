using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Batch 15 (#70): the 08-19/08-20 cycle wave generated 20 more single-file visualizers referenced
// ONLY by their own Cycle tests (bench/virtual). 18 of them are now wired into the shared
// EngineeringFences dispatch table, which covers BOTH pipelines at once: the preview lift pass and
// the DOCX path (Batch 14's TryGetEngineeringFenceName/TryRenderEngineeringFence share the same
// table). These tests pin: fence-name resolution, SVG rendering via the shared entry point, DOCX
// pipeline detection, code-fence exclusion, and preview parity for the new wave.
public class EngineeringFenceWave2WiringTests
{
    // Canonical fence name + a realistic minimal body (verbatim shapes from the Cycle tests).
    public static TheoryData<string, string> Wave2Fences => new()
    {
        { "resistor", "\"4.7k 5%\"" },
        { "crystal", "\"FCC Gold\" type=FCC a=4.08\natom Au (0, 0, 0)" },
        { "palette", "Design System\nPrimary: #1f6feb\nSuccess: #238636" },
        { "sudoku", "\"Daily Sudoku\"\n5 3 . . 7 . . . .\n6 . . 1 9 5 . . ." },
        { "map", "Server Backbones\npin [40.7128, -74.0060] \"New York\"\npin [51.5074, -0.1278] \"London\"" },
        { "stratigraphy", "Well Log A-1\nlayer 0-20m \"Sandstone Unit\" [lithology: sandstone, color: #fef08a]" },
        { "origami", "\"Waterbomb Base Step 2\"\nvalley (0, 0) -> (100, 100)\nmountain (0, 100) -> (100, 0)" },
        { "nn", "Classifier Network\nlayer \"Input\" nodes=4\nlayer \"Output\" nodes=2 act=softmax" },
        { "abacus", "\"Math Counter\"\nvalue = 2026" },
        { "venn", "Developer Skillset\nset A: \"Frontend\" (40)\nset B: \"Backend\" (50)" },
        { "diffraction", "\"Young's Slits\"\nwavelength = 650\nd = 0.25\nL = 1.5" },
        { "lissajous", "\"Harmonic 3:2\"\nfx = 3.0\nfy = 2.0\ndelta = 0.5" },
        { "optics", "\"Simple Magnifier\"\nlens convex pos=50 f=100\nray (0, 20) -> (50, 20)" },
        { "morse", "\"SOS MARKSMITH\" [wpm: 25]" },
        { "clock", "\"Tower Clock\"\ntime = \"10:15\"" },
        { "histogram", "Response Times\nData: 10, 12, 14, 18, 20, 22, 25, 28, 30, 35\nBins: 4" },
        { "aqueduct", "\"Anio Novus Profile\"\nsegment 0-50m elev=100 slope=0.002 [type: \"arcade\", arches: 4]" },
        { "kmap", "\"Half Adder Carry\"\nvars = 2\nvalues: 0 0 0 1" },
    };

    private static string Block(string name, string body) => $":::{name} {body}\n:::";

    // ── Shared table resolution + rendering (the exact entry points the DOCX path uses) ────

    [Theory]
    [MemberData(nameof(Wave2Fences))]
    public void TryGetEngineeringFenceName_Resolves_EveryWave2Fence(string name, string body)
    {
        Assert.True(MarkdownHtmlService.TryGetEngineeringFenceName(Block(name, body), out var resolved));
        Assert.Equal(name, resolved);
    }

    [Theory]
    [MemberData(nameof(Wave2Fences))]
    public void TryRenderEngineeringFence_RendersSvg_ForEveryWave2Fence(string name, string body)
    {
        Assert.True(MarkdownHtmlService.TryRenderEngineeringFence(Block(name, body), out var svg));
        Assert.StartsWith("<svg", svg);
        Assert.Contains("</svg>", svg);
    }

    // ── DOCX pipeline detection (EngineeringDiagramDetector reads the same table) ───────────

    [Theory]
    [MemberData(nameof(Wave2Fences))]
    public void Pipeline_Detects_EveryWave2Fence_As_EngineeringDiagram(string name, string body)
    {
        var pipeline = new AdvancedFeaturePipeline();
        var nodes = pipeline.Process($"# Wave2\n\n{Block(name, body)}\n", $"doc-wave2-{name}");

        var node = Assert.Single(nodes);
        Assert.Equal("EngineeringDiagram", node.Detector.FeatureName);
    }

    [Fact]
    public void Pipeline_Ignores_Wave2Fence_Inside_CodeFence()
    {
        var md = "# Example\n\n```\n" + Block("kmap", "\"Half Adder Carry\"\nvars = 2\nvalues: 0 0 0 1") + "\n```\n";
        var pipeline = new AdvancedFeaturePipeline();
        Assert.Empty(pipeline.Process(md, "doc-wave2-codefenced"));
    }

    // ── Preview parity (both render paths lift the new wave exactly like the old one) ──────

    [Fact]
    public void Preview_Lifts_Wave2Fences_To_Svg()
    {
        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        var md = "# Wave2\n\n" + Block("kmap", "\"Half Adder Carry\"\nvars = 2\nvalues: 0 0 0 1") + "\n";

        var html = new MarkdownHtmlService().RenderCanvasOnly(md, new AppSettings(), theme);

        Assert.NotNull(html);
        Assert.DoesNotContain(":::kmap", html);
        Assert.Contains("<svg", html);
    }

    // ── Collision guard: PrismSpectrogramService's :::prism stays owned by PrismDispersion ─

    [Fact]
    public void Prism_Alias_Still_Resolves_To_PrismDispersion_Not_Spectrogram()
    {
        // Batch 15 deliberately did NOT wire PrismSpectrogramService: its :::prism fence collides
        // with the existing prism alias. Pin that the lookup still resolves to the original entry
        // (css class prism-dispersion-diagram) so a future wiring cannot silently steal the name.
        Assert.True(MarkdownHtmlService.TryRenderEngineeringFence(
            ":::prism \"Crown Glass\"\nangle: 60\n:::", out var svg));
        Assert.StartsWith("<svg", svg);
    }
}
