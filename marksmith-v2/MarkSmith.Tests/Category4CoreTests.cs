using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests
{
    // Core-only subset preserved from the original Category4Tests.cs. The remaining tests in that
    // file exercised Avalonia-hosted services (ClipboardWatcherService, FolderWatcherService,
    // LocalAssetServer, AmbiguityColorizer, ContentDialog, App) which live in the UI head and are
    // out of scope for the platform-agnostic engine's test project.
    public class Category4CoreTests
    {
        [Fact]
        public void Challenger1_WikiLink_InsideHtmlAttribute_IsPreservedRaw()
        {
            var input = "<a href=\"[[TargetDoc]]\">link</a> and [[Page]]";
            var result = DialectNormalizer.Apply(input);
            Assert.Contains("<a href=\"[[TargetDoc]]\">link</a>", result);
            Assert.Contains("<span class=\"wikilink\">Page</span>", result);
            Assert.DoesNotContain("href=\"<span", result);
        }

        [Fact]
        public void Challenger1_HtmlSanitizer_DataTextHtml_IsSanitized()
        {
            var input = "<a href=\"data:text/html,<script>alert(1)</script>\">click</a>";
            var sanitized = HtmlSanitizer.Apply(input);
            Assert.DoesNotContain("data:text/html", sanitized);
            Assert.Contains("href=\"#\"", sanitized);
        }

        [Fact]
        public void Challenger1_MainViewModel_IngestMarkdown_NullSafety()
        {
            var vm = new MainViewModel();
            vm.IngestMarkdown(null!, "test_origin");
            Assert.Equal("", vm.PastedMarkdown);
            Assert.True(vm.UsePasteSource);
        }

        [Fact]
        public void Challenger1_DashReplacer_PreservesSpacingAndCurrency()
        {
            var input = "Price range $10 -- $20 for item  --  name";
            var result = DashReplacer.NormalizeDoubleHyphens(input);
            Assert.Contains("$10 — $20", result);
            Assert.Contains("item  —  name", result);
        }
    }
}
