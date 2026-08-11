## 2026-08-10 - [Escaped Double Quotes in Tables]
**Learning:** AI providers like ChatGPT sometimes leak escaped double quotes (`\"`) into Markdown tables, especially when transforming JSON payloads into Markdown.
**Action:** Added a normalization rule in `DialectNormalizer.cs` to replace `\"` with `"` on table lines to prevent visual artifacts.
