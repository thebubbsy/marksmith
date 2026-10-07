using MarkSmith.Services.Import;
using Xunit;

namespace MarkSmith.Tests.Import;

/// <summary>
/// Run #23: the DOM-based HTML → Markdown converter behind opening .html files and email bodies.
/// The inputs are the shapes real clients produce (Word/Outlook HTML, Gmail, our own exports),
/// and every expectation is the Markdown a person would have written by hand.
/// </summary>
public class HtmlToMarkdownTests
{
    private static string Md(string html, HtmlToMarkdownOptions? o = null) => HtmlToMarkdown.Convert(html, o).TrimEnd('\n');

    [Fact]
    public void Headings_Paragraphs_And_Inline_Formatting()
    {
        var md = Md("<h1>Q3 <b>plan</b></h1><p>Hello <strong>bold</strong>, <em>italic</em> and <code>x = 1</code>.</p><hr><h2><strong>Next</strong></h2>");
        Assert.Equal("# Q3 **plan**\n\nHello **bold**, *italic* and `x = 1`.\n\n---\n\n## Next", md);
    }

    [Fact]
    public void Adjacent_Runs_Of_The_Same_Format_Merge_And_Spaces_Move_Outside()
    {
        // Word splits one bold word into several runs; trailing spaces sit inside the run.
        Assert.Equal("**Hello** world", Md("<p><b>Hel</b><b>lo </b>world</p>"));
        Assert.Equal("Say **hi** now", Md("<p>Say<span style='font-weight:bold'> hi </span>now</p>"));
    }

    [Fact]
    public void Word_Styles_Count_As_Formatting()
    {
        var md = Md("<p class=MsoNormal><span style='font-family:Consolas'>npm test</span> then <span style='font-style:italic'>wait</span><o:p></o:p></p><p class=MsoNormal><o:p>&nbsp;</o:p></p><p>End</p>");
        Assert.Equal("`npm test` then *wait*\n\nEnd", md);
    }

    [Fact]
    public void Links_Autolinks_And_Outlook_Safe_Links()
    {
        var safe = "https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.com%2Fa%3Fb%3D1&data=05%7C01";
        var md = Md($"<p><a href='https://x.dev'>site</a> <a href='https://x.dev'>https://x.dev</a> <a href='{safe}'>doc</a> <a href='#top'>top</a></p>");
        Assert.Equal("[site](https://x.dev) <https://x.dev> [doc](https://example.com/a?b=1) top", md);
    }

    [Fact]
    public void Code_Blocks_Keep_Language_And_Use_A_Longer_Fence_When_Needed()
    {
        var md = Md("<pre><code class='language-python'>def f():\n    return 1</code></pre><pre>a ``` b</pre>");
        Assert.Equal("```python\ndef f():\n    return 1\n```\n\n````\na ``` b\n````", md);
    }

    [Fact]
    public void Nested_Ordered_And_Task_Lists()
    {
        var md = Md("<ol start='3'><li>Three<ul><li>a</li><li>b</li></ul></li><li>Four</li></ol><ul><li><input type=checkbox checked> done</li><li><input type=checkbox> todo</li></ul>");
        Assert.Equal("3. Three\n   - a\n   - b\n4. Four\n\n- [x] done\n- [ ] todo", md);
    }

    [Fact]
    public void Ballot_Box_Items_From_An_Email_Come_Back_As_A_Task_List()
    {
        // MarkSmith's email export draws task boxes as ☑ / ☐ (mail has no checkboxes).
        var md = Md("<ul><li><span>☑</span>&#160;Draft</li><li><span>☐</span>&#160;Send</li><li>☐</li></ul>");
        Assert.Equal("- [x] Draft\n- [ ] Send\n- ☐", md);
    }

    [Fact]
    public void Word_List_Paragraphs_Become_Real_Nested_Lists()
    {
        var html = "<p class=MsoListParagraphCxSpFirst style='mso-list:l0 level1 lfo1'><![if !supportLists]><span>1.<span>&nbsp;&nbsp;</span></span><![endif]>First</p>"
                 + "<p class=MsoListParagraphCxSpMiddle style='mso-list:l0 level2 lfo1'><![if !supportLists]><span style='font-family:\"Courier New\"'>o<span>&nbsp;&nbsp;</span></span><![endif]>Sub point</p>"
                 + "<p class=MsoListParagraphCxSpLast style='mso-list:l0 level1 lfo1'><![if !supportLists]><span>2.<span>&nbsp;&nbsp;</span></span><![endif]>Second</p>"
                 + "<p>After</p>";
        Assert.Equal("1. First\n   - Sub point\n2. Second\n\nAfter", Md(html));
    }

    [Fact]
    public void Data_Tables_Become_Pipe_Tables_With_Alignment()
    {
        var md = Md("<table><thead><tr><th>Item</th><th align=right>Cost</th><th style='text-align:center'>Ok</th></tr></thead>"
                  + "<tbody><tr><td>A|B</td><td align=right>$5</td><td>yes</td></tr><tr><td><p>two</p><p>lines</p></td><td>$7</td><td></td></tr></tbody></table>");
        Assert.Equal("| Item | Cost | Ok |\n| --- | ---: | :---: |\n| A\\|B | $5 | yes |\n| two<br>lines | $7 |  |", md);
    }

