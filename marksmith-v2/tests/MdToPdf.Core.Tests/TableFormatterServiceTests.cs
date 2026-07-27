using MdToPdf.Core.Services;
using Xunit;

namespace MdToPdf.Core.Tests
{
    public class TableFormatterServiceTests
    {
        private readonly TableFormatterService _service = new TableFormatterService();

        [Fact]
        public void FormatTable_AlignsColumnsUniformly()
        {
            string input = "| Header 1 | Long Header 2 |\n|---|---|\n| val | test val |";
            string formatted = _service.FormatTable(input);

            Assert.Contains("| Header 1 | Long Header 2 |", formatted);
            Assert.Contains("| val      | test val      |", formatted);
        }

        [Fact]
        public void FormatTable_PreservesAlignmentSpecifiers()
        {
            string input = "| Left | Center | Right |\n|:---|:---:|---:|\n| a | b | c |";
            string formatted = _service.FormatTable(input);

            Assert.Contains("| :--- | :---: | ---: |", formatted);
        }

        [Fact]
        public void FormatTable_HandlesEmptyAndInvalidInputs()
        {
            Assert.Equal("", _service.FormatTable(""));
            Assert.Equal("Just a normal text line", _service.FormatTable("Just a normal text line"));
        }
    }
}
