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
}
