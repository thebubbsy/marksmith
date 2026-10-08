using System;
using System.Linq;
using MarkSmith.Core.Mermaid.Routing;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests.Mermaid;

/// <summary>
/// The sequence AST kept messages, notes and blocks in three separate lists, so a round trip
/// (and every Diagram Studio save) moved all notes to the top, every block after them and every
/// plain message to the bottom; nested blocks were flattened; activate/deactivate, break, rect,
/// par's "and" and critical's "option" were dropped; messages inside blocks never reached the
/// canvas; and notes, frames and activations were never drawn. These pin the ordered script, the
/// Studio's mapping back into it, and the frame/note/activation layout.
/// </summary>
public class SequenceScriptTests
{
    private const string Checkout =
        "sequenceDiagram\n" +
        "    participant U as User\n" +
        "    participant S as Shop\n" +
        "    participant P as Payments\n" +
        "    U->>+S: checkout\n" +
        "    Note right of S: validates cart\n" +
        "    loop until confirmed\n" +
        "        S->>P: charge\n" +
        "        alt approved\n" +
        "            P-->>S: ok\n" +
        "        else declined\n" +
        "            P-->>S: declined\n" +
        "        end\n" +
        "    end\n" +
        "    S-->>-U: receipt\n";

    private static int At(string code, string needle)
    {
        int i = code.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{needle}' missing from:\n{code}");
        return i;
    }

    private static void AssertInOrder(string code, params string[] needles)
    {
        for (int i = 1; i < needles.Length; i++)
            Assert.True(At(code, needles[i - 1]) < At(code, needles[i]), $"'{needles[i - 1]}' should come before '{needles[i]}' in:\n{code}");
    }

    private static string RoundTrip(string code) =>
        MermaidCodeGenerator.Generate(Assert.IsType<SequenceDiagramAst>(MermaidParser.Parse(code).Ast));

    // ---------------- Parser and generator ----------------

    [Fact]
    public void Round_trip_keeps_notes_blocks_and_messages_in_written_order()
    {
        var code = RoundTrip(Checkout);
        AssertInOrder(code, "U->>+S: checkout", "Note right of S: validates cart", "loop until confirmed",
            "S->>P: charge", "alt approved", "P-->>S: ok", "else declined", "P-->>S: declined", "end", "S-->>-U: receipt");
    }

    [Fact]
    public void Nested_blocks_stay_nested_and_indented()
    {
        var lines = RoundTrip(Checkout).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int Indent(string startsWith) => lines.First(l => l.TrimStart().StartsWith(startsWith, StringComparison.Ordinal)).TakeWhile(c => c == ' ').Count();
        Assert.True(Indent("alt approved") > Indent("loop until confirmed"));
        Assert.True(Indent("P-->>S: ok") > Indent("alt approved"));
        Assert.Equal(Indent("alt approved"), Indent("else declined"));
        Assert.Equal(2, lines.Count(l => l.Trim() == "end"));
    }

    [Fact]
    public void Round_trip_is_stable()
    {
        var once = RoundTrip(Checkout);
        Assert.Equal(once, RoundTrip(once));
    }

    [Fact]
    public void Activations_break_rect_par_and_critical_option_survive()
    {
        const string code =
            "sequenceDiagram\n" +
            "    A->>B: hi\n" +
            "    activate B\n" +
            "    rect rgb(200, 220, 255)\n" +
            "        par to C\n" +
            "            B->>C: one\n" +
            "        and to D\n" +
            "            B->>D: two\n" +
            "        end\n" +
            "    end\n" +
            "    critical connect\n" +
            "        B->>C: open\n" +
            "    option timeout\n" +
            "        B->>B: retry\n" +
            "    end\n" +
            "    break when failing\n" +
            "        B-->>A: error\n" +
            "    end\n" +
            "    deactivate B\n";
        var outCode = RoundTrip(code);
        AssertInOrder(outCode, "A->>B: hi", "activate B", "rect rgb(200, 220, 255)", "par to C", "B->>C: one", "and to D",
            "B->>D: two", "critical connect", "B->>C: open", "option timeout", "B->>B: retry", "break when failing",
            "B-->>A: error", "deactivate B");
        Assert.Equal(4, outCode.Split('\n').Count(l => l.Trim() == "end"));
    }

