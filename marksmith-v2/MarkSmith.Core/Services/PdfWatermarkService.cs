using System;
using System.IO;
using MarkSmith.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace MarkSmith.Services;

public sealed record PdfWatermarkOptions
{
    public string Text { get; init; } = "CONFIDENTIAL";
    public double FontSize { get; init; } = 54.0;
    public double Opacity { get; init; } = 0.15; // 0.0 to 1.0
    public double RotationAngle { get; init; } = 45.0; // Angle in degrees
    public string ColorHex { get; init; } = "#888888";
    public bool Foreground { get; init; } = true;
}

/// <summary>
/// Custom PDF Watermark &amp; Classification Stamp Engine (Task 19). Post-processes generated PDF streams
/// to draw configurable diagonal text watermarks (e.g. "CONFIDENTIAL", "DRAFT") across every page using PDFsharp.
/// </summary>
public static class PdfWatermarkService
{
    public static byte[] Apply(byte[] pdfBytes, PdfWatermarkOptions options)
    {
        if (pdfBytes == null || pdfBytes.Length == 0) return Array.Empty<byte>();
        if (options == null || string.IsNullOrWhiteSpace(options.Text)) return pdfBytes;

        try
        {
            using var inStream = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(inStream, PdfDocumentOpenMode.Modify);

            var opacity = Math.Clamp(options.Opacity, 0.01, 1.0);
            var fontSize = Math.Max(10.0, options.FontSize);
            var color = ParseColorHex(options.ColorHex, opacity);
            var font = new XFont("Helvetica", fontSize, XFontStyleEx.Bold);
            var brush = new XSolidBrush(color);
            var format = new XStringFormat
            {
                Alignment = XStringAlignment.Center,
                LineAlignment = XLineAlignment.Center
            };

            var pageOptions = options.Foreground ? XGraphicsPdfPageOptions.Append : XGraphicsPdfPageOptions.Prepend;

            foreach (PdfPage page in doc.Pages)
            {
                using var gfx = XGraphics.FromPdfPage(page, pageOptions);
                var state = gfx.Save();

                var centerX = page.Width.Point / 2.0;
                var centerY = page.Height.Point / 2.0;

                gfx.TranslateTransform(centerX, centerY);
                gfx.RotateTransform(-options.RotationAngle);

                gfx.DrawString(options.Text, font, brush, new XPoint(0, 0), format);
                gfx.Restore(state);
            }

            using var outStream = new MemoryStream();
            doc.Save(outStream);
            return outStream.ToArray();
        }
        catch
        {
            // Fail gracefully by returning unmodified pdf bytes if PDFsharp parsing encounters a problem
            return pdfBytes;
        }
    }

    private static XColor ParseColorHex(string hex, double opacity)
    {
        if (string.IsNullOrWhiteSpace(hex)) return XColor.FromArgb((int)(opacity * 255), 128, 128, 128);
        hex = hex.TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return XColor.FromArgb((int)(opacity * 255), r, g, b);
        }
        return XColor.FromArgb((int)(opacity * 255), 128, 128, 128);
    }
}
