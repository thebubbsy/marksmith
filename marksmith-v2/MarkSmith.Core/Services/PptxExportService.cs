using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using static MarkSmith.Services.Presentation.SlideGeometry;

namespace MarkSmith.Services;

// PowerPoint export. SlideDeckBuilder turns the Markdown into a paginated deck (title slide,
// section dividers, content slides that continue when a section is too long); this class draws
// it as a native, editable PowerPoint file: a slide master with real text styles and three
// layouts (Title Slide, Title and Content, Section Header), placeholders for titles and body
// text, real bullets and numbering, tables as PowerPoint tables, code on a themed panel with
// syntax colours, pictures and diagrams as pictures, quotes and alerts on panels, links that
// open, and slide numbers. Colours and fonts come from the selected Marksmith theme and the
// branding font.
public sealed class PptxExportService
{
    public const string Extension = "pptx";

    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string Ns = $"xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\"";

    // Shared AppServices.Themes singleton instead of a private instance (see DocxExportService).
    private static ThemeCatalog Themes => AppServices.Themes;

    /// <param name="mermaidPngs">Rendered PNGs of the document's ```mermaid fences, in order
    /// (MermaidHarvestService.RenderMermaidPngsAsync). Without them a diagram is shown as its
    /// labelled source.</param>
    public Task ExportAsync(string markdown, string pptxPath, AppSettings settings,
        IReadOnlyList<byte[]?>? mermaidPngs = null) => Task.Run(() =>
    {
        markdown = TextNormalizer.Newlines(markdown);
        if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
        markdown = FormattingService.Apply(markdown, settings);

        var deck = SlideDeckBuilder.Build(markdown, new SlideDeckOptions
        {
            FallbackTitle = HistoryEntry.ExtractTitle(markdown) ?? "Marksmith",
            AuthorName = settings.AuthorName,
            MermaidPngs = mermaidPngs,
            NoEmoji = settings.NoEmoji,
            DrawDiagrams = settings.MermaidEnabled,
        });
        var palette = new Palette(Themes.GetOrDefault(settings.Theme), settings.BrandFontFamily);

        var dir = Path.GetDirectoryName(pptxPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(pptxPath)) File.Delete(pptxPath);

        using var doc = PresentationDocument.Create(pptxPath, DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
        // Metadata parity with the DOCX exporter: creator from the user's author name, low-key
        // product attribution in the Company field, never rendered on a slide.
        doc.PackageProperties.Title = deck.Title;
        doc.PackageProperties.Creator = settings.AuthorName;
        doc.PackageProperties.Subject = ExportBranding.CreatedIn;
        ExportBranding.SetCompany(doc);
        doc.PackageProperties.Created = DateTime.UtcNow;
        doc.PackageProperties.Modified = DateTime.UtcNow;

        var presPart = doc.AddPresentationPart();
        var masterPart = presPart.AddNewPart<SlideMasterPart>("rIdMaster");
        var themePart = masterPart.AddNewPart<ThemePart>("rIdTheme");
        themePart.Theme = new A.Theme(ThemeXml(palette));

        var layouts = new Dictionary<SlideKind, SlideLayoutPart>();
        var layoutDefs = new (SlideKind Kind, string Id, string Xml)[]
        {
            (SlideKind.Title, "rIdLayout1", TitleLayoutXml()),
            (SlideKind.Content, "rIdLayout2", ContentLayoutXml()),
            (SlideKind.Section, "rIdLayout3", SectionLayoutXml()),
        };
        foreach (var (kind, id, xml) in layoutDefs)
        {
            var part = masterPart.AddNewPart<SlideLayoutPart>(id);
            part.SlideLayout = new P.SlideLayout(xml);
            part.AddPart(masterPart, "rIdMaster");
            layouts[kind] = part;
        }
        masterPart.SlideMaster = new P.SlideMaster(MasterXml(palette, layoutDefs.Select(l => l.Id).ToList()));

        var logo = LoadLogo(settings.BrandLogoPath);
        for (int i = 0; i < deck.Slides.Count; i++)
        {
            var slidePart = presPart.AddNewPart<SlidePart>($"rIdSlide{i + 1}");
            slidePart.AddPart(layouts[deck.Slides[i].Kind], "rIdLayout");
            var writer = new SlideWriter(slidePart, palette, deck, i);
            slidePart.Slide = new P.Slide(writer.Write(deck.Slides[i], i == 0 ? logo : null));
        }

        presPart.AddNewPart<PresentationPropertiesPart>("rIdPresProps").PresentationProperties =
            new P.PresentationProperties($"<p:presentationPr {Ns}/>");
        presPart.AddNewPart<ViewPropertiesPart>("rIdViewProps").ViewProperties =
            new P.ViewProperties($"<p:viewPr {Ns}><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>");
        presPart.AddNewPart<TableStylesPart>("rIdTableStyles").TableStyleList =
            new A.TableStyleList($"<a:tblStyleLst xmlns:a=\"{NsA}\" def=\"{{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}}\"/>");

        presPart.Presentation = new P.Presentation(PresentationXml(deck.Slides.Count));
    });

    // ── palette ──────────────────────────────────────────────────────────────

    internal sealed class Palette
    {
        public readonly string Background, Text, Heading, Accent, Muted, CodeBackground, CodeText, Border, Band, Panel, Link;
        public readonly string HeaderFill, HeaderText;
        public readonly string MajorFont, MinorFont;
        public readonly ThemeDefinition Theme;

        public Palette(ThemeDefinition t, string? brandFont)
        {
            Theme = t;
            Background = Hex(t.Background);
            Text = Hex(ContrastGuard.EnsureLegibleText(Hex(t.Text), Background));
            Heading = Hex(ContrastGuard.EnsureLegibleText(Hex(t.Heading), Background, Text));
            Accent = Hex(ContrastGuard.EnsureVisibleFill(Hex(t.Primary), Background));
            Link = Hex(ContrastGuard.EnsureLegibleText(Hex(t.Primary), Background, Heading));
            Muted = Mix(Text, Background, 0.32);
            // ThemeDefinition.Code is the code BACKGROUND.
            CodeBackground = Hex(t.Code);
            CodeText = Hex(ContrastGuard.EnsureLegibleText(Text, CodeBackground, ThemeDefinition.IsLight("#" + CodeBackground) ? "1F2328" : "E6EDF3"));
            Border = Hex(t.Border);
            Band = Mix(Background, Text, 0.05);
            Panel = Mix(Background, Text, 0.045);
            HeaderFill = Accent;
            HeaderText = Hex(ContrastGuard.EnsureLegibleText("FFFFFF", HeaderFill, "111111"));
            var font = string.IsNullOrWhiteSpace(brandFont) ? null : brandFont.Trim();
            MajorFont = font ?? "Calibri Light";
            MinorFont = font ?? "Calibri";
        }
    }

    internal static string Hex(string css)
    {
        var s = (css ?? "").Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(c => $"{c}{c}"));
        if (s.Length < 6 || !s[..6].All(Uri.IsHexDigit)) return "000000";
        return s[..6].ToUpperInvariant();
    }

