using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace EverythingHttpPlugin
{
    public sealed class NativeThumbnailService
    {
        private readonly string _cacheDir;
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".ico", ".tiff", ".tif"
        };
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv", ".m4v", ".ts"
        };

        public NativeThumbnailService(string? customCacheDir = null)
        {
            if (!string.IsNullOrEmpty(customCacheDir))
            {
                _cacheDir = customCacheDir;
            }
            else
            {
                _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EverythingHttpPlugin", "thumbnails");
            }

            try
            {
                Directory.CreateDirectory(_cacheDir);
            }
            catch { }
        }

        public async Task<byte[]?> GetThumbnailBytesAsync(string filePath, int targetSize = 360)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            var ext = Path.GetExtension(filePath);
            var lastMod = File.GetLastWriteTimeUtc(filePath).Ticks;
            var cacheKey = ComputeHash($"{filePath}_{lastMod}_{targetSize}");
            var cacheFile = Path.Combine(_cacheDir, $"{cacheKey}.jpg");

            if (File.Exists(cacheFile))
            {
                try
                {
                    return await File.ReadAllBytesAsync(cacheFile);
                }
                catch { }
            }

            byte[]? result = null;

            if (ImageExtensions.Contains(ext))
            {
                result = await Task.Run(() => ExtractImageThumbnail(filePath, targetSize));
            }
            else if (VideoExtensions.Contains(ext) || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // Try Windows Shell native thumbnail extraction first
                result = await Task.Run(() => ExtractShellThumbnail(filePath, targetSize));

                // Fallback to ffmpeg for videos if shell fails
                if (result == null && VideoExtensions.Contains(ext))
                {
                    result = await ExtractVideoThumbnailFfmpegAsync(filePath, targetSize);
                }
            }

            if (result != null && result.Length > 0)
            {
                try
                {
                    await File.WriteAllBytesAsync(cacheFile, result);
                }
                catch { }
            }

            return result;
        }

        private static byte[]? ExtractImageThumbnail(string filePath, int targetSize)
        {
            try
            {
                using var original = Image.FromFile(filePath);
                var (width, height) = CalculateDimensions(original.Width, original.Height, targetSize);

                using var thumb = new Bitmap(width, height);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(original, 0, 0, width, height);
                }

                using var ms = new MemoryStream();
                var encoder = GetEncoder(ImageFormat.Jpeg);
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)85);

                if (encoder != null)
                {
                    thumb.Save(ms, encoder, encoderParams);
                }
                else
                {
                    thumb.Save(ms, ImageFormat.Jpeg);
                }

                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? ExtractShellThumbnail(string filePath, int targetSize)
        {
            try
            {
                var guid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItemImageFactory
                int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, guid, out var imageFactory);
                if (hr != 0 || imageFactory == null)
                {
                    return null;
                }

                var size = new SIZE { cx = targetSize, cy = targetSize };
                hr = imageFactory.GetImage(size, SIIGBF.SIIGBF_THUMBNAILONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, out var hBitmap);

                if (hr != 0 && hr != 1) // Try again with icon fallback
                {
                    hr = imageFactory.GetImage(size, SIIGBF.SIIGBF_ICONBACKGROUND, out hBitmap);
                }

                if (hr == 0 && hBitmap != IntPtr.Zero)
                {
                    try
                    {
                        using var bmp = Image.FromHbitmap(hBitmap);
                        using var ms = new MemoryStream();
                        var encoder = GetEncoder(ImageFormat.Jpeg);
                        using var encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)85);

                        if (encoder != null)
                        {
                            bmp.Save(ms, encoder, encoderParams);
                        }
                        else
                        {
                            bmp.Save(ms, ImageFormat.Jpeg);
                        }

                        return ms.ToArray();
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
            }
            catch
            {
                // COM or GDI error
            }

            return null;
        }

        private async Task<byte[]?> ExtractVideoThumbnailFfmpegAsync(string filePath, int targetSize)
        {
            var ffmpeg = FindFfmpegExecutable();
            if (string.IsNullOrEmpty(ffmpeg)) return null;

            var tempOut = Path.Combine(_cacheDir, $"tmp_{Guid.NewGuid():N}.jpg");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-ss 00:00:02 -i \"{filePath}\" -vframes 1 -vf \"scale={targetSize}:-1\" -q:v 2 \"{tempOut}\" -y",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                await proc.WaitForExitAsync();
                if (File.Exists(tempOut))
                {
                    var bytes = await File.ReadAllBytesAsync(tempOut);
                    File.Delete(tempOut);
                    return bytes;
                }
            }
            catch
            {
                if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
            }

            return null;
        }

        private static string? FindFfmpegExecutable()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                var full = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(full)) return full;
            }
            return null;
        }

        private static (int, int) CalculateDimensions(int origW, int origH, int target)
        {
            if (origW <= 0 || origH <= 0) return (target, target);
            if (origW > origH)
            {
                int h = (int)Math.Max(1, Math.Round((double)origH * target / origW));
                return (target, h);
            }
            else
            {
                int w = (int)Math.Max(1, Math.Round((double)origW * target / origH));
                return (w, target);
            }
        }

        private static ImageCodecInfo? GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid) return codec;
            }
            return null;
        }

        private static string ComputeHash(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
        }

        #region Windows COM Interop
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage([In] SIZE size, [In] SIIGBF flags, [Out] out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [Flags]
        private enum SIIGBF
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_MEMORYONLY = 0x02,
            SIIGBF_ICONONLY = 0x04,
            SIIGBF_THUMBNAILONLY = 0x08,
            SIIGBF_INCACHEONLY = 0x10,
            SIIGBF_CROPTOSQUARE = 0x20,
            SIIGBF_WIDETHUMBNAILS = 0x40,
            SIIGBF_ICONBACKGROUND = 0x80,
            SIIGBF_SCALEUP = 0x100
        }
        #endregion
    }
}
