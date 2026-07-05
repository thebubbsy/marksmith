namespace MdToPdf.Models;

public enum LlmSource
{
    Generic,
    ChatGpt,
    Gemini,
    Claude,
}

// Result of sniffing a Markdown blob for which AI assistant produced it, plus the cleanup
// actions that were (or would be) applied. Drives the preview badge, the export attribution
// strip, and the /api/classify endpoint.
public sealed class LlmClassification
{
    public LlmSource Source { get; init; } = LlmSource.Generic;
    public int Confidence { get; init; } // 0-100
    public List<string> Signals { get; init; } = new();
    public List<string> AppliedFixes { get; set; } = new();
    public bool HasMath { get; set; }

    public string SourceName => Source switch
    {
        LlmSource.ChatGpt => "ChatGPT",
        LlmSource.Gemini => "Gemini",
        LlmSource.Claude => "Claude",
        _ => "Markdown",
    };
}
