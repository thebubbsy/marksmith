using MdToPdf.Core.Services;
using Xunit;

namespace MdToPdf.Core.Tests
{
    public class TocAnchorInjectorServiceTests
    {
        [Fact]
        public void Process_GeneratesUniqueSlugsAndInjectsToc_WhenTocTagPresent()
        {
            var service = new TocAnchorInjectorService();
            string md = @"[TOC]

# Introduction
Welcome to the guide.

## Getting Started
Installation instructions.

## Getting Started
Duplicate heading test.

### Advanced Usage
Deep section.";

            var result = service.Process(md);

            Assert.True(result.TocInjected);
            Assert.Equal(4, result.Items.Count);
            Assert.Equal("introduction", result.Items[0].Slug);
            Assert.Equal("getting-started", result.Items[1].Slug);
            Assert.Equal("getting-started-1", result.Items[2].Slug);
            Assert.Equal("advanced-usage", result.Items[3].Slug);

            Assert.Contains("### Table of Contents", result.ProcessedMarkdown);
            Assert.Contains("- [Introduction](#introduction)", result.ProcessedMarkdown);
            Assert.Contains("- [Getting Started](#getting-started)", result.ProcessedMarkdown);
            Assert.Contains("- [Getting Started](#getting-started-1)", result.ProcessedMarkdown);
            Assert.Contains("  - [Advanced Usage](#advanced-usage)", result.ProcessedMarkdown);
            Assert.Contains("<a id=\"introduction\"></a>", result.ProcessedMarkdown);
        }

        [Fact]
        public void Process_IgnoresHeadingsInsideCodeBlocks()
        {
            var service = new TocAnchorInjectorService();
            string md = @"# Real Heading

```markdown
# Fake Heading Inside Code
```

## Another Real Heading";

            var result = service.Process(md);

            Assert.Equal(2, result.Items.Count);
            Assert.Equal("real-heading", result.Items[0].Slug);
            Assert.Equal("another-real-heading", result.Items[1].Slug);
        }
    }
}
