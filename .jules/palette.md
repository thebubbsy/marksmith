## 2024-08-01 - Add Screen Reader Visibility & Keyboard Focus to Extensions

**Learning:** Screen reader users missing context from icon-only buttons, dynamic content without aria-live, and users navigating via keyboard needing a visible outline on custom-styled extensions / website pages. Also, XAML requires `AutomationProperties.Name` on buttons/icons that only have `ToolTipService.ToolTip` so screen readers pick up the meaning.

**Action:** Added `aria-live="polite" role="status"` to connection and toast banners in extensions. Used `:focus-visible` to give clear keyboard outlines (with `outline-offset` to not overlap borders) in extension popup.
