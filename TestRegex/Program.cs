using System;
using System.Text.RegularExpressions;
class Program {
    static void Main() {
        var s = ""2. `mermaid\nflowchart TB\n`"";
        var m = Regex.Matches(s, ""`mermaid[ \\t]*\\n(.*?)`"", RegexOptions.Singleline);
        Console.WriteLine(""Matches: "" + m.Count);
    }
}
