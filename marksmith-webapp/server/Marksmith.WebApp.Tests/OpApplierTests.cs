using MarkSmith.WebApp.Server.Documents;
using MarkSmith.WebApp.Server.Ot;
using Xunit;

namespace MarkSmith.WebApp.Tests;

/// <summary>
/// End-to-end OOXML mutation tests: real DOCX bytes through the OpenXml SDK public API. These
/// are the "Core is a black box" guarantee -- we never touch MarkSmith.Core, we only mutate
/// through DocumentFormat.OpenXml and validate the result with the SDK's own validator.
/// </summary>
public class OpApplierTests : IDisposable
{
    private readonly DocxDocument _doc;
    private readonly OpApplier _applier = new();
    private readonly DocxValidator _validator = new();

    public OpApplierTests()
    {
        _doc = DocxDocument.CreateBlank();
    }

    public void Dispose() => _doc.Dispose();

    private static Operation Op(string id, OpType type, int? block = null, int? offset = null,
        int? length = null, string? text = null, string? style = null, string? dataUri = null,
        string? imageId = null)
        => new()
        {
            Id = id, ClientId = "tester", Type = type,
            Block = block, Offset = offset, Length = length, Text = text, Style = style,
            DataUri = dataUri, ImageId = imageId,
        };

    private void AssertValid()
    {
        var problems = _validator.Validate(_doc);
        Assert.Empty(problems);
    }

    [Fact]
    public void InsertText_ThenDeleteText_ReturnsToOriginal()
    {
        var ins = _applier.Apply(_doc, Op("1", OpType.InsertText, block: 0, offset: 0, text: "Hello, world!"));
        Assert.True(ins.Ok);

        var text = DocxDocument.ParagraphText(_doc.ParagraphAt(0)!);
        Assert.Equal("Hello, world!", text);

        var del = _applier.Apply(_doc, Op("2", OpType.DeleteText, block: 0, offset: 0, length: 13));
        Assert.True(del.Ok);
        Assert.Equal("", DocxDocument.ParagraphText(_doc.ParagraphAt(0)!));
        AssertValid();
    }

    [Fact]
    public void InsertText_AtMiddle_SplitsRuns()
    {
        _applier.Apply(_doc, Op("1", OpType.InsertText, block: 0, offset: 0, text: "AAAA"));
        _applier.Apply(_doc, Op("2", OpType.InsertText, block: 0, offset: 2, text: "BB"));

        Assert.Equal("AABBAA", DocxDocument.ParagraphText(_doc.ParagraphAt(0)!));
        AssertValid();
    }

    [Fact]
    public void DeleteText_AcrossMiddle_RemovesExactRange()
    {
        _applier.Apply(_doc, Op("1", OpType.InsertText, block: 0, offset: 0, text: "0123456789"));
        var del = _applier.Apply(_doc, Op("2", OpType.DeleteText, block: 0, offset: 2, length: 3));

        Assert.True(del.Ok);
        Assert.Equal("0156789", DocxDocument.ParagraphText(_doc.ParagraphAt(0)!));
        Assert.Equal("234", del.CapturedText);
        AssertValid();
    }

    [Fact]
    public void DeleteText_OutOfBounds_Rejects_AndMakesNoChange()
    {
        _applier.Apply(_doc, Op("1", OpType.InsertText, block: 0, offset: 0, text: "abc"));
        var del = _applier.Apply(_doc, Op("2", OpType.DeleteText, block: 0, offset: 1, length: 5));

        Assert.False(del.Ok);
        Assert.Equal("abc", DocxDocument.ParagraphText(_doc.ParagraphAt(0)!));
    }

    [Fact]
    public void ApplyFormatting_Bold_TogglesRunProperties()
    {
        _applier.Apply(_doc, Op("1", OpType.InsertText, block: 0, offset: 0, text: "hello"));
        var fmt = _applier.Apply(_doc, new Operation
        {
            Id = "2", ClientId = "tester", Type = OpType.ApplyFormatting,
            Block = 0, Offset = 0, Length = 5, Format = new Formatting { Bold = true },
        });

        Assert.True(fmt.Ok);
        var run = _doc.ParagraphAt(0)!.Elements<DocumentFormat.OpenXml.Wordprocessing.Run>().First();
        Assert.Equal(true, run.RunProperties?.Bold?.Val?.Value);
        AssertValid();
    }

    [Fact]
    public void InsertParagraph_AddsBlockWithStyle()
    {
        _applier.Apply(_doc, Op("1", OpType.InsertParagraph, block: 0, style: "Heading1"));
        var blocks = _doc.Blocks();
        Assert.Equal(2, blocks.Count); // blank + inserted
        var p = _doc.ParagraphAt(0)!;
        Assert.Equal("Heading1", p.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        AssertValid();
    }

    [Fact]
    public void InsertTable_ThenRowOps_KeepValid()
    {
        var ins = _applier.Apply(_doc, new Operation
        {
            Id = "1", ClientId = "tester", Type = OpType.InsertTable,
            Block = 1, Rows = 2, Cols = 3,
        });
        Assert.True(ins.Ok);

        var t1 = _applier.Apply(_doc, new Operation
        {
            Id = "2", ClientId = "tester", Type = OpType.InsertTableRow, Block = 1, Row = 1,
        });
        Assert.True(t1.Ok);
        Assert.Equal(3, _doc.TableAt(1)!.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>().Count());

        var t2 = _applier.Apply(_doc, new Operation
        {
            Id = "3", ClientId = "tester", Type = OpType.DeleteTableRow, Block = 1, Row = 2,
        });
        Assert.True(t2.Ok);
        Assert.Equal(2, _doc.TableAt(1)!.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>().Count());
        AssertValid();
    }

    [Fact]
    public void DeleteTableRow_OnLastRow_Rejects()
    {
        _applier.Apply(_doc, new Operation
        {
            Id = "1", ClientId = "tester", Type = OpType.InsertTable, Block = 1, Rows = 1, Cols = 2,
        });
        var del = _applier.Apply(_doc, new Operation
        {
            Id = "2", ClientId = "tester", Type = OpType.DeleteTableRow, Block = 1, Row = 0,
        });
        Assert.False(del.Ok);
    }

    [Fact]
    public void DeleteParagraph_ThenDocument_StaysValid()
    {
        _applier.Apply(_doc, Op("1", OpType.InsertText, block: 0, offset: 0, text: "keep me"));
        _applier.Apply(_doc, Op("2", OpType.InsertParagraph, block: 1, style: "Normal"));
        _applier.Apply(_doc, Op("3", OpType.InsertText, block: 1, offset: 0, text: "delete me"));

        var del = _applier.Apply(_doc, Op("4", OpType.DeleteParagraph, block: 1));
        Assert.True(del.Ok);

        // Body must never be empty; deleting the second paragraph leaves the first.
        Assert.Equal("keep me", DocxDocument.ParagraphText(_doc.ParagraphAt(0)!));
        AssertValid();
    }

    [Fact]
    public void SaveToBytes_RoundTrips()
    {
        _applier.Apply(_doc, Op("1", OpType.InsertText, block: 0, offset: 0, text: "round trip"));
        var bytes = _doc.SaveToBytes();

        using var reloaded = DocxDocument.Open(bytes);
        Assert.Equal("round trip", DocxDocument.ParagraphText(reloaded.ParagraphAt(0)!));
        AssertValid();
    }
}