    internal static string Mix(string a, string b, double t)
    {
        int C(string h, int i) => int.Parse(h.Substring(i, 2), NumberStyles.HexNumber);
        string Part(int i) => ((int)Math.Round(C(a, i) * (1 - t) + C(b, i) * t)).ToString("X2");
        return Part(0) + Part(2) + Part(4);
    }

    // ── package-level parts ──────────────────────────────────────────────────

    private static string PresentationXml(int slideCount)
    {
        var sb = new StringBuilder($"<p:presentation {Ns} saveSubsetFonts=\"1\">");
        sb.Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rIdMaster\"/></p:sldMasterIdLst>");
        sb.Append("<p:sldIdLst>");
        for (int i = 0; i < slideCount; i++) sb.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rIdSlide{i + 1}\"/>");
        sb.Append("</p:sldIdLst>");
        sb.Append($"<p:sldSz cx=\"{SlideWidth}\" cy=\"{SlideHeight}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>");
        sb.Append("</p:presentation>");
        return sb.ToString();
    }

    private static string ThemeXml(Palette p)
    {
        string C(string v) => $"<a:srgbClr val=\"{v}\"/>";
        var a2 = Hex(p.Theme.Line);
        var sb = new StringBuilder($"<a:theme xmlns:a=\"{NsA}\" name=\"Marksmith\"><a:themeElements>");
        sb.Append("<a:clrScheme name=\"Marksmith\">");
        sb.Append($"<a:dk1>{C(p.Text)}</a:dk1><a:lt1>{C(p.Background)}</a:lt1><a:dk2>{C(p.Heading)}</a:dk2><a:lt2>{C(Hex(p.Theme.Secondary))}</a:lt2>");
        sb.Append($"<a:accent1>{C(p.Accent)}</a:accent1><a:accent2>{C(a2)}</a:accent2><a:accent3>{C(p.Heading)}</a:accent3>");
        sb.Append($"<a:accent4>{C(p.Border)}</a:accent4><a:accent5>{C(p.CodeBackground)}</a:accent5><a:accent6>{C(p.Muted)}</a:accent6>");
        sb.Append($"<a:hlink>{C(p.Link)}</a:hlink><a:folHlink>{C(p.Link)}</a:folHlink></a:clrScheme>");
        sb.Append($"<a:fontScheme name=\"Marksmith\"><a:majorFont><a:latin typeface=\"{X(p.MajorFont)}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>");
        sb.Append($"<a:minorFont><a:latin typeface=\"{X(p.MinorFont)}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>");
        sb.Append("<a:fmtScheme name=\"Marksmith\"><a:fillStyleLst>");
        for (int i = 0; i < 3; i++) sb.Append("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>");
        sb.Append("</a:fillStyleLst><a:lnStyleLst>");
        foreach (var w in new[] { 6350, 12700, 19050 })
            sb.Append($"<a:ln w=\"{w}\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>");
        sb.Append("</a:lnStyleLst><a:effectStyleLst>");
        for (int i = 0; i < 3; i++) sb.Append("<a:effectStyle><a:effectLst/></a:effectStyle>");
        sb.Append("</a:effectStyleLst><a:bgFillStyleLst>");
        for (int i = 0; i < 3; i++) sb.Append("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>");
        sb.Append("</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>");
        return sb.ToString();
    }

    private const string GroupHeader =
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static string Xfrm(double x, double y, double w, double h) =>
        $"<a:xfrm><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(Math.Max(1, w))}\" cy=\"{Emu(Math.Max(1, h))}\"/></a:xfrm>";

    private static string Placeholder(int id, string name, string ph, double x, double y, double w, double h, string anchor = "b") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr>" +
        $"<p:spPr>{Xfrm(x, y, w, h)}</p:spPr>" +
        $"<p:txBody><a:bodyPr anchor=\"{anchor}\"><a:normAutofit/></a:bodyPr><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>";

    private static string MasterXml(Palette p, List<string> layoutIds)
    {
        var sb = new StringBuilder($"<p:sldMaster {Ns}><p:cSld><p:bg><p:bgPr><a:solidFill><a:schemeClr val=\"bg1\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree>");
        sb.Append(GroupHeader);
        sb.Append(Placeholder(2, "Title Placeholder 1", "<p:ph type=\"title\"/>", MarginXPt, TitleTopPt, ContentWidthPt, TitleHeightPt));
        sb.Append(Placeholder(3, "Text Placeholder 2", "<p:ph type=\"body\" idx=\"1\"/>", MarginXPt, BodyTopPt, ContentWidthPt, BodyHeightPt, "t"));
        sb.Append("</p:spTree></p:cSld>");
        sb.Append("<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>");
        sb.Append("<p:sldLayoutIdLst>");
        for (int i = 0; i < layoutIds.Count; i++) sb.Append($"<p:sldLayoutId id=\"{2147483649u + (uint)i}\" r:id=\"{layoutIds[i]}\"/>");
        sb.Append("</p:sldLayoutIdLst>");

        // Text styles, so slides added in PowerPoint look like the exported ones.
        sb.Append("<p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc><a:buNone/>");
        sb.Append("<a:defRPr sz=\"3200\" b=\"1\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/></a:defRPr></a:lvl1pPr></p:titleStyle>");
        sb.Append("<p:bodyStyle>");
        var chars = new[] { "•", "–", "▪", "–", "•" };
        for (int lvl = 0; lvl < 5; lvl++)
        {
            var marL = Emu(BulletIndentPt * (lvl + 1));
            var size = lvl == 0 ? 2000 : 1800;
            sb.Append($"<a:lvl{lvl + 1}pPr marL=\"{marL}\" indent=\"{-Emu(BulletIndentPt)}\"><a:lnSpc><a:spcPct val=\"100000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"800\"/></a:spcBef>");
            sb.Append($"<a:buClr><a:schemeClr val=\"accent1\"/></a:buClr><a:buFont typeface=\"Arial\"/><a:buChar char=\"{chars[lvl]}\"/>");
            sb.Append($"<a:defRPr sz=\"{size}\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl{lvl + 1}pPr>");
        }
        sb.Append("</p:bodyStyle><p:otherStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr>");
        sb.Append("<a:lvl1pPr><a:defRPr sz=\"1800\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:otherStyle></p:txStyles>");
        sb.Append("</p:sldMaster>");
        return sb.ToString();
    }

    private static string LayoutXml(string type, string name, string shapes) =>
        $"<p:sldLayout {Ns} type=\"{type}\" preserve=\"1\"><p:cSld name=\"{name}\"><p:spTree>{GroupHeader}{shapes}</p:spTree></p:cSld>" +
        "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    // Title slide geometry, shared by the layout and the slides that use it.
    private const double CoverTitleTopPt = 150, CoverTitleHeightPt = 130, CoverBarTopPt = 292, CoverSubtitleTopPt = 308, CoverSubtitleHeightPt = 80;
    private const double SectionTitleTopPt = 196, SectionTitleHeightPt = 110, SectionBarTopPt = 316;

    private static string TitleLayoutXml() => LayoutXml("title", "Title Slide",
        Placeholder(2, "Title 1", "<p:ph type=\"ctrTitle\"/>", MarginXPt, CoverTitleTopPt, ContentWidthPt, CoverTitleHeightPt) +
        Placeholder(3, "Subtitle 2", "<p:ph type=\"subTitle\" idx=\"1\"/>", MarginXPt, CoverSubtitleTopPt, ContentWidthPt, CoverSubtitleHeightPt, "t"));

    private static string ContentLayoutXml() => LayoutXml("obj", "Title and Content",
        Placeholder(2, "Title 1", "<p:ph type=\"title\"/>", MarginXPt, TitleTopPt, ContentWidthPt, TitleHeightPt) +
        Placeholder(3, "Content Placeholder 2", "<p:ph idx=\"1\"/>", MarginXPt, BodyTopPt, ContentWidthPt, BodyHeightPt, "t"));

    private static string SectionLayoutXml() => LayoutXml("secHead", "Section Header",
        Placeholder(2, "Title 1", "<p:ph type=\"title\"/>", MarginXPt, SectionTitleTopPt, ContentWidthPt, SectionTitleHeightPt) +
        Placeholder(3, "Text Placeholder 2", "<p:ph type=\"body\" idx=\"1\"/>", MarginXPt, SectionBarTopPt + 16, ContentWidthPt, 60, "t"));

    // ── brand logo ───────────────────────────────────────────────────────────

    private sealed record Logo(byte[] Data, string ContentType, double WidthPt, double HeightPt);

    private static Logo? LoadLogo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(bytes));
            if (codec is null) return null;
            var type = codec.EncodedFormat switch
            {
                SkiaSharp.SKEncodedImageFormat.Png => "image/png",
                SkiaSharp.SKEncodedImageFormat.Jpeg => "image/jpeg",
                _ => null,
            };
            if (type is null) return null;
            double w = codec.Info.Width * 0.75, h = codec.Info.Height * 0.75;
            var scale = Math.Min(1, Math.Min(200 / Math.Max(1, w), 54 / Math.Max(1, h)));
            return new Logo(bytes, type, w * scale, h * scale);
        }
        catch { return null; }
    }

    // ── XML text helpers ─────────────────────────────────────────────────────

    /// <summary>XML-escapes text and drops characters XML can't carry (control codes).</summary>
    internal static string X(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\t': sb.Append("    "); break;
                default:
                    if (ch < 0x20 || ch is '￾' or '￿') continue;
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Pts(double pt) => ((int)Math.Round(pt * 100)).ToString(CultureInfo.InvariantCulture);

    // ── one slide ────────────────────────────────────────────────────────────

    private sealed class SlideWriter
    {
        private readonly SlidePart _part;
        private readonly Palette _p;
        private readonly PptxDeck _deck;
        private readonly int _index;
        private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
        private int _nextId = 2;
        private int _tables, _pictures, _codes;

        public SlideWriter(SlidePart part, Palette palette, PptxDeck deck, int index)
        {
            _part = part;
            _p = palette;
            _deck = deck;
            _index = index;
        }

        public string Write(DeckSlide slide, Logo? logo)
        {
            var sb = new StringBuilder($"<p:sld {Ns}><p:cSld><p:spTree>");
            sb.Append(GroupHeader);
            switch (slide.Kind)
            {
                case SlideKind.Title: WriteTitleSlide(sb, slide, logo); break;
                case SlideKind.Section: WriteSectionSlide(sb, slide); break;
                default: WriteContentSlide(sb, slide); break;
            }
            sb.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            return sb.ToString();
        }

        private int Id() => _nextId++;

        private void WriteTitleSlide(StringBuilder sb, DeckSlide slide, Logo? logo)
        {
            if (logo is not null)
            {
                var rel = AddImage(logo.Data, logo.ContentType);
                sb.Append(Picture(rel, "Logo", "Logo", MarginXPt, 40, logo.WidthPt, logo.HeightPt));
            }
            var size = slide.Title.Length > 60 ? 36 : slide.Title.Length > 32 ? 42 : 48;
            sb.Append(TitleShape("<p:ph type=\"ctrTitle\"/>", "Title 1", slide.Title, size, MarginXPt, CoverTitleTopPt, ContentWidthPt, CoverTitleHeightPt, "b"));
            sb.Append(Rect("Accent", MarginXPt, CoverBarTopPt, 96, 5, _p.Accent));

            var lines = new List<(string Text, double Size, string Color)>();
            if (!string.IsNullOrWhiteSpace(slide.Subtitle)) lines.Add((slide.Subtitle!, 22, _p.Muted));
            if (!string.IsNullOrWhiteSpace(slide.Byline)) lines.Add((slide.Byline!, 16, _p.Text));
            if (lines.Count > 0)
            {
                var body = new StringBuilder();
                for (int i = 0; i < lines.Count; i++)
                {
                    var (text, sz, color) = lines[i];
                    body.Append($"<a:p><a:pPr marL=\"0\" indent=\"0\">{(i > 0 ? "<a:spcBef><a:spcPts val=\"1000\"/></a:spcBef>" : "")}<a:buNone/></a:pPr>");
                    body.Append($"<a:r><a:rPr lang=\"en-US\" sz=\"{Pts(sz)}\" dirty=\"0\"><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:rPr><a:t>{X(text)}</a:t></a:r></a:p>");
                }
                sb.Append(Shape(Id(), "Subtitle 2", "<p:ph type=\"subTitle\" idx=\"1\"/>", MarginXPt, CoverSubtitleTopPt, ContentWidthPt, CoverSubtitleHeightPt + 60,
                    "", body.ToString(), anchor: "t", placeholder: true));
            }
        }

        private void WriteSectionSlide(StringBuilder sb, DeckSlide slide)
        {
            var size = slide.Title.Length > 50 ? 34 : 40;
            sb.Append(TitleShape("<p:ph type=\"title\"/>", "Title 1", slide.Title, size, MarginXPt, SectionTitleTopPt, ContentWidthPt, SectionTitleHeightPt, "b"));
            sb.Append(Rect("Accent", MarginXPt, SectionBarTopPt, 96, 5, _p.Accent));
            WriteFooter(sb);
        }

        private void WriteContentSlide(StringBuilder sb, DeckSlide slide)
        {
            sb.Append(TitleShape("<p:ph type=\"title\"/>", "Title 1", slide.Title, TitleFont(slide.Title), MarginXPt, TitleTopPt, ContentWidthPt, TitleHeightPt, "b"));
            sb.Append(Rect("Accent", MarginXPt, AccentBarTopPt, 56, 3, _p.Accent));

            double y = BodyTopPt;
            // A slide that is just one picture centres it in the body area.
            if (slide.Blocks.Count == 1 && slide.Blocks[0] is PictureSlideBlock or MissingPictureBlock)
                y += Math.Max(0, (BodyHeightPt - slide.Blocks[0].HeightPt) / 2);

            bool bodyUsed = false;
            for (int i = 0; i < slide.Blocks.Count; i++)
            {
                if (i > 0) y += BlockGapPt;
                var block = slide.Blocks[i];
                switch (block)
                {
                    case TextBlock tb:
                        WriteText(sb, tb, y, usePlaceholder: !bodyUsed && tb.Panel == PanelKind.None);
                        if (tb.Panel == PanelKind.None) bodyUsed = true;
                        break;
                    case CodeSlideBlock code: WriteCode(sb, code, y); break;
                    case TableSlideBlock table: WriteTable(sb, table, y); break;
                    case PictureSlideBlock pic: WritePicture(sb, pic, y); break;
                    case MissingPictureBlock missing: WriteMissing(sb, missing, y); break;
                }
                y += block.HeightPt;
            }
            WriteFooter(sb);
        }

        private void WriteFooter(StringBuilder sb)
        {
            var title = $"<a:p><a:pPr><a:buNone/></a:pPr><a:r>{RPr(11, _p.Muted, font: "+mn-lt")}<a:t>{X(_deck.Title)}</a:t></a:r></a:p>";
            sb.Append(Shape(Id(), "Footer", "", MarginXPt, FooterTopPt, ContentWidthPt - 80, 20, "", title, anchor: "ctr"));
            var number = $"<a:p><a:pPr algn=\"r\"><a:buNone/></a:pPr><a:fld id=\"{{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}}\" type=\"slidenum\">{RPr(11, _p.Muted, font: "+mn-lt")}<a:t>{_index + 1}</a:t></a:fld></a:p>";
            sb.Append(Shape(Id(), "Slide Number", "", MarginXPt + ContentWidthPt - 72, FooterTopPt, 72, 20, "", number, anchor: "ctr"));
        }

        // ── text ──

        private string TitleShape(string ph, string name, string text, double size, double x, double y, double w, double h, string anchor)
        {
            var para = $"<a:p><a:r><a:rPr lang=\"en-US\" sz=\"{Pts(size)}\" b=\"1\" dirty=\"0\"><a:solidFill><a:srgbClr val=\"{_p.Heading}\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/></a:rPr><a:t>{X(text)}</a:t></a:r></a:p>";
            return Shape(Id(), name, ph, x, y, w, h, "", para, anchor: anchor, placeholder: true);
        }

        private void WriteText(StringBuilder sb, TextBlock tb, double y, bool usePlaceholder)
        {
            var italic = tb.Panel == PanelKind.Quote;
            var color = tb.Panel == PanelKind.Quote ? _p.Muted : _p.Text;
            var paras = new StringBuilder();
            for (int i = 0; i < tb.Paragraphs.Count; i++)
                paras.Append(Paragraph(tb.Paragraphs[i], i == 0, tb.FontScale, color, italic));

            if (tb.Panel == PanelKind.None)
            {
                sb.Append(Shape(Id(), usePlaceholder ? "Content Placeholder 2" : "Text", usePlaceholder ? "<p:ph idx=\"1\"/>" : "",
                    MarginXPt, y, ContentWidthPt, tb.HeightPt, "", paras.ToString(), anchor: "t", placeholder: usePlaceholder));
                return;
            }

            var accent = tb.Panel == PanelKind.Alert && tb.PanelColor is { } c ? c : _p.Accent;
            var fill = tb.Panel == PanelKind.Alert
                ? $"<a:solidFill><a:srgbClr val=\"{accent}\"><a:alpha val=\"12000\"/></a:srgbClr></a:solidFill>"
                : $"<a:solidFill><a:srgbClr val=\"{_p.Panel}\"/></a:solidFill>";
            sb.Append(Shape(Id(), tb.Panel == PanelKind.Alert ? "Callout" : "Quote", "", MarginXPt, y, ContentWidthPt, tb.HeightPt, fill + "<a:ln><a:noFill/></a:ln>", "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>", geometry: "rect"));
            sb.Append(Rect("Bar", MarginXPt, y, 4, tb.HeightPt, accent));
            sb.Append(Shape(Id(), "Text", "", MarginXPt + 4 + PanelPadXPt, y + PanelPadYPt, TextWidth(tb), tb.HeightPt - 2 * PanelPadYPt, "", paras.ToString(), anchor: "t"));
        }

        private string Paragraph(SlideParagraph para, bool first, double scale, string color, bool italic)
        {
            var size = FontSize(para) * scale;
            var sb = new StringBuilder("<a:p>");
            var marL = Emu(LeftIndent(para));
            var before = ParagraphSpaceBefore(para, first) * scale;
            var spc = $"<a:lnSpc><a:spcPct val=\"100000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"{Pts(before)}\"/></a:spcBef>";
            switch (para.Style)
            {
                case ParaStyle.Bullet:
                {
                    var ch = (para.Level % 3) switch { 0 => "•", 1 => "–", _ => "▪" };
                    sb.Append($"<a:pPr marL=\"{marL}\" indent=\"{-Emu(BulletIndentPt)}\" lvl=\"{para.Level}\">{spc}<a:buClr><a:srgbClr val=\"{_p.Accent}\"/></a:buClr><a:buFont typeface=\"Arial\"/><a:buChar char=\"{ch}\"/></a:pPr>");
                    break;
                }
                case ParaStyle.Numbered:
                {
                    var scheme = (para.Level % 3) switch { 0 => "arabicPeriod", 1 => "alphaLcPeriod", _ => "romanLcPeriod" };
                    sb.Append($"<a:pPr marL=\"{marL}\" indent=\"{-Emu(BulletIndentPt + 6)}\" lvl=\"{para.Level}\">{spc}<a:buClr><a:srgbClr val=\"{_p.Accent}\"/></a:buClr><a:buFont typeface=\"+mj-lt\"/><a:buAutoNum type=\"{scheme}\" startAt=\"{Math.Max(1, para.Number)}\"/></a:pPr>");
                    break;
                }
                case ParaStyle.Task:
                    sb.Append($"<a:pPr marL=\"{marL}\" indent=\"{-Emu(BulletIndentPt)}\" lvl=\"{para.Level}\">{spc}<a:buClr><a:srgbClr val=\"{(para.Checked ? _p.Accent : _p.Muted)}\"/></a:buClr><a:buFont typeface=\"Segoe UI Symbol\"/><a:buChar char=\"{(para.Checked ? "☑" : "☐")}\"/></a:pPr>");
                    break;
                case ParaStyle.Continuation:
                    sb.Append($"<a:pPr marL=\"{marL}\" indent=\"0\" lvl=\"{para.Level}\">{spc}<a:buNone/></a:pPr>");
                    break;
                case ParaStyle.Math:
                    sb.Append($"<a:pPr marL=\"0\" indent=\"0\" algn=\"ctr\">{spc}<a:buNone/></a:pPr>");
                    break;
                default:
                    sb.Append($"<a:pPr marL=\"0\" indent=\"0\">{spc}<a:buNone/></a:pPr>");
                    break;
            }

            var runColor = para.Style switch
            {
                ParaStyle.Subheading => _p.Heading,
                ParaStyle.Caption => _p.Muted,
                ParaStyle.Task when para.Checked => _p.Muted,
                _ => color,
            };
            foreach (var run in para.Runs) sb.Append(Run(run, size, runColor, italic || para.Style == ParaStyle.Caption, para.Style == ParaStyle.Math));
            sb.Append($"<a:endParaRPr lang=\"en-US\" sz=\"{Pts(size)}\" dirty=\"0\"/></a:p>");
            return sb.ToString();
        }

        private string Run(TextRun run, double size, string color, bool italic, bool math)
        {
            if (run.LineBreak) return $"<a:br>{RPr(size, color)}</a:br>";
            if (run.Text.Length == 0) return "";
            var font = run.Code ? "Consolas" : run.Math || math ? "Cambria Math" : "+mn-lt";
            var link = run.Url is { } url ? LinkId(url) : null;
            var rpr = RPr(size, link is null ? run.Color ?? color : null, font, run.Bold, run.Italic || italic, run.Underline, run.Strike, run.Baseline, link);
            return $"<a:r>{rpr}<a:t>{X(run.Text)}</a:t></a:r>";
        }

        private static string RPr(double size, string? color, string font = "+mn-lt", bool bold = false, bool italic = false,
            bool underline = false, bool strike = false, int baseline = 0, string? linkId = null)
        {
            var sb = new StringBuilder($"<a:rPr lang=\"en-US\" sz=\"{Pts(size)}\"");
            if (bold) sb.Append(" b=\"1\"");
            if (italic) sb.Append(" i=\"1\"");
            if (underline) sb.Append(" u=\"sng\"");
            if (strike) sb.Append(" strike=\"sngStrike\"");
            if (baseline != 0) sb.Append($" baseline=\"{baseline}\"");
            sb.Append(" dirty=\"0\">");
            if (color is not null) sb.Append($"<a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill>");
            sb.Append($"<a:latin typeface=\"{X(font)}\"/>");
            if (font != "+mn-lt") sb.Append($"<a:cs typeface=\"{X(font)}\"/>");
            if (linkId is not null) sb.Append($"<a:hlinkClick r:id=\"{linkId}\"/>");
            sb.Append("</a:rPr>");
            return sb.ToString();
        }

        private string? LinkId(string url)
        {
            if (_links.TryGetValue(url, out var id)) return id;
            try
            {
                id = _part.AddHyperlinkRelationship(new Uri(url, UriKind.Absolute), true).Id;
                _links[url] = id;
                return id;
            }
            catch { return null; }
        }

        // ── code ──

        private void WriteCode(StringBuilder sb, CodeSlideBlock code, double y)
        {
            _codes++;
            var captionH = CaptionHeight(code.Caption);
            if (captionH > 0)
            {
                var cap = $"<a:p><a:pPr><a:buNone/></a:pPr><a:r>{RPr(CaptionFontPt, _p.Muted, italic: true)}<a:t>{X(code.Caption)}</a:t></a:r></a:p>";
                sb.Append(Shape(Id(), "Caption", "", MarginXPt, y, ContentWidthPt, captionH, "", cap, anchor: "t"));
            }
            var font = CodeFont(code);
            var paras = new StringBuilder();
            var text = string.Join("\n", code.Lines);
            var line = new StringBuilder();
            void Flush()
            {
                paras.Append("<a:p><a:pPr marL=\"0\" indent=\"0\"><a:lnSpc><a:spcPct val=\"100000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"0\"/></a:spcBef><a:buNone/></a:pPr>");
                paras.Append(line);
                paras.Append($"<a:endParaRPr lang=\"en-US\" sz=\"{Pts(font)}\" dirty=\"0\"/></a:p>");
                line.Clear();
            }
            foreach (var (span, hex, it, bold) in OpenXmlSyntaxHighlighter.GetHighlightedSpans(text, code.Language, _p.CodeBackground))
            {
                var pieces = span.Split('\n');
                for (int i = 0; i < pieces.Length; i++)
                {
                    if (i > 0) Flush();
                    if (pieces[i].Length == 0) continue;
                    var color = hex is null ? _p.CodeText : Hex(hex);
                    line.Append($"<a:r>{RPr(font, color, "Consolas", bold, it)}<a:t>{X(pieces[i])}</a:t></a:r>");
                }
            }
            Flush();

            var fill = $"<a:solidFill><a:srgbClr val=\"{_p.CodeBackground}\"/></a:solidFill><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"{_p.Border}\"/></a:solidFill></a:ln>";
            var insets = $" lIns=\"{Emu(CodePadXPt)}\" rIns=\"{Emu(CodePadXPt)}\" tIns=\"{Emu(CodePadYPt)}\" bIns=\"{Emu(CodePadYPt)}\"";
            sb.Append(Shape(Id(), $"Code {_codes}", "", MarginXPt, y + captionH, ContentWidthPt, code.HeightPt - captionH, fill, paras.ToString(),
                anchor: "t", geometry: "roundRect", insets: insets, adjust: "<a:gd name=\"adj\" fmla=\"val 3000\"/>"));
        }

        // ── tables ──

        private void WriteTable(StringBuilder sb, TableSlideBlock t, double y)
        {
            _tables++;
            var font = TableFont(t);
            var widths = t.Widths.Select(w => Emu(w * ContentWidthPt)).ToList();
            var totalW = widths.Sum();
            var tbl = new StringBuilder("<a:tbl><a:tblPr firstRow=\"1\" bandRow=\"1\"/><a:tblGrid>");
            foreach (var w in widths) tbl.Append($"<a:gridCol w=\"{w}\"/>");
            tbl.Append("</a:tblGrid>");

            double h = 0;
            void Row(IReadOnlyList<List<SlideParagraph>> cells, bool header, bool band)
            {
                var rh = RowHeight(t, cells);
                h += rh;
                tbl.Append($"<a:tr h=\"{Emu(rh)}\">");
                for (int c = 0; c < t.Columns; c++)
                {
                    var cell = c < cells.Count ? cells[c] : new List<SlideParagraph>();
                    var algn = t.Align[c] switch { CellAlign.Center => "ctr", CellAlign.Right => "r", _ => "l" };
                    tbl.Append("<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>");
                    if (cell.Count == 0) cell = new List<SlideParagraph> { new() { Style = ParaStyle.Body } };
                    foreach (var para in cell)
                    {
                        tbl.Append($"<a:p><a:pPr marL=\"0\" indent=\"0\" algn=\"{algn}\"><a:buNone/></a:pPr>");
                        foreach (var run in para.Runs) tbl.Append(Run(header ? run with { Bold = true } : run, font, header ? _p.HeaderText : _p.Text, false, false));
                        tbl.Append($"<a:endParaRPr lang=\"en-US\" sz=\"{Pts(font)}\" dirty=\"0\"/></a:p>");
                    }
                    tbl.Append("</a:txBody>");
                    tbl.Append($"<a:tcPr marL=\"{Emu(CellPadXPt)}\" marR=\"{Emu(CellPadXPt)}\" marT=\"{Emu(CellPadYPt)}\" marB=\"{Emu(CellPadYPt)}\" anchor=\"ctr\">");
                    tbl.Append("<a:lnL w=\"0\"><a:noFill/></a:lnL><a:lnR w=\"0\"><a:noFill/></a:lnR>");
                    tbl.Append($"<a:lnT w=\"9525\"><a:solidFill><a:srgbClr val=\"{_p.Border}\"/></a:solidFill></a:lnT>");
                    tbl.Append($"<a:lnB w=\"{(header ? 19050 : 9525)}\"><a:solidFill><a:srgbClr val=\"{(header ? _p.HeaderFill : _p.Border)}\"/></a:solidFill></a:lnB>");
                    tbl.Append(header ? $"<a:solidFill><a:srgbClr val=\"{_p.HeaderFill}\"/></a:solidFill>"
                        : band ? $"<a:solidFill><a:srgbClr val=\"{_p.Band}\"/></a:solidFill>" : "<a:noFill/>");
                    tbl.Append("</a:tcPr></a:tc>");
                }
                tbl.Append("</a:tr>");
            }
            if (t.Header.Count > 0) Row(t.Header, header: true, band: false);
            for (int r = 0; r < t.Rows.Count; r++) Row(t.Rows[r], header: false, band: r % 2 == 1);
            tbl.Append("</a:tbl>");

            sb.Append($"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{Id()}\" name=\"Table {_tables}\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>");
            sb.Append($"<p:xfrm><a:off x=\"{Emu(MarginXPt)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{totalW}\" cy=\"{Emu(h)}\"/></p:xfrm>");
            sb.Append("<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">");
            sb.Append(tbl);
            sb.Append("</a:graphicData></a:graphic></p:graphicFrame>");
        }

        // ── pictures ──

        private string AddImage(byte[] data, string contentType)
        {
            var type = contentType switch
            {
                "image/jpeg" => ImagePartType.Jpeg,
                "image/gif" => ImagePartType.Gif,
                "image/bmp" => ImagePartType.Bmp,
                _ => ImagePartType.Png,
            };
            var part = _part.AddImagePart(type);
            using (var ms = new MemoryStream(data)) part.FeedData(ms);
            return _part.GetIdOfPart(part);
        }

        private void WritePicture(StringBuilder sb, PictureSlideBlock pic, double y)
        {
            _pictures++;
            var (w, h) = FitPicture(pic, pic.HeightPt);
            var x = MarginXPt + (ContentWidthPt - w) / 2;
            var rel = AddImage(pic.Data, pic.ContentType);
            sb.Append(Picture(rel, pic.IsDiagram ? $"Diagram {_pictures}" : $"Picture {_pictures}", pic.Description, x, y, w, h));
            if (!string.IsNullOrEmpty(pic.Caption))
            {
                var cap = $"<a:p><a:pPr algn=\"ctr\"><a:buNone/></a:pPr><a:r>{RPr(CaptionFontPt, _p.Muted, italic: true)}<a:t>{X(pic.Caption)}</a:t></a:r></a:p>";
                sb.Append(Shape(Id(), "Caption", "", MarginXPt, y + h + 4, ContentWidthPt, CaptionHeight(pic.Caption), "", cap, anchor: "t"));
            }
        }

        private string Picture(string rel, string name, string description, double x, double y, double w, double h) =>
            $"<p:pic><p:nvPicPr><p:cNvPr id=\"{Id()}\" name=\"{X(name)}\" descr=\"{X(description)}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
            $"<p:blipFill><a:blip r:embed=\"{rel}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
            $"<p:spPr>{Xfrm(x, y, w, h)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>";

        private void WriteMissing(StringBuilder sb, MissingPictureBlock m, double y)
        {
            var file = Path.GetFileName((m.Source ?? "").Replace('/', '\\').Split('?', '#')[0]);
            var label = string.IsNullOrWhiteSpace(m.Description) ? "Image not found" : m.Description;
            var detail = file.Length > 0 ? $"Image not found: {file}" : "Image not found";
            var body = $"<a:p><a:pPr algn=\"ctr\"><a:buNone/></a:pPr><a:r>{RPr(16, _p.Text)}<a:t>{X(label)}</a:t></a:r></a:p>";
            if (!string.IsNullOrWhiteSpace(m.Description))
                body += $"<a:p><a:pPr algn=\"ctr\"><a:buNone/></a:pPr><a:r>{RPr(12, _p.Muted, italic: true)}<a:t>{X(detail)}</a:t></a:r></a:p>";
            var outline = $"<a:noFill/><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"{_p.Border}\"/></a:solidFill><a:prstDash val=\"dash\"/></a:ln>";
            var w = Math.Min(ContentWidthPt, 420);
            sb.Append(Shape(Id(), "Missing image", "", MarginXPt + (ContentWidthPt - w) / 2, y, w, m.HeightPt, outline, body,
                anchor: "ctr", geometry: "roundRect"));
        }

        // ── primitives ──

        private string Rect(string name, double x, double y, double w, double h, string color) =>
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"{Id()}\" name=\"{name}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
            $"<p:spPr>{Xfrm(x, y, w, h)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr>" +
            "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>";

        /// <summary>A shape with text. <paramref name="fill"/> is the spPr fill/line XML (empty =
        /// no fill, no line); a placeholder inherits its geometry kind from the layout.</summary>
        private static string Shape(int id, string name, string ph, double x, double y, double w, double h, string fill, string paragraphs,
            string anchor = "t", bool placeholder = false, string geometry = "rect", string? insets = null, string adjust = "")
        {
            var nv = placeholder
                ? $"<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr>"
                : $"<p:cNvSpPr txBox=\"{(fill.Length == 0 ? 1 : 0)}\"/><p:nvPr/>";
            var geom = placeholder ? "" : $"<a:prstGeom prst=\"{geometry}\"><a:avLst>{adjust}</a:avLst></a:prstGeom>";
            var fillXml = placeholder ? "" : fill.Length == 0 ? "<a:noFill/>" : fill;
            insets ??= " lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\"";
            return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{X(name)}\"/>{nv}</p:nvSpPr>" +
                   $"<p:spPr>{Xfrm(x, y, w, h)}{geom}{fillXml}</p:spPr>" +
                   $"<p:txBody><a:bodyPr wrap=\"square\"{insets} anchor=\"{anchor}\"><a:normAutofit/></a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";
        }
    }
}
