using Xunit;
using MarkSmith.Services;

namespace MarkSmith.Tests;

// The local version-history database: lazily created on first capture, deduped by content hash,
// pruned per file, newest-first. Uses a temp directory so tests never touch the real store.
public class VersionHistoryServiceTests : IDisposable
{
    private readonly string _dir;

    public VersionHistoryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MarkSmith_hist_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private VersionHistoryService NewService(int maxVersions = 100) =>
        new(Path.Combine(_dir, "history"), maxVersions);

    [Fact]
    public async Task Database_IsCreatedLazily_OnFirstCapture()
    {
        var svc = NewService();
        Assert.False(svc.Exists); // nothing on disk until the user works

        await svc.CaptureAsync(@"C:\docs\notes.md", "# Hello");

        Assert.True(svc.Exists);
        Assert.True(File.Exists(svc.DatabasePath));
    }

    [Fact]
    public async Task UnchangedContent_DoesNotCreateAVersion()
    {
        var svc = NewService();
        Assert.True(await svc.CaptureAsync(@"C:\docs\a.md", "same"));
        Assert.False(await svc.CaptureAsync(@"C:\docs\a.md", "same"));

        var versions = await svc.GetVersionsAsync(@"C:\docs\a.md");
        Assert.Single(versions);
    }

    [Fact]
    public async Task Changes_AreAppendedNewestFirst()
    {
        var svc = NewService();
        await svc.CaptureAsync(@"C:\docs\a.md", "v1", "opened");
        await Task.Delay(10);
        await svc.CaptureAsync(@"C:\docs\a.md", "v2", "export:pdf");
        await Task.Delay(10);
        await svc.CaptureAsync(@"C:\docs\a.md", "v3", "save");

        var versions = await svc.GetVersionsAsync(@"C:\docs\a.md");
        Assert.Equal(3, versions.Count);
        Assert.Equal("v3", await svc.GetContentAsync(versions[0].Id));
        Assert.Equal("v2", await svc.GetContentAsync(versions[1].Id));
        Assert.Equal("v1", await svc.GetContentAsync(versions[2].Id));
        Assert.Equal("save", versions[0].Source);
    }

    [Fact]
    public async Task ContentBlobs_AreDedupedAcrossFiles()
    {
        var svc = NewService();
        await svc.CaptureAsync(@"C:\docs\a.md", "identical body");
        await svc.CaptureAsync(@"C:\docs\b.md", "identical body");

        var blobCount = Directory.GetFiles(Path.Combine(_dir, "history", "blobs")).Length;
        Assert.Equal(1, blobCount); // one blob, two index rows
    }

    [Fact]
    public async Task Prune_KeepsOnlyTheNewestVersionsPerFile()
    {
        var svc = NewService(maxVersions: 3);
        for (int i = 0; i < 6; i++) await svc.CaptureAsync(@"C:\docs\a.md", "content #" + i);

        var versions = await svc.GetVersionsAsync(@"C:\docs\a.md");
        Assert.Equal(3, versions.Count);
        Assert.Equal("content #5", await svc.GetContentAsync(versions[0].Id)); // newest kept
        Assert.Equal("content #3", await svc.GetContentAsync(versions[2].Id)); // oldest of the kept
    }

    [Fact]
    public async Task GetContent_ForUnknownId_ReturnsNull()
    {
        var svc = NewService();
        await svc.CaptureAsync(@"C:\docs\a.md", "hello");
        Assert.Null(await svc.GetContentAsync("v_9999999999999_deadbeef"));
    }

    [Fact]
    public async Task Purge_RemovesAllVersionsForOneFile_LeavesOthers()
    {
        var svc = NewService();
        await svc.CaptureAsync(@"C:\docs\a.md", "a1");
        await svc.CaptureAsync(@"C:\docs\b.md", "b1");

        Assert.Equal(1, await svc.PurgeAsync(@"C:\docs\a.md"));

        Assert.Empty(await svc.GetVersionsAsync(@"C:\docs\a.md"));
        Assert.Single(await svc.GetVersionsAsync(@"C:\docs\b.md"));
    }

