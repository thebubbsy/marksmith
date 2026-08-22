using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using MarkSmith.Services;
using MarkSmith.Services.SpellCheck;
using MarkSmith.Services.Mermaid;

namespace MarkSmith.Tests
{
    public class Cycle6Block4ExecutionTests
    {
        [Fact]
        public void FastTrieSpellChecker_ValidatesKnownWords_AndRejectsTypos()
        {
            var checker = FastTrieSpellChecker.Default;
            Assert.True(checker.IsValidWord("markdown".AsSpan()));
            Assert.True(checker.IsValidWord("document".AsSpan()));
            Assert.True(checker.IsValidWord("smartart".AsSpan()));
            Assert.True(checker.IsValidWord("galaxy".AsSpan()));

            Assert.False(checker.IsValidWord("markdwnn".AsSpan()));
            Assert.False(checker.IsValidWord("documnt".AsSpan()));
        }

        [Fact]
        public void FastTrieSpellChecker_GeneratesCloseSuggestions()
        {
            var checker = FastTrieSpellChecker.Default;
            var suggestions = checker.GetSuggestions("markdwn", 3);
            Assert.NotEmpty(suggestions);
            Assert.Contains("markdown", suggestions, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void FastTrieSpellChecker_SkipsCodeBlocksAndMath()
        {
            var checker = FastTrieSpellChecker.Default;
            string doc = @"# Heading
This is a valid document with markdown text.

```csharp
var badtypo_var = 123;
```

$$
\frac{badmathxyz}{123}
$$

A typo word here: documnt.
";
            var errors = checker.CheckMarkdownText(doc);
            Assert.Single(errors);
            Assert.Equal("documnt", errors[0].word);
        }

        [Fact]
        public void LineDiff_ExtractHunks_GroupsChangesAccurately()
        {
            string oldText = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5";
            string newText = "Line 1\nLine 2 (Modified)\nLine 3\nLine 4\nLine 5";

            var lines = LineDiff.Diff(oldText, newText);
            var hunks = LineDiff.ExtractHunks(lines, context: 1);

            Assert.NotEmpty(hunks);
            Assert.StartsWith("@@ -", hunks[0].Header);
        }

        [Fact]
        public async Task VersionHistoryService_ExportDiffReport_GeneratesPatchAndHtml()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "ms_diff_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var history = new VersionHistoryService(tempDir);
                var filePath = "C:\\docs\\report.md";

                bool cap1 = await history.CaptureAsync(filePath, "Line A\nLine B", "snapshot");
                bool cap2 = await history.CaptureAsync(filePath, "Line A\nLine B (Modified)\nLine C", "snapshot");

                Assert.True(cap1);
                Assert.True(cap2);

                var versions = await history.GetVersionsAsync(filePath);
                Assert.True(versions.Count >= 2);
                var v2 = versions[0];
                var v1 = versions[1];

                string patch = await history.ExportDiffReportAsync(filePath, v1.Id, v2.Id, VersionHistoryService.DiffExportFormat.Patch);
                Assert.Contains("--- a/report.md", patch);
                Assert.Contains("+++ b/report.md", patch);
                Assert.Contains("Line B (Modified)", patch);

                string html = await history.ExportDiffReportAsync(filePath, v1.Id, v2.Id, VersionHistoryService.DiffExportFormat.Html);
                Assert.Contains("<!DOCTYPE html>", html);
                Assert.Contains("Diff Report: report.md", html);
                Assert.Contains("Line B (Modified)", html);

                string md = await history.ExportDiffReportAsync(filePath, v1.Id, v2.Id, VersionHistoryService.DiffExportFormat.Markdown);
                Assert.Contains("```diff", md);
                Assert.Contains("Line B (Modified)", md);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void DocxShapeEmitter_GradientFillXml_EmitsValidDrawingML()
        {
            string xml = DocxShapeEmitter.GradientFillXml("#FF7C4D", "#7C4DFF", 90);
            Assert.Contains("<a:gradFill>", xml);
            Assert.Contains("<a:gsLst>", xml);
            Assert.Contains("<a:srgbClr val=\"FF7C4D\"/>", xml);
            Assert.Contains("<a:srgbClr val=\"7C4DFF\"/>", xml);
            Assert.Contains("<a:lin ang=\"5400000\"/>", xml);
        }
    }
}
