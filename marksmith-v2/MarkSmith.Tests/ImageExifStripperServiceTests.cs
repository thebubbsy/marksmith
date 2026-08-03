using System;
using MarkSmith.Core.Services;
using SkiaSharp;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class ImageExifStripperServiceTests
    {
        [Fact]
        public void StripExif_ReturnsCleanImageByteArray()
        {
            // Create a small 10x10 test image using SkiaSharp
            using var bitmap = new SKBitmap(10, 10);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Red);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] originalBytes = data.ToArray();

            var stripper = new ImageExifStripperService();
            byte[] cleanedBytes = stripper.StripExif(originalBytes, SKEncodedImageFormat.Png, 100);

            Assert.NotNull(cleanedBytes);
            Assert.True(cleanedBytes.Length > 0);

            // Re-decode cleaned bytes to ensure it's still a valid renderable image
            using var cleanedBitmap = SKBitmap.Decode(cleanedBytes);
            Assert.NotNull(cleanedBitmap);
            Assert.Equal(10, cleanedBitmap.Width);
            Assert.Equal(10, cleanedBitmap.Height);
        }

        [Fact]
        public void StripExif_HandlesEmptyOrInvalidDataGracefully()
        {
            var stripper = new ImageExifStripperService();
            var emptyResult = stripper.StripExif(Array.Empty<byte>());

            Assert.Empty(emptyResult);
        }
    }
}
