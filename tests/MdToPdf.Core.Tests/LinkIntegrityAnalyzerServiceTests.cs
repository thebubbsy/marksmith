using MdToPdf.Core.Services;
using Xunit;

namespace MdToPdf.Core.Tests
{
    public class LinkIntegrityAnalyzerServiceTests
    {
        private readonly LinkIntegrityAnalyzerService _service = new LinkIntegrityAnalyzerService();

        [Fact]
        public void Analyze_DetectsValidAnchorLinks()
        {
            string md = @"# Section One

Link to [Section One](#section-one).
";
            var report = _service.Analyze(md);
            Assert.True(report.IsValid);
            Assert.Empty(report.Issues);
            Assert.Equal(1, report.TotalLinksChecked);
        }

        [Fact]
        public void Analyze_DetectsMissingAnchorLinks()
        {
            string md = @"# Section One

Link to [Missing](#missing-section).
";
            var report = _service.Analyze(md);
            Assert.False(report.IsValid);
            Assert.Single(report.Issues);
            Assert.Equal(LinkIssueType.MissingAnchor, report.Issues[0].IssueType);
        }

        [Fact]
        public void Analyze_IgnoresExternalUrls()
        {
            string md = @"[Google](https://www.google.com)";
            var report = _service.Analyze(md);
            Assert.True(report.IsValid);
            Assert.Equal(1, report.TotalLinksChecked);
        }
    }
}
