using MarkSmith.WebApp.Server.Documents;
using MarkSmith.WebApp.Server.Ot;
using MarkSmith.WebApp.Server.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarkSmith.WebApp.Tests;

/// <summary>
/// Session lifecycle: start (blank / upload / resume), batch atomicity, eviction, persistence
/// across restart. Uses a temp snapshot root so tests never touch real session state.
/// </summary>
public class SessionManagerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "marksmith-tests", Guid.NewGuid().ToString("N"));
    private SessionStore _store = null!;
    private SessionManager _manager = null!;

    public async Task InitializeAsync()
    {
        _store = new SessionStore(_root, NullLogger<SessionStore>.Instance);
        _manager = new SessionManager(_store, NullLogger<SessionManager>.Instance);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Operation Insert(string id, string text) => new()
    {
        Id = id, ClientId = "u1", Type = OpType.InsertText, Block = 0, Offset = 0, Text = text,
    };

    [Fact]
    public async Task Start_WithUpload_LoadsDocument()
    {
        // Build a docx with known content via the SDK, upload it, and verify the session sees it.
        byte[] upload;
        using (var doc = DocxDocument.CreateBlank())
        {
            var applier = new OpApplier();
            applier.Apply(doc, Insert("1", "uploaded content"));
            upload = doc.SaveToBytes();
        }

        var session = await _manager.StartAsync("doc-1", "u1", upload);
        var text = await _manager.WithSessionAsync("doc-1", FirstParagraphText);
        Assert.Contains("uploaded content", text);
        Assert.Equal("doc-1", session.SessionId);
    }

    private static string FirstParagraphText(DocumentSession s)
    {
        // Save/reload is the closest we get without exposing the doc; use the session's bytes.
        using var doc = DocxDocument.Open(s.SaveToBytes());
        return DocxDocument.ParagraphText(doc.ParagraphAt(0)!);
    }

    [Fact]
    public async Task ApplyBatch_SequencesAndRenders()
    {
        await _manager.StartAsync("doc-2", "u1", null);
        var result = await _manager.WithSessionAsync("doc-2", s =>
            s.ApplyBatch("u1", baseSeq: 0, new[] { Insert("a", "hello ") }));
        Assert.True(result.Ok);
        Assert.Equal(1L, result.Entries[0].Seq);

        var html = await _manager.WithSessionAsync("doc-2", s => s.RenderedHtml());
        Assert.Contains("hello", html);

        var seq = await _manager.WithSessionAsync("doc-2", s => s.RenderedHtmlAtSeq());
        Assert.Equal(1L, seq);
    }

    [Fact]
    public async Task ApplyBatch_OutOfRange_RejectsAtomically_AndKeepsDoc()
    {
        await _manager.StartAsync("doc-3", "u1", null);
        var ok = await _manager.WithSessionAsync("doc-3", s =>
            s.ApplyBatch("u1", 0, new[] { Insert("a", "valid") }));
        Assert.True(ok.Ok);

        // Deleting 10 chars from a paragraph with only 5 is out of range.
        var bad = await _manager.WithSessionAsync("doc-3", s => s.ApplyBatch("u1", 1, new[]
        {
            new Operation { Id = "b", ClientId = "u1", Type = OpType.DeleteText, Block = 0, Offset = 0, Length = 10 },
        }));
        Assert.False(bad.Ok);

        // Document survived the rejected batch.
        var html = await _manager.WithSessionAsync("doc-3", s => s.RenderedHtml());
        Assert.Contains("valid", html);
    }

    [Fact]
    public async Task ConcurrentTransform_TwoClientsSameParagraph_Converges()
    {
        await _manager.StartAsync("doc-4", "u1", null);

        // u1 inserts at 0, u2 inserts at 0 concurrently (baseSeq 0 for both).
        var r1 = await _manager.WithSessionAsync("doc-4", s =>
            s.ApplyBatch("u1", 0, new[] { Insert("a", "A") }));
        var r2 = await _manager.WithSessionAsync("doc-4", s =>
            s.ApplyBatch("u2", 0, new[] { Insert("b", "B") }));

        Assert.True(r1.Ok);
        Assert.True(r2.Ok);

        // Both entries landed in sequence; the second insert was transformed to offset 1.
        var second = r2.Entries[0];
        Assert.Equal(1, second.Op.Offset);
        Assert.Equal(2L, second.Seq);

        var text = await _manager.WithSessionAsync("doc-4", FirstParagraphText);
        // A then B, or B then A — deterministic order: "AB" (u1's op sequenced first).
        Assert.Equal("AB", text);
    }

    [Fact]
    public async Task Session_PersistsAndResumes_AfterRestart()
    {
        await _manager.StartAsync("doc-5", "u1", null);
        await _manager.WithSessionAsync("doc-5", s =>
            s.ApplyBatch("u1", 0, new[] { Insert("a", "persisted") }));

        // Simulate server restart: dispose manager, new manager over the same store.
        await _manager.DisposeAsync();
        _manager = new SessionManager(_store, NullLogger<SessionManager>.Instance);

        var resumed = await _manager.StartAsync("doc-5", "u1", null);
        var text = await _manager.WithSessionAsync("doc-5", FirstParagraphText);
        Assert.Contains("persisted", text);
        Assert.Equal("doc-5", resumed.SessionId);

        // Seq numbering resumed above the snapshot point.
        var seq = await _manager.WithSessionAsync("doc-5", s => s.RenderedHtmlAtSeq());
        Assert.True(seq >= 1);
    }

    [Fact]
    public async Task Undo_RevertsLastOwnedOp()
    {
        await _manager.StartAsync("doc-6", "u1", null);
        await _manager.WithSessionAsync("doc-6", s =>
            s.ApplyBatch("u1", 0, new[] { Insert("a", "before ") }));
        await _manager.WithSessionAsync("doc-6", s =>
            s.ApplyBatch("u1", 1, new[] { Insert("b", "after ") }));

        var undo = await _manager.WithSessionAsync("doc-6", s => s.ApplyBatch("u1", 2, new[]
        {
            new Operation { Id = "u", ClientId = "u1", Type = OpType.Undo, UptoSeq = 2 },
        }));
        Assert.True(undo.Ok);

        var text = await _manager.WithSessionAsync("doc-6", FirstParagraphText);
        Assert.Equal("before ", text);
    }
}
