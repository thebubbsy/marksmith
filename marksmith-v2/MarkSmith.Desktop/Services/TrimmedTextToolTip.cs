using Microsoft.UI.Xaml.Controls;

namespace MarkSmith.Services;

/// <summary>
/// Gives a trimmed TextBlock a tooltip with its full text, only while it is actually cut off
/// ("Editor / Pr…" at the default window width). Untrimmed text gets no redundant tooltip.
/// </summary>
public static class TrimmedTextToolTip
{
    public static void Attach(params TextBlock[] blocks)
    {
        foreach (var block in blocks)
        {
            if (block is null) continue;
            block.IsTextTrimmedChanged += (sender, _) => Sync(sender);
            // A caption whose text changes while it stays trimmed must not keep the old tooltip.
            block.RegisterPropertyChangedCallback(TextBlock.TextProperty, (d, _) => Sync((TextBlock)d));
            Sync(block);
        }
    }

    private static void Sync(TextBlock block) =>
        ToolTipService.SetToolTip(block, block.IsTextTrimmed ? block.Text : null);
}
