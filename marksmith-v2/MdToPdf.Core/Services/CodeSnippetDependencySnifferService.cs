using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MdToPdf.Core.Services
{
    public class DependencyReference
    {
        public string Language { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;
        public string RawStatement { get; set; } = string.Empty;
    }

    public class CodeSnippetDependencySnifferService
    {
        public List<DependencyReference> SniffDependencies(string markdown)
        {
            var results = new List<DependencyReference>();
            if (string.IsNullOrWhiteSpace(markdown)) return results;

            // Match code fences ```lang ... ```
            var fencePattern = @"```([a-zA-Z0-9_+#-]*)\r?\n(.*?)\r?\n```";
            var matches = Regex.Matches(markdown, fencePattern, RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                string lang = match.Groups[1].Value.Trim().ToLowerInvariant();
                string code = match.Groups[2].Value;

                ExtractDependenciesFromCode(lang, code, results);
            }

            return results;
        }

        private void ExtractDependenciesFromCode(string lang, string code, List<DependencyReference> results)
        {
            var lines = code.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // C# / .NET using statement
                if (lang is "cs" or "csharp" or "dotnet")
                {
                    var m = Regex.Match(trimmed, @"^using\s+([A-Za-z0-9_.]+)\s*;");
                    if (m.Success && !m.Groups[1].Value.StartsWith("System."))
                    {
                        results.Add(new DependencyReference
                        {
                            Language = lang,
                            PackageName = m.Groups[1].Value,
                            RawStatement = trimmed
                        });
                    }
                }
                // JS / TS import / require
                else if (lang is "js" or "javascript" or "ts" or "typescript" or "node")
                {
                    var m1 = Regex.Match(trimmed, @"^import\s+.*?\s+from\s+['""]([^'""]+)['""]");
                    if (m1.Success)
                    {
                        results.Add(new DependencyReference
                        {
                            Language = lang,
                            PackageName = m1.Groups[1].Value,
                            RawStatement = trimmed
                        });
                        continue;
                    }

                    var m2 = Regex.Match(trimmed, @"require\s*\(\s*['""]([^'""]+)['""]\s*\)");
                    if (m2.Success)
                    {
                        results.Add(new DependencyReference
                        {
                            Language = lang,
                            PackageName = m2.Groups[1].Value,
                            RawStatement = trimmed
                        });
                    }
                }
                // Python import / from ... import
                else if (lang is "py" or "python")
                {
                    var m1 = Regex.Match(trimmed, @"^import\s+([a-zA-Z0-9_]+)");
                    if (m1.Success)
                    {
                        results.Add(new DependencyReference
                        {
                            Language = lang,
                            PackageName = m1.Groups[1].Value,
                            RawStatement = trimmed
                        });
                        continue;
                    }

                    var m2 = Regex.Match(trimmed, @"^from\s+([a-zA-Z0-9_.]+)\s+import");
                    if (m2.Success)
                    {
                        results.Add(new DependencyReference
                        {
                            Language = lang,
                            PackageName = m2.Groups[1].Value,
                            RawStatement = trimmed
                        });
                    }
                }
                // C / C++ #include
                else if (lang is "c" or "cpp" or "c++")
                {
                    var m = Regex.Match(trimmed, @"^#include\s+[<""]([^>'""]+)[>""]");
                    if (m.Success)
                    {
                        results.Add(new DependencyReference
                        {
                            Language = lang,
                            PackageName = m.Groups[1].Value,
                            RawStatement = trimmed
                        });
                    }
                }
            }
        }
    }
}
