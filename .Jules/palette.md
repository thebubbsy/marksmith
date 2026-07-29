## 2024-05-24 - Missing AutomationProperties.Name on XAML icon buttons
**Learning:** Many icon-only buttons and toggle buttons in the WinUI/WPF XAML rely solely on `ToolTipService.ToolTip` for context. While tooltips might be read by some screen readers, the standard and most reliable way to ensure a11y support in Windows apps is to provide an explicit `AutomationProperties.Name`.
**Action:** Added `AutomationProperties.Name` to prominent icon-only UI elements in MainWindow to ensure screen readers always have proper context without relying on hover/tooltip behavior.
