using System;
using MdToPdf.Models;
using Xunit;

namespace MdToPdf.Core.Tests;

// Task 15 — Export History. HistoryEntry is a pure model (formatting + title extraction), so these
// are fully deterministic. Persistence (HistoryService) is best-effort file I/O and not unit-tested.
public sealed class HistoryEntryTests
{
    // ---- Output size formatting ----

    [Theory]
    [InlineData(0, "")]
    [InlineData(-5, "")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    public void OutputSizeText_SmallOrUnknown(long bytes, string expected)
    {
        Assert.Equal(expected, new HistoryEntry { OutputSizeBytes = bytes }.OutputSizeText);
    }

    [Fact]
    public void OutputSizeText_FormatsKilobytesAndMegabytes()
    {
        Assert.Equal("18.4 KB", new HistoryEntry { OutputSizeBytes = (long)(18.4 * 1024) }.OutputSizeText);
        Assert.Equal("1.5 MB", new HistoryEntry { OutputSizeBytes = (long)(1.5 * 1024 * 1024) }.OutputSizeText);
    }

    // ---- Duration formatting ----

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(320, "320 ms")]
    [InlineData(999, "999 ms")]
    public void DurationText_ShortOrUnknown(long ms, string expected)
    {
        Assert.Equal(expected, new HistoryEntry { DurationMs = ms }.DurationText);
    }

    [Fact]
    public void DurationText_FormatsSeconds()
    {
        Assert.Equal("2.5 s", new HistoryEntry { DurationMs = 2500 }.DurationText);
    }

    // ---- Subtitle composition ----

    [Fact]
    public void Subtitle_OmitsTelemetry_WhenUnknown()
    {
        var ts = new DateTime(2026, 7, 27, 8, 0, 0);
        var e = new HistoryEntry { Kind = "PDF", Detected = "ChatGPT", Theme = "GitHub Light", Timestamp = ts };
        // Build the expected with the same culture-dependent ":g" format so the test is culture-invariant.
        Assert.Equal($"PDF · ChatGPT · GitHub Light · {ts:g}", e.Subtitle);
    }

    [Fact]
    public void Subtitle_AppendsDurationAndSize_WhenKnown()
    {
        var ts = new DateTime(2026, 7, 27, 8, 0, 0);
        var e = new HistoryEntry
        {
            Kind = "DOCX", Detected = "Gemini", Theme = "Nova", Timestamp = ts,
            DurationMs = 1500, OutputSizeBytes = 2048,
        };
        Assert.Equal($"DOCX · Gemini · Nova · {ts:g} · 1.5 s · 2 KB", e.Subtitle);
    }

    // ---- Title extraction ----

    [Fact]
    public void ExtractTitle_PrefersYamlFrontMatterTitle()
    {
        var md = "---\ntitle: My Report\nauthor: Q\n---\n# Heading";
        Assert.Equal("My Report", HistoryEntry.ExtractTitle(md));
    }

    [Fact]
    public void ExtractTitle_FallsBackToFirstHeading()
    {
        Assert.Equal("Overview", HistoryEntry.ExtractTitle("Some intro\n## Overview\nbody"));
    }

    [Fact]
    public void ExtractTitle_FallsBackToFirstMeaningfulLine_SkippingFencesAndQuotes()
    {
        var md = "```\ncode\n```\n> a quote\nFirst real line";
        Assert.Equal("First real line", HistoryEntry.ExtractTitle(md));
    }

    [Fact]
    public void ExtractTitle_StripsLinksAndEmphasis()
    {
        Assert.Equal("Read the docs now", HistoryEntry.ExtractTitle("Read [the docs](https://x.y) **now**"));
    }

    [Fact]
    public void ExtractTitle_CapsVeryLongTitles()
    {
        var longTitle = new string('a', 200);
        var title = HistoryEntry.ExtractTitle(longTitle);
        Assert.True(title.Length <= 80);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void ExtractTitle_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal("", HistoryEntry.ExtractTitle(""));
        Assert.Equal("", HistoryEntry.ExtractTitle("   \n  "));
    }

    [Fact]
    public void Title_FallsBackGracefully()
    {
        Assert.Equal("Untitled document", new HistoryEntry().Title);
        Assert.Equal("file.md", new HistoryEntry { SourceLabel = "file.md" }.Title);
        Assert.Equal("Doc Title", new HistoryEntry { DocumentTitle = "Doc Title", SourceLabel = "file.md" }.Title);
    }
}
