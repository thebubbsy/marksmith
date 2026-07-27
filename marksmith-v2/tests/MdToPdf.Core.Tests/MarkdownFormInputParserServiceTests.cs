using System.Linq;
using MdToPdf.Core.Services;
using Xunit;

namespace MdToPdf.Core.Tests
{
    public class MarkdownFormInputParserServiceTests
    {
        [Fact]
        public void ExtractFormInputs_ExtractsTextAndSelectInputs()
        {
            string markdown = @"# Feedback Form
Please enter your details:
[input:user_name ""Jane Smith""]
[select:country options=[USA, UK, Canada, Australia]]";

            var parser = new MarkdownFormInputParserService();
            var fields = parser.ExtractFormInputs(markdown);

            Assert.Equal(2, fields.Count);

            var textInput = fields.FirstOrDefault(f => f.Name == "user_name");
            Assert.NotNull(textInput);
            Assert.Equal("text", textInput.Type);
            Assert.Equal("Jane Smith", textInput.DefaultValue);

            var selectOption = fields.FirstOrDefault(f => f.Name == "country");
            Assert.NotNull(selectOption);
            Assert.Equal("select", selectOption.Type);
            Assert.Equal(4, selectOption.Options.Count);
            Assert.Contains("Australia", selectOption.Options);
        }

        [Fact]
        public void RenderInputsToHtml_ConvertsSyntaxToHtmlInputs()
        {
            string markdown = @"[input:email email ""test@example.com""]";
            var parser = new MarkdownFormInputParserService();
            string html = parser.RenderInputsToHtml(markdown);

            Assert.Contains("<input type=\"email\" name=\"email\" value=\"test@example.com\"", html);
        }
    }
}
