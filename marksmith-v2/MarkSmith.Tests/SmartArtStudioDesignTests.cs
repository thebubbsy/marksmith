using System.Linq;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

// The SmartArt Design Studio's outline designer: every node operation must land in the Markdown
// (the canonical form the preview + DOCX export consume) and stay consistent through the
// markdown <-> tree round-trip.
public class SmartArtStudioDesignTests
{
    private static SmartArtDesignStudioViewModel NewStudio(string markdown = "- Root\n  - A\n  - B")
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = markdown;
        return vm;
    }

    private static StudioNodeViewModel NodeByText(SmartArtDesignStudioViewModel vm, string text) =>
        vm.OutlineRows.First(n => n.Text == text);

    [Fact]
    public void AddChild_NestsUnderSelectedNode_AndEntersRename()
    {
        var vm = NewStudio();
        var a = NodeByText(vm, "A");
        vm.Select(a);

        vm.AddChildCommand.Execute(null);

        Assert.Equal("- Root\n  - A\n    - New Node\n  - B", vm.MarkdownText);
        Assert.True(NodeByText(vm, "New Node").IsEditing, "new node drops straight into rename");
    }

    [Fact]
    public void AddSibling_InsertsAfterSelectedNode_AtSameDepth()
    {
        var vm = NewStudio();
        var a = NodeByText(vm, "A");
        vm.Select(a);

        vm.AddSiblingCommand.Execute(null);

        Assert.Equal("- Root\n  - A\n  - New Node\n  - B", vm.MarkdownText);
    }

    [Fact]
    public void DeleteSelected_RemovesNodeAndSubtree_AndSelectsNextSibling()
    {
        var vm = NewStudio("- Root\n  - A\n    - A1\n    - A2\n  - B");
        var a = NodeByText(vm, "A");
        vm.Select(a);

        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal("- Root\n  - B", vm.MarkdownText);
        Assert.Equal("B", vm.SelectedNode?.Text);
    }

    [Fact]
    public void MoveUpAndDown_ReorderSiblings()
    {
        var vm = NewStudio("- A\n- B\n- C");
        vm.Select(NodeByText(vm, "C"));
        vm.MoveUpCommand.Execute(null);
        Assert.Equal("- A\n- C\n- B", vm.MarkdownText);

        vm.MoveDownCommand.Execute(null);
        Assert.Equal("- A\n- B\n- C", vm.MarkdownText);
    }

    [Fact]
    public void Promote_OutdentsNode_ToParentLevel()
    {
        var vm = NewStudio("- Root\n  - A\n    - A1\n  - B");
        vm.Select(NodeByText(vm, "A1"));

        vm.PromoteCommand.Execute(null);

        Assert.Equal("- Root\n  - A\n  - A1\n  - B", vm.MarkdownText);
    }

    [Fact]
    public void Demote_IndentsNode_UnderPreviousSibling()
    {
        var vm = NewStudio("- Root\n  - A\n  - B");
        vm.Select(NodeByText(vm, "B"));

        vm.DemoteCommand.Execute(null);

        Assert.Equal("- Root\n  - A\n    - B", vm.MarkdownText);
    }

    [Fact]
    public void UndoRedo_RevertsAndReappliesDesignOperation()
    {
        var vm = NewStudio();
        vm.Select(NodeByText(vm, "A"));
        vm.AddChildCommand.Execute(null);
        Assert.Contains("- A\n    - New Node", vm.MarkdownText);

        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal("- Root\n  - A\n  - B", vm.MarkdownText);

        Assert.True(vm.CanRedo);
        vm.RedoCommand.Execute(null);
        Assert.Contains("- A\n    - New Node", vm.MarkdownText);
    }

    [Fact]
    public void Rename_CommitsNewText_AndCancellationRestores()
    {
        var vm = NewStudio();
        var a = NodeByText(vm, "A");
        vm.Select(a);

        vm.BeginRename(a);
        Assert.True(a.IsEditing);
        a.Text = "Alpha";
        vm.CommitRename();
        Assert.Equal("- Root\n  - Alpha\n  - B", vm.MarkdownText);

        vm.BeginRename(NodeByText(vm, "B"));
        NodeByText(vm, "B").Text = "should be discarded";
        vm.CancelRename();
        Assert.Equal("- Root\n  - Alpha\n  - B", vm.MarkdownText);
        Assert.False(NodeByText(vm, "B").IsEditing);
    }

    [Fact]
    public void DeleteLastNode_ClearsOutline_NoPhantomRoot()
    {
        var vm = NewStudio("- Only");
        vm.Select(NodeByText(vm, "Only"));
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal("", vm.MarkdownText);
        Assert.Empty(vm.RootNodes);
        Assert.Empty(vm.OutlineRows);
    }

    [Fact]
    public void DeleteLastChild_SelectsParent()
    {
        var vm = NewStudio("- Root\n  - Only");
        vm.Select(NodeByText(vm, "Only"));
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal("- Root", vm.MarkdownText);
        Assert.Equal("Root", vm.SelectedNode?.Text);
    }

    [Fact]
    public void RenameCommit_IsUndoable_AsItsOwnStep()
    {
        var vm = NewStudio();
        vm.Select(NodeByText(vm, "A"));
        vm.AddChildCommand.Execute(null);
        vm.CommitRename(); // commit the "New Node" placeholder rename without changing text

        var node = NodeByText(vm, "New Node");
        vm.BeginRename(node);
        node.Text = "Renamed";
        vm.CommitRename();
        Assert.Equal("- Root\n  - A\n    - Renamed\n  - B", vm.MarkdownText);

        vm.UndoCommand.Execute(null); // must revert the RENAME, not the add
        Assert.Equal("- Root\n  - A\n    - New Node\n  - B", vm.MarkdownText);
    }

    [Fact]
    public void MarkdownToTree_RoundTripsStably()
    {
        const string md = "- Executive Board\n  - CEO\n    - Engineering\n    - Product\n  - CFO\n- COO";
        var vm = NewStudio(md);

        vm.SyncTreeToMarkdown(); // rebuild from tree; must not churn the text

        Assert.Equal(md, vm.MarkdownText);
        Assert.Equal(2, vm.RootNodes.Count);                        // Executive Board, COO
        Assert.Equal(2, vm.RootNodes[0].Children.Count);            // CEO, CFO
        Assert.Equal(2, vm.RootNodes[0].Children[0].Children.Count); // Engineering, Product
        Assert.Equal(6, vm.OutlineRows.Count);
        Assert.Equal(2, NodeByText(vm, "Engineering").Depth);
    }
}
