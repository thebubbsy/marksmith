
using System;
using System.IO;
using System.Reflection;
using MdToPdf.Models;
using MdToPdf.Services;

class Program {
    static void Main() {
        var asm = Assembly.LoadFrom(@"C:\Users\Tony\.gemini\antigravity\scratch\marksmith\MdToPdf.Core\bin\Debug\net8.0\MdToPdf.Core.dll");
        var type = asm.GetType("MdToPdf.Services.MermaidDocxRenderer");
        var source = File.ReadAllText("diagram.txt");
        
        var theme = new ThemeDefinition(
            "#1e1e1e", "#ffffff", "Consolas", 14, 16,
            "#007acc", "#e51400", "#d4d4d4",
            "#252526", "#333333", "#444444", "#3e3e42",
            "#569cd6", "#4ec9b0", "#dcdcaa",
            "#b5cea8", "#ce9178", "#d16969", "#c586c0", "#608b4e",
            "#2d2d30", "#ffffff", "#ffffff", "#000000",
            true);
            
        var tryRender = type.GetMethod("TryRender", BindingFlags.Public | BindingFlags.Static);
        object[] args = new object[] { source, theme, 1u, null, null, false };
        var success = (bool)tryRender.Invoke(null, args);
        Console.WriteLine($"TryRender success: {success}");
        if (success) {
            Console.WriteLine($"Oversized: {args[4]}");
        }
    }
}