    [Fact]
    public void Unmodelled_lines_are_kept_in_place_not_dropped()
    {
        var code = RoundTrip("sequenceDiagram\n    A->>B: hi\n    create participant C\n    B->>C: make\n    destroy C\n    B-->>A: done\n");
        AssertInOrder(code, "A->>B: hi", "create participant C", "B->>C: make", "destroy C", "B-->>A: done");
    }

    [Fact]
    public void A_created_participant_is_declared_once_by_its_create_line()
    {
        const string code = "sequenceDiagram\n    participant A\n    A->>B: hi\n    create participant C\n    A->>C: make\n    destroy C\n    C-->>A: bye\n";
        var outCode = RoundTrip(code);
        Assert.DoesNotContain("    participant C\n", outCode.Replace("\r\n", "\n"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(outCode, @"participant C\b"));

        var vm = Load(code);
        Assert.Contains(vm.Nodes, n => n.Id == "C"); // still drawn
        var saved = vm.GenerateMermaidCode();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(saved, @"participant C\b"));

        vm.SelectNode(vm.Nodes.Single(n => n.Id == "C"));
        vm.DeleteSelected();
        saved = vm.GenerateMermaidCode();
        Assert.DoesNotContain("create participant C", saved);
        Assert.DoesNotContain("destroy C", saved);
    }

    [Fact]
    public void A_participant_box_end_does_not_close_a_block_and_par_needs_a_word_boundary()
    {
        var ast = Assert.IsType<SequenceDiagramAst>(MermaidParser.Parse(
            "sequenceDiagram\n    box Aqua Team\n    participant A\n    participant B\n    end\n    loop forever\n    A->>B: ping\n    end\n").Ast);
        Assert.Single(ast.Blocks);
        Assert.Single(ast.Blocks[0].Messages);
        Assert.Empty(ast.Messages);
    }

    [Fact]
    public void Indexes_still_describe_blocks_branches_and_top_level_messages()
    {
        var ast = Assert.IsType<SequenceDiagramAst>(MermaidParser.Parse(Checkout).Ast);
        Assert.Equal(2, ast.Messages.Count); // checkout, receipt
        Assert.Equal(2, ast.Blocks.Count);   // loop, alt (nested ones too, as before)
        var alt = ast.Blocks.Single(b => b.BlockType == SequenceBlockType.Alt);
        Assert.Single(alt.Messages);
        Assert.Single(alt.ElseBranches);
        Assert.Equal("declined", alt.ElseBranches[0].Condition);
        Assert.Single(ast.Notes);
    }

    // ---------------- Diagram Studio ----------------

