using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Ocr.MarkSmith;
using Xunit;

namespace MarkSmith.Core.Tests.Ocr;

/// <summary>What MarkSmith OCR's dictionary step may and may not change.</summary>
public class MsLexiconTests
{
    private static readonly MsLexicon Lex = new(new[] { "panel", "is", "if", "items", "john", "son", "new", "ton", "of", "gulls", "cool" });

    // One read character per letter, each confidently read, at the given gaps (px) between them.
    private static List<ReadChar> Word(string text, params int[] gaps)
    {
        var list = new List<ReadChar>();
        int x = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (i > 0) x += gaps.Length >= i ? gaps[i - 1] : 2;
            list.Add(new ReadChar(text[i].ToString(), 0.99f, x, x + 9, new[] { (text[i].ToString(), 0.99f) }));
            x += 10;
        }
        return list;
    }

    private static string Text(List<ReadChar>? w) => w is null ? "(unchanged)" : string.Concat(w.Select(c => c.Text));

    [Fact]
    public void A_typewriter_1_inside_a_word_is_an_l()
    {
        Assert.Equal("panel", Text(Lex.BestMatch(Word("pane1"))));
        Assert.Equal("cool", Text(Lex.BestMatch(Word("cooi"))));
    }

    [Fact]
    public void Confident_short_tokens_and_identifiers_are_left_alone()
    {
        Assert.Null(Lex.BestMatch(Word("ls")));
        Assert.Null(Lex.BestMatch(Word("lf")));
        Assert.Null(Lex.BestMatch(Word("item5")));
    }

    [Fact]
    public void Words_run_together_split_only_at_a_real_gap()
    {
        // "ofgulls": a clear gap between f and g.
        var joined = Lex.SplitJoined(Word("ofgulls", 2, 9, 2, 2, 2, 2), minGap: 3);
        Assert.NotNull(joined);
        Assert.Equal("of", Text(joined!.Value.First));
        Assert.Equal("gulls", Text(joined.Value.Second));
        // A name the list doesn't know, with one slightly wider kerning gap, stays whole.
        Assert.Null(Lex.SplitJoined(Word("johnson", 2, 2, 2, 3, 2, 2), minGap: 3));
        Assert.Null(Lex.SplitJoined(Word("newton", 2, 2, 4, 2, 2), minGap: 3));
    }
}
