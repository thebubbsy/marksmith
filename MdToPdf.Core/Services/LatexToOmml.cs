using DocumentFormat.OpenXml;
using M = DocumentFormat.OpenXml.Math;

namespace MdToPdf.Services;

// Converts a practical subset of LaTeX math into Office MathML (OMML) so equations exported to DOCX
// become *editable Word equations* (Cambria Math), not flat text. It covers what actually shows up
// in AI-chat math — fractions, super/subscripts, roots, n-ary sum/integral with limits, delimiters,
// Greek letters, common operators, function names, and simple accents. Anything it can't parse
// degrades gracefully to a literal text run, so the document stays valid and no content is lost.
internal static class LatexToOmml
{
    // ---- public entry ------------------------------------------------------------------------

    public static M.OfficeMath Build(string? latex)
    {
        var math = new M.OfficeMath();
        var src = latex ?? string.Empty;
        try
        {
            var parser = new Parser(Tokenize(src));
            foreach (var el in parser.ParseSequence(SeqStop.End))
                math.Append(el);
        }
        catch
        {
            math.RemoveAllChildren();
        }
        if (!math.HasChildren) math.Append(TextRun(src)); // fallback: never lose the source
        return math;
    }

    // ---- element helpers ---------------------------------------------------------------------

    private static M.Run TextRun(string s, bool upright = false)
    {
        var r = new M.Run();
        if (upright) r.Append(new M.RunProperties(new M.Style { Val = M.StyleValues.Plain }));
        r.Append(new M.Text(s) { Space = SpaceProcessingModeValues.Preserve });
        return r;
    }

    private static T Arg<T>(IEnumerable<OpenXmlElement> kids) where T : OpenXmlCompositeElement, new()
    {
        var a = new T();
        foreach (var k in kids) a.Append(k);
        return a;
    }

    private static M.Base Base(IEnumerable<OpenXmlElement> e) => Arg<M.Base>(e);

    // ---- tokenizer ---------------------------------------------------------------------------

    private enum Kind { Cmd, LBrace, RBrace, LBracket, RBracket, Sup, Sub, Chr, Amp }
    private readonly record struct Tok(Kind Kind, string Text);

