using System;
using System.Collections.Generic;
using MarkSmith.Services.Analytics;
using MarkSmith.Services.Biology;
using MarkSmith.Services.Cad;
using MarkSmith.Services.Code;
using MarkSmith.Services.Geo;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle16Block4ExecutionTests
{
    [Fact]
    public void GeometricBlueprintService_ParsesAndRendersBlueprintSvg()
    {
        string bp = """
            :::blueprint Flange Plate
            line (0, 0) -> (100, 0) [100mm]
            rect (10, 10) 80x40 [80x40]
            circle (50, 30) r=15 [Ø30mm]
            :::
            """;

        var model = GeometricBlueprintService.ParseBlueprint(bp);
        Assert.Equal("Flange Plate", model.Title);
        Assert.Equal(3, model.Elements.Count);

        string svg = GeometricBlueprintService.RenderBlueprintSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("FLANGE PLATE", svg);
        Assert.Contains("100mm", svg);
        Assert.Contains("30mm", svg);
        Assert.Contains("class=\"bp-geom\"", svg);
    }

    [Fact]
    public void MarkdownGeoMapService_ParsesPinsAndRoutes_AndRendersMapSvg()
    {
        string mapMd = """
            :::map Server Backbones
            pin [40.7128, -74.0060] "New York"
            pin [51.5074, -0.1278] "London"
            route "New York" -> "London" [5,585 km]
            :::
            """;

        var model = MarkdownGeoMapService.ParseMap(mapMd);
        Assert.Equal("Server Backbones", model.Title);
        Assert.Equal(2, model.Pins.Count);
        Assert.Single(model.Routes);
        Assert.Equal("5,585 km", model.Routes[0].DistanceLabel);

        string svg = MarkdownGeoMapService.RenderMapSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Server Backbones", svg);
        Assert.Contains("New York", svg);
        Assert.Contains("London", svg);
        Assert.Contains("5,585 km", svg);
    }

    [Fact]
    public void MarkdownHistogramService_CalculatesBinsAndRendersSvg()
    {
        string histMd = """
            :::histogram Response Times
            Data: 10, 12, 14, 18, 20, 22, 25, 28, 30, 35, 40, 45, 50
            Bins: 4
            :::
            """;

        var model = MarkdownHistogramService.ParseHistogram(histMd);
        Assert.Equal("Response Times", model.Title);
        Assert.Equal(13, model.DataPoints.Count);
        Assert.Equal(4, model.Bins.Count);
        Assert.True(model.Mean > 20);

        string svg = MarkdownHistogramService.RenderHistogramSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Response Times", svg);
        Assert.Contains("class=\"h-bar\"", svg);
    }

    [Fact]
    public void MetabolicPathwayRendererService_ParsesReactionsAndRendersSvg()
    {
        string pwMd = """
            :::pathway Glycolysis Entry
            Glucose + ATP -(Hexokinase)-> G6P + ADP
            G6P <-> F6P
            :::
            """;

        var model = MetabolicPathwayRendererService.ParsePathway(pwMd);
        Assert.Equal("Glycolysis Entry", model.Title);
        Assert.Equal(2, model.Steps.Count);
        Assert.False(model.Steps[0].IsReversible);
        Assert.Equal("Hexokinase", model.Steps[0].Enzyme);
        Assert.True(model.Steps[1].IsReversible);

        string svg = MetabolicPathwayRendererService.RenderPathwaySvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Glycolysis Entry", svg);
        Assert.Contains("Glucose + ATP", svg);
        Assert.Contains("Hexokinase", svg);
    }

    [Fact]
    public void DocumentSideBySideDiffService_TransformsDiffViewsToHtml()
    {
        string diffMd = """
            :::diff-view Release Notes
            <<< v1.0
            Feature A
            Feature B (Old)
            === v2.0
            Feature A
            Feature B (New)
            Feature C (Added)
            >>>
            :::
            """;

        string html = DocumentSideBySideDiffService.TransformDiffViews(diffMd);
        Assert.Contains("class=\"ms-diff-container\"", html);
        Assert.Contains("Release Notes", html);
        Assert.Contains("v1.0", html);
        Assert.Contains("v2.0", html);
        Assert.Contains("Feature A", html);
    }
}
