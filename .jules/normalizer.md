## 2024-07-29 - [Strip Claude Raw XML Tags]
**Learning:** Claude AI sometimes includes internal XML tags like `<antArtifact>` or `<antThinking>` in its output. These artifacts leak into the final rendered PDF/DOCX and confuse the Markdown parser.
**Action:** Add a normalization rule to `DialectNormalizer.cs` to identify and strip Claude-specific XML tags when they appear outside of inline code blocks.