    private static List<Tok> Tokenize(string s)
    {
        var toks = new List<Tok>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == '~') { i++; continue; }
            if (c == '\\')
            {
                i++;
                if (i >= s.Length) break;
                if (char.IsLetter(s[i]))
                {
                    int start = i;
                    while (i < s.Length && char.IsLetter(s[i])) i++;
                    toks.Add(new Tok(Kind.Cmd, s[start..i]));
                }
                else
                {
                    // escaped single char: \{ \} \| \, \; etc. Spacing commands are dropped.
                    char e = s[i++];
                    if (e is ',' or ';' or ':' or '!' or ' ') continue; // thin spaces
                    if (e == '\\') toks.Add(new Tok(Kind.Cmd, "\\"));
                    else toks.Add(new Tok(Kind.Chr, e.ToString()));
                }
                continue;
            }
            i++;
            switch (c)
            {
                case '{': toks.Add(new Tok(Kind.LBrace, "{")); break;
                case '}': toks.Add(new Tok(Kind.RBrace, "}")); break;
                case '[': toks.Add(new Tok(Kind.LBracket, "[")); break;
                case ']': toks.Add(new Tok(Kind.RBracket, "]")); break;
                case '^': toks.Add(new Tok(Kind.Sup, "^")); break;
                case '_': toks.Add(new Tok(Kind.Sub, "_")); break;
                case '&': toks.Add(new Tok(Kind.Amp, "&")); break;
                default: toks.Add(new Tok(Kind.Chr, c.ToString())); break;
            }
        }
        return toks;
    }

    private enum SeqStop { End, Brace, Right, Bracket }

    // ---- parser ------------------------------------------------------------------------------

    private sealed class Parser
    {
        private readonly List<Tok> _t;
        private int _i;
        public Parser(List<Tok> t) { _t = t; }

        private bool More => _i < _t.Count;
        private Tok Cur => _t[_i];

        public List<OpenXmlElement> ParseSequence(SeqStop stop)
        {
            var outp = new List<OpenXmlElement>();
            while (More)
            {
                var k = Cur.Kind;
                if (stop == SeqStop.Brace && k == Kind.RBrace) { _i++; break; }
                if (stop == SeqStop.Bracket && k == Kind.RBracket) { _i++; break; }
                if (stop == SeqStop.Right && k == Kind.Cmd && Cur.Text == "right") break;
                if (k == Kind.RBrace || k == Kind.RBracket) { _i++; continue; } // stray closer
                outp.AddRange(ParseAtomWithScripts());
            }
            return outp;
        }

        // One atom plus any trailing ^/_ scripts, returned as a flat element list.
        private List<OpenXmlElement> ParseAtomWithScripts()
        {
            var baseEls = ParseAtom();

            List<OpenXmlElement>? sub = null, sup = null;
            while (More && (Cur.Kind == Kind.Sup || Cur.Kind == Kind.Sub))
            {
                bool isSup = Cur.Kind == Kind.Sup;
                _i++;
                var arg = ParseAtom();
                if (isSup) sup = arg; else sub = arg;
            }

            if (sub != null && sup != null)
                return new() { new M.SubSuperscript(Base(baseEls), Arg<M.SubArgument>(sub), Arg<M.SuperArgument>(sup)) };
            if (sup != null)
                return new() { new M.Superscript(Base(baseEls), Arg<M.SuperArgument>(sup)) };
            if (sub != null)
                return new() { new M.Subscript(Base(baseEls), Arg<M.SubArgument>(sub)) };
            return baseEls;
        }

        // A single atom: a group {...}, a command construct, or one character.
        private List<OpenXmlElement> ParseAtom()
        {
            if (!More) return new();
            var t = Cur;
            switch (t.Kind)
            {
                case Kind.LBrace:
                    _i++;
                    return ParseSequence(SeqStop.Brace);
                case Kind.Cmd:
                    _i++;
                    return ParseCommand(t.Text);
                case Kind.Chr:
                    _i++;
                    return new() { TextRun(t.Text) };
                default:
                    _i++;
                    return new() { TextRun(t.Text) };
            }
        }

        // The next braced/single argument, as an element list (for \frac operands etc.).
        private List<OpenXmlElement> ParseGroupArg() => ParseAtom();

        // Raw text of a {...} group (for \text / \mathrm), preserving spaces.
        private string ParseBracedRaw()
        {
            if (!More || Cur.Kind != Kind.LBrace) { if (More) { var s = Cur.Text; _i++; return s; } return ""; }
            _i++; // {
            var sb = new System.Text.StringBuilder();
            int depth = 1;
            while (More)
            {
                var t = Cur; _i++;
                if (t.Kind == Kind.LBrace) { depth++; sb.Append('{'); }
                else if (t.Kind == Kind.RBrace) { if (--depth == 0) break; sb.Append('}'); }
                else sb.Append(t.Text);
            }
            return sb.ToString();
        }

        private List<OpenXmlElement> ParseCommand(string cmd)
        {
            switch (cmd)
            {
                case "frac":
                case "dfrac":
                case "tfrac":
                {
                    var num = ParseGroupArg();
                    var den = ParseGroupArg();
                    return new() { new M.Fraction(Arg<M.Numerator>(num), Arg<M.Denominator>(den)) };
                }
                case "binom":
                case "dbinom":
                case "tbinom":
                {
                    var num = ParseGroupArg();
                    var den = ParseGroupArg();
                    var frac = new M.Fraction(
                        new M.FractionProperties(new M.FractionType { Val = M.FractionTypeValues.NoBar }),
                        Arg<M.Numerator>(num), Arg<M.Denominator>(den));
                    return new() { new M.Delimiter(
                        new M.DelimiterProperties(new M.BeginChar { Val = "(" }, new M.EndChar { Val = ")" }),
                        Base(new[] { frac })) };
                }
                case "underbrace":
                case "overbrace":
                {
                    var body = ParseGroupArg();
                    var chr = cmd == "underbrace" ? "⏟" : "⏞";
                    return new() { new M.GroupChar(
                        new M.GroupCharProperties(
                            new M.AccentChar { Val = chr },
                            new M.Position { Val = cmd == "underbrace" ? M.VerticalJustificationValues.Bottom : M.VerticalJustificationValues.Top }),
                        Base(body)) };
                }
                case "begin":
                {
                    var envName = ParseBracedRaw();
                    return ParseEnvironment(envName);
                }
                case "sqrt":
                {
                    List<OpenXmlElement>? deg = null;
                    if (More && Cur.Kind == Kind.LBracket) { _i++; deg = ParseSequence(SeqStop.Bracket); }
                    var body = ParseGroupArg();
                    if (deg != null)
                        return new() { new M.Radical(Arg<M.Degree>(deg), Base(body)) };
                    return new() { new M.Radical(
                        new M.RadicalProperties(new M.HideDegree { Val = M.BooleanValues.One }),
                        new M.Degree(), Base(body)) };
                }
                case "left":
                {
                    string beg = ReadDelim();
                    var inner = ParseSequence(SeqStop.Right);
                    string end = ")";
                    if (More && Cur.Kind == Kind.Cmd && Cur.Text == "right") { _i++; end = ReadDelim(); }
                    return new() { new M.Delimiter(
                        new M.DelimiterProperties(new M.BeginChar { Val = beg }, new M.EndChar { Val = end }),
                        Base(inner)) };
                }
                case "text": case "mathrm": case "operatorname": case "mathbf":
                case "mathsf": case "mathtt": case "mathit": case "mathcal": case "mathbb":
                    return new() { TextRun(ParseBracedRaw(), upright: cmd != "mathit") };
                case "hat": case "bar": case "vec": case "tilde": case "dot": case "ddot": case "overline":
                {
                    var inner = ParseBracedRaw();
                    var comb = cmd switch { "hat" => "̂", "bar" or "overline" => "̄",
                        "vec" => "⃗", "tilde" => "̃", "dot" => "̇", "ddot" => "̈", _ => "" };
                    return new() { TextRun(inner + comb) };
                }
                case "limits": return new(); // just skip \limits, we assume under/over behavior based on command
                case "left.": return new();
                case "boxed":
                {
                    // \boxed{X} highlights a final answer with a real border box — common in AI-chat
                    // math for "here's the answer". OMML's exact equivalent is <m:borderBox>; without
                    // this case it fell through to the "unknown command" fallback below, which would
                    // have emitted the literal word "boxed" as text next to the (otherwise fine)
                    // content, instead of an actual box around it. Default border-box properties (no
                    // BorderBoxProperties supplied) show all four sides, matching \boxed's plain box.
                    var body = ParseGroupArg();
                    return new() { new M.BorderBox(Base(body)) };
                }
                case "overset":
                case "stackrel":
                {
                    // \overset{above}{base} / \stackrel{above}{base}: `above` set over `base`, no bar.
                    // OMML m:limUpp (limit-upper) is the faithful mapping — a base with material above.
                    var above = ParseGroupArg();
                    var overBase = ParseGroupArg();
                    return new() { new M.LimitUpper(Base(overBase), Arg<M.Limit>(above)) };
                }
                case "underset":
                {
                    var below = ParseGroupArg();
                    var underBase = ParseGroupArg();
                    return new() { new M.LimitLower(Base(underBase), Arg<M.Limit>(below)) };
                }
                case "xrightarrow":
                case "xleftarrow":
                {
                    // Labeled arrow (reactions, mappings): optional [below] then {above}. OMML has no
                    // stretchy-arrow-with-label primitive, so stack the label(s) over/under a plain
                    // arrow glyph via limUpp/limLow — reads correctly even if the arrow doesn't grow.
                    List<OpenXmlElement>? below = null;
                    if (More && Cur.Kind == Kind.LBracket) { _i++; below = ParseSequence(SeqStop.Bracket); }
                    var above = ParseGroupArg();
                    var arrowRun = TextRun(cmd == "xrightarrow" ? "→" : "←");
                    OpenXmlElement stacked = new M.LimitUpper(Base(new OpenXmlElement[] { arrowRun }), Arg<M.Limit>(above));
                    if (below is not null)
                        stacked = new M.LimitLower(Base(new[] { stacked }), Arg<M.Limit>(below));
                    return new() { stacked };
                }
                case "cancel":
                case "bcancel":
                case "xcancel":
                {
                    // OMML has no diagonal-strike primitive. Overlay a combining long solidus (U+0338)
                    // on each visible char of the argument — the standard TeX→text approximation —
                    // so a worked-solution cancellation reads as "crossed out" without losing content.
                    var raw = ParseBracedRaw();
                    var sb = new System.Text.StringBuilder();
                    foreach (var ch in raw)
                    {
                        sb.Append(ch);
                        if (!char.IsWhiteSpace(ch)) sb.Append('̸');
                    }
                    return new() { TextRun(sb.ToString()) };
                }
                case "substack":
                    // Multi-line material (typically under \sum): \\-separated lines as a 1-column stack.
                    return new() { ParseBracedStack() };
                case "pmod":
                {
                    var n = ParseGroupArg();
                    var res = new List<OpenXmlElement> { TextRun(" (mod ", upright: true) };
                    res.AddRange(n);
                    res.Add(TextRun(")", upright: true));
                    return res;
                }
                case "bmod":
                    return new() { TextRun(" mod ", upright: true) };
                case "pod":
                {
                    var n = ParseGroupArg();
                    var res = new List<OpenXmlElement> { TextRun(" (", upright: true) };
                    res.AddRange(n);
                    res.Add(TextRun(")", upright: true));
                    return res;
                }
                case "tag":
                {
                    // Equation tag/number, e.g. \tag{3.1}. Rendered inline as " (3.1)" — Word doesn't
                    // carry LaTeX's right-flush equation numbering, but the label itself is preserved.
                    var n = ParseBracedRaw();
                    return new() { TextRun(" (" + n + ")") };
                }
            }

            if (Nary.TryGetValue(cmd, out var nary))
                return new() { ParseNary(nary.Char, nary.UnderOver) };
            if (Symbols.TryGetValue(cmd, out var sym))
                return new() { TextRun(sym) };
            if (Functions.Contains(cmd))
            {
                var run = TextRun(cmd, upright: true);
                if (More && Cur.Kind == Kind.Sub)
                {
                    _i++;
                    var lim = ParseAtom();
                    return new() { new M.LimitLower(
                        new M.LimitLowerProperties(),
                        Base(new[] { run }),
                        Arg<M.Limit>(lim)) };
                }
                return new() { run };
            }

            // Unknown command — emit its name so nothing silently disappears.
            return new() { TextRun(cmd) };
        }

        // Reads a {...} group whose content is \\-separated lines and stacks them as a single-column
        // matrix — used by \substack (and a reasonable fallback for any "lines stacked vertically").
        private M.Matrix ParseBracedStack()
        {
            if (More && Cur.Kind == Kind.LBrace) _i++; // opening {
            var rows = new List<M.MatrixRow>();
            var curCol = new List<OpenXmlElement>();
            void EndRow()
            {
                var r = new M.MatrixRow();
                r.Append(Base(curCol));
                rows.Add(r);
                curCol = new List<OpenXmlElement>();
            }
            while (More && Cur.Kind != Kind.RBrace)
            {
                if (Cur.Kind == Kind.Cmd && (Cur.Text == "\\" || Cur.Text == "\\\\")) { _i++; EndRow(); continue; }
                curCol.AddRange(ParseAtomWithScripts());
            }
            if (More && Cur.Kind == Kind.RBrace) _i++; // closing }
            if (curCol.Count > 0 || rows.Count == 0) EndRow();

            var mat = new M.Matrix();
            mat.Append(new M.MatrixProperties(new M.HidePlaceholder { Val = M.BooleanValues.One }));
            foreach (var r in rows) mat.Append(r);
            return mat;
        }

        private List<OpenXmlElement> ParseEnvironment(string envName)
        {
            var rows = new List<M.MatrixRow>();
            var curRow = new List<M.Base>();
            var curCol = new List<OpenXmlElement>();

            void EndCol() { curRow.Add(Base(curCol)); curCol.Clear(); }
            void EndRow() { EndCol(); var r = new M.MatrixRow(); foreach (var c in curRow) r.Append(c); rows.Add(r); curRow.Clear(); }
            // \begin{array}{cc} carries a column-spec group; consume the WHOLE {…} so it doesn't
            // leak into the first cell. ParseBracedRaw reads the full braced group itself — do NOT
            // pre-advance past the '{' first (that double-consumes and corrupts the parse).
            if (envName == "array" && More && Cur.Kind == Kind.LBrace)
                ParseBracedRaw();

            while (More)
            {
                if (Cur.Kind == Kind.Cmd && Cur.Text == "end")
                {
                    _i++;
                    var endEnv = ParseBracedRaw();
                    if (curCol.Count > 0 || curRow.Count > 0) EndRow();
                    break;
                }
                if (Cur.Kind == Kind.Cmd && (Cur.Text == "\\" || Cur.Text == "\\\\")) { _i++; EndRow(); continue; }
                if (Cur.Kind == Kind.Amp) { _i++; EndCol(); continue; }
                curCol.AddRange(ParseAtomWithScripts());
            }

            // Normalize column counts so Word doesn't complain about jagged matrices
            int maxCols = 0;
            foreach (var r in rows) maxCols = Math.Max(maxCols, r.ChildElements.Count);
            foreach (var r in rows)
            {
                while (r.ChildElements.Count < maxCols) r.Append(Base(new List<OpenXmlElement>()));
            }

            var mat = new M.Matrix();
            mat.Append(new M.MatrixProperties(
                new M.HidePlaceholder { Val = M.BooleanValues.One }
            ));
            foreach (var r in rows) mat.Append(r);

            // NOTE: build the wrapping Base ONLY inside the delimiter cases. Creating it
            // unconditionally parents `mat`, so the bare cases that return `mat` directly would then
            // append an already-parented element and throw (→ literal fallback). One env runs per
            // call, so wrapping in the chosen case parents `mat` exactly once.
            M.Base Wrapped() => Base(new[] { mat });
            switch (envName)
            {
                case "matrix":
                case "array": return new() { mat };
                case "pmatrix": return new() { new M.Delimiter(new M.DelimiterProperties(new M.BeginChar { Val = "(" }, new M.EndChar { Val = ")" }), Wrapped()) };
                case "bmatrix": return new() { new M.Delimiter(new M.DelimiterProperties(new M.BeginChar { Val = "[" }, new M.EndChar { Val = "]" }), Wrapped()) };
                case "vmatrix": return new() { new M.Delimiter(new M.DelimiterProperties(new M.BeginChar { Val = "|" }, new M.EndChar { Val = "|" }), Wrapped()) };
                case "Vmatrix": return new() { new M.Delimiter(new M.DelimiterProperties(new M.BeginChar { Val = "‖" }, new M.EndChar { Val = "‖" }), Wrapped()) };
                case "cases": return new() { new M.Delimiter(new M.DelimiterProperties(new M.BeginChar { Val = "{" }, new M.EndChar { Val = "" }), Wrapped()) };
                default: return new() { mat };
            }
        }

        private M.Nary ParseNary(string chr, bool underOver)
        {
            List<OpenXmlElement>? sub = null, sup = null;
            while (More && (Cur.Kind == Kind.Sup || Cur.Kind == Kind.Sub))
            {
                bool isSup = Cur.Kind == Kind.Sup; _i++;
                var arg = ParseAtom();
                if (isSup) sup = arg; else sub = arg;
            }
            var baseEls = More && Cur.Kind != Kind.RBrace ? ParseAtomWithScripts() : new List<OpenXmlElement>();
            if (baseEls.Count == 0) baseEls.Add(TextRun("\u200B")); // Hide dotted placeholder box for empty base


            var np = new M.NaryProperties(
                new M.AccentChar { Val = chr },
                new M.LimitLocation { Val = underOver ? M.LimitLocationValues.UnderOver : M.LimitLocationValues.SubscriptSuperscript },
                new M.HideSubArgument { Val = sub == null ? M.BooleanValues.One : M.BooleanValues.Zero },
                new M.HideSuperArgument { Val = sup == null ? M.BooleanValues.One : M.BooleanValues.Zero });
            return new M.Nary(np,
                Arg<M.SubArgument>(sub ?? new()),
                Arg<M.SuperArgument>(sup ?? new()),
                Base(baseEls));
        }

        // Resolves the delimiter after \left or \right (a char or a named command).
        private string ReadDelim()
        {
            if (!More) return "";
            var t = Cur; _i++;
            if (t.Kind == Kind.Cmd)
            {
                return t.Text switch
                {
                    "langle" => "⟨", "rangle" => "⟩", "lvert" or "rvert" or "vert" => "|",
                    "lVert" or "rVert" or "Vert" => "‖", "lfloor" => "⌊", "rfloor" => "⌋",
                    "lceil" => "⌈", "rceil" => "⌉", "{" => "{", "}" => "}", _ => ""
                };
            }
            return t.Text == "." ? "" : t.Text; // '.' = null delimiter
        }
    }

    // ---- lookup tables -----------------------------------------------------------------------

    private static readonly Dictionary<string, (string Char, bool UnderOver)> Nary = new()
    {
        ["sum"] = ("∑", true), ["prod"] = ("∏", true), ["coprod"] = ("∐", true),
        ["bigcup"] = ("⋃", true), ["bigcap"] = ("⋂", true), ["bigvee"] = ("⋁", true),
        ["bigwedge"] = ("⋀", true), ["bigoplus"] = ("⨁", true), ["bigotimes"] = ("⨂", true),
        ["int"] = ("∫", false), ["iint"] = ("∬", false), ["iiint"] = ("∭", false),
        ["oint"] = ("∮", false),
    };

    private static readonly HashSet<string> Functions = new()
    {
        "sin","cos","tan","cot","sec","csc","sinh","cosh","tanh","coth","arcsin","arccos","arctan",
        "log","ln","lg","exp","lim","limsup","liminf","max","min","sup","inf","det","dim","ker",
        "deg","gcd","hom","arg","Pr","mod",
    };

    private static readonly Dictionary<string, string> Symbols = new()
    {
        // lowercase greek
        ["alpha"]="α",["beta"]="β",["gamma"]="γ",["delta"]="δ",["epsilon"]="ε",
        ["varepsilon"]="ε",["zeta"]="ζ",["eta"]="η",["theta"]="θ",["vartheta"]="ϑ",
        ["iota"]="ι",["kappa"]="κ",["lambda"]="λ",["mu"]="μ",["nu"]="ν",
        ["xi"]="ξ",["pi"]="π",["varpi"]="ϖ",["rho"]="ρ",["varrho"]="ϱ",
        ["sigma"]="σ",["varsigma"]="ς",["tau"]="τ",["upsilon"]="υ",["phi"]="φ",
        ["varphi"]="ϕ",["chi"]="χ",["psi"]="ψ",["omega"]="ω",
        // uppercase greek
        ["Gamma"]="Γ",["Delta"]="Δ",["Theta"]="Θ",["Lambda"]="Λ",["Xi"]="Ξ",
        ["Pi"]="Π",["Sigma"]="Σ",["Upsilon"]="Υ",["Phi"]="Φ",["Psi"]="Ψ",["Omega"]="Ω",
        // operators & relations
        ["times"]="×",["cdot"]="⋅",["div"]="÷",["pm"]="±",["mp"]="∓",
        ["leq"]="≤",["le"]="≤",["geq"]="≥",["ge"]="≥",["neq"]="≠",["ne"]="≠",
        ["approx"]="≈",["equiv"]="≡",["cong"]="≅",["sim"]="∼",["simeq"]="≃",
        ["propto"]="∝",["ll"]="≪",["gg"]="≫",["subset"]="⊂",["subseteq"]="⊆",
        ["supset"]="⊃",["supseteq"]="⊇",["in"]="∈",["notin"]="∉",["ni"]="∋",
        ["cup"]="∪",["cap"]="∩",["setminus"]="∖",["emptyset"]="∅",["varnothing"]="∅",
        ["infty"]="∞",["partial"]="∂",["nabla"]="∇",["forall"]="∀",["exists"]="∃",
        ["neg"]="¬",["land"]="∧",["wedge"]="∧",["lor"]="∨",["vee"]="∨",
        ["oplus"]="⊕",["otimes"]="⊗",["odot"]="⊙",["circ"]="∘",["bullet"]="∙",
        ["star"]="⋆",["ast"]="∗",["dagger"]="†",["angle"]="∠",["perp"]="⊥",
        ["parallel"]="∥",["mid"]="∣",["cdots"]="⋯",["ldots"]="…",["dots"]="…",
        ["vdots"]="⋮",["ddots"]="⋱",["prime"]="′",["hbar"]="ℏ",["ell"]="ℓ",
        ["Re"]="ℜ",["Im"]="ℑ",["aleph"]="ℵ",["wp"]="℘",["degree"]="°",
        // arrows
        ["rightarrow"]="→",["to"]="→",["leftarrow"]="←",["gets"]="←",
        ["leftrightarrow"]="↔",["Rightarrow"]="⇒",["implies"]="⇒",["Leftarrow"]="⇐",
        ["Leftrightarrow"]="⇔",["iff"]="⇔",["mapsto"]="↦",["uparrow"]="↑",["downarrow"]="↓",
        // sets / misc
        ["mathbbR"]="ℝ",["Real"]="ℝ",["quad"]=" ",["qquad"]=" ",["cdotp"]="·",
    };
}
