using System.IO;
using System.Threading.Tasks;
using MarkSmith.ViewModels.ShapeStudio;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// Run #56: Shape Studio and SmartArt Studio closed over unfinished work without asking. The
/// windows now ask while the view model reports work that hasn't been inserted, exported or
/// copied; these pin when that is.
/// </summary>
public class StudioCloseGuardTests
{
    [Fact]
    public void An_empty_shape_canvas_closes_freely()
    {
        var vm = new ShapeDesignStudioViewModel();
        Assert.False(vm.HasUnkeptWork);
    }

    [Fact]
    public void A_new_shape_is_unkept_until_it_is_inserted()
    {
        var vm = new ShapeDesignStudioViewModel();
        string? inserted = null;
        vm.InsertToDocumentRequested += (_, block) => inserted = block;

        vm.AddShapeAt("rect", 10, 10);
        Assert.True(vm.HasUnkeptWork);

        vm.InsertIntoDocument();
        Assert.NotNull(inserted);
        Assert.False(vm.HasUnkeptWork);

        vm.AddShapeAt("ellipse", 200, 10); // a change after the insert is new work
        Assert.True(vm.HasUnkeptWork);
    }

    [Fact]
    public async Task Exporting_to_Word_keeps_the_canvas()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.AddShapeAt("rect", 10, 10);
        string path = Path.Combine(Path.GetTempPath(), $"ms-closeguard-{System.Guid.NewGuid():N}.docx");
        try
        {
            Assert.True(await vm.ExportToWordAsync(template: false, path));
            Assert.False(vm.HasUnkeptWork);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Clearing_a_kept_canvas_leaves_nothing_to_lose()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.AddShapeAt("rect", 10, 10);
        vm.ClearAll();
        Assert.False(vm.HasUnkeptWork);
    }

    [Fact]
    public void SmartArt_opens_clean_and_an_outline_edit_is_unkept()
    {
        var vm = new SmartArtDesignStudioViewModel();
        Assert.False(vm.HasUnkeptWork);

        vm.MarkdownText = "- One\n- Two\n- Three";
        Assert.True(vm.HasUnkeptWork);

        vm.InsertIntoDocument();
        Assert.False(vm.HasUnkeptWork);
    }

    [Fact]
    public void SmartArt_a_preloaded_outline_is_not_work_to_lose()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Preload("- Plan\n- Build\n- Ship", "process");
        Assert.False(vm.HasUnkeptWork);
    }

    [Fact]
    public void SmartArt_a_second_preload_keeps_an_uninserted_outline_one_undo_away()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Preload("- Plan\n- Build", "process");
        vm.MarkdownText = "- Plan\n- Build\n- My own step";

        vm.Preload("- Something else", "process");
        Assert.Contains("Ctrl+Z", vm.StatusMessage);

        vm.Undo();
        Assert.Contains("My own step", vm.MarkdownText);
    }

    [Fact]
    public void SmartArt_a_second_preload_over_kept_work_starts_fresh()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Preload("- Plan\n- Build", "process");
        vm.Preload("- Something else", "process");
        Assert.False(vm.CanUndo);
    }
}
