using System.Globalization;
using System.Text.RegularExpressions;
using MdToPdf.Models;

namespace MdToPdf.Services.Mermaid;

/// <summary>
/// Renders the mermaid "chart" family — pie, gantt, quadrantChart and xychart(-beta) — as pure
/// geometry (an <see cref="MDiagram"/> in points, origin top-left). No OOXML here; the shape
/// emitter turns the result into native Word shapes.
/// </summary>
public sealed class MermaidChartsRenderer : IMermaidRenderer
{
    public bool CanRender(string diagramType) => diagramType.ToLowerInvariant() switch
    {
        "pie" or "gantt" or "quadrantchart" or "xychart-beta" or "xychart" => true,
        _ => false,
    };

    public MDiagram Render(string source, ThemeDefinition theme)
    {
        var lines = Preprocess(source);
        if (lines.Count == 0)
            throw new MermaidParseException("Empty mermaid source.");

        string head = FirstWord(lines[0]).ToLowerInvariant();
        return head switch
        {
            "pie" => RenderPie(lines, theme),
            "gantt" => RenderGantt(lines, theme),
            "quadrantchart" => RenderQuadrant(lines, theme),
            "xychart-beta" or "xychart" => RenderXyChart(lines, theme),
            _ => throw new MermaidParseException($"Unsupported chart type '{head}'."),
        };
    }

    // ============================================================ shared helpers

    private static List<string> Preprocess(string source)
    {
        var result = new List<string>();
        foreach (var raw in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = StripComment(raw).Trim();
            if (line.Length > 0) result.Add(line);
        }
        return result;
    }

