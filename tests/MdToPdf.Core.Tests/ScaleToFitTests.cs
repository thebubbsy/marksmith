using System;
using System.Linq;
using MdToPdf.Services.Mermaid;
using Xunit;

namespace MdToPdf.Core.Tests;

public class ScaleToFitTests
{
    private MDiagram CreateLargeDiagram()
    {
        var d = new MDiagram { Width = 2000, Height = 1000 };
        // Shapes spaced out
        d.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 0, Y = 0, W = 100, H = 50 });
        d.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 1900, Y = 950, W = 100, H = 50 });
        return d;
    }

    [Fact]
    public void ScaleToFit_Mode0_ReturnsTrueForHugeDiagram()
    {
        var huge = new MDiagram { Width = 4000, Height = 1000 };
        huge.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 0, Y = 0, W = 100, H = 50 });
        huge.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 3900, Y = 950, W = 100, H = 50 });
        bool isOversizedHuge = DocxShapeEmitter.ScaleToFit(huge, oversizedMode: 0); 
        Assert.True(isOversizedHuge);
    }

    [Fact]
    public void ScaleToFit_Mode6_ShrinksSpacingOnly()
    {
        var d = CreateLargeDiagram();
        bool isOversized = DocxShapeEmitter.ScaleToFit(d, oversizedMode: 6); 
        Assert.False(isOversized);
        var a = d.Shapes[0];
        var b = d.Shapes[1];
        double finalSpanX = b.X + b.W - a.X;
        Assert.True(finalSpanX <= 631.0);
    }
    
    [Fact]
    public void ScaleToFit_Mode7_ShrinksShapesOnly()
    {
        var d = CreateLargeDiagram();
        bool isOversized = DocxShapeEmitter.ScaleToFit(d, oversizedMode: 7); 
        Assert.False(isOversized);
        var a = d.Shapes[0];
        var b = d.Shapes[1];
        double finalSpanX = b.X + b.W - a.X;
        Assert.True(finalSpanX <= 631.0);
    }

    [Fact]
    public void ScaleToFit_Mode8_ShrinksBothEqually()
    {
        var d = CreateLargeDiagram();
        bool isOversized = DocxShapeEmitter.ScaleToFit(d, oversizedMode: 8); 
        Assert.False(isOversized);
        var a = d.Shapes[0];
        var b = d.Shapes[1];
        double finalSpanX = b.X + b.W - a.X;
        Assert.True(finalSpanX <= 631.0);
    }
}
