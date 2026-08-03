using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Core.Kanban;

namespace MarkSmith.Core.Tests;

public class KanbanChallenger2Tests
{
    private readonly AdvancedFeaturePipeline _pipeline = new();
    private readonly KanbanDetector _detector = new();

    #region 1. Multiple Identical Blocks & Pipeline Interaction

    [Fact]
    public void MultipleIdenticalBlocks_PipelineDetectsAll_WithUniqueStableIds()
    {
        var blockContent = @":::kanban title=""Identical Board""
# To Do
- Task 1
- Task 2
# Done
- Task 3
:::";

        var markdown = $@"# Document Header

{blockContent}

Middle content paragraph.

{blockContent}

Ending paragraph.";

        var documentId = AdvancedFeaturePipeline.ContentBasedDocumentId(markdown);
        var nodes = _pipeline.Process(markdown, documentId);

        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, n => Assert.Equal("Kanban", n.Detector.FeatureName));
        Assert.All(nodes, n => Assert.Equal("Identical Board", n.Attributes["title"]));

        // Verify that StableIds are UNIQUE despite identical block text
        Assert.NotEqual(nodes[0].StableId, nodes[1].StableId);

        // Verify deterministic generation across pipeline runs on same document
        var reprocessedNodes = _pipeline.Process(markdown, documentId);
        Assert.Equal(nodes[0].StableId, reprocessedNodes[0].StableId);
        Assert.Equal(nodes[1].StableId, reprocessedNodes[1].StableId);
    }

    [Fact]
    public void MultipleIdenticalBlocks_BackToBack_PipelineDetectsBoth()
    {
        var markdown = @":::kanban title=""Board A""
# Col 1
- Task 1
:::
:::kanban title=""Board A""
# Col 1
- Task 1
:::";

        var documentId = AdvancedFeaturePipeline.ContentBasedDocumentId(markdown);
        var nodes = _pipeline.Process(markdown, documentId);

        Assert.Equal(2, nodes.Count);
        Assert.NotEqual(nodes[0].StableId, nodes[1].StableId);
    }

    [Fact]
    public void IndentedKanbanBlock_DetectorAndPipeline_Behavior()
    {
        var indentedBlock = @"  :::kanban title=""Indented Board""
  # Column 1
  - Task 1
  :::";

        // Detector direct test
        bool detectorMatchesDirect = _detector.Matches(indentedBlock);
        
        // Pipeline test
        var markdown = $@"Some text before

{indentedBlock}

Some text after";

        var nodes = _pipeline.Process(markdown, "doc_indented");

        // Documenting behavior: whether indented blocks match
        if (detectorMatchesDirect)
        {
            Assert.Single(nodes);
        }
        else
        {
            // If Matches fails due to leading spaces in rawBlock without TrimStart()
            Assert.Empty(nodes);
        }
    }

    #endregion

    #region 2. Checkbox State & Inline Markdown Parsing

    [Theory]
    [InlineData("- [ ] Uncompleted Task", false, "Uncompleted Task")]
    [InlineData("- [x] Completed Task Lowercase", true, "Completed Task Lowercase")]
    [InlineData("- [X] Completed Task Uppercase", true, "Completed Task Uppercase")]
    [InlineData("* [ ] Star Bullet Uncompleted", false, "Star Bullet Uncompleted")]
    [InlineData("+ [x] Plus Bullet Completed", true, "Plus Bullet Completed")]
    [InlineData("1. [ ] Numbered Bullet Uncompleted", false, "Numbered Bullet Uncompleted")]
    [InlineData("2) [x] Numbered Paren Bullet Completed", true, "Numbered Paren Bullet Completed")]
    [InlineData("- [-] Custom Cancelled Checkbox", null, "[-] Custom Cancelled Checkbox")]
    [InlineData("- [?] Question Checkbox", null, "[?] Question Checkbox")]
    public void CardCheckbox_StateParsing(string inputLine, bool? expectedCompleted, string expectedText)
    {
        var rawBlock = $@":::kanban
# Checkbox Column
{inputLine}
:::";

        var kanban = KanbanParser.Parse(rawBlock);
        Assert.Single(kanban.Columns);
        Assert.Single(kanban.Columns[0].Cards);

        var card = kanban.Columns[0].Cards[0];
        Assert.Equal(expectedCompleted, card.IsCompleted);
        Assert.Equal(expectedText, card.Text);
    }

    [Fact]
    public void CardCheckbox_EmptyTaskText_HandledGracefully()
    {
        var rawBlock = @":::kanban
# Column
- [ ]
- [x]
:::";

        var kanban = KanbanParser.Parse(rawBlock);
        var cards = kanban.Columns[0].Cards;

        Assert.Equal(2, cards.Count);
        Assert.False(cards[0].IsCompleted);
        Assert.Equal("", cards[0].Text);
        Assert.True(cards[1].IsCompleted);
        Assert.Equal("", cards[1].Text);
    }

    [Fact]
    public void CardInlineMarkdown_PreservesFormattingSyntax()
    {
        var rawBlock = @":::kanban
# Formatting Column
- Card with **bold**, *italic*, `code()`, [link](https://example.com), and ~~strikethrough~~
- Card with HTML <span style=""color:red"">red text</span> and <code>inline html</code>
:::";

        var kanban = KanbanParser.Parse(rawBlock);
        var cards = kanban.Columns[0].Cards;

        Assert.Equal(2, cards.Count);
        Assert.Equal(@"Card with **bold**, *italic*, `code()`, [link](https://example.com), and ~~strikethrough~~", cards[0].Text);
        Assert.Equal(@"Card with HTML <span style=""color:red"">red text</span> and <code>inline html</code>", cards[1].Text);
    }

    [Fact]
    public void CardTags_ExtractionInInlineMarkdownContext()
    {
        var rawBlock = @":::kanban
# Tag Column
- Normal tag #urgent
- Tag in bold **#critical**
- Tag in link [#feature](http://example.com)
- Tag in parens (#review)
:::";

        var kanban = KanbanParser.Parse(rawBlock);
        var cards = kanban.Columns[0].Cards;

        Assert.Contains("urgent", cards[0].Tags);

        // Documenting behavior of TagRegex with formatting boundaries
        bool boldTagExtracted = cards[1].Tags.Contains("critical");
        bool linkTagExtracted = cards[2].Tags.Contains("feature");
        bool parenTagExtracted = cards[3].Tags.Contains("review");

        Assert.False(boldTagExtracted, "TagRegex lookbehind requires whitespace before #, so **#critical fails");
        Assert.False(linkTagExtracted, "TagRegex lookbehind requires whitespace before #, so [#feature fails");
        Assert.False(parenTagExtracted, "TagRegex lookbehind requires whitespace before #, so (#review fails");
    }

    [Fact]
    public void MultilineCard_TagExtraction_BehaviorOnContinuationLines()
    {
        var rawBlock = @":::kanban
# Column 1
- Card title #tag1
  Continuation text #tag2
:::";

        var kanban = KanbanParser.Parse(rawBlock);
        var card = kanban.Columns[0].Cards[0];

        Assert.Contains("tag1", card.Tags);
        Assert.Equal("Card title #tag1\nContinuation text #tag2", card.Text);

        // Documenting behavior: continuation line tags are NOT added to card.Tags list
        Assert.DoesNotContain("tag2", card.Tags);
    }

    #endregion
}