    /// <summary>Removes a %% line comment, honouring double-quoted strings.</summary>
    private static string StripComment(string line)
    {
        bool inQuote = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            char c = line[i];
            if (c == '"') inQuote = !inQuote;
            else if (!inQuote && c == '%' && line[i + 1] == '%') return line[..i];
        }
        return line;
    }

    private static string FirstWord(string line)
    {
        int i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        return line[..i];
    }

    private static double TextW(string s, double fontSize) => s.Length * fontSize * 0.62 + 4;

    private static double ParseNum(string s)
    {
        if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            throw new MermaidParseException($"Cannot parse number '{s}'.");
        return v;
    }

    private static string FormatNum(double v)
    {
        double r = Math.Round(v, 4);
        return r == Math.Floor(r)
            ? ((long)r).ToString(CultureInfo.InvariantCulture)
            : r.ToString("0.####", CultureInfo.InvariantCulture);
    }

    // -------- palette: 8 distinct fills rotated in hue from theme.Heading / theme.Primary

    private static string[] BuildPalette(ThemeDefinition theme)
    {
        var palette = new string[8];
        for (int i = 0; i < 8; i++)
        {
            string baseHex = (i % 2 == 0) ? theme.Heading : theme.Primary;
            palette[i] = RotateHue(baseHex, (i / 2) * 52.0 + (i % 2) * 14.0);
        }
        return palette;
    }

    private static string RotateHue(string hex, double degrees)
    {
        var (r, g, b) = ParseHex(hex);
        var (h, s, l) = RgbToHsl(r, g, b);
        if (s < 0.18) s = 0.45;              // grays cannot rotate — give them chroma first
        l = Math.Clamp(l, 0.28, 0.72);       // keep fills legible on white/dark
        h = (h + degrees) % 360.0;
        if (h < 0) h += 360.0;
        var (nr, ng, nb) = HslToRgb(h, s, l);
        return $"#{nr:X2}{ng:X2}{nb:X2}";
    }

    private static (int r, int g, int b) ParseHex(string? hex)
    {
        string h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            return (0x44, 0x72, 0xC4); // safe fallback blue
        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    private static (double h, double s, double l) RgbToHsl(int ri, int gi, int bi)
    {
        double r = ri / 255.0, g = gi / 255.0, b = bi / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) * 60.0;
        else if (max == g) h = ((b - r) / d + 2) * 60.0;
        else h = ((r - g) / d + 4) * 60.0;
        return (h, s, l);
    }

    private static (int r, int g, int b) HslToRgb(double h, double s, double l)
    {
        double C(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        double hh = h / 360.0;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        int r = (int)Math.Round(C(p, q, hh + 1.0 / 3) * 255);
        int g = (int)Math.Round(C(p, q, hh) * 255);
        int b = (int)Math.Round(C(p, q, hh - 1.0 / 3) * 255);
        return (Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }

    // ============================================================ PIE

    private static MDiagram RenderPie(List<string> lines, ThemeDefinition theme)
    {
        bool showData = false;
        string? title = null;
        var data = new List<(string Label, double Value)>();
        var dataRx = new Regex("^\"(?<label>[^\"]*)\"\\s*:\\s*(?<val>[-+0-9.eE]+)\\s*$");

        // header: "pie [showData] [title ...]" — title/showData may also sit on later lines
        string header = lines[0][3..].Trim();
        while (header.Length > 0)
        {
            if (header.StartsWith("showData", StringComparison.OrdinalIgnoreCase))
            {
                showData = true;
                header = header["showData".Length..].Trim();
            }
            else if (header.StartsWith("title", StringComparison.OrdinalIgnoreCase))
            {
                title = header[5..].Trim();
                header = "";
            }
            else header = ""; // unknown trailing token — ignore
        }

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("title", StringComparison.OrdinalIgnoreCase) &&
                (line.Length == 5 || char.IsWhiteSpace(line[5])))
            {
                title = line[5..].Trim();
                continue;
            }
            if (line.Equals("showData", StringComparison.OrdinalIgnoreCase)) { showData = true; continue; }
            var m = dataRx.Match(line);
            if (!m.Success) continue; // unknown line inside a known chart — skip
            double v = ParseNum(m.Groups["val"].Value);
            if (v > 0) data.Add((m.Groups["label"].Value, v));
        }

        if (data.Count == 0)
            throw new MermaidParseException("pie chart has no data rows.");

        var palette = BuildPalette(theme);
        var d = new MDiagram();

        const double margin = 16, pieD = 200;
        double topY = margin;
        double total = data.Sum(x => x.Value);

        // legend measurements first so we can size the title box across the full canvas
        string LegendText(int i)
        {
            var (label, value) = data[i];
            return showData ? $"{label} ({FormatNum(value)}, {Math.Round(value / total * 100)}%)" : label;
        }
        double legendTextW = 0;
        for (int i = 0; i < data.Count; i++) legendTextW = Math.Max(legendTextW, TextW(LegendText(i), 9));
        double legendX = margin + pieD + 24;
        double canvasW = legendX + 14 + legendTextW + margin;

        if (!string.IsNullOrEmpty(title))
        {
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = margin, Y = topY, W = canvasW - 2 * margin, H = 18,
                Text = title, Bold = true, FontSize = 13, TextColor = theme.Text,
            });
            topY += 26;
        }

        double pieX = margin, pieY = topY;

        // wedges — mermaid measures degrees clockwise from 12 o'clock; the emitter's 0° is at
        // 3 o'clock (clockwise), so shift every angle by -90°. All wedges share one bounding square.
        double cum = 0;
        for (int i = 0; i < data.Count; i++)
        {
            double startDeg = cum / total * 360.0;
            cum += data[i].Value;
            double endDeg = cum / total * 360.0;
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Pie,
                X = pieX, Y = pieY, W = pieD, H = pieD,
                AdjStartDeg = startDeg - 90.0,
                AdjEndDeg = endDeg - 90.0,
                Fill = palette[i % palette.Length],
                Stroke = theme.Background,
                StrokeWidth = 1,
            });
        }

        // legend: color chip + label per entry, vertically centred on the pie
        double entryH = 16;
        double legendH = data.Count * entryH;
        double legendY = Math.Max(pieY, pieY + (pieD - legendH) / 2);
        for (int i = 0; i < data.Count; i++)
        {
            double ey = legendY + i * entryH;
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Rect, X = legendX, Y = ey + 2, W = 10, H = 10,
                Fill = palette[i % palette.Length], Stroke = theme.Line, StrokeWidth = 0.75,
            });
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = legendX + 14, Y = ey, W = legendTextW, H = 14,
                Text = LegendText(i), FontSize = 9, TextColor = theme.Text,
            });
        }

        d.Width = canvasW;
        d.Height = Math.Max(pieY + pieD, legendY + legendH) + margin;
        return d;
    }

    // ============================================================ GANTT

    private sealed class GanttTask
    {
        public string Name = "";
        public string? Section;
        public DateTime Start;
        public DateTime End;
        public bool Crit, Active, Done, Milestone;
    }

    private static MDiagram RenderGantt(List<string> lines, ThemeDefinition theme)
    {
        string? title = null;
        string dateFormat = "YYYY-MM-DD";
        string? currentSection = null;
        var tasks = new List<GanttTask>();
        var idEnd = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        DateTime? lastEnd = null;

        static bool StartsWithWord(string line, string word) =>
            line.StartsWith(word, StringComparison.OrdinalIgnoreCase) &&
            (line.Length == word.Length || char.IsWhiteSpace(line[word.Length]));

        foreach (var line in lines.Skip(1))
        {
            if (StartsWithWord(line, "title")) { title = line[5..].Trim(); continue; }
            if (StartsWithWord(line, "dateFormat")) { dateFormat = line[10..].Trim(); continue; }
            if (StartsWithWord(line, "axisFormat") || StartsWithWord(line, "tickInterval") ||
                StartsWithWord(line, "excludes") || StartsWithWord(line, "includes") ||
                StartsWithWord(line, "todayMarker") || StartsWithWord(line, "inclusiveEndDates") ||
                StartsWithWord(line, "weekday") || StartsWithWord(line, "displayMode"))
                continue; // recognised but not rendered
            if (StartsWithWord(line, "section")) { currentSection = line[7..].Trim(); continue; }

            int colon = line.IndexOf(':');
            if (colon <= 0) continue; // unknown line — skip

            string name = line[..colon].Trim();
            var fields = line[(colon + 1)..].Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
            if (fields.Count == 0) continue;

            var task = new GanttTask { Name = name, Section = currentSection };
            string? id = null;

            // leading tags in any order
            int fi = 0;
            while (fi < fields.Count)
            {
                string f = fields[fi].ToLowerInvariant();
                if (f == "crit") task.Crit = true;
                else if (f == "active") task.Active = true;
                else if (f == "done") task.Done = true;
                else if (f == "milestone") task.Milestone = true;
                else break;
                fi++;
            }
            var rest = fields.Skip(fi).ToList();

            bool IsDateTok(string t) => TryParseGanttDate(t, dateFormat, out _);
            bool IsAfterTok(string t) => t.StartsWith("after ", StringComparison.OrdinalIgnoreCase);
            bool IsDurTok(string t) => Regex.IsMatch(t, @"^\d+(\.\d+)?(ms|s|m|h|d|w)$", RegexOptions.IgnoreCase);

            // optional task id: the first remaining token when it is not a date / "after x" / duration
            if (rest.Count >= 2 && !IsDateTok(rest[0]) && !IsAfterTok(rest[0]) && !IsDurTok(rest[0]))
            {
                id = rest[0];
                rest.RemoveAt(0);
            }
            if (rest.Count == 0) continue;

            // start
            DateTime start;
            int consumed = 1;
            if (IsAfterTok(rest[0]))
            {
                DateTime s = DateTime.MinValue;
                foreach (var refId in rest[0][6..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (idEnd.TryGetValue(refId, out var e) && e > s) s = e;
                start = s == DateTime.MinValue ? (lastEnd ?? DateTime.Today) : s;
            }
            else if (TryParseGanttDate(rest[0], dateFormat, out var sd))
            {
                start = sd;
            }
            else if (IsDurTok(rest[0]))
            {
                start = lastEnd ?? DateTime.Today; // duration-only task chains after the previous one
                consumed = 0;
            }
            else continue; // unparsable task — skip

            // end
            DateTime end;
            var tail = rest.Skip(consumed).ToList();
            if (tail.Count == 0)
            {
                end = task.Milestone ? start : start.AddDays(1);
            }
            else if (TryParseGanttDate(tail[0], dateFormat, out var ed))
            {
                end = ed;
            }
            else if (IsDurTok(tail[0]))
            {
                end = start.Add(ParseGanttDuration(tail[0]));
            }
            else
            {
                end = task.Milestone ? start : start.AddDays(1);
            }
            if (end < start) end = start;

            task.Start = start;
            task.End = end;
            tasks.Add(task);
            lastEnd = end;
            if (id != null) idEnd[id] = end;
        }

        if (tasks.Count == 0)
            throw new MermaidParseException("gantt chart has no tasks.");

        DateTime min = tasks.Min(t => t.Start);
        DateTime max = tasks.Max(t => t.End);
        if (max <= min) max = min.AddDays(1);
        double totalDays = (max - min).TotalDays;

        const double plotW = 460, rowH = 22, sectionH = 20, axisH = 26, margin = 12;
        double leftColW = tasks.Max(t => t.Name.Length) * 5.8 + 12;
        double plotX = margin + leftColW;
        double topY = margin;

        var d = new MDiagram();
        double canvasW = plotX + plotW + margin;

        if (!string.IsNullOrEmpty(title))
        {
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = margin, Y = topY, W = canvasW - 2 * margin, H = 18,
                Text = title, Bold = true, FontSize = 13, TextColor = theme.Text,
            });
            topY += 26;
        }

        double axisY = topY + axisH;          // baseline of the time axis
        double rowsTop = axisY + 4;

        // rows: section headers + tasks, grouped in source order
        double y = rowsTop;
        var rowY = new double[tasks.Count];
        string? sec = null;
        double rowsBottom;
        for (int i = 0; i < tasks.Count; i++)
        {
            if (tasks[i].Section != null && tasks[i].Section != sec)
            {
                sec = tasks[i].Section;
                d.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.Text, X = margin, Y = y + 2, W = leftColW + plotW - 8, H = 14,
                    Text = sec, Bold = true, FontSize = 10, TextColor = theme.Text,
                });
                y += sectionH;
            }
            rowY[i] = y;
            y += rowH;
        }
        rowsBottom = y;

        // time axis: line + weekly or monthly ticks (≤ 10)
        d.Connectors.Add(new MConnector
        {
            X1 = plotX, Y1 = axisY, X2 = plotX + plotW, Y2 = axisY,
            StartHead = ArrowHead.None, EndHead = ArrowHead.None, Stroke = theme.Line, StrokeWidth = 1,
        });

        double XOf(DateTime t) => plotX + (t - min).TotalDays / totalDays * plotW;

        var ticks = new List<(DateTime At, string Label)>();
        if (totalDays / 7.0 <= 10.0)
        {
            for (var t = min.Date; t <= max; t = t.AddDays(7))
                ticks.Add((t, t.ToString("MM-dd", CultureInfo.InvariantCulture)));
        }
        else
        {
            int months = (max.Year - min.Year) * 12 + (max.Month - min.Month) + 1;
            int step = Math.Max(1, (int)Math.Ceiling(months / 10.0));
            for (var t = new DateTime(min.Year, min.Month, 1); t <= max; t = t.AddMonths(step))
                if (t >= min.Date) ticks.Add((t, t.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
            if (ticks.Count == 0) ticks.Add((min.Date, min.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
        }
        foreach (var (at, label) in ticks)
        {
            double tx = XOf(at);
            if (tx < plotX - 0.01 || tx > plotX + plotW + 0.01) continue;
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = tx - 22, Y = axisY - 16, W = 44, H = 12,
                Text = label, FontSize = 7.5, TextColor = theme.Text,
            });
            d.Connectors.Add(new MConnector
            {
                X1 = tx, Y1 = axisY, X2 = tx, Y2 = rowsBottom,
                StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                Stroke = theme.Line, StrokeWidth = 0.5, Dashed = true,
            });
        }

        // task rows
        for (int i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            double ry = rowY[i];

            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = margin + 2, Y = ry + 4, W = leftColW - 8, H = 14,
                Text = t.Name, FontSize = 9, TextColor = theme.Text,
            });

            string fill = t.Crit ? "#D9534F"
                        : t.Done ? "#999999"
                        : t.Active ? theme.Primary
                        : theme.Heading;

            if (t.Milestone)
            {
                double mx = XOf(t.Start) + (XOf(t.End) - XOf(t.Start)) / 2;
                d.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.Diamond, X = mx - 7, Y = ry + (rowH - 14) / 2, W = 14, H = 14,
                    Fill = fill, Stroke = theme.Line, StrokeWidth = 1,
                });
            }
            else
            {
                double bx = XOf(t.Start);
                double bw = Math.Max(2, XOf(t.End) - bx);
                d.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.RoundRect, X = bx, Y = ry + 5, W = bw, H = 12,
                    Fill = fill, Stroke = theme.Line, StrokeWidth = 0.75,
                });
            }
        }

        d.Width = canvasW;
        d.Height = rowsBottom + margin;
        return d;
    }

    private static bool TryParseGanttDate(string s, string mermaidFormat, out DateTime result)
    {
        string net = mermaidFormat
            .Replace("YYYY", "yyyy").Replace("YY", "yy")
            .Replace("DD", "dd").Replace("D", "d")
            .Replace("SSS", "fff");
        if (DateTime.TryParseExact(s, net, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return true;
        return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result)
            || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static TimeSpan ParseGanttDuration(string s)
    {
        var m = Regex.Match(s, @"^(\d+(?:\.\d+)?)(ms|s|m|h|d|w)$", RegexOptions.IgnoreCase);
        if (!m.Success) return TimeSpan.FromDays(1);
        double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            "w" => TimeSpan.FromDays(n * 7),
            _ => TimeSpan.FromDays(n),
        };
    }

    // ============================================================ QUADRANT

    private static MDiagram RenderQuadrant(List<string> lines, ThemeDefinition theme)
    {
        string? title = null;
        string xLow = "", xHigh = "", yLow = "", yHigh = "";
        var quadLabels = new string?[4];
        var points = new List<(string Name, double X, double Y)>();
        var pointRx = new Regex(@"^(?<name>[^:\[\]]+?)\s*:\s*\[\s*(?<x>[-+0-9.eE]+)\s*,\s*(?<y>[-+0-9.eE]+)\s*\]");
        var axisRx = new Regex(@"^(?<low>.*?)\s*-->\s*(?<high>.*)$");

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("title", StringComparison.OrdinalIgnoreCase) &&
                (line.Length == 5 || char.IsWhiteSpace(line[5])))
            { title = line[5..].Trim(); continue; }

            if (line.StartsWith("x-axis", StringComparison.OrdinalIgnoreCase))
            {
                var m = axisRx.Match(line[6..].Trim());
                if (m.Success) { xLow = m.Groups["low"].Value.Trim(); xHigh = m.Groups["high"].Value.Trim(); }
                else xLow = line[6..].Trim();
                continue;
            }
            if (line.StartsWith("y-axis", StringComparison.OrdinalIgnoreCase))
            {
                var m = axisRx.Match(line[6..].Trim());
                if (m.Success) { yLow = m.Groups["low"].Value.Trim(); yHigh = m.Groups["high"].Value.Trim(); }
                else yLow = line[6..].Trim();
                continue;
            }
            var qm = Regex.Match(line, @"^quadrant-(?<n>[1-4])\s+(?<label>.+)$", RegexOptions.IgnoreCase);
            if (qm.Success)
            {
                quadLabels[int.Parse(qm.Groups["n"].Value) - 1] = qm.Groups["label"].Value.Trim();
                continue;
            }
            var pm = pointRx.Match(line);
            if (pm.Success)
            {
                points.Add((pm.Groups["name"].Value.Trim(),
                    Math.Clamp(ParseNum(pm.Groups["x"].Value), 0, 1),
                    Math.Clamp(ParseNum(pm.Groups["y"].Value), 0, 1)));
            }
            // unknown line — skip
        }

        const double side = 380;
        double padLeft = Math.Max(24, Math.Max(TextW(yLow, 9), TextW(yHigh, 9)) + 8);
        double padTop = string.IsNullOrEmpty(title) ? 16 : 42;
        double plotX = padLeft, plotY = padTop;

        var d = new MDiagram();

        if (!string.IsNullOrEmpty(title))
        {
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = plotX, Y = 12, W = side, H = 18,
                Text = title, Bold = true, FontSize = 13, TextColor = theme.Text,
            });
        }

        // outer frame + mid split lines
        d.Shapes.Add(new MShape
        {
            Kind = ShapeKind.Frame, X = plotX, Y = plotY, W = side, H = side,
            Stroke = theme.Line, StrokeWidth = 1.25,
        });
        d.Connectors.Add(new MConnector
        {
            X1 = plotX + side / 2, Y1 = plotY, X2 = plotX + side / 2, Y2 = plotY + side,
            StartHead = ArrowHead.None, EndHead = ArrowHead.None, Stroke = theme.Line, StrokeWidth = 0.75,
        });
        d.Connectors.Add(new MConnector
        {
            X1 = plotX, Y1 = plotY + side / 2, X2 = plotX + side, Y2 = plotY + side / 2,
            StartHead = ArrowHead.None, EndHead = ArrowHead.None, Stroke = theme.Line, StrokeWidth = 0.75,
        });

        // quadrant labels: q1 top-right, q2 top-left, q3 bottom-left, q4 bottom-right
        var quadPos = new (double qx, double qy)[]
        {
            (plotX + side / 2, plotY),              // 1: top right
            (plotX, plotY),                          // 2: top left
            (plotX, plotY + side / 2),               // 3: bottom left
            (plotX + side / 2, plotY + side / 2),    // 4: bottom right
        };
        for (int q = 0; q < 4; q++)
        {
            if (string.IsNullOrEmpty(quadLabels[q])) continue;
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text,
                X = quadPos[q].qx, Y = quadPos[q].qy + side / 4 - 7, W = side / 2, H = 14,
                Text = quadLabels[q], FontSize = 9, TextColor = theme.Line,
            });
        }

        // axis end labels
        if (xLow.Length > 0)
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = plotX, Y = plotY + side + 6, W = TextW(xLow, 9), H = 13,
                Text = xLow, FontSize = 9, TextColor = theme.Text,
            });
        if (xHigh.Length > 0)
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = plotX + side - TextW(xHigh, 9), Y = plotY + side + 6,
                W = TextW(xHigh, 9), H = 13, Text = xHigh, FontSize = 9, TextColor = theme.Text,
            });
        if (yHigh.Length > 0)
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = plotX - TextW(yHigh, 9) - 4, Y = plotY, W = TextW(yHigh, 9), H = 13,
                Text = yHigh, FontSize = 9, TextColor = theme.Text,
            });
        if (yLow.Length > 0)
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = plotX - TextW(yLow, 9) - 4, Y = plotY + side - 13,
                W = TextW(yLow, 9), H = 13, Text = yLow, FontSize = 9, TextColor = theme.Text,
            });

        // points: x grows right, y grows UP → invert Y for the top-left origin
        foreach (var (name, px, py) in points)
        {
            double cx = plotX + px * side;
            double cy = plotY + (1.0 - py) * side;
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Circle, X = cx - 4, Y = cy - 4, W = 8, H = 8,
                Fill = theme.Primary, Stroke = theme.Primary, StrokeWidth = 0.75,
            });
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = cx + 6, Y = cy - 6, W = TextW(name, 8), H = 12,
                Text = name, FontSize = 8, TextColor = theme.Text,
            });
        }

        d.Width = plotX + side + Math.Max(16, points.Count > 0 ? points.Max(p => TextW(p.Name, 8)) + 12 : 16);
        d.Height = plotY + side + 24;
        return d;
    }

    // ============================================================ XYCHART

    private sealed class XySeries
    {
        public bool IsBar;
        public List<double> Values = new();
    }

    private static MDiagram RenderXyChart(List<string> lines, ThemeDefinition theme)
    {
        string? title = null, xTitle = null, yTitle = null;
        List<string>? categories = null;
        double? xMinNum = null, xMaxNum = null;
        double? yMinGiven = null, yMaxGiven = null;
        var series = new List<XySeries>();

        var rangeRx = new Regex(@"^(?<a>[-+0-9.eE]+)\s*-->\s*(?<b>[-+0-9.eE]+)$");

        static (string? quoted, string rest) TakeQuoted(string s)
        {
            s = s.Trim();
            if (s.StartsWith('"'))
            {
                int end = s.IndexOf('"', 1);
                if (end > 0) return (s[1..end], s[(end + 1)..].Trim());
            }
            return (null, s);
        }

        static List<string> ParseBracketList(string s)
        {
            int open = s.IndexOf('['), close = s.LastIndexOf(']');
            if (open < 0 || close <= open) return new List<string>();
            return s[(open + 1)..close].Split(',')
                .Select(t => t.Trim().Trim('"').Trim())
                .Where(t => t.Length > 0)
                .ToList();
        }

        foreach (var line in lines.Skip(1))
        {
            if (line.Equals("horizontal", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("vertical", StringComparison.OrdinalIgnoreCase))
                continue; // orientation keyword — only vertical layout is produced

            if (line.StartsWith("title", StringComparison.OrdinalIgnoreCase) &&
                (line.Length == 5 || char.IsWhiteSpace(line[5])))
            {
                title = line[5..].Trim().Trim('"');
                continue;
            }
            if (line.StartsWith("x-axis", StringComparison.OrdinalIgnoreCase))
            {
                var (quoted, rest) = TakeQuoted(line[6..]);
                if (quoted != null) xTitle = quoted;
                if (rest.StartsWith('['))
                {
                    categories = ParseBracketList(rest);
                }
                else
                {
                    var m = rangeRx.Match(rest);
                    if (m.Success) { xMinNum = ParseNum(m.Groups["a"].Value); xMaxNum = ParseNum(m.Groups["b"].Value); }
                    else if (rest.Length > 0 && quoted == null) xTitle = rest.Trim('"');
                }
                continue;
            }
            if (line.StartsWith("y-axis", StringComparison.OrdinalIgnoreCase))
            {
                var (quoted, rest) = TakeQuoted(line[6..]);
                if (quoted != null) yTitle = quoted;
                var m = rangeRx.Match(rest);
                if (m.Success) { yMinGiven = ParseNum(m.Groups["a"].Value); yMaxGiven = ParseNum(m.Groups["b"].Value); }
                else if (rest.Length > 0 && quoted == null) yTitle = rest.Trim('"');
                continue;
            }
            if (line.StartsWith("bar", StringComparison.OrdinalIgnoreCase) &&
                (line.Length == 3 || line[3] == ' ' || line[3] == '['))
            {
                var vals = ParseBracketList(line[3..]);
                if (vals.Count > 0)
                    series.Add(new XySeries { IsBar = true, Values = vals.Select(ParseNum).ToList() });
                continue;
            }
            if (line.StartsWith("line", StringComparison.OrdinalIgnoreCase) &&
                (line.Length == 4 || line[4] == ' ' || line[4] == '['))
            {
                var vals = ParseBracketList(line[4..]);
                if (vals.Count > 0)
                    series.Add(new XySeries { IsBar = false, Values = vals.Select(ParseNum).ToList() });
                continue;
            }
            // unknown line — skip
        }

        if (series.Count == 0)
            throw new MermaidParseException("xychart has no bar/line series.");

        int n = series.Max(s => s.Values.Count);
        if (categories == null || categories.Count == 0)
        {
            categories = new List<string>();
            for (int i = 0; i < n; i++)
            {
                categories.Add(xMinNum.HasValue && xMaxNum.HasValue && n > 1
                    ? FormatNum(xMinNum.Value + i * (xMaxNum.Value - xMinNum.Value) / (n - 1))
                    : (i + 1).ToString(CultureInfo.InvariantCulture));
            }
        }
        n = Math.Max(n, categories.Count);
        while (categories.Count < n) categories.Add((categories.Count + 1).ToString(CultureInfo.InvariantCulture));

        // y-range: explicit wins (kept exact); otherwise nice-rounded from the data
        double dataMin = series.SelectMany(s => s.Values).DefaultIfEmpty(0).Min();
        double dataMax = series.SelectMany(s => s.Values).DefaultIfEmpty(1).Max();
        double yMin, yMax, tickStep;
        if (yMinGiven.HasValue && yMaxGiven.HasValue && yMaxGiven.Value > yMinGiven.Value)
        {
            yMin = yMinGiven.Value;
            yMax = yMaxGiven.Value;
            tickStep = (yMax - yMin) / 4.0;
        }
        else
        {
            double lo = Math.Min(0, dataMin), hi = Math.Max(dataMax, lo + 1e-9);
            if (hi <= lo) hi = lo + 1;
            tickStep = NiceStep((hi - lo) / 4.0);
            yMin = Math.Floor(lo / tickStep) * tickStep;
            yMax = Math.Ceiling(hi / tickStep) * tickStep;
            if (yMax <= yMin) yMax = yMin + tickStep;
        }

        const double plotW = 420, plotH = 220, marginR = 20, xLabelH = 16;
        var yTickLabels = new List<(double V, string Label)>();
        for (double v = yMin; v <= yMax + tickStep * 1e-6; v += tickStep)
            yTickLabels.Add((v, FormatNum(v)));
        double yLabW = yTickLabels.Max(t => TextW(t.Label, 8));
        double plotX = 12 + (yTitle != null ? 14 : 0) + yLabW + 8;
        double topY = 12;

        var d = new MDiagram();
        double canvasW = plotX + plotW + marginR;

        if (!string.IsNullOrEmpty(title))
        {
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = 12, Y = topY, W = canvasW - 24, H = 18,
                Text = title, Bold = true, FontSize = 13, TextColor = theme.Text,
            });
            topY += 26;
        }

        double plotY = topY;
        double plotBottom = plotY + plotH;

        double YOf(double v) => plotBottom - (v - yMin) / (yMax - yMin) * plotH;

        // axes
        d.Connectors.Add(new MConnector
        {
            X1 = plotX, Y1 = plotY, X2 = plotX, Y2 = plotBottom,
            StartHead = ArrowHead.None, EndHead = ArrowHead.None, Stroke = theme.Line, StrokeWidth = 1.25,
        });
        d.Connectors.Add(new MConnector
        {
            X1 = plotX, Y1 = plotBottom, X2 = plotX + plotW, Y2 = plotBottom,
            StartHead = ArrowHead.None, EndHead = ArrowHead.None, Stroke = theme.Line, StrokeWidth = 1.25,
        });

        // y ticks + labels + light gridlines
        foreach (var (v, label) in yTickLabels)
        {
            double ty = YOf(v);
            d.Connectors.Add(new MConnector
            {
                X1 = plotX - 4, Y1 = ty, X2 = plotX, Y2 = ty,
                StartHead = ArrowHead.None, EndHead = ArrowHead.None, Stroke = theme.Line, StrokeWidth = 1,
            });
            if (ty < plotBottom - 0.5)
                d.Connectors.Add(new MConnector
                {
                    X1 = plotX, Y1 = ty, X2 = plotX + plotW, Y2 = ty,
                    StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                    Stroke = theme.Line, StrokeWidth = 0.5, Dashed = true,
                });
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = plotX - 8 - yLabW, Y = ty - 6, W = yLabW, H = 12,
                Text = label, FontSize = 8, TextColor = theme.Text,
            });
        }

        // x category labels
        double slot = plotW / n;
        for (int i = 0; i < n; i++)
        {
            double cx = plotX + i * slot + slot / 2;
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = cx - slot / 2, Y = plotBottom + 4, W = slot, H = 12,
                Text = categories[i], FontSize = 8, TextColor = theme.Text,
            });
        }

        // axis titles
        if (!string.IsNullOrEmpty(xTitle))
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = plotX, Y = plotBottom + 4 + xLabelH, W = plotW, H = 14,
                Text = xTitle, FontSize = 9, Bold = true, TextColor = theme.Text,
            });
        if (!string.IsNullOrEmpty(yTitle))
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = 4, Y = plotY - 2, W = Math.Max(20, TextW(yTitle, 8)), H = 12,
                Text = yTitle, FontSize = 8, Bold = true, TextColor = theme.Text,
            });

        var palette = BuildPalette(theme);
        double baseline = YOf(Math.Clamp(0, yMin, yMax));
        var barSeries = series.Where(s => s.IsBar).ToList();
        double groupW = slot * 0.6;
        double barW = barSeries.Count > 0 ? groupW / barSeries.Count : 0;

        int colorIdx = 0;
        int barIdx = 0;
        foreach (var s in series)
        {
            string color = palette[colorIdx++ % palette.Length];
            if (s.IsBar)
            {
                for (int i = 0; i < s.Values.Count && i < n; i++)
                {
                    double v = Math.Clamp(s.Values[i], yMin, yMax);
                    double vy = YOf(v);
                    double byTop = Math.Min(vy, baseline);
                    double bh = Math.Max(1, Math.Abs(baseline - vy));
                    double bx = plotX + i * slot + (slot - groupW) / 2 + barIdx * barW;
                    d.Shapes.Add(new MShape
                    {
                        Kind = ShapeKind.Rect, X = bx, Y = byTop, W = Math.Max(1, barW - 1), H = bh,
                        Fill = color, Stroke = theme.Line, StrokeWidth = 0.5,
                    });
                }
                barIdx++;
            }
            else
            {
                var pts = new List<(double X, double Y)>();
                for (int i = 0; i < s.Values.Count && i < n; i++)
                {
                    double v = Math.Clamp(s.Values[i], yMin, yMax);
                    pts.Add((plotX + i * slot + slot / 2, YOf(v)));
                }
                for (int i = 0; i + 1 < pts.Count; i++)
                    d.Connectors.Add(new MConnector
                    {
                        X1 = pts[i].X, Y1 = pts[i].Y, X2 = pts[i + 1].X, Y2 = pts[i + 1].Y,
                        StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                        Stroke = color, StrokeWidth = 1.75,
                    });
                foreach (var (px, py) in pts)
                    d.Shapes.Add(new MShape
                    {
                        Kind = ShapeKind.Circle, X = px - 2.5, Y = py - 2.5, W = 5, H = 5,
                        Fill = color, Stroke = color, StrokeWidth = 0.5,
                    });
            }
        }

        d.Width = canvasW;
        d.Height = plotBottom + 4 + xLabelH + (xTitle != null ? 16 : 0) + 8;
        return d;
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10;
        return nice * mag;
    }
}
