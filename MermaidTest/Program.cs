using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;

class Program {
    static void Main() {
        string text = "public void <font color=\"red\">Test</font>() { <span style=\"color:#00ff00\">var</span> x = 1; }";
        var regex = new Regex(@"<(?:font\s+color\s*=\s*[""']?([^""'>]+)[""']?|span\s+style\s*=\s*[""']?color\s*:\s*([^;""'>]+)[^>]*|/(font|span))>", RegexOptions.IgnoreCase);
        
        int lastPos = 0;
        foreach (Match m in regex.Matches(text)) {
            if (m.Index > lastPos) {
                Console.WriteLine($"TEXT: {text.Substring(lastPos, m.Index - lastPos)}");
            }
            if (m.Groups[3].Success) {
                Console.WriteLine("POP");
            } else {
                string color = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                Console.WriteLine($"PUSH: {color}");
            }
            lastPos = m.Index + m.Length;
        }
        if (lastPos < text.Length) {
            Console.WriteLine($"TEXT: {text.Substring(lastPos)}");
        }
    }
}
