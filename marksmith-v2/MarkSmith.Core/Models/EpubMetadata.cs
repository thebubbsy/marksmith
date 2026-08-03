namespace MarkSmith.Models;

/// <summary>
/// Metadata fields and cover image configuration for EPUB3 exports (Task 20).
/// </summary>
public sealed record EpubMetadata
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Language { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Identifier { get; init; } = ""; // ISBN or custom UUID
    public string Description { get; init; } = "";
    public string Rights { get; init; } = "";
    public string CoverImagePath { get; init; } = "";
}
