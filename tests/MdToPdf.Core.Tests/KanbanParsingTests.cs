using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using MdToPdf.Core.AdvancedFeatures;
using MdToPdf.Core.Kanban;
using MdToPdf.Models;
using MdToPdf.Services;

namespace MdToPdf.Core.Tests;

public class KanbanParsingTests
{
    [Fact]
    public void KanbanDetector_Matches_And_Validates_KanbanBlocks()
    {
        var detector = new KanbanDetector();
        Assert.Equal("Kanban", detector.FeatureName);

        var validBlock = @":::kanban
# To Do
- Task 1
- Task 2
# In Progress
- Task 3
:::";

        Assert.True(detector.Matches(validBlock));
        var (isValid, confidence, errors) = detector.Validate(validBlock);
        Assert.True(isValid);
        Assert.True(confidence >= detector.Threshold);
        Assert.Empty(errors);
    }

    [Fact]
    public void AdvancedFeaturePipeline_Identifies_KanbanBlocks()
    {
        var pipeline = new AdvancedFeaturePipeline();
        var markdown = @"# Document Title

:::kanban title=""Sprint Board""
# To Do
- Task 1
- Task 2
# In Progress
- Task 3
:::

Some extra paragraph.";

        var nodes = pipeline.Process(markdown, "doc123");
        Assert.Single(nodes);
        var node = nodes[0];
        Assert.Equal("Kanban", node.Detector.FeatureName);
        Assert.Equal("Sprint Board", node.Attributes["title"]);
    }

    [Fact]
    public void KanbanParser_Parses_Columns_And_Cards()
    {
        var blockText = @":::kanban
# To Do
- Design UI layout
- Implement AST parser
# In Progress
* Write unit tests
+ Refactor pipeline
# Done
1. Initial spec definition
:::";

        var kanban = KanbanParser.Parse(blockText);

        Assert.Equal(3, kanban.Columns.Count);

        // Column 0: To Do
        var col0 = kanban.Columns[0];
        Assert.Equal("To Do", col0.Title);
        Assert.Equal(0, col0.Index);
        Assert.Equal(2, col0.Cards.Count);
        Assert.Equal("Design UI layout", col0.Cards[0].Text);
        Assert.Equal(0, col0.Cards[0].Index);
        Assert.Equal("Implement AST parser", col0.Cards[1].Text);
        Assert.Equal(1, col0.Cards[1].Index);

        // Column 1: In Progress
        var col1 = kanban.Columns[1];
        Assert.Equal("In Progress", col1.Title);
        Assert.Equal(1, col1.Index);
        Assert.Equal(2, col1.Cards.Count);
        Assert.Equal("Write unit tests", col1.Cards[0].Text);
        Assert.Equal("Refactor pipeline", col1.Cards[1].Text);

        // Column 2: Done
        var col2 = kanban.Columns[2];
        Assert.Equal("Done", col2.Title);
        Assert.Equal(2, col2.Index);
        Assert.Single(col2.Cards);
        Assert.Equal("Initial spec definition", col2.Cards[0].Text);
    }

    [Fact]
    public void KanbanParser_Parses_Checkboxes_And_Tags()
    {
        var blockText = @":::kanban title=""Feature Tracker""
# Backlog
- [ ] Fix memory leak #bug #urgent
- [x] Add Dark Mode theme #ui
:::";

        var kanban = KanbanParser.Parse(blockText);
        Assert.Equal("Feature Tracker", kanban.Title);
        Assert.Single(kanban.Columns);

        var col = kanban.Columns[0];
        Assert.Equal(2, col.Cards.Count);

        var card1 = col.Cards[0];
        Assert.Equal("Fix memory leak #bug #urgent", card1.Text);
        Assert.False(card1.IsCompleted);
        Assert.Contains("bug", card1.Tags);
        Assert.Contains("urgent", card1.Tags);

        var card2 = col.Cards[1];
        Assert.Equal("Add Dark Mode theme #ui", card2.Text);
        Assert.True(card2.IsCompleted);
        Assert.Contains("ui", card2.Tags);
    }

    [Fact]
    public void KanbanParser_Handles_EmptyColumns_And_ImplicitBacklog()
    {
        var blockText = @":::kanban
- Orphan Task 1
- Orphan Task 2
# Empty Column
# Ready Column
- Task A
:::";

        var kanban = KanbanParser.Parse(blockText);
        Assert.Equal(3, kanban.Columns.Count);

        // Column 0: Implicit Backlog
        Assert.Equal("Backlog", kanban.Columns[0].Title);
        Assert.Equal(2, kanban.Columns[0].Cards.Count);
        Assert.Equal("Orphan Task 1", kanban.Columns[0].Cards[0].Text);

        // Column 1: Empty Column
        Assert.Equal("Empty Column", kanban.Columns[1].Title);
        Assert.Empty(kanban.Columns[1].Cards);

        // Column 2: Ready Column
        Assert.Equal("Ready Column", kanban.Columns[2].Title);
        Assert.Single(kanban.Columns[2].Cards);
        Assert.Equal("Task A", kanban.Columns[2].Cards[0].Text);
    }

    [Fact]
    public void MarkdownHtmlService_Renders_KanbanBlocks()
    {
        var service = new MarkdownHtmlService();
        var markdown = @":::kanban title=""Web Preview Board""
# To Do
- Task 1
# In Progress
- Task 2
:::";

        var html = service.Render(markdown, new AppSettings(), new ThemeCatalog().GetOrDefault(""));
        Assert.Contains("kanban-board", html);
        Assert.Contains("Web Preview Board", html);
        Assert.Contains("kanban-column", html);
        Assert.Contains("To Do", html);
        Assert.Contains("Task 1", html);
        Assert.Contains("Task 2", html);
    }

    [Fact]
    public void DocxExportService_Exports_KanbanBlocks_WithoutError()
    {
        var exporter = new DocxExportService();
        var markdown = @":::kanban title=""DOCX Board""
# To Do
- Task 10
# Done
- Task 20
:::";

        var tempPath = Path.Combine(Path.GetTempPath(), $"kanban_test_{Guid.NewGuid():N}.docx");
        try
        {
            exporter.ExportAsync(markdown, tempPath, new AppSettings()).GetAwaiter().GetResult();
            Assert.True(File.Exists(tempPath));
            var fileInfo = new FileInfo(tempPath);
            Assert.True(fileInfo.Length > 0);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void KanbanBoard_DataStructure_And_Hierarchy_ParsedCorrectly()
    {
        var blockText = @":::kanban title=""Project Backlog""
# To Do
- Task 1
- Task 2
# In Progress
- Task 3
# Done
- Task 4
:::";

        KanbanBoard board = KanbanParser.Parse(blockText);
        Assert.Equal("Project Backlog", board.Title);
        Assert.Equal(3, board.Columns.Count);

        // Level 1 Nodes (Columns)
        KanbanColumn col1 = board.Columns[0];
        Assert.Equal("To Do", col1.Title);
        Assert.Equal(0, col1.Index);

        // Level 2 Nodes (Cards)
        Assert.Equal(2, col1.Cards.Count);
        KanbanCard card1 = col1.Cards[0];
        Assert.Equal("Task 1", card1.Text);
        Assert.Equal(0, card1.Index);

        KanbanCard card2 = col1.Cards[1];
        Assert.Equal("Task 2", card2.Text);
        Assert.Equal(1, card2.Index);
    }
}

