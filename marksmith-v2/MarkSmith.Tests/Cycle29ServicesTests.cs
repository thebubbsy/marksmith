using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Audio;
using MarkSmith.Services.Civil;
using MarkSmith.Services.Electronics;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle29ServicesTests
{
    [Fact]
    public void PavementDesign_CalculatesSnProvidedAndAdequacy()
    {
        string md = """
            :::pavement-design "Interstate Highway Section"
            esal: 8.0M
            reliability: 95%
            delta_psi: 1.8
            mr: 45MPa
            layer_d1: 125mm
            layer_d2: 175mm
            layer_d3: 225mm
            :::
            """;

        var model = PavementDesignService.ParsePavement(md);
        Assert.Equal("Interstate Highway Section", model.Title);
        Assert.Equal(8.0, model.EsalMillions, precision: 1);
        Assert.Equal(95.0, model.ReliabilityPercent, precision: 1);
        Assert.Equal(1.8, model.DeltaPsi, precision: 1);
        Assert.Equal(45.0, model.SubgradeMrMpa, precision: 1);
        Assert.Equal(125.0, model.AsphaltD1Mm, precision: 1);
        Assert.Equal(175.0, model.BaseD2Mm, precision: 1);
        Assert.Equal(225.0, model.SubbaseD3Mm, precision: 1);

        // Provided SN = 0.44 * (125/25.4) + 0.14 * (175/25.4) + 0.11 * (225/25.4)
        // 0.44 * 4.921 + 0.14 * 6.890 + 0.11 * 8.858 = 2.165 + 0.965 + 0.974 = 4.10
        Assert.True(model.SnProvided > 3.8 && model.SnProvided < 4.5);
        Assert.True(model.SnRequired > 2.5 && model.SnRequired < 6.0);

        string svg = PavementDesignService.RenderPavementSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Interstate Highway Section", svg);
        Assert.Contains("pv-asphalt", svg);
        Assert.Contains("pv-base", svg);
    }

    [Fact]
    public void BuckBoost_CalculatesDutyCycleAndRipple()
    {
        string md = """
            :::buck-boost "Automotive Inverting Supply"
            vin: 12V
            vout: -12V
            iout: 1.5A
            fsw: 300kHz
            l: 33uH
            c: 150uF
            :::
            """;

        var model = BuckBoostConverterService.ParseBuckBoost(md);
        Assert.Equal("Automotive Inverting Supply", model.Title);
        Assert.Equal(12.0, model.InputVoltageVin, precision: 1);
        Assert.Equal(-12.0, model.OutputVoltageVout, precision: 1);
        Assert.Equal(1.5, model.OutputCurrentIout, precision: 1);
        Assert.Equal(300.0, model.SwitchFreqKhz, precision: 1);
        Assert.Equal(33.0, model.InductorUh, precision: 1);

        // Vin=12, |Vout|=12 => D = 12 / (12 + 12) = 0.50
        Assert.Equal(0.50, model.DutyCycle, precision: 2);

        // Average inductor current I_L = Iout / (1 - D) = 1.5 / 0.5 = 3.0 A
        Assert.Equal(3.0, model.AvgInductorCurrent, precision: 1);

        // Current ripple Delta I_L = (12 * 0.5) / (300e3 * 33e-6) = 6 / 9.9 = 0.606 A
        Assert.Equal(0.606, model.InductorCurrentRipple, precision: 2);

        // Peak current = 3.0 + 0.606/2 = 3.303 A
        Assert.Equal(3.303, model.PeakInductorCurrent, precision: 2);

        Assert.True(model.IsCcm);

        string svg = BuckBoostConverterService.RenderBuckBoostSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Automotive Inverting Supply", svg);
        Assert.Contains("bb-il-wave", svg);
    }

    [Fact]
    public void StormwaterBasin_CalculatesPeakFlowAndDetentionVolume()
    {
        string md = """
            :::stormwater-basin "Commercial Development Basin"
            area: 6.0ha
            c_pre: 0.30
            c_post: 0.80
            tc: 25min
            i_storm: 90mm/hr
            q_allow: 250L/s
            :::
            """;

        var model = StormwaterDetentionService.ParseBasin(md);
        Assert.Equal("Commercial Development Basin", model.Title);
        Assert.Equal(6.0, model.AreaHectares, precision: 1);
        Assert.Equal(0.30, model.RunoffCoeffPre, precision: 2);
        Assert.Equal(0.80, model.RunoffCoeffPost, precision: 2);
        Assert.Equal(25.0, model.TimeConcentrationMin, precision: 1);
        Assert.Equal(90.0, model.RainfallIntensityMmHr, precision: 1);
        Assert.Equal(250.0, model.AllowableReleaseLps, precision: 1);

        // Q_post = (0.80 * 90 * 6.0) / 360 = 1.20 m3/s = 1200 L/s
        Assert.Equal(1.20, model.PeakFlowPostM3S, precision: 2);

        // Q_pre = (0.30 * 90 * 6.0) / 360 = 0.45 m3/s = 450 L/s
        Assert.Equal(0.45, model.PeakFlowPreM3S, precision: 2);

        // Q_allow = 0.25 m3/s => Excess = 1.20 - 0.25 = 0.95 m3/s
        Assert.Equal(0.95, model.ExcessPeakFlowM3S, precision: 2);

        // V_det = 0.95 * (25 * 60) = 0.95 * 1500 = 1425 m3
        Assert.Equal(1425.0, model.StorageVolumeM3, precision: 0);

        string svg = StormwaterDetentionService.RenderBasinSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Commercial Development Basin", svg);
        Assert.Contains("sb-storage", svg);
        Assert.Contains("sb-inflow", svg);
    }

    [Fact]
    public void PllFilter_CalculatesBandwidthAndPhaseMargin()
    {
        string md = """
            :::pll-filter "5GHz WLAN Synthesizer"
            f_ref: 40MHz
            icp: 4.0mA
            kvco: 150MHz/V
            n_div: 125
            r1: 1.2kohm
            c1: 1.5nF
            c2: 120pF
            :::
            """;

        var model = PllLoopFilterService.ParsePllFilter(md);
        Assert.Equal("5GHz WLAN Synthesizer", model.Title);
        Assert.Equal(40.0, model.RefFreqMhz, precision: 1);
        Assert.Equal(4.0, model.ChargePumpCurrentMa, precision: 1);
        Assert.Equal(150.0, model.VcoGainMhzPerV, precision: 1);
        Assert.Equal(125, model.FeedbackDividerN);
        Assert.Equal(1.2, model.ResistorR1Kohm, precision: 1);
        Assert.Equal(1.5, model.CapacitorC1Nf, precision: 1);
        Assert.Equal(120.0, model.CapacitorC2Pf, precision: 1);

        // Zero frequency fz = 1 / (2*pi * 1200 * 1.5e-9) = 1 / (1.1309e-5) = 88.4 kHz
        Assert.True(model.FreqZKhz > 80.0 && model.FreqZKhz < 95.0);

        // Pole frequency fp ~ (1.5e-9 + 120e-12) / (2*pi * 1200 * 1.5e-9 * 120e-12) ~ 1.19 MHz
        Assert.True(model.FreqPKhz > 1000.0 && model.FreqPKhz < 1400.0);

        // Loop Bandwidth ~ sqrt(88.4k * 1.19M) ~ 324 kHz
        Assert.True(model.LoopBandwidthKhz > 280.0 && model.LoopBandwidthKhz < 380.0);

        // Phase Margin typically 50° to 70°
        Assert.True(model.PhaseMarginDeg > 40.0 && model.PhaseMarginDeg < 80.0);

        string svg = PllLoopFilterService.RenderPllFilterSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("5GHz WLAN Synthesizer", svg);
        Assert.Contains("pf-gain", svg);
        Assert.Contains("pf-phase", svg);
    }

    [Fact]
    public void SoilBearingCapacity_CalculatesMeyerhofCapacity()
    {
        string md = """
            :::bearing-capacity "Building Column Footing"
            width: 2.5m
            length: 2.5m
            depth: 1.8m
            gamma: 20kN/m3
            cohesion: 15kPa
            phi: 28deg
            safety_factor: 3.0
            :::
            """;

        var model = SoilBearingCapacityService.ParseBearing(md);
        Assert.Equal("Building Column Footing", model.Title);
        Assert.Equal(2.5, model.WidthM, precision: 1);
        Assert.Equal(2.5, model.LengthM, precision: 1);
        Assert.Equal(1.8, model.EmbedmentDepthM, precision: 1);
        Assert.Equal(20.0, model.UnitWeightGamma, precision: 1);
        Assert.Equal(15.0, model.CohesionC, precision: 1);
        Assert.Equal(28.0, model.FrictionAnglePhiDeg, precision: 1);
        Assert.Equal(3.0, model.FactorOfSafety, precision: 1);

        // Surcharge q = 20 * 1.8 = 36 kPa
        Assert.Equal(36.0, model.SurchargeQ, precision: 1);

        // Bearing factors for phi=28 deg: Nq ~ 14.7, Nc ~ 25.8, Ngamma ~ 16.7
        Assert.True(model.Nq > 12.0 && model.Nq < 18.0);
        Assert.True(model.Nc > 20.0 && model.Nc < 32.0);

        // Ultimate capacity should be substantial (> 800 kPa)
        Assert.True(model.UltimateBearingCapacity > 800.0);
        Assert.True(model.AllowableBearingPressure > 250.0);
        Assert.True(model.AllowableColumnLoadKn > 1500.0);

        string svg = SoilBearingCapacityService.RenderBearingSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Building Column Footing", svg);
        Assert.Contains("bc-footing", svg);
        Assert.Contains("bc-shear-wedge", svg);
    }

    [Fact]
    public void SallenKey_CalculatesCutoffAndQFactor()
    {
        string md = """
            :::sallen-key "Equal Component Butterworth LPF"
            type: "lowpass"
            r1: 15kohm
            r2: 15kohm
            c1: 10nF
            c2: 10nF
            gain: 1.586
            :::
            """;

        var model = SallenKeyFilterService.ParseFilter(md);
        Assert.Equal("Equal Component Butterworth LPF", model.Title);
        Assert.Equal("lowpass", model.FilterType);
        Assert.Equal(15.0, model.ResistorR1Kohm, precision: 1);
        Assert.Equal(15.0, model.ResistorR2Kohm, precision: 1);
        Assert.Equal(10.0, model.CapacitorC1Nf, precision: 1);
        Assert.Equal(10.0, model.CapacitorC2Nf, precision: 1);
        Assert.Equal(1.586, model.OpAmpGainK, precision: 3);

        // f0 = 1 / (2*pi * 15000 * 10e-9) = 1 / (9.4248e-4) = 1061 Hz = 1.061 kHz
        Assert.Equal(1.061, model.CutoffFreqKhz, precision: 2);

        // For equal R and C, Q = 1 / (3 - K) = 1 / (3 - 1.586) = 1 / 1.414 = 0.707 (Butterworth)
        Assert.Equal(0.707, model.QualityFactorQ, precision: 2);
        Assert.Equal(0.707, model.DampingRatio, precision: 2);
        Assert.Equal("Butterworth (Maximally Flat)", model.Alignment);

        string svg = SallenKeyFilterService.RenderFilterSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Equal Component Butterworth LPF", svg);
        Assert.Contains("sk-bode", svg);
    }

    [Fact]
    public void MarkdownHtmlService_RendersAllCycle29Visualizers()
    {
        string doc = """
            # Engineering Analysis Suite

            :::pavement-design "Freeway Pavement"
            esal: 5.0M
            reliability: 95%
            :::

            :::buck-boost "Inverting Converter"
            vin: 12V
            vout: -15V
            :::

            :::stormwater-basin "Detention Facility"
            area: 4.5ha
            q_allow: 180L/s
            :::

            :::pll-filter "RF PLL Synthesizer"
            f_ref: 20MHz
            :::

            :::bearing-capacity "Pad Footing"
            width: 2.0m
            depth: 1.5m
            :::

            :::sallen-key "Active Filter"
            type: "lowpass"
            r1: 10kohm
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string html = new MarkdownHtmlService().Render(doc, new AppSettings(), theme);

        Assert.Contains("pavement-design-diagram", html);
        Assert.Contains("ms-pavement-svg", html);

        Assert.Contains("buck-boost-diagram", html);
        Assert.Contains("ms-buckboost-svg", html);

        Assert.Contains("stormwater-basin-diagram", html);
        Assert.Contains("ms-basin-svg", html);

        Assert.Contains("pll-filter-diagram", html);
        Assert.Contains("ms-pllfilter-svg", html);

        Assert.Contains("bearing-capacity-diagram", html);
        Assert.Contains("ms-bearing-svg", html);

        Assert.Contains("sallen-key-diagram", html);
        Assert.Contains("ms-sallenkey-svg", html);
    }
}
