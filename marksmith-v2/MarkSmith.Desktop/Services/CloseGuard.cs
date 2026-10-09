using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MarkSmith.Services;

/// <summary>
/// Asks before a studio window closes over work that hasn't gone anywhere yet. Shape Studio and
/// SmartArt Studio used to vanish with the diagram on the X; Diagram Studio and Document Galaxy
/// already asked. The question leads with the thing people opened the studio for: insert it.
/// </summary>
public static class CloseGuard
{
    /// <param name="window">The studio window.</param>
    /// <param name="hasUnkeptWork">True while closing would lose something.</param>
    /// <param name="insert">Inserts the work into the document; false if it couldn't.</param>
    /// <param name="what">What's on the canvas, for the copy ("diagram", "SmartArt graphic").</param>
    /// <param name="alsoExportsOrCopies">The studio can also export or copy the work, so the copy
    /// names those ways out too.</param>
    public static void Attach(Window window, Func<bool> hasUnkeptWork, Func<bool> insert, string what,
        bool alsoExportsOrCopies = false)
    {
        bool allowClose = false, asking = false;

        window.AppWindow.Closing += (sender, e) =>
        {
            if (allowClose || !hasUnkeptWork()) return;
            e.Cancel = true;
            // One ContentDialog per window: with another one up, the X leaves it in front.
            if (asking || HoverPolish.IsContentDialogOpen(window.Content?.XamlRoot)) return;
            asking = true;
            _ = AskAsync();
        };

        async Task AskAsync()
        {
            try
            {
                window.Activate(); // a close from the taskbar must not ask from behind other windows
                var dialog = new ContentDialog
                {
                    Title = $"Insert the {what} before closing?",
                    Content = $"This {what} hasn't been "
                            + (alsoExportsOrCopies ? "inserted into your document, exported or copied" : "inserted into your document")
                            + " yet. Closing now throws it away.",
                    PrimaryButtonText = "Insert and close",
                    SecondaryButtonText = "Discard",
                    CloseButtonText = "Keep editing",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = window.Content.XamlRoot,
                    RequestedTheme = (window.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                };

                var result = await dialog.ShowPolishedAsync();
                if (result == ContentDialogResult.Primary)
                {
                    if (!insert()) return;
                }
                else if (result != ContentDialogResult.Secondary)
                {
                    return; // Keep editing, or Esc
                }

                allowClose = true;
                window.Close();
            }
            finally
            {
                asking = false;
            }
        }
    }
}
