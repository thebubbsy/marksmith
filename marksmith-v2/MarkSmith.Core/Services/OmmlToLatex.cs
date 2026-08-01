using System.Text;
using DocumentFormat.OpenXml;
using M = DocumentFormat.OpenXml.Math;

namespace MdToPdf.Services;

// The inverse of LatexToOmml: turns Office MathML (OMML) back into LaTeX source so a DOCX exported
// by Marksmith can be imported back to Markdown with its equations intact. LatexToOmml is lossy in
// the general case (it drops whitespace and normalises equivalent commands), so this converter emits
// a CANONICAL LaTeX form: no insignificant whitespace, scripts braced only when multi-character, and
// each Unicode symbol mapped back to a single chosen command. Any Markdown written in that canonical
// form therefore round-trips byte-for-byte (MD -> DOCX -> MD); equivalent non-canonical spellings
// converge to it on the first pass. Anything unrecognised degrades to its literal text so content is
// never dropped.
internal static class OmmlToLatex
{
    // ---- public entry ------------------------------------------------------------------------

    public static string Convert(OpenXmlElement math) => ConvertChildren(math);

    // ---- traversal ---------------------------------------------------------------------------

    private static string ConvertChildren(OpenXmlElement node)
    {
        var sb = new StringBuilder();
        foreach (var child in node.ChildElements)
            sb.Append(ConvertNode(child));
        return sb.ToString();
    }

    private static string ConvertNode(OpenXmlElement node)
    {
        switch (node)
        {
            case M.OfficeMath om:
                return ConvertChildren(om);

            case M.Run r:
                return ConvertRun(r);

            case M.Nary nary:
                return ConvertNary(nary);

            case M.SubSuperscript ss:
            {
                var b = ConvertChildren((OpenXmlElement?)ss.GetFirstChild<M.Base>() ?? ss);
                var sub = ConvertChildren(ss.GetFirstChild<M.SubArgument>() ?? new M.SubArgument());
                var sup = ConvertChildren(ss.GetFirstChild<M.SuperArgument>() ?? new M.SuperArgument());
                return b + Script("_", sub) + Script("^", sup);
            }

            case M.Subscript sub:
            {
                var b = ConvertChildren((OpenXmlElement?)sub.GetFirstChild<M.Base>() ?? sub);
                var s = ConvertChildren(sub.GetFirstChild<M.SubArgument>() ?? new M.SubArgument());
                return b + Script("_", s);
            }

            case M.Superscript sup:
            {
                var b = ConvertChildren((OpenXmlElement?)sup.GetFirstChild<M.Base>() ?? sup);
                var s = ConvertChildren(sup.GetFirstChild<M.SuperArgument>() ?? new M.SuperArgument());
                return b + Script("^", s);
            }

            case M.Delimiter d:
                return ConvertDelimiter(d);

            case M.Matrix m:
                return ConvertMatrix(m);

            case M.Fraction f:
            {
                var num = ConvertChildren(f.GetFirstChild<M.Numerator>() ?? new M.Numerator());
                var den = ConvertChildren(f.GetFirstChild<M.Denominator>() ?? new M.Denominator());
                // A no-bar fraction is \binom (LatexToOmml wraps it in (...) delimiters, but when we
                // see the bare fraction itself there's no bar -> binomial).
                var noBar = f.GetFirstChild<M.FractionProperties>()?
                    .GetFirstChild<M.FractionType>()?.Val?.Value == M.FractionTypeValues.NoBar;
                return (noBar ? "\\binom{" : "\\frac{") + num + "}{" + den + "}";
            }

            case M.Radical rad:
            {
                var body = ConvertChildren(rad.GetFirstChild<M.Base>() ?? new M.Base());
                var hideDeg = rad.GetFirstChild<M.RadicalProperties>()?
                    .GetFirstChild<M.HideDegree>()?.Val?.Value == M.BooleanValues.One;
                var deg = rad.GetFirstChild<M.Degree>();
                var degText = deg is null ? "" : ConvertChildren(deg);
                if (!hideDeg && degText.Length > 0) return "\\sqrt[" + degText + "]{" + body + "}";
                return "\\sqrt{" + body + "}";
            }

            case M.LimitUpper lu:
            {
                var b = ConvertChildren(lu.GetFirstChild<M.Base>() ?? new M.Base());
                var lim = ConvertChildren(lu.GetFirstChild<M.Limit>() ?? new M.Limit());
                return "\\overset{" + lim + "}{" + b + "}";
            }

            case M.LimitLower ll:
            {
                var b = ConvertChildren(ll.GetFirstChild<M.Base>() ?? new M.Base());
                var lim = ConvertChildren(ll.GetFirstChild<M.Limit>() ?? new M.Limit());
                return "\\underset{" + lim + "}{" + b + "}";
            }

            case M.GroupChar gc:
            {
                var b = ConvertChildren(gc.GetFirstChild<M.Base>() ?? new M.Base());
                var chr = gc.GetFirstChild<M.GroupCharProperties>()?
                    .GetFirstChild<M.AccentChar>()?.Val?.Value ?? "⏟";
                var pos = gc.GetFirstChild<M.GroupCharProperties>()?
                    .GetFirstChild<M.Position>()?.Val?.Value;
                var cmd = pos == M.VerticalJustificationValues.Top ? "\\overbrace" : "\\underbrace";
                return cmd + "{" + b + "}";
            }

            case M.BorderBox bb:
            {
                var b = ConvertChildren(bb.GetFirstChild<M.Base>() ?? new M.Base());
                return "\\boxed{" + b + "}";
            }

            case M.Base baseEl:
                return ConvertChildren(baseEl);

            // Structural/property containers contribute nothing directly.
            case M.NaryProperties:
            case M.DelimiterProperties:
            case M.MatrixProperties:
            case M.FractionProperties:
            case M.RadicalProperties:
            case M.GroupCharProperties:
            case M.LimitUpperProperties:
            case M.LimitLowerProperties:
            case M.SubSuperscriptProperties:
                return "";

            default:
                // Unknown construct: recurse so its text content is never lost.
                return node.HasChildren ? ConvertChildren(node) : (node as M.Text)?.Text ?? "";
        }
    }

