using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Wordprocessing;
using MdToPdf.Models;

namespace MdToPdf.Services;

/// <summary>
/// Turns a code block into a sequence of coloured OOXML runs.  A small, self-contained
/// regex tokenizer replaces ColorCode 2.0.15 whose compiled grammars only fire for the
/// first handful of tokens in any language, leaving the rest of the block monochrome.
/// The palette below is GitHub-style and already clears WCAG 4.5:1 on typical code
/// backgrounds, so token variety survives ContrastGuard instead of collapsing to white.
/// </summary>
public class OpenXmlSyntaxHighlighter
{
    public IEnumerable<Run> GetHighlightedRuns(string sourceCode, string languageId, ThemeDefinition theme)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            yield break;

        var profile = ResolveProfile(languageId);

        if (profile == null)
        {
            // Unknown language → one plain run, no highlighting.
            var run = MakeRun(sourceCode, theme.Code.TrimStart('#'), false, false);
            yield return run;
            yield break;
        }

        bool isDark = !ThemeDefinition.IsLight(theme.Code);
        string bgHex = theme.Code.TrimStart('#');

        foreach (var (text, kind) in Tokenize(sourceCode, profile))
        {
            string hex = ColorFor(kind, isDark);
            hex = ContrastGuard.EnsureLegibleText(hex, bgHex, isDark ? "E6EDF3" : "1F2328");
            yield return MakeRun(text, hex, kind == TokenKind.Comment, kind == TokenKind.Keyword);
        }
    }

    // ── language-id normalisation ──────────────────────────────────────────────

    private static string NormalizeLanguageId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        id = id.Trim().ToLowerInvariant();
        return id switch
        {
            "cs" or "c#" or "csharp" => "csharp",
            "js" or "javascript" or "jsx" or "mjs" or "cjs" => "javascript",
            "ts" or "typescript" or "tsx" => "typescript",
            "py" or "python" or "python3" => "python",
            "java" => "java",
            "cpp" or "c++" or "cc" or "cxx" or "hpp" => "cpp",
            "c" or "h" => "c",
            "go" or "golang" => "go",
            "rs" or "rust" => "rust",
            "php" => "php",
            "sql" or "tsql" or "mysql" or "pgsql" or "plsql" => "sql",
            "sh" or "bash" or "zsh" or "shell" or "ksh" => "bash",
            "powershell" or "ps1" or "psm1" or "pwsh" => "powershell",
            "html" or "htm" or "xhtml" or "svg" => "html",
            "xml" or "xsl" or "xslt" or "xsd" or "csproj" or "vbproj" or "props" or "targets" => "xml",
            "json" or "jsonc" or "json5" => "json",
            "yaml" or "yml" => "yaml",
            "css" or "scss" or "sass" or "less" => "css",
            "md" or "markdown" => "markdown",
            "vb" or "vbnet" or "visual basic" => "vb",
            _ => id
        };
    }

    // ── token model ────────────────────────────────────────────────────────────

    private enum TokenKind
    {
        Plain,      // identifiers, punctuation, whitespace
        Comment,    // line / block comments (italic)
        String,     // string & char literals
        Number,     // numeric literals
        Keyword,    // reserved words & control flow (bold)
        Type,       // built-in / well-known type names
        Function,   // call-site identifiers  name(
        Builtin,    // well-known functions & constants
    }

    // ── per-language profile ───────────────────────────────────────────────────

    private sealed class SyntaxProfile
    {
        public bool HasLineComment { get; init; }      // // style
        public bool HasHashComment { get; init; }      // #  style
        public bool HasDashComment { get; init; }      // -- style  (SQL / Lua)
        public bool HasXmlComment { get; init; }       // <!-- -->
        public bool HasSqlBlockComment { get; init; }  // /* */ also in SQL
        public char[] StringDelimiters { get; init; } = Array.Empty<char>();
        public bool HasTripleStrings { get; init; }    // Python """ / '''
        public bool HasVerbatimStrings { get; init; }  // C# @"…"
        public bool HasBacktickStrings { get; init; }  // JS/TS/Go `…`
        public bool HasDoubleSlashBlockComment { get; init; } = true; // /* */
        public bool KeywordsCaseInsensitive { get; init; }
        public HashSet<string> Keywords { get; init; } = new(StringComparer.Ordinal);
        public HashSet<string> Types { get; init; } = new(StringComparer.Ordinal);
        public HashSet<string> Builtins { get; init; } = new(StringComparer.Ordinal);

        // The tokenizer patterns depend only on this (immutable) profile, so build the compiled
        // regex set ONCE per language and reuse it for every code block — it used to be rebuilt
        // from scratch (15–20 new Regex(...) allocations) on every highlight pass.
        private List<(Regex, TokenKind)>? _patterns;
        public List<(Regex, TokenKind)> Patterns => _patterns ??= BuildPatterns(this);
    }

    private static HashSet<string> Kw(string words, bool caseInsensitive = false)
        => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries),
               caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // ── profile catalogue ──────────────────────────────────────────────────────

    private static readonly Dictionary<string, SyntaxProfile> Profiles =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = new SyntaxProfile
        {
            HasLineComment = true, HasHashComment = true,
            StringDelimiters = new[] { '"', '\'' },
            HasVerbatimStrings = true,
            Keywords = Kw("abstract as base break case catch class checked const continue default delegate do else enum event explicit extern finally fixed for foreach goto if implicit in interface internal is lock namespace new operator out override params private protected public readonly ref return sealed sizeof stackalloc static switch this throw try typeof unchecked unsafe using virtual void volatile while async await var dynamic yield partial record init required global"),
            Types = Kw("bool byte char decimal double float int long object sbyte short string uint ulong ushort nint nuint Task IEnumerable IList IReadOnlyList IDictionary KeyValuePair List Dictionary HashSet Array Guid DateTime DateTimeOffset TimeSpan Exception StringBuilder Func Action Nullable"),
            Builtins = Kw("Console WriteLine ReadLine Write Math Abs Min Max Round Floor Ceiling Sqrt Pow Log Sin Cos Tan Parse TryParse Format Join Split Trim ToLower ToUpper Contains Replace Substring StartsWith EndsWith IndexOf ToArray ToList ToDictionary Select Where OrderBy GroupBy Any All Count Sum First FirstOrDefault Single SingleOrDefault Task Run FromResult Delay WhenAll WhenAny"),
        },
        ["javascript"] = new SyntaxProfile
        {
            HasLineComment = true,
            StringDelimiters = new[] { '"', '\'' },
            HasBacktickStrings = true,
            Keywords = Kw("async await break case catch class const continue debugger default delete do else export extends finally for from function get if import in instanceof let new of return set static super switch this throw try typeof var void while yield"),
            Types = Kw("Array Boolean Date Error Function JSON Map Math Number Object Promise Proxy Reflect RegExp Set String Symbol WeakMap WeakRef"),
            Builtins = Kw("console log warn error info debug trace fetch require module exports process setTimeout setInterval clearTimeout clearInterval parseInt parseFloat isNaN isFinite encodeURIComponent decodeURIComponent push pop map filter reduce forEach find some every includes indexOf slice splice join split trim replace match test keys values entries assign create freeze defineProperty"),
        },
        ["typescript"] = new SyntaxProfile
        {
            HasLineComment = true,
            StringDelimiters = new[] { '"', '\'' },
            HasBacktickStrings = true,
            Keywords = Kw("abstract any as asserts async await bigint boolean break case catch class const continue declare debugger default delete do else enum export extends finally for from function get if implements import in infer instanceof interface is keyof let module namespace never new number object of out override private protected public readonly return set static string super switch symbol this throw try type typeof undefined unique unknown var void while yield satisfies"),
            Types = Kw("Array Boolean Date Error Function JSON Map Math Number Object Promise Proxy Reflect RegExp Set String Symbol WeakMap WeakRef Partial Required Readonly Record Pick Omit Exclude Extract NonNullable ReturnType InstanceType"),
            Builtins = Kw("console log warn error info debug trace fetch require setTimeout setInterval clearTimeout clearInterval parseInt parseFloat isNaN isFinite encodeURIComponent decodeURIComponent push pop map filter reduce forEach find some every includes indexOf slice splice join split trim replace match test keys values entries assign create freeze defineProperty"),
        },
        ["python"] = new SyntaxProfile
        {
            HasHashComment = true,
            StringDelimiters = new[] { '"', '\'' },
            HasTripleStrings = true,
            Keywords = Kw("and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield match case"),
            Types = Kw("bool bytes bytearray complex dict float frozenset int list object range set slice str tuple type Exception ValueError TypeError KeyError IndexError AttributeError RuntimeError StopIteration Generator"),
            Builtins = Kw("abs all any ascii bin callable chr classmethod compile delattr dir divmod enumerate eval exec filter format getattr globals hasattr hash help hex id input isinstance issubclass iter len locals map max memoryview min next oct open ord pow print property repr reversed round setattr sorted staticmethod sum super vars zip __init__ __name__ __main__ self cls"),
        },
        ["java"] = new SyntaxProfile
        {
            HasLineComment = true,
            StringDelimiters = new[] { '"', '\'' },
            Keywords = Kw("abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for goto if implements import instanceof int interface long native new package private protected public return short static strictfp super switch synchronized this throw throws transient try var void volatile while yield record sealed permits"),
            Types = Kw("String Integer Long Double Float Boolean Byte Short Character Object List Map Set ArrayList HashMap HashSet LinkedList TreeMap TreeSet Optional Stream Collector Iterable Runnable Thread Exception RuntimeException IllegalArgumentException IllegalStateException IOException"),
            Builtins = Kw("System out println print printf Math abs min max round floor ceil sqrt pow log sin cos tan parseInt parseLong parseDouble parseBoolean format join split trim toLowerCase toUpperCase contains replace substring startsWith endsWith indexOf toString equals hashCode compareTo stream map filter reduce collect forEach of asList emptyList singletonList"),
        },
        ["cpp"] = new SyntaxProfile
        {
            HasLineComment = true, HasHashComment = true,
            StringDelimiters = new[] { '"', '\'' },
            Keywords = Kw("alignas alignof auto break case catch class const constexpr consteval constinit continue decltype default delete do else enum explicit extern for friend goto if inline mutable namespace new noexcept operator private protected public register return sizeof static struct switch template this thread_local throw try typedef typename union using virtual void volatile while concept requires co_await co_return co_yield"),
            Types = Kw("bool char char8_t char16_t char32_t double float int long short signed unsigned size_t ssize_t ptrdiff_t intptr_t uintptr_t int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t string wstring vector map set list deque array unordered_map unordered_set shared_ptr unique_ptr weak_ptr optional variant any tuple pair"),
            Builtins = Kw("cout cin cerr endl printf scanf malloc free new delete make_shared make_unique move forward swap begin end size empty push_back pop_back insert erase find sort reverse min max abs sqrt pow ceil floor"),
        },
        ["c"] = new SyntaxProfile
        {
            HasLineComment = true, HasHashComment = true,
            StringDelimiters = new[] { '"', '\'' },
            Keywords = Kw("auto break case const continue default do else enum extern for goto if inline register restrict return sizeof static struct switch typedef union void volatile while _Alignas _Alignof _Atomic _Bool _Complex _Generic _Imaginary _Noreturn _Static_assert _Thread_local"),
            Types = Kw("char double float int long short signed unsigned size_t ssize_t ptrdiff_t intptr_t uintptr_t int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t FILE"),
            Builtins = Kw("printf scanf fprintf fscanf sprintf snprintf malloc calloc realloc free memcpy memmove memset memcmp strlen strcmp strncmp strcpy strncpy strcat strchr strrchr strstr fopen fclose fread fwrite fseek ftell"),
        },
        ["go"] = new SyntaxProfile
        {
            HasLineComment = true,
            StringDelimiters = new[] { '"', '\'' },
            HasBacktickStrings = true,
            Keywords = Kw("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var"),
            Types = Kw("bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any"),
            Builtins = Kw("append cap close copy delete len make new panic print println recover Errorf Printf Sprintf Fprintf Println Scanf Sscanf Fscanf"),
        },
        ["rust"] = new SyntaxProfile
        {
            HasLineComment = true,
            StringDelimiters = new[] { '"' },
            Keywords = Kw("as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait type unsafe use where while union"),
            Types = Kw("bool char f32 f64 i8 i16 i32 i64 i128 isize str String u8 u16 u32 u64 u128 usize Vec Box Rc Arc Cell RefCell Option Result HashMap HashSet BTreeMap BTreeSet"),
            Builtins = Kw("println eprintln format vec panic assert assert_eq assert_ne debug_assert todo unimplemented unreachable Some None Ok Err unwrap expect map filter fold collect iter into_iter len is_empty push pop insert remove get contains_key"),
        },
        ["php"] = new SyntaxProfile
        {
            HasLineComment = true, HasHashComment = true,
            StringDelimiters = new[] { '"', '\'' },
            Keywords = Kw("abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty enddeclare endfor endforeach endif endswitch endwhile enum extends final finally fn for foreach function global goto if implements include include_once instanceof insteadof interface isset list match namespace new or print private protected public readonly require require_once return static switch throw trait try unset use var while yield"),
            Types = Kw("bool float int string void mixed object iterable never null false true self parent"),
            Builtins = Kw("echo print var_dump print_r isset unset empty count strlen strpos substr str_replace strtolower strtoupper trim explode implode array_map array_filter array_reduce array_push array_pop in_array array_key_exists json_encode json_decode file_get_contents file_put_contents"),
        },
        ["sql"] = new SyntaxProfile
        {
            HasDashComment = true, HasSqlBlockComment = true,
            StringDelimiters = new[] { '\'' },
            KeywordsCaseInsensitive = true,
            Keywords = Kw("SELECT INSERT UPDATE DELETE FROM WHERE AND OR NOT IN IS NULL LIKE BETWEEN EXISTS JOIN INNER LEFT RIGHT OUTER FULL CROSS ON AS GROUP BY ORDER HAVING LIMIT OFFSET UNION ALL DISTINCT CREATE TABLE ALTER DROP INDEX VIEW TRIGGER PROCEDURE FUNCTION BEGIN END COMMIT ROLLBACK TRANSACTION GRANT REVOKE DENY INTO VALUES SET DEFAULT PRIMARY KEY FOREIGN REFERENCES UNIQUE CHECK CONSTRAINT CASCADE ADD COLUMN IF ELSE THEN WHEN CASE END WHILE FOR LOOP RETURN DECLARE CURSOR OPEN CLOSE FETCH NEXT PRIOR FIRST LAST ABSOLUTE RELATIVE SCHEMA DATABASE USE EXEC EXECUTE WITH RECURSIVE TEMPORARY TEMP REPLACE TRUNCATE RENAME TO COMMENT", true),
            Types = Kw("INT INTEGER BIGINT SMALLINT TINYINT DECIMAL NUMERIC FLOAT REAL DOUBLE PRECISION CHAR VARCHAR NCHAR NVARCHAR TEXT NTEXT DATE TIME DATETIME DATETIME2 SMALLDATETIME TIMESTAMP BOOLEAN BOOL BIT BLOB CLOB NCLOB BINARY VARBINARY IMAGE MONEY SMALLMONEY UNIQUEIDENTIFIER XML JSON", true),
            Builtins = Kw("COUNT SUM AVG MIN MAX ABS CEILING FLOOR ROUND POWER SQRT SIGN RAND COALESCE NULLIF ISNULL NVL IIF CHOOSE CAST CONVERT TRY_CAST TRY_CONVERT LEN LENGTH CHARINDEX PATINDEX REPLACE STUFF SUBSTRING LEFT RIGHT UPPER LOWER LTRIM RTRIM TRIM CONCAT FORMAT STRING_AGG STRING_SPLIT GETDATE GETUTCDATE SYSDATETIME DATEADD DATEDIFF DATEPART DATENAME YEAR MONTH DAY EOMONTH ROW_NUMBER RANK DENSE_RANK NTILE LAG LEAD FIRST_VALUE LAST_VALUE", true),
        },
        ["bash"] = new SyntaxProfile
        {
            HasHashComment = true,
            StringDelimiters = new[] { '"', '\'' },
            Keywords = Kw("if then elif else fi for while until do done case esac function in select time coproc break continue return exit export local declare readonly unset shift source alias unalias set shopt trap wait eval exec"),
            Builtins = Kw("echo printf read cd pwd ls cp mv rm mkdir rmdir touch cat grep sed awk find sort uniq wc head tail cut tr tee xargs chmod chown chgrp ln df du ps kill top curl wget git docker npm node python python3 pip pip3 sudo apt yum brew make cmake gcc g++ cargo rustc go java javac dotnet msbuild"),
        },
        ["powershell"] = new SyntaxProfile
        {
            HasHashComment = true,
            StringDelimiters = new[] { '"', '\'' },
            KeywordsCaseInsensitive = true,
            Keywords = Kw("if else elseif switch while for foreach do until break continue return function filter workflow in trap try catch finally throw param begin process end data define using var", true),
            Builtins = Kw("Write-Host Write-Output Write-Error Write-Warning Write-Verbose Write-Debug Read-Host Get-Content Set-Content Add-Content Out-File Out-Null Out-String ForEach-Object Where-Object Select-Object Sort-Object Group-Object Measure-Object Compare-Object New-Object Remove-Item Get-Item Set-Item Get-ChildItem Copy-Item Move-Item Rename-Item Test-Path Invoke-Command Invoke-Expression Start-Process Stop-Process Get-Process Get-Service Start-Service Stop-Service Import-Module Get-Module Get-Help Get-Command Get-Member Get-Date Get-Random", true),
        },
        ["html"] = new SyntaxProfile
        {
            HasXmlComment = true,
            StringDelimiters = new[] { '"', '\'' },
            Keywords = Kw("html head body title meta link script style div span p a img ul ol li table tr td th thead tbody form input button select option textarea label h1 h2 h3 h4 h5 h6 header footer nav main section article aside br hr pre code blockquote figure figcaption video audio source canvas svg path rect circle line polygon polyline ellipse g defs use symbol marker pattern clipPath mask filter feGaussianBlur feOffset feMerge feMergeNode feBlend feColorMatrix feComposite feFlood feImage feMorphology feTurbulence"),
            Types = Kw("class id href src alt title type name value placeholder action method target rel media charset content http-equiv style lang dir onclick onload onsubmit onchange oninput disabled checked readonly required multiple autofocus autocomplete colspan rowspan scope headers"),
        },
        ["xml"] = new SyntaxProfile
        {
            HasXmlComment = true,
            StringDelimiters = new[] { '"', '\'' },
        },
        ["json"] = new SyntaxProfile
        {
            StringDelimiters = new[] { '"' },
            Keywords = Kw("true false null"),
        },
        ["yaml"] = new SyntaxProfile
        {
            HasHashComment = true,
            StringDelimiters = new[] { '"', '\'' },
            Keywords = Kw("true false null yes no on off"),
        },
        ["css"] = new SyntaxProfile
        {
            HasDashComment = false, HasSqlBlockComment = true,
            StringDelimiters = new[] { '"', '\'' },
            Keywords = Kw("important inherit initial unset revert none auto block inline flex grid absolute relative fixed sticky hidden visible scroll overflow"),
            Types = Kw("color background background-color background-image background-position background-size background-repeat border border-radius border-color border-width border-style margin margin-top margin-right margin-bottom margin-left padding padding-top padding-right padding-bottom padding-left width height min-width min-height max-width max-height display position top right bottom left float clear z-index opacity visibility cursor pointer-events user-select box-shadow text-shadow transform transition animation animation-name animation-duration animation-timing-function animation-delay animation-iteration-count animation-direction flex-direction flex-wrap justify-content align-items align-content align-self gap grid-template-columns grid-template-rows grid-column grid-row font font-family font-size font-weight font-style line-height letter-spacing text-align text-decoration text-transform text-indent vertical-align white-space word-break word-spacing overflow-x overflow-y list-style list-style-type content counter-reset counter-increment"),
        },
        ["markdown"] = new SyntaxProfile
        {
            HasHashComment = false,
            StringDelimiters = new[] { '`' },
        },
        ["vb"] = new SyntaxProfile
        {
            HasDashComment = false,
            StringDelimiters = new[] { '"' },
            KeywordsCaseInsensitive = true,
            Keywords = Kw("AddHandler AddressOf AndAlso Alias And As Boolean ByRef Byte ByVal Call Case Catch CBool CByte CChar CDate CDbl CDec Char CInt Class CLng CObj Const CShort CSng CStr CType Date Decimal Declare Default Delegate Dim DirectCast Do Double Each Else ElseIf End Enum Erase Error Event Exit False Finally For Friend Function Get GetType GoSub GoTo Handles If Implements Imports In Inherits Integer Interface Is Let Lib Like Long Loop Me Mod Module MustInherit MustOverride MyBase MyClass Namespace New Next Not Nothing NotInheritable NotOverridable Object On Option Optional Or OrElse Overloads Overridable Overrides ParamArray Preserve Private Property Protected Public RaiseEvent ReadOnly ReDim REM RemoveHandler Resume Return Select Set Shadows Shared Short Single Static Step Stop String Structure Sub SyncLock Then Throw To True Try TypeOf Unicode Until Variant When While With WithEvents WriteOnly Xor", true),
        },
    };

    private static SyntaxProfile? ResolveProfile(string languageId)
    {
        var normalized = NormalizeLanguageId(languageId);
        return Profiles.TryGetValue(normalized, out var p) ? p : null;
    }

    // ── tokenizer ──────────────────────────────────────────────────────────────

    private static List<(string Text, TokenKind Kind)> Tokenize(string source, SyntaxProfile profile)
    {
        var patterns = profile.Patterns;
        var raw = new List<(string, TokenKind)>();
        int pos = 0;
        int len = source.Length;

        while (pos < len)
        {
            bool matched = false;

            foreach (var (regex, kind) in patterns)
            {
                var m = regex.Match(source, pos);
                if (m.Success && m.Index == pos && m.Length > 0)
                {
                    var text = m.Value;
                    if (kind == TokenKind.Plain)
                        raw.Add(ClassifyWord(text, source, pos, profile));
                    else
                        raw.Add((text, kind));
                    pos += m.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                raw.Add((source[pos].ToString(), TokenKind.Plain));
                pos++;
            }
        }

        // Merge adjacent tokens of the same kind so contiguous text stays in one run
        // (keeps the XML compact and preserves searchable phrases in the output).
        var merged = new List<(string, TokenKind)>();
        var sb = new StringBuilder();
        TokenKind? current = null;

        foreach (var (text, kind) in raw)
        {
            if (current == kind)
            {
                sb.Append(text);
            }
            else
            {
                if (current != null)
                    merged.Add((sb.ToString(), current.Value));
                sb.Clear();
                sb.Append(text);
                current = kind;
            }
        }
        if (current != null)
            merged.Add((sb.ToString(), current.Value));

        return merged;
    }

    private static List<(Regex, TokenKind)> BuildPatterns(SyntaxProfile p)
    {
        var list = new List<(Regex, TokenKind)>();
        const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.Singleline;

        // ── comments (highest priority) ──
        if (p.HasXmlComment)
            list.Add((new Regex(@"<!--[\s\S]*?(?:-->|\z)", Opt), TokenKind.Comment));
        if (p.HasDoubleSlashBlockComment)
            list.Add((new Regex(@"/\*[\s\S]*?(?:\*/|\z)", Opt), TokenKind.Comment));
        if (p.HasSqlBlockComment)
            list.Add((new Regex(@"/\*[\s\S]*?(?:\*/|\z)", Opt), TokenKind.Comment));
        if (p.HasLineComment)
            list.Add((new Regex(@"///?[^\n]*", Opt), TokenKind.Comment));
        if (p.HasDashComment)
            list.Add((new Regex(@"--[^\n]*", Opt), TokenKind.Comment));
        if (p.HasHashComment)
            list.Add((new Regex(@"#[^\n]*", Opt), TokenKind.Comment));

        // ── strings ──
        if (p.HasTripleStrings)
        {
            list.Add((new Regex(@"""""""[\s\S]*?(?:""""""|\z)", Opt), TokenKind.String));
            list.Add((new Regex(@"'''[\s\S]*?(?:'''|\z)", Opt), TokenKind.String));
        }
        if (p.HasVerbatimStrings)
        {
            list.Add((new Regex(@"\$@""(?:[^""]|"""")*?(?:""|\z)", Opt), TokenKind.String));
            list.Add((new Regex(@"@""(?:[^""]|"""")*?(?:""|\z)", Opt), TokenKind.String));
            list.Add((new Regex(@"\$""(?:\\.|[^""\\])*?(?:""|\z)", Opt), TokenKind.String));
        }
        if (p.HasBacktickStrings)
            list.Add((new Regex(@"`(?:\\.|[^`\\])*?(?:`|\z)", Opt), TokenKind.String));
        foreach (var d in p.StringDelimiters)
        {
            if (d == '"') list.Add((new Regex(@"""(?:\\.|[^""\\])*?(?:""|\z)", Opt), TokenKind.String));
            else if (d == '\'') list.Add((new Regex(@"'(?:\\.|[^'\\])*?(?:'|\z)", Opt), TokenKind.String));
            else if (d == '`') list.Add((new Regex(@"`(?:\\.|[^`\\])*?(?:`|\z)", Opt), TokenKind.String));
        }

        // ── numbers ──
        list.Add((new Regex(@"0[xX][0-9a-fA-F_]+[uUlL]*|0[bB][01_]+[uUlL]*|\d[\d_]*(?:\.[\d_]+)?(?:[eE][+-]?\d+)?[fFdDmMuUlL]*", Opt), TokenKind.Number));

        // ── words (classified as keyword / type / builtin / function / plain) ──
        list.Add((new Regex(@"[A-Za-z_]\w*", Opt), TokenKind.Plain));

        // ── operators & punctuation ──
        list.Add((new Regex(@"[+\-*/%=<>!&|^~?:;,.()\[\]{}@#$\\]+|\s+", Opt), TokenKind.Plain));

        return list;
    }

    private static (string, TokenKind) ClassifyWord(string word, string source, int pos, SyntaxProfile profile)
    {
        var cmp = profile.KeywordsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        if (profile.Keywords.Contains(word))
            return (word, TokenKind.Keyword);
        if (profile.Types.Contains(word))
            return (word, TokenKind.Type);
        if (profile.Builtins.Contains(word))
            return (word, TokenKind.Builtin);

        // Function-call heuristic: identifier immediately followed by '('
        int after = pos + word.Length;
        while (after < source.Length && source[after] == ' ') after++;
        if (after < source.Length && source[after] == '(')
            return (word, TokenKind.Function);

        return (word, TokenKind.Plain);
    }

    // ── run construction ───────────────────────────────────────────────────────

    private static Run MakeRun(string text, string hexColor, bool italic, bool bold)
    {
        var run = new Run(new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
        var rPr = new RunProperties();
        // OOXML schema order: rFonts → b → i → noProof → color
        rPr.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
        if (bold) rPr.Append(new Bold());
        if (italic) rPr.Append(new Italic());
        rPr.Append(new NoProof());
        rPr.Append(new Color { Val = hexColor });
        run.RunProperties = rPr;
        return run;
    }

    // ── GitHub-style palette ───────────────────────────────────────────────────

    private static string ColorFor(TokenKind kind, bool isDark) => kind switch
    {
        TokenKind.Comment  => isDark ? "B0B8C4" : "6E7781",
        TokenKind.String   => isDark ? "A5D6FF" : "0A3069",
        TokenKind.Number   => isDark ? "79C0FF" : "0550AE",
        TokenKind.Keyword  => isDark ? "79B8FF" : "0550AE",
        TokenKind.Type     => isDark ? "7EE787" : "116329",
        TokenKind.Function => isDark ? "D2A8FF" : "8250DF",
        TokenKind.Builtin  => isDark ? "FFA657" : "953800",
        _                  => isDark ? "E6EDF3" : "24292F",
    };
}
