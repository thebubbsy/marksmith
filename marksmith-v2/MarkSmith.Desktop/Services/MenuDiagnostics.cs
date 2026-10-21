using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;

namespace MarkSmith.Services;

// Keeps context menus about the task at hand. Read-only "Audit ..." reports pile up in a menu fast,
// and dozens of them stacked under the real actions bury the actions. Fold moves them into one
// alphabetised "Diagnostics" submenu at the bottom (the same shape the XAML-declared studio menus
// use), then tidies the separators the move left behind.
public static class MenuDiagnostics
{
    public const string SubmenuText = "Diagnostics";
    public const string SubmenuGlyph = "";

    // Below this many audits a submenu is more clicks for nothing, so the menu is left alone.
    private const int MinimumToFold = 3;

    public static bool IsDiagnostic(MenuFlyoutItemBase item) =>
        item is MenuFlyoutItem { Text: { } text } &&
        (text.StartsWith("Audit", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(" audit", StringComparison.OrdinalIgnoreCase));

    public static void Fold(MenuFlyout menu) => Fold(menu.Items);

    public static void Fold(IList<MenuFlyoutItemBase> items)
    {
        var audits = items.Where(IsDiagnostic).Cast<MenuFlyoutItem>().ToList();
        if (audits.Count < MinimumToFold) return;

        foreach (var audit in audits) items.Remove(audit);

        var submenu = new MenuFlyoutSubItem
        {
            Text = SubmenuText,
            Icon = new FontIcon { Glyph = SubmenuGlyph }
        };
        ToolTipService.SetToolTip(submenu, "Read-only checks that summarise and copy a report");
        foreach (var audit in audits.OrderBy(a => a.Text, StringComparer.OrdinalIgnoreCase))
            submenu.Items.Add(audit);

        TidySeparators(items);
        items.Add(new MenuFlyoutSeparator());
        items.Add(submenu);
    }

    // No separator first, last, or next to another one.
    public static void TidySeparators(IList<MenuFlyoutItemBase> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is not MenuFlyoutSeparator) continue;
            var dangling = i == 0 || i == items.Count - 1 || items[i - 1] is MenuFlyoutSeparator;
            if (dangling) items.RemoveAt(i);
        }
    }
}