    // ---- runs --------------------------------------------------------------------------------

    private static string ConvertRun(M.Run r)
    {
        var text = r.GetFirstChild<M.Text>()?.Text ?? "";
        var style = r.GetFirstChild<M.RunProperties>()?.GetFirstChild<M.Style>()?.Val?.Value;
        var upright = style == M.StyleValues.Plain;

        if (upright)
        {
            // Upright multi-char text came from \text / \mathrm / \operatorname; a known function
            // name came from \sin, \log, ... — reverse each to its canonical command.
            if (Functions.Contains(text)) return "\\" + text;
            if (text.Length > 1) return "\\text{" + text + "}";
        }

        return ReverseMapSymbol(text);
    }

    private static string ReverseMapSymbol(string text)
    {
        if (text.Length == 1 && SymbolReverse.TryGetValue(text, out var cmd)) return cmd;
        return text;
    }

    // ---- n-ary (sum/integral/...) ------------------------------------------------------------

    private static string ConvertNary(M.Nary nary)
    {
        var props = nary.GetFirstChild<M.NaryProperties>();
        var chr = props?.GetFirstChild<M.AccentChar>()?.Val?.Value ?? "∑";
        var cmd = NaryReverse.TryGetValue(chr, out var c) ? c : "\\sum";
        var hideSub = props?.GetFirstChild<M.HideSubArgument>()?.Val?.Value == M.BooleanValues.One;
        var hideSup = props?.GetFirstChild<M.HideSuperArgument>()?.Val?.Value == M.BooleanValues.One;

        var sb = new StringBuilder(cmd);
        if (!hideSub)
        {
            var sub = ConvertChildren(nary.GetFirstChild<M.SubArgument>() ?? new M.SubArgument());
            if (sub.Length > 0) sb.Append(Script("_", sub));
        }
        if (!hideSup)
        {
            var sup = ConvertChildren(nary.GetFirstChild<M.SuperArgument>() ?? new M.SuperArgument());
            if (sup.Length > 0) sb.Append(Script("^", sup));
        }

        var baseEl = nary.GetFirstChild<M.Base>();
        if (baseEl is not null)
        {
            var baseText = ConvertChildren(baseEl);
            // LatexToOmml inserts a zero-width space to hide the empty-base placeholder; drop it.
            baseText = baseText.Replace("\u200B", "");
            sb.Append(baseText);
        }
        return sb.ToString();
    }

