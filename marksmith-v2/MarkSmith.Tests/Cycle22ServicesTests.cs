using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Audio;
using MarkSmith.Services.Civil;
using MarkSmith.Services.Design;
using MarkSmith.Services.Electronics;
using MarkSmith.Services.Physics;
using MarkSmith.Services.Science;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle22ServicesTests
{
    [Fact]
    public void DopplerShockwave_ParsesSupersonicAndRendersMachCone()
    {
        string md = """
            :::doppler "Supersonic Concorde"
            mach: 2.0
            waves: 10
            :::
            """;

        var model = DopplerShockwaveService.ParseDoppler(md);
        Assert.Equal("Supersonic Concorde", model.Title);
        Assert.Equal(2.0, model.MachNumber, precision: 2);
        Assert.True(model.IsSupersonic);
        Assert.Equal(30.0, model.MachAngleDeg, precision: 1); // arcsin(1/2) = 30 deg

        string svg = DopplerShockwaveService.RenderDopplerSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Supersonic Concorde", svg);
        Assert.Contains("dop-shock", svg);
        Assert.Contains("Mach Cone", svg);
    }

    [Fact]
    public void ColorWheelGamut_ParsesTriadicHarmonyAndRendersNodes()
    {
        string md = """
            :::color-wheel "Studio Gamut"
            hue: 180
            harmony: "triadic"
            :::
            """;

        var model = ColorWheelGamutService.ParseColorWheel(md);
        Assert.Equal("Studio Gamut", model.Title);
        Assert.Equal(180, model.BaseHueDeg, precision: 1);
        Assert.Equal("triadic", model.HarmonyMode);
        Assert.Equal(3, model.Swatches.Count);

        string svg = ColorWheelGamutService.RenderColorWheelSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Studio Gamut", svg);
        Assert.Contains("cw-chord", svg);
        Assert.Contains("cw-node", svg);
    }

    [Fact]
    public void DecibelVuMeter_ParsesStereoLevelsAndRendersSegments()
    {
        string md = """
            :::vumeter "Analog Console Meter"
            left: -4.5dB
            right: 1.2dB
            :::
            """;

        var model = DecibelVuMeterService.ParseVuMeter(md);
        Assert.Equal("Analog Console Meter", model.Title);
        Assert.Equal(-4.5, model.LeftDb, precision: 1);
        Assert.Equal(1.2, model.RightDb, precision: 1);

        string svg = DecibelVuMeterService.RenderVuMeterSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Analog Console Meter", svg);
        Assert.Contains("vu-chan", svg);
        Assert.Contains("vu-db-text", svg);
    }

    [Fact]
    public void ElementCardRenderer_ParsesElementAndRendersBohrShells()
    {
        string md = """
            :::element "Ag"
            name: "Silver"
            atomic: 47
            mass: 107.868
            category: "Transition Metal"
            :::
            """;

        var model = ElementCardRendererService.ParseElement(md);
        Assert.Equal("Ag", model.Symbol);
        Assert.Equal("Silver", model.Name);
        Assert.Equal(47, model.AtomicNumber);
        Assert.Equal(107.868, model.AtomicMass, precision: 3);

        string svg = ElementCardRendererService.RenderElementSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Ag", svg);
        Assert.Contains("Silver", svg);
        Assert.Contains("elem-orbit", svg);
        Assert.Contains("elem-electron", svg);
    }

    [Fact]
    public void WeirDischarge_CalculatesVNotchFlowDischarge()
    {
        string md = """
            :::weir "Triangular Spillway"
            type: "v-notch"
            head: 0.40m
            angle: 90
            :::
            """;

        var model = WeirDischargeService.ParseWeir(md);
        Assert.Equal("Triangular Spillway", model.Title);
        Assert.Equal(0.40, model.HeadMeters, precision: 2);
        Assert.Equal(90.0, model.NotchAngleDeg, precision: 1);
        Assert.True(model.DischargeQ > 0.05 && model.DischargeQ < 0.20);
        Assert.True(model.DischargeLps > 50.0);

        string svg = WeirDischargeService.RenderWeirSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Triangular Spillway", svg);
        Assert.Contains("weir-plate", svg);
        Assert.Contains("weir-res-val", svg);
    }

    [Fact]
    public void TransistorCharacteristic_ParsesBjtAndRendersCurves()
    {
        string md = """
            :::bjt "BC547 NPN"
            type: "NPN"
            beta: 200
            va: 80V
            :::
            """;

        var model = TransistorCharacteristicService.ParseBjt(md);
        Assert.Equal("BC547 NPN", model.Title);
        Assert.Equal("NPN", model.TransistorType);
        Assert.Equal(200.0, model.Beta, precision: 1);
        Assert.Equal(80.0, model.EarlyVoltageVa, precision: 1);

        string svg = TransistorCharacteristicService.RenderBjtSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("BC547 NPN", svg);
        Assert.Contains("bjt-curve", svg);
        Assert.Contains("Saturation", svg);
    }

    [Fact]
    public void MarkdownHtmlService_RendersAllCycle22Blocks()
    {
        string md = """
            # Engineering & Physics Document

            :::doppler "F-16 Flyby"
            mach: 1.2
            :::

            :::color-wheel "UI Palette"
            hue: 240
            :::

            :::vumeter "Mix Bus"
            left: -6.0dB
            right: -6.0dB
            :::

            :::element "Fe"
            name: "Iron"
            atomic: 26
            mass: 55.845
            :::

            :::weir "Canal Gate"
            head: 0.30m
            :::

            :::bjt "2N3904"
            beta: 150
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string html = new MarkdownHtmlService().Render(md, new AppSettings(), theme);

        Assert.Contains("doppler-diagram", html);
        Assert.Contains("color-wheel-diagram", html);
        Assert.Contains("vumeter-diagram", html);
        Assert.Contains("element-card-diagram", html);
        Assert.Contains("weir-diagram", html);
        Assert.Contains("bjt-diagram", html);
    }

    // Regression (#58): the incremental canvas-swap path must lift the engineering fences exactly
    // like the full Render() path, or in-place preview updates flicker raw :::fence text.
    [Fact]
    public void MarkdownHtmlService_RenderCanvasOnly_LiftsEngineeringFences()
    {
        string md = """
            # Canvas Update

            :::doppler "Flyby"
            mach: 1.2
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string? html = new MarkdownHtmlService().RenderCanvasOnly(md, new AppSettings(), theme);

        Assert.NotNull(html);
        Assert.Contains("doppler-diagram", html);
        Assert.DoesNotContain(":::doppler", html);
    }
}
