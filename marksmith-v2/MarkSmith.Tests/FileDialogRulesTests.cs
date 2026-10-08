using System.Runtime.CompilerServices;
using MarkSmith.Models;
using Xunit;

namespace MarkSmith.Tests;

// The native file dialogs (Desktop Services/NativeFilePicker) replaced Windows.Storage.Pickers,
// which crash with 0x800706BE when the app runs elevated. The replacement first shipped without a
// default extension: "Export table" saved a typed "report" as an extensionless file. These rules
// are the parts of those dialogs that don't need Windows.
public class FileDialogRulesTests
{
    private static readonly FileType Xlsx = FileType.Of("Excel workbook", ".xlsx");
    private static readonly FileType Csv = FileType.Of("CSV", "csv");

    // Windows appends "(*.png;*.jpg)" to the label itself when extensions are showing, so a label
    // that carried its own patterns showed them twice.
    [Fact]
    public void Labels_are_the_name_and_specs_carry_every_pattern()
    {
        Assert.Equal("Excel workbook", Xlsx.Label);
        Assert.Equal("*.xlsx", Xlsx.Spec);
        var images = FileType.Of("Images", "*.PNG", ".jpg", "jpeg");
        Assert.Equal("Images", images.Label);
        Assert.Equal("*.png;*.jpg;*.jpeg", images.Spec);
        Assert.Equal("All files", FileType.AllFiles.Label);
        Assert.Equal("*.*", FileType.AllFiles.Spec);
    }

    // Save dialogs never filter the folder (a filtered save view froze on a real PC) but the first
    // pattern still sets the extension the dialog appends.
    [Fact]
    public void Save_specs_list_everything_but_lead_with_the_format()
    {
        Assert.Equal("*.xlsx;*.*", Xlsx.SaveSpec);
        Assert.Equal("*.jpg;*.jpeg;*.*", FileType.Of("JPEG image", ".jpg", ".jpeg").SaveSpec);
        Assert.Equal("*.*", FileType.AllFiles.SaveSpec);
    }

    [Fact]
    public void Extensions_are_normalised_and_deduplicated()
    {
        var t = FileType.Of("Markdown", "md", ".MD", "*.markdown", " ");
        Assert.Equal(new[] { ".md", ".markdown" }, t.Extensions);
        Assert.True(t.Matches(@"C:\notes\Read me.MD"));
        Assert.False(t.Matches("notes.txt"));
        Assert.Throws<ArgumentException>(() => FileType.Of("Nothing"));
    }

    [Fact]
    public void Default_extension_is_the_first_real_type()
    {
        Assert.Equal("xlsx", FileDialogRules.DefaultExtension(new[] { Xlsx, Csv }));
        Assert.Equal("csv", FileDialogRules.DefaultExtension(new[] { FileType.AllFiles, Csv }));
        Assert.Null(FileDialogRules.DefaultExtension(new[] { FileType.AllFiles }));
    }

    [Theory]
    [InlineData(@"C:\out\report", @"C:\out\report.xlsx")]
    [InlineData(@"C:\out\report.", @"C:\out\report.xlsx")]
    [InlineData(@"C:\out\report.csv", @"C:\out\report.csv")] // a typed extension picks the format
    [InlineData(@"C:\out\report.v2", @"C:\out\report.v2")]   // the dialog already asked about this name
    public void A_saved_file_always_has_an_extension(string typed, string expected)
        => Assert.Equal(expected, FileDialogRules.EnsureExtension(typed, new[] { Xlsx, Csv }));

    [Fact]
    public void Without_a_real_type_the_name_is_kept()
        => Assert.Equal(@"C:\out\report", FileDialogRules.EnsureExtension(@"C:\out\report", new[] { FileType.AllFiles }));

    [Fact]
    public void Each_purpose_remembers_its_own_folder()
    {
        Assert.Equal(FileDialogRules.ClientGuid("images"), FileDialogRules.ClientGuid(" Images "));
        Assert.NotEqual(FileDialogRules.ClientGuid("images"), FileDialogRules.ClientGuid("exports"));
        Assert.NotEqual(Guid.Empty, FileDialogRules.ClientGuid(""));
    }

    [Theory]
    [InlineData("Quarterly: plan / draft?", "x", "Quarterly plan draft")]
    [InlineData("  ...  ", "Document Galaxy", "Document Galaxy")]
    [InlineData(null, "diagram", "diagram")]
    [InlineData("Notes.", "x", "Notes")]
    public void Suggested_names_are_safe_file_names(string? name, string fallback, string expected)
        => Assert.Equal(expected, FileDialogRules.SafeFileName(name, fallback));

    // Windows.Storage.Pickers goes through pickerhost.exe and crashes elevated; every dialog in the
    // desktop app must go through NativeFilePicker instead.
    private static string DesktopDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "MarkSmith.Desktop");

    [Fact]
    public void The_desktop_app_never_uses_the_broker_pickers()
    {
        var desktop = DesktopDir();
        var offenders = Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && Path.GetFileName(f) != "NativeFilePicker.cs")
            .Where(f =>
            {
                var src = File.ReadAllText(f);
                return src.Contains("Windows.Storage.Pickers") || src.Contains("FileOpenPicker")
                    || src.Contains("FileSavePicker") || src.Contains("FolderPicker");
            })
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }
}
