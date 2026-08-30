using System.Text;
using MarkSmith.Core.Composer;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Renders every MLShape Studio preset to SVG so the gallery can be inspected as a whole, and
/// asserts the structural properties a preset has to satisfy to be worth shipping.
///
/// The presets are generated code paths that nothing rendered in bulk, so a template that emitted
/// the wrong geometry — a connector that came out as a filled block, a shape stack with nothing
/// drawn — looked fine in the list and only failed when a user picked it.
/// </summary>
public class ShapePresetContactSheetTests
{
    private static ShapeDesignStudioViewModel NewVm()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.RegisterPresets();
        return vm;
    }

    public static TheoryData<string> PresetNames()
    {
        var data = new TheoryData<string>();
        foreach (var p in NewVm().AllPresets) data.Add(p.Name);
        return data;
    }

    private static List<ComposedShape> Build(string name)
    {
        var vm = NewVm();
        var preset = vm.AllPresets.First(p => p.Name == name);
        preset.Generate(vm);
        return vm.SnapshotComposed();
    }

    [Fact]
    public void Every_Preset_Produces_Shapes()
    {
        var empty = NewVm().AllPresets
            .Where(p => Build(p.Name).Count == 0)
            .Select(p => p.Name)
            .ToList();
        Assert.True(empty.Count == 0, "presets that generated nothing: " + string.Join(", ", empty));
    }

    [Theory]
    [MemberData(nameof(PresetNames))]
    public void Preset_Shapes_Have_Real_Geometry(string name)
    {
        foreach (var s in Build(name))
        {
            Assert.True(s.W > 0 && s.H > 0, $"{name}: '{s.Prst}' has a zero dimension ({s.W}x{s.H})");
            Assert.False(string.IsNullOrWhiteSpace(s.Fill), $"{name}: '{s.Prst}' has no fill colour");
        }
    }

    [Theory]
    [MemberData(nameof(PresetNames))]
    public void Preset_Connectors_Are_Strokes_Not_Filled_Blocks(string name)
    {
        // A connector must carry PathPoints (rendered as a stroked polyline in both SVG and
        // DrawingML). A "line" without them falls back to a filled bounding box, which is how a
        // connector ends up looking like a square.
        foreach (var s in Build(name).Where(s => s.Prst == "line"))
        {
            Assert.True(s.PathPoints is { Count: >= 2 },
                $"{name}: a 'line' shape has no PathPoints, so it renders as a filled block");
        }
    }

    [Fact]
    public void Render_The_Whole_Gallery_For_Inspection()
    {
        var vm = NewVm();
        var sb = new StringBuilder("<body style=\"margin:0;background:#eef0f4;font:13px system-ui\">");
        foreach (var p in vm.AllPresets)
        {
            var shapes = Build(p.Name);
            var (w, h) = ShapeMarkdownCodec.CanvasSize(shapes);
            var svg = ImageShapeComposer.RenderSvg(shapes, w, h);
            sb.Append($"<div style=\"background:#fff;margin:10px;padding:8px;border:1px solid #ccd\">")
              .Append($"<div style=\"font-weight:600;margin-bottom:4px\">{p.Category} — {p.Name} ")
              .Append($"<span style=\"font-weight:400;color:#777\">({shapes.Count} shapes)</span></div>")
              .Append(svg).Append("</div>");
        }
        sb.Append("</body>");

        var outPath = Path.Combine(Path.GetTempPath(), "mlshape-gallery.html");
        File.WriteAllText(outPath, sb.ToString());
        Assert.True(File.Exists(outPath));
    }
}
