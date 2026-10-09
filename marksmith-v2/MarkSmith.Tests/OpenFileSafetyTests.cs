using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Ctrl+S writes the editor to <see cref="MainViewModel.InputFilePath"/>, which nothing ever
/// cleared: open a file, send a chat in from the browser extension, press Ctrl+S, and the file
/// was replaced by the chat. <see cref="MainViewModel.IsEditingOpenFile"/> now says whether the
/// editor's text is the open file's, and the disk stamp says whether someone else changed it.
/// </summary>
public sealed class OpenFileSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ms-openfile-" + Guid.NewGuid().ToString("N"));

    public OpenFileSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<MainViewModel> OpenAsync(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, text);
        var vm = new MainViewModel { UsePasteSource = false, InputFilePath = path };
        for (int i = 0; i < 100 && !vm.IsEditingOpenFile; i++) await Task.Delay(50);
        return vm;
    }

    [Fact]
    public async Task Opening_and_editing_a_file_keeps_it_the_open_document()
    {
        var vm = await OpenAsync("notes.md", "# Notes\n");
        Assert.True(vm.IsEditingOpenFile);
        Assert.Equal(_dir, vm.DocumentFolder);

        vm.CurrentMarkdown = "# Notes\n\nTyped more.\n";   // editing flips to the paste source
        Assert.True(vm.UsePasteSource);
        Assert.True(vm.IsEditingOpenFile);
        Assert.Equal(_dir, vm.DocumentFolder);             // relative images still resolve
    }

    [Fact]
    public async Task Ingested_text_is_not_the_open_file_and_has_no_image_folder()
    {
        var vm = await OpenAsync("report.md", "# Report\n");
        vm.IngestMarkdown("# A chat from the browser\n", "the browser extension");

        Assert.False(vm.IsEditingOpenFile);
        Assert.Null(vm.DocumentFolder);
        Assert.Equal(Path.Combine(_dir, "report.md"), vm.InputFilePath); // still remembered, never written
    }

    [Fact]
    public async Task Choosing_the_file_again_reattaches_it()
    {
        var vm = await OpenAsync("report.md", "# Report\n");
        vm.IngestMarkdown("# Pasted\n", "clipboard");
        vm.UsePasteSource = false;
        Assert.True(vm.IsEditingOpenFile);
    }

    [Fact]
    public async Task A_change_by_another_program_is_noticed_until_MarkSmith_saves()
    {
        var vm = await OpenAsync("shared.md", "# Shared\n");
        Assert.False(vm.OpenFileChangedOnDisk());

        await File.WriteAllTextAsync(vm.InputFilePath, "# Shared\n\nEdited in another editor, now longer.\n");
        File.SetLastWriteTimeUtc(vm.InputFilePath, DateTime.UtcNow.AddMinutes(1));
        Assert.True(vm.OpenFileChangedOnDisk());

        await File.WriteAllTextAsync(vm.InputFilePath, "# Shared, saved by MarkSmith\n");
        vm.MarkOpenFileSaved();
        Assert.False(vm.OpenFileChangedOnDisk());
    }
}
