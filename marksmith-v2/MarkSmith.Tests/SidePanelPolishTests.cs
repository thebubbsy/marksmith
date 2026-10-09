using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Email;
using MarkSmith.Services.Mermaid;
using MarkSmith.Services.Presentation;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Run #54: the side panel (Style &amp; Export, Automation), done properly. Every option was traced
/// to the code that reads it; these pin what that found.
/// </summary>
[Collection("LicenseState")]
public sealed class SidePanelPolishTests
{
    private const string Flowchart = "# Doc\n\n```mermaid\nflowchart LR\n  A --> B\n```\n";

    // ---- Arrowhead style ----

    [Fact]
    public void Default_keeps_every_head_as_drawn()
    {
        foreach (var head in Enum.GetValues<ArrowHead>())
            Assert.Equal(head, ArrowheadStyle.Apply(head, "default"));
    }

    [Fact]
    public void A_style_restyles_heads_but_never_adds_one_to_a_plain_line()
    {
        // The old fallback filled in missing heads: Triangle made every --> double-headed.
        Assert.Equal(ArrowHead.Stealth, ArrowheadStyle.Apply(ArrowHead.Triangle, "stealth"));
        Assert.Equal(ArrowHead.Triangle, ArrowheadStyle.Apply(ArrowHead.Open, "triangle"));
        Assert.Equal(ArrowHead.None, ArrowheadStyle.Apply(ArrowHead.None, "triangle"));
    }

    [Fact]
    public void Class_and_ER_markers_keep_their_meaning()
    {
        Assert.Equal(ArrowHead.Diamond, ArrowheadStyle.Apply(ArrowHead.Diamond, "triangle"));
        Assert.Equal(ArrowHead.Oval, ArrowheadStyle.Apply(ArrowHead.Oval, "none"));
    }

    [Fact]
    public void None_removes_arrowheads_and_unknown_values_change_nothing()
    {
        Assert.Equal(ArrowHead.None, ArrowheadStyle.Apply(ArrowHead.Triangle, "none"));
        Assert.Equal(ArrowHead.Triangle, ArrowheadStyle.Apply(ArrowHead.Triangle, "zigzag"));
        Assert.Equal(ArrowHead.Triangle, ArrowheadStyle.Apply(ArrowHead.Triangle, null));
    }

    [Fact]
    public void A_whole_diagram_is_restyled_in_place()
    {
        var d = new MDiagram();
        d.Connectors.Add(new MConnector { StartHead = ArrowHead.None, EndHead = ArrowHead.Triangle });
        d.Connectors.Add(new MConnector { StartHead = ArrowHead.Diamond, EndHead = ArrowHead.None });
        ArrowheadStyle.Apply(d, "open");
        Assert.Equal((ArrowHead.None, ArrowHead.Open), (d.Connectors[0].StartHead, d.Connectors[0].EndHead));
        Assert.Equal((ArrowHead.Diamond, ArrowHead.None), (d.Connectors[1].StartHead, d.Connectors[1].EndHead));
    }

    [Fact]
    public void The_fallback_renderer_draws_one_head_on_an_ordinary_arrow()
    {
        var settings = new AppSettings { ConnectorArrowhead = "triangle" };
        Assert.True(MermaidDocxRenderer.TryRender("flowchart LR\n  A --> B\n", AppServices.Themes.GetOrDefault("GitHub Light"),
            settings, 1, out var paragraph, out _));
        var xml = paragraph.OuterXml;
        Assert.Contains("<a:tailEnd type=\"triangle\"", xml);
        Assert.DoesNotContain("<a:headEnd type=\"triangle\"", xml);
    }

    [Fact]
    public void Every_side_panel_arrowhead_value_is_one_the_style_knows()
    {
        var tags = Regex.Matches(SidePanelXaml(), "<ComboBoxItem Content=\"[^\"]+\" Tag=\"([a-z]+)\" />")
            .Select(m => m.Groups[1].Value)
            .Where(ArrowheadStyle.Values.Contains)
            .ToList();
        Assert.Equal(ArrowheadStyle.Values, tags);
    }

