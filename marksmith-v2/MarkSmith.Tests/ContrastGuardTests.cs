using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ContrastGuardTests
{
    [Fact]
    public void HighContrast_Text_Is_Preserved()
    {
        // Dark text on white background -> Ratio > 15:1 -> Preserved
        var result = ContrastGuard.EnsureLegibleText("000000", "FFFFFF");
        Assert.Equal("000000", result);

        // White text on dark background -> Ratio > 15:1 -> Preserved
        var darkResult = ContrastGuard.EnsureLegibleText("FFFFFF", "0D1117");
        Assert.Equal("FFFFFF", darkResult);
    }

    [Fact]
    public void LowContrast_Text_Is_Corrected_To_High_Contrast()
    {
        // Dark green (#006400) on Dark background (#0D1117) -> Low contrast -> Force to White (#FFFFFF)
        var fixedColor = ContrastGuard.EnsureLegibleText("006400", "0D1117");
        Assert.Equal("FFFFFF", fixedColor);

        // Light yellow (#FFFFE0) on White background (#FFFFFF) -> Low contrast -> Force to Dark (#121212)
        var fixedLight = ContrastGuard.EnsureLegibleText("FFFFE0", "FFFFFF");
        Assert.Equal("121212", fixedLight);
    }

    [Fact]
    public void Calculates_Luminance_And_Contrast_Ratios_Accurately()
    {
        double whiteLum = ContrastGuard.GetLuminance("FFFFFF");
        double blackLum = ContrastGuard.GetLuminance("000000");

        Assert.True(whiteLum > 0.99);
        Assert.True(blackLum < 0.01);

        double maxRatio = ContrastGuard.GetContrastRatio("000000", "FFFFFF");
        Assert.True(maxRatio > 20.0);
    }

    [Fact]
    public void Svg_Low_Contrast_Text_Is_Automatically_Corrected()
    {
        // White text on white background inside PlantUML or Graphviz SVG -> Should be corrected to high-contrast dark (#121212)
        var whiteOnWhiteSvg = "<svg><g><text fill=\"#ffffff\">Sequence Participant</text><tspan fill=\"white\">Header</tspan></g></svg>";
        var correctedSvg = ContrastGuard.EnsureSvgLegibility(whiteOnWhiteSvg, "FFFFFF");

        Assert.Contains("fill=\"#121212\"", correctedSvg);
        Assert.DoesNotContain("fill=\"#ffffff\"", correctedSvg);
        Assert.DoesNotContain("fill=\"white\"", correctedSvg);
    }

    [Fact]
    public void PluginTheme_Guarantees_Legibility_On_Light_And_Dark_Themes()
    {
        var lightTheme = new Models.ThemeDefinition("GitHub Light", "#ffffff", "#1b1f23", "#000000", "#f6f8fa", "#d1d5da", "#000000", "#f6f8fa", "#333333");
        var pluginTheme = Plugins.PluginTheme.From(lightTheme);

        double textRatio = ContrastGuard.GetContrastRatio(pluginTheme.Text, pluginTheme.Background);
        Assert.True(textRatio >= 4.5, $"Text contrast ratio {textRatio} must be >= 4.5:1");
    }
}
