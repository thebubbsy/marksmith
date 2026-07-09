using MdToPdf.Models;

namespace MdToPdf.Services;

// 1:1 port of the THEMES dict in md_to_pdf_tui.py — same hex values, same 10 themes.
public sealed class ThemeCatalog
{
    public IReadOnlyList<ThemeDefinition> All { get; } = new List<ThemeDefinition>
    {
        new("GitHub Light",    "#ffffff", "#1b1f23", "#000000", "#f6f8fa", "#d1d5da", "#000000", "#f6f8fa", "#333333"),
        new("GitHub Dark",     "#0d1117", "#c9d1d9", "#58a6ff", "#161b22", "#30363d", "#c9d1d9", "#161b22", "#8b949e"),
        new("Solarized Light", "#fdf6e3", "#657b83", "#b58900", "#eee8d5", "#93a1a1", "#657b83", "#eee8d5", "#586e75"),
        new("Solarized Dark",  "#002b36", "#839496", "#b58900", "#073642", "#586e75", "#93a1a1", "#073642", "#839496"),
        new("Dracula",         "#282a36", "#f8f8f2", "#bd93f9", "#44475a", "#6272a4", "#f8f8f2", "#282a36", "#bd93f9"),
        new("Monokai Pro",     "#2d2a2e", "#fcfcfa", "#ffd866", "#19181a", "#5d5d5d", "#fcfcfa", "#2d2a2e", "#ffd866"),
        new("Cyberpunk",       "#05051e", "#00ff9f", "#ff003c", "#0d0221", "#00ff9f", "#f5ed00", "#0d0221", "#00ff9f"),
        new("Nordic",          "#2e3440", "#eceff4", "#88c0d0", "#3b4252", "#4c566a", "#d8dee9", "#2e3440", "#81a1c1"),
        new("Forest",          "#0b1a0b", "#d4e1d4", "#78a75a", "#1a2f1a", "#3d5a3d", "#a3bfa3", "#0b1a0b", "#78a75a"),
        new("Obsidian",        "#050000", "#e0e0e0", "#ff4500", "#1a0000", "#ff0000", "#ff4500", "#050000", "#ff0000"),
    };

    public ThemeDefinition GetOrDefault(string name) =>
        All.FirstOrDefault(t => t.Name == name) ?? All[0];
}
