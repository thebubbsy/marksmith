using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MdToPdf.Core.AdvancedFeatures;
using MdToPdf.Core.Kanban;
using Xunit;

namespace MdToPdf.Core.Tests;

public class KanbanAdversarialTests
{
    private readonly KanbanDetector _detector = new();

    #region 1. Unclosed Blocks

    [Fact]
    public void Test_UnclosedKanbanBlock_WithoutClosingMarker_ParsesSuccessfully()
    {
        var rawBlock = @":::kanban title=""Unclosed Board""
# Column 1
- Task A
# Column 2
- Task B";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Equal("Unclosed Board", kanban.Title);
        Assert.Equal(2, kanban.Columns.Count);
        Assert.Equal("Column 1", kanban.Columns[0].Title);
        Assert.Single(kanban.Columns[0].Cards);
        Assert.Equal("Task A", kanban.Columns[0].Cards[0].Text);
        Assert.Equal("Column 2", kanban.Columns[1].Title);
        Assert.Single(kanban.Columns[1].Cards);
        Assert.Equal("Task B", kanban.Columns[1].Cards[0].Text);
    }

    [Fact]
    public void Test_UnclosedKanbanBlock_SingleLine_ReturnsEmptyBlock()
    {
        var rawBlock = ":::kanban";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Empty(kanban.Columns);
        Assert.Equal(rawBlock, kanban.RawText);
    }

    [Fact]
    public void Test_UnclosedKanbanBlock_DetectorValidation()
    {
        var rawBlock = @":::kanban
# To Do
- Task 1";

        Assert.True(_detector.Matches(rawBlock));
        var (isValid, confidence, errors) = _detector.Validate(rawBlock);
        Assert.True(isValid);
        Assert.True(confidence >= _detector.Threshold);
        Assert.Empty(errors);
    }

    #endregion

    #region 2. Deeply Nested Headers

    [Fact]
    public void Test_DeeplyNestedHeaders_H1ToH6_ParsedAsColumns()
    {
        var rawBlock = @":::kanban
# Level 1 Column
- Task 1
## Level 2 Column
- Task 2
### Level 3 Column
- Task 3
#### Level 4 Column
- Task 4
##### Level 5 Column
- Task 5
###### Level 6 Column
- Task 6";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Equal(6, kanban.Columns.Count);
        Assert.Equal("Level 1 Column", kanban.Columns[0].Title);
        Assert.Equal("Level 2 Column", kanban.Columns[1].Title);
        Assert.Equal("Level 3 Column", kanban.Columns[2].Title);
        Assert.Equal("Level 4 Column", kanban.Columns[3].Title);
        Assert.Equal("Level 5 Column", kanban.Columns[4].Title);
        Assert.Equal("Level 6 Column", kanban.Columns[5].Title);
    }

    [Fact]
    public void Test_HeaderWithSevenHashes_NotParsedAsColumn()
    {
        var rawBlock = @":::kanban
# Valid Column
- Task 1
####### Not A Column Header
- Task 2
:::";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Single(kanban.Columns);
        Assert.Equal("Valid Column", kanban.Columns[0].Title);
        // "####### Not A Column Header" is treated as multiline text continuation of Task 1
        Assert.Equal(2, kanban.Columns[0].Cards.Count);
        Assert.Equal("Task 1\n####### Not A Column Header", kanban.Columns[0].Cards[0].Text);
        Assert.Equal("Task 2", kanban.Columns[0].Cards[1].Text);
    }

    #endregion

    #region 3. Mixed Bullet Styles

    [Fact]
    public void Test_MixedBulletStyles_AllParsedAsCards()
    {
        var rawBlock = @":::kanban
# All Styles
- Dash Bullet
* Star Bullet
+ Plus Bullet
1. Number Dot Bullet
2) Number Paren Bullet
100. Large Number Bullet
:::";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Single(kanban.Columns);
        var cards = kanban.Columns[0].Cards;
        Assert.Equal(6, cards.Count);

