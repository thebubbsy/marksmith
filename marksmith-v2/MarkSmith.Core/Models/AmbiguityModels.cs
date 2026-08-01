namespace MdToPdf.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Represents an ambiguous markdown construct that can be rendered multiple ways.
/// </summary>
public sealed class AmbiguityCase
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Description { get; init; } = "";
    public string SourceMarkdown { get; init; } = "";
    public int SourceLine { get; init; }
    public AmbiguityKind Kind { get; init; }
    public List<RenderOption> Options { get; init; } = new();
}

public sealed class RenderOption
{
    public string Label { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string PreviewXml { get; init; } = ""; // Simplified OpenXML preview snippet
    public int Priority { get; init; } // Lower = more likely default
}

public enum AmbiguityKind
{
    DiagramSize,        // Mermaid too large for page
    GridTableOrAscii,   // +---+---+ lines
    DefinitionOrQuote,  // : prefix line
    StyledHtmlBlock,    // <div style="..."> rendering
    UnknownFenceLanguage, // Unrecognized code fence
    DetailsLayout,      // <details> rendering style
    TableInBlockquote,  // Table nested in > quote
}

/// <summary>
/// Ambiguity resolution mode stored in AppSettings.
/// </summary>
public enum AmbiguityMode
{
    AlwaysAsk = 0,      // Show the picker UI every time
    UseDefault = 1,     // Silently pick the highest-priority option
    RememberChoices = 2 // Remember per-kind user selections
}

/// <summary>
/// Stores a user's remembered choice for a specific ambiguity kind.
/// </summary>
public sealed class AmbiguityPreference
{
    public AmbiguityKind Kind { get; init; }
    public string ChosenLabel { get; init; } = "";
}
