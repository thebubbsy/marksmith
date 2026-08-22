using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkSmith.Models;
using SkiaSharp;

namespace MarkSmith.Services;

/// <summary>
/// Headless document snapshot rasterizer that renders Markdown documents into high-resolution PNG images using SkiaSharp.
/// Supports theme palettes, auto-height calculation, high-DPI scaling, and rich Markdown typography.
/// </summary>
public class DocumentImageRasterizerService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseMathematics()
        .Build();

    public async Task<byte[]> RenderPngAsync(string markdown, AppSettings settings, ThemeDefinition theme, ImageRenderOptions options)
    {
        return await Task.Run(() =>
        {
            options ??= new ImageRenderOptions();
            settings ??= new AppSettings();
            theme ??= new ThemeCatalog().GetOrDefault(options.Theme ?? settings.Theme ?? "GitHub Light");

            var bgHex = string.IsNullOrWhiteSpace(theme.Background) ? "#FFFFFF" : theme.Background;
            var textHex = string.IsNullOrWhiteSpace(theme.Text) ? "#24292E" : theme.Text;
            var headingHex = string.IsNullOrWhiteSpace(theme.Heading) ? "#0969DA" : theme.Heading;
            var codeHex = string.IsNullOrWhiteSpace(theme.Code) ? "#F6F8FA" : theme.Code;
            var borderHex = string.IsNullOrWhiteSpace(theme.Border) ? "#D0D7DE" : theme.Border;
            var secondaryHex = string.IsNullOrWhiteSpace(theme.Secondary) ? "#F1F5F9" : theme.Secondary;

            SKColor bgColor = SKColor.TryParse(bgHex, out var bc) ? bc : SKColors.White;
            SKColor textColor = SKColor.TryParse(textHex, out var tc) ? tc : SKColors.Black;
            SKColor headingColor = SKColor.TryParse(headingHex, out var hc) ? hc : new SKColor(9, 105, 218);
            SKColor codeColor = SKColor.TryParse(codeHex, out var cc) ? cc : new SKColor(246, 248, 250);
            SKColor borderColor = SKColor.TryParse(borderHex, out var brc) ? brc : new SKColor(208, 215, 222);
            SKColor secondaryColor = SKColor.TryParse(secondaryHex, out var sc) ? sc : new SKColor(241, 245, 249);

            // Watermark detection
            string? watermarkText = null;
            var wmMatch = Regex.Match(markdown ?? "", @"^:::watermark\s+""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (wmMatch.Success)
            {
                watermarkText = wmMatch.Groups[1].Value;
            }

            var cleanMd = markdown ?? "";
            cleanMd = DialectNormalizer.Apply(cleanMd, settings.DashMode);
            var doc = Markdown.Parse(cleanMd, Pipeline);

            int logicalWidth = Math.Max(200, options.Width);
            float padding = 40f;
            float contentWidth = logicalWidth - (padding * 2);

            // 1. Measure content height
            float contentHeight = MeasureDocumentHeight(doc, cleanMd, contentWidth);
            int logicalHeight = options.Height > 0 ? options.Height : (int)Math.Ceiling(Math.Max(300f, contentHeight + (padding * 2)));

            double scale = options.Scale > 0 ? options.Scale : 2.0;
            int targetWidth = (int)Math.Round(logicalWidth * scale);
            int targetHeight = (int)Math.Round(logicalHeight * scale);

            var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(bgColor);
            canvas.Scale((float)scale);

            // 2. Render content
            float currentY = padding;
            RenderDocumentContent(canvas, doc, cleanMd, padding, contentWidth, ref currentY, textColor, headingColor, codeColor, borderColor, secondaryColor);

            // 3. Render watermark if present
            if (!string.IsNullOrWhiteSpace(watermarkText))
            {
                DrawWatermarkOverlay(canvas, watermarkText, logicalWidth, logicalHeight, textColor);
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, Math.Clamp(options.Quality, 1, 100));
            return data.ToArray();
        });
    }

    public async Task RenderPngToFileAsync(string markdown, string outputPath, AppSettings settings, ThemeDefinition theme, ImageRenderOptions options)
    {
        var bytes = await RenderPngAsync(markdown, settings, theme, options);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllBytesAsync(outputPath, bytes);
    }

    private static float MeasureDocumentHeight(MarkdownDocument doc, string rawMd, float contentWidth)
    {
        if (doc.Count == 0 && string.IsNullOrWhiteSpace(rawMd)) return 100f;

        float totalY = 0f;
        foreach (var block in doc)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    int hLevel = Math.Clamp(heading.Level, 1, 6);
                    float hSize = hLevel == 1 ? 28f : (hLevel == 2 ? 22f : (hLevel == 3 ? 18f : 15f));
                    totalY += hSize + 24f;
                    break;

                case ParagraphBlock p:
                    var text = GetPlainText(p.Inline);
                    int lineCount = Math.Max(1, (int)Math.Ceiling(text.Length * 8.5f / contentWidth));
                    totalY += (lineCount * 22f) + 16f;
                    break;

                case FencedCodeBlock code:
                    int codeLines = code.Lines.Count;
                    totalY += (codeLines * 18f) + 32f;
                    break;

                case Markdig.Extensions.Tables.Table table:
                    int rows = table.Count;
                    totalY += (rows * 28f) + 24f;
                    break;

                case ListBlock list:
                    int items = list.Count;
                    totalY += (items * 24f) + 16f;
                    break;

                default:
                    totalY += 24f;
                    break;
            }
        }

        return Math.Max(80f, totalY);
    }

    private static void RenderDocumentContent(SKCanvas canvas, MarkdownDocument doc, string rawMd, float startX, float contentWidth, ref float currentY,
        SKColor textColor, SKColor headingColor, SKColor codeColor, SKColor borderColor, SKColor secondaryColor)
    {
        using var textPaint = new SKPaint
        {
            Color = textColor,
            TextSize = 14f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
        };

        using var boldPaint = new SKPaint
        {
            Color = textColor,
            TextSize = 14f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
        };

        using var codePaint = new SKPaint
        {
            Color = textColor,
            TextSize = 12f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Normal)
        };

        using var borderPaint = new SKPaint
        {
            Color = borderColor,
            StrokeWidth = 1f,
            IsStroke = true,
            IsAntialias = true
        };

        using var fillCodePaint = new SKPaint
        {
            Color = codeColor,
            IsAntialias = true
        };

        using var headerBgPaint = new SKPaint
        {
            Color = secondaryColor,
            IsAntialias = true
        };

        foreach (var block in doc)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    int hLevel = Math.Clamp(heading.Level, 1, 6);
                    float hSize = hLevel == 1 ? 26f : (hLevel == 2 ? 20f : (hLevel == 3 ? 16f : 14f));
                    using var hPaint = new SKPaint
                    {
                        Color = headingColor,
                        TextSize = hSize,
                        IsAntialias = true,
                        Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
                    };
                    var hText = GetPlainText(heading.Inline);
                    currentY += hSize;
                    canvas.DrawText(hText, startX, currentY, hPaint);
                    currentY += 12f;

                    if (hLevel <= 2)
                    {
                        canvas.DrawLine(startX, currentY, startX + contentWidth, currentY, borderPaint);
                        currentY += 12f;
                    }
                    break;
                }

                case ParagraphBlock p:
                {
                    var pText = GetPlainText(p.Inline);
                    var wrapped = WrapText(pText, contentWidth, textPaint);
                    foreach (var line in wrapped)
                    {
                        currentY += 18f;
                        canvas.DrawText(line, startX, currentY, textPaint);
                    }
                    currentY += 12f;
                    break;
                }

                case FencedCodeBlock code:
                {
                    float blockHeight = (code.Lines.Count * 18f) + 16f;
                    var rect = new SKRect(startX, currentY, startX + contentWidth, currentY + blockHeight);
                    canvas.DrawRoundRect(rect, 4f, 4f, fillCodePaint);
                    canvas.DrawRoundRect(rect, 4f, 4f, borderPaint);

                    float codeY = currentY + 16f;
                    for (int i = 0; i < code.Lines.Count; i++)
                    {
                        var codeLine = code.Lines.Lines[i].Slice.ToString();
                        canvas.DrawText(codeLine, startX + 12f, codeY, codePaint);
                        codeY += 18f;
                    }
                    currentY += blockHeight + 14f;
                    break;
                }

                case Markdig.Extensions.Tables.Table table:
                {
                    int colCount = table.ColumnDefinitions.Count;
                    if (colCount == 0) colCount = 1;
                    float colW = contentWidth / colCount;

                    int rIdx = 0;
                    foreach (var rObj in table)
                    {
                        if (rObj is not Markdig.Extensions.Tables.TableRow row) continue;
                        float rowH = 26f;
                        var rowRect = new SKRect(startX, currentY, startX + contentWidth, currentY + rowH);

                        if (row.IsHeader)
                        {
                            canvas.DrawRect(rowRect, headerBgPaint);
                        }

                        canvas.DrawRect(rowRect, borderPaint);

                        for (int c = 0; c < row.Count && c < colCount; c++)
                        {
                            if (row[c] is Markdig.Extensions.Tables.TableCell cell)
                            {
                                var cellText = GetCellPlainText(cell);
                                float cellX = startX + (c * colW) + 8f;
                                canvas.DrawText(cellText, cellX, currentY + 18f, row.IsHeader ? boldPaint : textPaint);
                                if (c > 0)
                                {
                                    canvas.DrawLine(startX + (c * colW), currentY, startX + (c * colW), currentY + rowH, borderPaint);
                                }
                            }
                        }

                        currentY += rowH;
                        rIdx++;
                    }
                    currentY += 14f;
                    break;
                }

                case ListBlock list:
                {
                    int itemNum = 1;
                    foreach (var itemObj in list)
                    {
                        if (itemObj is not ListItemBlock item) continue;
                        string bullet = list.IsOrdered ? $"{itemNum++}. " : "• ";
                        float bulletW = textPaint.MeasureText(bullet);
                        canvas.DrawText(bullet, startX + 8f, currentY + 16f, boldPaint);

                        foreach (var c in item)
                        {
                            if (c is ParagraphBlock pb)
                            {
                                var itText = GetPlainText(pb.Inline);
                                var wrapped = WrapText(itText, contentWidth - bulletW - 16f, textPaint);
                                for (int li = 0; li < wrapped.Count; li++)
                                {
                                    if (li > 0) currentY += 18f;
                                    canvas.DrawText(wrapped[li], startX + 8f + bulletW + 4f, currentY + 16f, textPaint);
                                }
                            }
                        }
                        currentY += 22f;
                    }
                    currentY += 8f;
                    break;
                }
            }
        }
    }

    private static void DrawWatermarkOverlay(SKCanvas canvas, string text, float width, float height, SKColor textColor)
    {
        using var wmPaint = new SKPaint
        {
            Color = new SKColor(textColor.Red, textColor.Green, textColor.Blue, 30),
            TextSize = 48f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
            TextAlign = SKTextAlign.Center
        };

        canvas.Save();
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees(-45f);
        canvas.DrawText(text, 0, 0, wmPaint);
        canvas.Restore();
    }

    private static List<string> WrapText(string text, float maxWidth, SKPaint paint)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;

        var words = text.Split(' ');
        var currentLine = "";

        foreach (var word in words)
        {
            var testLine = string.IsNullOrEmpty(currentLine) ? word : $"{currentLine} {word}";
            if (paint.MeasureText(testLine) <= maxWidth)
            {
                currentLine = testLine;
            }
            else
            {
                if (!string.IsNullOrEmpty(currentLine)) lines.Add(currentLine);
                currentLine = word;
            }
        }
        if (!string.IsNullOrEmpty(currentLine)) lines.Add(currentLine);
        return lines;
    }

    private static string GetPlainText(ContainerInline? container)
    {
        if (container == null) return "";
        var list = new List<string>();
        Collect(container, list);
        return string.Concat(list);

        static void Collect(ContainerInline c, List<string> list)
        {
            foreach (var inline in c)
            {
                switch (inline)
                {
                    case LiteralInline l: list.Add(l.Content.ToString()); break;
                    case CodeInline ci: list.Add(ci.Content); break;
                    case ContainerInline nested: Collect(nested, list); break;
                }
            }
        }
    }

    private static string GetCellPlainText(Markdig.Extensions.Tables.TableCell cell)
    {
        var parts = new List<string>();
        foreach (var b in cell)
        {
            if (b is ParagraphBlock pb && pb.Inline != null)
            {
                parts.Add(GetPlainText(pb.Inline));
            }
        }
        return string.Join(" ", parts).Trim();
    }
}