    private static MermaidStudioViewModel Load(string code)
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(code);
        return vm;
    }

    [Fact]
    public void Messages_inside_blocks_are_on_the_canvas()
    {
        var vm = Load(Checkout);
        Assert.Equal(5, vm.Connectors.Count);
        Assert.Contains(vm.Connectors, c => c.Label == "declined");
    }

    [Fact]
    public void Saving_from_the_studio_keeps_the_written_order()
    {
        var code = Load(Checkout).GenerateMermaidCode();
        AssertInOrder(code, "U->>+S: checkout", "Note right of S: validates cart", "loop until confirmed",
            "S->>P: charge", "alt approved", "P-->>S: ok", "else declined", "P-->>S: declined", "S-->>-U: receipt");
    }

    [Fact]
    public void An_edited_label_inside_a_block_is_written_in_place()
    {
        var vm = Load(Checkout);
        vm.Connectors.Single(c => c.Label == "ok").Label = "approved!";
        var code = vm.GenerateMermaidCode();
        AssertInOrder(code, "alt approved", "P-->>S: approved!", "else declined");
        Assert.DoesNotContain("P-->>S: ok", code);
    }

    [Fact]
    public void Deleting_a_participant_drops_its_notes_and_messages_but_keeps_the_rest()
    {
        var vm = Load(Checkout);
        vm.SelectNode(vm.Nodes.Single(n => n.Id == "S"));
        vm.DeleteSelected();
        var code = vm.GenerateMermaidCode();
        Assert.DoesNotContain("Note right of S", code);
        Assert.DoesNotContain("checkout", code);
        Assert.Contains("loop until confirmed", code);
    }

    [Fact]
    public void A_newly_drawn_message_goes_at_the_end()
    {
        var vm = Load(Checkout);
        vm.AddConnector("U", "Right", "P", "Left");
        var code = vm.GenerateMermaidCode().TrimEnd();
        Assert.EndsWith("U->>P: ", code.Split('\n').Last().Trim() + " ");
        AssertInOrder(code, "S-->>-U: receipt", "U->>P:");
    }

    // ---------------- Layout: frames, notes, activations ----------------

    [Fact]
    public void Frames_enclose_their_messages_and_nest()
    {
        var vm = Load(Checkout);
        Assert.Equal(2, vm.SequenceFrames.Count);
        var loop = vm.SequenceFrames.Single(f => f.Keyword == "loop");
        var alt = vm.SequenceFrames.Single(f => f.Keyword == "alt");
        Assert.Equal("[until confirmed]", loop.Caption);

        foreach (var label in new[] { "charge", "ok", "declined" })
        {
            var c = vm.Connectors.Single(x => x.Label == label);
            Assert.InRange(c.SourceY, loop.Y + SequenceLayout.FrameHeader - 1, loop.Y + loop.Height);
            Assert.InRange(Math.Min(c.SourceX, c.TargetX), loop.X, loop.X + loop.Width);
            Assert.InRange(Math.Max(c.SourceX, c.TargetX), loop.X, loop.X + loop.Width);
        }
        // Nested: alt sits inside loop on every side.
        Assert.True(alt.X > loop.X && alt.Y > loop.Y);
        Assert.True(alt.X + alt.Width < loop.X + loop.Width && alt.Y + alt.Height < loop.Y + loop.Height);
        Assert.Equal(1, alt.Depth);

        // Messages before and after sit outside the loop.
        Assert.True(vm.Connectors.Single(c => c.Label == "checkout").SourceY < loop.Y);
        Assert.True(vm.Connectors.Single(c => c.Label == "receipt").SourceY > loop.Y + loop.Height);
    }

    [Fact]
    public void Else_divider_falls_between_its_branches()
    {
        var vm = Load(Checkout);
        var alt = vm.SequenceFrames.Single(f => f.Keyword == "alt");
        var divider = Assert.Single(alt.Dividers);
        Assert.Equal("[declined]", divider.Caption);
        Assert.True(vm.Connectors.Single(c => c.Label == "ok").SourceY < divider.Y);
        Assert.True(vm.Connectors.Single(c => c.Label == "declined").SourceY > divider.Y);
        Assert.Equal(alt.X, divider.X, 3);
        Assert.Equal(alt.Width, divider.Width, 3);
    }

    [Fact]
    public void Notes_get_their_own_row_beside_their_lifeline()
    {
        var vm = Load(Checkout);
        var note = Assert.Single(vm.SequenceNotes);
        var s = vm.Nodes.Single(n => n.Id == "S");
        Assert.True(note.X > s.LifelineX, "right of S starts right of S's lifeline");
        double before = vm.Connectors.Single(c => c.Label == "checkout").SourceY;
        double after = vm.Connectors.Single(c => c.Label == "charge").SourceY;
        Assert.True(note.Y > before && note.Y + note.Height < after, $"note {note.Y}..{note.Y + note.Height} between rows {before} and {after}");
    }

    [Fact]
    public void Left_of_and_over_notes_place_correctly_and_count_toward_fit()
    {
        var vm = Load("sequenceDiagram\n    participant A\n    participant B\n    Note left of A: a fairly long note on the left\n    Note over A,B: spans both\n    A->>B: hi\n");
        var a = vm.Nodes.Single(n => n.Id == "A");
        var b = vm.Nodes.Single(n => n.Id == "B");
        var left = vm.SequenceNotes[0];
        var over = vm.SequenceNotes[1];
        Assert.True(left.X + left.Width < a.LifelineX);
        Assert.True(over.X < a.LifelineX && over.X + over.Width > b.LifelineX);
        Assert.True(over.Y > left.Y + left.Height - 1, "notes don't overlap");

        var bounds = vm.GetContentBounds()!.Value;
        Assert.True(bounds.X <= left.X, "Fit includes a note left of the first participant");
    }

    [Fact]
    public void Activation_bars_run_from_plus_to_minus_and_from_activate_to_deactivate()
    {
        var vm = Load(Checkout);
        var bar = Assert.Single(vm.SequenceActivations);
        Assert.Equal("S", bar.ParticipantId);
        Assert.Equal(vm.Connectors.Single(c => c.Label == "checkout").SourceY, bar.Y, 3);
        Assert.Equal(vm.Connectors.Single(c => c.Label == "receipt").SourceY, bar.Y + bar.Height, 3);

        var vm2 = Load("sequenceDiagram\n    A->>B: one\n    activate B\n    B->>B: inner\n    activate B\n    B-->>A: two\n    deactivate B\n    deactivate B\n");
        Assert.Equal(2, vm2.SequenceActivations.Count);
        var outer = vm2.SequenceActivations.OrderBy(x => x.Y).First();
        var inner = vm2.SequenceActivations.OrderBy(x => x.Y).Last();
        Assert.True(inner.X > outer.X, "a stacked activation steps right");
        Assert.True(inner.Y >= outer.Y && inner.Y + inner.Height <= outer.Y + outer.Height);
    }

    [Fact]
    public void Arrows_meet_an_activation_bar_at_its_edge()
    {
        var vm = Load(Checkout);
        var bar = Assert.Single(vm.SequenceActivations);
        var checkout = vm.Connectors.Single(c => c.Label == "checkout"); // U ->> +S, from the left
        var charge = vm.Connectors.Single(c => c.Label == "charge");     // S ->> P, leaves to the right
        var ok = vm.Connectors.Single(c => c.Label == "ok");             // P -->> S, arrives from the right
        Assert.Equal(bar.X, checkout.TargetX, 3);
        Assert.Equal(bar.X + bar.Width, charge.SourceX, 3);
        Assert.Equal(bar.X + bar.Width, ok.TargetX, 3);
        // Without an activation, messages still run centre to centre.
        Assert.Equal(vm.Nodes.Single(n => n.Id == "U").LifelineX, checkout.SourceX, 3);
    }

    [Fact]
    public void An_unclosed_activation_runs_to_the_last_row()
    {
        var vm = Load("sequenceDiagram\n    A->>+B: start\n    B->>A: still going\n");
        var bar = Assert.Single(vm.SequenceActivations);
        Assert.True(bar.Y + bar.Height > vm.Connectors[1].SourceY);
    }

    [Fact]
    public void Rect_draws_a_band_without_a_label()
    {
        var vm = Load("sequenceDiagram\n    rect rgb(0, 0, 255)\n    A->>B: inside\n    end\n");
        var band = Assert.Single(vm.SequenceFrames);
        Assert.True(band.IsHighlight);
        Assert.Equal(string.Empty, band.Caption);
    }

    [Fact]
    public void Other_diagram_types_have_no_sequence_decorations()
    {
        var vm = Load(Checkout);
        vm.LoadFromMermaidCode("flowchart TD\n    A --> B\n");
        Assert.Empty(vm.SequenceFrames);
        Assert.Empty(vm.SequenceNotes);
        Assert.Empty(vm.SequenceActivations);
    }
}