    [Fact]
    public void Word_Header_Cells_Lose_Their_Redundant_Bold_And_Angle_Brackets_Stay_Readable()
    {
        var md = Md("<table><tr><td><b>Name</b></td><td><b>Mail</b></td></tr><tr><td>Bob</td><td>Bob &lt;bob@x.com&gt;</td></tr></table>");
        Assert.Equal("| Name | Mail |\n| --- | --- |\n| Bob | Bob \\<bob@x.com> |", md);
    }

    [Fact]
    public void Layout_Tables_Are_Unwrapped()
    {
        // Every HTML email: a centred 600 px single-cell wrapper, with a two-cell header row inside.
        var html = "<table role='presentation' width='600'><tr><td><h1>News</h1><p>Body text.</p>"
                 + "<table><tr><td><img src='logo.png' alt='Logo'></td><td>Acme Ltd</td></tr></table></td></tr></table>";
        Assert.Equal("# News\n\nBody text.\n\n![Logo](logo.png)\n\nAcme Ltd", Md(html));
    }

    [Fact]
    public void Hidden_Content_Preheaders_And_Tracking_Pixels_Are_Dropped()
    {
        var html = "<div style='display:none;max-height:0;overflow:hidden'>Preview text for the inbox</div>"
                 + "<p>Visible<img src='https://t.example/p.gif' width=1 height=1></p>"
                 + "<p style='mso-hide:all'>Outlook only</p><span hidden>gone</span>";
        Assert.Equal("Visible", Md(html));
    }

    [Fact]
    public void Images_Go_Through_The_Resolver()
    {
        var o = new HtmlToMarkdownOptions { ResolveImage = src => src == "cid:a" ? "media/a.png" : null };
        Assert.Equal("![Chart](media/a.png) and", Md("<p><img src='cid:a' alt='Chart'> and <img src='cid:missing'></p>", o));
    }

    [Fact]
    public void Literal_Markdown_Characters_Are_Escaped_But_Snake_Case_Is_Not()
    {
        var md = Md("<p># not a heading</p><p>2. not a list, a *star*, [brackets] and my_var_name</p><p>- dash</p>");
        Assert.Equal("\\# not a heading\n\n2\\. not a list, a \\*star\\*, \\[brackets\\] and my_var_name\n\n\\- dash", md);
    }

    [Fact]
    public void Line_Breaks_Are_Hard_Breaks()
    {
        Assert.Equal("Kind regards,\\\nAnn Lee\\\n**Acme**", Md("<p>Kind regards,<br>Ann Lee<br><b>Acme</b></p>"));
    }

    [Fact]
    public void Blockquotes_And_Details()
    {
        var md = Md("<blockquote><p>One</p><p>Two</p></blockquote><details><summary>More</summary><p>Hidden <b>bit</b></p></details>");
        Assert.Equal("> One\n>\n> Two\n\n<details><summary>More</summary>\n\nHidden **bit**\n\n</details>", md);
    }

    [Fact]
    public void MarkSmith_Export_Chrome_And_Navigation_Are_Left_Out()
    {
        var html = "<nav id='toc'><div class='toc-title'>Contents</div><ul><li><a href='#a'>A</a></li></ul></nav>"
                 + "<h1 id='a'>A</h1><p>Body</p><div class='mark-footer'>Made with <a href='https://x'>Marksmith</a></div>";
        Assert.Equal("# A\n\nBody", Md(html));
    }

    [Fact]
    public void Opening_An_Html_File_Moves_Embedded_Images_Into_A_Media_Folder()
    {
        var dir = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.AppContext.BaseDirectory, "htmlimg_" + System.Guid.NewGuid().ToString("N")[..8])).FullName;
        var png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var file = System.IO.Path.Combine(dir, "Page.html");
        System.IO.File.WriteAllText(file, $"<p><img alt='dot' src='data:image/png;base64,{png}'></p><p><img alt='again' src='data:image/png;base64,{png}'></p>");

        var md = MarkSmith.Plugins.PluginFileReader.ImportAsync(file).GetAwaiter().GetResult().Markdown;

        var media = System.IO.Directory.GetFiles(System.IO.Path.Combine(dir, "Page_media"));
        Assert.Single(media); // the same picture twice is one file
        var name = System.IO.Path.GetFileName(media[0]);
        Assert.Equal($"![dot](Page_media/{name})\n\n![again](Page_media/{name})\n", md);
    }

    [Fact]
    public void Plain_Text_Keeps_Short_Lines_And_Quotes()
    {
        var md = HtmlToMarkdown.FromPlainText("Hi Bob,\r\n\r\nSee below.\r\n\r\nThanks\r\nAnn\r\n\r\n> quoted\r\n> more").TrimEnd('\n');
        Assert.Equal("Hi Bob,\n\nSee below.\n\nThanks\\\nAnn\n\n> quoted\n> more", md);
    }

    [Fact]
    public void Junk_And_Empty_Input_Give_Empty_Markdown()
    {
        Assert.Equal("", HtmlToMarkdown.Convert(""));
        Assert.Equal("", HtmlToMarkdown.Convert("<html><head><style>p{}</style><script>x()</script></head><body><p>&nbsp;</p></body></html>"));
    }
}