    // ---- delimiters & matrices ---------------------------------------------------------------

    private static string ConvertDelimiter(M.Delimiter d)
    {
        var props = d.GetFirstChild<M.DelimiterProperties>();
        var beg = props?.GetFirstChild<M.BeginChar>()?.Val?.Value ?? "(";
        var end = props?.GetFirstChild<M.EndChar>()?.Val?.Value ?? ")";
        var baseEl = d.GetFirstChild<M.Base>();

        // A delimiter wrapping a matrix is a matrix environment (\begin{bmatrix}...), not \left..\right.
        var matrix = baseEl?.Descendants<M.Matrix>().FirstOrDefault();
        if (matrix is not null)
        {
            var env = EnvFromDelimiters(beg, end);
            return "\\begin{" + env + "}" + ConvertMatrix(matrix) + "\\end{" + env + "}";
        }

        var content = baseEl is null ? "" : ConvertChildren(baseEl);
        return "\\left" + beg + content + "\\right" + end;
    }

    private static string ConvertMatrix(M.Matrix m)
    {
        var rows = new List<string>();
        foreach (var mr in m.Elements<M.MatrixRow>())
        {
            var cells = new List<string>();
            foreach (var e in mr.Elements<M.Base>())
                cells.Add(ConvertChildren(e));
            rows.Add(string.Join(" & ", cells));
        }
        return string.Join(" \\\\ ", rows);
    }

    private static string EnvFromDelimiters(string beg, string end) => (beg, end) switch
    {
        ("[", "]") => "bmatrix",
        ("(", ")") => "pmatrix",
        ("|", "|") => "vmatrix",
        ("‖", "‖") => "Vmatrix",
        ("{", "") => "cases",
        _ => "matrix",
    };

    // ---- helpers -----------------------------------------------------------------------------

    // Braces a script argument only when it has more than one character — the canonical LaTeX style
    // (p_i but x_{10}), matching the form LatexToOmml round-trips stably.
    private static string Script(string op, string arg) =>
        arg.Length == 0 ? "" : op + (arg.Length == 1 ? arg : "{" + arg + "}");

    // Upright function names emitted by LatexToOmml's Functions set.
    private static readonly HashSet<string> Functions = new()
    {
        "sin","cos","tan","cot","sec","csc","sinh","cosh","tanh","coth","arcsin","arccos","arctan",
        "log","ln","lg","exp","lim","limsup","liminf","max","min","sup","inf","det","dim","ker",
        "deg","gcd","hom","arg","Pr","mod",
    };

    private static readonly Dictionary<string, string> NaryReverse = new()
    {
        ["∑"] = "\\sum", ["∏"] = "\\prod", ["∐"] = "\\coprod",
        ["⋃"] = "\\bigcup", ["⋂"] = "\\bigcap", ["⋁"] = "\\bigvee",
        ["⋀"] = "\\bigwedge", ["⨁"] = "\\bigoplus", ["⨂"] = "\\bigotimes",
        ["∫"] = "\\int", ["∬"] = "\\iint", ["∭"] = "\\iiint", ["∮"] = "\\oint",
    };

