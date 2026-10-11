using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarkSmith.Core.Composer;
using MarkSmith.Ocr;
using MarkSmith.Services;
using SkiaSharp;
using Xunit;

namespace MarkSmith.Core.Tests.Ocr;

public class OcrExperienceAndVisualsTests
{
    private static OcrLine MakeLine(string text, float x, float y, float height = 24f, float wordStep = 65f)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = parts
            .Select((w, i) => new OcrWord(w, x + i * wordStep, y, Math.Max(28f, w.Length * 10f), height))
            .ToList();
        return new OcrLine(text, words, y, height);
    }

    [Fact]
    public void OcrTrace_Build_Produces_Normalized_Word_And_Block_Boxes_In_Reading_Order()
    {
        var page = new OcrPageResult(new List<OcrLine>
        {
            MakeLine("Quarterly Overview", 80, 60, height: 44f, wordStep: 150f),
            MakeLine("Revenue grew strongly across every region this quarter.", 80, 150, height: 22f),
            MakeLine("• North America led enterprise adoption", 90, 220, height: 22f),
            MakeLine("• Europe expanded partner channels", 90, 252, height: 22f),
        }, 1000, 1400);

        var trace = OcrTrace.Build(1, page, TimeSpan.FromMilliseconds(42));

        Assert.Equal(1, trace.PageNumber);
        Assert.Equal(3, trace.Blocks.Count);
        Assert.Equal(OcrBlockKind.Heading, trace.Blocks[0].Kind);
        Assert.Equal(OcrBlockKind.Paragraph, trace.Blocks[1].Kind);
        Assert.Equal(OcrBlockKind.List, trace.Blocks[2].Kind);
        Assert.Equal(1, trace.HeadingCount);
        Assert.Equal(1, trace.ListCount);
        Assert.True(trace.Words.Count >= 18);
        Assert.All(trace.Words, w =>
        {
            Assert.InRange(w.Box.X, 0f, 1f);
            Assert.InRange(w.Box.Y, 0f, 1f);
            Assert.InRange(w.Box.Right, 0f, 1f);
            Assert.InRange(w.Box.Bottom, 0f, 1f);
        });
        Assert.Equal(OcrMarkdown.FromPage(page), trace.Markdown);
    }

    [Fact]
    public void OcrTrace_NotifyStarted_And_NotifyRead_Drive_Observer_Safely()
    {
        using var bmp = new SKBitmap(800, 1000);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
        }

        var page = new OcrPageResult(new List<OcrLine>
        {
            MakeLine("Hello World", 100, 120, height: 24f),
        }, 800, 1000);

        var observer = new RecordingObserver();
        OcrTrace.NotifyStarted(observer, bmp, 1, 0, 1, "Windows OCR");
        OcrTrace.NotifyRead(observer, 1, page, TimeSpan.FromMilliseconds(19));

        Assert.Single(observer.Started);
        Assert.Equal("Windows OCR", observer.Started[0].Engine);
        Assert.True(observer.Started[0].Image.Length > 100);

        Assert.Single(observer.Read);
        Assert.Equal(2, observer.Read[0].Words.Count);
        Assert.Contains("Hello World", observer.Read[0].Markdown);
    }

    [Fact]
    public void OcrVisualElements_Reconstructs_Box_And_Connector_Diagram_With_Mermaid_And_ShapeForge()
    {
        using var bmp = new SKBitmap(1000, 900, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);

            using var boxStroke = new SKPaint
            {
                Color = new SKColor(30, 64, 175),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 5,
                IsAntialias = false
            };

            // Draw three flowchart boxes connected horizontally: [Ingest] ---> [Transform] ---> [Export]
            canvas.DrawRect(new SKRect(90, 260, 290, 350), boxStroke);
            canvas.DrawLine(290, 305, 400, 305, boxStroke);
            canvas.DrawRect(new SKRect(400, 260, 600, 350), boxStroke);
            canvas.DrawLine(600, 305, 710, 305, boxStroke);
            canvas.DrawRect(new SKRect(710, 260, 910, 350), boxStroke);
        }

        var lines = new List<OcrLine>
        {
            MakeLine("Pipeline Architecture", 90, 80, height: 42f, wordStep: 170f),
            new("Ingest", new List<OcrWord> { new("Ingest", 150, 292, 80, 24) }, 292, 24),
            new("Transform", new List<OcrWord> { new("Transform", 450, 292, 100, 24) }, 292, 24),
            new("Export", new List<OcrWord> { new("Export", 770, 292, 80, 24) }, 292, 24),
            MakeLine("Each stage validates the document before passing it downstream", 90, 520, height: 22f),
            MakeLine("so downstream consumers receive structured Markdown.", 90, 550, height: 22f),
        };
        var recognized = new OcrPageResult(lines, 1000, 900);

        var extracted = OcrVisualElements.ExtractPage(bmp, recognized);

        Assert.Equal(3, extracted.Blocks.Count);
        Assert.Equal(OcrBlockKind.Heading, extracted.Blocks[0].Kind);
        Assert.Equal(OcrBlockKind.Diagram, extracted.Blocks[1].Kind);
        Assert.Equal(OcrBlockKind.Paragraph, extracted.Blocks[2].Kind);

        string diagramMd = extracted.Blocks[1].Markdown;
        Assert.StartsWith(":::shapes", diagramMd);
        Assert.Contains("# mermaid: flowchart LR", diagramMd);
        Assert.Contains("Ingest", diagramMd);
        Assert.Contains("Transform", diagramMd);
        Assert.Contains("Export", diagramMd);

        // Verify the ShapeForge block round-trips into ComposedShapes (nodes + connector lines + arrowheads).
        string inner = string.Join("\n", diagramMd.Split('\n').Skip(1).TakeWhile(l => !l.Trim().Equals(":::", StringComparison.Ordinal)));
        var shapes = ShapeMarkdownCodec.Parse(inner);
        Assert.True(shapes.Count >= 5);
        Assert.Contains(shapes, s => s.Text == "Ingest");
        Assert.Contains(shapes, s => s.Text == "Transform");
        Assert.Contains(shapes, s => s.Text == "Export");
        Assert.Contains(shapes, s => s.Prst == "line" && s.PathPoints is { Count: >= 2 });

        // Ensure diagram labels were NOT duplicated as stray paragraphs outside the :::shapes block.
        Assert.DoesNotContain(extracted.Blocks.Where(b => b.Kind != OcrBlockKind.Diagram), b => b.Markdown.Contains("Transform"));

        // Verify OcrTrace includes the diagram block and its words.
        var trace = OcrTrace.Build(1, recognized, TimeSpan.FromMilliseconds(25), extracted.Blocks);
        Assert.Equal(1, trace.DiagramCount);
    }

    [Fact]
    public void OcrVisualElements_Composes_Graphic_Illustration_Into_ShapeForge_Mosaic()
    {
        using var bmp = new SKBitmap(900, 900, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);

            // Draw a colourful illustration graphic on white paper in the middle of the page
            using var paint1 = new SKPaint { Color = new SKColor(236, 72, 153), Style = SKPaintStyle.Fill };
            using var paint2 = new SKPaint { Color = new SKColor(14, 165, 233), Style = SKPaintStyle.Fill };
            using var paint3 = new SKPaint { Color = new SKColor(245, 158, 11), Style = SKPaintStyle.Fill };
            canvas.DrawCircle(320, 420, 85, paint1);
            canvas.DrawRect(new SKRect(390, 340, 560, 500), paint2);
            canvas.DrawCircle(590, 420, 75, paint3);
        }

        var recognized = new OcrPageResult(new List<OcrLine>
        {
            MakeLine("Visual Design Summary", 80, 80, height: 40f, wordStep: 140f),
            MakeLine("The graphic above is converted into native Word DrawingML", 80, 680, height: 22f),
            MakeLine("shapes so it stays editable inside the exported document.", 80, 710, height: 22f),
        }, 900, 900);

        var extracted = OcrVisualElements.ExtractPage(bmp, recognized);

        Assert.Equal(3, extracted.Blocks.Count);
        Assert.Equal(OcrBlockKind.Heading, extracted.Blocks[0].Kind);
        Assert.Equal(OcrBlockKind.ShapeMosaic, extracted.Blocks[1].Kind);
        Assert.Equal(OcrBlockKind.Paragraph, extracted.Blocks[2].Kind);

        string mosaicMd = extracted.Blocks[1].Markdown;
        Assert.StartsWith(":::shapes compact=true", mosaicMd);
        var decodedShapes = ShapeMarkdownCodec.Parse(mosaicMd);
        Assert.True(decodedShapes.Count >= 50);
    }

    [Fact]
    public void OcrVisualElements_Extracts_Continuous_Tone_Photo_To_Media_Folder()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "marksmith_ocr_pic_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var bmp = new SKBitmap(900, 900, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.White);
                // Render a rich multi-hue continuous-tone photograph rectangle at y=240..560, x=140..720
                for (int y = 240; y < 560; y++)
                {
                    for (int x = 140; x < 720; x++)
                    {
                        byte r = (byte)(40 + ((x - 140) * 200 / 580));
                        byte g = (byte)(30 + ((y - 240) * 200 / 320));
                        byte b = (byte)(80 + (((x + y) % 180)));
                        bmp.SetPixel(x, y, new SKColor(r, g, b));
                    }
                }
            }

            var recognized = new OcrPageResult(new List<OcrLine>
            {
                MakeLine("Field Site Inspection", 140, 90, height: 40f, wordStep: 140f),
                MakeLine("Photograph captured at the northern substation.", 140, 660, height: 22f),
            }, 900, 900);

            var extracted = OcrVisualElements.ExtractPage(bmp, recognized, new OcrVisualOptions
            {
                PageNumber = 1,
                MediaDirectory = tempDir,
                MediaLink = "doc_media",
            });

            Assert.Equal(1, extracted.PicturesSaved);
            Assert.Contains(extracted.Blocks, b => b.Kind == OcrBlockKind.Picture);
            Assert.Contains("![Picture from page 1](doc_media/page1-picture1.png)", extracted.Markdown);
            Assert.True(File.Exists(Path.Combine(tempDir, "page1-picture1.png")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void OcrVisualElements_Leaves_Pure_Text_Pages_Completely_Untouched()
    {
        using var bmp = new SKBitmap(1000, 1200, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
        }

        var recognized = new OcrPageResult(new List<OcrLine>
        {
            MakeLine("Executive Summary", 100, 100, height: 42f, wordStep: 160f),
            MakeLine("All text paragraphs remain pure Markdown text without false visuals.", 100, 200, height: 22f),
        }, 1000, 1200);

        var extracted = OcrVisualElements.ExtractPage(bmp, recognized);
        Assert.All(extracted.Blocks, b => Assert.True(b.Kind is OcrBlockKind.Heading or OcrBlockKind.Paragraph or OcrBlockKind.List));
        Assert.Equal(OcrMarkdown.FromPage(recognized), extracted.Markdown);
    }

    [Fact]
    public void OcrTrace_SynthesizeFromMarkdown_Generates_Preview_And_Trace_For_Any_Import()
    {
        const string markdown = """
            # Imported Architecture Document

            This document was imported from Word and contains text, lists, pictures, diagrams, and mosaics.

            - First imported item
            - Second imported item

            ![Architecture Photo](media/photo1.png)

            :::shapes
            # mermaid: flowchart LR; n1["Ingest"] --> n2["Transform"]
            roundRect 0.1,0.2,0.3,0.4 fill=#1E40AF text="Ingest"
            roundRect 0.5,0.2,0.3,0.4 fill=#047857 text="Transform"
            :::

            :::shapes compact=true
            rect 0.1,0.1,0.2,0.2 fill=#38BDF8
            :::
            """;

        var (preview, trace) = OcrTrace.SynthesizeFromMarkdown(markdown, "MarkSmith Word (.docx) Importer");

        Assert.Equal("MarkSmith Word (.docx) Importer", preview.Engine);
        Assert.True(preview.Image.Length > 500);
        Assert.Equal(960, preview.ImageWidth);
        Assert.Equal(1320, preview.ImageHeight);

        Assert.Equal(6, trace.Blocks.Count);
        Assert.Equal(OcrBlockKind.Heading, trace.Blocks[0].Kind);
        Assert.Equal(OcrBlockKind.Paragraph, trace.Blocks[1].Kind);
        Assert.Equal(OcrBlockKind.List, trace.Blocks[2].Kind);
        Assert.Equal(OcrBlockKind.Picture, trace.Blocks[3].Kind);
        Assert.Equal(OcrBlockKind.Diagram, trace.Blocks[4].Kind);
        Assert.Equal(OcrBlockKind.ShapeMosaic, trace.Blocks[5].Kind);
        Assert.True(trace.Words.Count >= 18);
        Assert.Equal(1, trace.PictureCount);
        Assert.Equal(2, trace.DiagramCount);
    }

    [Fact]
    public void OcrPlaybackSchedule_ForPage_Produces_Content_Invariant_Duration_Across_All_Document_Sizes()
    {
        var empty = OcrPlaybackSchedule.ForPage(blockCount: 0, wordCount: 0, pageIndex: 0, pageCount: 1, stage1ElapsedMs: 0);
        var tiny = OcrPlaybackSchedule.ForPage(blockCount: 1, wordCount: 2, pageIndex: 0, pageCount: 1, stage1ElapsedMs: 0);
        var medium = OcrPlaybackSchedule.ForPage(blockCount: 6, wordCount: 75, pageIndex: 0, pageCount: 1, stage1ElapsedMs: 0);
        var large = OcrPlaybackSchedule.ForPage(blockCount: 42, wordCount: 1250, pageIndex: 0, pageCount: 1, stage1ElapsedMs: 0);

        Assert.Equal(7280, empty.TotalSinglePageSessionMs);
        Assert.Equal(7280, tiny.TotalSinglePageSessionMs);
        Assert.Equal(7280, medium.TotalSinglePageSessionMs);
        Assert.Equal(7280, large.TotalSinglePageSessionMs);

        Assert.Equal(1600, tiny.Stage2TotalMs);
        Assert.Equal(1600, large.Stage2TotalMs);
        Assert.Equal(1600, tiny.Stage3TotalMs);
        Assert.Equal(1600, large.Stage3TotalMs);
        Assert.Equal(1800, tiny.Stage4TotalMs);
        Assert.Equal(1800, large.Stage4TotalMs);
        Assert.Equal(950, tiny.FinalSheetHoldMs);
        Assert.Equal(180, tiny.FadeOutMs);

        Assert.InRange(large.Stage2Steps, 1, 8);
        Assert.Equal(10, large.Stage3Steps);
        Assert.InRange(large.Stage4Steps, 1, 8);
    }

    [Fact]
    public void OcrPlaybackSchedule_ForPage_Subtracts_Elapsed_Stage1_Time_While_Enforcing_Sweep_Floor()
    {
        var fastOcr = OcrPlaybackSchedule.ForPage(blockCount: 5, wordCount: 50, pageIndex: 0, pageCount: 1, stage1ElapsedMs: 400);
        Assert.Equal(750, fastOcr.Stage1RemainingMs);
        Assert.Equal(1150, 400 + fastOcr.Stage1RemainingMs);

        var slowOcr = OcrPlaybackSchedule.ForPage(blockCount: 5, wordCount: 50, pageIndex: 0, pageCount: 1, stage1ElapsedMs: 4500);
        Assert.Equal(450, slowOcr.Stage1RemainingMs);
    }

    [Fact]
    public void OcrPlaybackSchedule_ForPage_Uses_Deterministic_FollowUp_Schedule_On_Subsequent_Pages()
    {
        var page2Small = OcrPlaybackSchedule.ForPage(blockCount: 2, wordCount: 15, pageIndex: 1, pageCount: 3, stage1ElapsedMs: 0);
        var page2Large = OcrPlaybackSchedule.ForPage(blockCount: 30, wordCount: 800, pageIndex: 1, pageCount: 3, stage1ElapsedMs: 0);

        Assert.Equal(page2Small.TotalPageMs, page2Large.TotalPageMs);
        Assert.Equal(960, page2Small.Stage2TotalMs);
        Assert.Equal(960, page2Small.Stage3TotalMs);
        Assert.Equal(1080, page2Small.Stage4TotalMs);
    }

    private sealed class RecordingObserver : IOcrObserver
    {
        public List<OcrPagePreview> Started { get; } = new();
        public List<OcrPageTrace> Read { get; } = new();
        public void PageStarted(OcrPagePreview preview) => Started.Add(preview);
        public void PageRead(OcrPageTrace trace) => Read.Add(trace);
    }
}
