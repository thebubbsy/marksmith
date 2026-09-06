using Xunit;
using MarkSmith.Services;

namespace MarkSmith.Tests
{
    public class TableFormulaEvaluatorTests
    {
        [Fact]
        public void RangeFormula_HandlesMultiLetterColumns()
        {
            // Column "AA" is the 27th spreadsheet column (0-based grid index 26), one past "Z".
            // A base-26 conversion that forgets the +1 offset for each letter collapses every
            // "AA"-style column onto its first letter, so =SUM(AA1:AA1) would silently read
            // column A instead. Column A holds a different value here so a regression shows up
            // as the wrong sum rather than an out-of-range failure.
            var grid = new double?[1, 30];
            grid[0, 0] = 999;   // A1 - must NOT be picked up by an AA1:AA1 range
            grid[0, 26] = 42;   // AA1
            grid[0, 27] = 1000; // AB1 - outside the AA1:AA1 range

            bool ok = TableFormulaEvaluator.TryEvaluateCell(
                "=SUM(AA1:AA1)", 0, 1, grid, 1, 30,
                out double result, out string formatted, out _);

            Assert.True(ok);
            Assert.Equal(42, result);
            Assert.Equal("42", formatted);
        }

        [Fact]
        public void RangeFormula_MultiLetterColumnRangeSumsCorrectRange()
        {
            // AA1:AB1 should sum exactly the two multi-letter columns, not column A/B.
            var grid = new double?[1, 30];
            grid[0, 0] = 1;    // A1
            grid[0, 1] = 2;    // B1
            grid[0, 26] = 10;  // AA1
            grid[0, 27] = 20;  // AB1

            bool ok = TableFormulaEvaluator.TryEvaluateCell(
                "=SUM(AA1:AB1)", 0, 2, grid, 1, 30,
                out double result, out _, out _);

            Assert.True(ok);
            Assert.Equal(30, result);
        }

        [Theory]
        [InlineData("SUM", 60)]
        [InlineData("AVERAGE", 20)]
        [InlineData("MIN", 10)]
        [InlineData("MAX", 30)]
        [InlineData("PRODUCT", 6000)]
        [InlineData("COUNT", 3)]
        public void PositionalFormula_Above_AppliesEachOperation(string op, double expected)
        {
            var grid = new double?[4, 1];
            grid[0, 0] = 10;
            grid[1, 0] = 20;
            grid[2, 0] = 30;

            bool ok = TableFormulaEvaluator.TryEvaluateCell(
                $"={op}(ABOVE)", 3, 0, grid, 4, 1,
                out double result, out _, out _);

            Assert.True(ok);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void TryParseNumber_ParsesParenthesizedNegatives()
        {
            Assert.True(TableFormulaEvaluator.TryParseNumber("(100)", out double val));
            Assert.Equal(-100, val);
        }

        [Fact]
        public void FormatNumber_AppliesCurrencyFormatSwitch()
        {
            var formatted = TableFormulaEvaluator.FormatNumber(1234.5, "$#,##0.00");
            Assert.Equal("$1,234.50", formatted);
        }
    }
}
