using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using MdToPdf.Models;

namespace MdToPdf.Services;

// PPTX export. Splits the Markdown on H1/H2 headings (each becomes a slide), lays the body out as
// bullet levels, and themes the deck from the selected Marksmith theme. Built with
// DocumentFormat.OpenXml by writing the presentation parts (theme / master / layout / slides) as
// well-formed OOXML, wired with explicit relationship ids. No external dependency.
public sealed class PptxExportService
{
    public const string Extension = "pptx";

    private static readonly ThemeCatalog Themes = new();

    public Task ExportAsync(string markdown, string pptxPath, AppSettings settings) => Task.Run(() =>
    {
        markdown = TextNormalizer.Newlines(markdown);
        if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);

        var theme = Themes.GetOrDefault(settings.Theme);
        var slides = BuildSlides(markdown, HistoryEntry.ExtractTitle(markdown) ?? "Marksmith");

        var dir = Path.GetDirectoryName(pptxPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(pptxPath)) File.Delete(pptxPath);

        using var doc = PresentationDocument.Create(pptxPath, DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
        var presPart = doc.AddPresentationPart();

        var masterPart = presPart.AddNewPart<SlideMasterPart>("rIdMaster");
        masterPart.AddNewPart<ThemePart>("rIdTheme");
        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>("rIdLayout");
        layoutPart.AddPart(masterPart, "rIdMasterFromLayout");

        WriteXml(masterPart.GetStream(FileMode.Create), MasterXml(theme));
        WriteXml(layoutPart.GetStream(FileMode.Create), LayoutXml());
        WriteXml(masterPart.ThemePart!.GetStream(FileMode.Create), ThemeXml(theme));

        var slideIds = new StringBuilder();
        for (int i = 0; i < slides.Count; i++)
        {
            var rId = $"rIdSlide{i + 1}";
            var slidePart = presPart.AddNewPart<SlidePart>(rId);
            slidePart.AddPart(layoutPart, "rIdLayoutFromSlide");
            WriteXml(slidePart.GetStream(FileMode.Create), SlideXml(slides[i], theme));
            slideIds.Append($"<p:sldId id=\"{256 + i}\" r:id=\"{rId}\"/>");
        }

        WriteXml(presPart.GetStream(FileMode.Create), PresentationXml(slideIds.ToString()));
    });

    private sealed record Slide(string Title, List<(int Level, string Text)> Bullets);

    // ---- markdown -> slides (headings split; lists/paragraphs become bullets) ----
    private static List<Slide> BuildSlides(string markdown, string deckTitle)
    {
        var slides = new List<Slide>();
        Slide? cur = null;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (h.Success)
            {
                var level = h.Groups[1].Value.Length;
                var text = Plain(h.Groups[2].Value);
                if (level <= 2) { cur = new Slide(text, new()); slides.Add(cur); }
                else { cur ??= NewSlide(deckTitle, slides); cur.Bullets.Add((0, text)); }
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (Regex.IsMatch(line, @"^\s*(```|~~~)")) continue; // skip code fences markers
            var bullet = Regex.Match(line, @"^(\s*)(?:[-*+]|\d+\.)\s+(.*)$");
            cur ??= NewSlide(deckTitle, slides);
            if (bullet.Success)
                cur.Bullets.Add((Math.Min(4, bullet.Groups[1].Value.Length / 2), Plain(bullet.Groups[2].Value)));
            else
                cur.Bullets.Add((0, Plain(line)));
        }
        if (slides.Count == 0) slides.Add(new Slide(deckTitle, new()));
        return slides;
    }

    private static Slide NewSlide(string title, List<Slide> slides) { var s = new Slide(title, new()); slides.Add(s); return s; }

    private static string Plain(string md) =>
        Regex.Replace(md, @"(\*\*|__|\*|_|`|~~)", "").Trim();

