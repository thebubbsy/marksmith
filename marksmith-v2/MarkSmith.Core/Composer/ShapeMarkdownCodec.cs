using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Composer
{
    /// <summary>
    /// Encodes a shape composition as a :::shapes markdown block so designs round-trip
    /// through the editor, the main preview pane (SVG), and DOCX export (native DrawingML).
    ///
    /// Supports both human-readable plain-text syntax and high-density Deflate+Base64
    /// compressed binary streams (for large image mosaics, intricate line art, and sketches).
    /// </summary>
    public static class ShapeMarkdownCodec
    {
        public const string BlockTag = ":::shapes";
        public const string CompressedPrefix = "data:deflate;base64,";

        // Compiled once — cached compiled matchers.
        private static readonly Regex PtsRegex = new(@"pts=""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TextRegex = new(@"text=""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TColorRegex = new(@"tcolor=([0-9A-Fa-f]{6})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SwRegex = new(@"sw=(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<ComposedShape> Parse(string innerContent)
        {
            var result = new List<ComposedShape>();
            if (string.IsNullOrWhiteSpace(innerContent)) return result;

            // Check for compressed binary stream
            int prefixIdx = innerContent.IndexOf(CompressedPrefix, StringComparison.OrdinalIgnoreCase);
            if (prefixIdx >= 0)
            {
                string b64 = innerContent.Substring(prefixIdx + CompressedPrefix.Length).Trim();
                int endIdx = b64.IndexOfAny(new[] { ' ', '\r', '\n', ':' });
                if (endIdx > 0) b64 = b64.Substring(0, endIdx);
                try
                {
                    byte[] raw = Convert.FromBase64String(b64);
                    return DecodeBinary(raw);
                }
                catch
                {
                    // Fall back to line parser if decompression fails
                }
            }

            foreach (var raw in innerContent.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                // Pull the quoted pts="x1,y1;x2,y2;..." and text="..." tokens out BEFORE
                // splitting on whitespace.
                var ptsMatch = PtsRegex.Match(line);
                var textMatch = TextRegex.Match(line);
                var tcolorMatch = TColorRegex.Match(line);
                var swMatch = SwRegex.Match(line);

                List<(double X, double Y)>? ptsList = null;
                if (ptsMatch.Success)
                {
                    ptsList = new List<(double X, double Y)>();
                    foreach (var pair in ptsMatch.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var xy = pair.Split(',');
                        if (xy.Length == 2 &&
                            double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px) &&
                            double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py))
                        {
                            ptsList.Add((px, py));
                        }
                    }
                }
                string? label = textMatch.Success
                    ? textMatch.Groups[1].Value.Replace("&quot;", "\"").Replace("&amp;", "&").Replace("&#10;", "\n")
                    : null;
                // A tcolor=/sw= INSIDE a quoted pts/text token is label data, not a token —
                // the old sequential removal never let it match, so neither do we.
                string? labelColor = tcolorMatch.Success && !ContainedIn(tcolorMatch, ptsMatch, textMatch)
                    ? tcolorMatch.Groups[1].Value
                    : null;
                double sw = swMatch.Success && !ContainedIn(swMatch, ptsMatch, textMatch) &&
                    double.TryParse(swMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var swParsed)
                    ? swParsed
                    : 1.5;

                // Strip the token spans in ONE pass (index-ordered) instead of four line.Remove
                // re-allocations per line.
                var spans = new (int Index, int Length)[4];
                int nSpans = 0;
                foreach (var m in new[] { ptsMatch, textMatch, tcolorMatch, swMatch })
                {
                    if (m.Success) spans[nSpans++] = (m.Index, m.Length);
                }
                if (nSpans > 0)
                {
                    Array.Sort(spans, 0, nSpans, SpanStartComparer.Instance);
                    var sb = new StringBuilder(line.Length);
                    int pos = 0;
                    for (int i = 0; i < nSpans; i++)
                    {
                        // Overlapping span (e.g. "sw=" INSIDE a text="..." label) — the outer
                        // token already consumed it; skipping keeps old sequential semantics.
                        if (spans[i].Index < pos) continue;
                        sb.Append(line, pos, spans[i].Index - pos);
                        pos = spans[i].Index + spans[i].Length;
                    }
                    sb.Append(line, pos, line.Length - pos);
                    line = sb.ToString();
                }

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6) continue;

                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                    !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                    continue;

                int rot = 0;
                foreach (var p in parts.Skip(6))
                {
                    var kv = p.Split('=');
                    if (kv.Length == 2 && kv[0].Equals("rot", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(kv[1], out var r))
                    {
                        rot = r;
                    }
                }

                result.Add(new ComposedShape
                {
                    Prst = parts[0].ToLowerInvariant(),
                    X = x,
                    Y = y,
                    W = w,
                    H = h,
                    Fill = parts[5].TrimStart('#'),
                    Rot = rot,
                    PathPoints = ptsList is { Count: >= 2 } ? ptsList : null,
                    StrokeWidthPt = sw,
                    Text = label,
                    TextColor = labelColor
                });
            }

            return result;
        }

        public static string Serialize(IEnumerable<ComposedShape> shapes, bool? compact = null)
        {
            var shapeList = shapes as List<ComposedShape> ?? shapes.ToList();
            bool useCompact = compact ?? (shapeList.Count >= 20);

            var sb = new StringBuilder();
            sb.AppendLine();
            if (useCompact && shapeList.Count > 0)
            {
                byte[] binary = EncodeBinary(shapeList);
                string b64 = Convert.ToBase64String(binary);
                sb.AppendLine($"{BlockTag} compact=true");
                sb.AppendLine($"{CompressedPrefix}{b64}");
            }
            else
            {
                sb.AppendLine(BlockTag);
                foreach (var s in shapeList)
                {
                    sb.AppendLine(Format(s));
                }
            }
            sb.AppendLine(":::");
            sb.AppendLine();
            return sb.ToString();
        }

        public static byte[] EncodeBinary(IReadOnlyList<ComposedShape> shapes)
        {
            using var ms = new MemoryStream(Math.Max(64, shapes.Count * 28));
            using (var def = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            using (var bw = new BinaryWriter(def, Encoding.UTF8))
            {
                bw.Write((byte)1); // Version
                bw.Write(shapes.Count);
                foreach (var s in shapes)
                {
                    bw.Write(s.Prst ?? "rect");
                    bw.Write((float)s.X);
                    bw.Write((float)s.Y);
                    bw.Write((float)s.W);
                    bw.Write((float)s.H);
                    bw.Write(s.Fill ?? "000000");
                    bw.Write((short)s.Rot);
                    bw.Write((float)s.StrokeWidthPt);

                    if (s.PathPoints is { Count: >= 2 })
                    {
                        bw.Write((short)s.PathPoints.Count);
                        foreach (var pt in s.PathPoints)
                        {
                            bw.Write((float)pt.X);
                            bw.Write((float)pt.Y);
                        }
                    }
                    else
                    {
                        bw.Write((short)0);
                    }

                    bw.Write(s.Text ?? "");
                    bw.Write(s.TextColor ?? "");
                }
            }
            return ms.ToArray();
        }

        public static List<ComposedShape> DecodeBinary(byte[] compressedBytes)
        {
            using var ms = new MemoryStream(compressedBytes);
            using var def = new DeflateStream(ms, CompressionMode.Decompress);
            using var br = new BinaryReader(def, Encoding.UTF8);

            byte version = br.ReadByte();
            int count = br.ReadInt32();
            var list = new List<ComposedShape>(Math.Clamp(count, 0, 100_000));
            for (int i = 0; i < count; i++)
            {
                string prst = br.ReadString();
                float x = br.ReadSingle();
                float y = br.ReadSingle();
                float w = br.ReadSingle();
                float h = br.ReadSingle();
                string fill = br.ReadString();
                short rot = br.ReadInt16();
                float sw = br.ReadSingle();

                short ptCount = br.ReadInt16();
                List<(double X, double Y)>? pts = null;
                if (ptCount > 0)
                {
                    pts = new List<(double X, double Y)>(ptCount);
                    for (int p = 0; p < ptCount; p++)
                    {
                        pts.Add((br.ReadSingle(), br.ReadSingle()));
                    }
                }

                string text = br.ReadString();
                string textColor = br.ReadString();

                list.Add(new ComposedShape
                {
                    Prst = prst,
                    X = x,
                    Y = y,
                    W = w,
                    H = h,
                    Fill = fill,
                    Rot = rot,
                    StrokeWidthPt = sw,
                    PathPoints = pts,
                    Text = string.IsNullOrEmpty(text) ? null : text,
                    TextColor = string.IsNullOrEmpty(textColor) ? null : textColor
                });
            }
            return list;
        }

        /// <summary>True when <paramref name="m"/> lies entirely inside any outer match.</summary>
        private static bool ContainedIn(Match m, params Match[] outer)
        {
            foreach (var o in outer)
            {
                if (o.Success && m.Index >= o.Index && m.Index + m.Length <= o.Index + o.Length) return true;
            }
            return false;
        }

        private sealed class SpanStartComparer : IComparer<(int Index, int Length)>
        {
            public static readonly SpanStartComparer Instance = new();
            public int Compare((int Index, int Length) x, (int Index, int Length) y) => x.Index.CompareTo(y.Index);
        }

        public static string Format(ComposedShape s)
        {
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{s.Prst,-14} {s.X:F2} {s.Y:F2} {s.W:F2} {s.H:F2} {s.Fill.TrimStart('#')}");
            if (s.Rot != 0) line += $" rot={s.Rot}";
            if (s.PathPoints is { Count: >= 2 })
            {
                line += " pts=\"";
                line += string.Join(";", s.PathPoints.Select(p =>
                    p.X.ToString("F1", CultureInfo.InvariantCulture) + "," +
                    p.Y.ToString("F1", CultureInfo.InvariantCulture)));
                line += $"\" sw={s.StrokeWidthPt.ToString("F1", CultureInfo.InvariantCulture)}";
            }
            if (!string.IsNullOrWhiteSpace(s.Text))
            {
                string safeText = s.Text
                    .Replace("&", "&amp;")
                    .Replace("\"", "&quot;")
                    .Replace("\r", "")
                    .Replace("\n", "&#10;");
                line += " text=\"" + safeText + "\"";
            }
            if (!string.IsNullOrWhiteSpace(s.TextColor))
            {
                line += $" tcolor={s.TextColor.TrimStart('#').ToUpperInvariant()}";
            }
            return line;
        }

        public static (double Width, double Height) CanvasSize(IEnumerable<ComposedShape> shapes)
        {
            double w = 2, h = 2;
            foreach (var s in shapes)
            {
                w = Math.Max(w, s.X + s.W);
                h = Math.Max(h, s.Y + s.H);
            }
            return (w + 0.5, h + 0.5);
        }

        /// <summary>
        /// Simplifies redundant collinear points in polyline vector strokes (Ramer-Douglas-Peucker).
        /// Reduces serialized markdown size and SVG/DrawingML vertex count while preserving exact geometry.
        /// </summary>
        public static List<(double X, double Y)> SimplifyPolyline(IReadOnlyList<(double X, double Y)> points, double epsilon = 0.5)
        {
            if (points == null || points.Count <= 2) return points != null ? new List<(double X, double Y)>(points) : new List<(double X, double Y)>();

            double maxDist = 0;
            int maxIdx = 0;
            var start = points[0];
            var end = points[points.Count - 1];

            for (int i = 1; i < points.Count - 1; i++)
            {
                double dist = PerpendicularDistance(points[i], start, end);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    maxIdx = i;
                }
            }

            if (maxDist > epsilon)
            {
                var left = SimplifyPolyline(points.Take(maxIdx + 1).ToList(), epsilon);
                var right = SimplifyPolyline(points.Skip(maxIdx).ToList(), epsilon);

                var merged = new List<(double X, double Y)>(left);
                merged.AddRange(right.Skip(1));
                return merged;
            }

            return new List<(double X, double Y)> { start, end };
        }

        private static double PerpendicularDistance((double X, double Y) pt, (double X, double Y) lineStart, (double X, double Y) lineEnd)
        {
            double dx = lineEnd.X - lineStart.X;
            double dy = lineEnd.Y - lineStart.Y;
            double mag = Math.Sqrt(dx * dx + dy * dy);
            if (mag < 1e-6)
            {
                double px = pt.X - lineStart.X;
                double py = pt.Y - lineStart.Y;
                return Math.Sqrt(px * px + py * py);
            }

            double u = ((pt.X - lineStart.X) * dx + (pt.Y - lineStart.Y) * dy) / (mag * mag);
            double ix = lineStart.X + u * dx;
            double iy = lineStart.Y + u * dy;
            double rx = pt.X - ix;
            double ry = pt.Y - iy;
            return Math.Sqrt(rx * rx + ry * ry);
        }
    }
}
