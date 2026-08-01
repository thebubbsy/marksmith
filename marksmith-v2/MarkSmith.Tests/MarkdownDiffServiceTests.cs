using MdToPdf.Core.Services;
using Xunit;

namespace MdToPdf.Core.Tests
{
    public class MarkdownDiffServiceTests
    {
        private readonly MarkdownDiffService _service = new MarkdownDiffService();

        [Fact]
        public void Compare_IdenticalStrings_ReturnsUnchanged()
        {
            string text = "# Header\nLine 1\nLine 2";
            var result = _service.Compare(text, text);

            Assert.False(result.HasChanges);
            Assert.Equal(3, result.UnchangedCount);
            Assert.Equal(0, result.InsertedCount);
            Assert.Equal(0, result.DeletedCount);
        }

        [Fact]
        public void Compare_InsertedLine_ReturnsInsertedCount()
        {
            string oldText = "# Header\nLine 1";
            string newText = "# Header\nLine 1\nLine 2";

            var result = _service.Compare(oldText, newText);

            Assert.True(result.HasChanges);
            Assert.Equal(1, result.InsertedCount);
            Assert.Equal(2, result.UnchangedCount);
            Assert.Equal(0, result.DeletedCount);
        }

        [Fact]
        public void Compare_DeletedLine_ReturnsDeletedCount()
        {
            string oldText = "# Header\nLine 1\nLine 2";
            string newText = "# Header\nLine 2";

            var result = _service.Compare(oldText, newText);

            Assert.True(result.HasChanges);
            Assert.Equal(1, result.DeletedCount);
            Assert.Equal(2, result.UnchangedCount);
        }
    }
}