    // ---- "Render Mermaid diagrams" off means off in every export ----

    private static AppSettings DiagramsOff() => new() { MermaidEnabled = false };

    [Fact]
    public void Html_keeps_the_fence_as_code_when_diagrams_are_off()
    {
        // It used to become a <div class="mermaid"> that no script drew: bare, unstyled text.
        var html = new MarkdownHtmlService().Render(Flowchart, DiagramsOff(), AppServices.Themes.GetOrDefault("GitHub Light"));
        Assert.DoesNotContain("class=\"mermaid\"", html);
        Assert.Contains("language-mermaid", html);
        Assert.Contains("A --&gt; B", html);
    }

    [Fact]
    public void Diagram_source_shown_as_code_is_escaped()
    {
        const string ClassDiagram = "```mermaid\nclassDiagram\n  Animal <|-- Duck\n```\n";
        var html = new MarkdownHtmlService().Render(ClassDiagram, DiagramsOff(), AppServices.Themes.GetOrDefault("GitHub Light"));
        Assert.Contains("Animal &lt;|-- Duck", html);
    }

    [Fact]
    public void Html_still_draws_diagrams_when_on()
    {
        var html = new MarkdownHtmlService().Render(Flowchart, new AppSettings(), AppServices.Themes.GetOrDefault("GitHub Light"));
        Assert.Contains("<div class=\"mermaid\">", html);
    }

    [Fact]
    public void Slides_show_the_fence_as_plain_code_when_diagrams_are_off()
    {
        static string? Caption(bool draw) => SlideDeckBuilder.Build(Flowchart, new SlideDeckOptions { DrawDiagrams = draw })
            .Slides.SelectMany(s => s.Blocks).OfType<CodeSlideBlock>().Single().Caption;
        Assert.Contains("diagram source", Caption(true));
        Assert.Null(Caption(false));
    }

    [Fact]
    public void Email_writes_code_and_no_diagram_placeholder_when_off()
    {
        var on = new EmailHtmlRenderer(new AppSettings(), AppServices.Themes.GetOrDefault("GitHub Light")).Render(Flowchart);
        var off = new EmailHtmlRenderer(DiagramsOff(), AppServices.Themes.GetOrDefault("GitHub Light")).Render(Flowchart);
        Assert.Contains("[Diagram]", on.Text);
        Assert.DoesNotContain("[Diagram]", off.Text);
        Assert.Contains("A --> B", off.Text);
    }

    [Fact]
    public void Google_docs_asks_for_no_diagram_picture_when_off()
    {
        Assert.Contains(GoogleDocsDocumentBuilder.Build(Flowchart, new AppSettings()).Images, i => i.Source.StartsWith("mermaid:"));
        Assert.DoesNotContain(GoogleDocsDocumentBuilder.Build(Flowchart, DiagramsOff()).Images, i => i.Source.StartsWith("mermaid:"));
    }

    [Fact]
    public async System.Threading.Tasks.Task No_diagram_is_rendered_for_an_export_when_off()
    {
        // Returns before touching the web host, so exports with diagrams off don't wait for Mermaid.
        var harvest = new MermaidHarvestService();
        Assert.Empty(await harvest.RenderMermaidPngsAsync(null!, Flowchart, DiagramsOff(), AppServices.Themes.GetOrDefault("GitHub Light")));
        Assert.Empty(await harvest.HarvestGenericGeometryAsync(null!, Flowchart, DiagramsOff(), AppServices.Themes.GetOrDefault("GitHub Light")));
    }

