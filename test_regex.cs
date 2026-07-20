using System;
using System.IO;
using System.Text.RegularExpressions;

class Program {
    static void Main() {
        var markdown = File.ReadAllText(""diagram.txt"");
        var fences = Regex.Matches(markdown.Replace(""\r\n"", ""\n"").Replace('\r', '\n'), ""`mermaid[ \\t]*\\n(.*?)`"", RegexOptions.Singleline);
        foreach (Match m in fences) {
            Console.WriteLine(m.Groups[1].Value.Substring(0, Math.Min(50, m.Groups[1].Value.Length)));
        }
        Console.WriteLine(""Done."");
    }
}
