using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

// Quick test to see which lines fail in MermaidDocxRenderer.Parse
class Program
{
    static void Main()
    {
        var source = File.ReadAllText("diagram.txt");
        
        // Simulate the Parse logic inline
        string? dir = null;
        int lineNum = 0;
        foreach (var raw in source.Replace("\r", "").Split('\n'))
        {
            lineNum++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%")) continue;
            if (dir is null)
            {
                var m = Regex.Match(line, @"^(?:graph|flowchart)\s+(TD|TB|LR|RL|BT)\b", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                dir = m.Groups[1].Value.ToUpperInvariant();
                Console.WriteLine($"Line {lineNum}: Found direction: {dir}");
                continue;
            }
            if (Regex.IsMatch(line, @"^(subgraph\b|end\b|direction\b|classDef\b|class\b|style\b|linkStyle\b|click\b|accTitle\b|accDescr\b)"))
            {
                Console.WriteLine($"Line {lineNum}: SKIP ({line.Substring(0, Math.Min(40, line.Length))})");
                continue;
            }
            
            // Try ReadNode-style parsing: check if line starts with a valid ID
            var idMatch = Regex.Match(line, @"^([A-Za-z0-9_](?:(?!--|-\.)[A-Za-z0-9_.:-])*)");
            if (!idMatch.Success)
            {
                Console.WriteLine($"Line {lineNum}: *** PARSE FAIL - no valid ID at start: '{line}'");
                continue;
            }
            Console.WriteLine($"Line {lineNum}: OK ({line.Substring(0, Math.Min(50, line.Length))})");
        }

        // Now test via reflection on the actual Parse method
        Console.WriteLine("\n--- Testing actual MermaidDocxRenderer.Parse ---");
        var asm = Assembly.LoadFrom("MdToPdf.Core\\bin\\Debug\\net8.0\\MdToPdf.Core.dll");
        var type = asm.GetType("MdToPdf.Services.MermaidDocxRenderer");
        var parse = type?.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static);
        if (parse != null)
        {
            var result = parse.Invoke(null, new object[] { source });
            Console.WriteLine($"Parse result: {(result == null ? "NULL (FAILED)" : "SUCCESS")}");
            if (result != null)
            {
                var nodesProp = result.GetType().GetProperty("Nodes");
                var edgesProp = result.GetType().GetProperty("Edges");
                var nodes = nodesProp?.GetValue(result) as System.Collections.IList;
                var edges = edgesProp?.GetValue(result) as System.Collections.IList;
                Console.WriteLine($"  Nodes: {nodes?.Count}, Edges: {edges?.Count}");
            }
        }
        else Console.WriteLine("Could not find Parse method");
    }
}
