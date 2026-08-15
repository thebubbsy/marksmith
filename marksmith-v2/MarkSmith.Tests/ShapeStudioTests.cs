using System;
using System.Linq;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Tests;

public class ShapeStudioTests
{
    [Fact]
    public void GeneratePyramidTemplate_CreatesStackedTiersWithThemeColors()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GeneratePyramidTemplateCommand.Execute(null);

        Assert.Equal(4, vm.Shapes.Count);
        Assert.Equal("triangle", vm.Shapes[0].Prst);
        Assert.Equal("trapezoid", vm.Shapes[1].Prst);
        Assert.Equal("trapezoid", vm.Shapes[2].Prst);
        Assert.Equal("trapezoid", vm.Shapes[3].Prst);

        // Verify top-to-bottom vertical stacking
        Assert.True(vm.Shapes[0].Y < vm.Shapes[1].Y);
        Assert.True(vm.Shapes[1].Y < vm.Shapes[2].Y);
        Assert.True(vm.Shapes[2].Y < vm.Shapes[3].Y);

        // Verify increasing width from apex to base
        Assert.True(vm.Shapes[0].Width < vm.Shapes[1].Width);
        Assert.True(vm.Shapes[1].Width < vm.Shapes[2].Width);
        Assert.True(vm.Shapes[2].Width < vm.Shapes[3].Width);
    }

    [Fact]
    public void GenerateOrgChartTemplate_CreatesHierarchyWithConnectors()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GenerateOrgChartTemplateCommand.Execute(null);

        Assert.True(vm.Shapes.Count >= 10);
        Assert.Contains(vm.Shapes, s => s.Text.Contains("Executive Board"));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("CEO"));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("CTO"));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("CFO"));
        Assert.Contains(vm.Shapes, s => s.Prst == "line");
    }

    [Fact]
    public void GenerateSwotMatrixTemplate_Creates4Quadrants()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GenerateSwotMatrixTemplateCommand.Execute(null);

        Assert.Equal(4, vm.Shapes.Count);
        Assert.Contains(vm.Shapes, s => s.Text.Contains("STRENGTHS"));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("WEAKNESSES"));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("OPPORTUNITIES"));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("THREATS"));
    }

    [Fact]
    public void GenerateCycleTemplate_CreatesNodesAndArrows()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GenerateCycleTemplateCommand.Execute(null);

        Assert.Equal(8, vm.Shapes.Count);
        Assert.Equal(4, vm.Shapes.Count(s => s.Prst == "roundrect"));
        Assert.Equal(4, vm.Shapes.Count(s => s.Prst == "circulararrow"));
    }

    [Fact]
    public void GenerateTimelineTemplate_CreatesChevrons()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GenerateTimelineTemplateCommand.Execute(null);

        Assert.Equal(4, vm.Shapes.Count);
        Assert.All(vm.Shapes, s => Assert.Equal("chevron", s.Prst));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("PHASE 1"));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("PHASE 4"));
    }

    [Fact]
    public void GenerateVennTemplate_Creates3OverlappingEllipses()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GenerateVennTemplateCommand.Execute(null);

        Assert.Equal(3, vm.Shapes.Count);
        Assert.All(vm.Shapes, s => Assert.Equal("ellipse", s.Prst));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("Desirability"));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("Feasibility"));
        Assert.Contains(vm.Shapes, s => s.Text.Contains("Viability"));
    }

    [Fact]
    public void ApplyPaletteTheme_RecolorsAllCanvasShapes()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.GeneratePyramidTemplateCommand.Execute(null);

        vm.SelectedPaletteName = "Sunset Coral";
        vm.ApplyPaletteThemeCommand.Execute(null);

        var sunsetColors = ShapeDesignStudioViewModel.ColorPalettes["Sunset Coral"];
        Assert.Contains(vm.Shapes[0].Fill, sunsetColors);
    }

    [Fact]
    public void AlignAndDistribute_PositionsShapesCorrectly()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.AddShapeAt("rect", 10, 50, 100, 50);
        vm.AddShapeAt("rect", 200, 150, 100, 50);
        vm.AddShapeAt("rect", 80, 250, 100, 50);

        vm.AlignLeftCommand.Execute(null);
        Assert.All(vm.Shapes, s => Assert.Equal(10, s.X));

        vm.DistributeVerticalCommand.Execute(null);
        Assert.Equal(50, vm.Shapes[0].Y);
        Assert.Equal(150, vm.Shapes[1].Y);
        Assert.Equal(250, vm.Shapes[2].Y);
    }

    [Fact]
    public void InsertIntoDocument_EmitsValidShapesMarkdownBlock()
    {
        var vm = new ShapeDesignStudioViewModel();
        string? emitted = null;
        vm.InsertToDocumentRequested += (s, block) => emitted = block;

        vm.GenerateTimelineTemplateCommand.Execute(null);
        vm.InsertIntoDocumentCommand.Execute(null);

        Assert.NotNull(emitted);
        Assert.Contains(":::shapes", emitted);
        Assert.Contains("chevron", emitted);
        Assert.Contains("PHASE 1", emitted);
        Assert.Contains(":::", emitted);
    }

    [Fact]
    public void MultiLineText_EscapesAndUnescapesWithoutCorruptingBlock()
    {
        var shape = new MarkSmith.Core.Composer.ComposedShape
        {
            Prst = "cylinder",
            X = 1.0,
            Y = 2.0,
            W = 3.0,
            H = 4.0,
            Fill = "0078D4",
            Text = "Line One\nLine Two & Special \"Quotes\""
        };

        string formatted = MarkSmith.Core.Composer.ShapeMarkdownCodec.Format(shape);
        Assert.DoesNotContain("\n", formatted);
        Assert.Contains("&#10;", formatted);
        Assert.Contains("&amp;", formatted);
        Assert.Contains("&quot;", formatted);

        var parsed = MarkSmith.Core.Composer.ShapeMarkdownCodec.Parse(formatted);
        Assert.Single(parsed);
        Assert.Equal("cylinder", parsed[0].Prst);
        Assert.Equal("Line One\nLine Two & Special \"Quotes\"", parsed[0].Text);
    }

    [Fact]
    public void ShapeMarkdownHtml_LiftsAndPostInjectsSvgInPreview()
    {
        string md = "# Header\n\n:::shapes\ntrapezoid 0.62 0.42 6.04 0.68 0078D4 text=\"ENTERPRISE ARCHITECTURE\"\n:::\n\nTail prose";
        var (cleanMd, svgs) = MarkSmith.Core.Composer.ShapeMarkdownHtml.LiftShapes(md);

        Assert.Single(svgs);
        Assert.Contains("<svg", svgs[0]);
        Assert.Contains("<!--SHAPES:0-->", cleanMd);

        string html = MarkSmith.Core.Composer.ShapeMarkdownHtml.PostInject(cleanMd, svgs);
        Assert.Contains("<svg", html);
        Assert.Contains("ENTERPRISE ARCHITECTURE", html);
        Assert.DoesNotContain("<!--SHAPES:0-->", html);
    }

    [Fact]
    public void AllPresets_HaveValidGenerators_AndProduceShapes()
    {
        var vm = new ShapeDesignStudioViewModel();
        Assert.True(vm.AllPresets.Count >= 35, $"Expected at least 35 presets, got {vm.AllPresets.Count}");

        foreach (var preset in vm.AllPresets)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.False(string.IsNullOrWhiteSpace(preset.Category));
            Assert.False(string.IsNullOrWhiteSpace(preset.Icon));
            Assert.NotNull(preset.Generate);

            vm.ApplyPreset(preset);
            Assert.NotEmpty(vm.Shapes);
            Assert.True(vm.IsEditable);
            Assert.False(vm.IsEmpty);
        }
    }

    [Fact]
    public void PresetCategoryFilter_FiltersCorrectly()
    {
        var vm = new ShapeDesignStudioViewModel();
        Assert.NotEmpty(vm.PresetCategories);

        vm.SelectedPresetCategory = "Architecture & Cloud";
        Assert.All(vm.FilteredPresets, p => Assert.Equal("Architecture & Cloud", p.Category));

        vm.SelectedPresetCategory = "All Categories";
        Assert.Equal(vm.AllPresets.Count, vm.FilteredPresets.Count);
    }
}
