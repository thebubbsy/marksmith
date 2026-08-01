using System;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class HeaderFooterTemplateServiceTests
{
    [Fact]
    public void RenderHtml_ReturnsEmptyString_WhenTemplateNullOrEmpty()
    {
        Assert.Equal("", HeaderFooterTemplateService.RenderHtml(null!));
        Assert.Equal("", HeaderFooterTemplateService.RenderHtml("   "));
    }

    [Fact]
    public void RenderHtml_InterpolatesTokens_Successfully()
    {
        var now = new DateTime(2026, 7, 27, 14, 30, 0);
        var ctx = new HeaderFooterContext
        {
            Title = "Q&A System Architecture",
            Author = "Alan Turing",
            Timestamp = now
        };

        var template = "{title} | Page {page} of {pages} | {date} {time} | {author}";
        var html = HeaderFooterTemplateService.RenderHtml(template, ctx);

        Assert.Contains("Q&amp;A System Architecture", html);
        Assert.Contains("class=\"pageNumber\"", html);
        Assert.Contains("class=\"totalPages\"", html);
        Assert.Contains("2026-07-27", html);
        Assert.Contains("14:30", html);
        Assert.Contains("Alan Turing", html);
    }
}
