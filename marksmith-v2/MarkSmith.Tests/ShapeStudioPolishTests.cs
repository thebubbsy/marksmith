using MarkSmith.Core.Composer;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Shape Studio polish (routine run #18): Word-accurate preset geometry and label areas, presets
/// whose labels are never buried under another shape, colour schemes that swap colour for colour,
/// and the gallery miniatures that must not disturb the live canvas.
/// </summary>
public class ShapeStudioPolishTests
{
    private static ShapeDesignStudioViewModel NewVm() => new();

    public static TheoryData<string> PresetNames()
    {
        var data = new TheoryData<string>();
        foreach (var p in NewVm().AllPresets) data.Add(p.Name);
        return data;
    }

    private static ShapeDesignStudioViewModel Generate(string name, string palette = "Office Blue")
    {
        var vm = NewVm();
        vm.SelectedPaletteName = palette;
        vm.AllPresets.First(p => p.Name == name).Generate(vm);
        return vm;
    }

    [Fact]
    public void Chevron_text_area_stops_at_the_notch_and_the_point()
    {
        Assert.Equal(new[] { 25d, 0, 25, 0 }, PresetGeometry.TextInsets("chevron", 140, 50));
        // Narrower than tall: Word gives the whole box (the notch and point meet).
        Assert.Equal(new[] { 0d, 0, 0, 0 }, PresetGeometry.TextInsets("chevron", 40, 80));
    }

    [Fact]
    public void Wide_chevron_outline_uses_words_half_height_notch()
    {
        var pts = PresetGeometry.Outline("chevron", 200, 60)!;
        Assert.Contains((170d, 0d), pts);  // x2 = w - h/2
        Assert.Contains((30d, 30d), pts);  // notch depth h/2, not 35 % of the width
    }

    [Fact]
    public void Hexagon_outline_sits_flush_in_its_box()
    {
        var pts = PresetGeometry.Outline("hexagon", 140, 120)!;
        Assert.InRange(pts.Min(p => p.Y), -0.5, 0.5);
        Assert.InRange(pts.Max(p => p.Y), 119.5, 120.5);
        Assert.Contains(pts, p => Math.Abs(p.X - 30) < 0.01); // x1 = ss/4
    }

    [Fact]
    public void Shape_label_insets_follow_the_preset_geometry()
    {
        var item = new ShapeCanvasItemViewModel { Prst = "triangle", Width = 200, Height = 100 };
        Assert.Equal(new[] { 50d, 50, 50, 0 }, item.LabelInsets);
        item.Prst = "rect";
        Assert.All(item.LabelInsets, v => Assert.Equal(0, v));
    }

    [Theory]
    [MemberData(nameof(PresetNames))]
    public void Every_label_has_room_inside_its_shapes_text_area(string name)
    {
        var vm = Generate(name);
        foreach (var s in vm.Shapes.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
        {
            var ins = s.LabelInsets;
            double room = s.Width - ins[0] - ins[2];
            Assert.True(room >= 40, $"{name}: \"{s.Text}\" gets only {room:F0} px of its {s.Prst}'s {s.Width:F0} px");
        }
    }

    [Theory]
    [MemberData(nameof(PresetNames))]
    public void No_label_is_buried_under_a_shape_drawn_after_it(string name)
    {
        var shapes = Generate(name).Shapes.ToList();
        for (int i = 0; i < shapes.Count; i++)
        {
            var s = shapes[i];
            if (string.IsNullOrWhiteSpace(s.Text) || s.PathPoints is { Count: >= 2 }) continue;
            var ins = s.LabelInsets;
            double cx = s.X + ins[0] + (s.Width - ins[0] - ins[2]) / 2;
            double cy = s.Y + ins[1] + (s.Height - ins[1] - ins[3]) / 2;
            foreach (var above in shapes.Skip(i + 1).Where(a => a.PathPoints is not { Count: >= 2 }))
            {
                bool covers = above.Prst == "ellipse"
                    ? Math.Pow((cx - (above.X + above.Width / 2)) / (above.Width / 2), 2) + Math.Pow((cy - (above.Y + above.Height / 2)) / (above.Height / 2), 2) < 1
                    : cx > above.X && cx < above.X + above.Width && cy > above.Y && cy < above.Y + above.Height;
                Assert.False(covers, $"{name}: \"{s.Text}\" sits under {above.Prst} \"{above.Text}\"");
            }
        }
    }

    [Fact]
    public void Label_size_fits_the_longest_word_instead_of_following_the_height()
    {
        // A 150 × 90 swimlane header: the old height-only rule gave Word ~24 pt and split the word.
        var (pt, lines) = PresetGeometry.FitLabel("PRODUCT\nMANAGEMENT", "rect", 150, 90);
        Assert.InRange(pt, PresetGeometry.MinLabelPt, PresetGeometry.MaxLabelPt);
        Assert.Equal(new[] { "PRODUCT", "MANAGEMENT" }, lines);
        Assert.True(PresetGeometry.WidthEm("MANAGEMENT") * pt * 96 / 72 <= 150 - 2 * 96 / 72.0);

        // Big shapes stop at the caption size rather than growing with the box.
        Assert.Equal(PresetGeometry.MaxLabelPt, PresetGeometry.FitLabel("Go", "rect", 600, 400).Pt);
    }

    [Theory]
    [MemberData(nameof(PresetNames))]
    public void Every_preset_label_fits_above_the_minimum_size(string name)
    {
        foreach (var s in Generate(name).Shapes.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
        {
            var (pt, _) = PresetGeometry.FitLabel(s.Text, s.Prst, s.Width, s.Height);
            Assert.True(pt > PresetGeometry.MinLabelPt, $"{name}: \"{s.Text}\" only fits its {s.Prst} at {pt} pt");
        }
    }

    [Fact]
    public void Word_export_label_size_matches_the_canvas()
    {
        var vm = Generate("Swimlane Workflow (3 Lanes)");
        var header = vm.Shapes.First(s => s.Text.StartsWith("PRODUCT"));
        var docx = Path.Combine(Path.GetTempPath(), $"lanes-{Guid.NewGuid():N}.docx");
        try
        {
            var composed = vm.SnapshotComposed();
            var (w, h) = ShapeMarkdownCodec.CanvasSize(composed);
            ShapeComposerDocxWriter.WriteDocx(docx, composed, w, h, null);
            using var zip = System.IO.Compression.ZipFile.OpenRead(docx);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            int expected = (int)Math.Round(header.LabelFontSize * 72 / 96 * 2);
            Assert.Contains($"<w:sz w:val=\"{expected}\"/>", xml);
            Assert.True(expected <= PresetGeometry.MaxLabelPt * 2);
        }
        finally { if (File.Exists(docx)) File.Delete(docx); }
    }

    [Fact]
    public void Ring_presets_label_every_ring_with_a_callout()
    {
        foreach (var name in new[] { "Concentric Bullseye Target", "Onion Security Model", "Continuous Feedback Spiral" })
        {
            var shapes = Generate(name).Shapes;
            int rings = shapes.Count(s => s.Prst == "ellipse");
            Assert.Equal(rings, shapes.Count(s => s.Prst == "roundrect" && !string.IsNullOrWhiteSpace(s.Text)));
            Assert.Equal(rings, shapes.Count(s => s.PathPoints is { Count: >= 2 }));
        }
    }

    [Fact]
    public void Honeycomb_cells_do_not_overlap()
    {
        var hexes = Generate("Hexagonal Honeycomb Matrix").Shapes.Where(s => s.Prst == "hexagon").ToList();
        Assert.Equal(7, hexes.Count);
        // Flat-top cells in the same column must stack a full height apart.
        foreach (var column in hexes.GroupBy(h => Math.Round(h.X)))
        {
            var ys = column.Select(h => h.Y).OrderBy(y => y).ToList();
            for (int i = 1; i < ys.Count; i++) Assert.True(ys[i] - ys[i - 1] >= 120, "cells overlap vertically");
        }
    }

    [Fact]
    public void Changing_colour_scheme_swaps_colour_for_colour()
    {
        var vm = Generate("Swimlane Workflow (3 Lanes)");
        var white = vm.AddShapeAt("rect", 0, 0, 10, 10, "FFFFFF");
        var before = vm.Shapes.Select(s => s.Fill).ToList();
        var office = ShapeDesignStudioViewModel.ColorPalettes["Office Blue"];
        var coral = ShapeDesignStudioViewModel.ColorPalettes["Sunset Coral"];

        vm.SelectedPaletteName = "Sunset Coral";
        vm.ApplyPaletteTheme();

        Assert.Equal("FFFFFF", white.Fill);
        for (int i = 0; i < before.Count; i++)
        {
            var s = vm.Shapes[i];
            if (s.PathPoints is { Count: >= 2 }) { Assert.Equal(before[i], s.Fill); continue; }
            int k = Array.IndexOf(office, before[i]);
            if (k >= 0) Assert.Equal(coral[k], s.Fill);
            else if (before[i] != "FFFFFF")
            {
                // A lane body: the tint of the matching new colour.
                int t = Array.FindIndex(office, c => ShapeDesignStudioViewModel.Tint(c, 0.8) == before[i]);
                Assert.True(t >= 0, $"unexpected fill {before[i]}");
                Assert.Equal(ShapeDesignStudioViewModel.Tint(coral[t], 0.8), s.Fill);
            }
        }
    }

    [Fact]
    public void Hand_coloured_canvas_is_still_recoloured_in_order()
    {
        var vm = NewVm();
        vm.AddShapeAt("rect", 0, 0, 10, 10, "123456");
        vm.AddShapeAt("rect", 20, 0, 10, 10, "654321");
        vm.SelectedPaletteName = "Sunset Coral";
        vm.ApplyPaletteTheme();
        var coral = ShapeDesignStudioViewModel.ColorPalettes["Sunset Coral"];
        Assert.Equal(new[] { coral[0], coral[1] }, vm.Shapes.Select(s => s.Fill));
    }

    [Fact]
    public void Preset_miniature_leaves_the_live_canvas_alone()
    {
        var vm = NewVm();
        var preset = vm.AllPresets.First(p => p.Name == "Executive Org Chart");
        var shapes = ShapeDesignStudioViewModel.PreviewPreset(preset, "Sunset Coral");

        Assert.NotEmpty(shapes);
        Assert.Empty(vm.Shapes);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Contains(shapes, s => s.Fill == ShapeDesignStudioViewModel.ColorPalettes["Sunset Coral"][0]);
    }

    [Fact]
    public void Svg_polyline_stroke_is_the_same_width_in_every_direction()
    {
        // An elbow: down then across, in a box much wider than tall.
        var elbow = new ComposedShape
        {
            Prst = "line", X = 1, Y = 1, W = 2, H = 0.5, Fill = "595959", StrokeWidthPt = 2,
            PathPoints = new() { (0, 0), (0, 100), (100, 100) },
        };
        var svg = ImageShapeComposer.RenderSvg(new() { elbow }, 4, 2);
        Assert.DoesNotContain("scale(", svg);
        Assert.Contains("stroke-width=\"2.67\"", svg);
        Assert.Contains("M 96.0 96.0 96.0 144.0 288.0 144.0", svg);
    }

    [Fact]
    public void Svg_chevron_and_label_use_words_proportions()
    {
        // 1.5" × 0.5" → 144 × 48 px: notch 24 px deep, label centred between notch and point.
        var chevron = new ComposedShape { Prst = "chevron", X = 0, Y = 0, W = 1.5, H = 0.5, Fill = "0078D4", Text = "Go" };
        var svg = ImageShapeComposer.RenderSvg(new() { chevron }, 2, 1);
        Assert.Contains("120.0,0.0", svg);
        Assert.Contains("24.0,24.0", svg);
        Assert.Contains("<text x=\"72.0\"", svg);
    }

    [Fact]
    public void Svg_label_on_an_upside_down_triangle_sits_in_its_wide_half()
    {
        // An inverted pyramid tip: a triangle turned 180°, wide edge on top. Its text area (the
        // bottom half in Word's frame) is turned too, so the label moves up, not down.
        var tier = new ComposedShape { Prst = "triangle", X = 0, Y = 0, W = 2, H = 1, Fill = "0078D4", Text = "Top", Rot = 180 };
        var svg = ImageShapeComposer.RenderSvg(new() { tier }, 2, 1);
        // Upright triangle text area is the bottom half (y 48..96); turned over it is 0..48.
        Assert.Contains("y=\"24.0\"", svg);
    }

    [Fact]
    public void CenterDiagramOnCanvas_CentersAllShapesWithinVirtualBounds()
    {
        var vm = NewVm();
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 0, Y = 0, Width = 200, Height = 100 });
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 200, Y = 100, Width = 200, Height = 100 });
        // Collective bounding box: X: 0..400 (center 200), Y: 0..200 (center 100).
        // Canvas size: 2200 x 1800 (center 1100, 900).
        // dx = 1100 - 200 = 900. dy = 900 - 100 = 800.
        vm.CenterDiagramOnCanvas(horizontal: true, vertical: true, canvasW: 2200, canvasH: 1800);

        Assert.Equal(900, vm.Shapes[0].X);
        Assert.Equal(800, vm.Shapes[0].Y);
        Assert.Equal(1100, vm.Shapes[1].X);
        Assert.Equal(900, vm.Shapes[1].Y);
        Assert.Equal("Centered diagram on canvas", vm.StatusMessage);
    }

    [Fact]
    public void ResetDiagramToTopLeft_RepositionsShapesToStandardMargin()
    {
        var vm = NewVm();
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 500, Y = 400, Width = 150, Height = 80 });
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 700, Y = 500, Width = 150, Height = 80 });

        vm.ResetDiagramToTopLeft(padX: 60, padY: 60);

        Assert.Equal(60, vm.Shapes[0].X);
        Assert.Equal(60, vm.Shapes[0].Y);
        Assert.Equal(260, vm.Shapes[1].X);
        Assert.Equal(160, vm.Shapes[1].Y);
        Assert.Equal("Moved diagram to top-left margin", vm.StatusMessage);
    }

    [Fact]
    public void DistributeDiagramOnCanvas_Horizontally_SpacesShapesEvenly()
    {
        var vm = NewVm();
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 0, Y = 100, Width = 100, Height = 60 });
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 120, Y = 100, Width = 100, Height = 60 });
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 400, Y = 100, Width = 100, Height = 60 });

        vm.DistributeDiagramOnCanvas(vertical: false);

        Assert.Equal(0, vm.Shapes[0].X);
        Assert.Equal(200, vm.Shapes[1].X);
        Assert.Equal(400, vm.Shapes[2].X);
        Assert.Equal("Distributed diagram horizontally", vm.StatusMessage);
    }

    [Fact]
    public void DistributeDiagramOnCanvas_Vertically_SpacesShapesEvenly()
    {
        var vm = NewVm();
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 50, Y = 0, Width = 80, Height = 50 });
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 50, Y = 90, Width = 80, Height = 50 });
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 50, Y = 300, Width = 80, Height = 50 });

        vm.DistributeDiagramOnCanvas(vertical: true);

        Assert.Equal(0, vm.Shapes[0].Y);
        Assert.Equal(150, vm.Shapes[1].Y);
        Assert.Equal(300, vm.Shapes[2].Y);
        Assert.Equal("Distributed diagram vertically", vm.StatusMessage);
    }

    [Fact]
    public void TidyDiagramIntoGrid_ArrangesShapesInOrder()
    {
        var vm = NewVm();
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 500, Y = 500, Width = 100, Height = 50 });
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 100, Y = 200, Width = 100, Height = 50 });
        vm.Shapes.Add(new ShapeCanvasItemViewModel { X = 300, Y = 800, Width = 100, Height = 50 });

        vm.TidyDiagramIntoGrid(columns: 2, marginX: 100, marginY: 100, gapX: 20, gapY: 30);

        // Row 0, Col 0:
        Assert.Equal(100, vm.Shapes[0].X);
        Assert.Equal(100, vm.Shapes[0].Y);
        // Row 0, Col 1:
        Assert.Equal(220, vm.Shapes[1].X);
        Assert.Equal(100, vm.Shapes[1].Y);
        // Row 1, Col 0:
        Assert.Equal(100, vm.Shapes[2].X);
        Assert.Equal(180, vm.Shapes[2].Y);
        Assert.Equal("Arranged 3 shapes in a grid", vm.StatusMessage);
    }

    [Fact]
    public void CollectiveBoundingBoxAndCenterSnap_CalculatesAccurateCoordinates()
    {
        var vm = NewVm();
        var s1 = new ShapeCanvasItemViewModel { X = 100, Y = 100, Width = 100, Height = 60, IsSelected = true };
        var s2 = new ShapeCanvasItemViewModel { X = 300, Y = 200, Width = 100, Height = 60, IsSelected = true };
        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);

        var sel = vm.Shapes.Where(s => s.IsSelected).ToList();
        double minX = sel.Min(s => s.X);
        double maxX = sel.Max(s => s.X + s.Width);
        double minY = sel.Min(s => s.Y);
        double maxY = sel.Max(s => s.Y + s.Height);
        double centerX = (minX + maxX) / 2.0;
        double centerY = (minY + maxY) / 2.0;

        Assert.Equal(100, minX);
        Assert.Equal(400, maxX);
        Assert.Equal(250, centerX); // (100 + 400) / 2
        Assert.Equal(100, minY);
        Assert.Equal(260, maxY);
        Assert.Equal(180, centerY); // (100 + 260) / 2

        // When snapping to canvas center (1100, 900)
        double deltaX = 1100.0 - centerX; // +850
        double deltaY = 900.0 - centerY;  // +720

        Assert.Equal(850, deltaX);
        Assert.Equal(720, deltaY);
    }

    [Fact]
    public void ComputeSelectionBounds_returns_zeros_when_empty()
    {
        var (minX, minY, maxX, maxY, w, h) = ShapeDesignStudioViewModel.ComputeSelectionBounds(new List<ShapeCanvasItemViewModel>());
        Assert.Equal(0, minX);
        Assert.Equal(0, minY);
        Assert.Equal(0, maxX);
        Assert.Equal(0, maxY);
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    [Fact]
    public void ComputeSelectionBounds_computes_tight_bounding_box_for_multiple_shapes()
    {
        var s1 = new ShapeCanvasItemViewModel { X = 50, Y = 100, Width = 150, Height = 80, IsSelected = true };
        var s2 = new ShapeCanvasItemViewModel { X = 300, Y = 250, Width = 100, Height = 50, IsSelected = true };
        var list = new List<ShapeCanvasItemViewModel> { s1, s2 };

        var (minX, minY, maxX, maxY, w, h) = ShapeDesignStudioViewModel.ComputeSelectionBounds(list);
        Assert.Equal(50, minX);
        Assert.Equal(100, minY);
        Assert.Equal(400, maxX);
        Assert.Equal(300, maxY);
        Assert.Equal(350, w); // 400 - 50
        Assert.Equal(200, h); // 300 - 100
    }

    [Fact]
    public void ComputeEqualSpacing_ComputesAccurateDistributedPositions()
    {
        // 3 items of width 100 spanning from 0 to 500 (total span 500, sum sizes 300, free space 200, gap 100)
        var sizes = new[] { 100.0, 100.0, 100.0 };
        var positions = ShapeDesignStudioViewModel.ComputeEqualSpacing(startPos: 0, totalSpan: 500, itemSizes: sizes);

        Assert.Equal(3, positions.Count);
        Assert.Equal(0.0, positions[0]);
        Assert.Equal(200.0, positions[1]); // 0 + 100 + 100
        Assert.Equal(400.0, positions[2]); // 200 + 100 + 100 (ends at 500)

        // Single item
        var single = ShapeDesignStudioViewModel.ComputeEqualSpacing(startPos: 50, totalSpan: 100, itemSizes: new[] { 50.0 });
        Assert.Equal(new[] { 50.0 }, single);

        // Empty items
        var empty = ShapeDesignStudioViewModel.ComputeEqualSpacing(startPos: 0, totalSpan: 100, itemSizes: Array.Empty<double>());
        Assert.Empty(empty);
    }

    [Fact]
    public void ComputeClampOffset_CalculatesAccurateClampingDeltas()
    {
        // 1. Fully inside canvas -> (0, 0)
        var inside = ShapeDesignStudioViewModel.ComputeClampOffset(
            minX: 100, minY: 100, maxX: 500, maxY: 400, canvasWidth: 2200, canvasHeight: 1800, padding: 40);
        Assert.Equal((0.0, 0.0), inside);

        // 2. Off-screen left (negative X)
        var offLeft = ShapeDesignStudioViewModel.ComputeClampOffset(
            minX: -60, minY: 100, maxX: 300, maxY: 400, canvasWidth: 2200, canvasHeight: 1800, padding: 40);
        Assert.Equal(100.0, offLeft.DeltaX); // 40 - (-60) = 100
        Assert.Equal(0.0, offLeft.DeltaY);

        // 3. Off-screen right (> 2200 - 40 = 2160)
        var offRight = ShapeDesignStudioViewModel.ComputeClampOffset(
            minX: 1800, minY: 100, maxX: 2300, maxY: 400, canvasWidth: 2200, canvasHeight: 1800, padding: 40);
        Assert.Equal(-140.0, offRight.DeltaX); // 2160 - 2300 = -140
        Assert.Equal(0.0, offRight.DeltaY);

        // 4. Off-screen top and bottom
        var offTopBottom = ShapeDesignStudioViewModel.ComputeClampOffset(
            minX: 100, minY: -20, maxX: 500, maxY: 1900, canvasWidth: 2200, canvasHeight: 1800, padding: 40);
        Assert.Equal(60.0, offTopBottom.DeltaY); // 40 - (-20) = 60
    }

    [Theory]
    [InlineData(0.0, 10.0, 0.0)]
    [InlineData(4.0, 10.0, 0.0)]
    [InlineData(5.0, 10.0, 10.0)]
    [InlineData(14.9, 10.0, 10.0)]
    [InlineData(15.0, 10.0, 20.0)]
    [InlineData(123.4, 10.0, 120.0)]
    [InlineData(-14.0, 10.0, -10.0)]
    [InlineData(50.0, 0.0, 50.0)] // 0 grid size returns unmodified value
    public void Run134_SnapToGrid_SnapsAccurately(double value, double grid, double expected)
    {
        Assert.Equal(expected, ShapeDesignStudioViewModel.SnapToGrid(value, grid));
    }

    [Fact]
    public void Run134_SnapPointToGrid_SnapsBothCoordinates()
    {
        var (x, y) = ShapeDesignStudioViewModel.SnapPointToGrid(42.3, 87.6, 10.0);
        Assert.Equal(40.0, x);
        Assert.Equal(90.0, y);
    }

    [Fact]
    public void Run135_CalculateAspectRatio_DetectsStandardRatios()
    {
        var (r1, l1) = ShapeDesignStudioViewModel.CalculateAspectRatio(100, 100);
        Assert.Equal(1.0, r1);
        Assert.Contains("Square", l1);

        var (r2, l2) = ShapeDesignStudioViewModel.CalculateAspectRatio(160, 90);
        Assert.True(Math.Abs(r2 - 16.0 / 9.0) < 0.01);
        Assert.Contains("Widescreen", l2);

        var (r3, l3) = ShapeDesignStudioViewModel.CalculateAspectRatio(400, 300);
        Assert.True(Math.Abs(r3 - 4.0 / 3.0) < 0.01);
        Assert.Contains("Standard", l3);
    }

    [Fact]
    public void Run135_ApplyStandardAspectRatio_AdjustsHeightCorrectly()
    {
        var (newW, newH) = ShapeDesignStudioViewModel.ApplyStandardAspectRatio(160, 100, 16.0 / 9.0);
        Assert.Equal(160.0, newW);
        Assert.Equal(90.0, newH); // 160 / (16/9) = 90
    }

    [Fact]
    public void Run136_CycleCardinalRotation_CyclesThroughCardinalAngles()
    {
        var (r1, l1) = ShapeDesignStudioViewModel.CycleCardinalRotation(0);
        Assert.Equal(90, r1);
        Assert.Contains("90°", l1);

        var (r2, l2) = ShapeDesignStudioViewModel.CycleCardinalRotation(r1);
        Assert.Equal(180, r2);
        Assert.Contains("180°", l2);

        var (r3, l3) = ShapeDesignStudioViewModel.CycleCardinalRotation(r2);
        Assert.Equal(-90, r3);
        Assert.Contains("-90°", l3);

        var (r4, l4) = ShapeDesignStudioViewModel.CycleCardinalRotation(r3);
        Assert.Equal(0, r4);
        Assert.Contains("0°", l4);

        // Intermediate angle (e.g. 45° -> 90°)
        var (rMid, _) = ShapeDesignStudioViewModel.CycleCardinalRotation(45);
        Assert.Equal(90, rMid);
    }

    [Fact]
    public void Run136_ToggleCornerStyle_TogglesSharpAndRounded()
    {
        var (p1, l1) = ShapeDesignStudioViewModel.ToggleCornerStyle("roundrect");
        Assert.Equal("rect", p1);
        Assert.Contains("Sharp", l1);

        var (p2, l2) = ShapeDesignStudioViewModel.ToggleCornerStyle(p1);
        Assert.Equal("roundrect", p2);
        Assert.Contains("Rounded", l2);

        var (p3, _) = ShapeDesignStudioViewModel.ToggleCornerStyle(null);
        Assert.Equal("roundrect", p3);
    }

    [Fact]
    public void Run137_CycleStrokeDashStyle_CyclesPatternsDeterministically()
    {
        var (d1, l1) = ShapeDesignStudioViewModel.CycleStrokeDashStyle("solid");
        Assert.Equal("dashed", d1);
        Assert.Contains("Dashed", l1);

        var (d2, l2) = ShapeDesignStudioViewModel.CycleStrokeDashStyle(d1);
        Assert.Equal("dotted", d2);
        Assert.Contains("Dotted", l2);

        var (d3, l3) = ShapeDesignStudioViewModel.CycleStrokeDashStyle(d2);
        Assert.Equal("solid", d3);
        Assert.Contains("Solid", l3);

        // Null / empty defaults to dashed
        var (dDef, _) = ShapeDesignStudioViewModel.CycleStrokeDashStyle(null);
        Assert.Equal("dashed", dDef);
    }

    [Fact]
    public void Run138_CycleShapeOpacity_CyclesAcrossOpacityLevels()
    {
        var (o1, l1) = ShapeDesignStudioViewModel.CycleShapeOpacity(1.0);
        Assert.Equal(0.75, o1);
        Assert.Contains("75%", l1);

        var (o2, l2) = ShapeDesignStudioViewModel.CycleShapeOpacity(o1);
        Assert.Equal(0.50, o2);
        Assert.Contains("50%", l2);

        var (o3, l3) = ShapeDesignStudioViewModel.CycleShapeOpacity(o2);
        Assert.Equal(0.25, o3);
        Assert.Contains("25%", l3);

        var (o4, l4) = ShapeDesignStudioViewModel.CycleShapeOpacity(o3);
        Assert.Equal(1.0, o4);
        Assert.Contains("100%", l4);
    }

    [Fact]
    public void Run138_CycleShadowBlurRadius_CyclesBlurLevels()
    {
        var (b1, l1) = ShapeDesignStudioViewModel.CycleShadowBlurRadius(0.0);
        Assert.Equal(4.0, b1);
        Assert.Contains("4px", l1);

        var (b2, l2) = ShapeDesignStudioViewModel.CycleShadowBlurRadius(b1);
        Assert.Equal(8.0, b2);
        Assert.Contains("8px", l2);

        var (b3, l3) = ShapeDesignStudioViewModel.CycleShadowBlurRadius(b2);
        Assert.Equal(16.0, b3);
        Assert.Contains("16px", l3);

        var (b4, l4) = ShapeDesignStudioViewModel.CycleShadowBlurRadius(b3);
        Assert.Equal(0.0, b4);
        Assert.Contains("0px", l4);
    }

    [Fact]
    public void Run139_FormatShadowBlurSummary_FormatsAccurately()
    {
        Assert.Equal("Shadow blur: 0px (Crisp / Off)", ShapeDesignStudioViewModel.FormatShadowBlurSummary(0.0));
        Assert.Equal("Shadow blur: 4px (Subtle)", ShapeDesignStudioViewModel.FormatShadowBlurSummary(4.0));
        Assert.Equal("Shadow blur: 8px (Medium)", ShapeDesignStudioViewModel.FormatShadowBlurSummary(8.0));
        Assert.Equal("Shadow blur: 16px (Deep)", ShapeDesignStudioViewModel.FormatShadowBlurSummary(16.0));
        Assert.Equal("Shadow blur: 0px (Crisp / Off)", ShapeDesignStudioViewModel.FormatShadowBlurSummary(-2.0));
    }

    [Fact]
    public void Run140_CycleCornerRadius_And_FormatCornerRadiusSummary()
    {
        var (r1, l1) = ShapeDesignStudioViewModel.CycleCornerRadius(0.0);
        Assert.Equal(4.0, r1);
        Assert.Contains("4px", l1);

        var (r2, l2) = ShapeDesignStudioViewModel.CycleCornerRadius(r1);
        Assert.Equal(8.0, r2);
        Assert.Contains("8px", l2);

        var (r3, l3) = ShapeDesignStudioViewModel.CycleCornerRadius(r2);
        Assert.Equal(16.0, r3);
        Assert.Contains("16px", l3);

        var (r4, l4) = ShapeDesignStudioViewModel.CycleCornerRadius(r3);
        Assert.Equal(0.0, r4);
        Assert.Contains("0px", l4);

        Assert.Equal("Corner radius: 0px (Square / Sharp)", ShapeDesignStudioViewModel.FormatCornerRadiusSummary(0.0));
        Assert.Equal("Corner radius: 4px (Subtle rounded)", ShapeDesignStudioViewModel.FormatCornerRadiusSummary(4.0));
        Assert.Equal("Corner radius: 8px (Medium rounded)", ShapeDesignStudioViewModel.FormatCornerRadiusSummary(8.0));
        Assert.Equal("Corner radius: 16px (Pill / Deeply rounded)", ShapeDesignStudioViewModel.FormatCornerRadiusSummary(16.0));
    }

    [Fact]
    public void Run141_CycleStrokeDashOffset_And_FormatStrokeDashOffsetSummary()
    {
        var (o1, l1) = ShapeDesignStudioViewModel.CycleStrokeDashOffset(0.0);
        Assert.Equal(2.0, o1);
        Assert.Contains("2.0", l1);

        var (o2, l2) = ShapeDesignStudioViewModel.CycleStrokeDashOffset(o1);
        Assert.Equal(4.0, o2);
        Assert.Contains("4.0", l2);

        var (o3, l3) = ShapeDesignStudioViewModel.CycleStrokeDashOffset(o2);
        Assert.Equal(6.0, o3);
        Assert.Contains("6.0", l3);

        var (o4, l4) = ShapeDesignStudioViewModel.CycleStrokeDashOffset(o3);
        Assert.Equal(0.0, o4);
        Assert.Contains("0.0", l4);

        Assert.Equal("Stroke dash offset: 0.0 (Zero / Aligned)", ShapeDesignStudioViewModel.FormatStrokeDashOffsetSummary(0.0));
        Assert.Equal("Stroke dash offset: 2 (Phase 1)", ShapeDesignStudioViewModel.FormatStrokeDashOffsetSummary(2.0));
        Assert.Equal("Stroke dash offset: 4 (Phase 2)", ShapeDesignStudioViewModel.FormatStrokeDashOffsetSummary(4.0));
        Assert.Equal("Stroke dash offset: 6 (Phase 3)", ShapeDesignStudioViewModel.FormatStrokeDashOffsetSummary(6.0));
    }
}
