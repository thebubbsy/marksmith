using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Audio;
using MarkSmith.Services.Civil;
using MarkSmith.Services.Electronics;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle27ServicesTests
{
    [Fact]
    public void RefrigerationPh_CalculatesCopAndEnthalpies()
    {
        string md = """
            :::refrigeration "Chiller R134a Cycle"
            evap: 5C
            cond: 40C
            superheat: 6K
            subcool: 5K
            refrigerant: "R134a"
            :::
            """;

        var model = RefrigerationPhService.ParseRefrigeration(md);
        Assert.Equal("Chiller R134a Cycle", model.Title);
        Assert.Equal("R134a", model.Refrigerant);
        Assert.Equal(5.0, model.EvaporatorTempC, precision: 1);
        Assert.Equal(40.0, model.CondenserTempC, precision: 1);
        Assert.Equal(6.0, model.SuperheatK, precision: 1);
        Assert.Equal(5.0, model.SubcoolingK, precision: 1);

        // h1 approx 400 + 4 + 5.4 = 409.4 kJ/kg
        Assert.True(model.EnthalpyH1 > 400.0 && model.EnthalpyH1 < 420.0);
        // h3 approx 250 + 13.5 - 7 = 256.5 kJ/kg
        Assert.True(model.EnthalpyH3 > 240.0 && model.EnthalpyH3 < 270.0);
        Assert.Equal(model.EnthalpyH3, model.EnthalpyH4, precision: 2);
        // COP approx qe / wc approx (409 - 256) / (450 - 409) = 153 / 41 approx 3.7
        Assert.True(model.Cop > 2.5 && model.Cop < 6.0);

        string svg = RefrigerationPhService.RenderRefrigerationSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Chiller R134a Cycle", svg);
        Assert.Contains("rf-dome", svg);
        Assert.Contains("rf-cycle", svg);
        Assert.Contains("COP", svg);
    }

    [Fact]
    public void BldcCommutation_CalculatesSectorAndTiming()
    {
        string md = """
            :::bldc "E-Bike Motor"
            poles: 8
            vdc: 36V
            current: 12A
            advance: 10deg
            :::
            """;

        var model = BldcCommutationService.ParseBldc(md);
        Assert.Equal("E-Bike Motor", model.Title);
        Assert.Equal(8, model.PolePairsP);
        Assert.Equal(36.0, model.BusVoltageVdc, precision: 1);
        Assert.Equal(12.0, model.PhaseCurrentAmps, precision: 1);
        Assert.Equal(10.0, model.AdvanceDeg, precision: 1);

        Assert.Equal(1.0 / 8.0, model.ElectricalToMechanicalRatio, precision: 4);

        string svg = BldcCommutationService.RenderBldcSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("E-Bike Motor", svg);
        Assert.Contains("bd-hall-h1", svg);
        Assert.Contains("bd-hall-h2", svg);
        Assert.Contains("bd-hall-h3", svg);
        Assert.Contains("bd-emf-a", svg);
    }

    [Fact]
    public void EulerBuckling_CalculatesCriticalLoadAndSlenderness()
    {
        string md = """
            :::buckling "Bridge Pier Column"
            length: 6.0m
            e_gpa: 210GPa
            i_cm4: 8000cm4
            area_cm2: 120cm2
            ends: "fixed-fixed"
            :::
            """;

        var model = EulerBucklingService.ParseBuckling(md);
        Assert.Equal("Bridge Pier Column", model.Title);
        Assert.Equal(6.0, model.LengthM, precision: 1);
        Assert.Equal(210.0, model.YoungsModulusGpa, precision: 1);
        Assert.Equal(8000.0, model.MomentInertiaCm4, precision: 1);
        Assert.Equal(120.0, model.AreaCm2, precision: 1);
        Assert.Equal("fixed-fixed", model.EndCondition);

        // K = 0.5 -> Le = 3.0 m
        Assert.Equal(0.5, model.EffectiveLengthFactorK, precision: 1);
        Assert.Equal(3.0, model.EffectiveLengthM, precision: 1);
        // r = sqrt(8000 / 120) = sqrt(66.67) = 8.16 cm
        Assert.True(model.RadiusGyrationCm > 8.0 && model.RadiusGyrationCm < 8.3);
        // Pcr in kN approx (pi^2 * 210e9 * 8e-5) / 9.0 = 165.8e6 / 9.0 = 18.4 MN = 18420 kN
        Assert.True(model.CriticalBucklingLoadKn > 10000.0 && model.CriticalBucklingLoadKn < 25000.0);

        string svg = EulerBucklingService.RenderBucklingSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Bridge Pier Column", svg);
        Assert.Contains("bk-buckled", svg);
        Assert.Contains("P_cr", svg);
    }

    [Fact]
    public void SmithChart_CalculatesReflectionCoefficientAndVswr()
    {
        string md = """
            :::rf-matching "50-Ohm Dipole Antenna"
            z0: 50ohm
            rl: 75ohm
            xl: 25ohm
            freq: 1.4GHz
            :::
            """;

        var model = RfSmithMatchingService.ParseSmith(md);
        Assert.Equal("50-Ohm Dipole Antenna", model.Title);
        Assert.Equal(50.0, model.CharacteristicZ0, precision: 1);
        Assert.Equal(75.0, model.LoadResistanceRl, precision: 1);
        Assert.Equal(25.0, model.LoadReactanceXl, precision: 1);
        Assert.Equal(1.4, model.FrequencyGhz, precision: 1);

        // z = 1.5 + j0.5
        Assert.Equal(1.5, model.NormalizedR, precision: 2);
        Assert.Equal(0.5, model.NormalizedX, precision: 2);
        // Gamma = (1.5 + j0.5 - 1) / (1.5 + j0.5 + 1) = (0.5 + j0.5) / (2.5 + j0.5)
        // |Gamma| approx sqrt(0.5) / sqrt(6.5) = 0.707 / 2.55 = 0.277
        Assert.True(model.GammaMag > 0.25 && model.GammaMag < 0.30);
        // VSWR = (1 + 0.277) / (1 - 0.277) = 1.277 / 0.723 = 1.76
        Assert.True(model.Vswr > 1.6 && model.Vswr < 1.9);
        Assert.True(model.ReturnLossDb > 10.0 && model.ReturnLossDb < 15.0);

        string svg = RfSmithMatchingService.RenderSmithSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("50-Ohm Dipole Antenna", svg);
        Assert.Contains("sm-outer", svg);
        Assert.Contains("sm-circle", svg);
        Assert.Contains("sm-load-pt", svg);
    }

    [Fact]
    public void SlopeStability_CalculatesBishopFactorOfSafety()
    {
        string md = """
            :::slope-stability "Cut Slope Section"
            height: 10m
            slope: 35deg
            gamma: 20kN/m3
            cohesion: 15kPa
            phi: 28deg
            radius: 18m
            :::
            """;

        var model = SlopeStabilityService.ParseSlope(md);
        Assert.Equal("Cut Slope Section", model.Title);
        Assert.Equal(10.0, model.HeightM, precision: 1);
        Assert.Equal(35.0, model.SlopeAngleDeg, precision: 1);
        Assert.Equal(20.0, model.UnitWeightGamma, precision: 1);
        Assert.Equal(15.0, model.CohesionC, precision: 1);
        Assert.Equal(28.0, model.FrictionAnglePhiDeg, precision: 1);
        Assert.Equal(18.0, model.SlipRadiusM, precision: 1);

        Assert.True(model.FactorOfSafety > 1.0 && model.FactorOfSafety < 2.5);

        string svg = SlopeStabilityService.RenderSlopeSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Cut Slope Section", svg);
        Assert.Contains("sl-soil", svg);
        Assert.Contains("sl-slip", svg);
        Assert.Contains("Bishop", svg);
    }

    [Fact]
    public void ClassDPwm_CalculatesFilterCutoffAndEfficiency()
    {
        string md = """
            :::class-d "Subwoofer Amp"
            f_in: 0.1kHz
            f_pwm: 350kHz
            mod: 0.90
            l: 22uH
            c: 680nF
            load: 4ohm
            :::
            """;

        var model = ClassDPwmService.ParseClassD(md);
        Assert.Equal("Subwoofer Amp", model.Title);
        Assert.Equal(0.1, model.AudioFreqKhz, precision: 2);
        Assert.Equal(350.0, model.CarrierFreqKhz, precision: 1);
        Assert.Equal(0.90, model.ModulationIndex, precision: 2);
        Assert.Equal(22.0, model.InductorUh, precision: 1);
        Assert.Equal(680.0, model.CapacitorNf, precision: 1);
        Assert.Equal(4.0, model.SpeakerLoadOhms, precision: 1);

        // fc = 1 / (2 * pi * sqrt(22e-6 * 680e-9)) = 1 / (2 * pi * sqrt(1.496e-11)) = 1 / (2 * pi * 3.868e-6) = 41.1 kHz
        Assert.True(model.FilterCutoffKhz > 35.0 && model.FilterCutoffKhz < 45.0);
        Assert.True(model.EfficiencyPercent > 90.0);

        string svg = ClassDPwmService.RenderClassDSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Subwoofer Amp", svg);
        Assert.Contains("cd-tri", svg);
        Assert.Contains("cd-pwm", svg);
        Assert.Contains("cd-filtered", svg);
    }

    [Fact]
    public void MarkdownHtmlService_RendersAllCycle27Blocks()
    {
        string md = """
            # Multi-Domain Industrial Engineering Specification

            :::refrigeration "Chiller"
            evap: 4C
            :::

            :::bldc "Spindle Motor"
            poles: 6
            :::

            :::buckling "Column A"
            length: 5m
            :::

            :::rf-matching "RF Feed"
            z0: 50ohm
            :::

            :::slope-stability "Embankment"
            height: 6m
            :::

            :::class-d "Audio System"
            f_in: 1kHz
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string html = new MarkdownHtmlService().Render(md, new AppSettings(), theme);

        Assert.Contains("refrigeration-ph-diagram", html);
        Assert.Contains("bldc-commutation-diagram", html);
        Assert.Contains("euler-buckling-diagram", html);
        Assert.Contains("rf-smith-matching-diagram", html);
        Assert.Contains("slope-stability-diagram", html);
        Assert.Contains("classd-pwm-diagram", html);
    }
}
