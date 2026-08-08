using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Persistent undo/redo for the markdown editor: burst coalescing, per-document isolation, and the
// JSON round-trip that keeps Ctrl+Z working after the app is closed and re-opened.
public class EditorUndoHistoryTests
{
    private static EditorUndoHistory NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "ms-undo-" + Guid.NewGuid().ToString("N") + ".json");
        return new EditorUndoHistory(path);
    }

    private static void RecordBurst(EditorUndoHistory h, string from, string to)
    {
        h.RecordChange(from, 0);
        h.RecordChange(to, to.Length); // two rapid changes -> one coalesced step
    }

    [Fact]
    public void ContinuousTyping_CoalescesIntoOneUndoStep()
    {
        var h = NewStore();
        h.RecordChange("", 0);
        h.RecordChange("h", 1);
        h.RecordChange("he", 2);
        h.RecordChange("hel", 3);
        h.RecordChange("hell", 4);

        Assert.True(h.CanUndo);
        var snap = h.Undo()!;
        Assert.Equal("", snap.Text); // the whole burst is one step back to the pre-burst state
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void Pause_BetweenBursts_CreatesSeparateSteps()
    {
        var h = NewStore();
        h.RecordChange("", 0);
        h.RecordChange("hello", 5);

        h.BreakBurst(); // simulates a pause: forces a fresh step
        h.RecordChange("hello world", 11);

        var snap1 = h.Undo()!;
        Assert.Equal("hello", snap1.Text);
        var snap2 = h.Undo()!;
        Assert.Equal("", snap2.Text);
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void UndoRedo_RoundTripsTextAndCaret()
    {
        var h = NewStore();
        h.RecordChange("alpha", 0);
        h.BreakBurst();
        h.RecordChange("alphabet", 8);

        var snap = h.Undo()!;
        Assert.Equal("alpha", snap.Text);

        var redo = h.Redo()!;
        Assert.Equal("alphabet", redo.Text);
        Assert.Equal(8, redo.Caret);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void NewEdit_AfterUndo_ClearsRedo()
    {
        var h = NewStore();
        h.RecordChange("a", 0);
        h.BreakBurst();
        h.RecordChange("ab", 2);

        h.Undo();
        Assert.True(h.CanRedo);

        h.RecordChange("ax", 2); // typing after an undo invalidates redo
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Undo_DoesNotRecordPhantomStep_FromBindingRoundTrip()
    {
        var h = NewStore();
        h.RecordChange("one", 0);
        h.BreakBurst();
        h.RecordChange("one two", 7);

        var snap = h.Undo()!;
        Assert.Equal("one", snap.Text);
        h.RecordChange(snap.Text, snap.Caret); // what the binding pushes back after the UI applies it

        Assert.True(h.CanRedo); // the round-trip must not clobber the redo stack
        Assert.Equal("", h.Undo()!.Text); // only the ORIGINAL pre-burst step remains — no phantom step
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void Seed_OnFileOpen_DoesNotCreateAnUndoStep()
    {
        var h = NewStore();
        h.Seed(@"C:\docs\a.md", "file content");
        h.RecordChange("file content", 12); // the binding fires after the seed

        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Documents_KeepIndependentStacks()
    {
        var h = NewStore();
        h.Seed(@"C:\docs\a.md", "A");
        h.BreakBurst();
        h.RecordChange("A1", 2);

        h.SetDocument(@"C:\docs\b.md");
        h.Seed(@"C:\docs\b.md", "B");
        h.BreakBurst();
        h.RecordChange("B1", 2);

        // B's stack is isolated from A's edits.
        Assert.Equal("B", h.Undo()!.Text);
        Assert.False(h.CanUndo);

        // Switching back to A restores A's undo history.
        h.SetDocument(@"C:\docs\a.md");
        Assert.Equal("A", h.Undo()!.Text);
    }

    [Fact]
    public void History_SurvivesCloseAndReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "ms-undo-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var h1 = new EditorUndoHistory(path);
            h1.Seed(@"C:\docs\persist.md", "first draft");
            h1.BreakBurst();
            h1.RecordChange("first draft edited", 18);
            h1.Flush(); // app close

            var h2 = new EditorUndoHistory(path); // app re-open
            h2.SetDocument(@"C:\docs\persist.md");
            h2.RecordChange("first draft edited", 18); // binding re-fires with the restored text

            Assert.True(h2.CanUndo);
            var snap = h2.Undo()!;
            Assert.Equal("first draft", snap.Text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Keystrokes_BeforeFileContentLands_DoNotPolluteTheNewFilesStack()
    {
        var h = NewStore();
        h.Seed(@"C:\docs\a.md", "old doc");

        // The user keeps typing while the new file's content is being read: these keystrokes must
        // land in the OLD document's stack, not the new file's.
        h.RecordChange("old doc more", 12);
        h.BreakBurst();
        h.RecordChange("old doc more!", 13);

        // The new file's content lands — Seed switches the active document.
        h.Seed(@"C:\docs\b.md", "new file content");
        h.RecordChange("new file content", 16); // binding re-fires with the seeded text

        Assert.False(h.CanUndo); // new file's stack is clean
        Assert.False(h.CanRedo);

        // ...and the old document kept its typing steps.
        h.SetDocument(@"C:\docs\a.md");
        Assert.Equal("old doc more", h.Undo()!.Text);
        Assert.Equal("old doc", h.Undo()!.Text);
    }

    [Fact]
    public void Stack_IsCappedAtMaxSteps()
    {
        var h = NewStore();
        for (int i = 0; i < 300; i++)
        {
            h.BreakBurst();
            h.RecordChange("v" + i, ("v" + i).Length);
        }

        int undos = 0;
        while (h.CanUndo) { h.Undo(); undos++; }
        Assert.InRange(undos, 1, 120);
    }
}
