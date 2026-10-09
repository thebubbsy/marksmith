using Xunit;
using MarkSmith.Services;

namespace MarkSmith.Core.Tests;

// KanbanNormalizer turns :::kanban fenced blocks into the <div class="kanban-*"> HTML the live
// preview's WebView renders. It sits alongside KanbanParser (well covered elsewhere) but had no
// direct test coverage of its own: the fenced-code-block guard, HTML escaping, and the
// completed/tag/title rendering it owns were all previously unverified.
public class KanbanNormalizerTests
{
    [Fact]
    public void Apply_LeavesMarkdownWithoutKanbanBlockUntouched()
    {
        var input = "# Just a doc\n\nSome prose, no boards here.";
        Assert.Equal(input, KanbanNormalizer.Apply(input));
    }

    [Fact]
    public void Apply_HandlesEmptyString()
    {
        Assert.Equal("", KanbanNormalizer.Apply(""));
    }

    [Fact]
    public void Apply_RendersColumnsAndCards()
    {
        var md = ":::kanban\n# To Do\n- Task 1\n- [x] Task 2\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("<div class=\"kanban-board\">", html);
        Assert.Contains("<div class=\"kanban-column-title\">To Do</div>", html);
        Assert.Contains("<div class=\"kanban-card\">Task 1</div>", html);
    }

    [Fact]
    public void Apply_RendersCheckedCardWithCompletedClassAndIcon()
    {
        var md = ":::kanban\n# To Do\n- [x] Ship it\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("kanban-card completed", html);
        Assert.Contains("☑ Ship it", html);
    }

    [Fact]
    public void Apply_RendersUncheckedCardWithoutCompletedClassButWithIcon()
    {
        var md = ":::kanban\n# To Do\n- [ ] Not yet\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.DoesNotContain("kanban-card completed", html);
        Assert.Contains("☐ Not yet", html);
    }

    [Fact]
    public void Apply_PlainBulletHasNoCheckboxIconOrCompletedClass()
    {
        var md = ":::kanban\n# To Do\n- Plain task\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("<div class=\"kanban-card\">Plain task</div>", html);
        Assert.DoesNotContain("☑", html);
        Assert.DoesNotContain("☐", html);
    }

    [Fact]
    public void Apply_RendersHashtagsAsTagSpans()
    {
        var md = ":::kanban\n# To Do\n- Fix the bug #bug #urgent\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("<span class=\"kanban-tag\">#bug</span>", html);
        Assert.Contains("<span class=\"kanban-tag\">#urgent</span>", html);
    }

    [Fact]
    public void Apply_RendersBoardTitleFromAttribute()
    {
        var md = ":::kanban title=\"Sprint 1\"\n# To Do\n- Task\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("<div class=\"kanban-board-title\">Sprint 1</div>", html);
    }

    [Fact]
    public void Apply_OmitsBoardTitleDivWhenNoTitleAttribute()
    {
        var md = ":::kanban\n# To Do\n- Task\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.DoesNotContain("kanban-board-title", html);
    }

    [Fact]
    public void Apply_HtmlEncodesCardTextAndTitle()
    {
        var md = ":::kanban title=\"R&D <Sprint>\"\n# To Do\n- Fix <script>alert(1)</script>\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("R&amp;D &lt;Sprint&gt;", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Apply_SupportsColumnsWithNoCardsAndMultipleColumns()
    {
        var md = ":::kanban\n# To Do\n# Doing\n- In progress\n# Done\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("<div class=\"kanban-column-title\">To Do</div>", html);
        Assert.Contains("<div class=\"kanban-column-title\">Doing</div>", html);
        Assert.Contains("<div class=\"kanban-column-title\">Done</div>", html);
        Assert.Contains("<div class=\"kanban-card\">In progress</div>", html);
    }

    [Fact]
    public void Apply_DoesNotProcessKanbanMarkersInsideFencedCodeBlock()
    {
        var md = "```\n:::kanban\n# To Do\n- Task\n:::\n```";
        Assert.Equal(md, KanbanNormalizer.Apply(md));
    }

    [Fact]
    public void Apply_DoesNotProcessKanbanMarkersInsideTildeFence()
    {
        var md = "~~~\n:::kanban\n# To Do\n- Task\n:::\n~~~";
        Assert.Equal(md, KanbanNormalizer.Apply(md));
    }

    [Fact]
    public void Apply_ProcessesKanbanBlockFollowingAnUnrelatedCodeFence()
    {
        var md = "```\nplain code, no boards\n```\n\n:::kanban\n# To Do\n- Task\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("```\nplain code, no boards\n```", html);
        Assert.Contains("<div class=\"kanban-board\">", html);
    }

    [Fact]
    public void Apply_HandlesMultipleKanbanBlocksInOneDocument()
    {
        var md = ":::kanban\n# Backlog\n- A\n:::\n\nSome text between.\n\n:::kanban\n# Sprint\n- B\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("<div class=\"kanban-column-title\">Backlog</div>", html);
        Assert.Contains("<div class=\"kanban-column-title\">Sprint</div>", html);
        Assert.Contains("Some text between.", html);
    }

    [Fact]
    public void Apply_HandlesUnclosedBlockByConsumingToEndOfDocument()
    {
        // No closing ::: — the normalizer must still terminate (not hang or throw) and render
        // whatever content it collected through end-of-input.
        var md = ":::kanban\n# To Do\n- Task 1";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("<div class=\"kanban-board\">", html);
        Assert.Contains("<div class=\"kanban-card\">Task 1</div>", html);
    }

    [Fact]
    public void Apply_SupportsExtraColonsInOpener()
    {
        // KanbanNormalizer's own Opener/Closer regexes accept 3-OR-MORE colons (so a kanban block
        // can be fenced with extra colons when nested inside another ::: container). Regression
        // guard: the raw "::::" closer must be stripped, not appended onto the last card's text,
        // and the opener's attributes must still be parsed.
        var md = "::::kanban title=\"Sprint 1\"\n# To Do\n- Task\n::::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("<div class=\"kanban-board-title\">Sprint 1</div>", html);
        Assert.Contains("<div class=\"kanban-card\">Task</div>", html);
        Assert.DoesNotContain(":::", html);
    }

    [Fact]
    public void Apply_BareBulletsBeforeAnyHeaderGetADefaultBacklogColumn()
    {
        var md = ":::kanban\n- No column yet\n:::";
        var html = KanbanNormalizer.Apply(md);

        Assert.Contains("<div class=\"kanban-column-title\">Backlog</div>", html);
        Assert.Contains("<div class=\"kanban-card\">No column yet</div>", html);
    }
}
