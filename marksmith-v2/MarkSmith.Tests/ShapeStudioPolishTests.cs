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

    [Fact]
    public async Task Run142_ReverseSelectedZOrder_And_FormatZOrderSummary()
    {
        Assert.Equal("Layer stack: 0 shapes (Empty canvas)", ShapeDesignStudioViewModel.FormatZOrderSummary(null));
        Assert.Equal("Layer stack: 0 shapes (Empty canvas)", ShapeDesignStudioViewModel.FormatZOrderSummary(Array.Empty<ShapeCanvasItemViewModel>()));

        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        vm.AddShapeAt("rect", 10, 10, 100, 100, "FF0000", "Shape A");
        vm.AddShapeAt("ellipse", 50, 50, 100, 100, "00FF00", "Shape B");
        vm.AddShapeAt("roundrect", 90, 90, 100, 100, "0000FF", "Shape C");

        Assert.Equal(3, vm.Shapes.Count);
        Assert.Equal("Shape A", vm.Shapes[0].Text);
        Assert.Equal("Shape B", vm.Shapes[1].Text);
        Assert.Equal("Shape C", vm.Shapes[2].Text);

        var sum1 = ShapeDesignStudioViewModel.FormatZOrderSummary(vm.Shapes);
        Assert.Contains("3 shapes", sum1);
        Assert.Contains("0 selected", sum1);

        // Select Shape A and Shape C (indices 0 and 2)
        vm.Shapes[0].IsSelected = true;
        vm.Shapes[2].IsSelected = true;

        var sum2 = ShapeDesignStudioViewModel.FormatZOrderSummary(vm.Shapes, vm.Shapes.Where(s => s.IsSelected));
        Assert.Contains("2 selected shapes @ layer [1, 3]", sum2);

        // Reverse selected items (0 and 2 swap, 1 stays)
        vm.ReverseSelectedZOrder();
        Assert.Equal("Shape C", vm.Shapes[0].Text);
        Assert.Equal("Shape B", vm.Shapes[1].Text);
        Assert.Equal("Shape A", vm.Shapes[2].Text);

        // Undo restores original order
        Assert.True(vm.CanUndo);
        await vm.UndoAsync();
        Assert.Equal("Shape A", vm.Shapes[0].Text);
        Assert.Equal("Shape B", vm.Shapes[1].Text);
        Assert.Equal("Shape C", vm.Shapes[2].Text);
    }

    [Fact]
    public async Task Run143_NormalizeShapePrecision_And_FormatPrecisionNormalizationSummary()
    {
        Assert.Equal("Precision snap (1px grid): 0 shapes (Empty canvas)", ShapeDesignStudioViewModel.FormatPrecisionNormalizationSummary(0, 0, 1.0, 0));

        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        vm.AddShapeAt("rect", 10.37, 20.62, 100.44, 50.12, "FF0000", "Subpixel A");
        vm.AddShapeAt("ellipse", 40.0, 80.0, 120.0, 60.0, "00FF00", "Aligned B");
        vm.Shapes[0].IsSelected = true;
        vm.Shapes[1].IsSelected = true;

        var (adjusted, total, maxDelta) = vm.NormalizeShapePrecision(1.0);
        Assert.Equal(1, adjusted);
        Assert.Equal(2, total);
        Assert.True(maxDelta >= 0.43);

        Assert.Equal(10.0, vm.Shapes[0].X);
        Assert.Equal(21.0, vm.Shapes[0].Y);
        Assert.Equal(100.0, vm.Shapes[0].Width);
        Assert.Equal(50.0, vm.Shapes[0].Height);

        var summary = ShapeDesignStudioViewModel.FormatPrecisionNormalizationSummary(adjusted, total, 1.0, maxDelta);
        Assert.Contains("Normalized 1 of 2 shapes", summary);
        Assert.Contains("Max sub-pixel drift: 0.44px", summary);

        // Second pass finds 0 shapes needing adjustment
        var (adjusted2, total2, _) = vm.NormalizeShapePrecision(1.0);
        Assert.Equal(0, adjusted2);
        Assert.Equal("Precision snap (1px grid): All 2 shapes already pixel-aligned (0px drift)",
            ShapeDesignStudioViewModel.FormatPrecisionNormalizationSummary(adjusted2, total2, 1.0, 0));

        // Undo restores sub-pixel coordinates
        Assert.True(vm.CanUndo);
        await vm.UndoAsync();
        Assert.Equal(10.37, vm.Shapes[0].X, 2);
    }

    [Fact]
    public async Task Run144_MatchSelectedShapeDimensions_And_FormatDimensionMatchSummary()
    {
        Assert.Equal("Match dimensions: 0 shapes (Empty selection)",
            ShapeDesignStudioViewModel.FormatDimensionMatchSummary(0, 0, 0, 0, null));

        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        vm.AddShapeAt("rect", 10, 10, 160, 90, "FF0000", "Master Card");
        vm.AddShapeAt("ellipse", 200, 10, 100, 50, "00FF00", "Badge B");
        vm.AddShapeAt("diamond", 350, 10, 120, 120, "0000FF", "Node C");

        vm.SelectedShape = vm.Shapes[0];
        vm.Shapes[0].IsSelected = true;
        vm.Shapes[1].IsSelected = true;
        vm.Shapes[2].IsSelected = true;

        var (resized, total, targetW, targetH, refLabel) = vm.MatchSelectedShapeDimensions(matchWidth: true, matchHeight: true);
        Assert.Equal(2, resized);
        Assert.Equal(3, total);
        Assert.Equal(160, targetW);
        Assert.Equal(90, targetH);
        Assert.Equal("Master Card", refLabel);

        Assert.Equal(160, vm.Shapes[1].Width);
        Assert.Equal(90, vm.Shapes[1].Height);
        Assert.Equal(160, vm.Shapes[2].Width);
        Assert.Equal(90, vm.Shapes[2].Height);

        var summary = ShapeDesignStudioViewModel.FormatDimensionMatchSummary(resized, total, targetW, targetH, refLabel);
        Assert.Equal("Match dimensions ('Master Card' · 160×90px): 2 shapes resized across 3 selected shapes", summary);

        // Undo restores original dimensions
        Assert.True(vm.CanUndo);
        await vm.UndoAsync();
        Assert.Equal(100, vm.Shapes[1].Width);
        Assert.Equal(50, vm.Shapes[1].Height);
    }

    [Fact]
    public async Task Run145_FlipSelectedShapes_And_FormatFlipTransformSummary()
    {
        Assert.Equal("Mirror flip (Horizontal): 0 shapes (Empty selection)",
            ShapeDesignStudioViewModel.FormatFlipTransformSummary(0, true, false, 0, 0));

        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        vm.AddShapeAt("rect", 10, 20, 40, 30, "FF0000", "Left Box");   // X: 10..50
        vm.AddShapeAt("ellipse", 110, 20, 40, 30, "00FF00", "Right Box"); // X: 110..150
        vm.Shapes[0].Rotation = 30;

        vm.SelectedShape = vm.Shapes[0];
        vm.Shapes[0].IsSelected = true;
        vm.Shapes[1].IsSelected = true;

        // Collective bounds: minX=10, maxX=150 -> pivotCenterX=80, minY=20, maxY=50 -> pivotCenterY=35
        var (flipped, pivotX, pivotY) = vm.FlipSelectedShapes(horizontal: true, vertical: false);
        Assert.Equal(2, flipped);
        Assert.Equal(80, pivotX);
        Assert.Equal(35, pivotY);

        // Left Box (10..50) mirrors to (110..150) => X=110, rotation 30 => -30
        Assert.Equal(110, vm.Shapes[0].X);
        Assert.Equal(-30, vm.Shapes[0].Rotation);
        // Right Box (110..150) mirrors to (10..50) => X=10
        Assert.Equal(10, vm.Shapes[1].X);

        var summary = ShapeDesignStudioViewModel.FormatFlipTransformSummary(flipped, true, false, pivotX, pivotY);
        Assert.Equal("Mirror flip (Horizontal): 2 shapes mirrored across pivot (80, 35)", summary);

        Assert.True(vm.CanUndo);
        await vm.UndoAsync();
        Assert.Equal(10, vm.Shapes[0].X);
        Assert.Equal(110, vm.Shapes[1].X);
    }

    [Fact]
    public void Run146_SelectSimilarShapes_And_FormatSimilarShapesSummary()
    {
        Assert.Equal("Select similar: 0 shapes (Empty canvas)",
            ShapeDesignStudioViewModel.FormatSimilarShapesSummary(0, 0));

        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        vm.AddShapeAt("rect", 10, 10, 100, 60, "0078D4", "Button 1");
        vm.AddShapeAt("rect", 120, 10, 100, 60, "0078D4", "Button 2");
        vm.AddShapeAt("rect", 230, 10, 100, 60, "D83B01", "Alert Button");
        vm.AddShapeAt("ellipse", 10, 100, 60, 60, "0078D4", "Indicator 1");
        vm.AddShapeAt("ellipse", 80, 100, 60, 60, "107C41", "Success Bubble");

        // Select Button 1 as reference
        vm.SelectedShape = vm.Shapes[0];

        // 1. Select similar by kind (all 3 "rect" shapes)
        var (kindMatched, totalKind, refKind, _, kindSummary) = vm.SelectSimilarShapes(matchKind: true, matchFill: false);
        Assert.Equal(3, kindMatched);
        Assert.Equal(5, totalKind);
        Assert.Equal("rect", refKind);
        Assert.Equal(3, vm.Shapes.Count(s => s.IsSelected));
        Assert.True(vm.Shapes[0].IsSelected);
        Assert.True(vm.Shapes[1].IsSelected);
        Assert.True(vm.Shapes[2].IsSelected);
        Assert.False(vm.Shapes[3].IsSelected);
        Assert.Contains("Select similar (kind 'rect'): 3 shapes matched across 5 canvas shapes", kindSummary);

        // 2. Select similar by fill (all shapes with fill #0078D4: Button 1, Button 2, Indicator 1)
        var (fillMatched, _, _, refFill, fillSummary) = vm.SelectSimilarShapes(matchKind: false, matchFill: true);
        Assert.Equal(3, fillMatched);
        Assert.Equal(3, vm.Shapes.Count(s => s.IsSelected));
        Assert.True(vm.Shapes[0].IsSelected);
        Assert.True(vm.Shapes[1].IsSelected);
        Assert.True(vm.Shapes[3].IsSelected);
        Assert.False(vm.Shapes[2].IsSelected); // Alert Button is D83B01
        Assert.Contains("Select similar (fill #0078D4): 3 shapes matched across 5 canvas shapes", fillSummary);

        // 3. Select similar by both kind AND fill (only Button 1 and Button 2)
        var (bothMatched, _, _, _, bothSummary) = vm.SelectSimilarShapes(matchKind: true, matchFill: true);
        Assert.Equal(2, bothMatched);
        Assert.Equal(2, vm.Shapes.Count(s => s.IsSelected));
        Assert.True(vm.Shapes[0].IsSelected);
        Assert.True(vm.Shapes[1].IsSelected);
        Assert.False(vm.Shapes[2].IsSelected);
        Assert.False(vm.Shapes[3].IsSelected);
        Assert.Contains("Select similar (kind 'rect' & fill #0078D4): 2 shapes matched across 5 canvas shapes", bothSummary);
    }

    [Fact]
    public void Run147_ResolveShapeOverlaps_NudgesApartAndEnforcesMinGap()
    {
        Assert.Equal("Resolve overlaps: 0-1 shapes (No overlap resolution needed)",
            ShapeDesignStudioViewModel.FormatOverlapResolutionSummary(0, 1));

        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // Add 3 heavily overlapping shapes at (10, 10), (20, 15), (25, 20)
        vm.AddShapeAt("rect", 10, 10, 100, 60, "0078D4", "Box 1");
        vm.AddShapeAt("rect", 20, 15, 100, 60, "0078D4", "Box 2");
        vm.AddShapeAt("rect", 25, 20, 100, 60, "0078D4", "Box 3");

        var (nudged, overlaps, summary) = vm.ResolveShapeOverlaps(minGap: 16.0);
        Assert.True(nudged >= 2);
        Assert.True(overlaps >= 2);
        Assert.Contains("Resolve overlaps:", summary);
        Assert.Contains("shapes nudged apart (Enforced 16px minimum clearance across 3 shapes)", summary);

        // Verify none of the shapes overlap now with 16px clearance
        for (int i = 0; i < vm.Shapes.Count; i++)
        {
            var s1 = vm.Shapes[i];
            for (int j = i + 1; j < vm.Shapes.Count; j++)
            {
                var s2 = vm.Shapes[j];
                bool overlapsX = (s1.X < s2.X + s2.Width + 16) && (s1.X + s1.Width + 16 > s2.X);
                bool overlapsY = (s1.Y < s2.Y + s2.Height + 16) && (s1.Y + s1.Height + 16 > s2.Y);
                Assert.False(overlapsX && overlapsY, $"Shapes {i} and {j} still overlap!");
            }
        }

        // Running it again on the already cleared layout detects 0 overlaps
        var (nudgedAgain, overlapsAgain, summaryAgain) = vm.ResolveShapeOverlaps(minGap: 16.0);
        Assert.Equal(0, nudgedAgain);
        Assert.Equal(0, overlapsAgain);
        Assert.Contains("0 overlaps detected", summaryAgain);
    }

    [Fact]
    public void Run148_DistributeShapesEvenly()
    {
        Assert.Equal("Distribute evenly (Horizontal): Requires at least 3 shapes (Found 1)",
            ShapeDesignStudioViewModel.FormatDistributionSummary(0, 1, DistributionAxis.Horizontal));

        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // Add 3 shapes: left at 0, middle at 50, right at 300 (width = 100 each)
        // Span = (300 + 100) - 0 = 400. Widths = 300. Gap = (400 - 300) / 2 = 50.
        vm.AddShapeAt("rect", 0, 10, 100, 60, "0078D4", "Box 1");
        vm.AddShapeAt("rect", 50, 10, 100, 60, "0078D4", "Box 2");
        vm.AddShapeAt("rect", 300, 10, 100, 60, "0078D4", "Box 3");

        var (distH, totalH, sumH) = vm.DistributeShapesEvenly(DistributionAxis.Horizontal);
        Assert.Equal(1, distH);
        Assert.Equal(3, totalH);
        Assert.Equal(0, vm.Shapes[0].X);
        Assert.Equal(150, vm.Shapes[1].X); // 0 + 100 + 50 = 150
        Assert.Equal(300, vm.Shapes[2].X);
        Assert.Contains("Distribute evenly (Horizontal): Balanced 1 shape across 3 shapes (Gap: 50px)", sumH);

        // Undo restores middle shape to X=50
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal(50, vm.Shapes[1].X);

        // Test Vertical Distribution
        vm.Shapes[0].Y = 0;
        vm.Shapes[0].Height = 50;
        vm.Shapes[1].Y = 20;
        vm.Shapes[1].Height = 50;
        vm.Shapes[2].Y = 200;
        vm.Shapes[2].Height = 50;

        // Span = (200 + 50) - 0 = 250. Heights = 150. Gap = (250 - 150) / 2 = 50.
        var (distV, totalV, sumV) = vm.DistributeShapesEvenly(DistributionAxis.Vertical);
        Assert.Equal(1, distV);
        Assert.Equal(3, totalV);
        Assert.Equal(0, vm.Shapes[0].Y);
        Assert.Equal(100, vm.Shapes[1].Y); // 0 + 50 + 50 = 100
        Assert.Equal(200, vm.Shapes[2].Y);
        Assert.Contains("Distribute evenly (Vertical): Balanced 1 shape across 3 shapes (Gap: 50px)", sumV);
    }

    [Fact]
    public void Run149_ScaleSelectedShapesProportionally()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 2 shapes: Shape 1 at (0, 0, 100, 100); Shape 2 at (200, 0, 100, 100)
        // Bounding box: X: 0..300, Y: 0..100. Center = (150, 50).
        vm.AddShapeAt("rect", 0, 0, 100, 100, "0078D4", "Box 1");
        vm.AddShapeAt("rect", 200, 0, 100, 100, "0078D4", "Box 2");
        vm.SelectAll();

        // Scale by 2.0x
        var (scaledCount, totalCount, factor, summary) = vm.ScaleSelectedShapesProportionally(2.0);
        Assert.Equal(2, scaledCount);
        Assert.Equal(2, totalCount);
        Assert.Equal(2.0, factor);

        // Dimensions should be 200x200
        Assert.Equal(200.0, vm.Shapes[0].Width);
        Assert.Equal(200.0, vm.Shapes[0].Height);
        Assert.Equal(200.0, vm.Shapes[1].Width);
        Assert.Equal(200.0, vm.Shapes[1].Height);

        // Center of Shape 1 was 50. Relative to 150 = -100. Scaled = -200. New center = 150 - 200 = -50.
        // Clamped to 0: X = 0.
        Assert.Equal(0.0, vm.Shapes[0].X);

        Assert.Contains("Scale proportionally (2x): Resized 2 shapes around selection center", summary);

        // Test Undo restores original dimensions
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal(100.0, vm.Shapes[0].Width);
        Assert.Equal(100.0, vm.Shapes[0].Height);
    }

    [Fact]
    public void Run150_DistributeShapesRadially_ArrangesShapesOnOrbit()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Minimum shape requirement guard (<2 shapes)
        vm.AddShapeAt("rect", 10, 10, 50, 50, "0078D4", "Solo");
        var (c0, _, _, s0) = vm.DistributeShapesRadially(100, 100);
        Assert.Equal(0, c0);
        Assert.Contains("requires at least 2 shapes", s0);

        // 2. Arrange 4 shapes in circular orbit
        vm.ClearAll();
        vm.AddShapeAt("rect", 0, 0, 50, 50, "0078D4", "Box 0");
        vm.AddShapeAt("rect", 200, 0, 50, 50, "0078D4", "Box 1");
        vm.AddShapeAt("rect", 200, 200, 50, 50, "0078D4", "Box 2");
        vm.AddShapeAt("rect", 0, 200, 50, 50, "0078D4", "Box 3");

        // Bounding centroid: (125, 125)
        var (countCirc, rxCirc, ryCirc, sumCirc) = vm.DistributeShapesRadially(100, 100);
        Assert.Equal(4, countCirc);
        Assert.Equal(100, rxCirc);
        Assert.Equal(100, ryCirc);
        Assert.Contains("circular orbit (R=100px) around (125, 125)", sumCirc);

        // Box 0 (angle = -90 deg): top (100, 0)
        Assert.Equal(100.0, vm.Shapes[0].X);
        Assert.Equal(0.0, vm.Shapes[0].Y);

        // Box 1 (angle = 0 deg): right (200, 100)
        Assert.Equal(200.0, vm.Shapes[1].X);
        Assert.Equal(100.0, vm.Shapes[1].Y);

        // Box 2 (angle = +90 deg): bottom (100, 200)
        Assert.Equal(100.0, vm.Shapes[2].X);
        Assert.Equal(200.0, vm.Shapes[2].Y);

        // Box 3 (angle = 180 deg): left (0, 100)
        Assert.Equal(0.0, vm.Shapes[3].X);
        Assert.Equal(100.0, vm.Shapes[3].Y);

        // 3. Test Undo restores original position of Box 0
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0.0, vm.Shapes[0].X);
        Assert.Equal(0.0, vm.Shapes[0].Y);

        // 4. Elliptical orbit distribution
        var (countEll, rxEll, ryEll, sumEll) = vm.DistributeShapesRadially(150, 80);
        Assert.Equal(4, countEll);
        Assert.Equal(150, rxEll);
        Assert.Equal(80, ryEll);
        Assert.Contains("elliptical orbit (150×80px) around (125, 125)", sumEll);
    }

    [Fact]
    public void Run151_AuditLayerStackIndices_And_NormalizeZOrderStack()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Empty canvas
        var empty = vm.AuditLayerStackIndices();
        Assert.Equal(0, empty.TotalShapes);
        Assert.Contains("0 shapes (Empty canvas)", empty.Summary);

        // 2. Add 4 shapes
        vm.AddShapeAt("rect", 0, 0, 50, 50, "0078D4", "Shape 0");
        vm.AddShapeAt("circle", 100, 0, 50, 50, "107C41", "Shape 1");
        vm.AddShapeAt("diamond", 200, 0, 50, 50, "D83B01", "Shape 2");
        vm.AddShapeAt("star", 300, 0, 50, 50, "FFB900", "Shape 3");

        // 3. Select non-contiguous shapes (Shape 0 and Shape 2)
        vm.Shapes[0].IsSelected = true;
        vm.Shapes[1].IsSelected = false;
        vm.Shapes[2].IsSelected = true;
        vm.Shapes[3].IsSelected = false;

        var audit = vm.AuditLayerStackIndices();
        Assert.Equal(4, audit.TotalShapes);
        Assert.Equal(2, audit.SelectedCount);
        Assert.Equal(0, audit.MinIndex);
        Assert.Equal(2, audit.MaxIndex);
        Assert.False(audit.IsContiguous);
        Assert.Contains("non-contiguous", audit.Summary);
        Assert.Contains("[0, 2]", audit.Summary);

        // 4. Normalize z-order stack to compact them
        var (normCount, modified, summary) = vm.NormalizeZOrderStack();
        Assert.Equal(2, normCount);
        Assert.True(modified);
        Assert.Contains("compacted 2 shapes into contiguous layers 0..1", summary);

        // 5. Audit again -> now contiguous
        var postAudit = vm.AuditLayerStackIndices();
        Assert.True(postAudit.IsContiguous);
        Assert.Contains("contiguous", postAudit.Summary);

        // 6. Test undo
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
    }

    [Fact]
    public void Run152_AuditGridAlignment_And_QuantizeShapeCoordinates()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Empty canvas
        var empty = vm.AuditGridAlignment(8.0);
        Assert.Equal(0, empty.TotalShapes);
        Assert.Contains("0 shapes (Empty canvas)", empty.Summary);

        // 2. Add shapes: one aligned (X:16, Y:24, W:80, H:40), one misaligned (X:13, Y:25, W:77, H:43)
        vm.AddShapeAt("rect", 16, 24, 80, 40, "0078D4", "Aligned");
        vm.AddShapeAt("circle", 13, 25, 77, 43, "107C41", "Misaligned");
        vm.ClearSelection();

        var audit = vm.AuditGridAlignment(8.0);
        Assert.Equal(2, audit.TotalShapes);
        Assert.Equal(1, audit.AlignedCount);
        Assert.Equal(1, audit.MisalignedCount);
        Assert.Equal(50.0, audit.AlignmentPercentage);
        Assert.True(audit.MaxOffset > 0);
        Assert.Contains("1/2 aligned (50%)", audit.Summary);

        // 3. Quantize misaligned shape
        vm.Shapes[1].IsSelected = true;
        vm.Shapes[0].IsSelected = false;

        var (targets, modified, summary) = vm.QuantizeShapeCoordinates(8.0);
        Assert.Equal(1, targets);
        Assert.Equal(1, modified);
        Assert.Contains("Snapped 1 shapes to 8px grid", summary);

        // Check snapped values for shape 1
        Assert.Equal(16.0, vm.Shapes[1].X);
        Assert.Equal(24.0, vm.Shapes[1].Y);
        Assert.Equal(80.0, vm.Shapes[1].Width);
        Assert.Equal(40.0, vm.Shapes[1].Height);

        // 4. Re-audit -> 100% aligned
        vm.ClearSelection();
        var postAudit = vm.AuditGridAlignment(8.0);
        Assert.Equal(2, postAudit.AlignedCount);
        Assert.Equal(0, postAudit.MisalignedCount);
        Assert.Equal(100.0, postAudit.AlignmentPercentage);
        Assert.Equal(0.0, postAudit.MaxOffset);

        // 5. Test undo reverts shape 1 back to 13, 25
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal(13.0, vm.Shapes[1].X);
        Assert.Equal(25.0, vm.Shapes[1].Y);
    }

    [Fact]
    public void Run153_AuditAspectRatioProportions_And_EnforceLock()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        // 1. Empty canvas
        var empty = vm.AuditAspectRatioProportions();
        Assert.Equal(0, empty.TotalShapes);
        Assert.Contains("0 shapes", empty.Summary);

        // 2. Add shapes: one square (100x100), one widescreen (160x90 = 1.78), one custom (200x50 = 4.0)
        vm.AddShapeAt("rect", 0, 0, 100, 100, "0078D4", "Square Shape");
        vm.AddShapeAt("rect", 200, 0, 160, 90, "107C41", "Widescreen Shape");
        vm.AddShapeAt("rect", 400, 0, 200, 50, "D83B01", "Custom Shape");
        vm.ClearSelection();

        var audit = vm.AuditAspectRatioProportions();
        Assert.Equal(3, audit.TotalShapes);
        Assert.Equal(1, audit.SquareCount);
        Assert.Equal(1, audit.Widescreen169Count);
        Assert.Equal(1, audit.CustomCount);
        Assert.Contains("1 square (1:1)", audit.Summary);
        Assert.Contains("1 widescreen (16:9)", audit.Summary);

        // 3. Lock custom shape to 1:1 square
        vm.Shapes[2].IsSelected = true;
        vm.Shapes[0].IsSelected = false;
        vm.Shapes[1].IsSelected = false;

        var (targets, modified, summary) = vm.EnforceAspectRatioLock(1.0, "1:1 Square");
        Assert.Equal(1, targets);
        Assert.Equal(1, modified);
        Assert.Contains("Locked 1 shapes to 1:1 Square aspect ratio", summary);

        // Check new dimensions: area = 200*50 = 10000 => sqrt(10000) = 100x100
        Assert.Equal(100.0, vm.Shapes[2].Width);
        Assert.Equal(100.0, vm.Shapes[2].Height);

        // 4. Test undo restores original 200x50 dimensions
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal(200.0, vm.Shapes[2].Width);
        Assert.Equal(50.0, vm.Shapes[2].Height);
    }

    [Fact]
    public void Run154_AuditSpacingDistributionAndDistributeUniformly()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Less than 2 shapes
        var emptyAudit = vm.AuditSpacingDistribution();
        Assert.Equal(0, emptyAudit.TotalShapes);
        Assert.Contains("Insufficient shapes", emptyAudit.Summary);

        // 2. Add 3 shapes with uneven spacing:
        // Shape 0: X=0, W=100 (ends at 100)
        // Shape 1: X=120, W=100 (gap 20, ends at 220)
        // Shape 2: X=300, W=100 (gap 80, ends at 400)
        // Total envelope: 400 - 0 = 400. Shapes width: 300. Total gap: 100. Uniform gap = 100 / 2 = 50.
        vm.AddShapeAt("rect", 0, 0, 100, 50, "FF0000", "A");
        vm.AddShapeAt("rect", 120, 0, 100, 50, "00FF00", "B");
        vm.AddShapeAt("rect", 300, 0, 100, 50, "0000FF", "C");

        var unevenAudit = vm.AuditSpacingDistribution(DistributionAxis.Horizontal);
        Assert.Equal(3, unevenAudit.TotalShapes);
        Assert.Equal(2, unevenAudit.GapCount);
        Assert.Equal(20.0, unevenAudit.MinGap);
        Assert.Equal(80.0, unevenAudit.MaxGap);
        Assert.Equal(50.0, unevenAudit.AverageGap);
        Assert.False(unevenAudit.IsUniform);
        Assert.Contains("Uneven", unevenAudit.Summary);

        // 3. Distribute spacing uniformly
        var (targets, modified, gap, summary) = vm.DistributeSpacingUniformly(DistributionAxis.Horizontal);
        Assert.Equal(3, targets);
        Assert.Equal(1, modified);
        Assert.Equal(50.0, gap);
        Assert.Contains("Distributed uniform horizontal spacing", summary);

        // Shape 1 should now be at X = 0 + 100 + 50 = 150
        Assert.Equal(150.0, vm.Shapes[1].X);

        // 4. Verify post-distribution audit reports Uniform
        var uniformAudit = vm.AuditSpacingDistribution(DistributionAxis.Horizontal);
        Assert.True(uniformAudit.IsUniform);
        Assert.Equal(50.0, uniformAudit.MinGap);
        Assert.Equal(50.0, uniformAudit.MaxGap);
        Assert.Contains("Uniform", uniformAudit.Summary);

        // 5. Test Undo restores Shape 1 to X=120
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal(120.0, vm.Shapes[1].X);
    }

    [Fact]
    public void Run155_AuditOcclusionAndOverlap()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Empty / single shape
        var emptyAudit = vm.AuditOcclusionAndOverlap();
        Assert.Equal(0, emptyAudit.TotalShapes);
        Assert.Equal(0, emptyAudit.OverlappingPairCount);
        Assert.Contains("Insufficient shapes", emptyAudit.Summary);

        // 2. Add two shapes where Shape B (index 1, on top) covers 75% of Shape A (index 0, underlying)
        // Shape A: X=0, Y=0, W=100, H=100 (Area = 10000)
        // Shape B: X=25, Y=0, W=100, H=100 (Intersection: X from 25 to 100 = 75, Y from 0 to 100 = 100 -> IntersectArea = 7500 -> 75% occlusion of A)
        vm.AddShapeAt("rect", 0, 0, 100, 100, "FF0000", "Underlying Base");
        vm.AddShapeAt("rect", 25, 0, 100, 100, "00FF00", "Top Overlay");

        var audit = vm.AuditOcclusionAndOverlap();
        Assert.Equal(2, audit.TotalShapes);
        Assert.Equal(1, audit.OverlappingPairCount);
        Assert.Equal(1, audit.OccludedShapeCount);
        Assert.Equal(75.0, audit.MaxOcclusionPercentage);
        Assert.Equal(1, audit.OccludedShapes.Count);
        Assert.Equal("Rectangle", audit.WorstOccludedShapeName);
        Assert.Equal("Rectangle", audit.WorstCoveringShapeName);
        Assert.Contains("75% covered", audit.Summary);
        Assert.Contains("1 heavily occluded shapes", audit.Summary);
    }

    [Fact]
    public void Run156_AuditBoundaryMarginsAndBleed()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Empty canvas
        var emptyAudit = vm.AuditBoundaryMarginsAndBleed();
        Assert.Equal(0, emptyAudit.TotalShapes);
        Assert.Equal("Empty Canvas", emptyAudit.SafetyCategory);
        Assert.Contains("Canvas is empty", emptyAudit.Summary);

        // 2. Safe canvas shapes (all within safe margin ≥ 24px)
        vm.AddShapeAt("rect", 100, 100, 200, 100, "0000FF", "Centered Box");
        var safeAudit = vm.AuditBoundaryMarginsAndBleed(safeMargin: 24.0, canvasWidth: 1200.0, canvasHeight: 800.0);
        Assert.Equal(1, safeAudit.TotalShapes);
        Assert.Equal(0, safeAudit.BleedingShapeCount);
        Assert.Equal(0, safeAudit.TightMarginShapeCount);
        Assert.Equal(1, safeAudit.SafeShapeCount);
        Assert.Equal(100.0, safeAudit.MinObservedMargin);
        Assert.Equal("Safe Printable Margins", safeAudit.SafetyCategory);
        Assert.Contains("min margin 100px", safeAudit.Summary);

        // 3. Bleeding shape (X = -10, protruding past left border)
        vm.AddShapeAt("circle", -10, 50, 80, 80, "FF0000", "Bleeding Circle");
        var bleedAudit = vm.AuditBoundaryMarginsAndBleed(safeMargin: 24.0, canvasWidth: 1200.0, canvasHeight: 800.0);
        Assert.Equal(2, bleedAudit.TotalShapes);
        Assert.Equal(1, bleedAudit.BleedingShapeCount);
        Assert.Equal(-10.0, bleedAudit.MinObservedMargin);
        Assert.Equal("Boundary Bleed (Clipped Shapes)", bleedAudit.SafetyCategory);
        Assert.Contains("1 bleeding/clipped", bleedAudit.Summary);
    }

    [Fact]
    public void Run157_AuditShapeTextContrast()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        var emptyAudit = vm.AuditShapeTextContrast();
        Assert.Equal(0, emptyAudit.TotalShapes);
        Assert.Equal("Empty Canvas", emptyAudit.ComplianceCategory);
        Assert.Contains("Canvas is empty", emptyAudit.Summary);

        // 2. High contrast accessible shapes (Black text on White, Deep Navy text on Light Cyan)
        var s1 = vm.AddShapeAt("rect", 50, 50, 100, 50, "FFFFFF", "Light Card");
        s1.TextColor = "000000";
        var s2 = vm.AddShapeAt("roundrect", 200, 50, 100, 50, "B3E5FC", "Cyan Badge");
        s2.TextColor = "001F3F";

        var passAudit = vm.AuditShapeTextContrast();
        Assert.Equal(2, passAudit.TotalShapes);
        Assert.Equal(2, passAudit.LabeledShapes);
        Assert.Equal(2, passAudit.CompliantAaCount);
        Assert.Equal(2, passAudit.CompliantAaaCount);
        Assert.Equal(0, passAudit.FailingCount);
        Assert.True(passAudit.MinObservedRatio >= 7.0, $"Min ratio {passAudit.MinObservedRatio} should be >= 7.0");
        Assert.Equal("WCAG AAA Compliant (All ≥7.0:1)", passAudit.ComplianceCategory);
        Assert.Contains("WCAG AAA Compliant", passAudit.Summary);

        // 3. Contrast violation (Yellow text on White fill)
        var s3 = vm.AddShapeAt("circle", 350, 50, 80, 80, "FFFFFF", "Yellow Label");
        s3.TextColor = "FFFF00";
        var failAudit = vm.AuditShapeTextContrast();
        Assert.Equal(3, failAudit.TotalShapes);
        Assert.Equal(3, failAudit.LabeledShapes);
        Assert.Equal(1, failAudit.FailingCount);
        Assert.Equal("Contrast Violations Detected (<4.5:1)", failAudit.ComplianceCategory);
        Assert.Contains("1 failing", failAudit.Summary);
        Assert.Equal("Yellow Label", failAudit.WorstShapeName);
    }

    [Fact]
    public void Run158_AuditGoldenRatioProgression()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Empty canvas
        var emptyAudit = vm.AuditGoldenRatioProgression();
        Assert.Equal(0, emptyAudit.TotalShapes);
        Assert.Equal("Empty Canvas", emptyAudit.ProgressionCategory);
        Assert.Contains("Canvas is empty", emptyAudit.Summary);

        // 2. Golden ratio shapes (width / height ≈ 1.618: e.g. 162x100 and 262x162)
        var s1 = vm.AddShapeAt("rect", 50, 50, 162, 100, "FFFFFF", "Golden Card 1");
        var s2 = vm.AddShapeAt("rect", 300, 50, 262, 162, "EEEEEE", "Golden Card 2");

        var goldenAudit = vm.AuditGoldenRatioProgression();
        Assert.Equal(2, goldenAudit.TotalShapes);
        Assert.Equal(2, goldenAudit.GoldenAspectCount);
        Assert.Equal("Harmonic Golden Progression", goldenAudit.ProgressionCategory);
        Assert.Contains("2/2 golden aspect", goldenAudit.Summary);

        // Model list overload test
        var staticAudit = vm.AuditGoldenRatioProgression(new[] { s1, s2 });
        Assert.Equal(2, staticAudit.TotalShapes);
        Assert.Equal(goldenAudit.ProgressionAdherenceScore, staticAudit.ProgressionAdherenceScore);

        // 3. Arbitrary non-golden shape (Square 100x100: ratio 1.0, delta 0.618)
        vm.ClearAll();
        vm.AddShapeAt("rect", 50, 50, 100, 100, "FFFFFF", "Square");
        vm.AddShapeAt("rect", 200, 50, 500, 50, "FFFFFF", "Skinny Strip"); // ratio 10.0
        var arbAudit = vm.AuditGoldenRatioProgression();
        Assert.Equal(0, arbAudit.GoldenAspectCount);
        Assert.Equal("Arbitrary Scale & Aspect", arbAudit.ProgressionCategory);
    }

    [Fact]
    public void Run159_AuditGoldenSpiralProgression_EvaluatesRadialAndAngularExpansion()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Empty canvas
        var emptyAudit = vm.AuditGoldenSpiralProgression();
        Assert.Equal(0, emptyAudit.TotalShapes);
        Assert.Equal("Empty Canvas", emptyAudit.Category);
        Assert.Contains("Canvas is empty", emptyAudit.Summary);

        // 2. Multi-shape spiral layout (radial distances expanding ~1.6x)
        var s1 = vm.AddShapeAt("rect", 190, 190, 20, 20, "FFFFFF", "Center 1");
        var s2 = vm.AddShapeAt("rect", 220, 190, 20, 20, "EEEEEE", "Node 2");
        var s3 = vm.AddShapeAt("rect", 190, 240, 20, 20, "DDDDDD", "Node 3");
        var s4 = vm.AddShapeAt("rect", 110, 190, 20, 20, "CCCCCC", "Node 4");

        var spiralAudit = vm.AuditGoldenSpiralProgression();
        Assert.Equal(4, spiralAudit.TotalShapes);
        Assert.NotEmpty(spiralAudit.Shapes);
        Assert.Contains("Golden spiral audit", spiralAudit.Summary);

        // Target list overload
        var targetAudit = vm.AuditGoldenSpiralProgression(new[] { s1, s2, s3, s4 });
        Assert.Equal(4, targetAudit.TotalShapes);
        Assert.Equal(spiralAudit.AverageGrowthRatio, targetAudit.AverageGrowthRatio);
    }

    [Fact]
    public void Run160_AuditColorPaletteHarmony_EvaluatesChromaticHarmonies()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        // 1. Empty canvas
        var emptyAudit = vm.AuditColorPaletteHarmony();
        Assert.Equal(0, emptyAudit.TotalAnalyzedShapes);
        Assert.Equal("Empty Canvas", emptyAudit.SummaryCategory);

        // 2. Monochromatic / Cohesive blue palette
        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Text = "Primary", Fill = "#1E88E5" }; // Azure / Blue (~207 deg)
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Text = "Secondary", Fill = "#1976D2" }; // Azure / Blue (~210 deg)
        var s3 = new ShapeCanvasItemViewModel { Id = "s3", Text = "Dark Tone", Fill = "#0D47A1" }; // Azure / Blue (~217 deg)
        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);
        vm.Shapes.Add(s3);

        var monoAudit = vm.AuditColorPaletteHarmony();
        Assert.Equal(3, monoAudit.TotalAnalyzedShapes);
        Assert.Equal(3, monoAudit.ChromaticColorCount);
        Assert.Equal(0, monoAudit.NeutralColorCount);
        Assert.Contains("Monochromatic", monoAudit.HarmonyType);
        Assert.True(monoAudit.HarmonyCoherenceScore >= 90.0);

        // 3. Complementary contrast (Blue + Amber/Orange)
        var sOrange = new ShapeCanvasItemViewModel { Id = "s4", Text = "Accent", Fill = "#FFA000" }; // Orange (~38 deg, ~170 deg diff from 210 deg)
        vm.Shapes.Add(sOrange);

        var compAudit = vm.AuditColorPaletteHarmony();
        Assert.Equal(4, compAudit.TotalAnalyzedShapes);
        Assert.Equal("Complementary (Dynamic Opposing Accent)", compAudit.HarmonyType);
        Assert.True(compAudit.HarmonyCoherenceScore >= 80.0);

        // 4. Target shapes overload
        var neutralShape = new ShapeCanvasItemViewModel { Id = "sn", Text = "White Card", Fill = "#FFFFFF" };
        var neutralAudit = vm.AuditColorPaletteHarmony(new[] { neutralShape });
        Assert.Equal(1, neutralAudit.TotalAnalyzedShapes);
        Assert.Equal(1, neutralAudit.NeutralColorCount);
        Assert.Equal(0, neutralAudit.ChromaticColorCount);
        Assert.Equal("Monochrome Neutral Anchors", neutralAudit.HarmonyType);
        Assert.Equal(100.0, neutralAudit.HarmonyCoherenceScore);
    }

    [Fact]
    public void Run161_AuditIsometricGridAlignment_DetectsIsometricAnglesAndDepthStacking()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        // 1. Empty canvas (<2 shapes)
        var emptyAudit = vm.AuditIsometricGridAlignment();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal("Empty Canvas", emptyAudit.AlignmentCategory);

        // 2. 1 shape
        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Text = "Base", X = 100, Y = 100, Width = 40, Height = 40 };
        vm.Shapes.Add(s1);
        var singleAudit = vm.AuditIsometricGridAlignment();
        Assert.Equal(1, singleAudit.TotalShapesEvaluated);
        Assert.Equal("Insufficient Shapes (<2)", singleAudit.AlignmentCategory);

        // 3. Strict 30° isometric alignment (dx=100, dy=57.7 => angle 30°)
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Text = "Iso Block", X = 200, Y = 157.7, Width = 40, Height = 40 };
        vm.Shapes.Add(s2);

        var isoAudit = vm.AuditIsometricGridAlignment();
        Assert.Equal(2, isoAudit.TotalShapesEvaluated);
        Assert.Equal(1, isoAudit.EvaluatedPairCount);
        Assert.Equal(1, isoAudit.AlignedPairCount);
        Assert.Equal(0, isoAudit.DepthStackingConflictCount);
        Assert.True(isoAudit.IsometricFidelityScore >= 80.0);
        Assert.Contains("Isometric", isoAudit.AlignmentCategory);
        Assert.Contains("Fidelity:", isoAudit.Summary);

        // 4. Depth stacking conflict (A is above B on screen, but has higher ZIndex)
        var sConflict = new ShapeCanvasItemViewModel { Id = "s3", Text = "Conflict", X = 100, Y = 50, Width = 40, Height = 40 };
        vm.Shapes.Add(sConflict);
        var conflictAudit = vm.AuditIsometricGridAlignment();
        Assert.True(conflictAudit.DepthStackingConflictCount > 0);
    }

    [Fact]
    public void Run162_AuditDiagramSymmetry_IdentifiesBilateralAndRotationalSymmetry()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        // 1. Empty canvas (<2 shapes)
        var emptyAudit = vm.AuditDiagramSymmetry();
        Assert.Equal(0, emptyAudit.TotalShapes);
        Assert.Equal("Asymmetric / Freeform Composition", emptyAudit.Category);
        Assert.Equal(0.0, emptyAudit.OverallSymmetryScore);

        // 2. Vertical bilateral symmetry (C1v)
        // Two shapes of same size mirrored across vertical axis
        // Shape 1: X=100, Y=100, W=40, H=40 => Centroid (120, 120)
        // Shape 2: X=240, Y=100, W=40, H=40 => Centroid (260, 120)
        // Center of bounding box = (190, 120). Mirrored X around 190: 190 - (120 - 190) = 260. Perfect vertical symmetry!
        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Text = "Left Wing", X = 100, Y = 100, Width = 40, Height = 40, Prst = "rect" };
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Text = "Right Wing", X = 240, Y = 100, Width = 40, Height = 40, Prst = "rect" };
        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);

        var vertAudit = vm.AuditDiagramSymmetry();
        Assert.Equal(2, vertAudit.TotalShapes);
        Assert.Equal(2, vertAudit.VerticalMatches);
        Assert.Equal(100.0, vertAudit.VerticalSymmetryPercent);
        Assert.Equal("Dual-Axis Bilateral Symmetry (D2)", vertAudit.Category);
        Assert.True(vertAudit.OverallSymmetryScore >= 50.0);
        Assert.Equal(vertAudit.Summary, ShapeDesignStudioViewModel.FormatDiagramSymmetrySummary(
            vertAudit.TotalShapes, vertAudit.VerticalMatches, vertAudit.HorizontalMatches,
            vertAudit.RotationalMatches, vertAudit.VerticalSymmetryPercent, vertAudit.HorizontalSymmetryPercent,
            vertAudit.RotationalSymmetryPercent, vertAudit.OverallSymmetryScore, vertAudit.Category, vertAudit.IsSelectionScope ? "Selected Shapes · " : ""));

        // 3. Selection target list overload
        var targetAudit = vm.AuditDiagramSymmetry(new[] { s1, s2 });
        Assert.Equal(2, targetAudit.TotalShapes);
        Assert.Equal(2, targetAudit.VerticalMatches);

        // 4. Asymmetric addition
        var sAsym = new ShapeCanvasItemViewModel { Id = "s3", Text = "Outlier", X = 500, Y = 800, Width = 80, Height = 20, Prst = "ellipse" };
        vm.Shapes.Add(sAsym);
        var asymAudit = vm.AuditDiagramSymmetry();
        Assert.Equal(3, asymAudit.TotalShapes);
        Assert.True(asymAudit.OverallSymmetryScore < vertAudit.OverallSymmetryScore);
    }

    [Fact]
    public void Run163_AuditSpacingUniformityAndCollisions_DetectsGuttersAndCollisions()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        // 1. Insufficient shapes (<2)
        var emptyAudit = vm.AuditSpacingUniformityAndCollisions();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal("Insufficient Shapes", emptyAudit.SpacingCategory);
        Assert.Equal(100.0, emptyAudit.SpacingUniformityScore);

        // 2. Pixel-perfect uniform spacing (20px gutters)
        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Text = "Col 1", X = 0, Y = 100, Width = 100, Height = 50 };
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Text = "Col 2", X = 120, Y = 100, Width = 100, Height = 50 }; // gap = 20
        var s3 = new ShapeCanvasItemViewModel { Id = "s3", Text = "Col 3", X = 240, Y = 100, Width = 100, Height = 50 }; // gap = 20
        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);
        vm.Shapes.Add(s3);

        var uniformAudit = vm.AuditSpacingUniformityAndCollisions();
        Assert.Equal(3, uniformAudit.TotalShapesEvaluated);
        Assert.Equal(3, uniformAudit.EvaluatedPairCount);
        Assert.Equal(0, uniformAudit.CollidingPairCount);
        Assert.Equal(0.0, uniformAudit.GutterStandardDeviation);
        Assert.Equal("Pixel-Perfect Uniform Spacing", uniformAudit.SpacingCategory);
        Assert.Equal(100.0, uniformAudit.SpacingUniformityScore);
        Assert.Equal(uniformAudit.Summary, ShapeDesignStudioViewModel.FormatSpacingCollisionSummary(
            uniformAudit.TotalShapesEvaluated, uniformAudit.EvaluatedPairCount, uniformAudit.CollidingPairCount,
            uniformAudit.MeanHorizontalGutter, uniformAudit.MeanVerticalGutter, uniformAudit.GutterStandardDeviation,
            uniformAudit.SpacingUniformityScore, uniformAudit.SpacingCategory, uniformAudit.IsSelectionScope ? "Selected Shapes · " : ""));

        // 3. Collision detection (overlapping shapes)
        var sCollide = new ShapeCanvasItemViewModel { Id = "s4", Text = "Overlapper", X = 50, Y = 120, Width = 100, Height = 50 };
        vm.Shapes.Add(sCollide);
        var collideAudit = vm.AuditSpacingUniformityAndCollisions();
        Assert.Equal(4, collideAudit.TotalShapesEvaluated);
        Assert.True(collideAudit.CollidingPairCount >= 1);
        Assert.Equal("Bounding Box Collisions Detected", collideAudit.SpacingCategory);
        Assert.True(collideAudit.SpacingUniformityScore < 100.0);
        Assert.Contains(collideAudit.Pairs, p => p.IsColliding);
    }

    [Fact]
    public void Run164_AuditOrthogonalRoutingAndIntersections_EvaluatesManhattanChannelsAndCrossings()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        // 1. Insufficient shapes (<2)
        var emptyAudit = vm.AuditOrthogonalRoutingAndIntersections();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal(0, emptyAudit.ConnectionChannelsEvaluated);
        Assert.Equal("Orthogonal routing audit: Insufficient shapes (<2) · Minimum 2 shapes required for channel routing", emptyAudit.Summary);

        // 2. Clear horizontal planar channel (0 bends, 0 crossings, 0 collisions)
        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Text = "Source Node", X = 0, Y = 100, Width = 60, Height = 60 };
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Text = "Target Node", X = 200, Y = 100, Width = 60, Height = 60 };
        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);

        var clearAudit = vm.AuditOrthogonalRoutingAndIntersections();
        Assert.Equal(2, clearAudit.TotalShapesEvaluated);
        Assert.Equal(1, clearAudit.ConnectionChannelsEvaluated);
        Assert.Equal(1, clearAudit.ClearChannelCount);
        Assert.Equal(0, clearAudit.ObstructedChannelCount);
        Assert.Equal(0, clearAudit.TotalWireCrossings);
        Assert.Equal(100.0, clearAudit.RoutingEfficiencyScore);
        Assert.Contains("Optimal Orthogonal Planar Channels", clearAudit.RoutingCategory);
        Assert.Equal(clearAudit.Summary, ShapeDesignStudioViewModel.FormatOrthogonalRoutingSummary(
            2, 1, 1, 0, 0, clearAudit.MeanChannelClearance, 100.0, clearAudit.RoutingCategory, ""));

        // 3. Obstructed channel with collision
        var sObstacle = new ShapeCanvasItemViewModel { Id = "sObs", Text = "Obstacle", X = 100, Y = 100, Width = 60, Height = 60 };
        vm.Shapes.Add(sObstacle);

        var obstAudit = vm.AuditOrthogonalRoutingAndIntersections();
        Assert.Equal(3, obstAudit.TotalShapesEvaluated);
        Assert.True(obstAudit.ObstructedChannelCount >= 1);
        Assert.Contains("Shape Collisions on Routing Channels", obstAudit.RoutingCategory);
        Assert.True(obstAudit.RoutingEfficiencyScore < 100.0);
    }

    [Fact]
    public void Run165_AuditGridSnapConformance_EvaluatesMultiGridAndSubpixelDrift()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        // 1. Empty shapes
        var emptyAudit = vm.AuditGridSnapConformance();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal(100.0, emptyAudit.GridSnapConformanceScore);
        Assert.Equal("Empty Canvas", emptyAudit.ConformanceCategory);
        Assert.Contains("Canvas contains 0 shapes", emptyAudit.Summary);

        // 2. Pixel-perfect 8px snapped shapes with zero subpixel drift
        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Text = "Box 1", X = 16, Y = 32, Width = 64, Height = 48 };
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Text = "Box 2", X = 80, Y = 96, Width = 32, Height = 16 };
        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);

        var cleanAudit = vm.AuditGridSnapConformance(gridSize: 8.0);
        Assert.Equal(2, cleanAudit.TotalShapesEvaluated);
        Assert.Equal(2, cleanAudit.FullySnappedCount);
        Assert.Equal(0, cleanAudit.SubpixelDriftCount);
        Assert.Equal(0.0, cleanAudit.MeanSnapErrorPixels);
        Assert.Equal(100.0, cleanAudit.GridSnapConformanceScore);
        Assert.Contains("Pixel-Perfect", cleanAudit.ConformanceCategory);
        Assert.Equal(cleanAudit.Summary, ShapeDesignStudioViewModel.FormatGridSnapSummary(
            2, 8.0, 2, 0, 0, 0.0, 100.0, cleanAudit.ConformanceCategory, ""));

        // 3. Shape with fractional coordinates / subpixel drift
        var sDrift = new ShapeCanvasItemViewModel { Id = "s3", Text = "Drift", X = 15.35, Y = 22.75, Width = 50.4, Height = 40.2 };
        vm.Shapes.Add(sDrift);

        var driftAudit = vm.AuditGridSnapConformance(gridSize: 8.0);
        Assert.Equal(3, driftAudit.TotalShapesEvaluated);
        Assert.Equal(1, driftAudit.SubpixelDriftCount);
        Assert.True(driftAudit.MeanSnapErrorPixels > 0.0);
        Assert.True(driftAudit.GridSnapConformanceScore < 100.0);
    }

    [Fact]
    public void Run166_AuditVisualWeightAndOpticalCentroid_CalculatesCentroidAndOffset()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Empty canvas
        var emptyAudit = vm.AuditVisualWeightAndOpticalCentroid();
        Assert.Equal(0, emptyAudit.TotalShapes);
        Assert.Equal(100.0, emptyAudit.OpticalBalanceScore);
        Assert.Equal("Empty Canvas", emptyAudit.Category);

        // 2. Symmetric centered layout: two identical shapes at left and right
        var sLeft = new ShapeCanvasItemViewModel { Id = "s1", Text = "Left", X = 100, Y = 100, Width = 100, Height = 100 };
        var sRight = new ShapeCanvasItemViewModel { Id = "s2", Text = "Right", X = 300, Y = 100, Width = 100, Height = 100 };
        vm.Shapes.Add(sLeft);
        vm.Shapes.Add(sRight);

        var balancedAudit = vm.AuditVisualWeightAndOpticalCentroid();
        Assert.Equal(2, balancedAudit.TotalShapes);
        Assert.Equal(250.0, balancedAudit.GeometricCenterX);
        Assert.Equal(250.0, balancedAudit.OpticalCentroidX);
        Assert.Equal(0.0, balancedAudit.DeltaX);
        Assert.Equal(100.0, balancedAudit.OpticalBalanceScore);
        Assert.Equal("Equilibrium Centered (≥95%)", balancedAudit.Category);

        // 3. Unbalanced heavy right side: huge shape on right
        var sHeavy = new ShapeCanvasItemViewModel { Id = "s3", Text = "Heavy", X = 400, Y = 100, Width = 300, Height = 300 };
        vm.Shapes.Add(sHeavy);

        var unbalancedAudit = vm.AuditVisualWeightAndOpticalCentroid();
        Assert.Equal(3, unbalancedAudit.TotalShapes);
        Assert.True(unbalancedAudit.DeltaX > 0.0);
        Assert.Contains("Right", unbalancedAudit.DirectionalBias);
        Assert.True(unbalancedAudit.OpticalBalanceScore < 100.0);

        // 4. Formatter test
        string formatted = ShapeDesignStudioViewModel.FormatVisualWeightSummary(
            unbalancedAudit.TotalShapes, unbalancedAudit.DeltaX, unbalancedAudit.DeltaY,
            unbalancedAudit.NormalizedOffsetPercent, unbalancedAudit.OpticalBalanceScore,
            unbalancedAudit.DirectionalBias, unbalancedAudit.Category);
        Assert.Equal(unbalancedAudit.Summary, formatted);
    }

    [Fact]
    public void Run167_AuditConnectorCurvatureAndTangents_EvaluatesSmoothnessAndMicroKinks()
    {
        var vm = new ShapeDesignStudioViewModel();

        // 1. Empty canvas (0 connectors)
        var emptyAudit = vm.AuditConnectorCurvatureAndTangents();
        Assert.Equal(0, emptyAudit.TotalConnectorsEvaluated);
        Assert.Equal(0, emptyAudit.TotalKnotsEvaluated);
        Assert.Equal(100.0, emptyAudit.CurvatureSmoothnessScore);
        Assert.Equal("Zero Connectors", emptyAudit.SmoothnessCategory);

        // 2. Straight collinear connector (0° turning angle -> G1 Smooth)
        var smoothConnector = new ShapeCanvasItemViewModel
        {
            Id = "c1",
            ShapeType = "connector",
            Text = "Smooth Connector",
            Points = new List<System.Numerics.Vector2>
            {
                new(0, 0),
                new(100, 0),
                new(200, 0)
            }
        };
        vm.Shapes.Add(smoothConnector);

        var smoothAudit = vm.AuditConnectorCurvatureAndTangents();
        Assert.Equal(1, smoothAudit.TotalConnectorsEvaluated);
        Assert.Equal(1, smoothAudit.TotalKnotsEvaluated);
        Assert.Equal(1, smoothAudit.SmoothKnotCount);
        Assert.Equal(0, smoothAudit.MicroKinkCount);
        Assert.Equal(0.0, smoothAudit.MeanTurningAngleDegrees);
        Assert.Equal(100.0, smoothAudit.CurvatureSmoothnessScore);
        Assert.Equal("Seamless G1 Tangent Continuity", smoothAudit.SmoothnessCategory);

        // 3. Connector with accidental 10° micro-kink
        var kinkConnector = new ShapeCanvasItemViewModel
        {
            Id = "c2",
            ShapeType = "connector",
            Text = "Kinked Connector",
            Points = new List<System.Numerics.Vector2>
            {
                new(0, 0),
                new(100, 0),
                new(198.48f, 17.36f) // ~10 degree turning angle
            }
        };
        vm.Shapes.Clear();
        vm.Shapes.Add(kinkConnector);

        var kinkAudit = vm.AuditConnectorCurvatureAndTangents();
        Assert.Equal(1, kinkAudit.TotalConnectorsEvaluated);
        Assert.Equal(1, kinkAudit.TotalKnotsEvaluated);
        Assert.Equal(1, kinkAudit.MicroKinkCount);
        Assert.True(kinkAudit.MeanTurningAngleDegrees >= 8.0 && kinkAudit.MeanTurningAngleDegrees <= 12.0);
        Assert.True(kinkAudit.CurvatureSmoothnessScore < 100.0);
        Assert.Equal("Micro-Kinks Detected", kinkAudit.SmoothnessCategory);

        // 4. Formatter test
        string formatted = ShapeDesignStudioViewModel.FormatConnectorCurvatureSummary(
            kinkAudit.TotalConnectorsEvaluated, kinkAudit.TotalKnotsEvaluated, kinkAudit.SmoothKnotCount,
            kinkAudit.MicroKinkCount, kinkAudit.MeanTurningAngleDegrees, kinkAudit.CurvatureSmoothnessScore,
            kinkAudit.SmoothnessCategory, kinkAudit.IsSelectionScope ? "Selection · " : "");
        Assert.Equal(kinkAudit.Summary, formatted);
    }

    [Fact]
    public void Run168_AuditAspectRatioAndPrimitiveScaling_DetectsConformingAndDrift()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Empty canvas
        var emptyAudit = vm.AuditAspectRatioAndPrimitiveScaling();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal(100.0, emptyAudit.AspectRatioFidelityScore);
        Assert.Equal("Empty Canvas", emptyAudit.Category);

        // 2. Conforming 1:1 square and 16:9 widescreen
        var sSquare = new ShapeCanvasItemViewModel { Id = "s1", Text = "Square", Width = 100, Height = 100 };
        var sWide = new ShapeCanvasItemViewModel { Id = "s2", Text = "16:9 Slide", Width = 160, Height = 90 };
        vm.Shapes.Add(sSquare);
        vm.Shapes.Add(sWide);

        var cleanAudit = vm.AuditAspectRatioAndPrimitiveScaling();
        Assert.Equal(2, cleanAudit.TotalShapesEvaluated);
        Assert.Equal(2, cleanAudit.ConformingShapeCount);
        Assert.Equal(0, cleanAudit.AccidentalDriftCount);
        Assert.Equal(100.0, cleanAudit.AspectRatioFidelityScore);
        Assert.Equal("Perfect Geometric Aspect Ratio Conformance", cleanAudit.Category);

        // 3. Accidental scaling drift (e.g. 100 x 106, 6% drift from 1:1)
        var sDrift = new ShapeCanvasItemViewModel { Id = "s3", Text = "Slightly skewed square", Width = 100, Height = 106 };
        vm.Shapes.Add(sDrift);

        var driftAudit = vm.AuditAspectRatioAndPrimitiveScaling();
        Assert.Equal(3, driftAudit.TotalShapesEvaluated);
        Assert.Equal(2, driftAudit.ConformingShapeCount);
        Assert.Equal(1, driftAudit.AccidentalDriftCount);
        Assert.True(driftAudit.MeanDriftPercentage >= 5.0 && driftAudit.MeanDriftPercentage <= 7.0);
        Assert.True(driftAudit.AspectRatioFidelityScore < 100.0);
    }

    [Fact]
    public void Run169_AuditStrokeAlignmentAndCornerRadius_DetectsHeavyStrokesAndCornerDiscrepancy()
    {
        var vm = new ShapeDesignStudioViewModel();

        // 1. Empty canvas
        var emptyAudit = vm.AuditStrokeAlignmentAndCornerRadius();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal(100.0, emptyAudit.StrokeFidelityScore);
        Assert.Equal("Empty Canvas", emptyAudit.Category);

        // 2. Conforming strokes
        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Text = "Box 1", Prst = "roundrect", Width = 120, Height = 60, StrokeWidthPt = 2.0 };
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Text = "Box 2", Prst = "roundrect", Width = 120, Height = 60, StrokeWidthPt = 2.0 };
        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);

        var cleanAudit = vm.AuditStrokeAlignmentAndCornerRadius();
        Assert.Equal(2, cleanAudit.TotalShapesEvaluated);
        Assert.Equal(2, cleanAudit.UniformCornerRadiusCount);
        Assert.Equal(0, cleanAudit.HeavyStrokeCount);
        Assert.Equal(0, cleanAudit.GhostShapeCount);
        Assert.Equal(0, cleanAudit.MiterSpikeRiskCount);
        Assert.Equal(100.0, cleanAudit.StrokeFidelityScore);
        Assert.Equal("Uniform Pixel-Perfect Strokes & Corners", cleanAudit.Category);

        // 3. Heavy stroke, corner discrepancy, and miter spike on sharp vertex
        var sHeavy = new ShapeCanvasItemViewModel { Id = "s3", Text = "Heavy Mini Box", Prst = "rect", Width = 30, Height = 30, StrokeWidthPt = 8.0 };
        var sTinyRoundRect = new ShapeCanvasItemViewModel { Id = "s4", Text = "Tiny Round", Prst = "roundrect", Width = 10, Height = 10, StrokeWidthPt = 1.0 };
        var sDiamondMiter = new ShapeCanvasItemViewModel { Id = "s5", Text = "Sharp Diamond", Prst = "diamond", Width = 100, Height = 100, StrokeWidthPt = 6.0 };

        vm.Shapes.Add(sHeavy);
        vm.Shapes.Add(sTinyRoundRect);
        vm.Shapes.Add(sDiamondMiter);

        var mixedAudit = vm.AuditStrokeAlignmentAndCornerRadius();
        Assert.Equal(5, mixedAudit.TotalShapesEvaluated);
        Assert.True(mixedAudit.HeavyStrokeCount >= 1);
        Assert.True(mixedAudit.MiterSpikeRiskCount >= 1);
        Assert.True(mixedAudit.StrokeFidelityScore < 100.0);
    }

    [Fact]
    public void Run170_AuditConnectorObstacleCollisions_DetectsCollidingObstacles()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();

        // 1. Empty canvas
        var emptyAudit = vm.AuditConnectorObstacleCollisions();
        Assert.Equal(0, emptyAudit.TotalConnectors);
        Assert.Equal(100.0, emptyAudit.RoutingClearanceScore);
        Assert.Equal("Empty Canvas", emptyAudit.ClearanceCategory);

        // 2. Clear connector between Shape A and Shape B
        var sA = new ShapeCanvasItemViewModel { Id = "sA", Text = "Source", X = 0, Y = 0, Width = 50, Height = 50 };
        var sB = new ShapeCanvasItemViewModel { Id = "sB", Text = "Target", X = 200, Y = 0, Width = 50, Height = 50 };
        var conn = new ShapeCanvasItemViewModel { Id = "c1", Prst = "line", X = 50, Y = 25, Width = 150, Height = 0 };
        var sFar = new ShapeCanvasItemViewModel { Id = "sFar", Text = "Far Obstacle", X = 100, Y = 100, Width = 50, Height = 50 };

        vm.Shapes.Add(sA);
        vm.Shapes.Add(sB);
        vm.Shapes.Add(conn);
        vm.Shapes.Add(sFar);

        var clearAudit = vm.AuditConnectorObstacleCollisions();
        Assert.Equal(1, clearAudit.TotalConnectors);
        Assert.Equal(3, clearAudit.TotalObstacleShapes);
        Assert.Equal(1, clearAudit.ClearConnectors);
        Assert.Equal(0, clearAudit.CollidedConnectors);
        Assert.Equal(100.0, clearAudit.RoutingClearanceScore);
        Assert.Contains("Clear Channel Orthogonal Routing", clearAudit.ClearanceCategory);

        // 3. Move obstacle to intersect connector line directly
        sFar.X = 100;
        sFar.Y = 10;
        sFar.Height = 30; // Y from 10 to 40, crosses Y=25

        var collisionAudit = vm.AuditConnectorObstacleCollisions();
        Assert.Equal(1, collisionAudit.TotalConnectors);
        Assert.Equal(1, collisionAudit.CollidedConnectors);
        Assert.True(collisionAudit.TotalCollisions >= 1);
        Assert.True(collisionAudit.RoutingClearanceScore < 80.0);
        Assert.NotEmpty(collisionAudit.Connectors);

        // 4. Formatter test
        string formatted = ShapeDesignStudioViewModel.FormatConnectorCollisionSummary(
            collisionAudit.TotalConnectors, collisionAudit.TotalObstacleShapes,
            collisionAudit.ClearConnectors, collisionAudit.CollidedConnectors,
            collisionAudit.NearMissConnectors, collisionAudit.TotalCollisions,
            collisionAudit.RoutingClearanceScore, collisionAudit.ClearanceCategory, collisionAudit.IsSelectionScope);
        Assert.Equal(collisionAudit.Summary, formatted);
    }

    [Fact]
    public void Run171_AuditCompoundGroupBoundsAndStacking_ValidatesContainmentAndZOrder()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        // 1. Empty canvas
        var empty = vm.AuditCompoundGroupBoundsAndStacking();
        Assert.Equal(0, empty.TotalShapesEvaluated);
        Assert.Equal(0, empty.TotalGroupsFound);
        Assert.Equal(100.0, empty.GroupContainmentScore);
        Assert.Equal("Empty Canvas", empty.Category);

        // 2. Perfectly contained compound group with correct Z-order (container first, children second)
        var container = new ShapeCanvasItemViewModel { Id = "g1", Text = "Container Frame", X = 10, Y = 10, Width = 200, Height = 200 };
        var c1 = new ShapeCanvasItemViewModel { Id = "c1", Text = "Child A", X = 30, Y = 30, Width = 50, Height = 50 };
        var c2 = new ShapeCanvasItemViewModel { Id = "c2", Text = "Child B", X = 100, Y = 100, Width = 60, Height = 60 };
        vm.Shapes.Add(container);
        vm.Shapes.Add(c1);
        vm.Shapes.Add(c2);

        var cleanAudit = vm.AuditCompoundGroupBoundsAndStacking();
        Assert.Equal(3, cleanAudit.TotalShapesEvaluated);
        Assert.Equal(1, cleanAudit.TotalGroupsFound);
        Assert.Equal(2, cleanAudit.GroupedChildrenCount);
        Assert.Equal(0, cleanAudit.OverflowingGroupCount);
        Assert.Equal(0, cleanAudit.ZOrderInversionCount);
        Assert.Equal(100.0, cleanAudit.GroupContainmentScore);
        Assert.Equal("Perfect Nested Containment (100%)", cleanAudit.Category);

        var groupInfo = cleanAudit.Groups.First();
        Assert.False(groupInfo.HasOverflow);
        Assert.True(groupInfo.IsZOrderMonotonic);

        // 3. Overflow and Z-order inversion
        // Make c2 extend far outside the container boundary and move container to top of Z-stack
        c2.X = 250; // exceeds 10 + 200 = 210
        vm.Shapes.Remove(container);
        vm.Shapes.Add(container); // container is now last (renders on top of children)

        var defectAudit = vm.AuditCompoundGroupBoundsAndStacking();
        // Since c2 center is at 250 + 30 = 280, it is no longer inside container center.
        // Let's test overflow by having center inside but right edge outside:
        c2.X = 180;
        c2.Width = 80; // Right edge = 260 > 210, center = 220 which is outside, let's make container width 200 (X=10..210), child X=150, W=100 (Center=200 inside, Right=250 outside!)
        c2.X = 150;
        c2.Width = 100; // Center is 200 (inside 10..210), Right is 250 (outside 210)

        var overflowAudit = vm.AuditCompoundGroupBoundsAndStacking();
        Assert.Equal(1, overflowAudit.TotalGroupsFound);
        Assert.True(overflowAudit.OverflowingGroupCount >= 1);
        Assert.True(overflowAudit.ZOrderInversionCount >= 1);
        Assert.True(overflowAudit.GroupContainmentScore < 80.0);
        Assert.NotEmpty(overflowAudit.StructuralAlerts);

        // 4. Formatter test
        string formatted = ShapeDesignStudioViewModel.FormatCompoundGroupSummary(
            cleanAudit.TotalShapesEvaluated, cleanAudit.TotalGroupsFound,
            cleanAudit.GroupedChildrenCount, cleanAudit.StandaloneShapesCount,
            cleanAudit.OverflowingGroupCount, cleanAudit.ZOrderInversionCount,
            cleanAudit.GroupContainmentScore, cleanAudit.Category, cleanAudit.IsSelectionScope);
        Assert.Equal(cleanAudit.Summary, formatted);
    }

    [Fact]
    public void Run172_AuditConnectorPortSnapping_ValidatesPerimeterPortsAndSubpixelDrift()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        var empty = vm.AuditConnectorPortSnapping();
        Assert.Equal(0, empty.TotalConnectors);
        Assert.Equal(100.0, empty.PortSnappingScore);
        Assert.Equal("Empty Canvas", empty.Category);

        var sA = new ShapeCanvasItemViewModel { Id = "sa", Text = "Shape A", X = 100, Y = 100, Width = 100, Height = 60 };
        var sB = new ShapeCanvasItemViewModel { Id = "sb", Text = "Shape B", X = 400, Y = 100, Width = 100, Height = 60 };

        var connSnapped = new ShapeCanvasItemViewModel
        {
            Id = "c1",
            Text = "Connector 1",
            ShapeType = "line",
            X = 200,
            Y = 130,
            Width = 200,
            Height = 0,
            PathPoints = new List<(double X, double Y)> { (0, 0), (100, 100) }
        };

        vm.Shapes.Add(sA);
        vm.Shapes.Add(sB);
        vm.Shapes.Add(connSnapped);

        var snappedAudit = vm.AuditConnectorPortSnapping();
        Assert.Equal(1, snappedAudit.TotalConnectors);
        Assert.Equal(2, snappedAudit.TotalShapesEvaluated);
        Assert.Equal(1, snappedAudit.FullySnappedConnectors);
        Assert.Equal(0, snappedAudit.FloatingConnectors);
        Assert.Equal(0, snappedAudit.NearMissPortHazards);
        Assert.Equal(100.0, snappedAudit.PortSnappingScore);

        var detail = snappedAudit.Details.First();
        Assert.True(detail.StartSnapped);
        Assert.True(detail.EndSnapped);
        Assert.Equal("Right", detail.StartSnappedPort);
        Assert.Equal("Left", detail.EndSnappedPort);

        var connFloating = new ShapeCanvasItemViewModel
        {
            Id = "c2",
            Text = "Floating Connector",
            ShapeType = "line",
            X = 850.37,
            Y = 900.82,
            Width = 50,
            Height = 50
        };
        vm.Shapes.Add(connFloating);

        var mixedAudit = vm.AuditConnectorPortSnapping();
        Assert.Equal(2, mixedAudit.TotalConnectors);
        Assert.Equal(1, mixedAudit.FullySnappedConnectors);
        Assert.Equal(1, mixedAudit.FloatingConnectors);
        Assert.True(mixedAudit.SubpixelDriftCount >= 1);
        Assert.True(mixedAudit.PortSnappingScore < 100.0);

        string formatted = ShapeDesignStudioViewModel.FormatConnectorPortSnappingSummary(
            snappedAudit.TotalConnectors, snappedAudit.TotalShapesEvaluated,
            snappedAudit.FullySnappedConnectors, snappedAudit.PartiallySnappedConnectors,
            snappedAudit.FloatingConnectors, snappedAudit.NearMissPortHazards,
            snappedAudit.SubpixelDriftCount, snappedAudit.PortSnappingScore,
            snappedAudit.Category, snappedAudit.IsSelectionScope);
        Assert.Equal(snappedAudit.Summary, formatted);
    }

    [Fact]
    public void Run173_AuditCanvasSpatialBalance_CalculatesCentroidAndMarginCrowding()
    {
        var vm = new ShapeDesignStudioViewModel();
        var empty = vm.AuditCanvasSpatialBalance();
        Assert.Equal(0, empty.TotalShapesEvaluated);
        Assert.Equal(100.0, empty.SpatialBalanceScore);
        Assert.Equal("Empty Canvas", empty.Category);

        // Center a single shape precisely in a 1920x1080 canvas
        // Center is (960, 540). Shape width=200, height=100 -> X=860, Y=490
        var sCenter = new ShapeCanvasItemViewModel
        {
            Id = "center",
            Name = "Centered Box",
            X = 860,
            Y = 490,
            Width = 200,
            Height = 100
        };
        vm.Shapes.Add(sCenter);

        var balancedAudit = vm.AuditCanvasSpatialBalance(canvasWidth: 1920, canvasHeight: 1080);
        Assert.Equal(1, balancedAudit.TotalShapesEvaluated);
        Assert.Equal(960.0, balancedAudit.CentroidX);
        Assert.Equal(540.0, balancedAudit.CentroidY);
        Assert.Equal(0.0, balancedAudit.OffsetFromCenter);
        Assert.Equal(0, balancedAudit.EdgeCrowdingHazards);
        Assert.Equal(100.0, balancedAudit.SpatialBalanceScore);
        Assert.Equal("Equilibrium Spatial Distribution", balancedAudit.Category);

        // Add a shape crowding the extreme top-left border (X=10, Y=10 < 40px)
        var sCrowded = new ShapeCanvasItemViewModel
        {
            Id = "crowded",
            Name = "Edge Shape",
            X = 10,
            Y = 10,
            Width = 100,
            Height = 100
        };
        vm.Shapes.Add(sCrowded);

        var crowdedAudit = vm.AuditCanvasSpatialBalance(canvasWidth: 1920, canvasHeight: 1080);
        Assert.Equal(2, crowdedAudit.TotalShapesEvaluated);
        Assert.True(crowdedAudit.EdgeCrowdingHazards >= 1);
        Assert.True(crowdedAudit.SpatialBalanceScore < 100.0);
    }

    [Fact]
    public void Run174_AuditShapeCollinearityAndSpacing_CalculatesCollinearGroupsAndJitter()
    {
        var vm = new ShapeDesignStudioViewModel();
        var empty = vm.AuditShapeCollinearityAndSpacing();
        Assert.Equal(0, empty.TotalShapesEvaluated);
        Assert.Equal(100.0, empty.CollinearityAndSpacingScore);
        Assert.Equal("Insufficient Shapes", empty.Category);

        // 3 horizontally collinear boxes aligned at Y=100
        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Name = "Box 1", X = 100, Y = 100, Width = 80, Height = 50 };
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Name = "Box 2", X = 200, Y = 100, Width = 80, Height = 50 };
        var s3 = new ShapeCanvasItemViewModel { Id = "s3", Name = "Box 3", X = 300, Y = 100, Width = 80, Height = 50 };

        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);
        vm.Shapes.Add(s3);

        var alignedAudit = vm.AuditShapeCollinearityAndSpacing();
        Assert.Equal(3, alignedAudit.TotalShapesEvaluated);
        Assert.True(alignedAudit.CollinearGroupCount >= 1);
        Assert.Equal(0, alignedAudit.NearMissJitterHazards);
        Assert.True(alignedAudit.UniformSpacingSequences >= 1);
        Assert.Equal(100.0, alignedAudit.CollinearityAndSpacingScore);
        Assert.Equal("Precision Collinear & Uniform Spacing", alignedAudit.Category);

        // Add a box with slight sub-pixel/sub-grid jitter: Y=102 (within tolerance 3.0px)
        var s4 = new ShapeCanvasItemViewModel { Id = "s4", Name = "Jitter Box", X = 400, Y = 102, Width = 80, Height = 50 };
        vm.Shapes.Add(s4);

        var jitterAudit = vm.AuditShapeCollinearityAndSpacing();
        Assert.Equal(4, jitterAudit.TotalShapesEvaluated);
        Assert.True(jitterAudit.NearMissJitterHazards >= 1);
        Assert.True(jitterAudit.CollinearityAndSpacingScore < 100.0);

        string formatted = ShapeDesignStudioViewModel.FormatCollinearitySummary(
            alignedAudit.TotalShapesEvaluated, alignedAudit.CollinearGroupCount, alignedAudit.NearMissJitterHazards,
            alignedAudit.UniformSpacingSequences, alignedAudit.IrregularSpacingSequences,
            alignedAudit.CollinearityAndSpacingScore, alignedAudit.Category, alignedAudit.IsSelectionScope);
        Assert.Equal(alignedAudit.Summary, formatted);
    }

    [Fact]
    public void Run175_AuditVisualFlowDirectionality_CalculatesConnectorVectorsAndMonotonicity()
    {
        var vm = new ShapeDesignStudioViewModel();
        var empty = vm.AuditVisualFlowDirectionality();
        Assert.Equal(0, empty.TotalShapesEvaluated);
        Assert.Equal(0, empty.TotalConnectors);
        Assert.Equal(100.0, empty.FlowMonotonicityScore);
        Assert.Equal("Empty Canvas", empty.Category);

        // 2 boxes and 0 connectors
        var b1 = new ShapeCanvasItemViewModel { Id = "b1", Name = "Box 1", X = 100, Y = 100, Width = 80, Height = 50, Prst = "rect" };
        var b2 = new ShapeCanvasItemViewModel { Id = "b2", Name = "Box 2", X = 300, Y = 100, Width = 80, Height = 50, Prst = "rect" };
        vm.Shapes.Add(b1);
        vm.Shapes.Add(b2);

        var noConnAudit = vm.AuditVisualFlowDirectionality();
        Assert.Equal(2, noConnAudit.TotalShapesEvaluated);
        Assert.Equal(0, noConnAudit.TotalConnectors);
        Assert.Equal(100.0, noConnAudit.FlowMonotonicityScore);
        Assert.Equal("No Connectors Present", noConnAudit.Category);

        // Add 1 LTR connector (100, 100) -> (300, 100) (dx=200, dy=0)
        var c1 = new ShapeCanvasItemViewModel { Id = "c1", Name = "Conn 1", X = 100, Y = 100, Width = 200, Height = 0, Prst = "line" };
        vm.Shapes.Add(c1);

        var ltrAudit = vm.AuditVisualFlowDirectionality();
        Assert.Equal(3, ltrAudit.TotalShapesEvaluated);
        Assert.Equal(1, ltrAudit.TotalConnectors);
        Assert.Equal(1, ltrAudit.LeftToRightCount);
        Assert.Equal(0, ltrAudit.RightToLeftRetrogradeCount);
        Assert.Equal(100.0, ltrAudit.FlowMonotonicityScore);
        Assert.Equal("Strict Monotonic Reading Flow", ltrAudit.Category);

        // Add 1 retrograde connector: (300, 100) back to (100, 100) via PathPoints
        var c2 = new ShapeCanvasItemViewModel
        {
            Id = "c2",
            Name = "Retrograde Conn",
            X = 0,
            Y = 0,
            Width = 100,
            Height = 100,
            Prst = "line",
            PathPoints = new List<(double X, double Y)>
            {
                (300, 100),
                (100, 100)
            }
        };
        vm.Shapes.Add(c2);

        var retroAudit = vm.AuditVisualFlowDirectionality();
        Assert.Equal(4, retroAudit.TotalShapesEvaluated);
        Assert.Equal(2, retroAudit.TotalConnectors);
        Assert.Equal(1, retroAudit.RightToLeftRetrogradeCount);
        Assert.True(retroAudit.FlowMonotonicityScore < 100.0);

        string formatted = ShapeDesignStudioViewModel.FormatVisualFlowSummary(
            ltrAudit.TotalShapesEvaluated, ltrAudit.TotalConnectors, ltrAudit.LeftToRightCount,
            ltrAudit.TopToBottomCount, ltrAudit.RightToLeftRetrogradeCount, ltrAudit.BottomToTopUpwardCount,
            ltrAudit.DominantFlowAxis, ltrAudit.FlowMonotonicityScore, ltrAudit.Category, ltrAudit.IsSelectionScope);
        Assert.Equal(ltrAudit.Summary, formatted);
    }

    [Fact]
    public void Run176_AuditShapeAspectRatioAndGoldenRatio_Comprehensive()
    {
        var vm = new ShapeDesignStudioViewModel();
        var emptyAudit = vm.AuditShapeAspectRatioAndGoldenRatio();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal(100.0, emptyAudit.GoldenRatioAdherenceScore);
        Assert.Equal("Empty / Line Shapes", emptyAudit.Category);

        // Add 1 Golden Ratio shape: 161.8 x 100
        var sGolden = new ShapeCanvasItemViewModel
        {
            Id = "sgolden",
            Name = "Golden Card",
            X = 50,
            Y = 50,
            Width = 161.8,
            Height = 100.0,
            ShapeType = "roundrect"
        };
        vm.Shapes.Add(sGolden);

        // Add 1 Square shape: 100 x 100
        var sSquare = new ShapeCanvasItemViewModel
        {
            Id = "ssquare",
            Name = "Square Avatar",
            X = 250,
            Y = 50,
            Width = 100.0,
            Height = 100.0,
            ShapeType = "circle"
        };
        vm.Shapes.Add(sSquare);

        var auditHarmonious = vm.AuditShapeAspectRatioAndGoldenRatio();
        Assert.Equal(2, auditHarmonious.TotalShapesEvaluated);
        Assert.Equal(1, auditHarmonious.GoldenRatioConformingCount);
        Assert.Equal(1, auditHarmonious.SquareCircleCount);
        Assert.Equal(2, auditHarmonious.StandardAestheticCount);
        Assert.Equal(0, auditHarmonious.UnalignedCount);
        Assert.Equal(100.0, auditHarmonious.GoldenRatioAdherenceScore);
        Assert.Equal("Harmonious Canonical Ratios (Golden Ratio, Square & Standard Proportions)", auditHarmonious.Category);

        // Add 1 irregular non-standard shape: 300 x 50 (Ratio = 6:1)
        var sIrreg = new ShapeCanvasItemViewModel
        {
            Id = "sirreg",
            Name = "Banner Strip",
            X = 50,
            Y = 200,
            Width = 300.0,
            Height = 50.0,
            ShapeType = "rect"
        };
        vm.Shapes.Add(sIrreg);

        var auditWithIrreg = vm.AuditShapeAspectRatioAndGoldenRatio();
        Assert.Equal(3, auditWithIrreg.TotalShapesEvaluated);
        Assert.Equal(1, auditWithIrreg.UnalignedCount);
        Assert.True(auditWithIrreg.GoldenRatioAdherenceScore < 100.0);

        string formatted = ShapeDesignStudioViewModel.FormatAspectRatioGoldenRatioSummary(
            auditHarmonious.TotalShapesEvaluated, auditHarmonious.GoldenRatioConformingCount,
            auditHarmonious.SquareCircleCount, auditHarmonious.StandardAestheticCount,
            auditHarmonious.UnalignedCount, auditHarmonious.MeanGoldenDeviationPercentage,
            auditHarmonious.GoldenRatioAdherenceScore, auditHarmonious.Category, auditHarmonious.IsSelectionScope);
        Assert.Equal(auditHarmonious.Summary, formatted);
    }

    [Fact]
    public void Run177_AuditShapeCornerRadiusConsistency_And_FormatCornerRadiusConsistencySummary()
    {
        var emptyAudit = ShapeDesignStudioViewModel.AuditShapeCornerRadiusConsistency(null);
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal(100.0, emptyAudit.UniformityScore);
        Assert.Equal("No Shapes to Evaluate", emptyAudit.Category);
        Assert.Contains("0 shapes", emptyAudit.Summary);

        var vm = new ShapeDesignStudioViewModel();
        vm.Shapes.Clear();

        // Add 2 uniform rounded rectangle shapes with 8px radius
        var s1 = new ShapeCanvasItemViewModel
        {
            Id = "s1",
            Name = "Card 1",
            Width = 120.0,
            Height = 80.0,
            Prst = "roundrect",
            CornerRadius = 8.0
        };
        var s2 = new ShapeCanvasItemViewModel
        {
            Id = "s2",
            Name = "Card 2",
            Width = 140.0,
            Height = 90.0,
            Prst = "roundrect",
            CornerRadius = 8.0
        };
        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);

        var auditUniform = vm.AuditShapeCornerRadiusConsistency();
        Assert.Equal(2, auditUniform.TotalShapesEvaluated);
        Assert.Equal(2, auditUniform.RoundedRectCount);
        Assert.Equal(0, auditUniform.SharpRectCount);
        Assert.Equal(2, auditUniform.MediumRadiusCount);
        Assert.Equal(1, auditUniform.DistinctRadiiCount);
        Assert.Equal(8.0, auditUniform.DominantRadius);
        Assert.Equal(100.0, auditUniform.UniformityScore);
        Assert.Equal("100% Uniform Fillet Curvature", auditUniform.Category);

        // Add 1 sharp rectangular shape with 0px corner radius
        var sSharp = new ShapeCanvasItemViewModel
        {
            Id = "ssharp",
            Name = "Header Bar",
            Width = 200.0,
            Height = 40.0,
            Prst = "rect",
            CornerRadius = 0.0
        };
        vm.Shapes.Add(sSharp);

        var auditMixed = vm.AuditShapeCornerRadiusConsistency();
        Assert.Equal(3, auditMixed.TotalShapesEvaluated);
        Assert.Equal(2, auditMixed.RoundedRectCount);
        Assert.Equal(1, auditMixed.SharpRectCount);
        Assert.Equal(2, auditMixed.DistinctRadiiCount);
        Assert.Contains("Mixed Fillet Styles", auditMixed.Category);
        Assert.True(auditMixed.UniformityScore < 100.0);

        string formatted = ShapeDesignStudioViewModel.FormatCornerRadiusConsistencySummary(
            auditUniform.TotalShapesEvaluated, auditUniform.RoundedRectCount, auditUniform.SharpRectCount,
            auditUniform.SubtleRadiusCount, auditUniform.MediumRadiusCount, auditUniform.PillRadiusCount,
            auditUniform.DistinctRadiiCount, auditUniform.DominantRadius, auditUniform.UniformityScore,
            auditUniform.Category, auditUniform.IsSelectionScope);
        Assert.Equal(auditUniform.Summary, formatted);
    }

    [Fact]
    public void Run178_AuditShapeZIndexLayeringAndOcclusion()
    {
        var vm = new ShapeDesignStudioViewModel();
        var emptyAudit = vm.AuditShapeZIndexLayeringAndOcclusion();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal(100.0, emptyAudit.StackingClarityScore);
        Assert.Contains("0 shapes", emptyAudit.Summary);

        // 2 non-overlapping shapes: s1 at (10, 10, 100, 100), s2 at (200, 10, 100, 100)
        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Name = "Shape 1", X = 10, Y = 10, Width = 100, Height = 100 };
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Name = "Shape 2", X = 200, Y = 10, Width = 100, Height = 100 };
        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);

        var cleanAudit = vm.AuditShapeZIndexLayeringAndOcclusion();
        Assert.Equal(2, cleanAudit.TotalShapesEvaluated);
        Assert.Equal(0, cleanAudit.OverlapPairsCount);
        Assert.Equal(100.0, cleanAudit.StackingClarityScore);
        Assert.Equal("Pristine Layering & Zero Occlusion", cleanAudit.Category);

        // Add s3 heavily occluding s1: (20, 20, 100, 100) -> overlaps s1 significantly
        var s3 = new ShapeCanvasItemViewModel { Id = "s3", Name = "Shape 3", X = 20, Y = 20, Width = 100, Height = 100 };
        vm.Shapes.Add(s3);

        var occludedAudit = vm.AuditShapeZIndexLayeringAndOcclusion();
        Assert.Equal(3, occludedAudit.TotalShapesEvaluated);
        Assert.Equal(1, occludedAudit.OverlapPairsCount);
        Assert.Equal(1, occludedAudit.MajorOcclusionsCount);
        Assert.True(occludedAudit.StackingClarityScore < 100.0);

        // Add s4 duplicate coincident to s2: (200, 10, 100, 100)
        var s4 = new ShapeCanvasItemViewModel { Id = "s4", Name = "Shape 4", X = 200, Y = 10, Width = 100, Height = 100 };
        vm.Shapes.Add(s4);

        var dupAudit = vm.AuditShapeZIndexLayeringAndOcclusion();
        Assert.Equal(1, dupAudit.DuplicateCoincidentsCount);
        Assert.Contains("Duplicate", dupAudit.Category);

        string formatted = ShapeDesignStudioViewModel.FormatZIndexOcclusionSummary(
            cleanAudit.TotalShapesEvaluated, cleanAudit.OverlapPairsCount, cleanAudit.MajorOcclusionsCount,
            cleanAudit.ModerateOverlapsCount, cleanAudit.DuplicateCoincidentsCount, cleanAudit.ContainerNestingCount,
            cleanAudit.StackingClarityScore, cleanAudit.Category, cleanAudit.IsSelectionScope);
        Assert.Equal(cleanAudit.Summary, formatted);
    }

    [Fact]
    public void Run179_AuditConnectorArrowheadUniformity()
    {
        var vm = new ShapeDesignStudioViewModel();
        var emptyAudit = vm.AuditConnectorArrowheadUniformity();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal(100.0, emptyAudit.OverallConnectorHarmonyScore);
        Assert.Contains("0 shapes", emptyAudit.Summary);

        var n1 = new ShapeCanvasItemViewModel { Id = "n1", Name = "Source", X = 0, Y = 0, Width = 100, Height = 100, Prst = "roundrect" };
        var n2 = new ShapeCanvasItemViewModel { Id = "n2", Name = "Target", X = 200, Y = 0, Width = 100, Height = 100, Prst = "roundrect" };

        var c1 = new ShapeCanvasItemViewModel
        {
            Id = "c1",
            Name = "Connector 1",
            Prst = "line",
            X = 100,
            Y = 50,
            Width = 100,
            Height = 0,
            ArrowheadStart = "None",
            ArrowheadEnd = "FilledTriangle"
        };
        vm.Shapes.Add(n1);
        vm.Shapes.Add(n2);
        vm.Shapes.Add(c1);

        var uniformAudit = vm.AuditConnectorArrowheadUniformity();
        Assert.Equal(3, uniformAudit.TotalShapesEvaluated);
        Assert.Equal(1, uniformAudit.TotalConnectors);
        Assert.Equal(2, uniformAudit.AttachedEndpointsCount);
        Assert.Equal(0, uniformAudit.DanglingEndpointsCount);
        Assert.Equal(1, uniformAudit.FullyAttachedConnectors);
        Assert.Equal("FilledTriangle", uniformAudit.DominantArrowheadType);
        Assert.Equal(100.0, uniformAudit.ArrowheadUniformityScore);
        Assert.Equal(100.0, uniformAudit.EndpointAttachmentScore);
        Assert.Equal(100.0, uniformAudit.OverallConnectorHarmonyScore);
        Assert.Equal("Pristine Uniform & Docked Connectors", uniformAudit.Category);

        var c2 = new ShapeCanvasItemViewModel
        {
            Id = "c2",
            Name = "Floating Connector",
            Prst = "line",
            X = 500,
            Y = 500,
            Width = 100,
            Height = 0,
            ArrowheadStart = "None",
            ArrowheadEnd = "Diamond"
        };
        vm.Shapes.Add(c2);

        var mixedAudit = vm.AuditConnectorArrowheadUniformity();
        Assert.Equal(2, mixedAudit.TotalConnectors);
        Assert.Equal(2, mixedAudit.DanglingEndpointsCount);
        Assert.Equal(50.0, mixedAudit.ArrowheadUniformityScore);
        Assert.True(mixedAudit.OverallConnectorHarmonyScore < 100.0);

        string formatted = ShapeDesignStudioViewModel.FormatArrowheadUniformitySummary(
            uniformAudit.TotalShapesEvaluated, uniformAudit.TotalConnectors, uniformAudit.AttachedEndpointsCount,
            uniformAudit.DanglingEndpointsCount, uniformAudit.FullyAttachedConnectors, uniformAudit.DominantArrowheadType,
            uniformAudit.DominantTypeCount, uniformAudit.ArrowheadUniformityScore, uniformAudit.EndpointAttachmentScore,
            uniformAudit.OverallConnectorHarmonyScore, uniformAudit.Category, uniformAudit.IsSelectionScope);
        Assert.Equal(uniformAudit.Summary, formatted);
    }

    [Fact]
    public void Run180_AuditStrokeWeightAndDashUniformity_CalculatesOutlinesAndHarmony()
    {
        var vm = new ShapeDesignStudioViewModel();
        var emptyAudit = vm.AuditStrokeWeightAndDashUniformity();
        Assert.Equal(0, emptyAudit.TotalShapesEvaluated);
        Assert.Equal(100.0, emptyAudit.OutlineHarmonyScore);
        Assert.Equal("Empty Canvas", emptyAudit.Category);

        var s1 = new ShapeCanvasItemViewModel { Id = "s1", Name = "Shape 1", StrokeWidthPt = 1.5, DashStyle = "Solid" };
        var s2 = new ShapeCanvasItemViewModel { Id = "s2", Name = "Shape 2", StrokeWidthPt = 1.5, DashStyle = "Solid" };
        var s3 = new ShapeCanvasItemViewModel { Id = "s3", Name = "Shape 3", StrokeWidthPt = 1.5, DashStyle = "Solid" };

        vm.Shapes.Add(s1);
        vm.Shapes.Add(s2);
        vm.Shapes.Add(s3);

        var uniformAudit = vm.AuditStrokeWeightAndDashUniformity();
        Assert.Equal(3, uniformAudit.TotalShapesEvaluated);
        Assert.Equal(3, uniformAudit.SolidStrokeCount);
        Assert.Equal(0, uniformAudit.DashedStrokeCount);
        Assert.Equal(3, uniformAudit.StandardStrokeCount);
        Assert.Equal(1.5, uniformAudit.AverageStrokeThickness);
        Assert.Equal(0.0, uniformAudit.StrokeThicknessStdDev);
        Assert.Equal(100.0, uniformAudit.OutlineHarmonyScore);
        Assert.Equal("Harmonious Outlines", uniformAudit.Category);

        var s4 = new ShapeCanvasItemViewModel { Id = "s4", Name = "Shape 4", StrokeWidthPt = 6.0, DashStyle = "Dashed" };
        vm.Shapes.Add(s4);

        var mixedAudit = vm.AuditStrokeWeightAndDashUniformity();
        Assert.Equal(4, mixedAudit.TotalShapesEvaluated);
        Assert.Equal(1, mixedAudit.DashedStrokeCount);
        Assert.Equal(1, mixedAudit.HeavyStrokeCount);
        Assert.True(mixedAudit.StrokeThicknessStdDev > 0);
        Assert.True(mixedAudit.OutlineHarmonyScore < 100.0);

        string formatted = ShapeDesignStudioViewModel.FormatStrokeWeightUniformitySummary(
            uniformAudit.TotalShapesEvaluated, uniformAudit.SolidStrokeCount, uniformAudit.DashedStrokeCount,
            uniformAudit.DottedStrokeCount, uniformAudit.HeavyStrokeCount, uniformAudit.StandardStrokeCount,
            uniformAudit.HairlineStrokeCount, uniformAudit.AverageStrokeThickness, uniformAudit.StrokeThicknessStdDev,
            uniformAudit.OutlineHarmonyScore, uniformAudit.Category, uniformAudit.IsSelectionScope);
        Assert.Equal(uniformAudit.Summary, formatted);
    }
}






