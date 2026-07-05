namespace MdToPdf.Models;

public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string SourceLabel { get; set; } = "";   // input file name or "pasted"
    public string Detected { get; set; } = "";      // ChatGPT / Gemini / Claude / Markdown
    public string Theme { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Kind { get; set; } = "";          // PDF / DOCX

    public string Title => $"{Kind} · {SourceLabel}";
    public string Subtitle => $"{Detected} · {Theme} · {Timestamp:g}";
}
