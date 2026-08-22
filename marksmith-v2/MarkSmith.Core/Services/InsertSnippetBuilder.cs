using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MarkSmith.Services;

/// <summary>
/// Builds the Markdown snippets that the Insert-menu modals emit from the parameters the user
/// collected in a dialog. Pure functions with no UI dependency so the generation logic is
/// unit-testable independent of WinUI. The ProMode paths bypass these entirely and insert the
/// classic raw placeholders directly (see the On*Click handlers in MainWindow.xaml.cs).
/// </summary>
public static class InsertSnippetBuilder
{
    /// <summary>Pipe table. <paramref name="rows"/> counts body rows (the optional header row and
    /// the mandatory separator row come on top). Defaults reproduce the legacy placeholder.</summary>
    public static string Table(int rows, int cols, bool includeHeaderRow)
    {
        rows = Math.Clamp(rows, 1, 50);
        cols = Math.Clamp(cols, 1, 20);
        var sb = new StringBuilder("\n");
        if (includeHeaderRow)
        {
            sb.Append('|');
            for (var c = 1; c <= cols; c++) sb.Append($" Header {c} |");
            sb.Append('\n');
        }
        sb.Append('|'); // delimiter row — required by Markdown tables with or without a header
        for (var c = 0; c < cols; c++) sb.Append(" --- |");
        sb.Append('\n');
        var cell = 1;
        for (var r = 0; r < rows; r++)
        {
            sb.Append('|');
            for (var c = 0; c < cols; c++) sb.Append($" Value {cell++} |");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>[text](url). An empty URL falls back to the literal "url" placeholder.</summary>
    public static string Link(string text, string url) =>
        $"[{(text ?? "").Trim()}]({Or(url, "url")})";

    /// <summary>Fenced code block; an empty language yields a bare ``` fence.</summary>
    public static string CodeBlock(string language, string body) =>
        $"\n```{(language ?? "").Trim()}\n{(body ?? "").TrimEnd()}\n```\n";

    /// <summary>:::chart block from "label,value" lines.</summary>
    public static string Chart(string type, IEnumerable<string> labelValueLines)
    {
        var sb = new StringBuilder($"\n:::chart type=\"{Or(type, "bar")}\"\n");
        foreach (var line in Clean(labelValueLines)) sb.Append(line).Append('\n');
        return sb.Append(":::\n").ToString();
    }

    /// <summary>:::columns block with <paramref name="count"/> (2-4) ===-separated placeholders.</summary>
    public static string Columns(int count)
    {
        count = Math.Clamp(count, 2, 4);
        var sb = new StringBuilder($"\n:::columns count=\"{count}\"\n");
        for (var i = 1; i <= count; i++)
        {
            if (i > 1) sb.Append("===\n");
            sb.Append($"Column {i} content\n");
        }
        return sb.Append(":::\n").ToString();
    }

    /// <summary>:::smartart block; each step becomes a "- " bullet.</summary>
    public static string SmartArt(string type, IEnumerable<string> steps) =>
        BulletedBlock($":::smartart type=\"{Or(type, "process")}\"", steps, "Step 1");

    /// <summary>:::timeline block from "year: label" entries.</summary>
    public static string Timeline(IEnumerable<string> entries) =>
        BulletedBlock(":::timeline", entries, "2026: Milestone");

    /// <summary>:::workflow block; each step becomes a "- " bullet.</summary>
    public static string Workflow(IEnumerable<string> steps) =>
        BulletedBlock(":::workflow", steps, "Step 1");

    /// <summary>:::tabs block; one "=== title" section per line, numbered placeholder content.</summary>
    public static string Tabs(IEnumerable<string> titles)
    {
        var list = Clean(titles).ToList();
        if (list.Count == 0) { list.Add("Tab 1"); list.Add("Tab 2"); }
        var sb = new StringBuilder("\n:::tabs\n");
        var i = 1;
        foreach (var raw in list)
        {
            var title = raw.StartsWith("=== ", StringComparison.Ordinal) ? raw[4..] : raw;
            sb.Append($"=== {title}\nContent {i}\n");
            i++;
        }
        return sb.Append(":::\n").ToString();
    }

    /// <summary>:::embed block for a video/web provider.</summary>
    public static string Embed(string provider, string url) =>
        $"\n:::embed provider=\"{Or(provider, "youtube")}\" src=\"{Or(url, "https://www.youtube.com/watch?v=EXAMPLE_ID")}\"\n:::\n";

    /// <summary>:::references bibliography entry; empty fields fall back to the placeholders.</summary>
    public static string References(string id, string author, string title, string year) =>
        "\n:::references\n" +
        $"@{Or(id, "paper-id")}\n" +
        $"author: {Or(author, "Author Name")}\n" +
        $"title: {Or(title, "Publication Title")}\n" +
        $"year: {Or(year, "2026")}\n" +
        ":::\n";

    /// <summary>:::datagrid block; the first line is the header row.</summary>
    public static string Datagrid(IEnumerable<string> rows)
    {
        var list = Clean(rows).ToList();
        if (list.Count == 0) list.AddRange(new[] { "label,value", "Q1,10", "Q2,25" });
        var sb = new StringBuilder("\n:::datagrid\n");
        foreach (var row in list) sb.Append(row).Append('\n');
        return sb.Append(":::\n").ToString();
    }

    /// <summary>:::canvas SVG scaffold scaled to the requested size.</summary>
    public static string Canvas(int width, int height)
    {
        width = Math.Clamp(width, 10, 4000);
        height = Math.Clamp(height, 10, 4000);
        var cx = width / 2;
        var cy = height / 2;
        var r = (int)(Math.Min(width, height) * 0.4);
        return "\n:::canvas\n" +
               $"<svg viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\">\n" +
               $"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" stroke=\"black\" stroke-width=\"3\" fill=\"red\" />\n" +
               "</svg>\n:::\n";
    }

    /// <summary>:::wfc procedural wave function collapse grid snippet.</summary>
    public static string WaveFunctionCollapse(string title = "Procedural Map WFC", int width = 5, int height = 5)
    {
        width = Math.Clamp(width, 2, 20);
        height = Math.Clamp(height, 2, 20);
        return $"\n:::wfc \"{Or(title, "Procedural Map WFC")}\"\n" +
               $"size: {width}x{height}\n" +
               "tiles: Grass(#22c55e), Road(#64748b), Water(#38bdf8), Wall(#a855f7)\n" +
               "collapse: auto\n" +
               ":::\n";
    }

    /// <summary>:::wavefunction quantum wave superposition and collapse snippet.</summary>
    public static string QuantumWaveFunction(string title = "Quantum Superposition & Collapse", string states = "|0⟩: 0.6, |1⟩: 0.8", string collapseTo = "|1⟩")
    {
        return $"\n:::wavefunction \"{Or(title, "Quantum Superposition & Collapse")}\"\n" +
               $"states: {Or(states, "|0⟩: 0.6, |1⟩: 0.8")}\n" +
               $"collapse_to: {Or(collapseTo, "|1⟩")}\n" +
               ":::\n";
    }

    /// <summary>:::doppler acoustic wavefronts and supersonic Mach cone snippet.</summary>
    public static string DopplerShockwave(string title = "Supersonic Jet Shockwave", double mach = 1.4, int waves = 8)
    {
        return $"\n:::doppler \"{Or(title, "Supersonic Jet Shockwave")}\"\n" +
               $"mach: {mach:F1}\n" +
               $"waves: {waves}\n" +
               ":::\n";
    }

    /// <summary>:::color-wheel HSL harmony gamut snippet.</summary>
    public static string ColorWheel(string title = "Brand Harmony Gamut", double hue = 210, string harmony = "triadic")
    {
        return $"\n:::color-wheel \"{Or(title, "Brand Harmony Gamut")}\"\n" +
               $"hue: {hue:F0}\n" +
               $"harmony: {Or(harmony, "triadic")}\n" +
               ":::\n";
    }

    /// <summary>:::vumeter stereo audio decibel meter snippet.</summary>
    public static string DecibelVuMeter(string title = "Master Bus Audio Level", double leftDb = -6.0, double rightDb = -3.0)
    {
        return $"\n:::vumeter \"{Or(title, "Master Bus Audio Level")}\"\n" +
               $"left: {leftDb:F1}dB\n" +
               $"right: {rightDb:F1}dB\n" +
               ":::\n";
    }

    /// <summary>:::element periodic table element card snippet.</summary>
    public static string ChemicalElement(string symbol = "Au", string name = "Gold", int atomicNumber = 79, double mass = 196.967, string category = "Transition Metal")
    {
        return $"\n:::element \"{Or(symbol, "Au")}\"\n" +
               $"name: \"{Or(name, "Gold")}\"\n" +
               $"atomic: {atomicNumber}\n" +
               $"mass: {mass:F3}\n" +
               $"category: \"{Or(category, "Transition Metal")}\"\n" +
               ":::\n";
    }

    /// <summary>:::weir hydraulic flow discharge snippet.</summary>
    public static string HydraulicWeir(string title = "V-Notch Flow Measurement", string type = "v-notch", double head = 0.35, double angle = 90)
    {
        return $"\n:::weir \"{Or(title, "V-Notch Flow Measurement")}\"\n" +
               $"type: {Or(type, "v-notch")}\n" +
               $"head: {head:F2}m\n" +
               $"angle: {angle:F0}\n" +
               ":::\n";
    }

    /// <summary>:::bjt transistor IV characteristic family curves snippet.</summary>
    public static string TransistorBjt(string title = "2N2222 NPN Output Curves", string type = "NPN", double beta = 100, double va = 100)
    {
        return $"\n:::bjt \"{Or(title, "2N2222 NPN Output Curves")}\"\n" +
               $"type: {Or(type, "NPN")}\n" +
               $"beta: {beta:F0}\n" +
               $"va: {va:F0}V\n" +
               ":::\n";
    }

    /// <summary>:::smith-chart RF impedance and reflection snippet.</summary>
    public static string SmithChart(string title = "Antenna Feed Impedance", double r = 1.0, double x = 0.5, double z0 = 50.0)
    {
        string sign = x >= 0 ? "+" : "";
        return $"\n:::smith-chart \"{Or(title, "Antenna Feed Impedance")}\"\n" +
               $"z: {r:F2}{sign}{x:F2}j\n" +
               $"z0: {z0:F0}\n" +
               ":::\n";
    }

    /// <summary>:::mohrs-circle 2D planar stress tensor snippet.</summary>
    public static string MohrsCircle(string title = "Biaxial Element Stress", double sx = 80, double sy = 20, double txy = 30)
    {
        return $"\n:::mohrs-circle \"{Or(title, "Biaxial Element Stress")}\"\n" +
               $"sx: {sx:F0}\n" +
               $"sy: {sy:F0}\n" +
               $"txy: {txy:F0}\n" +
               ":::\n";
    }

    /// <summary>:::orbit Keplerian planetary orbit snippet.</summary>
    public static string KeplerOrbit(string title = "Mars Keplerian Ellipse", double a = 1.524, double e = 0.0934, double nu = 45)
    {
        return $"\n:::orbit \"{Or(title, "Mars Keplerian Ellipse")}\"\n" +
               $"a: {a:F3}AU\n" +
               $"e: {e:F4}\n" +
               $"nu: {nu:F0}deg\n" +
               ":::\n";
    }

    /// <summary>:::prism optical dispersion spectrum snippet.</summary>
    public static string PrismDispersion(string title = "BK7 Glass Prism Dispersion", double apex = 60, double incident = 48)
    {
        return $"\n:::prism \"{Or(title, "BK7 Glass Prism Dispersion")}\"\n" +
               $"apex: {apex:F0}deg\n" +
               $"incident: {incident:F0}deg\n" +
               ":::\n";
    }

    /// <summary>:::filter active op-amp Bode frequency response snippet.</summary>
    public static string OpAmpFilterBode(string title = "Sallen-Key Lowpass Filter", string type = "lowpass", double cutoff = 1000, double q = 0.707)
    {
        return $"\n:::filter \"{Or(title, "Sallen-Key Lowpass Filter")}\"\n" +
               $"type: {Or(type, "lowpass")}\n" +
               $"cutoff: {cutoff:F0}Hz\n" +
               $"q: {q:F3}\n" +
               ":::\n";
    }

    /// <summary>:::venturi Bernoulli flowmeter and manometer snippet.</summary>
    public static string VenturiFlow(string title = "Venturi Water Flowmeter", double d1 = 100, double d2 = 50, double dh = 180)
    {
        return $"\n:::venturi \"{Or(title, "Venturi Water Flowmeter")}\"\n" +
               $"d1: {d1:F0}mm\n" +
               $"d2: {d2:F0}mm\n" +
               $"dh: {dh:F0}mm\n" +
               ":::\n";
    }

    /// <summary>:::euler-gimbal 3D aircraft attitude and gimbal angles snippet.</summary>
    public static string EulerGimbal(string title = "Aircraft Attitude Angles", double roll = 30, double pitch = 15, double yaw = -45)
    {
        return $"\n:::euler-gimbal \"{Or(title, "Aircraft Attitude Angles")}\"\n" +
               $"roll: {roll:F0}deg\n" +
               $"pitch: {pitch:F0}deg\n" +
               $"yaw: {yaw:F0}deg\n" +
               ":::\n";
    }

    /// <summary>:::poiseuille Hagen-Poiseuille laminar pipe flow velocity profile snippet.</summary>
    public static string PoiseuilleFlow(string title = "Engine Oil Laminar Flow", double r = 50, double l = 10, double mu = 0.08, double dp = 5000)
    {
        return $"\n:::poiseuille \"{Or(title, "Engine Oil Laminar Flow")}\"\n" +
               $"r: {r:F0}mm\n" +
               $"L: {l:F1}m\n" +
               $"mu: {mu:F3}Pa.s\n" +
               $"dp: {dp:F0}Pa\n" +
               ":::\n";
    }

    /// <summary>:::fourier Fourier series harmonic wave synthesizer snippet.</summary>
    public static string FourierHarmonicSynthesizer(string title = "Square Wave Synthesis", string type = "square", int harmonics = 7, double freq = 100)
    {
        return $"\n:::fourier \"{Or(title, "Square Wave Synthesis")}\"\n" +
               $"type: {Or(type, "square")}\n" +
               $"harmonics: {harmonics}\n" +
               $"freq: {freq:F0}Hz\n" +
               ":::\n";
    }

    /// <summary>:::carnot thermodynamic Carnot heat engine cycle snippet.</summary>
    public static string CarnotCycle(string title = "Carnot Heat Engine", double th = 600, double tc = 300, double cr = 4.0)
    {
        return $"\n:::carnot \"{Or(title, "Carnot Heat Engine")}\"\n" +
               $"th: {th:F0}K\n" +
               $"tc: {tc:F0}K\n" +
               $"cr: {cr:F1}\n" +
               ":::\n";
    }

    /// <summary>:::hydraulic-jump open channel supercritical hydraulic jump snippet.</summary>
    public static string HydraulicJump(string title = "Spillway Stilling Basin", double y1 = 0.4, double v1 = 6.5, double width = 3.0)
    {
        return $"\n:::hydraulic-jump \"{Or(title, "Spillway Stilling Basin")}\"\n" +
               $"y1: {y1:F2}m\n" +
               $"v1: {v1:F1}m/s\n" +
               $"width: {width:F1}m\n" +
               ":::\n";
    }

    /// <summary>:::mosfet N-channel MOSFET IV output characteristic curves snippet.</summary>
    public static string MosfetCharacteristic(string title = "NMOS Output Curves", double vth = 2.0, double kn = 50, double lambda = 0.015)
    {
        return $"\n:::mosfet \"{Or(title, "NMOS Output Curves")}\"\n" +
               $"vth: {vth:F1}V\n" +
               $"kn: {kn:F0}\n" +
               $"lambda: {lambda:F3}\n" +
               ":::\n";
    }

    /// <summary>:::tensile-test engineering stress-strain tensile test curve snippet.</summary>
    public static string TensileTest(string title = "Structural Steel A36", double e = 200, double yield = 250, double uts = 400, double frac = 0.25)
    {
        return $"\n:::tensile-test \"{Or(title, "Structural Steel A36")}\"\n" +
               $"E: {e:F0}GPa\n" +
               $"yield: {yield:F0}MPa\n" +
               $"uts: {uts:F0}MPa\n" +
               $"frac: {frac * 100.0:F0}%\n" +
               ":::\n";
    }

    /// <summary>:::timing-diagram synchronous digital logic timing waveform snippet.</summary>
    public static string TimingDiagram(string title = "SPI Bus Transaction", string? clk = "P...P...P...P", string? cs = "1...0...0...1", string? mosi = "x...0...1...x", string? miso = "z...z...D...z")
    {
        return $"\n:::timing-diagram \"{Or(title, "SPI Bus Transaction")}\"\n" +
               $"CLK: {Or(clk, "P...P...P...P")}\n" +
               $"CS_N: {Or(cs, "1...0...0...1")}\n" +
               $"MOSI: {Or(mosi, "x...0...1...x")}\n" +
               $"MISO: {Or(miso, "z...z...D...z")}\n" +
               ":::\n";
    }

    /// <summary>:::rlc series/parallel RLC resonant tank circuit snippet.</summary>
    public static string RlcResonance(string title = "Series RLC Tank", double r = 10, double l = 1, double c = 100)
    {
        return $"\n:::rlc \"{Or(title, "Series RLC Tank")}\"\n" +
               $"r: {r:F0}ohm\n" +
               $"l: {l:F1}mH\n" +
               $"c: {c:F0}nF\n" +
               ":::\n";
    }

    /// <summary>:::relativistic special relativity Minkowski spacetime diagram snippet.</summary>
    public static string RelativisticSpacetime(string title = "Muon High-Speed Decay", double beta = 0.866, double t0 = 2.2, double l0 = 100)
    {
        return $"\n:::relativistic \"{Or(title, "Muon High-Speed Decay")}\"\n" +
               $"beta: {beta:F3}c\n" +
               $"proper_time: {t0:F1}us\n" +
               $"proper_length: {l0:F0}m\n" +
               ":::\n";
    }

    /// <summary>:::mohr-coulomb soil shear strength failure envelope snippet.</summary>
    public static string MohrCoulombSoil(string title = "Clayey Sand Shear Strength", double c = 25, double phi = 32, double sigma = 120, double u = 15)
    {
        return $"\n:::mohr-coulomb \"{Or(title, "Clayey Sand Shear Strength")}\"\n" +
               $"c: {c:F0}kPa\n" +
               $"phi: {phi:F0}deg\n" +
               $"sigma: {sigma:F0}kPa\n" +
               $"u: {u:F0}kPa\n" +
               ":::\n";
    }

    /// <summary>:::pll phase-locked loop transient lock response snippet.</summary>
    public static string PllTransient(string title = "2.4GHz RF Synthesizer PLL", double fref = 10, int n = 240, double zeta = 0.707, double fn = 250)
    {
        return $"\n:::pll \"{Or(title, "2.4GHz RF Synthesizer PLL")}\"\n" +
               $"f_ref: {fref:F0}MHz\n" +
               $"n: {n}\n" +
               $"zeta: {zeta:F3}\n" +
               $"fn: {fn:F0}kHz\n" +
               ":::\n";
    }

    /// <summary>:::pump-curve centrifugal pump head-discharge characteristic snippet.</summary>
    public static string PumpCurve(string title = "Chilled Water Pump", double h0 = 45, double kp = 0.005, double hstat = 15, double ksys = 0.008, double bep = 50)
    {
        return $"\n:::pump-curve \"{Or(title, "Chilled Water Pump")}\"\n" +
               $"h0: {h0:F0}m\n" +
               $"kp: {kp:F4}\n" +
               $"h_stat: {hstat:F0}m\n" +
               $"k_sys: {ksys:F4}\n" +
               $"bep: {bep:F0}L/s\n" +
               ":::\n";
    }

    /// <summary>:::solar-cell photovoltaic solar cell IV/PV power curve snippet.</summary>
    public static string SolarCell(string title = "Monocrystalline PV Panel", double isc = 9.5, double voc = 45, double irradiance = 1000, double temp = 25)
    {
        return $"\n:::solar-cell \"{Or(title, "Monocrystalline PV Panel")}\"\n" +
               $"isc: {isc:F1}A\n" +
               $"voc: {voc:F0}V\n" +
               $"irradiance: {irradiance:F0}W/m2\n" +
               $"temp: {temp:F0}C\n" +
               ":::\n";
    }

    /// <summary>:::gear-train planetary epicyclic gear train snippet.</summary>
    public static string PlanetaryGear(string title = "Planetary Reducer", int sun = 18, int planet = 24, string? fixedMem = "ring")
    {
        return $"\n:::gear-train \"{Or(title, "Planetary Reducer")}\"\n" +
               $"sun: {sun}\n" +
               $"planet: {planet}\n" +
               $"fixed: {Or(fixedMem, "ring")}\n" +
               ":::\n";
    }

    /// <summary>:::555-timer NE555 timer astable multivibrator snippet.</summary>
    public static string Timer555(string title = "Astable Pulse Generator", double ra = 10, double rb = 47, double c = 100)
    {
        return $"\n:::555-timer \"{Or(title, "Astable Pulse Generator")}\"\n" +
               $"ra: {ra:F0}k\n" +
               $"rb: {rb:F0}k\n" +
               $"c: {c:F0}nF\n" +
               ":::\n";
    }

    /// <summary>:::consolidation 1D Terzaghi soil consolidation settlement snippet.</summary>
    public static string SoilConsolidation(string title = "Soft Clay Settlement", double h = 4.0, double e0 = 1.10, double cc = 0.35, double s0 = 80, double ds = 60)
    {
        return $"\n:::consolidation \"{Or(title, "Soft Clay Settlement")}\"\n" +
               $"h: {h:F1}m\n" +
               $"e0: {e0:F2}\n" +
               $"cc: {cc:F2}\n" +
               $"sigma0: {s0:F0}kPa\n" +
               $"d_sigma: {ds:F0}kPa\n" +
               ":::\n";
    }

    /// <summary>:::dds direct digital synthesizer NCO generator snippet.</summary>
    public static string DdsSynthesizer(string title = "DDS NCO Generator", double fclk = 100, int n = 32, long m = 1073741824, int dac = 12)
    {
        return $"\n:::dds \"{Or(title, "DDS NCO Generator")}\"\n" +
               $"f_clk: {fclk:F0}MHz\n" +
               $"n_bits: {n}\n" +
               $"m_word: {m}\n" +
               $"dac_bits: {dac}\n" +
               ":::\n";
    }

    /// <summary>:::refrigeration vapor-compression refrigeration cycle P-h snippet.</summary>
    public static string Refrigeration(string title = "Chiller R134a Cycle", double evap = 4, double cond = 38, double sh = 5, double sc = 4, string fluid = "R134a")
    {
        return $"\n:::refrigeration \"{Or(title, "Chiller R134a Cycle")}\"\n" +
               $"evap: {evap:F0}C\n" +
               $"cond: {cond:F0}C\n" +
               $"superheat: {sh:F0}K\n" +
               $"subcool: {sc:F0}K\n" +
               $"refrigerant: {Or(fluid, "R134a")}\n" +
               ":::\n";
    }

    /// <summary>:::bldc brushless DC motor 3-phase commutation snippet.</summary>
    public static string BldcCommutation(string title = "Drone Motor 12-Pole", int poles = 4, double vdc = 24, double cur = 8)
    {
        return $"\n:::bldc \"{Or(title, "Drone Motor 12-Pole")}\"\n" +
               $"poles: {poles}\n" +
               $"vdc: {vdc:F0}V\n" +
               $"current: {cur:F1}A\n" +
               ":::\n";
    }

    /// <summary>:::buckling column Euler elastic buckling snippet.</summary>
    public static string EulerBuckling(string title = "Steel H-Beam Column", double l = 4.5, double e = 200, double i = 4500, double a = 80, string ends = "fixed-pinned")
    {
        return $"\n:::buckling \"{Or(title, "Steel H-Beam Column")}\"\n" +
               $"length: {l:F1}m\n" +
               $"e_gpa: {e:F0}GPa\n" +
               $"i_cm4: {i:F0}cm4\n" +
               $"area_cm2: {a:F0}cm2\n" +
               $"ends: {Or(ends, "fixed-pinned")}\n" +
               ":::\n";
    }

    /// <summary>:::rf-matching RF transmission line impedance matching snippet.</summary>
    public static string RfSmithMatching(string title = "50-Ohm Antenna Matching", double z0 = 50, double rl = 25, double xl = 40, double f = 2.4)
    {
        return $"\n:::rf-matching \"{Or(title, "50-Ohm Antenna Matching")}\"\n" +
               $"z0: {z0:F0}ohm\n" +
               $"rl: {rl:F0}ohm\n" +
               $"xl: {xl:F0}ohm\n" +
               $"freq: {f:F1}GHz\n" +
               ":::\n";
    }

    /// <summary>:::slope-stability soil slope stability Bishop slip circle snippet.</summary>
    public static string SlopeStability(string title = "Highway Embankment", double h = 8, double slope = 30, double gamma = 19, double c = 12, double phi = 26)
    {
        return $"\n:::slope-stability \"{Or(title, "Highway Embankment")}\"\n" +
               $"height: {h:F0}m\n" +
               $"slope: {slope:F0}deg\n" +
               $"gamma: {gamma:F0}kN/m3\n" +
               $"cohesion: {c:F0}kPa\n" +
               $"phi: {phi:F0}deg\n" +
               ":::\n";
    }

    /// <summary>:::class-d audio amplifier PWM modulation and LC filter snippet.</summary>
    public static string ClassDPwm(string title = "High-Efficiency Audio Amp", double fin = 1.0, double fpwm = 400, double mod = 0.85, double l = 15, double c = 470, double r = 8)
    {
        return $"\n:::class-d \"{Or(title, "High-Efficiency Audio Amp")}\"\n" +
               $"f_in: {fin:F1}kHz\n" +
               $"f_pwm: {fpwm:F0}kHz\n" +
               $"mod: {mod:F2}\n" +
               $"l: {l:F0}uH\n" +
               $"c: {c:F0}nF\n" +
               $"load: {r:F0}ohm\n" +
               ":::\n";
    }

    /// <summary>:::retaining-wall Rankine earth pressure retaining wall snippet.</summary>
    public static string RetainingWall(string title = "Cantilever Concrete Wall", double h = 6, double gamma = 18, double phi = 32, double q = 15, double c = 0)
    {
        return $"\n:::retaining-wall \"{Or(title, "Cantilever Concrete Wall")}\"\n" +
               $"height: {h:F1}m\n" +
               $"gamma: {gamma:F1}kN/m3\n" +
               $"phi: {phi:F0}deg\n" +
               $"surcharge: {q:F0}kPa\n" +
               $"cohesion: {c:F0}kPa\n" +
               ":::\n";
    }

    /// <summary>:::superhet-receiver superheterodyne RF receiver mixer & IF snippet.</summary>
    public static string SuperhetReceiver(string title = "FM Broadcast Front-End", double rf = 100, double ifreq = 10.7, string lo = "high", double q = 45)
    {
        return $"\n:::superhet-receiver \"{Or(title, "FM Broadcast Front-End")}\"\n" +
               $"f_rf: {rf:F1}MHz\n" +
               $"f_if: {ifreq:F1}MHz\n" +
               $"lo_side: \"{lo}\"\n" +
               $"q_filter: {q:F0}\n" +
               ":::\n";
    }

    /// <summary>:::prestressed-beam prestressed concrete beam snippet.</summary>
    public static string PrestressedBeam(string title = "Post-Tensioned Girder", double span = 18, double d = 1.2, double w = 0.5, double p = 2500, double e = 0.35, double load = 35)
    {
        return $"\n:::prestressed-beam \"{Or(title, "Post-Tensioned Girder")}\"\n" +
               $"span: {span:F1}m\n" +
               $"depth: {d:F1}m\n" +
               $"width: {w:F1}m\n" +
               $"p_jack: {p:F0}kN\n" +
               $"e_mid: {e:F2}m\n" +
               $"load: {load:F0}kN/m\n" +
               ":::\n";
    }

    /// <summary>:::delta-sigma Delta-Sigma ADC noise shaping snippet.</summary>
    public static string DeltaSigma(string title = "Audio Sigma-Delta ADC", double fin = 1.0, double fs = 44.1, int osr = 64, int bits = 1)
    {
        return $"\n:::delta-sigma \"{Or(title, "Audio Sigma-Delta ADC")}\"\n" +
               $"f_in: {fin:F1}kHz\n" +
               $"f_s: {fs:F1}kHz\n" +
               $"osr: {osr}\n" +
               $"bits: {bits}\n" +
               ":::\n";
    }

    /// <summary>:::concrete-section reinforced concrete ultimate moment capacity snippet.</summary>
    public static string ConcreteSection(string title = "RC Beam Section", double b = 300, double h = 600, double d = 540, double fc = 32, double fy = 500, double asSteel = 1800)
    {
        return $"\n:::concrete-section \"{Or(title, "RC Beam Section")}\"\n" +
               $"width: {b:F0}mm\n" +
               $"depth: {h:F0}mm\n" +
               $"d_eff: {d:F0}mm\n" +
               $"fc: {fc:F0}MPa\n" +
               $"fy: {fy:F0}MPa\n" +
               $"rebar_area: {asSteel:F0}mm2\n" +
               ":::\n";
    }

    /// <summary>:::rf-cascade cascaded RF front-end Friis noise figure budget snippet.</summary>
    public static string RfCascade(string title = "Receiver RF Front-End", string lna = "G=18dB, NF=1.5dB, IIP3=+5dBm", string filter = "G=-2dB, NF=2dB, IIP3=+50dBm", string mixer = "G=8dB, NF=9dB, IIP3=+12dBm")
    {
        return $"\n:::rf-cascade \"{Or(title, "Receiver RF Front-End")}\"\n" +
               $"lna: \"{lna}\"\n" +
               $"filter: \"{filter}\"\n" +
               $"mixer: \"{mixer}\"\n" +
               ":::\n";
    }

    /// <summary>:::pavement-design flexible pavement AASHTO structural number snippet.</summary>
    public static string PavementDesign(string title = "AASHTO Flexible Pavement Design", double esal = 5.0, double r = 95, double d1 = 100, double d2 = 150, double d3 = 200, double mr = 50)
    {
        return $"\n:::pavement-design \"{Or(title, "AASHTO Flexible Pavement Design")}\"\n" +
               $"esal: {esal:F1}M\n" +
               $"reliability: {r:F0}%\n" +
               $"layer_d1: {d1:F0}mm\n" +
               $"layer_d2: {d2:F0}mm\n" +
               $"layer_d3: {d3:F0}mm\n" +
               $"mr: {mr:F0}MPa\n" +
               ":::\n";
    }

    /// <summary>:::buck-boost inverting DC-DC switching converter snippet.</summary>
    public static string BuckBoost(string title = "Inverting Buck-Boost DC-DC Converter", double vin = 12.0, double vout = -15.0, double iout = 2.0, double fsw = 250.0, double l = 47.0, double c = 220.0)
    {
        return $"\n:::buck-boost \"{Or(title, "Inverting Buck-Boost DC-DC Converter")}\"\n" +
               $"vin: {vin:F1}V\n" +
               $"vout: {vout:F1}V\n" +
               $"iout: {iout:F1}A\n" +
               $"fsw: {fsw:F0}kHz\n" +
               $"l: {l:F0}uH\n" +
               $"c: {c:F0}uF\n" +
               ":::\n";
    }

    /// <summary>:::stormwater-basin detention basin & hydrograph routing snippet.</summary>
    public static string StormwaterBasin(string title = "Subdivision Retention Basin", double area = 4.5, double cPre = 0.25, double cPost = 0.75, double tc = 20.0, double i = 85.0, double qAllow = 180.0)
    {
        return $"\n:::stormwater-basin \"{Or(title, "Subdivision Retention Basin")}\"\n" +
               $"area: {area:F1}ha\n" +
               $"c_pre: {cPre:F2}\n" +
               $"c_post: {cPost:F2}\n" +
               $"tc: {tc:F0}min\n" +
               $"i_storm: {i:F0}mm/hr\n" +
               $"q_allow: {qAllow:F0}L/s\n" +
               ":::\n";
    }

    /// <summary>:::pll-filter PLL charge pump and 2nd-order loop filter snippet.</summary>
    public static string PllFilter(string title = "2.4GHz RF Synthesizer PLL", double fRef = 20.0, double icp = 2.5, double kvco = 120.0, int n = 120, double r1 = 1.8, double c1 = 2.2, double c2 = 150.0)
    {
        return $"\n:::pll-filter \"{Or(title, "2.4GHz RF Synthesizer PLL")}\"\n" +
               $"f_ref: {fRef:F1}MHz\n" +
               $"icp: {icp:F1}mA\n" +
               $"kvco: {kvco:F0}MHz/V\n" +
               $"n_div: {n}\n" +
               $"r1: {r1:F1}kohm\n" +
               $"c1: {c1:F1}nF\n" +
               $"c2: {c2:F0}pF\n" +
               ":::\n";
    }

    /// <summary>:::bearing-capacity Meyerhof shallow foundation bearing capacity snippet.</summary>
    public static string BearingCapacity(string title = "Pad Footing Bearing Capacity", double b = 2.0, double l = 2.0, double df = 1.5, double gamma = 19.0, double c = 10.0, double phi = 30.0, double fs = 3.0)
    {
        return $"\n:::bearing-capacity \"{Or(title, "Pad Footing Bearing Capacity")}\"\n" +
               $"width: {b:F1}m\n" +
               $"length: {l:F1}m\n" +
               $"depth: {df:F1}m\n" +
               $"gamma: {gamma:F1}kN/m3\n" +
               $"cohesion: {c:F1}kPa\n" +
               $"phi: {phi:F0}deg\n" +
               $"safety_factor: {fs:F1}\n" +
               ":::\n";
    }

    /// <summary>:::sallen-key active 2nd-order Sallen-Key filter snippet.</summary>
    public static string SallenKey(string title = "Butterworth 2nd-Order LPF", string type = "lowpass", double r1 = 10.0, double r2 = 10.0, double c1 = 22.0, double c2 = 10.0, double gain = 1.0)
    {
        return $"\n:::sallen-key \"{Or(title, "Butterworth 2nd-Order LPF")}\"\n" +
               $"type: \"{type}\"\n" +
               $"r1: {r1:F0}kohm\n" +
               $"r2: {r2:F0}kohm\n" +
               $"c1: {c1:F0}nF\n" +
               $"c2: {c2:F0}nF\n" +
               $"gain: {gain:F1}\n" +
               ":::\n";
    }

    /// <summary>:::watermark snippet.</summary>
    public static string Watermark(string text = "CONFIDENTIAL", double opacity = 0.15, bool diagonal = true) =>
        $"\n:::watermark \"{Or(text, "CONFIDENTIAL")}\" opacity={opacity:0.00}{(diagonal ? "" : " diagonal=false")}\n";

    /// <summary>:::line-numbers snippet.</summary>
    public static string LineNumbers(int countBy = 5, string restart = "per-page") =>
        $"\n:::line-numbers count-by={Math.Max(1, countBy)} restart=\"{Or(restart, "per-page")}\"\n";

    /// <summary>:::cover-page executive gallery snippet.</summary>
    public static string CoverPage(string title = "Document Title", string subtitle = "Executive Brief", string author = "Author Name", string date = "2026-08-23", string version = "v1.0") =>
        $"\n:::cover-page\n" +
        $"title: \"{Or(title, "Document Title")}\"\n" +
        $"subtitle: \"{Or(subtitle, "Executive Brief")}\"\n" +
        $"author: \"{Or(author, "Author Name")}\"\n" +
        $"date: \"{Or(date, "2026-08-23")}\"\n" +
        $"version: \"{Or(version, "v1.0")}\"\n" +
        $":::\n";

    /// <summary>:::dropcap editorial paragraph snippet.</summary>
    public static string DropCap(string text = "Paragraph beginning with a styled dropped capital letter.", int lines = 3) =>
        $"\n:::dropcap lines={Math.Max(1, lines)}\n{Or(text, "Paragraph text...")}\n:::\n";

    /// <summary>:::index back-of-document concordance index snippet.</summary>
    public static string ConcordanceIndex(int columns = 2) =>
        $"\n:::index count={Math.Max(1, columns)}\n:::\n";

    /// <summary>Inline index anchor ^[index: "Category:Topic"].</summary>
    public static string IndexAnchor(string category, string topic) =>
        $"^[index: \"{Or(category, "General")}:{Or(topic, "Topic")}\"]";

    /// <summary>:::parallel bilingual synchronized columns snippet.</summary>
    public static string ParallelColumns(string leftHeader = "English", string rightHeader = "Français", string leftContent = "Left column content", string rightContent = "Right column content") =>
        $"\n:::parallel \"{Or(leftHeader, "English")}\" | \"{Or(rightHeader, "Français")}\"\n" +
        $"{Or(leftContent, "Left text")}\n" +
        $"===\n" +
        $"{Or(rightContent, "Right text")}\n" +
        $":::\n";

    /// <summary>Fillable form dropdown [dropdown: Opt1 | Opt2 | Opt3].</summary>
    public static string FormDropdown(IEnumerable<string>? options = null)
    {
        var list = Clean(options).ToList();
        if (list.Count == 0) { list.Add("Option 1"); list.Add("Option 2"); list.Add("Option 3"); }
        return $"[dropdown: {string.Join(" | ", list)}]";
    }

    /// <summary>Fillable form date [date: YYYY-MM-DD].</summary>
    public static string FormDate(string? defaultDate = null) =>
        string.IsNullOrWhiteSpace(defaultDate) ? "[date]" : $"[date: {defaultDate.Trim()}]";

    /// <summary>Fillable form text [text: "Placeholder"].</summary>
    public static string FormText(string placeholder = "Enter text...") =>
        $"[text: \"{Or(placeholder, "Enter text...")}\"]";

    /// <summary>Table calculation formula cell snippet.</summary>
    public static string TableFormula(string formula = "=SUM(ABOVE)", string? format = null) =>
        string.IsNullOrWhiteSpace(format) ? formula.Trim() : $"{formula.Trim()} \\# \"{format.Trim()}\"";

    // ---- helpers -------------------------------------------------------------------------------

    private static string BulletedBlock(string fence, IEnumerable<string> lines, string fallback)
    {
        var list = Clean(lines).ToList();
        if (list.Count == 0) list.Add(fallback);
        var sb = new StringBuilder($"\n{fence}\n");
        foreach (var raw in list)
        {
            var item = raw.StartsWith("- ", StringComparison.Ordinal) ? raw[2..] : raw;
            sb.Append("- ").Append(item).Append('\n');
        }
        return sb.Append(":::\n").ToString();
    }

    private static IEnumerable<string> Clean(IEnumerable<string>? lines) =>
        (lines ?? Array.Empty<string>()).Select(l => l.Trim()).Where(l => l.Length > 0);

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
