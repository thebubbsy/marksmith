using System.Linq;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>SmartArt Design Studio gallery filtering and command enablement.</summary>
public class SmartArtStudioGalleryTests
{
    [Fact]
    public void Type_filter_narrows_the_gallery_and_the_count_says_so()
    {
        var vm = new SmartArtDesignStudioViewModel();
        int all = vm.Layouts.Count;
        Assert.True(all > 0);
        Assert.Equal($"{all} layouts", vm.LayoutCountText);

        vm.SelectedCategory = "Cycle";
        Assert.NotEmpty(vm.Layouts);
        Assert.All(vm.Layouts, l => Assert.Equal("Cycle", l.Category));
        Assert.Equal($"{vm.Layouts.Count} of {all} layouts", vm.LayoutCountText);

        vm.SelectedCategory = "All";
        Assert.Equal(all, vm.Layouts.Count);
    }

    [Fact]
    public void Every_category_the_gallery_assigns_is_offered_in_the_type_filter()
    {
        var vm = new SmartArtDesignStudioViewModel();
        foreach (var cat in vm.Layouts.Select(l => l.Category).Distinct())
            Assert.Contains(cat, vm.Categories);
    }

    [Fact]
    public void Search_and_type_filter_combine()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.SearchQuery = "cycle";
        int cycles = vm.Layouts.Count;
        vm.SelectedCategory = "Pyramid";
        Assert.True(vm.Layouts.Count < cycles);
        Assert.Equal(vm.Layouts.Count == 0, vm.HasNoLayoutMatches);
    }

    [Fact]
    public void Search_matches_the_friendly_name()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.SearchQuery = "Circle Process"; // DisplayName of AlternatingCircleProcess
        Assert.Contains(vm.Layouts, l => l.Alias == "AlternatingCircleProcess");
    }

    [Fact]
    public void Every_gallery_row_has_an_icon_and_a_markdown_hint()
    {
        var vm = new SmartArtDesignStudioViewModel();
        Assert.All(vm.Layouts, l =>
        {
            Assert.False(string.IsNullOrEmpty(l.Glyph));
            Assert.Equal($"Markdown name: {l.Alias}", l.AliasHint);
        });
    }

    [Fact]
    public void Insert_is_disabled_for_an_empty_outline()
    {
        var vm = new SmartArtDesignStudioViewModel();
        Assert.True(vm.InsertIntoDocumentCommand.CanExecute(null));
        vm.MarkdownText = "";
        Assert.False(vm.InsertIntoDocumentCommand.CanExecute(null));
    }

    [Fact]
    public void Opens_on_a_layout_that_suits_the_sample_outline()
    {
        var vm = new SmartArtDesignStudioViewModel();
        Assert.NotNull(vm.SelectedLayout);
        Assert.NotEqual("AccentedPicture", vm.SelectedLayout!.Alias); // not just the first alphabetically
    }

    [Fact]
    public void Preview_title_names_the_selected_layout()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.SelectedLayout = vm.Layouts.First(l => l.Alias == "AlternatingCircleProcess");
        Assert.Equal("Alternating Circle Process", vm.PreviewTitle);
    }
}
