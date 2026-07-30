## 2024-05-24 - [Add Screen Reader Label for Tooltip Icon Buttons]
**Learning:** In WinUI (and Avalonia) XAML, icon-only buttons with `ToolTipService.ToolTip` (or `ToolTip.Tip`) are NOT automatically announced by screen readers. You must explicitly set `AutomationProperties.Name` with the identical string for full accessibility.
**Action:** When adding or auditing icon-only `<Button>` or `<DropDownButton>` controls that rely on tooltips, ensure an identical `AutomationProperties.Name` attribute is also included.
