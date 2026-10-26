namespace MarkSmith.Services;

// The centre step's heading. At the default 1220 px window the view toggles leave it about
// 90 px, so "Editor / Preview" trimmed to "Editor / Pr…" on the first screen everyone sees. When
// the full title doesn't fit, it names the view that is showing instead ("Editor" or "Preview");
// the step badge and the tooltip keep the full "Step 2: Editor / Preview".
public static class EditorStepTitleText
{
    public const string Full = "Editor / Preview";

    /// <param name="view">"Code", "Split" or "Preview" (the centre view selector).</param>
    /// <param name="fullFits">Whether <see cref="Full"/> fits the space the header has.</param>
    public static string For(string view, bool fullFits) =>
        fullFits ? Full : view == "Preview" ? "Preview" : "Editor";
}
