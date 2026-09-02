using System.IO;

namespace MarkSmith.Services
{
    public interface IReverseImportService
    {
        string ConvertDocxToMarkdown(string docxPath, ReverseImportOptions? options = null);
        string ConvertDocxToMarkdown(Stream docxStream, ReverseImportOptions? options = null);
    }

    public record ReverseImportOptions
    {
        public bool PreserveRevisionsAsCriticMarkup { get; init; } = true;
        public bool PreserveCommentsAsCriticMarkup { get; init; } = true;
        public bool CoalesceSubstitutions { get; init; } = true;
    }
}

namespace MarkSmith.Core.Services
{
    // Alias for PROJECT.md contract compatibility
    public interface IReverseImportService : global::MarkSmith.Services.IReverseImportService { }
}

