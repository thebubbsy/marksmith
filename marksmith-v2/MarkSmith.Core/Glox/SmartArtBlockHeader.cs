using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Glox
{
    /// <summary>
    /// Reads the layout a <c>:::smartart</c> header line asks for. The preview and the DOCX export
    /// both use this, so a block draws the same layout in both places. Before this, the preview
    /// only recognised <c>type="…"</c>: <c>:::smartart process</c> stayed a bullet list in the
    /// preview while Word drew a diagram, and both ignored the bare <c>process</c>.
    /// </summary>
    public static class SmartArtBlockHeader
    {
        private static readonly Regex TypeAttr =
            new(@"type=[""']?([^""'\s>]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The family words the preview renderer and the suggester understand, on top of every
        // layout alias/URN the catalog resolves.
        private static readonly HashSet<string> Families = new(StringComparer.OrdinalIgnoreCase)
        {
            "process", "hierarchy", "cycle", "list", "relationship", "matrix", "pyramid", "venn",
            "picturelist", "mosaic", "org", "tree", "workflow", "timeline", "target", "grid", "basic", "default"
        };

        /// <summary>The requested layout, as written, from the text after <c>:::smartart</c>:
        /// a <c>type=</c> attribute wins; otherwise a bare first word that names a known family or
        /// layout (<c>:::smartart process</c>). Null when the header asks for nothing usable, so
        /// the caller falls back to suggesting a layout from the content.</summary>
        public static string? Layout(string? headerRest)
        {
            if (string.IsNullOrWhiteSpace(headerRest)) return null;

            var m = TypeAttr.Match(headerRest);
            if (m.Success) return m.Groups[1].Value.Trim();

            var word = headerRest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim('"', '\'', '{', '}', '.');
            if (string.IsNullOrEmpty(word) || word.Contains('=')) return null;
            return Families.Contains(word) || SmartArtLayoutCatalog.Shared.TryResolve(word) != null
                ? word
                : null;
        }
    }
}
