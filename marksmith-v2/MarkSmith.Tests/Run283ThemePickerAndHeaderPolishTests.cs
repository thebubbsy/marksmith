using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

// Run #283:
// - pinning a theme no longer blanks the Theme picker and saves "Theme": null (the list was
//   cleared under the picker's TwoWay binding), and it says what it did in the status line;
// - creating or editing a custom theme keeps pinned themes at the top of the list;
// - the star and palette buttons beside the picker name the theme and what pressing them does
//   ("Unpin GitHub Light", "Edit theme Ocean") instead of one fixed tooltip-sentence name;
// - the centre step title names the open view when "Editor / Preview" would trim to "Editor / Pr…";
// - the output folder's tooltip shows the full path the narrow box cuts off;
// - Insert ▸ Data grid counts rows under the header instead of counting the header as a row.
[Collection("MainViewModel settings")]
public class Run283ThemePickerAndHeaderPolishTests
{
    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoDir(), relative)).Replace("\r\n", "\n");

    private static string Body(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[start..(i + 1)];
        }
        return src[start..];
    }

    private const string Main = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string MainXaml = "MarkSmith.Desktop/MainWindow.xaml";

    // What the WinUI ComboBox does when its ItemsSource is cleared: its SelectedItem binding
    // writes null back to the view model.
    private static void BindLikeThePicker(MainViewModel vm) =>
        vm.ThemeNames.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) vm.SelectedThemeName = null!;
        };

    private static void WithFavorites(Action body)
    {
        var settings = AppServices.Settings.Current;
        var saved = settings.FavoriteThemes.ToList();
        var savedTheme = settings.Theme;
        try { body(); }
        finally
        {
            settings.FavoriteThemes.Clear();
            settings.FavoriteThemes.AddRange(saved);
            settings.Theme = savedTheme;
        }
    }

    [Fact]
    public void Pinning_keeps_the_theme_selected_and_never_saves_a_null_theme()
    {
        WithFavorites(() =>
        {
            var vm = new MainViewModel();
            var theme = vm.ThemeNames.Last();
            vm.SelectedThemeName = theme;
            AppServices.Settings.Current.FavoriteThemes.Remove(theme);
            BindLikeThePicker(vm);

            vm.ToggleFavoriteTheme();

            Assert.Equal(theme, vm.SelectedThemeName);
            Assert.Equal(theme, AppServices.Settings.Current.Theme);
            Assert.True(vm.IsCurrentThemeFavorite);
            Assert.Equal(theme, vm.ThemeNames.First()); // pinned themes float to the top
            Assert.Equal(ThemeButtonText.PinStatus(theme, pinned: true), vm.StatusText);

            vm.ToggleFavoriteTheme();
            Assert.Equal(theme, vm.SelectedThemeName);
            Assert.Equal(theme, AppServices.Settings.Current.Theme);
            Assert.False(vm.IsCurrentThemeFavorite);
            Assert.Equal(ThemeButtonText.PinStatus(theme, pinned: false), vm.StatusText);
        });
    }

    [Fact]
    public void Rebuild_keeps_pins_first_and_reselects_the_requested_theme()
    {
        WithFavorites(() =>
        {
            var vm = new MainViewModel();
            var pinned = vm.ThemeNames.Last();
            var other = vm.ThemeNames.First(n => n != pinned);
            AppServices.Settings.Current.FavoriteThemes.Clear();
            AppServices.Settings.Current.FavoriteThemes.Add(pinned);
            BindLikeThePicker(vm);
            var raised = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.SelectedThemeName)) raised++; };

            vm.RebuildThemeNames(other);

            Assert.Equal(pinned, vm.ThemeNames.First());
            Assert.Equal(other, vm.SelectedThemeName);
            Assert.Equal(other, AppServices.Settings.Current.Theme);
            Assert.True(raised > 0);
        });
    }

    [Fact]
    public void Rebuild_with_an_unknown_name_falls_back_to_the_first_theme()
    {
        WithFavorites(() =>
        {
            var vm = new MainViewModel();
            vm.RebuildThemeNames("No such theme " + Guid.NewGuid());
            Assert.Equal(vm.ThemeNames.First(), vm.SelectedThemeName);
        });
    }

    [Fact]
    public void A_null_theme_from_the_picker_never_reaches_settings()
    {
        WithFavorites(() =>
        {
            var vm = new MainViewModel();
            var theme = vm.ThemeNames.First();
            vm.SelectedThemeName = theme;
            vm.SelectedThemeName = null!;
            Assert.Equal(theme, AppServices.Settings.Current.Theme);
        });
    }

    [Theory]
    [InlineData("GitHub Light", false, "Pin GitHub Light")]
    [InlineData("GitHub Light", true, "Unpin GitHub Light")]
    [InlineData("", false, "Pin theme")]
    public void Pin_button_names_the_theme_and_the_action(string theme, bool pinned, string expected)
    {
        Assert.Equal(expected, ThemeButtonText.PinName(theme, pinned));
        Assert.Contains(pinned ? "Unpin" : "Pin", ThemeButtonText.PinToolTip(theme, pinned), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Palette_button_says_edit_for_a_custom_theme_and_create_otherwise()
    {
        Assert.Equal("Edit theme Ocean", ThemeButtonText.EditName("Ocean", isCustom: true));
        Assert.Equal("Create a theme", ThemeButtonText.EditName("GitHub Light", isCustom: false));
        Assert.Contains("“GitHub Light”", ThemeButtonText.EditToolTip("GitHub Light", isCustom: false));
        Assert.StartsWith("Edit “Ocean”", ThemeButtonText.EditToolTip("Ocean", isCustom: true));
    }

    [Fact]
    public void Theme_buttons_are_named_in_code_and_follow_the_view_model()
    {
        var xaml = Read(MainXaml);
        Assert.Contains("x:Name=\"ThemePinButton\"", xaml);
        Assert.Contains("x:Name=\"ThemeEditButton\"", xaml);
        Assert.DoesNotContain("Pin/unpin the selected theme", xaml);

        var code = Read(Main);
        var update = Body(code, "private void UpdateThemeButtons()");
        Assert.Contains("ThemeButtonText.PinName(", update);
        Assert.Contains("ThemeButtonText.EditName(", update);
        Assert.Contains("App.Themes.IsBuiltin(", update);
        var changed = Body(code, "private void OnViewModelPropertyChanged(");
        Assert.Contains("nameof(ViewModels.MainViewModel.IsCurrentThemeFavorite)", changed);
        Assert.Contains("UpdateThemeButtons();", changed);
        // Creating / deleting a custom theme rebuilds through the view model (pins kept first).
        Assert.Contains("ViewModel.RebuildThemeNames(select)", code);
        Assert.DoesNotContain("foreach (var t in App.Themes.All) ViewModel.ThemeNames.Add(", code);
    }

    [Theory]
    [InlineData("Code", false, "Editor")]
    [InlineData("Split", false, "Editor")]
    [InlineData("Preview", false, "Preview")]
    [InlineData("Code", true, "Editor / Preview")]
    [InlineData("Preview", true, "Editor / Preview")]
    public void Centre_step_title_names_the_open_view_when_the_full_title_would_trim(string view, bool fits, string expected) =>
        Assert.Equal(expected, EditorStepTitleText.For(view, fits));

    [Fact]
    public void Centre_step_title_refits_on_resize_and_view_change()
    {
        var code = Read(Main);
        Assert.Contains("EditorStepTitleHost.SizeChanged += (_, _) => FitEditorStepTitle();", code);
        Assert.Contains("EditorStepTitleProbe.SizeChanged += (_, _) => FitEditorStepTitle();", code);
        Assert.Contains("EditorStepTitleProbe?.ActualWidth", code);
        // The probe is invisible and silent: Opacity 0, raw to UIA, inside a zero-size Canvas.
        var xaml = Read(MainXaml);
        var probe = xaml[xaml.IndexOf("x:Name=\"EditorStepTitleProbe\"", StringComparison.Ordinal)..];
        probe = probe[..probe.IndexOf("/>", StringComparison.Ordinal)];
        Assert.Contains("Opacity=\"0\"", probe);
        Assert.Contains("AccessibilityView=\"Raw\"", probe);
        Assert.Contains("FitEditorStepTitle();", Body(code, "private void ApplyViewMode(ViewMode mode)"));
        var fit = Body(code, "private void FitEditorStepTitle()");
        Assert.Contains("EditorStepTitleText.For(_viewMode.ToString()", fit);
        // Only writes when the text changes, so a resize doesn't churn the trimmed-text tooltip.
        Assert.Contains("if (EditorStepTitle.Text != text)", fit);
    }

    [Fact]
    public void Output_folder_tooltip_shows_the_whole_path_then_the_menu_hint()
    {
        var tip = OutputFolderText.ToolTip(@"C:\Users\Someone\OneDrive\Documents\Exports");
        Assert.StartsWith(@"Output folder: C:\Users\Someone\OneDrive\Documents\Exports", tip);
        Assert.Contains("Shift+F10", tip);
        Assert.Contains("next to the input file", OutputFolderText.ToolTip("  "));

        var code = Read(Main);
        Assert.Contains("OutputFolderText.ToolTip(", Body(code, "private void UpdateOutputFolderToolTip()"));
        Assert.Contains("nameof(ViewModels.MainViewModel.OutputFolder)", Body(code, "private void OnViewModelPropertyChanged("));
    }

    [Theory]
    [InlineData(3, true, "Header + 2 rows")]
    [InlineData(2, true, "Header + 1 row")]
    [InlineData(1, true, "Header + 0 rows")]
    [InlineData(0, true, "0 rows")]
    [InlineData(3, false, "3 rows")]
    [InlineData(1, false, "1 row")]
    public void Data_grid_count_leaves_the_header_out(int lines, bool headerFirst, string expected) =>
        Assert.Equal(expected, InsertCountText.Lines(lines, "row", headerFirst));

    [Fact]
    public void One_per_line_boxes_are_named_after_their_heading()
    {
        var insert = Read("MarkSmith.Desktop/Views/InsertDialogControls.cs");
        Assert.Contains("AutomationProperties.SetName(_box, header);", Body(insert, "public LinesInsertControl("));
        Assert.Contains("AutomationProperties.SetName(_box, dataHeader);", Body(insert, "public ChartInsertControl()"));
    }

    [Fact]
    public void Shortfall_hint_asks_for_a_header_and_rows()
    {
        Assert.Equal("Add a header line and at least one row under it.", InsertCountText.Shortfall(2, "row", headerFirst: true));
        Assert.Equal("Add at least 2 steps.", InsertCountText.Shortfall(2, "step"));
        Assert.Equal("Add at least one milestone.", InsertCountText.Shortfall(1, "milestone"));

        var insert = Read("MarkSmith.Desktop/Views/InsertDialogControls.cs");
        Assert.Contains("InsertCountText.Lines(lines.Count, _noun, _headerFirst)", insert);
        Assert.Contains("Datagrid, \"row\", minimum: 2, headerFirst: true", Read(Main));
    }
}
