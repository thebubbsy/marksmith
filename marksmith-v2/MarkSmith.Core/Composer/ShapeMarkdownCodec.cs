using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MarkSmith.Core.Composer
{
    /// <summary>
    /// Encodes a shape composition as a :::shapes markdown block so designs round-trip
    /// through the editor, the main preview pane (SVG), and DOCX export (native DrawingML).
    ///
    /// Syntax (one shape per line, inches, # for comments):
    ///   :::shapes
    ///   ellipse 1.0 0.5 0.9 0.7 FFD9B3
    ///   heart   2.5 2.0 0.8 0.8 C0392B rot=15
    ///   :::
    /// </summary>
    public static class ShapeMarkdownCodec
    {
        public const string BlockTag = ":::shapes";

        public static List<ComposedShape> Parse(string innerContent)
        {
            var result = new List<ComposedShape>();
            if (string.IsNullOrWhiteSpace(innerContent)) return result;

            foreach (var raw in innerContent.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

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
                    Rot = rot
                });
            }

            return result;
        }

        public static string Serialize(IEnumerable<ComposedShape> shapes)
        {
            var sb = new StringBuilder();
            sb.AppendLine(BlockTag);
            foreach (var s in shapes)
            {
                sb.AppendLine(Format(s));
            }
            sb.AppendLine(":::");
            return sb.ToString();
        }

        public static string Format(ComposedShape s)
        {
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{s.Prst,-14} {s.X:F2} {s.Y:F2} {s.W:F2} {s.H:F2} {s.Fill.TrimStart('#')}");
            return s.Rot != 0 ? line + $" rot={s.Rot}" : line;
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
    }
}
