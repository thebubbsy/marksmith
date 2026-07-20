import sys, re

path = r"MdToPdf.Avalonia\Controls\Polyfills.cs"
with open(path, "r", encoding="utf-8") as f:
    lines = f.read()

repl = '''public class SymbolIcon : global::Avalonia.Controls.TextBlock
    {
        public static readonly StyledProperty<string> SymbolProperty = AvaloniaProperty.Register<SymbolIcon, string>("Symbol");
        public string Symbol { get => GetValue(SymbolProperty); set { SetValue(SymbolProperty, value); Text = value; } }
    }

    public class FontIcon : global::Avalonia.Controls.TextBlock
    {
        public static readonly StyledProperty<string> GlyphProperty = AvaloniaProperty.Register<FontIcon, string>("Glyph");
        public string Glyph { get => GetValue(GlyphProperty); set { SetValue(GlyphProperty, value); Text = value; } }
    }
'''

lines = re.sub(r'public class SymbolIcon.*\}', repl, lines, flags=re.DOTALL)
with open(path, "w", encoding="utf-8") as f:
    f.write(lines)
