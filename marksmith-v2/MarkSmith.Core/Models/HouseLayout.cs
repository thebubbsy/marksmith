using System;

namespace MarkSmith.Models;

/// <summary>
/// Page geometry and header/footer inherited from a corporate .dotx template (Advanced House Style
/// extraction). Import reads these straight out of the template's section properties and
/// header/footer parts — no AI round-trip — and DOCX export replays them so a converted document
/// lands on the company's paper: same page size, margins, column layout and running header/footer.
/// All lengths are OOXML twips (1/20 pt).
/// </summary>
public sealed class HouseLayout
{
    /// <summary>w:pgSz w:w (twips).</summary>
    public uint? PageWidthTwips { get; set; }

    /// <summary>w:pgSz w:h (twips).</summary>
    public uint? PageHeightTwips { get; set; }

    /// <summary>"portrait" | "landscape" (w:pgSz w:orient).</summary>
    public string? Orientation { get; set; }

    // w:pgMar (twips; read/written via raw w: attributes because this SDK's PageMargin typed
    // properties are inconsistent between versions)
    public int? MarginTop { get; set; }
    public int? MarginRight { get; set; }
    public int? MarginBottom { get; set; }
    public int? MarginLeft { get; set; }
    public int? HeaderDistance { get; set; }
    public int? FooterDistance { get; set; }

    // w:cols (document-level column layout)
    public int? ColumnCount { get; set; }
    public int? ColumnSpace { get; set; }

    /// <summary>Template's default header part XML, verbatim (w:hdr root; may contain tables,
    /// fields, images referenced via r:embed).</summary>
    public string? HeaderXml { get; set; }

    /// <summary>Template's default footer part XML, verbatim (w:ftr root).</summary>
    public string? FooterXml { get; set; }

    public bool HasPageLayout => PageWidthTwips is > 0 && PageHeightTwips is > 0;
    public bool HasMargins => MarginTop is not null;
    public bool HasColumns => ColumnCount is > 1;
    public bool HasHeader => !string.IsNullOrWhiteSpace(HeaderXml);
    public bool HasFooter => !string.IsNullOrWhiteSpace(FooterXml);

    /// <summary>Nothing inherited — export should fall back to its default page setup.</summary>
    public bool IsEmpty => !HasPageLayout && !HasMargins && !HasColumns && !HasHeader && !HasFooter;

    public HouseLayout Clone() => (HouseLayout)MemberwiseClone();

    /// <summary>Merges AI JSON geometry overrides on top of the locally-extracted template layout.
    /// The template extraction is the base (it carries the header/footer XML the AI can't
    /// fabricate); any page-geometry field the AI's JSON specifies wins. Returns null when neither
    /// side contributes anything.</summary>
    public static HouseLayout? Merge(HouseLayout? local, HouseLayout? overrides)
    {
        if (local is null && overrides is null) return null;
        var result = local?.Clone() ?? new HouseLayout();
        if (overrides is null) return result.IsEmpty ? null : result;

        result.PageWidthTwips = overrides.PageWidthTwips ?? result.PageWidthTwips;
        result.PageHeightTwips = overrides.PageHeightTwips ?? result.PageHeightTwips;
        result.Orientation = overrides.Orientation ?? result.Orientation;
        result.MarginTop = overrides.MarginTop ?? result.MarginTop;
        result.MarginRight = overrides.MarginRight ?? result.MarginRight;
        result.MarginBottom = overrides.MarginBottom ?? result.MarginBottom;
        result.MarginLeft = overrides.MarginLeft ?? result.MarginLeft;
        result.HeaderDistance = overrides.HeaderDistance ?? result.HeaderDistance;
        result.FooterDistance = overrides.FooterDistance ?? result.FooterDistance;
        result.ColumnCount = overrides.ColumnCount ?? result.ColumnCount;
        result.ColumnSpace = overrides.ColumnSpace ?? result.ColumnSpace;
        // Header/footer content is never overridden by the AI JSON — it comes from the template.
        return result.IsEmpty ? null : result;
    }
}
