using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Tests;

// The cycle presets placed identical circular-arrow icons between their stages — four "refresh"
// symbols rather than a direction of travel. Each connector is now a chevron midway between two
// stages, rotated to point at the next one.
public class ShapeCycleFlowTests
{
    private static (List<ShapeCanvasItemViewModel> Stages, List<ShapeCanvasItemViewModel> Chevrons) Generate(Action<ShapeDesignStudioViewModel> preset)
    {
        var vm = new ShapeDesignStudioViewModel();
        preset(vm);
        return (vm.Shapes.Where(s => s.Prst == "roundrect").ToList(), vm.Shapes.Where(s => s.Prst == "chevron").ToList());
    }

    public static IEnumerable<object[]> Presets() => new[]
    {
        new object[] { "PDCA", 4 },
        new object[] { "BuildMeasureLearn", 3 },
    };

    private static Action<ShapeDesignStudioViewModel> Preset(string name) =>
        name == "PDCA" ? vm => vm.GenerateCycleTemplate() : vm => vm.GenerateBuildMeasureLearnTemplate();

    [Theory]
    [MemberData(nameof(Presets))]
    public void Every_stage_hands_off_to_the_next_with_a_chevron_pointing_at_it(string name, int stages)
    {
        var (stage, chevrons) = Generate(Preset(name));
        Assert.Equal(stages, stage.Count);
        Assert.Equal(stages, chevrons.Count);

        for (int i = 0; i < stages; i++)
        {
            var from = stage[i];
            var to = stage[(i + 1) % stages];
            var c = chevrons[i];
            double fx = from.X + from.Width / 2, fy = from.Y + from.Height / 2;
            double tx = to.X + to.Width / 2, ty = to.Y + to.Height / 2;

            // Sits midway between the two stages...
            Assert.InRange(c.X + c.Width / 2, (fx + tx) / 2 - 1, (fx + tx) / 2 + 1);
            Assert.InRange(c.Y + c.Height / 2, (fy + ty) / 2 - 1, (fy + ty) / 2 + 1);

            // ...pointing from this stage to the next (a chevron points right at 0°).
            double expected = Math.Atan2(ty - fy, tx - fx) * 180 / Math.PI;
            double diff = Math.Abs(((c.Rotation - expected) % 360 + 540) % 360 - 180);
            Assert.True(diff <= 1, $"{name} chevron {i}: rotation {c.Rotation}, expected ≈{expected:F0}");
        }

        // A loop: the connectors turn through every direction, never four of the same.
        Assert.Equal(stages, chevrons.Select(c => c.Rotation).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Chevrons_never_sit_on_a_stage(string name, int _)
    {
        var (stages, chevrons) = Generate(Preset(name));
        foreach (var c in chevrons)
        {
            // Conservative: the rotated chevron fits inside a circle of its half-diagonal.
            double cx = c.X + c.Width / 2, cy = c.Y + c.Height / 2;
            double r = Math.Sqrt(c.Width * c.Width + c.Height * c.Height) / 2;
            foreach (var s in stages)
            {
                double nx = Math.Clamp(cx, s.X, s.X + s.Width), ny = Math.Clamp(cy, s.Y, s.Y + s.Height);
                double d = Math.Sqrt((cx - nx) * (cx - nx) + (cy - ny) * (cy - ny));
                Assert.True(d > r, $"{name}: chevron at ({cx:F0},{cy:F0}) overlaps stage '{s.Text}'");
            }
        }
    }
}
