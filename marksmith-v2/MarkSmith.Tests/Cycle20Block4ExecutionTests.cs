using System;
using MarkSmith.Services.Astronomy;
using MarkSmith.Services.Electronics;
using MarkSmith.Services.Physics;
using MarkSmith.Services.Time;
using MarkSmith.Services.Typography;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle20Block4ExecutionTests
{
    [Fact]
    public void BrailleMatrixRendererService_ParsesAndRendersSvgBraille()
    {
        string brailleMd = """
            :::braille "CODE"
            :::
            """;

        var model = BrailleMatrixRendererService.ParseBraille(brailleMd);
        Assert.Equal("CODE", model.Text);
        Assert.Equal(4, model.Cells.Count);
        Assert.Equal('C', model.Cells[0].Character);
        Assert.True(model.Cells[0].Dots[0]); // C has dot 1
        Assert.True(model.Cells[0].Dots[3]); // C has dot 4

        string svg = BrailleMatrixRendererService.RenderBrailleSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Braille: CODE", svg);
        Assert.Contains("br-dot-active", svg);
    }

    [Fact]
    public void ResistorColorCodeService_DecodesResistanceAndRendersSvg()
    {
        string resistorMd = """
            :::resistor "4.7k 5%"
            :::
            """;

        var model = ResistorColorCodeService.ParseResistor(resistorMd);
        Assert.Equal(4700, model.ResistanceOhms);
        Assert.Equal(5, model.TolerancePercent);
        Assert.Equal(4, model.Bands.Count);
        Assert.Equal("Yellow", model.Bands[0].ColorName); // 4
        Assert.Equal("Violet", model.Bands[1].ColorName); // 7
        Assert.Equal("Red", model.Bands[2].ColorName);    // x100
        Assert.Equal("Gold", model.Bands[3].ColorName);   // 5%

        string svg = ResistorColorCodeService.RenderResistorSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Resistor: 4700", svg);
        Assert.Contains("rs-lead", svg);
        Assert.Contains("rs-body", svg);
    }

    [Fact]
    public void PlanetaryOrbitRendererService_ParsesKeplerianOrbitsAndRendersSvg()
    {
        string orbitMd = """
            :::orbit "Terrestrial Orbits"
            body "Mercury" a=0.387 e=0.2056 [color: #94a3b8]
            body "Earth" a=1.000 e=0.0167 [color: #38bdf8]
            :::
            """;

        var model = PlanetaryOrbitRendererService.ParseOrbit(orbitMd);
        Assert.Equal("Terrestrial Orbits", model.Title);
        Assert.Equal(2, model.Bodies.Count);
        Assert.Equal("Mercury", model.Bodies[0].Name);
        Assert.Equal(0.387, model.Bodies[0].SemiMajorAxisAU);

        string svg = PlanetaryOrbitRendererService.RenderOrbitSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Terrestrial Orbits", svg);
        Assert.Contains("ob-sun", svg);
        Assert.Contains("ob-ellipse", svg);
    }

    [Fact]
    public void RomanNumeralClockRendererService_ParsesTimestampAndRendersSvgClock()
    {
        string clockMd = """
            :::clock "Tower Clock"
            time = "10:15"
            :::
            """;

        var model = RomanNumeralClockRendererService.ParseClock(clockMd);
        Assert.Equal("Tower Clock", model.Title);
        Assert.Equal(10, model.Hours);
        Assert.Equal(15, model.Minutes);

        string svg = RomanNumeralClockRendererService.RenderClockSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Tower Clock (10:15)", svg);
        Assert.Contains("XII", svg);
        Assert.Contains("ck-hour", svg);
        Assert.Contains("ck-min", svg);
    }

    [Fact]
    public void LissajousCurveRendererService_CalculatesPhaseAndRendersSvg()
    {
        string ljMd = """
            :::lissajous "Harmonic 3:2"
            fx = 3.0
            fy = 2.0
            delta = 0.5
            :::
            """;

        var model = LissajousCurveRendererService.ParseLissajous(ljMd);
        Assert.Equal("Harmonic 3:2", model.Title);
        Assert.Equal(3.0, model.FreqX);
        Assert.Equal(2.0, model.FreqY);
        Assert.Equal(0.5, model.PhaseDeltaPi);

        string svg = LissajousCurveRendererService.RenderLissajousSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Harmonic 3:2", svg);
        Assert.Contains("lj-curve", svg);
    }

    [Fact]
    public void KarnaughMapRendererService_ParsesTruthTableAndRendersSvgMap()
    {
        string kmapMd = """
            :::kmap "Half Adder Carry"
            vars = 2
            values: 0 0 0 1
            :::
            """;

        var model = KarnaughMapRendererService.ParseKmap(kmapMd);
        Assert.Equal("Half Adder Carry", model.Title);
        Assert.Equal(2, model.Variables);
        Assert.Equal(0, model.Matrix[0, 0]);
        Assert.Equal(1, model.Matrix[1, 1]);

        string svg = KarnaughMapRendererService.RenderKmapSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Half Adder Carry", svg);
        Assert.Contains("km-cell", svg);
        Assert.Contains("km-val-1", svg);
    }
}