    private static void WriteXml(Stream stream, string xml)
    {
        using var s = stream;
        var bytes = Encoding.UTF8.GetBytes(xml);
        s.Write(bytes, 0, bytes.Length);
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Hex(string css) => css.TrimStart('#').ToUpperInvariant().PadLeft(6, '0')[..6];

    // ---- OOXML parts ----

    private static string PresentationXml(string slideIds) =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
          <p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rIdMaster"/></p:sldMasterIdLst>
          <p:sldIdLst>{slideIds}</p:sldIdLst>
          <p:sldSz cx="12192000" cy="6858000"/>
          <p:notesSz cx="6858000" cy="9144000"/>
        </p:presentation>
        """;

    private static string MasterXml(ThemeDefinition t) =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:sldMaster xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
          <p:cSld>
            <p:bg><p:bgPr><a:solidFill><a:srgbClr val="{Hex(t.Background)}"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>
            <p:spTree>
              <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
              <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
            </p:spTree>
          </p:cSld>
          <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
          <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rIdLayout"/></p:sldLayoutIdLst>
        </p:sldMaster>
        """;

    private static string LayoutXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:sldLayout xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" type="obj" preserve="1">
          <p:cSld name="Title and Content">
            <p:spTree>
              <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
              <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
            </p:spTree>
          </p:cSld>
          <p:clrMapOvr><a:overrideClrMapping bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/></p:clrMapOvr>
        </p:sldLayout>
        """;

    private static string SlideXml(Slide slide, ThemeDefinition t)
    {
        var body = new StringBuilder();
        if (slide.Bullets.Count == 0)
            body.Append($"<a:p><a:endParaRPr lang=\"en-US\"/></a:p>");
        foreach (var (level, text) in slide.Bullets)
            body.Append($"<a:p><a:pPr lvl=\"{level}\"/><a:r><a:rPr lang=\"en-US\" dirty=\"0\"><a:solidFill><a:srgbClr val=\"{Hex(t.Text)}\"/></a:solidFill></a:rPr><a:t>{Esc(text)}</a:t></a:r></a:p>");

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
          <p:cSld>
            <p:spTree>
              <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
              <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="685800" y="381000"/><a:ext cx="10820400" cy="1143000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="en-US" sz="3200" b="1" dirty="0"><a:solidFill><a:srgbClr val="{Hex(t.Heading)}"/></a:solidFill></a:rPr><a:t>{Esc(slide.Title)}</a:t></a:r></a:p></p:txBody>
              </p:sp>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="3" name="Content"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="685800" y="1600200"/><a:ext cx="10820400" cy="4800600"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                <p:txBody><a:bodyPr/><a:lstStyle/>{body}</p:txBody>
              </p:sp>
            </p:spTree>
          </p:cSld>
        </p:sld>
        """;
    }

    private static string ThemeXml(ThemeDefinition t)
    {
        string dk1 = Hex(t.Text), lt1 = Hex(t.Background), dk2 = Hex(t.Heading), lt2 = Hex(t.Secondary);
        string a1 = Hex(t.Heading), a2 = Hex(t.Primary), a3 = Hex(t.Line), a4 = Hex(t.Code), a5 = Hex(t.Border), a6 = Hex(t.Primary);
        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Marksmith">
          <a:themeElements>
            <a:clrScheme name="Marksmith">
              <a:dk1><a:srgbClr val="{dk1}"/></a:dk1><a:lt1><a:srgbClr val="{lt1}"/></a:lt1>
              <a:dk2><a:srgbClr val="{dk2}"/></a:dk2><a:lt2><a:srgbClr val="{lt2}"/></a:lt2>
              <a:accent1><a:srgbClr val="{a1}"/></a:accent1><a:accent2><a:srgbClr val="{a2}"/></a:accent2>
              <a:accent3><a:srgbClr val="{a3}"/></a:accent3><a:accent4><a:srgbClr val="{a4}"/></a:accent4>
              <a:accent5><a:srgbClr val="{a5}"/></a:accent5><a:accent6><a:srgbClr val="{a6}"/></a:accent6>
              <a:hlink><a:srgbClr val="{a2}"/></a:hlink><a:folHlink><a:srgbClr val="{a2}"/></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="Marksmith">
              <a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
              <a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
            </a:fontScheme>
            <a:fmtScheme name="Marksmith">
              <a:fillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              </a:fillStyleLst>
              <a:lnStyleLst>
                <a:ln w="6350" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln>
                <a:ln w="12700" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln>
                <a:ln w="19050" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln>
              </a:lnStyleLst>
              <a:effectStyleLst>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
              </a:effectStyleLst>
              <a:bgFillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              </a:bgFillStyleLst>
            </a:fmtScheme>
          </a:themeElements>
        </a:theme>
        """;
    }
}
