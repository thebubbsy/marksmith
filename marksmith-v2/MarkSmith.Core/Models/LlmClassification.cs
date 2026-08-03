namespace MarkSmith.Models;

public enum LlmSource
{
    Generic,
    ChatGpt,
    Gemini,
    Claude,
    Copilot,
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

    // Model label reported by the browser extension when the page exposes one (e.g. "GPT-4o").
    // Best-effort; null when unknown. Enriches the source badge/attribution, never gates it.
    public string? Model { get; set; }

    public string SourceName => Source switch
    {
        LlmSource.ChatGpt => "ChatGPT",
        LlmSource.Gemini => "Gemini",
        LlmSource.Claude => "Claude",
        LlmSource.Copilot => "Copilot",
        _ => "Markdown",
    };

    // "ChatGPT (GPT-4o)" when a model is known, else just "ChatGPT" — used wherever attribution
    // wants the fullest available description of the source.
    public string SourceDescription =>
        string.IsNullOrWhiteSpace(Model) ? SourceName : $"{SourceName} ({Model})";
}
