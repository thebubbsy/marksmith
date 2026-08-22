using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

// Batch 11 (#57): the Cycle 22–29 engineering/science diagram fences were wired into the preview
// pipeline as 49 separate interpreted full-document Regex.Replace passes (plus 49 injection loop
// sites) — 49 extra scans of the whole markdown on EVERY debounced preview render, with the
// patterns thrashing .NET's bounded 15-entry regex cache. The fences are now lifted in ONE
// compiled pass: a single alternation regex plus a fence-name → renderer dispatch table. The
// alias sets, the code-fence exclusion, and the emitted <div class="…-diagram"> markup are the
// verbatim equivalents of the original per-type blocks, so rendering is byte-identical.
public sealed partial class MarkdownHtmlService
{
    private sealed record EngineeringFenceEntry(string Aliases, string CssClass, Func<string, string> RenderSvg);

    private static readonly EngineeringFenceEntry[] EngineeringFences =
    {
        new("wavefunction|wfc|collapse|quantum-wave", "wavefunction-diagram", v => MarkSmith.Services.Quantum.WaveFunctionCollapseService.RenderWaveFunctionSvg(MarkSmith.Services.Quantum.WaveFunctionCollapseService.ParseWaveFunction(v))),
        new("doppler|shockwave|mach", "doppler-diagram", v => MarkSmith.Services.Physics.DopplerShockwaveService.RenderDopplerSvg(MarkSmith.Services.Physics.DopplerShockwaveService.ParseDoppler(v))),
        new("color-wheel|gamut|colorwheel", "color-wheel-diagram", v => MarkSmith.Services.Design.ColorWheelGamutService.RenderColorWheelSvg(MarkSmith.Services.Design.ColorWheelGamutService.ParseColorWheel(v))),
        new("vumeter|vu-meter|audio-meter", "vumeter-diagram", v => MarkSmith.Services.Audio.DecibelVuMeterService.RenderVuMeterSvg(MarkSmith.Services.Audio.DecibelVuMeterService.ParseVuMeter(v))),
        new("element|element-card|periodic-element", "element-card-diagram", v => MarkSmith.Services.Science.ElementCardRendererService.RenderElementSvg(MarkSmith.Services.Science.ElementCardRendererService.ParseElement(v))),
        new("weir|hydraulic-weir|v-notch", "weir-diagram", v => MarkSmith.Services.Civil.WeirDischargeService.RenderWeirSvg(MarkSmith.Services.Civil.WeirDischargeService.ParseWeir(v))),
        new("bjt|transistor|bjt-curve", "bjt-diagram", v => MarkSmith.Services.Electronics.TransistorCharacteristicService.RenderBjtSvg(MarkSmith.Services.Electronics.TransistorCharacteristicService.ParseBjt(v))),
        new("smith-chart|smithchart|rf-impedance", "smith-chart-diagram", v => MarkSmith.Services.Electronics.SmithChartRendererService.RenderSmithChartSvg(MarkSmith.Services.Electronics.SmithChartRendererService.ParseSmithChart(v))),
        new("mohrs-circle|mohr-circle|stress-tensor", "mohrs-circle-diagram", v => MarkSmith.Services.Civil.MohrsCircleService.RenderMohrsCircleSvg(MarkSmith.Services.Civil.MohrsCircleService.ParseMohrsCircle(v))),
        new("orbit|kepler-orbit|planetary-orbit", "kepler-orbit-diagram", v => MarkSmith.Services.Astronomy.KeplerOrbitVisualizerService.RenderOrbitSvg(MarkSmith.Services.Astronomy.KeplerOrbitVisualizerService.ParseOrbit(v))),
        new("prism|dispersion|optical-prism", "prism-dispersion-diagram", v => MarkSmith.Services.Physics.PrismDispersionService.RenderPrismSvg(MarkSmith.Services.Physics.PrismDispersionService.ParsePrism(v))),
        new("bode-plot|active-filter|bode|filter(?![-\\w])", "filter-bode-diagram", v => MarkSmith.Services.Electronics.OpAmpFilterBodeService.RenderFilterBodeSvg(MarkSmith.Services.Electronics.OpAmpFilterBodeService.ParseFilter(v))),
        new("venturi|venturi-flow|bernoulli-tube", "venturi-diagram", v => MarkSmith.Services.Civil.VenturiFlowService.RenderVenturiSvg(MarkSmith.Services.Civil.VenturiFlowService.ParseVenturi(v))),
        new("euler-gimbal|gimbal|euler-angle", "euler-gimbal-diagram", v => MarkSmith.Services.MathCore.EulerGimbalService.RenderGimbalSvg(MarkSmith.Services.MathCore.EulerGimbalService.ParseGimbal(v))),
        new("poiseuille|laminar-flow|pipe-flow", "poiseuille-diagram", v => MarkSmith.Services.Civil.PoiseuilleFlowService.RenderPoiseuilleSvg(MarkSmith.Services.Civil.PoiseuilleFlowService.ParsePoiseuille(v))),
        new("fourier|fourier-series|harmonic-synth", "fourier-diagram", v => MarkSmith.Services.Audio.FourierHarmonicSynthesizerService.RenderFourierSvg(MarkSmith.Services.Audio.FourierHarmonicSynthesizerService.ParseFourier(v))),
        new("carnot|carnot-cycle|heat-engine", "carnot-diagram", v => MarkSmith.Services.Physics.CarnotCycleService.RenderCarnotSvg(MarkSmith.Services.Physics.CarnotCycleService.ParseCarnot(v))),
        new("hydraulic-jump|hydraulicjump|stilling-basin", "hydraulic-jump-diagram", v => MarkSmith.Services.Civil.HydraulicJumpService.RenderJumpSvg(MarkSmith.Services.Civil.HydraulicJumpService.ParseJump(v))),
        new("mosfet|mosfet-curve|nmos-curve", "mosfet-diagram", v => MarkSmith.Services.Electronics.MosfetCharacteristicService.RenderMosfetSvg(MarkSmith.Services.Electronics.MosfetCharacteristicService.ParseMosfet(v))),
        new("tensile-test|stress-strain|tensile-curve", "tensile-curve-diagram", v => MarkSmith.Services.Civil.TensileCurveService.RenderTensileSvg(MarkSmith.Services.Civil.TensileCurveService.ParseTensile(v))),
        new("timing-diagram|timing-wave|logic-timing", "timing-diagram", v => MarkSmith.Services.Electronics.DigitalTimingDiagramService.RenderTimingSvg(MarkSmith.Services.Electronics.DigitalTimingDiagramService.ParseTiming(v))),
        new("rlc|rlc-resonance|resonant-circuit", "rlc-resonance-diagram", v => MarkSmith.Services.Electronics.RlcResonanceService.RenderRlcSvg(MarkSmith.Services.Electronics.RlcResonanceService.ParseRlc(v))),
        new("relativistic|minkowski|lorentz", "relativistic-diagram", v => MarkSmith.Services.Physics.RelativisticMinkowskiService.RenderRelativisticSvg(MarkSmith.Services.Physics.RelativisticMinkowskiService.ParseRelativistic(v))),
        new("mohr-coulomb|soil-shear|coulomb-failure", "mohr-coulomb-diagram", v => MarkSmith.Services.Civil.MohrCoulombSoilService.RenderSoilSvg(MarkSmith.Services.Civil.MohrCoulombSoilService.ParseSoil(v))),
        new("pll-lock|phase-locked-loop|pll(?![-\\w])", "pll-transient-diagram", v => MarkSmith.Services.Electronics.PllTransientService.RenderPllSvg(MarkSmith.Services.Electronics.PllTransientService.ParsePll(v))),
        new("pump-curve|centrifugal-pump|pump-system", "pump-curve-diagram", v => MarkSmith.Services.Civil.PumpCharacteristicService.RenderPumpSvg(MarkSmith.Services.Civil.PumpCharacteristicService.ParsePump(v))),
        new("solar-cell|pv-curve|photovoltaic", "solar-cell-diagram", v => MarkSmith.Services.Electronics.SolarCellCurveService.RenderSolarSvg(MarkSmith.Services.Electronics.SolarCellCurveService.ParseSolar(v))),
        new("gear-train|planetary-gear|epicyclic-gear", "planetary-gear-diagram", v => MarkSmith.Services.Civil.PlanetaryGearService.RenderGearSvg(MarkSmith.Services.Civil.PlanetaryGearService.ParseGear(v))),
        new("555-timer|astable-555|timer555", "timer555-diagram", v => MarkSmith.Services.Electronics.Timer555AstableService.RenderTimerSvg(MarkSmith.Services.Electronics.Timer555AstableService.ParseTimer(v))),
        new("consolidation|soil-settlement|terzaghi-settlement", "soil-consolidation-diagram", v => MarkSmith.Services.Civil.SoilConsolidationService.RenderConsolidationSvg(MarkSmith.Services.Civil.SoilConsolidationService.ParseConsolidation(v))),
        new("dds|dds-synth|nco", "dds-synth-diagram", v => MarkSmith.Services.Electronics.DdsSynthesizerService.RenderDdsSvg(MarkSmith.Services.Electronics.DdsSynthesizerService.ParseDds(v))),
        new("refrigeration|refrigeration-cycle|ph-diagram", "refrigeration-ph-diagram", v => MarkSmith.Services.Civil.RefrigerationPhService.RenderRefrigerationSvg(MarkSmith.Services.Civil.RefrigerationPhService.ParseRefrigeration(v))),
        new("bldc|bldc-commutation|bldc-motor", "bldc-commutation-diagram", v => MarkSmith.Services.Electronics.BldcCommutationService.RenderBldcSvg(MarkSmith.Services.Electronics.BldcCommutationService.ParseBldc(v))),
        new("buckling|euler-buckling|column-buckling", "euler-buckling-diagram", v => MarkSmith.Services.Civil.EulerBucklingService.RenderBucklingSvg(MarkSmith.Services.Civil.EulerBucklingService.ParseBuckling(v))),
        new("rf-matching|smith-matching|antenna-matching", "rf-smith-matching-diagram", v => MarkSmith.Services.Electronics.RfSmithMatchingService.RenderSmithSvg(MarkSmith.Services.Electronics.RfSmithMatchingService.ParseSmith(v))),
        new("slope-stability|bishop-slope|slip-circle", "slope-stability-diagram", v => MarkSmith.Services.Civil.SlopeStabilityService.RenderSlopeSvg(MarkSmith.Services.Civil.SlopeStabilityService.ParseSlope(v))),
        new("class-d|class-d-amp|pwm-audio", "classd-pwm-diagram", v => MarkSmith.Services.Audio.ClassDPwmService.RenderClassDSvg(MarkSmith.Services.Audio.ClassDPwmService.ParseClassD(v))),
        new("retaining-wall|retainingwall|earth-pressure", "retaining-wall-diagram", v => MarkSmith.Services.Civil.RetainingWallPressureService.RenderRetainingWallSvg(MarkSmith.Services.Civil.RetainingWallPressureService.ParseRetainingWall(v))),
        new("superhet-receiver|superhet|rf-mixer", "superhet-receiver-diagram", v => MarkSmith.Services.Electronics.SuperhetReceiverService.RenderSuperhetSvg(MarkSmith.Services.Electronics.SuperhetReceiverService.ParseSuperhet(v))),
        new("prestressed-beam|prestressed|post-tensioned", "prestressed-beam-diagram", v => MarkSmith.Services.Civil.PrestressedBeamService.RenderPrestressedSvg(MarkSmith.Services.Civil.PrestressedBeamService.ParsePrestressed(v))),
        new("delta-sigma|sigma-delta|noise-shaping", "delta-sigma-diagram", v => MarkSmith.Services.Electronics.DeltaSigmaAdcService.RenderDeltaSigmaSvg(MarkSmith.Services.Electronics.DeltaSigmaAdcService.ParseDeltaSigma(v))),
        new("concrete-section|rc-beam|whitney-block", "concrete-section-diagram", v => MarkSmith.Services.Civil.ConcreteSectionService.RenderConcreteSectionSvg(MarkSmith.Services.Civil.ConcreteSectionService.ParseConcreteSection(v))),
        new("rf-cascade|friis-budget|rf-budget", "rf-cascade-diagram", v => MarkSmith.Services.Electronics.RfCascadedFriisService.RenderCascadeSvg(MarkSmith.Services.Electronics.RfCascadedFriisService.ParseCascade(v))),
        new("pavement-design|pavement|aashto-pavement", "pavement-design-diagram", v => MarkSmith.Services.Civil.PavementDesignService.RenderPavementSvg(MarkSmith.Services.Civil.PavementDesignService.ParsePavement(v))),
        new("buck-boost|inverting-buck-boost|dcdc-buck-boost", "buck-boost-diagram", v => MarkSmith.Services.Electronics.BuckBoostConverterService.RenderBuckBoostSvg(MarkSmith.Services.Electronics.BuckBoostConverterService.ParseBuckBoost(v))),
        new("stormwater-basin|stormwater|detention-basin", "stormwater-basin-diagram", v => MarkSmith.Services.Civil.StormwaterDetentionService.RenderBasinSvg(MarkSmith.Services.Civil.StormwaterDetentionService.ParseBasin(v))),
        new("pll-filter|pll-loop-filter|pll-bode", "pll-filter-diagram", v => MarkSmith.Services.Electronics.PllLoopFilterService.RenderPllFilterSvg(MarkSmith.Services.Electronics.PllLoopFilterService.ParsePllFilter(v))),
        new("bearing-capacity|footing-bearing|soil-bearing", "bearing-capacity-diagram", v => MarkSmith.Services.Civil.SoilBearingCapacityService.RenderBearingSvg(MarkSmith.Services.Civil.SoilBearingCapacityService.ParseBearing(v))),
        new("sallen-key|sallenkey-filter|sallen-key-filter", "sallen-key-diagram", v => MarkSmith.Services.Audio.SallenKeyFilterService.RenderFilterSvg(MarkSmith.Services.Audio.SallenKeyFilterService.ParseFilter(v))),
        // Batch 15 (#70): the 08-19/08-20 cycle wave generated 20 more single-file visualizers that
        // were referenced ONLY by their own Cycle tests (bench/virtual). Wiring them here covers
        // BOTH pipelines at once — the preview lift pass below AND the DOCX path (Batch 14's
        // TryGetEngineeringFenceName/TryRenderEngineeringFence share this exact table). Deliberately
        // NOT wired: PrismSpectrogramService (its :::prism fence collides with the existing
        // PrismDispersionService prism alias — needs a distinct fence name first), NetworkGraph
        // RendererService (library-style Parse, no fence syntax) and MediaTranscriptSyncService
        // (not a visualizer).
        new("resistor|resistor-color|color-code-bands", "resistor-diagram", v => MarkSmith.Services.Electronics.ResistorColorCodeService.RenderResistorSvg(MarkSmith.Services.Electronics.ResistorColorCodeService.ParseResistor(v))),
        new("crystal|crystal-lattice|lattice", "crystal-lattice-diagram", v => MarkSmith.Services.Crystallography.CrystalLatticeRendererService.RenderCrystalSvg(MarkSmith.Services.Crystallography.CrystalLatticeRendererService.ParseCrystal(v))),
        new("palette|color-palette|swatches", "color-palette-diagram", v => MarkSmith.Services.Design.ColorPaletteSwatchService.RenderSwatchesSvg(MarkSmith.Services.Design.ColorPaletteSwatchService.ParsePalette(v))),
        new("sudoku|sudoku-grid", "sudoku-diagram", v => MarkSmith.Services.Games.SudokuGridRendererService.RenderSudokuSvg(MarkSmith.Services.Games.SudokuGridRendererService.ParseSudoku(v))),
        new("map|geomap|geo-map", "geo-map-diagram", v => MarkSmith.Services.Geo.MarkdownGeoMapService.RenderMapSvg(MarkSmith.Services.Geo.MarkdownGeoMapService.ParseMap(v))),
        new("stratigraphy|strat-column|strat", "stratigraphy-diagram", v => MarkSmith.Services.Geology.StratigraphicColumnService.RenderStratigraphySvg(MarkSmith.Services.Geology.StratigraphicColumnService.ParseStratigraphy(v))),
        new("origami|crease-pattern|origami-crease", "origami-diagram", v => MarkSmith.Services.Geometry.OrigamiCreasePatternService.RenderOrigamiSvg(MarkSmith.Services.Geometry.OrigamiCreasePatternService.ParseOrigami(v))),
        new("nn|neural-net|neural-topology", "neural-topology-diagram", v => MarkSmith.Services.MachineLearning.NeuralTopologyRendererService.RenderTopologySvg(MarkSmith.Services.MachineLearning.NeuralTopologyRendererService.ParseTopology(v))),
        new("abacus|soroban", "abacus-diagram", v => MarkSmith.Services.MathDiagrams.SorobanAbacusRendererService.RenderAbacusSvg(MarkSmith.Services.MathDiagrams.SorobanAbacusRendererService.ParseAbacus(v))),
        new("venn-set|venn-diagram-fence|venn(?![-\\w])", "venn-diagram-render", v => MarkSmith.Services.MathDiagrams.VennDiagramRendererService.RenderVennSvg(MarkSmith.Services.MathDiagrams.VennDiagramRendererService.ParseVenn(v))),
        new("diffraction|double-slit|interference", "diffraction-diagram", v => MarkSmith.Services.Physics.DoubleSlitInterferenceService.RenderDiffractionSvg(MarkSmith.Services.Physics.DoubleSlitInterferenceService.ParseDiffraction(v))),
        new("lissajous|lissajous-curve", "lissajous-diagram", v => MarkSmith.Services.Physics.LissajousCurveRendererService.RenderLissajousSvg(MarkSmith.Services.Physics.LissajousCurveRendererService.ParseLissajous(v))),
        new("optics|lens-ray|ray-tracing", "optics-diagram", v => MarkSmith.Services.Physics.OpticalLensRayTracingService.RenderOpticsSvg(MarkSmith.Services.Physics.OpticalLensRayTracingService.ParseOptics(v))),
        new("morse|morse-code|telegraph", "morse-diagram", v => MarkSmith.Services.Telephony.MorseCodeTelegraphService.RenderMorseSvg(MarkSmith.Services.Telephony.MorseCodeTelegraphService.ParseMorse(v))),
        new("clock|roman-clock|roman-numeral-clock", "roman-clock-diagram", v => MarkSmith.Services.Time.RomanNumeralClockRendererService.RenderClockSvg(MarkSmith.Services.Time.RomanNumeralClockRendererService.ParseClock(v))),
        new("histogram|md-histogram", "histogram-diagram", v => MarkSmith.Services.Analytics.MarkdownHistogramService.RenderHistogramSvg(MarkSmith.Services.Analytics.MarkdownHistogramService.ParseHistogram(v))),
        new("aqueduct|hydraulic-gradient", "aqueduct-diagram", v => MarkSmith.Services.Civil.HydraulicGradientService.RenderAqueductSvg(MarkSmith.Services.Civil.HydraulicGradientService.ParseAqueduct(v))),
        new("kmap|karnaugh|karnaugh-map", "kmap-diagram", v => MarkSmith.Services.Electronics.KarnaughMapRendererService.RenderKmapSvg(MarkSmith.Services.Electronics.KarnaughMapRendererService.ParseKmap(v))),
    };

