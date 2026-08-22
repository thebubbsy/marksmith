using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Astronomy;
using MarkSmith.Services.Civil;
using MarkSmith.Services.Electronics;
using MarkSmith.Services.Physics;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle23ServicesTests
{
    [Fact]
    public void SmithChart_ParsesComplexImpedanceAndCalculatesGamma()
    {
        string md = """
            :::smith-chart "Patch Antenna"
            z: 1.0 + 0.5j
            z0: 50
            :::
            """;

        var model = SmithChartRfService.ParseSmithChart(md);
        Assert.Equal("Patch Antenna", model.Title);
        Assert.Equal(1.0, model.NormalizedR, precision: 2);
        Assert.Equal(0.5, model.NormalizedX, precision: 2);
        Assert.Equal(50.0, model.CharacteristicZ0, precision: 1);

        // |Gamma| should be approx 0.2425, VSWR approx 1.64
        Assert.True(model.GammaMag > 0.20 && model.GammaMag < 0.30);
        Assert.True(model.Vswr > 1.5 && model.Vswr < 1.8);

        string svg = SmithChartRfService.RenderSmithChartSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Patch Antenna", svg);
        Assert.Contains("sm-r-circle", svg);
        Assert.Contains("sm-point", svg);
    }

    [Fact]
    public void MohrsCircle_ParsesPlanarStressAndComputesPrincipalStresses()
    {
        string md = """
            :::mohrs-circle "Shaft Torsion & Tension"
            sx: 100
            sy: 20
            txy: 30
            :::
            """;

        var model = MohrsCircleService.ParseMohrsCircle(md);
        Assert.Equal("Shaft Torsion & Tension", model.Title);
        Assert.Equal(100.0, model.SigmaX, precision: 1);
        Assert.Equal(20.0, model.SigmaY, precision: 1);
        Assert.Equal(30.0, model.TauXY, precision: 1);

        // Center = 60, Radius = sqrt(40^2 + 30^2) = 50 -> Sigma1 = 110, Sigma2 = 10, TauMax = 50
        Assert.Equal(60.0, model.CenterSigma, precision: 1);
        Assert.Equal(50.0, model.RadiusR, precision: 1);
        Assert.Equal(110.0, model.PrincipalSigma1, precision: 1);
        Assert.Equal(10.0, model.PrincipalSigma2, precision: 1);
        Assert.Equal(50.0, model.MaxShearTau, precision: 1);

        string svg = MohrsCircleService.RenderMohrsCircleSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Shaft Torsion &amp; Tension", svg);
        Assert.Contains("mo-circle", svg);
        Assert.Contains("mo-diam", svg);
    }

    [Fact]
    public void KeplerOrbit_ParsesEllipticalParametersAndCalculatesApsides()
    {
        string md = """
            :::orbit "Halley Comet"
            a: 17.8AU
            e: 0.967
            nu: 30deg
            :::
            """;

        var model = KeplerOrbitVisualizerService.ParseOrbit(md);
        Assert.Equal("Halley Comet", model.Title);
        Assert.Equal(17.8, model.SemiMajorAxisAu, precision: 2);
        Assert.Equal(0.967, model.Eccentricity, precision: 3);
        Assert.Equal(30.0, model.TrueAnomalyDeg, precision: 1);

        // Periapsis = a*(1-e) approx 0.587 AU, Period = a^1.5 approx 75.1 yrs
        Assert.True(model.PeriapsisAu > 0.5 && model.PeriapsisAu < 0.7);
        Assert.True(model.OrbitalPeriodYears > 70.0 && model.OrbitalPeriodYears < 80.0);

        string svg = KeplerOrbitVisualizerService.RenderOrbitSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Halley Comet", svg);
        Assert.Contains("orb-ellipse", svg);
        Assert.Contains("orb-star", svg);
        Assert.Contains("orb-sector", svg);
    }

    [Fact]
    public void PrismDispersion_ParsesApexAngleAndCalculatesCauchyDeviations()
    {
        string md = """
            :::prism "Crown Glass Spectrometer"
            apex: 60deg
            incident: 50deg
            :::
            """;

        var model = PrismDispersionService.ParsePrism(md);
        Assert.Equal("Crown Glass Spectrometer", model.Title);
        Assert.Equal(60.0, model.ApexAngleDeg, precision: 1);
        Assert.Equal(50.0, model.IncidentAngleDeg, precision: 1);
        Assert.Equal(6, model.Rays.Count);

        // Violet wavelength should experience higher deviation than Red wavelength
        var red = model.Rays[0];
        var violet = model.Rays[^1];
        Assert.True(violet.RefractiveIndex > red.RefractiveIndex);
        Assert.True(violet.DeviationDeg > red.DeviationDeg);

        string svg = PrismDispersionService.RenderPrismSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Crown Glass Spectrometer", svg);
        Assert.Contains("pr-glass", svg);
        Assert.Contains("White Light", svg);
    }

    [Fact]
    public void OpAmpFilterBode_CalculatesLowpassBodeMagnitude()
    {
        string md = """
            :::filter "Butterworth LPF"
            type: "lowpass"
            cutoff: 10000Hz
            q: 0.707
            :::
            """;

        var model = OpAmpFilterBodeService.ParseFilter(md);
        Assert.Equal("Butterworth LPF", model.Title);
        Assert.Equal("lowpass", model.FilterType);
        Assert.Equal(10000.0, model.CutoffFreqHz, precision: 1);
        Assert.Equal(0.707, model.QualityFactorQ, precision: 3);

        // Low frequency (< 100Hz) should be ~0 dB; at cutoff (10kHz) should be ~ -3 dB; high frequency (100kHz) should roll off (~ -40 dB)
        double magLow = model.GetMagnitudeDb(100.0);
        double magCut = model.GetMagnitudeDb(10000.0);
        double magHigh = model.GetMagnitudeDb(100000.0);

        Assert.True(magLow > -0.5 && magLow <= 0.0);
        Assert.True(magCut > -3.5 && magCut < -2.8);
        Assert.True(magHigh < -30.0);

        string svg = OpAmpFilterBodeService.RenderFilterBodeSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Butterworth LPF", svg);
        Assert.Contains("bo-curve", svg);
        Assert.Contains("fc (-3dB)", svg);
    }

    [Fact]
    public void VenturiFlow_CalculatesBernoulliDischargeFlowRate()
    {
        string md = """
            :::venturi "Municipal Main Venturi"
            d1: 150mm
            d2: 75mm
            dh: 250mm
            :::
            """;

        var model = VenturiFlowService.ParseVenturi(md);
        Assert.Equal("Municipal Main Venturi", model.Title);
        Assert.Equal(150.0, model.InletDiameterMm, precision: 1);
        Assert.Equal(75.0, model.ThroatDiameterMm, precision: 1);
        Assert.Equal(250.0, model.ManometerDhMm, precision: 1);

        // Differential pressure approx 30.9 kPa, Discharge flow approx 30-40 L/s
        Assert.True(model.DeltaPressurePa > 25000.0);
        Assert.True(model.DischargeLps > 25.0 && model.DischargeLps < 50.0);

        string svg = VenturiFlowService.RenderVenturiSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Municipal Main Venturi", svg);
        Assert.Contains("vn-pipe", svg);
        Assert.Contains("vn-mano-tube", svg);
    }

    [Fact]
    public void MarkdownHtmlService_RendersAllCycle23Blocks()
    {
        string md = """
            # Advanced Engineering Multi-Discipline Document

            :::smith-chart "Feedline"
            z: 0.8+0.4j
            :::

            :::mohrs-circle "Cantilever Beam"
            sx: 60
            sy: -20
            txy: 40
            :::

            :::orbit "Earth"
            a: 1.0AU
            e: 0.0167
            :::

            :::prism "Flint Glass"
            apex: 60deg
            :::

            :::filter "Audio Sallen-Key"
            cutoff: 2000Hz
            :::

            :::venturi "Laboratory Venturi"
            d1: 80mm
            d2: 40mm
            dh: 120mm
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string html = new MarkdownHtmlService().Render(md, new AppSettings(), theme);

        Assert.Contains("smith-chart-diagram", html);
        Assert.Contains("mohrs-circle-diagram", html);
        Assert.Contains("kepler-orbit-diagram", html);
        Assert.Contains("prism-dispersion-diagram", html);
        Assert.Contains("filter-bode-diagram", html);
        Assert.Contains("venturi-diagram", html);
    }
}
