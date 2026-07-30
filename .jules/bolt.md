## 2024-05-14 - Replace Inline Regexes in DocxExportService with GeneratedRegex
**Learning:** `Regex.IsMatch` and `Regex.Match` instantiations inside loops or frequently called methods in `DocxExportService.cs` cause performance bottlenecks due to repetitive parsing and hashing overhead, and rely on the limited process-wide regex cache.
**Action:** Lift inline `Regex.*` calls in `DocxExportService.cs` to `[GeneratedRegex]` attributes to make them allocation-free singletons, significantly improving document processing latency.
