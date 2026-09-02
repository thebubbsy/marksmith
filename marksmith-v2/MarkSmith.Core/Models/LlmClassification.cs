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

    // Model label reported by the browser extension when the page exposes one (e.g. "Gemini 3.8 Flash", "GPT-4o").
    // Best-effort; null when unknown. Enriches the source badge/attribution, never gates it.
    public string? Model { get; set; }

    public bool IsReasoningModel =>
        (Model is not null && (
            Model.Contains("3.8", System.StringComparison.OrdinalIgnoreCase) ||
            Model.Contains("3.7", System.StringComparison.OrdinalIgnoreCase) ||
            Model.Contains("thinking", System.StringComparison.OrdinalIgnoreCase) ||
            Model.Contains("o1", System.StringComparison.OrdinalIgnoreCase) ||
            Model.Contains("o3", System.StringComparison.OrdinalIgnoreCase) ||
            Model.Contains("r1", System.StringComparison.OrdinalIgnoreCase))) ||
        Signals.Any(s => s.Contains("reasoning", System.StringComparison.OrdinalIgnoreCase) ||
                         s.Contains("thought", System.StringComparison.OrdinalIgnoreCase));

    public bool HasGroundingSources { get; set; }

    public string SourceName => Source switch
    {
        LlmSource.ChatGpt => "ChatGPT",
        LlmSource.Gemini => "Gemini",
        LlmSource.Claude => "Claude",
        LlmSource.Copilot => "Copilot",
        _ => "Markdown",
    };

    // "Gemini (Gemini 3.8 Flash)" when a model is known, else just "Gemini" — used wherever attribution
    // wants the fullest available description of the source.
    public string SourceDescription =>
        string.IsNullOrWhiteSpace(Model) ? SourceName : $"{SourceName} ({Model})";
}
