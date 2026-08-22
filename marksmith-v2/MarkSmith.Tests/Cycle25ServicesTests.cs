using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Civil;
using MarkSmith.Services.Electronics;
using MarkSmith.Services.Physics;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle25ServicesTests
{
    [Fact]
    public void TensileCurve_CalculatesYieldProofAndToughness()
    {
        string md = """
            :::tensile-test "Structural Steel A36"
            E: 200GPa
            yield: 250MPa
            uts: 400MPa
            frac: 25%
            :::
            """;

        var model = TensileCurveService.ParseTensile(md);
        Assert.Equal("Structural Steel A36", model.Title);
        Assert.Equal(200.0, model.ModulusGpa, precision: 1);
        Assert.Equal(250.0, model.YieldMpa, precision: 1);
        Assert.Equal(400.0, model.UtsMpa, precision: 1);
        Assert.Equal(0.25, model.FractureStrain, precision: 2);

        Assert.True(model.YieldStrain > 0.001 && model.YieldStrain < 0.002);
        Assert.True(model.ToughnessModulusMjM3 > 50.0);

        string svg = TensileCurveService.RenderTensileSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Structural Steel A36", svg);
        Assert.Contains("ts-curve", svg);
        Assert.Contains("ts-tough-fill", svg);
    }

    [Fact]
    public void DigitalTimingDiagram_ParsesSignalsAndGeneratesWaves()
    {
        string md = """
            :::timing-diagram "SPI Master Transfer"
            CLK: P...P...P...P
            CS_N: 1...0...0...1
            MOSI: 0...1...1...0
            MISO: Z...Z...D...Z
            :::
            """;

        var model = DigitalTimingDiagramService.ParseTiming(md);
        Assert.Equal("SPI Master Transfer", model.Title);
        Assert.Equal(4, model.Signals.Count);
        Assert.Equal("CLK", model.Signals[0].Name);
        Assert.Equal("MISO", model.Signals[3].Name);

        string svg = DigitalTimingDiagramService.RenderTimingSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("SPI Master Transfer", svg);
        Assert.Contains("tm-wave", svg);
        Assert.Contains("tm-bus", svg);
        Assert.Contains("tm-hiz", svg);
    }

    [Fact]
    public void RlcResonance_CalculatesResonantFrequencyAndBandwidth()
    {
        string md = """
            :::rlc "Series Tank"
            r: 20ohm
            l: 2mH
            c: 50nF
            :::
            """;

        var model = RlcResonanceService.ParseRlc(md);
        Assert.Equal("Series Tank", model.Title);
        Assert.Equal(20.0, model.ResistanceOhms, precision: 1);
        Assert.Equal(0.002, model.InductanceHenry, precision: 6);
        Assert.Equal(5e-8, model.CapacitanceFarads, precision: 10);

        // f0 = 1 / (2 * pi * sqrt(2e-3 * 50e-9)) = 1 / (2 * pi * 1e-5) approx 15915 Hz
        Assert.True(model.ResonantFreqHz > 15000 && model.ResonantFreqHz < 17000);
        Assert.True(model.QualityFactor > 5.0);
        Assert.True(model.BandwidthHz > 100.0);

        string svg = RlcResonanceService.RenderRlcSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Series Tank", svg);
        Assert.Contains("rc-curve", svg);
        Assert.Contains("rc-f0-line", svg);
    }

    [Fact]
    public void RelativisticMinkowski_CalculatesLorentzFactorAndTimeDilation()
    {
        string md = """
            :::relativistic "Atmospheric Muon"
            beta: 0.866
            proper_time: 2.2us
            proper_length: 100m
            :::
            """;

        var model = RelativisticMinkowskiService.ParseRelativistic(md);
        Assert.Equal("Atmospheric Muon", model.Title);
        Assert.Equal(0.866, model.BetaVelocity, precision: 3);
        Assert.Equal(2.2, model.ProperTimeUs, precision: 1);
        Assert.Equal(100.0, model.ProperLengthM, precision: 1);

        // gamma = 1 / sqrt(1 - 0.866^2) = 1 / sqrt(1 - 0.75) = 1 / 0.5 = 2.0
        Assert.Equal(2.0, model.Gamma, precision: 1);
        Assert.Equal(4.4, model.DilatedTimeUs, precision: 1);
        Assert.Equal(50.0, model.ContractedLengthM, precision: 1);

        string svg = RelativisticMinkowskiService.RenderRelativisticSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Atmospheric Muon", svg);
        Assert.Contains("rv-lightcone", svg);
        Assert.Contains("rv-boost-ct", svg);
    }

    [Fact]
    public void MohrCoulombSoil_CalculatesEffectiveShearStrength()
    {
        string md = """
            :::mohr-coulomb "Embankment Clay"
            c: 30kPa
            phi: 28deg
            sigma: 150kPa
            u: 20kPa
            :::
            """;

        var model = MohrCoulombSoilService.ParseSoil(md);
        Assert.Equal("Embankment Clay", model.Title);
        Assert.Equal(30.0, model.CohesionKPa, precision: 1);
        Assert.Equal(28.0, model.FrictionAngleDeg, precision: 1);
        Assert.Equal(150.0, model.NormalStressKPa, precision: 1);
        Assert.Equal(20.0, model.PoreWaterPressureKPa, precision: 1);

        // sigma' = 150 - 20 = 130 kPa
        Assert.Equal(130.0, model.EffectiveStressKPa, precision: 1);
        // tau_f = 30 + 130 * tan(28 deg) approx 30 + 130 * 0.5317 = 99.1 kPa
        Assert.True(model.ShearStrengthTauF > 90.0 && model.ShearStrengthTauF < 110.0);

        string svg = MohrCoulombSoilService.RenderSoilSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Embankment Clay", svg);
        Assert.Contains("mc-envelope", svg);
        Assert.Contains("mc-circle", svg);
    }

    [Fact]
    public void PllTransient_EvaluatesLockStepResponse()
    {
        string md = """
            :::pll "Cellular RF LO"
            f_ref: 20MHz
            n: 120
            zeta: 0.707
            fn: 500kHz
            :::
            """;

        var model = PllTransientService.ParsePll(md);
        Assert.Equal("Cellular RF LO", model.Title);
        Assert.Equal(20.0, model.RefFreqMhz, precision: 1);
        Assert.Equal(120, model.DividerN);
        Assert.Equal(0.707, model.DampingZeta, precision: 3);
        Assert.Equal(500.0, model.NaturalFreqKhz, precision: 1);

        // f_vco = 20 * 120 / 1000 = 2.4 GHz
        Assert.Equal(2.4, model.TargetVcoFreqGhz, precision: 2);
        Assert.True(model.LockTimeUs > 0.5 && model.LockTimeUs < 10.0);

        // Normalized step response at t=0 should be 0, at t=1.0 should be near 1.0
        double v0 = model.EvaluateStepResponse(0.0);
        double vFinal = model.EvaluateStepResponse(1.0);
        Assert.Equal(0.0, v0, precision: 1);
        Assert.True(vFinal > 0.95 && vFinal < 1.05);

        string svg = PllTransientService.RenderPllSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Cellular RF LO", svg);
        Assert.Contains("pl-curve", svg);
        Assert.Contains("pl-lock-target", svg);
    }

    [Fact]
    public void MarkdownHtmlService_RendersAllCycle25Blocks()
    {
        string md = """
            # Multidisciplinary Science & Engineering Document

            :::tensile-test "Mild Steel"
            yield: 250MPa
            :::

            :::timing-diagram "I2C Transaction"
            SCL: P...P...P...P
            SDA: 1...0...D...1
            :::

            :::rlc "Tuned Circuit"
            r: 10ohm
            :::

            :::relativistic "Fast Particle"
            beta: 0.90
            :::

            :::mohr-coulomb "Soil Foundation"
            c: 20kPa
            :::

            :::pll "Synthesizer"
            f_ref: 10MHz
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string html = new MarkdownHtmlService().Render(md, new AppSettings(), theme);

        Assert.Contains("tensile-curve-diagram", html);
        Assert.Contains("timing-diagram", html);
        Assert.Contains("rlc-resonance-diagram", html);
        Assert.Contains("relativistic-diagram", html);
        Assert.Contains("mohr-coulomb-diagram", html);
        Assert.Contains("pll-transient-diagram", html);
    }
}