    private static readonly Regex EngineeringFenceRe = BuildEngineeringFenceRegex();

    private static readonly Dictionary<string, EngineeringFenceEntry> EngineeringFenceLookup =
        BuildEngineeringFenceLookup();

    private static Regex BuildEngineeringFenceRegex()
    {
        var aliases = new List<string>(EngineeringFences.Length);
        foreach (var entry in EngineeringFences)
        {
            // Entries whose final alternative carries a lookahead (filter(?![-\w]), pll(?![-\w]))
            // keep it embedded in Aliases; split it off so it applies only to that alternative.
            int cut = entry.Aliases.LastIndexOf('|');
            if (cut >= 0)
            {
                string head = entry.Aliases[..cut];
                string tail = entry.Aliases[(cut + 1)..];
                aliases.Add($"(?:{head}|{tail})");
            }
            else
            {
                aliases.Add(entry.Aliases);
            }
        }
        // Group 1 must capture the fence name for the dispatch lookup; each entry contributes a
        // non-capturing (?:head|tail) set so no extra groups shift the numbering. The lookahead-
        // carrying aliases (filter/pll) keep their embedded (?![-\w]) verbatim from the originals.
        string pattern = @":::(" + string.Join("|", aliases) + @")([^\r\n]*)\r?\n([\s\S]*?):::";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static Dictionary<string, EngineeringFenceEntry> BuildEngineeringFenceLookup()
    {
        var lookup = new Dictionary<string, EngineeringFenceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in EngineeringFences)
        {
            // The lookahead-carrying alternative (e.g. filter(?![-\w])) contains a '|' inside its
            // character class, so split only at the LAST '|' and treat the tail as one alternative.
            int cut = entry.Aliases.LastIndexOf('|');
            string[] names = cut >= 0
                ? [.. entry.Aliases[..cut].Split('|'), entry.Aliases[(cut + 1)..]]
                : [entry.Aliases];
            foreach (var alias in names)
            {
                // Strip any trailing lookahead — the captured fence name is the bare keyword.
                int paren = alias.IndexOf('(');
                string name = paren >= 0 ? alias[..paren] : alias;
                lookup.TryAdd(name, entry);
            }
        }
        return lookup;
    }

