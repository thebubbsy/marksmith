using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace MarkSmith.Services;

/// <summary>
/// A closed ComboBox changes its selection on Up/Down (and Home/End/PageUp/PageDown), and some
/// combo boxes act the moment their selection changes. Down on the closed recent-files box opened
/// the next file and tucked the panel away; on the AI-agent folder presets it overwrote the watch
/// folder; on the OCR reclassify boxes it reclassified the element once per key. For those boxes
/// these keys open the list instead, where arrows only move the highlight and Enter picks.
/// </summary>
public static class ComboArrowOpens
{
    public static void Attach(ComboBox combo)
    {
        combo.PreviewKeyDown += (_, e) =>
        {
            if (combo.IsDropDownOpen || !OpensList(e.Key)) return;
            e.Handled = true;
            combo.IsDropDownOpen = true;
        };
    }

    internal static bool OpensList(VirtualKey key) =>
        key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End
            or VirtualKey.PageUp or VirtualKey.PageDown;
}
