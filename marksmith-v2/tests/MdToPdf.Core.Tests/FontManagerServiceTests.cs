using System;
using System.IO;
using System.Linq;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

/// <summary>Unit tests for the Task 16 typography preset + custom-font embedding engine.</summary>
public sealed class FontManagerServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FontManagerServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fontmgr_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteFont(string name, string ext, byte[]? bytes = null)
    {
        var path = Path.Combine(_tempDir, name + ext);
        File.WriteAllBytes(path, bytes ?? new byte[] { 0x00, 0x01, 0x00, 0x00 });
        return path;
    }

    // ---- Preset catalog -------------------------------------------------------------

    [Fact]
    public void Presets_ContainAllRequiredCategories()
    {
        var ids = FontManagerService.Presets.Select(p => p.Id).ToArray();
        Assert.Contains("Serif", ids);
        Assert.Contains("Sans-Serif", ids);
        Assert.Contains("Monospace", ids);
        Assert.Contains("Dyslexic-friendly", ids);
        Assert.Contains(FontManagerService.SystemPresetId, ids);
    }

    [Fact]
    public void Presets_AllHaveNonEmptyCssStacks()
    {
        foreach (var p in FontManagerService.Presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.CssStack));
            Assert.False(string.IsNullOrWhiteSpace(p.DisplayName));
        }
    }

    [Theory]
    [InlineData("serif", "Serif")]
    [InlineData("MONOSPACE", "Monospace")]
    [InlineData("  Sans-Serif  ", "Sans-Serif")]
    [InlineData("dyslexic-friendly", "Dyslexic-friendly")]
    public void FindPreset_MatchesCaseInsensitively_AndTrims(string input, string expectedId)
    {
        var preset = FontManagerService.FindPreset(input);
        Assert.NotNull(preset);
        Assert.Equal(expectedId, preset!.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotARealPreset")]
    public void FindPreset_ReturnsNull_ForUnknownOrEmpty(string? input)
    {
        Assert.Null(FontManagerService.FindPreset(input));
    }

    // ---- CSS stack resolution -------------------------------------------------------

    [Fact]
    public void ResolveCssStack_ReturnsPresetStack_ForKnownPreset()
    {
        var stack = FontManagerService.ResolveCssStack("Serif");
        Assert.Contains("Cambria", stack);
        Assert.EndsWith("serif", stack);
    }

    [Fact]
    public void ResolveCssStack_EmptyOrNull_ReturnsDefaultStack()
    {
        Assert.Equal(FontManagerService.DefaultStack, FontManagerService.ResolveCssStack(null));
        Assert.Equal(FontManagerService.DefaultStack, FontManagerService.ResolveCssStack(""));
        Assert.Equal(FontManagerService.DefaultStack, FontManagerService.ResolveCssStack("   "));
    }

    [Fact]
    public void ResolveCssStack_TreatsUnknownSelection_AsCustomFontName()
    {
        var stack = FontManagerService.ResolveCssStack("My Brand Font");
        Assert.StartsWith("\"My Brand Font\",", stack);
        Assert.Contains("sans-serif", stack);
    }

    [Fact]
    public void ResolveCssStack_StripsEmbeddedQuotes_FromCustomFontName()
    {
        var stack = FontManagerService.ResolveCssStack("\"Evil\" Font");
        Assert.DoesNotContain("\"\"", stack);
        Assert.StartsWith("\"Evil Font\",", stack);
    }

    // ---- Font file validation -------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsEmbeddableFontFile_RejectsNullOrEmpty(string? path)
    {
        Assert.False(FontManagerService.IsEmbeddableFontFile(path));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".png")]
    [InlineData(".docx")]
    [InlineData("")]
    public void IsEmbeddableFontFile_RejectsNonFontExtensions(string ext)
    {
        var path = WriteFont("notafont", ext.Length == 0 ? ".txt" : ext);
        Assert.False(FontManagerService.IsEmbeddableFontFile(path));
    }

    [Fact]
    public void IsEmbeddableFontFile_RejectsMissingFontFile()
    {
        var missing = Path.Combine(_tempDir, "does_not_exist.ttf");
        Assert.False(FontManagerService.IsEmbeddableFontFile(missing));
    }

    [Theory]
    [InlineData(".ttf")]
    [InlineData(".otf")]
    [InlineData(".woff")]
    [InlineData(".woff2")]
    public void IsEmbeddableFontFile_AcceptsExistingFontFiles(string ext)
    {
        var path = WriteFont("validfont", ext);
        Assert.True(FontManagerService.IsEmbeddableFontFile(path));
    }

    // ---- @font-face builder ---------------------------------------------------------

    [Fact]
    public void BuildFontFaceCss_ReturnsNull_ForMissingFile()
    {
        var missing = Path.Combine(_tempDir, "missing.ttf");
        Assert.Null(FontManagerService.BuildFontFaceCss(missing));
    }

    [Fact]
    public void BuildFontFaceCss_ReturnsNull_ForNonFontFile()
    {
        var path = WriteFont("readme", ".txt");
        Assert.Null(FontManagerService.BuildFontFaceCss(path));
    }

    [Fact]
    public void BuildFontFaceCss_EmbedsBase64_AndTruetypeFormat_ForTtf()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var path = WriteFont("MyCustomFont", ".ttf", bytes);
        var css = FontManagerService.BuildFontFaceCss(path);

        Assert.NotNull(css);
        Assert.StartsWith("@font-face", css);
        Assert.Contains("font-family: \"MyCustomFont\"", css);
        Assert.Contains("data:font/ttf;base64," + Convert.ToBase64String(bytes), css);
        Assert.Contains("format(\"truetype\")", css);
    }

    [Fact]
    public void BuildFontFaceCss_UsesOpentypeFormat_ForOtf()
    {
        var path = WriteFont("OpenFont", ".otf");
        var css = FontManagerService.BuildFontFaceCss(path);
        Assert.NotNull(css);
        Assert.Contains("data:font/otf;base64,", css);
        Assert.Contains("format(\"opentype\")", css);
    }

    [Fact]
    public void BuildFontFaceCss_UsesWoff2Format_ForWoff2()
    {
        var path = WriteFont("WebFont", ".woff2");
        var css = FontManagerService.BuildFontFaceCss(path);
        Assert.NotNull(css);
        Assert.Contains("data:font/woff2;base64,", css);
        Assert.Contains("format(\"woff2\")", css);
    }

    [Fact]
    public void GetFontFamilyName_StripsDirectoryAndExtension()
    {
        var path = Path.Combine(_tempDir, "Brand Font.otf");
        Assert.Equal("Brand Font", FontManagerService.GetFontFamilyName(path));
    }
}
