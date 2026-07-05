namespace MdToPdf.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "GitHub Light";
    public int ContentWidth { get; set; } = 800;
    public bool MermaidEnabled { get; set; } = true;
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    public bool UnlimitedHeight { get; set; } = true;
    public bool A4FixedWidth { get; set; } = true;

    // AI ingest + normalization
    public bool NormalizeLlm { get; set; } = true;
    public bool AutoClipboardIngest { get; set; }
    public bool WatchFolderEnabled { get; set; }
    public string WatchFolder { get; set; } = "";
    public bool WatchFolderAutoConvert { get; set; }
    public bool MinimizeToTray { get; set; }

    // Export extras
    public bool IncludeToc { get; set; }
    public bool ShowAttribution { get; set; } = true;
    public bool NoEmoji { get; set; } // strip all emoji from preview + every export format
    public int DashMode { get; set; } // em-dash handling: 0 keep, 1 hyphen, 2 spaced, 3 custom
    public string DashCustom { get; set; } = ""; // replacement text when DashMode == 3

    // Formatting personalization (structure, not cleanup)
    public int HeadingShift { get; set; } // -5..+5: promote (-) or demote (+) every heading
    public int BoldMode { get; set; }     // 0 keep, 1 remove, 2 to italic
    public int ItalicMode { get; set; }   // 0 keep, 1 remove

    // Local REST API
    public bool ApiEnabled { get; set; }
    public int ApiPort { get; set; } = 47821;
}
