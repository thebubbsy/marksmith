using System.Collections.Generic;
using MarkSmith.Core.Services;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class DocumentTemplateGalleryServiceTests
    {
        private readonly DocumentTemplateGalleryService _service = new DocumentTemplateGalleryService();

        [Fact]
        public void GetTemplates_ReturnsBuiltInPresets()
        {
            var presets = _service.GetTemplates();
            Assert.NotEmpty(presets);
            Assert.Contains(presets, p => p.Id == "tech_spec");
        }

        [Fact]
        public void ApplyTemplate_ReplacesVariablesCorrectly()
        {
            var vars = new Dictionary<string, string>
            {
                { "title", "Marksmith Engine Refactor" },
                { "author", "Tony" },
                { "date", "2026-07-27" }
            };

            string result = _service.ApplyTemplate("tech_spec", vars);

            Assert.Contains("# Technical Specification: Marksmith Engine Refactor", result);
            Assert.Contains("**Author**: Tony", result);
            Assert.Contains("**Date**: 2026-07-27", result);
        }

        [Fact]
        public void ApplyTemplate_ReturnsEmptyForInvalidId()
        {
            string result = _service.ApplyTemplate("non_existent_id", new Dictionary<string, string>());
            Assert.Empty(result);
        }
    }
}
