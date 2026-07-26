## 2024-05-30 - [DocxExportService AddText Allocation]
**Learning:** `Regex.Split` allocates a string array even when no matches are found. In `DocxExportService.cs`, `AddText` processes almost every text fragment in the document by calling `EmojiRegex.Split(text)`.
**Action:** Use a fast path `if (!EmojiRegex.IsMatch(text))` to bypass `Regex.Split` when there are no emojis, saving significant array allocations and processing time for plain text.
