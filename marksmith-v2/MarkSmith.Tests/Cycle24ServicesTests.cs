using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Audio;
using MarkSmith.Services.Civil;
using MarkSmith.Services.Electronics;
using MarkSmith.Services.MathCore;
using MarkSmith.Services.Physics;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle24ServicesTests
{
    [Fact]
    public void EulerGimbal_ParsesAnglesAndComputesQuaternion()
    {
        string md = """
            :::euler-gimbal "Drone Attitude"
            roll: 20deg
            pitch: 10deg
            yaw: -30deg
            :::
            """;

        var model = EulerGimbalService.ParseGimbal(md);
        Assert.Equal("Drone Attitude", model.Title);
        Assert.Equal(20.0, model.RollDeg, precision: 1);
        Assert.Equal(10.0, model.PitchDeg, precision: 1);
        Assert.Equal(-30.0, model.YawDeg, precision: 1);
        Assert.False(model.IsGimbalLock);

        var q = model.Quaternion;
        // Normalized quaternion magnitude should be 1.0
        double qNorm = Math.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        Assert.Equal(1.0, qNorm, precision: 3);

        string svg = EulerGimbalService.RenderGimbalSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Drone Attitude", svg);
        Assert.Contains("gm-ring-yaw", svg);
        Assert.Contains("gm-axis-x", svg);
    }

    [Fact]
    public void PoiseuilleFlow_CalculatesLaminarFlowProfileAndDischarge()
    {
        string md = """
            :::poiseuille "Lubrication Line"
            r: 40mm
            L: 8m
            mu: 0.05Pa.s
            dp: 4000Pa
            :::
            """;

        var model = PoiseuilleFlowService.ParsePoiseuille(md);
        Assert.Equal("Lubrication Line", model.Title);
        Assert.Equal(40.0, model.PipeRadiusMm, precision: 1);
        Assert.Equal(8.0, model.PipeLengthMeters, precision: 1);
        Assert.Equal(0.05, model.ViscosityPaS, precision: 3);
        Assert.Equal(4000.0, model.PressureDropPa, precision: 1);

        // u_max = (4000 * 0.04^2) / (4 * 0.05 * 8) = 6.4 / 1.6 = 4.0 m/s
        Assert.Equal(4.0, model.MaxVelocityMps, precision: 2);
        Assert.Equal(2.0, model.AvgVelocityMps, precision: 2);
        Assert.True(model.DischargeLps > 5.0 && model.DischargeLps < 15.0);

        string svg = PoiseuilleFlowService.RenderPoiseuilleSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Lubrication Line", svg);
        Assert.Contains("ps-pipe-wall", svg);
        Assert.Contains("ps-profile", svg);
    }

    [Fact]
    public void FourierHarmonic_EvaluatesPartialSumSquareWave()
    {
        string md = """
            :::fourier "Audio Synthesizer Square"
            type: "square"
            harmonics: 9
            freq: 440Hz
            :::
            """;

        var model = FourierHarmonicSynthesizerService.ParseFourier(md);
        Assert.Equal("Audio Synthesizer Square", model.Title);
        Assert.Equal("square", model.WaveformType);
        Assert.Equal(9, model.HarmonicsCount);
        Assert.Equal(440.0, model.FrequencyHz, precision: 1);

        // Mid-point of positive half cycle (t = 0.25) should be close to 1.0 (approx 1.05 with Gibbs)
        double valPositive = model.EvaluatePartialSum(0.25);
        double valNegative = model.EvaluatePartialSum(0.75);

        Assert.True(valPositive > 0.8 && valPositive < 1.3);
        Assert.True(valNegative < -0.8 && valNegative > -1.3);

        string svg = FourierHarmonicSynthesizerService.RenderFourierSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Audio Synthesizer Square", svg);
        Assert.Contains("fo-wave", svg);
        Assert.Contains("fo-spec-bar", svg);
    }

    [Fact]
    public void CarnotCycle_CalculatesThermalEfficiency()
    {
        string md = """
            :::carnot "Geothermal Heat Engine"
            th: 500K
            tc: 250K
            cr: 5.0
            :::
            """;

        var model = CarnotCycleService.ParseCarnot(md);
        Assert.Equal("Geothermal Heat Engine", model.Title);
        Assert.Equal(500.0, model.TempHotKelvin, precision: 1);
        Assert.Equal(250.0, model.TempColdKelvin, precision: 1);

        // Efficiency = 1 - 250/500 = 0.50 (50.0%)
        Assert.Equal(0.50, model.Efficiency, precision: 2);
        Assert.Equal(50.0, model.EfficiencyPercent, precision: 1);

        string svg = CarnotCycleService.RenderCarnotSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Geothermal Heat Engine", svg);
        Assert.Contains("cn-work", svg);
        Assert.Contains("50.0 %", svg);
    }

    [Fact]
    public void HydraulicJump_CalculatesConjugateDepthAndHeadLoss()
    {
        string md = """
            :::hydraulic-jump "Dam Spillway Apron"
            y1: 0.50m
            v1: 7.0m/s
            width: 4.0m
            :::
            """;

        var model = HydraulicJumpService.ParseJump(md);
        Assert.Equal("Dam Spillway Apron", model.Title);
        Assert.Equal(0.50, model.DepthY1Meters, precision: 2);
        Assert.Equal(7.0, model.VelocityV1Mps, precision: 1);
        Assert.True(model.IsSupercritical);

        // Fr1 = 7.0 / sqrt(9.81 * 0.5) approx 3.16 > 1.0
        Assert.True(model.FroudeNumberFr1 > 3.0 && model.FroudeNumberFr1 < 3.5);
        Assert.True(model.SubcriticalDepthY2 > 1.5 && model.SubcriticalDepthY2 < 2.5);
        Assert.True(model.EnergyLossHeadMeters > 0.5);

        string svg = HydraulicJumpService.RenderJumpSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Dam Spillway Apron", svg);
        Assert.Contains("hj-super", svg);
        Assert.Contains("hj-roller", svg);
    }

    [Fact]
    public void MosfetCharacteristic_ParsesKnAndRendersFamilyCurves()
    {
        string md = """
            :::mosfet "Power NMOS"
            vth: 2.5V
            kn: 40
            lambda: 0.02
            :::
            """;

        var model = MosfetCharacteristicService.ParseMosfet(md);
        Assert.Equal("Power NMOS", model.Title);
        Assert.Equal(2.5, model.ThresholdVoltageVth, precision: 1);
        Assert.Equal(40.0, model.TransconductanceKn, precision: 1);
        Assert.Equal(0.02, model.LambdaModulation, precision: 3);

        string svg = MosfetCharacteristicService.RenderMosfetSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Power NMOS", svg);
        Assert.Contains("mf-curve", svg);
        Assert.Contains("mf-sat-bnd", svg);
    }

    [Fact]
    public void MarkdownHtmlService_RendersAllCycle24Blocks()
    {
        string md = """
            # Advanced Mechanical & Electronic Multidiscipline Document

            :::euler-gimbal "Satellite"
            roll: 10deg
            pitch: 20deg
            :::

            :::poiseuille "Hydraulic Fluid"
            r: 30mm
            :::

            :::fourier "Synth Wave"
            type: "sawtooth"
            :::

            :::carnot "Power Plant"
            th: 800K
            tc: 300K
            :::

            :::hydraulic-jump "Flume"
            y1: 0.3m
            v1: 5.0m/s
            :::

            :::mosfet "MOS Switch"
            vth: 1.8V
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string html = new MarkdownHtmlService().Render(md, new AppSettings(), theme);

        Assert.Contains("euler-gimbal-diagram", html);
        Assert.Contains("poiseuille-diagram", html);
        Assert.Contains("fourier-diagram", html);
        Assert.Contains("carnot-diagram", html);
        Assert.Contains("hydraulic-jump-diagram", html);
        Assert.Contains("mosfet-diagram", html);
    }
}
