using System.Text;
using SkiaSharp;

namespace MarkSmith.Ocr.Benchmark;

/// <summary>One benchmark page: what it tests, and how to make it.</summary>
public sealed record OcrBenchmarkCase(string Id, string Description, Func<SynthPage> Make);

/// <summary>
/// The 20 pages every OCR engine is scored on (see OcrBenchmarkTests). Fonts marked "held out"
/// are never used to train MarkSmith OCR, so those cases measure fonts it has never seen. The
/// texts are written for the benchmark and are not in the training data either.
/// </summary>
public static class OcrBenchmarkCorpus
{
    /// <summary>Font families MarkSmith OCR's training never sees.</summary>
    public static readonly string[] HeldOutFamilies = { "FreeSerif", "FreeSans", "Bitstream Charter", "Courier 10 Pitch" };

    private const string Report =
        "The quarterly review found that most delays came from late approvals rather than from the work itself. " +
        "Teams that agreed on a single owner for each decision shipped their changes about two weeks sooner, " +
        "and they reported fewer surprises during testing. We recommend that every project names that owner " +
        "on its first page and keeps the list of open questions short enough to read in a minute.";

    private const string Letter =
        "Dear Ms. Patel, thank you for your letter of 14 March. We have looked again at the invoice you queried " +
        "and agree that the delivery charge was applied twice. A credit note for the difference is enclosed, " +
        "and your next statement will show the corrected balance. Please call us if anything is still unclear.";

    private const string Science =
        "Water boils at a lower temperature on a mountain because the air pressure is lower there. At the summit " +
        "of a high peak the boiling point can fall below seventy degrees, which is why cooking takes longer. " +
        "Pressure cookers raise the pressure instead, so food cooks faster than it would in an open pan.";

    private const string Story =
        "The lighthouse keeper wrote everything down: the weather at dawn, the ships that passed, the number of " +
        "gulls on the rail. Years later his notebooks were the only record of the winter the harbour froze, " +
        "and the town used them to settle an argument nobody else could remember the start of.";

    private const string Manual =
        "Before you start, unplug the device and let it cool for ten minutes. Remove the four screws on the back " +
        "panel, lift it away gently, and check that the filter is seated in its slot. Replace the panel, tighten " +
        "the screws by hand, and plug the device back in. The light should turn green within a few seconds.";

    private const string Numbers =
        "Invoice 2024-0417 dated 03/05/2024\n" +
        "Item A-17 Widgets 12 x $4.50 = $54.00\n" +
        "Item B-02 Brackets 3 x $19.99 = $59.97\n" +
        "Shipping (2-day) $12.50\n" +
        "Subtotal $126.47 Tax 8.25% $10.43\n" +
        "Total due $136.90 by 31 May 2024\n" +
        "Ref: PO #88213, account 0041-7782-19";

    private const string Code =
        "public static int Add(int a, int b)\n" +
        "{\n" +
        "    return a + b; // sum of two values\n" +
        "}\n" +
        "var items = list.Where(x => x.Count > 3).ToList();\n" +
        "if (items.Count == 0) { return null; }";

    private const string Contacts =
        "Contact Jane O'Neill at jane.oneill@example.org or call +1 (555) 013-2468.\n" +
        "Visit https://www.example.com/support for opening hours.\n" +
        "Our office is at 221B Baker Street, London NW1 6XE.\n" +
        "Questions? Email help@marksmith.example and quote ticket #4521.";

    private const string Short =
        "Meeting moved to Thursday at 10:30 in room 4.\nBring the signed forms and your badge.";