    // ── Shared entry points for the DOCX pipeline (Batch 14, #69) ─────────────────────────────
    // The same fence-name lookup + render dispatch the preview uses, so the two pipelines can
    // never diverge on WHICH fences are supported or what they render to. The fence name is the
    // first token of the opening line (:::doppler "Title" → doppler); attributes/titles stay in
    // the block text because every renderer receives the FULL block — the exact contract the
    // preview's regex path honors (entry.RenderSvg(m.Value)).
    public static bool TryGetEngineeringFenceName(string rawBlock, out string name)
    {
        name = "";
        var nl = rawBlock.IndexOfAny(['\r', '\n']);
        var firstLine = (nl >= 0 ? rawBlock[..nl] : rawBlock).TrimStart();
        if (!firstLine.StartsWith(":::")) return false;
        var rest = firstLine[3..];
        int end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end])) end++;
        var candidate = rest[..end];
        // Exact token lookup reproduces the lookahead aliases' semantics: :::filter matches,
        // :::filter-x / :::filterfoo do not (their tokens aren't in the lookup).
        if (candidate.Length == 0 || !EngineeringFenceLookup.ContainsKey(candidate)) return false;
        name = candidate;
        return true;
    }

    public static bool TryRenderEngineeringFence(string rawBlock, out string svg)
    {
        svg = "";
        if (!TryGetEngineeringFenceName(rawBlock, out var name)) return false;
        try
        {
            svg = EngineeringFenceLookup[name].RenderSvg(rawBlock);
            return true;
        }
        catch { return false; }
    }

    // One compiled pass lifts every supported engineering/science :::fence into a safe HTML-comment
    // placeholder (fenced code spans excluded, exactly like the :::smartart lift). htmlBlocks comes
    // back pre-wrapped in its themed <div class="…-diagram"> so injection is one indexed Replace loop.
    private static string LiftEngineeringDiagrams(string markdown, List<(int Start, int End)> fences,
        out List<string> htmlBlocks)
    {
        var blocks = new List<string>();
        string result = EngineeringFenceRe.Replace(markdown, m =>
        {
            foreach (var f in fences)
            {
                if (m.Index >= f.Start && m.Index < f.End) return m.Value; // inside a code fence
            }
            if (!EngineeringFenceLookup.TryGetValue(m.Groups[1].Value, out var entry))
            {
                return m.Value;
            }
            try
            {
                string svg = entry.RenderSvg(m.Value);
                blocks.Add($"<div class=\"{entry.CssClass}\">{svg}</div>");
                return $"\n\n<!--ENGDIAGRAM:{blocks.Count - 1}-->\n\n";
            }
            catch { return m.Value; }
        });
        htmlBlocks = blocks;
        return result;
    }
}
