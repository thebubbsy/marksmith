
using System;
using System.IO;
using MdToPdf.Core.Services;

class Program {
    static void Main() {
        var source = File.ReadAllText("diagram.txt");
        bool b = MermaidDocxRenderer.WouldOverflow(source);
        Console.WriteLine("WouldOverflow: " + b);
        var g = typeof(MermaidDocxRenderer).GetMethod("Parse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { source });
        Console.WriteLine(g != null ? "Parse success" : "Parse returned null");
    }
}

