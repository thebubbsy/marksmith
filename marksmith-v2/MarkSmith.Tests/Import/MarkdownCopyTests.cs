using System;
using System.IO;
using MarkSmith.Services.Import;
using Xunit;

namespace MarkSmith.Tests.Import;

/// <summary>
/// Run #23: Ctrl+S on a converted document. Before this, it wrote the editor's Markdown straight
/// over the opened .docx / .pdf / .html — destroying the original.
/// </summary>
public class MarkdownCopyTests
{
    // Under the test's own folder, not %TEMP% — the temp folder counts as transient on purpose.
    private static string Dir() =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "mdcopy_" + Guid.NewGuid().ToString("N")[..8])).FullName;

    [Fact]
    public void Saves_Beside_The_Original_And_Never_Touches_It()
    {
        var dir = Dir();
        var docx = Path.Combine(dir, "Report.docx");
        File.WriteAllBytes(docx, new byte[] { 0x50, 0x4B, 3, 4 });

        var target = MarkdownCopy.Save(docx, "# Report\n", Path.Combine(dir, "fallback"));

        Assert.Equal(Path.Combine(dir, "Report.md"), target);
        Assert.Equal("# Report\n", File.ReadAllText(target));
        Assert.Equal(new byte[] { 0x50, 0x4B, 3, 4 }, File.ReadAllBytes(docx));
    }

    [Fact]
    public void Never_Overwrites_An_Existing_Markdown_File()
    {
        var dir = Dir();
        var eml = Path.Combine(dir, "Mail.eml");
        File.WriteAllText(eml, "x");
        File.WriteAllText(Path.Combine(dir, "Mail.md"), "mine");

        var target = MarkdownCopy.Save(eml, "new", Path.Combine(dir, "fallback"));

        Assert.Equal(Path.Combine(dir, "Mail (2).md"), target);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(dir, "Mail.md")));
    }

    [Fact]
    public void A_Temporary_Original_Saves_To_The_Fallback_Folder_With_Its_Media()
    {
        var temp = Directory.CreateTempSubdirectory("ms_mdcopy_").FullName;
        var eml = Path.Combine(temp, "Att.eml");
        File.WriteAllText(eml, "x");
        Directory.CreateDirectory(Path.Combine(temp, "Att_media"));
        File.WriteAllText(Path.Combine(temp, "Att_media", "a.png"), "png");
        var fallback = Path.Combine(Dir(), "out");

        var target = MarkdownCopy.Save(eml, "![a](Att_media/a.png)", fallback);

        Assert.Equal(Path.Combine(fallback, "Att.md"), target);
        Assert.True(File.Exists(Path.Combine(fallback, "Att_media", "a.png")));
    }
}
