using System.Text;

namespace MarkSmith.Services;

// "No emoji mode": removes every emoji/pictograph from text — including ZWJ sequences, skin-tone
// modifiers, variation selectors, flags, and keycap combos — before rendering or export. Applied
// at the render/export layer (MarkdownHtmlService + DocxExportService) rather than at ingest so
// it covers every input path (paste, file, clipboard watcher, folder watcher, REST API) and can
// be toggled without re-ingesting.
public static class EmojiStripper
{
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        Span<char> utf16 = stackalloc char[2];
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsEmojiRune(rune.Value)) continue;
            sb.Append(utf16[..rune.EncodeToUtf16(utf16)]);
        }
        return sb.ToString();
    }

    public static bool ContainsEmoji(string text) =>
        text.EnumerateRunes().Any(r => IsEmojiRune(r.Value));

    private static bool IsEmojiRune(int v) => v switch
    {
        >= 0x1F000 and <= 0x1FAFF => true, // emoticons, pictographs, transport, flags, supplemental
        >= 0x2600 and <= 0x27BF => true,   // misc symbols + dingbats (☀ ⚠ ✅ ❌ ☑ ➿ …)
        >= 0x2B00 and <= 0x2BFF => true,   // ⬆ ⬛ ⭐ …
        >= 0xFE00 and <= 0xFE0F => true,   // variation selectors (emoji-presentation markers)
        >= 0x231A and <= 0x231B => true,   // ⌚ ⌛
        >= 0x23E9 and <= 0x23FA => true,   // ⏩ ⏰ … ⏺
        >= 0x25AA and <= 0x25AB => true,   // ▪ ▫
        >= 0x25FB and <= 0x25FE => true,   // ◻ ◼ ◽ ◾
        >= 0x2934 and <= 0x2935 => true,   // ⤴ ⤵
        0x200D or 0x20E3 => true,          // zero-width joiner, combining keycap
        0x203C or 0x2049 => true,          // ‼ ⁉
        0x2139 => true,                    // ℹ
        0x24C2 => true,                    // Ⓜ
        0x25B6 or 0x25C0 => true,          // ▶ ◀
        0x3030 or 0x303D => true,          // 〰 〽
        0x3297 or 0x3299 => true,          // ㊗ ㊙
        _ => false,
    };
}
