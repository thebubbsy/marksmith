using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

public partial class MeasureRegex {
    public static void Main() {
        string text = "public void Test() {}";
        var sw1 = Stopwatch.StartNew();
        for (int i=0; i<100000; i++) {
            Regex.IsMatch(text, @"\b(public|private|class|namespace|using System|void)\b");
        }
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        for (int i=0; i<100000; i++) {
            MyRegex().IsMatch(text);
        }
        sw2.Stop();

        Console.WriteLine($"Inline Regex: {sw1.ElapsedMilliseconds} ms");
        Console.WriteLine($"GeneratedRegex: {sw2.ElapsedMilliseconds} ms");
    }

    [GeneratedRegex(@"\b(public|private|class|namespace|using System|void)\b")]
    private static partial Regex MyRegex();
}