    [Fact]
    public async System.Threading.Tasks.Task Word_keeps_the_fence_as_code_when_off()
    {
        var dir = Directory.CreateTempSubdirectory("ms_sidepanel_").FullName;
        try
        {
            var path = Path.Combine(dir, "d.docx");
            await new DocxExportService().ExportAsync(Flowchart, path, DiagramsOff());
            using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
            var body = doc.MainDocumentPart!.Document.Body!;
            Assert.Contains("A --> B", body.InnerText);
            Assert.Empty(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Drawing>());
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- Presets ----

    [Fact]
    public void A_preset_captures_everything_that_changes_the_look()
    {
        var s = new AppSettings
        {
            ThemeLightInfluence = true, MermaidEnabled = false, NormalizeLlm = false, PageBorder = true,
            SmartConnectors = false, ConnectorArrowhead = "open", CustomFontPath = @"C:\f.ttf", AuthorName = "Ada",
        };
        var p = ExportPreset.Capture("x", s);
        Assert.Equal((true, false, false, true, false), (p.ThemeLightInfluence, p.MermaidEnabled, p.NormalizeLlm, p.PageBorder, p.SmartConnectors));
        Assert.Equal(("open", @"C:\f.ttf", "Ada"), (p.ConnectorArrowhead, p.CustomFontPath, p.AuthorName));
        Assert.True(p.Matches(s));
    }

    [Fact]
    public void A_preset_stops_matching_when_a_setting_it_covers_changes()
    {
        var s = new AppSettings();
        var p = ExportPreset.Capture("x", s);
        s.NoEmoji = !s.NoEmoji;
        Assert.False(p.Matches(s));
        s.NoEmoji = !s.NoEmoji;
        s.EmailTo = "someone@example.com"; // not part of a preset
        Assert.True(p.Matches(s));
    }

    [Fact]
    public void An_old_preset_ignores_fields_it_never_saved()
    {
        var old = JsonSerializer.Deserialize<ExportPreset>("""{"Name":"Old","Theme":"GitHub Light","ContentWidth":820,"MermaidDocxMode":1,"OversizedDiagramMode":0}""")!;
        Assert.Null(old.PageBorder);
        var s = new AppSettings { Theme = "GitHub Light", ContentWidth = 820, MermaidDocxMode = 1, PageBorder = true, AuthorName = "Ada" };
        s.IncludeToc = old.IncludeToc; s.NoEmoji = old.NoEmoji; s.ShowAttribution = old.ShowAttribution;
        s.A4FixedWidth = old.A4FixedWidth; s.UnlimitedHeight = old.UnlimitedHeight;
        s.DashMode = old.DashMode; s.HeadingShift = old.HeadingShift; s.BoldMode = old.BoldMode; s.ItalicMode = old.ItalicMode;
        s.BrandCoverPage = old.BrandCoverPage; s.BrandLogoPath = old.BrandLogoPath; s.BrandFontFamily = old.BrandFontFamily;
        Assert.True(old.Matches(s));
    }

    [Fact]
    public void The_summary_is_not_saved_with_the_preset() =>
        Assert.DoesNotContain("Summary", JsonSerializer.Serialize(ExportPreset.Capture("x", new AppSettings())));

    [Fact]
    public void Applying_saving_and_changing_track_the_preset_in_use()
    {
        var vm = new MainViewModel();
        vm.NoEmoji = false;
        vm.SavePreset("Plain");
        Assert.Equal("Plain", vm.ActivePreset?.Name);
        Assert.True(vm.HasActivePreset);

        vm.NoEmoji = true; // drifts from the preset
        Assert.Null(vm.ActivePreset);
        vm.NoEmoji = false; // and back
        Assert.Equal("Plain", vm.ActivePreset?.Name);
        vm.NoEmoji = true;

        vm.ActivePreset = vm.FindPreset("plain"); // picking it applies it
        Assert.False(vm.NoEmoji);
        Assert.Equal("Plain", vm.ActivePreset?.Name);
        Assert.Contains("Applied preset", vm.StatusText);

        var preset = vm.ActivePreset!;
        vm.DeletePreset(preset);
        Assert.Null(vm.ActivePreset);
        Assert.False(vm.NoEmoji); // deleting forgets the name, not the look
        Assert.Null(vm.FindPreset("Plain"));
    }

    [Fact]
    public void Applying_a_preset_no_longer_writes_the_dead_oversized_mode()
    {
        var vm = new MainViewModel();
        AppServices.Settings.Current.OversizedDiagramMode = 4;
        vm.ApplyPreset(new ExportPreset { Name = "Old", Theme = vm.SelectedThemeName });
        Assert.Equal(4, AppServices.Settings.Current.OversizedDiagramMode);
    }

    // ---- Rows that depend on another option ----

    [Fact]
    public void Word_connector_rows_need_editable_shapes()
    {
        var vm = new MainViewModel { MermaidEnabled = true, MermaidDocxMode = 1 };
        Assert.True(vm.WordDiagramsAreShapes);
        vm.MermaidDocxMode = 0;
        Assert.False(vm.WordDiagramsAreShapes);
        vm.MermaidDocxMode = 1;
        vm.MermaidEnabled = false;
        Assert.False(vm.WordDiagramsAreShapes);
        vm.MermaidEnabled = true;
    }

    [Fact]
    public void The_running_document_says_it_is_only_for_word()
    {
        AppServices.License.ResetToFree();
        var vm = new MainViewModel { TargetFormat = "pdf" };
        Assert.False(vm.RunningDocApplies);
        Assert.Contains("Only for Word", vm.RunningDocDescription);
        Assert.Contains("PDF", vm.RunningDocDescription);
        vm.TargetFormat = "docx";
        Assert.True(vm.RunningDocApplies);
    }

    [Fact]
    public void Turning_on_the_running_document_without_a_file_warns()
    {
        var vm = new MainViewModel { RunningDocPath = "" };
        vm.AppendToRunningDoc = true;
        Assert.True(vm.RunningDocNeedsPath);
        vm.RunningDocPath = @"C:\Docs\notebook.docx";
        Assert.False(vm.RunningDocNeedsPath);
        vm.AppendToRunningDoc = false;
        vm.RunningDocPath = "";
    }

    [Theory]
    [InlineData("Glued connectors", "WordDiagramsAreShapes")]
    [InlineData("Arrowhead style", "WordDiagramsAreShapes")]
    [InlineData("Diagrams", "MermaidEnabled")]
    [InlineData("Append to a running document", "RunningDocApplies")]
    [InlineData("Export watched files", "WatchFolderEnabled")]
    [InlineData("Page width", "A4FixedWidth")]
    public void Dependent_rows_bind_their_parent(string header, string parent)
    {
        var row = Regex.Match(SidePanelXaml(), $"<localcontrols:OptionRow Header=\"{Regex.Escape(header)}\"[^>]*>", RegexOptions.Singleline);
        Assert.True(row.Success, header);
        Assert.Contains($"IsEnabled=\"{{Binding {parent}", row.Value);
    }

    [Fact]
    public void Option_rows_have_hover_and_disabled_states()
    {
        var xaml = File.ReadAllText(Path.Combine(DesktopDir(), "Controls", "OptionRow.xaml"));
        Assert.Contains("x:Name=\"PointerOver\"", xaml);
        Assert.Contains("x:Name=\"Disabled\"", xaml);
        Assert.Contains("TextFillColorDisabledBrush", xaml);
    }

    [Fact]
    public void Every_option_row_says_what_it_does()
    {
        // Cc sits under To, whose description covers both.
        var bare = Regex.Matches(SidePanelXaml(), "<localcontrols:OptionRow Header=\"([^\"]+)\"\\s*>")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "Cc" }, bare);
    }

    private static string SidePanelXaml() => File.ReadAllText(Path.Combine(DesktopDir(), "MainWindow.xaml"));

    private static string DesktopDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "MarkSmith.Desktop");
}
