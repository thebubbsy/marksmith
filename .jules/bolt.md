
## 2024-05-18 - ⚡ Regex Compilation Overhead
**Learning:** Using `new Regex(...)` inside a loop or frequent method call causes unnecessary parsing and allocation overhead, even if the regex string is constant. Although .NET caches `Regex.Match("...")`, `new Regex(...)` bypasses it, and even `RegexOptions.Compiled` has startup overhead.
**Action:** Use `[GeneratedRegex("...")]` on a partial static method for compile-time generation of the state machine. This eliminates runtime parsing, caching, and allocation overheads, providing the fastest execution.

## 2024-05-18 - Performance boost for DocxExportService
**Learning:** Re-allocating `Regex` instances or repeatedly compiling complex regexes in tight loops significantly increases execution time and allocations.
**Action:** Transformed `DocxExportService` into a `partial class` and replaced in-loop `new Regex()` calls for code highlighting and emoji detection with static `[GeneratedRegex]` methods. Verified ~30-40% execution time reduction in benchmark tests.