        Assert.Equal("Dash Bullet", cards[0].Text);
        Assert.Equal("Star Bullet", cards[1].Text);
        Assert.Equal("Plus Bullet", cards[2].Text);
        Assert.Equal("Number Dot Bullet", cards[3].Text);
        Assert.Equal("Number Paren Bullet", cards[4].Text);
        Assert.Equal("Large Number Bullet", cards[5].Text);
    }

    #endregion

    #region 4. Unicode Column Titles & Cards

    [Fact]
    public void Test_UnicodeColumnTitlesAndCards()
    {
        var rawBlock = @":::kanban title=""國際化 Kanban 板""
# 待办事项 (To Do)
- [ ] 🚀 宇宙ロケット launch #space #日本語
# 進行中 (In Progress)
- [x] 🔥 Code review & Refactoring #dev #中文
# 已完成 (Done)
- [x] 📋 Specification complete 🎉 #docs
:::";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Equal("國際化 Kanban 板", kanban.Title);
        Assert.Equal(3, kanban.Columns.Count);

        Assert.Equal("待办事项 (To Do)", kanban.Columns[0].Title);
        Assert.Equal("🚀 宇宙ロケット launch #space #日本語", kanban.Columns[0].Cards[0].Text);
        Assert.Contains("space", kanban.Columns[0].Cards[0].Tags);

        Assert.Equal("進行中 (In Progress)", kanban.Columns[1].Title);
        Assert.Equal("🔥 Code review & Refactoring #dev #中文", kanban.Columns[1].Cards[0].Text);
        Assert.True(kanban.Columns[1].Cards[0].IsCompleted);

        Assert.Equal("已完成 (Done)", kanban.Columns[2].Title);
        Assert.Equal("📋 Specification complete 🎉 #docs", kanban.Columns[2].Cards[0].Text);
    }

    #endregion

    #region 5. Long Cards & Multiline Cards

    [Fact]
    public void Test_VeryLongCardDescription_10kChars()
    {
        var longDescription = new string('A', 10000);
        var rawBlock = $@":::kanban
# Column 1
- {longDescription} #longtag
:::";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Single(kanban.Columns);
        Assert.Single(kanban.Columns[0].Cards);
        Assert.Equal(10000 + 9, kanban.Columns[0].Cards[0].Text.Length); // description + " #longtag"
        Assert.Contains("longtag", kanban.Columns[0].Cards[0].Tags);
    }

    [Fact]
    public void Test_MultilineCardDescription()
    {
        var rawBlock = @":::kanban
# Column 1
- Line 1 of Card A
  Line 2 of Card A
  Line 3 of Card A
- Line 1 of Card B
:::";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Single(kanban.Columns);
        Assert.Equal(2, kanban.Columns[0].Cards.Count);

        var cardA = kanban.Columns[0].Cards[0];
        Assert.Equal("Line 1 of Card A\nLine 2 of Card A\nLine 3 of Card A", cardA.Text);

        var cardB = kanban.Columns[0].Cards[1];
        Assert.Equal("Line 1 of Card B", cardB.Text);
    }

    [Fact]
    public void Test_MultilineCard_TagsOnContinuationLine_BehaviorCheck()
    {
        var rawBlock = @":::kanban
# Column 1
- Initial Card #tag1
  Continuation line #tag2
:::";

        var kanban = KanbanParser.Parse(rawBlock);
        var card = kanban.Columns[0].Cards[0];

        Assert.Contains("tag1", card.Tags);
        // Note: tag2 on continuation line is appended to card.Text but ParseCard was only run on line 1.
        // Let's document whether tag2 is in Tags or not.
        Assert.Equal("Initial Card #tag1\nContinuation line #tag2", card.Text);
    }

    #endregion

    #region 6. Special Characters & Escaping

    [Fact]
    public void Test_SpecialCharacters_HTML_Quotes_Ampersands()
    {
        var rawBlock = @":::kanban title=""Board & <Test> 'Quotes'""
# <Header & Title> ""Quotes"" 'Singles'
- <script>alert('xss')</script> #sec
- Tom & Jerry > Mickey < Donald ""Double"" 'Single'
:::";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Equal(@"Board & <Test> 'Quotes'", kanban.Title);
        Assert.Single(kanban.Columns);
        Assert.Equal(@"<Header & Title> ""Quotes"" 'Singles'", kanban.Columns[0].Title);

        var cards = kanban.Columns[0].Cards;
        Assert.Equal(2, cards.Count);
        Assert.Equal(@"<script>alert('xss')</script> #sec", cards[0].Text);
        Assert.Contains("sec", cards[0].Tags);
        Assert.Equal(@"Tom & Jerry > Mickey < Donald ""Double"" 'Single'", cards[1].Text);
    }

    #endregion

    #region 7. Boundary Conditions & Performance

    [Fact]
    public void Test_ZeroColumns_ZeroCards_EmptyBlock()
    {
        var rawBlock = @":::kanban
:::";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Empty(kanban.Columns);
        Assert.Equal(rawBlock, kanban.RawText);
    }

    [Fact]
    public void Test_ImplicitBacklogColumn()
    {
        var rawBlock = @":::kanban
- Orphan Task 1
- Orphan Task 2
# Explicit Column 1
- Task 3
:::";

        var kanban = KanbanParser.Parse(rawBlock);

        Assert.Equal(2, kanban.Columns.Count);
        Assert.Equal("Backlog", kanban.Columns[0].Title);
        Assert.Equal(2, kanban.Columns[0].Cards.Count);

        Assert.Equal("Explicit Column 1", kanban.Columns[1].Title);
        Assert.Single(kanban.Columns[1].Cards);
    }

    [Fact]
    public void Test_50PlusColumns()
    {
        const int colCount = 60;
        var lines = new List<string> { ":::kanban title=\"Massive Board\"" };

        for (int i = 1; i <= colCount; i++)
        {
            lines.Add($"# Column {i}");
            lines.Add($"- Card A in Column {i}");
            lines.Add($"- Card B in Column {i}");
        }
        lines.Add(":::");

        var rawBlock = string.Join("\n", lines);

        var sw = Stopwatch.StartNew();
        var kanban = KanbanParser.Parse(rawBlock);
        sw.Stop();

        Assert.Equal(colCount, kanban.Columns.Count);
        for (int i = 0; i < colCount; i++)
        {
            Assert.Equal($"Column {i + 1}", kanban.Columns[i].Title);
            Assert.Equal(i, kanban.Columns[i].Index);
            Assert.Equal(2, kanban.Columns[i].Cards.Count);
        }

        Assert.True(sw.ElapsedMilliseconds < 500, $"50+ columns parse took too long: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Test_500PlusCards()
    {
        const int colCount = 5;
        const int cardsPerCol = 120; // 600 total cards
        var lines = new List<string> { ":::kanban title=\"High Volume Board\"" };

        for (int c = 1; c <= colCount; c++)
        {
            lines.Add($"# Column {c}");
            for (int k = 1; k <= cardsPerCol; k++)
            {
                var isDone = k % 2 == 0 ? "[x]" : "[ ]";
                lines.Add($"- {isDone} Card {k} in Col {c} #tag{k % 10}");
            }
        }
        lines.Add(":::");

        var rawBlock = string.Join("\n", lines);

        var sw = Stopwatch.StartNew();
        var kanban = KanbanParser.Parse(rawBlock);
        sw.Stop();

        Assert.Equal(colCount, kanban.Columns.Count);
        int totalCards = kanban.Columns.Sum(col => col.Cards.Count);
        Assert.Equal(colCount * cardsPerCol, totalCards);

        Assert.True(sw.ElapsedMilliseconds < 1000, $"600 cards parse took too long: {sw.ElapsedMilliseconds} ms");
    }

    #endregion
}
