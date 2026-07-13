using Markdig;
using Markdig.Syntax.Inlines;
using System.Linq;

var md = "| Endpoint | Example | Notes |\n|---|---|---|\n| `POST /auth` | `{\"user\": \"kt\"}` | 🔐 Rate-limited<br>10 req/min |";
var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseEmojiAndSmiley(false).Build();
var doc = Markdown.Parse(md, pipeline);
var cell = doc.Descendants<Markdig.Extensions.Tables.TableCell>().LastOrDefault();
if (cell != null) {
    var p = cell.Descendants<Markdig.Syntax.ParagraphBlock>().FirstOrDefault();
    if (p != null) {
        foreach (var inline in p.Inline) {
            System.Console.WriteLine(inline.GetType().Name + :  + inline.ToString());
        }
    }
}
