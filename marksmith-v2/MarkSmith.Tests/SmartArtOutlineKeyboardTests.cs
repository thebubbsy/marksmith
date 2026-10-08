using System.Linq;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;
using K = MarkSmith.ViewModels.SmartArtStudio.SmartArtDesignStudioViewModel.OutlineKey;

namespace MarkSmith.Core.Tests;

/// <summary>
/// The SmartArt outline answered only Delete and F2 from the keyboard; building a tree needed the
/// mouse for every step. These pin the outline's keyboard model (VM HandleOutlineKey).
/// </summary>
public class SmartArtOutlineKeyboardTests
{
    private static SmartArtDesignStudioViewModel Load(string md = "- Plan\n  - Research\n  - Design\n- Build\n- Ship\n")
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Preload(md, "hierarchy");
        return vm;
    }

    private static string[] Rows(SmartArtDesignStudioViewModel vm) => vm.OutlineRows.Select(r => new string(' ', r.Depth * 2) + r.Text).ToArray();

    [Fact]
    public void Up_down_home_end_walk_the_rows_in_outline_order()
    {
        var vm = Load();
        Assert.True(vm.HandleOutlineKey(K.Down)); // nothing selected: the first row
        Assert.Equal("Plan", vm.SelectedNode!.Text);
        vm.HandleOutlineKey(K.Down);
        Assert.Equal("Research", vm.SelectedNode!.Text);
        vm.HandleOutlineKey(K.End);
        Assert.Equal("Ship", vm.SelectedNode!.Text);
        vm.HandleOutlineKey(K.Down); // stays on the last row
        Assert.Equal("Ship", vm.SelectedNode!.Text);
        vm.HandleOutlineKey(K.Up);
        Assert.Equal("Build", vm.SelectedNode!.Text);
        vm.HandleOutlineKey(K.Home);
        Assert.Equal("Plan", vm.SelectedNode!.Text);
    }

    [Fact]
    public void Tab_indents_and_shift_tab_outdents()
    {
        var vm = Load();
        vm.Select(vm.OutlineRows.Single(r => r.Text == "Build"));
        Assert.True(vm.HandleOutlineKey(K.Tab));
        Assert.Equal(new[] { "Plan", "  Research", "  Design", "  Build", "Ship" }, Rows(vm));
        Assert.True(vm.HandleOutlineKey(K.Tab, shift: true));
        Assert.Equal(new[] { "Plan", "  Research", "  Design", "Build", "Ship" }, Rows(vm));
        Assert.Contains("- Build", vm.MarkdownText);
    }

    [Fact]
    public void Alt_up_and_down_move_the_item_among_its_siblings()
    {
        var vm = Load();
        vm.Select(vm.OutlineRows.Single(r => r.Text == "Design"));
        vm.HandleOutlineKey(K.Up, alt: true);
        Assert.Equal(new[] { "Plan", "  Design", "  Research", "Build", "Ship" }, Rows(vm));
        Assert.Equal("Design", vm.SelectedNode!.Text); // the selection travels with it
        vm.HandleOutlineKey(K.Down, alt: true);
        Assert.Equal(new[] { "Plan", "  Research", "  Design", "Build", "Ship" }, Rows(vm));
    }

    [Fact]
    public void Enter_adds_a_sibling_and_insert_a_child_both_ready_to_type()
    {
        var vm = Load();
        vm.Select(vm.OutlineRows.Single(r => r.Text == "Research"));
        vm.HandleOutlineKey(K.Enter);
        Assert.Equal(new[] { "Plan", "  Research", "  New Node", "  Design", "Build", "Ship" }, Rows(vm));
        Assert.True(vm.SelectedNode!.IsEditing);
        vm.CommitRename();

        vm.Select(vm.OutlineRows.Single(r => r.Text == "Ship"));
        vm.HandleOutlineKey(K.Insert);
        Assert.Equal(new[] { "Ship", "  New Node" }, Rows(vm)[^2..]); // a child of Ship
        Assert.True(vm.SelectedNode!.IsEditing);
    }

    [Fact]
    public void Delete_and_f2_work_and_unused_keys_report_unhandled()
    {
        var vm = Load();
        Assert.False(vm.HandleOutlineKey(K.Tab)); // nothing selected: let Tab move focus
        vm.Select(vm.OutlineRows.Single(r => r.Text == "Ship"));
        Assert.True(vm.HandleOutlineKey(K.Delete));
        Assert.DoesNotContain(vm.OutlineRows, r => r.Text == "Ship");
        Assert.True(vm.HandleOutlineKey(K.F2));
        Assert.True(vm.SelectedNode!.IsEditing);
    }

    [Fact]
    public void An_empty_outline_starts_with_enter()
    {
        var vm = Load("");
        while (vm.OutlineRows.Count > 0) { vm.Select(vm.OutlineRows[0]); vm.DeleteSelected(); }
        Assert.False(vm.HandleOutlineKey(K.Down));
        Assert.True(vm.HandleOutlineKey(K.Enter));
        Assert.Single(vm.OutlineRows);
    }

    [Fact]
    public void Each_structural_key_is_one_undo_step()
    {
        var vm = Load();
        var before = Rows(vm);
        vm.Select(vm.OutlineRows.Single(r => r.Text == "Build"));
        vm.HandleOutlineKey(K.Tab);
        vm.Undo();
        Assert.Equal(before, Rows(vm));
    }
}
