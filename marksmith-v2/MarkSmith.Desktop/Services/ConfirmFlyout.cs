using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MarkSmith.Services;

/// <summary>
/// A small "are you sure?" bubble beside the control that asked, for actions that rewrite or erase
/// the user's files from inside a ContentDialog (the Suite Hub), where a second ContentDialog can't
/// open. Cancel holds focus when it opens, so Enter or Esc straight away never goes ahead; only the
/// named action button does. Resolves false when dismissed any other way.
/// </summary>
public static class ConfirmFlyout
{
    public static Task<bool> AskAsync(FrameworkElement anchor, string title, string message, string confirmText)
    {
        var answer = new TaskCompletionSource<bool>();
        if (anchor.XamlRoot is null)
        {
            answer.SetResult(false);
            return answer.Task;
        }

        var confirm = new Button { Content = confirmText, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancel = new Button { Content = "Cancel" };
        ToolTipService.SetToolTip(cancel, "Cancel (Esc)");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var heading = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        ThemeBrush.Set(body, TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var content = new StackPanel { Spacing = 12, MaxWidth = 340 };
        content.Children.Add(heading);
        content.Children.Add(body);
        content.Children.Add(buttons);
        AutomationProperties.SetName(content, title);
        AutomationProperties.SetHelpText(content, message);

        var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.Bottom };
        var chosen = false;
        confirm.Click += (_, _) => { chosen = true; flyout.Hide(); };
        cancel.Click += (_, _) => flyout.Hide();
        flyout.Opened += (_, _) =>
        {
            HoverPolish.Apply(content);
            cancel.Focus(FocusState.Programmatic);
        };
        flyout.Closed += (_, _) => answer.TrySetResult(chosen);

        // Asked from a menu item, the menu is still closing on this tick; open once it has gone so
        // its focus hand-back doesn't land after (and close) this flyout.
        if (!anchor.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try { flyout.ShowAt(anchor); }
                catch (System.ArgumentException) { answer.TrySetResult(false); } // the anchor left the tree
            }))
        {
            answer.TrySetResult(false);
        }
        return answer.Task;
    }
}