    [Fact]
    public async Task Paths_AreNormalized_CaseInsensitive()
    {
        var svc = NewService();
        await svc.CaptureAsync(@"C:\Docs\Notes.md", "x");
        var versions = await svc.GetVersionsAsync(@"c:\docs\notes.MD");
        Assert.Single(versions);
    }

    [Fact]
    public async Task Prune_GarbageCollectsOrphanedBlobs()
    {
        var svc = NewService(maxVersions: 2);
        for (int i = 0; i < 4; i++) await svc.CaptureAsync(@"C:\docs.md", "content #" + i);

        var blobs = Directory.GetFiles(Path.Combine(_dir, "history", "blobs"));
        Assert.Equal(2, blobs.Length); // only the two kept versions' blobs survive
    }

    [Fact]
    public async Task CorruptIndex_IsBackedUp_AndCaptureAbortsInsteadOfWiping()
    {
        var svc = NewService();
        await svc.CaptureAsync(@"C:\docs.md", "real history");
        File.WriteAllText(svc.DatabasePath, "{{{{ not json");

        await Assert.ThrowsAsync<System.IO.IOException>(() => svc.CaptureAsync(@"C:\docs.md", "new"));

        // The corrupt file was preserved (renamed aside, byte-for-byte) instead of being silently
        // wiped — and NO fresh index was created in its place, so the history is recoverable.
        var backups = Directory.GetFiles(Path.GetDirectoryName(svc.DatabasePath)!, "*.corrupt-*");
        Assert.NotEmpty(backups);
        Assert.Equal("{{{{ not json", await File.ReadAllTextAsync(backups[0]));
        Assert.False(File.Exists(svc.DatabasePath));
    }

    [Fact]
    public async Task BlankPath_IsIgnored()
    {
        var svc = NewService();
        Assert.False(await svc.CaptureAsync("", "content"));
        Assert.False(await svc.CaptureAsync("   ", "content"));
        Assert.False(svc.Exists);
    }

    [Fact]
    public async Task StarAndLabel_CanBeSetAndToggled()
    {
        var svc = NewService();
        await svc.CaptureAsync(@"C:\docs\a.md", "version 1", "manual", "Initial Draft", isStarred: true);

        var versions = await svc.GetVersionsAsync(@"C:\docs\a.md");
        Assert.Single(versions);
        Assert.True(versions[0].IsStarred);
        Assert.Equal("Initial Draft", versions[0].Label);

        // Toggle star off
        Assert.False(await svc.ToggleStarAsync(versions[0].Id));
        versions = await svc.GetVersionsAsync(@"C:\docs\a.md");
        Assert.False(versions[0].IsStarred);

        // Rename label
        Assert.True(await svc.SetLabelAsync(versions[0].Id, "Renamed Milestone"));
        versions = await svc.GetVersionsAsync(@"C:\docs\a.md");
        Assert.Equal("Renamed Milestone", versions[0].Label);
    }

    [Fact]
    public async Task DeleteVersion_RemovesSpecificSnapshot()
    {
        var svc = NewService();
        await svc.CaptureAsync(@"C:\docs\a.md", "v1");
        await Task.Delay(10);
        await svc.CaptureAsync(@"C:\docs\a.md", "v2");

        var versions = await svc.GetVersionsAsync(@"C:\docs\a.md");
        Assert.Equal(2, versions.Count);

        var toDeleteId = versions[0].Id;
        Assert.True(await svc.DeleteVersionAsync(toDeleteId));

        var remaining = await svc.GetVersionsAsync(@"C:\docs\a.md");
        Assert.Single(remaining);
        Assert.NotEqual(toDeleteId, remaining[0].Id);
    }

    [Fact]
    public async Task DeltaLines_AreCalculatedAutomatically()
    {
        var svc = NewService();
        await svc.CaptureAsync(@"C:\docs\a.md", "line 1\nline 2\nline 3");
        await Task.Delay(10);
        await svc.CaptureAsync(@"C:\docs\a.md", "line 1\nline 2 modified\nline 3\nline 4 added");

        var versions = await svc.GetVersionsAsync(@"C:\docs\a.md");
        Assert.Equal(2, versions.Count);
        Assert.True(versions[0].LinesAdded >= 1);
    }
}
