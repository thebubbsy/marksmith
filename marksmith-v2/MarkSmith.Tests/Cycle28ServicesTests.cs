using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Civil;
using MarkSmith.Services.Electronics;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle28ServicesTests
{
    [Fact]
    public void RetainingWall_CalculatesKaAndThrust()
    {
        string md = """
            :::retaining-wall "Cantilever Concrete Wall"
            height: 6.0m
            gamma: 18kN/m3
            phi: 30deg
            surcharge: 12kPa
            cohesion: 0kPa
            :::
            """;

        var model = RetainingWallPressureService.ParseRetainingWall(md);
        Assert.Equal("Cantilever Concrete Wall", model.Title);
        Assert.Equal(6.0, model.HeightM, precision: 1);
        Assert.Equal(18.0, model.UnitWeightGamma, precision: 1);
        Assert.Equal(30.0, model.FrictionAnglePhiDeg, precision: 1);
        Assert.Equal(12.0, model.SurchargeQ, precision: 1);

        // Ka for phi=30 is (1-0.5)/(1+0.5) = 0.3333, Kp = 3.0
        Assert.Equal(1.0 / 3.0, model.Ka, precision: 3);
        Assert.Equal(3.0, model.Kp, precision: 2);

        // ForceSurcharge = Ka * q * H = (1/3) * 12 * 6 = 24 kN/m
        Assert.Equal(24.0, model.ForceSurcharge, precision: 1);

        // ForceSoil = 0.5 * Ka * gamma * H^2 = 0.5 * (1/3) * 18 * 36 = 108 kN/m
        Assert.Equal(108.0, model.ForceSoil, precision: 1);

        // Total thrust Pa = 132 kN/m
        Assert.Equal(132.0, model.TotalActiveThrustPa, precision: 1);

        // Overturning moment = 24 * 3 + 108 * 2 = 72 + 216 = 288 kNm/m
        Assert.Equal(288.0, model.OverturningMoment, precision: 1);

        string svg = RetainingWallPressureService.RenderRetainingWallSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Cantilever Concrete Wall", svg);
        Assert.Contains("rw-concrete", svg);
        Assert.Contains("rw-pressure", svg);
    }

    [Fact]
    public void SuperhetReceiver_CalculatesLoAndImageRejection()
    {
        string md = """
            :::superhet-receiver "FM Receiver"
            f_rf: 100MHz
            f_if: 10.7MHz
            lo_side: "high"
            q_filter: 50
            :::
            """;

        var model = SuperhetReceiverService.ParseSuperhet(md);
        Assert.Equal("FM Receiver", model.Title);
        Assert.Equal(100.0, model.RfFrequencyMhz, precision: 1);
        Assert.Equal(10.7, model.IfFrequencyMhz, precision: 1);
        Assert.Equal("high", model.LoSide);
        Assert.Equal(50.0, model.PreselectorQ, precision: 1);

        // f_lo = 100 + 10.7 = 110.7 MHz
        Assert.Equal(110.7, model.LoFrequencyMhz, precision: 1);

        // f_img = 100 + 21.4 = 121.4 MHz
        Assert.Equal(121.4, model.ImageFrequencyMhz, precision: 1);

        // IRR should be high (> 20 dB)
        Assert.True(model.ImageRejectionRatioDb > 20.0);

        string svg = SuperhetReceiverService.RenderSuperhetSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("FM Receiver", svg);
        Assert.Contains("sh-filter", svg);
        Assert.Contains("sh-rf-bar", svg);
        Assert.Contains("sh-lo-bar", svg);
    }

    [Fact]
    public void PrestressedBeam_CalculatesStressesAndSectionModulus()
    {
        string md = """
            :::prestressed-beam "Bridge Girder"
            span: 20m
            depth: 1.2m
            width: 0.6m
            p_jack: 3000kN
            e_mid: 0.35m
            load: 30kN/m
            :::
            """;

        var model = PrestressedBeamService.ParsePrestressed(md);
        Assert.Equal("Bridge Girder", model.Title);
        Assert.Equal(20.0, model.SpanM, precision: 1);
        Assert.Equal(1.2, model.DepthM, precision: 1);
        Assert.Equal(0.6, model.WidthM, precision: 1);
        Assert.Equal(3000.0, model.PrestressForceKn, precision: 1);
        Assert.Equal(0.35, model.TendonEccentricityM, precision: 2);
        Assert.Equal(30.0, model.AppliedLoadKnPerM, precision: 1);

        // Area = 0.72 m2, Z = (0.6 * 1.44) / 6 = 0.144 m3
        Assert.Equal(0.72, model.AreaM2, precision: 2);
        Assert.Equal(0.144, model.SectionModulusM3, precision: 3);

        // Midspan moment = 30 * 400 / 8 = 1500 kNm
        Assert.Equal(1500.0, model.MidspanMomentKnM, precision: 1);

        // Direct prestress = -3000 / 0.72 / 1000 = -4.167 MPa
        Assert.True(model.DirectPrestressMpa < -4.0 && model.DirectPrestressMpa > -4.5);

        string svg = PrestressedBeamService.RenderPrestressedSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Bridge Girder", svg);
        Assert.Contains("bm-tendon", svg);
        Assert.Contains("bm-concrete", svg);
    }

    [Fact]
    public void DeltaSigmaAdc_CalculatesEnobAndNoiseShaping()
    {
        string md = """
            :::delta-sigma "Studio Audio Converter"
            f_in: 1.0kHz
            f_s: 48kHz
            osr: 128
            bits: 1
            :::
            """;

        var model = DeltaSigmaAdcService.ParseDeltaSigma(md);
        Assert.Equal("Studio Audio Converter", model.Title);
        Assert.Equal(1.0, model.InputFreqKhz, precision: 1);
        Assert.Equal(48.0, model.NyquistRateKhz, precision: 1);
        Assert.Equal(128, model.OversamplingRatio);
        Assert.Equal(1, model.QuantizerBits);

        // Base SNR = 6.02 * 1 + 1.76 = 7.78 dB
        Assert.Equal(7.78, model.BaseSnrDb, precision: 2);

        // Sampling clock = 48 * 128 = 6144 kHz
        Assert.Equal(6144.0, model.SamplingClockKhz, precision: 1);

        // ENOB should be >= 10 bits with 128x OSR
        Assert.True(model.Enob > 10.0);
        Assert.True(model.TotalInBandSnrDb > 65.0);

        string svg = DeltaSigmaAdcService.RenderDeltaSigmaSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Studio Audio Converter", svg);
        Assert.Contains("ds-shaped-noise", svg);
        Assert.Contains("ds-signal", svg);
    }

    [Fact]
    public void ConcreteSection_CalculatesWhitneyBlockAndUltimateMoment()
    {
        string md = """
            :::concrete-section "RC Girder"
            width: 350mm
            depth: 650mm
            d_eff: 580mm
            fc: 35MPa
            fy: 500MPa
            rebar_area: 2400mm2
            :::
            """;

        var model = ConcreteSectionService.ParseConcreteSection(md);
        Assert.Equal("RC Girder", model.Title);
        Assert.Equal(350.0, model.WidthMm, precision: 1);
        Assert.Equal(650.0, model.DepthMm, precision: 1);
        Assert.Equal(580.0, model.EffectiveDepthMm, precision: 1);
        Assert.Equal(35.0, model.ConcreteFcMpa, precision: 1);
        Assert.Equal(500.0, model.SteelFyMpa, precision: 1);
        Assert.Equal(2400.0, model.SteelAreaMm2, precision: 1);

        // Whitney block a = (2400 * 500) / (0.85 * 35 * 350) = 1200000 / 10412.5 = 115.2 mm
        Assert.True(model.BlockDepthA > 110.0 && model.BlockDepthA < 120.0);

        // Mn = 2400 * 500 * (580 - 115.2/2) * 1e-6 = 1200 * 522.4 * 1e-3 = 626.8 kNm
        Assert.True(model.NominalMomentKnM > 580.0 && model.NominalMomentKnM < 660.0);
        Assert.True(model.UltimateMomentKnM > 500.0);

        string svg = ConcreteSectionService.RenderConcreteSectionSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("RC Girder", svg);
        Assert.Contains("rc-concrete", svg);
        Assert.Contains("rc-whitney", svg);
    }

    [Fact]
    public void RfCascade_CalculatesFriisNoiseFigureAndIip3()
    {
        string md = """
            :::rf-cascade "Cellular Front-End"
            lna: "G=20dB, NF=1.2dB, IIP3=+8dBm"
            filter: "G=-1.5dB, NF=1.5dB, IIP3=+60dBm"
            mixer: "G=10dB, NF=8dB, IIP3=+15dBm"
            :::
            """;

        var model = RfCascadedFriisService.ParseCascade(md);
        Assert.Equal("Cellular Front-End", model.Title);
        Assert.Equal(20.0, model.Stage1.GainDb, precision: 1);
        Assert.Equal(1.2, model.Stage1.NoiseFigureDb, precision: 1);
        Assert.Equal(8.0, model.Stage1.Iip3Dbm, precision: 1);

        // Total Gain = 20 - 1.5 + 10 = 28.5 dB
        Assert.Equal(28.5, model.TotalGainDb, precision: 1);

        // Total NF dominated by LNA NF=1.2dB (overall NF ~ 1.3-1.6 dB)
        Assert.True(model.TotalNoiseFigureDb >= 1.2 && model.TotalNoiseFigureDb < 2.0);

        string svg = RfCascadedFriisService.RenderCascadeSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Cellular Front-End", svg);
        Assert.Contains("cs-stage-lna", svg);
        Assert.Contains("cs-stage-box", svg);
    }

    [Fact]
    public void MarkdownHtmlService_RendersAllCycle28Blocks()
    {
        string md = """
            # Multi-Domain Advanced Industrial Design

            :::retaining-wall "Wall Section"
            height: 5m
            :::

            :::superhet-receiver "Tuner"
            f_rf: 98MHz
            :::

            :::prestressed-beam "Girder A"
            span: 16m
            :::

            :::delta-sigma "ADC 1"
            f_in: 2kHz
            :::

            :::concrete-section "RC Beam"
            width: 300mm
            :::

            :::rf-cascade "Front-End"
            lna: "G=15dB, NF=2dB, IIP3=+10dBm"
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string html = new MarkdownHtmlService().Render(md, new AppSettings(), theme);

        Assert.Contains("retaining-wall-diagram", html);
        Assert.Contains("superhet-receiver-diagram", html);
        Assert.Contains("prestressed-beam-diagram", html);
        Assert.Contains("delta-sigma-diagram", html);
        Assert.Contains("concrete-section-diagram", html);
        Assert.Contains("rf-cascade-diagram", html);
    }
}
