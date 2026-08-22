using System;
using MarkSmith.Services.Civil;
using MarkSmith.Services.Electronics;
using MarkSmith.Services.Identification;
using MarkSmith.Services.Physics;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle21Block4ExecutionTests
{
    [Fact]
    public void LogicGateSchematicService_ParsesNetlistAndRendersSvg()
    {
        string circuitMd = """
            :::circuit "Half Adder Logic"
            gate AND (A, B) -> C
            gate XOR (A, B) -> S
            :::
            """;

        var model = LogicGateSchematicService.ParseCircuit(circuitMd);
        Assert.Equal("Half Adder Logic", model.Title);
        Assert.Equal(2, model.Gates.Count);
        Assert.Equal("AND", model.Gates[0].Type);
        Assert.Equal("A", model.Gates[0].In1);
        Assert.Equal("B", model.Gates[0].In2);
        Assert.Equal("C", model.Gates[0].Out);

        string svg = LogicGateSchematicService.RenderCircuitSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Half Adder Logic", svg);
        Assert.Contains("ct-gate", svg);
        Assert.Contains("ct-wire", svg);
    }

    [Fact]
    public void BarcodeEan13RendererService_EncodesDigitsAndRendersSvg()
    {
        string barcodeMd = """
            :::barcode "9780132350884"
            :::
            """;

        var model = BarcodeEan13RendererService.ParseBarcode(barcodeMd);
        Assert.Equal("9780132350884", model.Code);
        Assert.StartsWith("101", model.BitPattern);
        Assert.EndsWith("101", model.BitPattern);

        string svg = BarcodeEan13RendererService.RenderBarcodeSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("9780132350884", svg);
        Assert.Contains("bc-bar", svg);
    }

    [Fact]
    public void PrismSpectrogramService_CalculatesDispersionAndRendersSvg()
    {
        string prismMd = """
            :::prism "Flint Glass Prism"
            angle = 60
            material = "Flint Glass"
            :::
            """;

        var model = PrismSpectrogramService.ParsePrism(prismMd);
        Assert.Equal("Flint Glass Prism", model.Title);
        Assert.Equal(60.0, model.ApexAngleDeg);
        Assert.Equal("Flint Glass", model.Material);

        string svg = PrismSpectrogramService.RenderPrismSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Flint Glass Prism", svg);
        Assert.Contains("ps-prism", svg);
        Assert.Contains("ps-red", svg);
        Assert.Contains("ps-violet", svg);
    }

    [Fact]
    public void HydraulicGradientService_ParsesChannelAndRendersSvgAqueduct()
    {
        string aqMd = """
            :::aqueduct "Anio Novus Profile"
            segment 0-50m elev=100 slope=0.002 [type: "arcade", arches: 4]
            :::
            """;

        var model = HydraulicGradientService.ParseAqueduct(aqMd);
        Assert.Equal("Anio Novus Profile", model.Title);
        Assert.Single(model.Segments);
        Assert.Equal("arcade", model.Segments[0].Type);
        Assert.Equal(4, model.Segments[0].Arches);

        string svg = HydraulicGradientService.RenderAqueductSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Anio Novus Profile", svg);
        Assert.Contains("aq-stone", svg);
        Assert.Contains("aq-water", svg);
    }

    [Fact]
    public void DoubleSlitInterferenceService_CalculatesIntensityAndRendersSvg()
    {
        string diffMd = """
            :::diffraction "Young's Slits"
            wavelength = 650
            d = 0.25
            L = 1.5
            :::
            """;

        var model = DoubleSlitInterferenceService.ParseDiffraction(diffMd);
        Assert.Equal("Young's Slits", model.Title);
        Assert.Equal(650.0, model.WavelengthNm);
        Assert.Equal(0.25, model.SlitSeparationMm);
        Assert.Equal(1.5, model.ScreenDistanceM);

        string svg = DoubleSlitInterferenceService.RenderDiffractionSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Young&#39;s Slits", svg);
        Assert.Contains("df-curve", svg);
    }

    [Fact]
    public void SevenSegmentDisplayService_DecodesDigitsAndRendersSvg()
    {
        string segMd = """
            :::7seg "12:34" color="green"
            :::
            """;

        var model = SevenSegmentDisplayService.ParseSevenSegment(segMd);
        Assert.Equal("12:34", model.Text);
        Assert.Equal(5, model.Digits.Count);
        Assert.Equal('1', model.Digits[0].Char);
        Assert.Equal(':', model.Digits[2].Char);

        string svg = SevenSegmentDisplayService.RenderSevenSegmentSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("7-Segment Display", svg);
    }
}
