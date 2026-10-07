using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using M = DocumentFormat.OpenXml.Math;

namespace MarkSmith.Services;

// Turns LaTeX into presentation MathML for formats that can't run KaTeX (EPUB readers have no
// script). It doesn't parse LaTeX itself: LatexToOmml already does that well for DOCX, so this
// walks the OMML tree it builds and writes the matching MathML element for each construct. One
// parser means an equation that exports correctly to Word exports correctly to an e-book too.
// The original source rides along as an application/x-tex annotation, so a reader that can't draw
// MathML (or a screen reader) still has the exact text.
internal static class LatexToMathMl
{
    public const string Namespace = "http://www.w3.org/1998/Math/MathML";

    public static string Convert(string? latex, bool display)
    {
        var src = (latex ?? string.Empty).Trim();
        var sb = new StringBuilder();
        sb.Append("<math xmlns=\"").Append(Namespace).Append('"');
        sb.Append(display ? " display=\"block\"" : " display=\"inline\"");
        sb.Append(" alttext=\"").Append(Attr(src)).Append("\">");
        sb.Append("<semantics><mrow>");
        WriteChildren(sb, LatexToOmml.Build(src));
        sb.Append("</mrow><annotation encoding=\"application/x-tex\">").Append(Text(src)).Append("</annotation>");
        sb.Append("</semantics></math>");
        return sb.ToString();
    }

    // LatexToOmml emits a run per token, so "12.5" arrives as four runs. Adjacent runs with the
    // same style are merged first, or every digit would become its own <mn>.
    private static void WriteChildren(StringBuilder sb, OpenXmlElement parent)
    {
        M.Run? pending = null;
        foreach (var child in parent.ChildElements)
        {
            if (child is M.Run run)
            {
                if (pending is not null && StyleOf(pending) == StyleOf(run))
                {
                    var merged = new M.Run();
                    if (pending.GetFirstChild<M.RunProperties>() is { } props) merged.Append(props.CloneNode(true));
                    merged.Append(new M.Text(RunText(pending) + RunText(run)));
                    pending = merged;
                }
                else
                {
                    if (pending is not null) WriteRun(sb, pending);
                    pending = run;
                }
                continue;
            }
            if (pending is not null) { WriteRun(sb, pending); pending = null; }
            Write(sb, child);
        }
        if (pending is not null) WriteRun(sb, pending);
    }

    private static M.StyleValues? StyleOf(M.Run run) =>
        run.GetFirstChild<M.RunProperties>()?.GetFirstChild<M.Style>()?.Val?.Value;

    private static string RunText(M.Run run) => string.Concat(run.Elements<M.Text>().Select(t => t.Text));

    // Writes the children wrapped in one <mrow>, so a multi-part argument (a numerator like "a+b")
    // is a single MathML argument, which is what mfrac/msup/etc. require.
    private static void Row(StringBuilder sb, OpenXmlElement? parent)
    {
        sb.Append("<mrow>");
        if (parent is not null) WriteChildren(sb, parent);
        sb.Append("</mrow>");
    }

