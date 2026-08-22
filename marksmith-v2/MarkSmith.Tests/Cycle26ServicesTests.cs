using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Civil;
using MarkSmith.Services.Electronics;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle26ServicesTests
{
    [Fact]
    public void PumpCurve_CalculatesOperatingDutyPointAndPower()
    {
        string md = """
            :::pump-curve "HVAC Chilled Pump"
            h0: 50m
            kp: 0.004
            h_stat: 18m
            k_sys: 0.006
            bep: 60L/s
            :::
            """;

        var model = PumpCharacteristicService.ParsePump(md);
        Assert.Equal("HVAC Chilled Pump", model.Title);
        Assert.Equal(50.0, model.ShutoffHeadMeters, precision: 1);
        Assert.Equal(0.004, model.PumpDropKp, precision: 4);
        Assert.Equal(18.0, model.StaticHeadMeters, precision: 1);
        Assert.Equal(0.006, model.SystemLossKsys, precision: 4);
        Assert.Equal(60.0, model.BepFlowLps, precision: 1);

        // Q_op = sqrt((50 - 18) / (0.004 + 0.006)) = sqrt(32 / 0.010) = sqrt(3200) approx 56.57 L/s
        Assert.True(model.OperatingFlowLps > 55.0 && model.OperatingFlowLps < 58.0);
        // H_op = 50 - 0.004 * 3200 = 50 - 12.8 = 37.2 m
        Assert.Equal(37.2, model.OperatingHeadMeters, precision: 1);
        Assert.True(model.HydraulicPowerKw > 15.0 && model.HydraulicPowerKw < 25.0);

        string svg = PumpCharacteristicService.RenderPumpSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("HVAC Chilled Pump", svg);
        Assert.Contains("pc-pump-curve", svg);
        Assert.Contains("pc-sys-curve", svg);
        Assert.Contains("Duty Point", svg);
    }

    [Fact]
    public void SolarCell_CalculatesMaximumPowerPointAndFillFactor()
    {
        string md = """
            :::solar-cell "Rooftop Monocrystalline"
            isc: 10.0A
            voc: 48V
            irradiance: 1000W/m2
            temp: 25C
            :::
            """;

        var model = SolarCellCurveService.ParseSolar(md);
        Assert.Equal("Rooftop Monocrystalline", model.Title);
        Assert.Equal(10.0, model.ShortCircuitIsc, precision: 1);
        Assert.Equal(48.0, model.OpenCircuitVoc, precision: 1);
        Assert.Equal(1000.0, model.IrradianceWm2, precision: 1);
        Assert.Equal(25.0, model.TemperatureC, precision: 1);

        // Vmp approx 48 * 0.82 = 39.36 V, Imp approx 10 * 0.92 = 9.2 A -> Pmax approx 362 W
        Assert.True(model.Pmax > 300.0 && model.Pmax < 400.0);
        Assert.True(model.FillFactorPercent > 70.0 && model.FillFactorPercent < 85.0);

        string svg = SolarCellCurveService.RenderSolarSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Rooftop Monocrystalline", svg);
        Assert.Contains("pv-iv-curve", svg);
        Assert.Contains("pv-power-curve", svg);
        Assert.Contains("MPPT", svg);
    }

    [Fact]
    public void PlanetaryGear_CalculatesWillisSpeedRatio()
    {
        string md = """
            :::gear-train "Planetary Speed Reducer"
            sun: 20
            planet: 30
            fixed: "ring"
            :::
            """;

        var model = PlanetaryGearService.ParseGear(md);
        Assert.Equal("Planetary Speed Reducer", model.Title);
        Assert.Equal(20, model.SunTeethZs);
        Assert.Equal(30, model.PlanetTeethZp);
        Assert.Equal("ring", model.FixedMember);

        // Zr = 20 + 2 * 30 = 80 teeth
        Assert.Equal(80, model.RingTeethZr);
        // Speed Ratio i = 1 + 80 / 20 = 5.0
        Assert.Equal(5.0, model.SpeedRatio, precision: 1);
        Assert.Equal(5.0, model.MechanicalAdvantage, precision: 1);

        string svg = PlanetaryGearService.RenderGearSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Planetary Speed Reducer", svg);
        Assert.Contains("gr-ring", svg);
        Assert.Contains("gr-sun", svg);
        Assert.Contains("gr-planet", svg);
    }

    [Fact]
    public void Timer555_CalculatesFrequencyAndDutyCycle()
    {
        string md = """
            :::555-timer "Square Pulse Generator"
            ra: 10k
            rb: 47k
            c: 100nF
            :::
            """;

        var model = Timer555AstableService.ParseTimer(md);
        Assert.Equal("Square Pulse Generator", model.Title);
        Assert.Equal(10000.0, model.ResistorAOhms, precision: 1);
        Assert.Equal(47000.0, model.ResistorBOhms, precision: 1);
        Assert.Equal(1e-7, model.CapacitorFarads, precision: 10);

        // t_high = ln(2) * (10k + 47k) * 100nF = 0.69315 * 57k * 1e-7 = 3.95 ms
        // t_low = ln(2) * 47k * 100nF = 0.69315 * 4.7e-3 = 3.26 ms
        // Period = 7.21 ms -> Frequency approx 138.7 Hz
        Assert.True(model.FrequencyHz > 130.0 && model.FrequencyHz < 145.0);
        // Duty = (10 + 47) / (10 + 94) = 57 / 104 = 54.8%
        Assert.True(model.DutyCyclePercent > 50.0 && model.DutyCyclePercent < 60.0);

        string svg = Timer555AstableService.RenderTimerSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Square Pulse Generator", svg);
        Assert.Contains("tm-cap-wave", svg);
        Assert.Contains("tm-out-wave", svg);
    }

    [Fact]
    public void SoilConsolidation_CalculatesPrimarySettlementAndTimes()
    {
        string md = """
            :::consolidation "Marine Clay Stratum"
            h: 5.0m
            e0: 1.20
            cc: 0.40
            sigma0: 100kPa
            d_sigma: 80kPa
            :::
            """;

        var model = SoilConsolidationService.ParseConsolidation(md);
        Assert.Equal("Marine Clay Stratum", model.Title);
        Assert.Equal(5.0, model.LayerThicknessM, precision: 1);
        Assert.Equal(1.20, model.InitialVoidRatio, precision: 2);
        Assert.Equal(0.40, model.CompressionIndex, precision: 2);
        Assert.Equal(100.0, model.InitialStressKPa, precision: 1);
        Assert.Equal(80.0, model.StressIncrementKPa, precision: 1);

        // Sc = (0.40 * 5.0 / 2.20) * log10(180 / 100) = (2.0 / 2.20) * log10(1.8) = 0.909 * 0.2553 = 0.232 m = 232 mm
        Assert.True(model.PrimarySettlementMm > 200.0 && model.PrimarySettlementMm < 260.0);
        Assert.True(model.Time50Years > 0.1);

        string svg = SoilConsolidationService.RenderConsolidationSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Marine Clay Stratum", svg);
        Assert.Contains("sc-clay", svg);
        Assert.Contains("sc-sand", svg);
        Assert.Contains("sc-isochrone", svg);
    }

    [Fact]
    public void DdsSynthesizer_CalculatesOutputFrequencyAndSfdr()
    {
        string md = """
            :::dds "RF Agile LO"
            f_clk: 120MHz
            n_bits: 32
            m_word: 1073741824
            dac_bits: 14
            :::
            """;

        var model = DdsSynthesizerService.ParseDds(md);
        Assert.Equal("RF Agile LO", model.Title);
        Assert.Equal(120.0, model.ClkFreqMhz, precision: 1);
        Assert.Equal(32, model.PhaseBitsN);
        Assert.Equal(1073741824, model.TuningWordM);
        Assert.Equal(14, model.DacBitsB);

        // Output freq = (2^30 * 120) / 2^32 = 120 / 4 = 30.0 MHz
        Assert.Equal(30.0, model.OutputFreqMhz, precision: 2);
        Assert.True(model.SfdrDb > 80.0);

        string svg = DdsSynthesizerService.RenderDdsSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("RF Agile LO", svg);
        Assert.Contains("dd-ramp", svg);
        Assert.Contains("dd-sine", svg);
    }

    [Fact]
    public void MarkdownHtmlService_RendersAllCycle26Blocks()
    {
        string md = """
            # Advanced Electromechanical System Document

            :::pump-curve "Water Loop"
            h0: 40m
            :::

            :::solar-cell "Solar Array"
            isc: 9.0A
            :::

            :::gear-train "Gearbox"
            sun: 16
            planet: 20
            :::

            :::555-timer "Clock Gen"
            ra: 10k
            :::

            :::consolidation "Foundation"
            h: 3.0m
            :::

            :::dds "Direct Synth"
            f_clk: 100MHz
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string html = new MarkdownHtmlService().Render(md, new AppSettings(), theme);

        Assert.Contains("pump-curve-diagram", html);
        Assert.Contains("solar-cell-diagram", html);
        Assert.Contains("planetary-gear-diagram", html);
        Assert.Contains("timer555-diagram", html);
        Assert.Contains("soil-consolidation-diagram", html);
        Assert.Contains("dds-synth-diagram", html);
    }
}