    // Canonical reverse symbol table (inverse of LatexToOmml.Symbols; where several commands share a
    // glyph, one canonical command is chosen so the round-trip is deterministic).
    private static readonly Dictionary<string, string> SymbolReverse = new()
    {
        // operators & relations
        ["×"]="\\times",["⋅"]="\\cdot",["÷"]="\\div",["±"]="\\pm",["∓"]="\\mp",
        ["≤"]="\\leq",["≥"]="\\geq",["≠"]="\\neq",["≈"]="\\approx",["≡"]="\\equiv",
        ["≅"]="\\cong",["∼"]="\\sim",["≃"]="\\simeq",["∝"]="\\propto",["≪"]="\\ll",["≫"]="\\gg",
        ["⊂"]="\\subset",["⊆"]="\\subseteq",["⊃"]="\\supset",["⊇"]="\\supseteq",
        ["∈"]="\\in",["∉"]="\\notin",["∋"]="\\ni",
        ["∪"]="\\cup",["∩"]="\\cap",["∖"]="\\setminus",["∅"]="\\emptyset",
        ["∞"]="\\infty",["∂"]="\\partial",["∇"]="\\nabla",["∀"]="\\forall",["∃"]="\\exists",
        ["¬"]="\\neg",["∧"]="\\land",["∨"]="\\lor",
        ["⊕"]="\\oplus",["⊗"]="\\otimes",["⊙"]="\\odot",["∘"]="\\circ",["∙"]="\\bullet",
        ["⋆"]="\\star",["∗"]="\\ast",["†"]="\\dagger",["∠"]="\\angle",["⊥"]="\\perp",
        ["∥"]="\\parallel",["∣"]="\\mid",["⋯"]="\\cdots",["…"]="\\ldots",
        ["⋮"]="\\vdots",["⋱"]="\\ddots",["′"]="\\prime",["ℏ"]="\\hbar",["ℓ"]="\\ell",
        ["ℜ"]="\\Re",["ℑ"]="\\Im",["ℵ"]="\\aleph",["℘"]="\\wp",["°"]="\\degree",
        // arrows
        ["→"]="\\rightarrow",["←"]="\\leftarrow",["↔"]="\\leftrightarrow",
        ["⇒"]="\\Rightarrow",["⇐"]="\\Leftarrow",["⇔"]="\\Leftrightarrow",
        ["↦"]="\\mapsto",["↑"]="\\uparrow",["↓"]="\\downarrow",
        // lowercase greek
        ["α"]="\\alpha",["β"]="\\beta",["γ"]="\\gamma",["δ"]="\\delta",["ε"]="\\epsilon",
        ["ζ"]="\\zeta",["η"]="\\eta",["θ"]="\\theta",["ϑ"]="\\vartheta",
        ["ι"]="\\iota",["κ"]="\\kappa",["λ"]="\\lambda",["μ"]="\\mu",["ν"]="\\nu",
        ["ξ"]="\\xi",["π"]="\\pi",["ϖ"]="\\varpi",["ρ"]="\\rho",["ϱ"]="\\varrho",
        ["σ"]="\\sigma",["ς"]="\\varsigma",["τ"]="\\tau",["υ"]="\\upsilon",["φ"]="\\phi",
        ["ϕ"]="\\varphi",["χ"]="\\chi",["ψ"]="\\psi",["ω"]="\\omega",
        // uppercase greek
        ["Γ"]="\\Gamma",["Δ"]="\\Delta",["Θ"]="\\Theta",["Λ"]="\\Lambda",["Ξ"]="\\Xi",
        ["Π"]="\\Pi",["Σ"]="\\Sigma",["Υ"]="\\Upsilon",["Φ"]="\\Phi",["Ψ"]="\\Psi",["Ω"]="\\Omega",
    };
}