    public static readonly IReadOnlyList<OcrBenchmarkCase> Cases = new[]
    {
        Case("01-serif-12", "Times-like serif (Liberation Serif) 12 pt, clean 300 dpi", Para(Report, new SynthStyle { Family = "Liberation Serif" })),
        Case("02-sans-11", "Arial-like sans (Liberation Sans) 11 pt, clean", Para(Letter, new SynthStyle { Family = "Liberation Sans", PointSize = 11 })),
        Case("03-calibri-11", "Calibri-like (Carlito) 11 pt, clean", Para(Science, new SynthStyle { Family = "Carlito", PointSize = 11 })),
        Case("04-cambria-12", "Cambria-like (Caladea) 12 pt, clean", Para(Story, new SynthStyle { Family = "Caladea" })),
        Case("05-code-mono", "Monospace code (DejaVu Sans Mono) 10 pt", Para(Code, new SynthStyle { Family = "DejaVu Sans Mono", PointSize = 10 })),
        Case("06-headings-bold", "Bold headings over sans body", () => OcrSynth.Render(new[]
        {
            new SynthBlock("Installation Guide", new SynthStyle { Family = "Liberation Sans", Weight = SKFontStyleWeight.Bold, PointSize = 18 }),
            new SynthBlock(Manual, new SynthStyle { Family = "Liberation Sans", PointSize = 11 }),
            new SynthBlock("Troubleshooting", new SynthStyle { Family = "Liberation Sans", Weight = SKFontStyleWeight.Bold, PointSize = 14 }),
            new SynthBlock(Short, new SynthStyle { Family = "Liberation Sans", PointSize = 11 }),
        }, new SynthStyle())),
        Case("07-italic", "Italic serif paragraph", Para(Story, new SynthStyle { Family = "Liberation Serif", Slant = SKFontStyleSlant.Italic })),
        Case("08-small-8pt", "Small print 8 pt at 300 dpi", Para(Report + " " + Science, new SynthStyle { Family = "Liberation Sans", PointSize = 8 })),
        Case("09-200dpi", "11 pt scanned at 200 dpi, slightly soft", Para(Letter, new SynthStyle { Family = "Liberation Serif", PointSize = 11, Dpi = 200, Blur = 0.5f })),
        Case("10-low-dpi", "11 pt scanned at 120 dpi with noise", Para(Manual, new SynthStyle { Family = "Liberation Sans", PointSize = 11, Dpi = 120, Noise = 10, Seed = 4 })),
        Case("11-skew", "Page skewed 3°", Para(Report, new SynthStyle { Family = "Liberation Serif", SkewDegrees = 3f })),
        Case("12-photocopy", "Photocopy: blur 1.3 px, grey noise and specks", Para(Science, new SynthStyle { Family = "Liberation Serif", Blur = 1.3f, Noise = 28, Speckle = 0.002f, Seed = 7 })),
        Case("13-jpeg", "Phone photo: JPEG quality 20 and uneven light", Para(Letter, new SynthStyle { Family = "Carlito", PointSize = 11, Jpeg = 20, Shading = 90, Seed = 2 })),
        Case("14-numbers", "Invoice lines: numbers, currency, dates, punctuation", Para(Numbers, new SynthStyle { Family = "Liberation Sans", PointSize = 11 })),
        Case("15-two-columns", "Two-column page (reading order)", () => OcrSynth.RenderColumns(new[]
        {
            (IReadOnlyList<SynthBlock>)new[] { new SynthBlock(Report) },
            new[] { new SynthBlock(Story) },
        }, new SynthStyle { Family = "Liberation Serif", PointSize = 10 })),
        Case("16-large-heading", "24 pt title and 11 pt body", () => OcrSynth.Render(new[]
        {
            new SynthBlock("Annual Report 2024", new SynthStyle { Family = "Caladea", Weight = SKFontStyleWeight.Bold, PointSize = 24 }),
            new SynthBlock(Report, new SynthStyle { Family = "Caladea", PointSize = 11 }),
        }, new SynthStyle())),
        Case("17-contacts", "Emails, URLs, phone numbers, apostrophes", Para(Contacts, new SynthStyle { Family = "Liberation Sans", PointSize = 11 })),
        Case("18-low-contrast", "Faded grey text on shaded off-white paper", Para(Manual, new SynthStyle { Family = "Liberation Serif", Ink = 140, Paper = 225, Shading = 40 })),
        Case("19-heldout-freeserif", "Held-out font: FreeSerif 13 pt", Para(Letter + " " + Story, new SynthStyle { Family = "FreeSerif", PointSize = 13 })),
        Case("20-heldout-typewriter", "Held-out font: Courier 10 Pitch 12 pt, smudged", Para(Manual, new SynthStyle { Family = "Courier 10 Pitch", Blur = 1f, Noise = 20, Speckle = 0.001f, Seed = 3 })),
    };

    private static OcrBenchmarkCase Case(string id, string description, Func<SynthPage> make) => new(id, description, make);

    private static Func<SynthPage> Para(string text, SynthStyle style) => () => OcrSynth.Render(new[] { new SynthBlock(text) }, style);
}

/// <summary>Scores recognised text against the truth.</summary>
public static class OcrAccuracy
{
    /// <summary>
    /// Character accuracy: 1 − (edit distance ÷ reference length), floored at 0, after both sides
    /// are normalised the same way (Unicode compatibility form, curly quotes and dashes to ASCII,
    /// all whitespace and line breaks to single spaces). Line breaks don't count: engines split long
    /// lines differently, and the Markdown import re-flows paragraphs anyway.
    /// </summary>
    public static double CharacterAccuracy(string reference, string hypothesis)
    {
        var r = Normalize(reference);
        var h = Normalize(hypothesis);
        if (r.Length == 0) return h.Length == 0 ? 1 : 0;
        return Math.Max(0, 1 - Levenshtein(r, h) / (double)r.Length);
    }

    public static string Normalize(string s)
    {
        var t = (s ?? "").Normalize(NormalizationForm.FormKC)
            .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"')
            .Replace('–', '-').Replace('—', '-').Replace('−', '-');
        var sb = new StringBuilder(t.Length);
        bool space = false;
        foreach (var ch in t)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
