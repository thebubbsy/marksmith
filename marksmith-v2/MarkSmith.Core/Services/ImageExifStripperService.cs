using System;
using System.IO;
using SkiaSharp;

namespace MarkSmith.Core.Services
{
    public class ImageExifStripperService
    {
        /// <summary>
        /// Decodes an image from input byte array or stream and re-encodes it clean without EXIF or metadata tags.
        /// </summary>
        public byte[] StripExif(byte[] imageBytes, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg, int quality = 90)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                return Array.Empty<byte>();

            try
            {
                using var inputStream = new MemoryStream(imageBytes);
                using var bitmap = SKBitmap.Decode(inputStream);
                if (bitmap == null) return imageBytes; // Return original if not a valid bitmap

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(format, quality);

                return data.ToArray();
            }
            catch
            {
                return imageBytes;
            }
        }
    }
}
