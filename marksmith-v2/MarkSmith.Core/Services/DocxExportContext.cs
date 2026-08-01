using System;
using System.Collections.Generic;
using DocumentFormat.OpenXml.Packaging;
using MdToPdf.Core.AdvancedFeatures;
using MdToPdf.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;
using MdToPdf.Services.Mermaid;

namespace MdToPdf.Services;

public enum RevisionKind
{
    None = 0,
    Insertion = 1,
    Deletion = 2
}

public sealed class Ctx
{
    public required MainDocumentPart MainPart { get; init; }
    public required W.Numbering Numbering { get; init; }
    public required AppSettings Settings { get; init; }
    public required ThemeDefinition Theme { get; init; }
    public required Dictionary<string, (string Color, string Icon)> Alerts { get; init; }
    public required string LinkColor { get; init; }
    public required bool NoEmoji { get; init; }
    public int NextNumId = 2; // numId 1 is the shared bullet instance
    public int NextBookmarkId = 1;
    public uint NextDrawingId = 1000; // docPr ids for drawings (mermaid shapes / snapshots)
    public int NextRevisionId = 1;    // sequential revision id for OpenXML track changes
    public string DefaultRevisionAuthor = "Marksmith AI";
    public DateTime DefaultRevisionDate = DateTime.UtcNow;
    public string? BrandFont;         // branding kit: document-wide font override
    public bool ForceWebLayout;       // an oversized ShapeForge diagram wants Web Layout view
    public int MermaidMode = 1;       // 0 = Snapshot picture, 1 = ShapeForge native shapes
    public IReadOnlyList<byte[]?>? MermaidImages; // pre-rasterized PNGs, one per fence in order
    public IReadOnlyList<Mermaid.HarvestedDiagram?>? MermaidGeometry; // exact mermaid geometry per fence
    public bool MermaidExactLayout; // OversizedDiagramMode==Exact: prefer harvested geometry
    public IReadOnlyList<Mermaid.GenericDiagram?>? MermaidGenericGeometry; // generic per-fence geometry (any type)
    public int MermaidSeen;           // index of the next mermaid fence encountered
    public bool DropCapPending = true;
    public readonly Dictionary<string, string> Anchors = new(); // markdig heading id -> bookmark name
    public int OversizedDiagramMode;   // 0=Ask,1=Exact,2=Reflow,3=MultiPageVertical,4=Grid,5=ShrinkToFit
    public int DiagramGridSize = 2;    // grid multiplier for mode 4 (2=2x2, 3=3x3)
    public bool SmartConnectors = true;
    
    public required Dictionary<string, FeatureNode> AdvancedFeatures { get; init; }

    public string TextHex => Hex(Theme.Text);
    public string HeadingHex => Hex(Theme.Heading);
    public string BorderHex => Hex(Theme.Border);
    public string CodeHex => Hex(Theme.Code);
    public string SecondaryHex => Hex(Theme.Secondary);
    public string BackgroundHex => Hex(Theme.Background);
    public string PrimaryHex => Hex(Theme.Primary);

    private static string Hex(string cssColor) => cssColor.TrimStart('#').ToUpperInvariant();
}

// Inline formatting state threaded through the inline walker.
public readonly record struct Fmt(
    bool Bold, bool Italic, bool Strike, bool Code, bool InlineBorder, bool Superscript, bool Subscript,
    bool Highlight, bool Underline, string? Color, bool WikiLink = false, bool NoProof = false, bool UnderlineDash = false,
    RevisionKind Revision = RevisionKind.None, string? RevisionAuthor = null, DateTime? RevisionDate = null, int RevisionId = 0,
    W.UnderlineValues? UnderlineStyle = null, string? UnderlineColor = null,
    W.HighlightColorValues? HighlightColor = null, string? ShadingColor = null)
{
    public bool EffectiveUnderline => Underline || (UnderlineStyle.HasValue && UnderlineStyle.Value != W.UnderlineValues.None);

    public W.UnderlineValues EffectiveUnderlineStyle => UnderlineStyle ?? (WikiLink ? W.UnderlineValues.Dash : (Underline ? W.UnderlineValues.Single : W.UnderlineValues.None));

    public W.HighlightColorValues? EffectiveHighlightColor => HighlightColor ?? (Highlight ? W.HighlightColorValues.Yellow : null);
}