    private static void Write(StringBuilder sb, OpenXmlElement el)
    {
        switch (el)
        {
            case M.Run run:
                WriteRun(sb, run);
                break;

            case M.Fraction f:
            {
                var noBar = f.FractionProperties?.FractionType?.Val?.Value == M.FractionTypeValues.NoBar;
                sb.Append(noBar ? "<mfrac linethickness=\"0\">" : "<mfrac>");
                Row(sb, f.Numerator);
                Row(sb, f.Denominator);
                sb.Append("</mfrac>");
                break;
            }

            case M.Superscript s:
                sb.Append("<msup>"); Row(sb, s.Base); Row(sb, s.SuperArgument); sb.Append("</msup>");
                break;

            case M.Subscript s:
                sb.Append("<msub>"); Row(sb, s.Base); Row(sb, s.SubArgument); sb.Append("</msub>");
                break;

            case M.SubSuperscript s:
                sb.Append("<msubsup>"); Row(sb, s.Base); Row(sb, s.SubArgument); Row(sb, s.SuperArgument); sb.Append("</msubsup>");
                break;

            case M.Radical r:
            {
                var hide = r.RadicalProperties?.HideDegree?.Val?.Value == M.BooleanValues.One
                           || r.Degree is null || !r.Degree.ChildElements.Any(c => c is not M.ControlProperties);
                if (hide) { sb.Append("<msqrt>"); Row(sb, r.Base); sb.Append("</msqrt>"); }
                else { sb.Append("<mroot>"); Row(sb, r.Base); Row(sb, r.Degree); sb.Append("</mroot>"); }
                break;
            }

            case M.Nary n:
            {
                var props = n.NaryProperties;
                var chr = props?.AccentChar?.Val?.Value;
                if (string.IsNullOrEmpty(chr)) chr = "∫";
                var underOver = props?.LimitLocation?.Val?.Value == M.LimitLocationValues.UnderOver;
                var hideSub = props?.HideSubArgument?.Val?.Value == M.BooleanValues.One;
                var hideSup = props?.HideSuperArgument?.Val?.Value == M.BooleanValues.One;
                var op = $"<mo largeop=\"true\" movablelimits=\"false\">{Text(chr)}</mo>";
                sb.Append("<mrow>");
                if (hideSub && hideSup) sb.Append(op);
                else
                {
                    var tag = (hideSub, hideSup, underOver) switch
                    {
                        (false, false, true) => "munderover",
                        (false, false, false) => "msubsup",
                        (false, true, true) => "munder",
                        (false, true, false) => "msub",
                        (true, false, true) => "mover",
                        _ => "msup",
                    };
                    sb.Append('<').Append(tag).Append('>').Append(op);
                    if (!hideSub) Row(sb, n.SubArgument);
                    if (!hideSup) Row(sb, n.SuperArgument);
                    sb.Append("</").Append(tag).Append('>');
                }
                Row(sb, n.Base);
                sb.Append("</mrow>");
                break;
            }

            case M.Delimiter d:
            {
                var props = d.DelimiterProperties;
                // OMML defaults: "(" and ")" when unset; an empty value means "no delimiter".
                var open = props?.BeginChar?.Val?.Value ?? "(";
                var close = props?.EndChar?.Val?.Value ?? ")";
                var sep = props?.SeparatorChar?.Val?.Value ?? "|";
                sb.Append("<mrow>");
                if (open.Length > 0) sb.Append("<mo fence=\"true\" stretchy=\"true\">").Append(Text(open)).Append("</mo>");
                var first = true;
                foreach (var b in d.Elements<M.Base>())
                {
                    if (!first) sb.Append("<mo separator=\"true\">").Append(Text(sep)).Append("</mo>");
                    Row(sb, b);
                    first = false;
                }
                if (close.Length > 0) sb.Append("<mo fence=\"true\" stretchy=\"true\">").Append(Text(close)).Append("</mo>");
                sb.Append("</mrow>");
                break;
            }

            case M.Matrix mat:
                sb.Append("<mtable>");
                foreach (var row in mat.Elements<M.MatrixRow>())
                {
                    sb.Append("<mtr>");
                    foreach (var cell in row.Elements<M.Base>()) { sb.Append("<mtd>"); Row(sb, cell); sb.Append("</mtd>"); }
                    sb.Append("</mtr>");
                }
                sb.Append("</mtable>");
                break;

            case M.EquationArray eq:
                sb.Append("<mtable columnalign=\"left\">");
                foreach (var line in eq.Elements<M.Base>()) { sb.Append("<mtr><mtd>"); Row(sb, line); sb.Append("</mtd></mtr>"); }
                sb.Append("</mtable>");
                break;

            case M.Accent a:
            {
                var chr = a.AccentProperties?.AccentChar?.Val?.Value;
                if (string.IsNullOrEmpty(chr)) chr = "̂";
                sb.Append("<mover accent=\"true\">"); Row(sb, a.Base);
                sb.Append("<mo>").Append(Text(SpacingAccent(chr))).Append("</mo></mover>");
                break;
            }

            case M.GroupChar g:
            {
                var chr = g.GroupCharProperties?.AccentChar?.Val?.Value;
                if (string.IsNullOrEmpty(chr)) chr = "⏟";
                var top = g.GroupCharProperties?.Position?.Val?.Value == M.VerticalJustificationValues.Top;
                var tag = top ? "mover" : "munder";
                sb.Append('<').Append(tag).Append('>'); Row(sb, g.Base);
                sb.Append("<mo stretchy=\"true\">").Append(Text(chr)).Append("</mo></").Append(tag).Append('>');
                break;
            }

            case M.Bar bar:
            {
                var top = bar.BarProperties?.Position?.Val?.Value == M.VerticalJustificationValues.Top;
                var tag = top ? "mover" : "munder";
                sb.Append('<').Append(tag).Append(" accent=\"true\">"); Row(sb, bar.Base);
                sb.Append("<mo stretchy=\"true\">")
                  .Append(top ? "‾" : "_").Append("</mo></").Append(tag).Append('>');
                break;
            }

            case M.LimitLower l:
                sb.Append("<munder>"); Row(sb, l.Base); Row(sb, l.Limit); sb.Append("</munder>");
                break;

            case M.LimitUpper l:
                sb.Append("<mover>"); Row(sb, l.Base); Row(sb, l.Limit); sb.Append("</mover>");
                break;

            case M.BorderBox b:
                sb.Append("<menclose notation=\"box\">"); Row(sb, b.Base); sb.Append("</menclose>");
                break;

            case M.MathFunction fn:
                sb.Append("<mrow>"); Row(sb, fn.FunctionName);
                sb.Append("<mo>⁡</mo>"); Row(sb, fn.Base); sb.Append("</mrow>");
                break;

            // Property bags carry no content of their own.
            case M.ControlProperties:
            case M.FractionProperties:
            case M.RadicalProperties:
            case M.NaryProperties:
            case M.DelimiterProperties:
            case M.MatrixProperties:
            case M.AccentProperties:
            case M.GroupCharProperties:
            case M.BarProperties:
            case M.LimitLowerProperties:
            case M.LimitUpperProperties:
            case M.BorderBoxProperties:
            case M.FunctionProperties:
            case M.SuperscriptProperties:
            case M.SubscriptProperties:
            case M.SubSuperscriptProperties:
            case M.EquationArrayProperties:
                break;

            default:
                // Anything new LatexToOmml learns to emit still shows its content, just unstyled.
                if (el.HasChildren) Row(sb, el);
                break;
        }
    }

