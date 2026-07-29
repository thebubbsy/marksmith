## 2024-05-18 - Init

## 2024-05-18 - Optimized DocxExportService Regex Parsing
**Learning:** Re-instantiating `Regex` classes inside of loops for things like SVG path parsing forces allocation and potential parsing overhead.
**Action:** Lift static or constant RegEx definitions to `[GeneratedRegex]` using `partial class` when possible.
