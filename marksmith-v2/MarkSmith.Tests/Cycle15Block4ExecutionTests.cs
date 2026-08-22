using System;
using System.Collections.Generic;
using MarkSmith.Services.Api;
using MarkSmith.Services.Design;
using MarkSmith.Services.Engineering;
using MarkSmith.Services.Music;
using MarkSmith.Services.Navigation;
using MarkSmith.Services.Specifications;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle15Block4ExecutionTests
{
    [Fact]
    public void RfcRequirementBadgeService_HighlightsRfcKeywords()
    {
        string md = "Clients MUST validate payloads and SHOULD NOT retry immediately. Servers MAY throttle requests.";
        string highlighted = RfcRequirementBadgeService.HighlightRfcKeywords(md);

        Assert.Contains("class=\"ms-rfc-badge ms-rfc-must\"", highlighted);
        Assert.Contains(">MUST</span>", highlighted);
        Assert.Contains("class=\"ms-rfc-badge ms-rfc-should-not\"", highlighted);
        Assert.Contains(">SHOULD NOT</span>", highlighted);
        Assert.Contains("class=\"ms-rfc-badge ms-rfc-may\"", highlighted);
        Assert.Contains(">MAY</span>", highlighted);
    }

    [Fact]
    public void ColorPaletteSwatchService_ParsesPaletteAndRendersSvg()
    {
        string paletteMd = """
            :::palette Design System
            Primary: #1f6feb
            Success: #238636
            Warning: #d29922
            Danger: #f85149
            :::
            """;

        var model = ColorPaletteSwatchService.ParsePalette(paletteMd);
        Assert.Equal("Design System", model.Title);
        Assert.Equal(4, model.Colors.Count);
        Assert.Equal("#1f6feb", model.Colors[0].HexCode);
        Assert.True(model.Colors[0].IsDark);

        string svg = ColorPaletteSwatchService.RenderSwatchesSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Primary", svg);
        Assert.Contains("#1F6FEB", svg);
    }

    [Fact]
    public void MarkdownApiSpecService_TransformsApiEndpoints()
    {
        string md = """
            :::api GET /api/v1/documents/{id}
            Summary: Fetch document metadata by ID
            Param: id (string, required) - The unique document identifier
            Param: version (int, optional) - Specific version index
            Response 200: { "id": "123", "title": "Doc" }
            :::
            """;

        string html = MarkdownApiSpecService.TransformApiSpecs(md);
        Assert.Contains("class=\"ms-api-card\"", html);
        Assert.Contains("ms-api-get", html);
        Assert.Contains("/api/v1/documents/{id}", html);
        Assert.Contains("Fetch document metadata by ID", html);
        Assert.Contains("required", html);
    }

    [Fact]
    public void AbcMusicScoreRendererService_ParsesAbcAndRendersStaffSvg()
    {
        string abc = """
            :::music
            X:1
            T:C Major Arpeggio
            M:4/4
            K:C
            C E G c | G E C |
            :::
            """;

        var score = AbcMusicScoreRendererService.ParseAbc(abc);
        Assert.Equal("C Major Arpeggio", score.Title);
        Assert.Equal("C", score.Key);
        Assert.Equal(9, score.Notes.Count);

        string svg = AbcMusicScoreRendererService.RenderStaffSvg(score);
        Assert.Contains("<svg", svg);
        Assert.Contains("class=\"ms-music-staff\"", svg);
        Assert.Contains("class=\"note-head\"", svg);
        Assert.Contains("class=\"staff-line\"", svg);
    }

    [Fact]
    public void LogicCircuitRendererService_ParsesCircuitAndRendersSvg()
    {
        string circuitText = """
            AND(A, B) -> X
            OR(X, C) -> OUT
            """;

        var circuit = LogicCircuitRendererService.ParseCircuit(circuitText);
        Assert.Equal(2, circuit.Gates.Count);
        Assert.Equal("AND", circuit.Gates[0].GateType);
        Assert.Equal("X", circuit.Gates[0].Output);
        Assert.Equal("OR", circuit.Gates[1].GateType);
        Assert.Equal("OUT", circuit.Gates[1].Output);

        string svg = LogicCircuitRendererService.RenderCircuitSvg(circuit);
        Assert.Contains("<svg", svg);
        Assert.Contains("AND", svg);
        Assert.Contains("OR", svg);
        Assert.Contains("class=\"gate-body\"", svg);
    }

    [Fact]
    public void AccordionTabMatrixService_ParsesMatrixAndRendersHtml()
    {
        string md = """
            :::matrix-tabs Architecture Capabilities
            ### Storage Engines
            | Feature | SQLite | OpenXML | LevelDB |
            |---------|--------|---------|---------|
            | Speed   | Fast   | Fast    | Extreme |
            | Schema  | Rigid  | XML     | Key-Val |
            ### Security Features
            | Feature | SQLite | OpenXML | LevelDB |
            |---------|--------|---------|---------|
            | Crypto  | AES256 | ZIP Enc | None    |
            :::
            """;

        string html = AccordionTabMatrixService.TransformMatrixTabs(md);
        Assert.Contains("class=\"ms-matrix-container\"", html);
        Assert.Contains("Architecture Capabilities", html);
        Assert.Contains("Storage Engines", html);
        Assert.Contains("Security Features", html);
        Assert.Contains("SQLite", html);
    }
}