    private static void WriteRun(StringBuilder sb, M.Run run)
    {
        var text = string.Concat(run.Elements<M.Text>().Select(t => t.Text));
        if (text.Length == 0) return;
        var style = run.GetFirstChild<M.RunProperties>()?.GetFirstChild<M.Style>()?.Val?.Value;
        var upright = style == M.StyleValues.Plain || style == M.StyleValues.Bold;
        var variant = style == M.StyleValues.Bold ? "bold"
                    : style == M.StyleValues.BoldItalic ? "bold-italic"
                    : null;

        // Upright multi-letter runs are function names (sin, log) or \text{...}: one token, not
        // one italic letter per character.
        if (upright && text.Trim().Length > 1)
        {
            var trimmed = text.Trim();
            if (trimmed.All(char.IsLetter))
            {
                sb.Append("<mi mathvariant=\"").Append(variant ?? "normal").Append("\">").Append(Text(trimmed)).Append("</mi>");
                return;
            }
            sb.Append("<mtext");
            if (variant is not null) sb.Append(" mathvariant=\"").Append(variant).Append('"');
            sb.Append('>').Append(Text(text.Replace(' ', ' '))).Append("</mtext>");
            return;
        }

        var e = StringInfo.GetTextElementEnumerator(text);
        var number = new StringBuilder();
        void FlushNumber()
        {
            if (number.Length == 0) return;
            sb.Append("<mn>").Append(Text(number.ToString())).Append("</mn>");
            number.Clear();
        }
        while (e.MoveNext())
        {
            var g = (string)e.Current;
            var c = g[0];
            if (char.IsDigit(c) || (c == '.' && number.Length > 0)) { number.Append(g); continue; }
            FlushNumber();
            if (c == '​') continue;                       // LatexToOmml's empty-base placeholder
            if (char.IsWhiteSpace(c))
            {
                if (c == ' ') sb.Append("<mspace width=\"0.25em\"/>");
                continue;                                       // ordinary LaTeX spaces are insignificant
            }
            if (char.IsLetter(c) || char.IsSurrogate(c))
            {
                sb.Append("<mi");
                if (upright) sb.Append(" mathvariant=\"").Append(variant ?? "normal").Append('"');
                else if (variant is not null) sb.Append(" mathvariant=\"").Append(variant).Append('"');
                sb.Append('>').Append(Text(g)).Append("</mi>");
                continue;
            }
            // ASCII hyphen-minus renders as a short hyphen ("-b"); MathML wants the real minus sign.
            sb.Append("<mo>").Append(Text(g == "-" ? "\u2212" : g)).Append("</mo>");
        }
        FlushNumber();
    }

    // MathML accents want the spacing form; OMML stores the combining one.
    private static string SpacingAccent(string chr) => chr switch
    {
        "̂" => "^", "̃" => "~", "̄" or "̅" => "¯", "̇" => "˙",
        "̈" => "¨", "⃗" => "→", "́" => "´", "̀" => "`",
        "̆" => "˘", "̌" => "ˇ",
        _ => chr,
    };

    private static string Text(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Attr(string s) => Text(s).Replace("\"", "&quot;");
}
