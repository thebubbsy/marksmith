using System.Text;

namespace MarkSmith.Services.Email;

/// <summary>Turns everyday LaTeX into readable Unicode (<c>E = mc^2</c> → <c>E = mc²</c>,
/// <c>\frac{a}{b}</c> → <c>a/b</c>, <c>\alpha \le \beta</c> → <c>α ≤ β</c>) for places that can't
/// typeset maths — an email body without a rendered picture of the equation, or its plain-text
/// part. Anything it doesn't know is kept as written, minus the backslash noise.</summary>
public static class LatexText
{
    private const string SupFrom = "0123456789+-=()niax";
    private const string SupTo = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱᵃˣ";
    private const string SubFrom = "0123456789+-=()aeoxijkn";
    private const string SubTo = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₒₓᵢⱼₖₙ";

    public static string ToReadable(string? tex)
    {
        if (string.IsNullOrWhiteSpace(tex)) return "";
        var s = tex.Trim();
        int i = 0;
        var result = Convert(s, ref i, stopAtBrace: false);
        return System.Text.RegularExpressions.Regex.Replace(result, @"[ \t]{2,}", " ").Trim();
    }

    private static string Convert(string s, ref int i, bool stopAtBrace)
    {
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '}' && stopAtBrace) { i++; break; }
            if (c == '{') { i++; sb.Append(Convert(s, ref i, true)); continue; }
            if (c == '^' || c == '_')
            {
                i++;
                var arg = ReadArg(s, ref i);
                sb.Append(Script(arg, c == '^'));
                continue;
            }
            if (c == '\\')
            {
                i++;
                if (i >= s.Length) break;
                if (!char.IsLetter(s[i]))
                {
                    char sym = s[i++];
                    sb.Append(sym switch
                    {
                        ',' or ':' or ';' or ' ' => " ",
                        '!' => "",
                        '\\' => "; ",
                        '{' or '}' or '%' or '$' or '#' or '&' or '_' => sym.ToString(),
                        _ => "",
                    });
                    continue;
                }
                int start = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                var name = s[start..i];
                sb.Append(Command(name, s, ref i));
                continue;
            }
            if (c == '&') { i++; sb.Append(' '); continue; }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static string Command(string name, string s, ref int i)
    {
        switch (name)
        {
            case "frac":
            case "dfrac":
            case "tfrac":
            {
                var num = ReadArg(s, ref i);
                var den = ReadArg(s, ref i);
                return $"{Group(num)}/{Group(den)}";
            }
            case "sqrt":
            {
                SkipSpaces(s, ref i);
                string? degree = null;
                if (i < s.Length && s[i] == '[')
                {
                    int close = s.IndexOf(']', i);
                    if (close > i) { degree = s[(i + 1)..close]; i = close + 1; }
                }
                var arg = ReadArg(s, ref i);
                var root = degree switch { "3" => "∛", "4" => "∜", null => "√", _ => Script(degree, true) + "√" };
                return root + Group(arg);
            }
            case "text": case "textrm": case "mathrm": case "mathbf": case "mathit": case "mathsf":
            case "operatorname": case "textbf": case "textit": case "mathcal": case "boldsymbol": case "mbox":
                return ReadArg(s, ref i);
            case "mathbb":
            {
                var arg = ReadArg(s, ref i);
                return arg switch { "R" => "ℝ", "N" => "ℕ", "Z" => "ℤ", "Q" => "ℚ", "C" => "ℂ", _ => arg };
            }
            case "left": case "right": case "big": case "Big": case "bigg": case "Bigg":
            case "displaystyle": case "limits": case "nolimits":
                return "";
            case "hat": return ReadArg(s, ref i) + "̂";
            case "bar": case "overline": return ReadArg(s, ref i) + "̅";
            case "vec": return ReadArg(s, ref i) + "⃗";
            case "dot": return ReadArg(s, ref i) + "̇";
            case "tilde": return ReadArg(s, ref i) + "̃";
            case "begin": case "end":
                ReadArg(s, ref i);
                return name == "end" ? "" : " ";
        }
        if (LatexToOmml.Nary.TryGetValue(name, out var nary)) return nary.Char;
        if (LatexToOmml.Symbols.TryGetValue(name, out var sym)) return sym;
        return name; // \sin, \log, \max… read fine as plain words
    }

    private static string ReadArg(string s, ref int i)
    {
        SkipSpaces(s, ref i);
        if (i >= s.Length) return "";
        if (s[i] == '{') { i++; return Convert(s, ref i, true); }
        if (s[i] == '\\')
        {
            i++;
            int start = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            if (i == start && i < s.Length) i++;
            var name = s[start..i];
            return Command(name, s, ref i);
        }
        return s[i++].ToString();
    }

    private static void SkipSpaces(string s, ref int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
    }

    private static string Group(string x) =>
        x.Length <= 1 || x.All(char.IsLetterOrDigit) ? x : $"({x})";

    private static string Script(string arg, bool sup)
    {
        var from = sup ? SupFrom : SubFrom;
        var to = sup ? SupTo : SubTo;
        if (arg.Length > 0 && arg.All(ch => from.Contains(ch)))
            return new string(arg.Select(ch => to[from.IndexOf(ch)]).ToArray());
        return (sup ? "^" : "_") + Group(arg);
    }
}
