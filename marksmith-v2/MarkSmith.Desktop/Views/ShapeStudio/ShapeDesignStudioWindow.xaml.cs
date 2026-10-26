using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.System;
using MarkSmith.Core.Composer;
using MarkSmith.Services;
using MarkSmith.ViewModels.ShapeStudio;

namespace MarkSmith.Views.ShapeStudio
{
    public sealed partial class ShapeDesignStudioWindow : Window
    {
        // Reports an edit's result on the status line. Edits (arrange, recolour, normalise) used to
        // copy their summary as well, which silently replaced whatever the user had on the clipboard;
        // only commands that say Copy, and the Diagnostics reports, put text there now.
        private void ReportEdit(string? summary)
        {
            if (ViewModel is null) return;
            var line = (summary ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0].Trim();
            if (string.IsNullOrWhiteSpace(line)) return;
            // The view model often reported this result already as "✓ … · Ctrl+Z to undo"; replacing
            // that with the bare summary dropped the undo hint, so a line containing it is kept.
            if (ViewModel.StatusMessage?.Contains(line, StringComparison.Ordinal) == true) return;
            ViewModel.StatusMessage = line;
        }

        // Copies text and reports it on the status line. These copies used to discard ClipboardWriter's
        // result, so a busy clipboard still said "Copied ...". With no message, the command has already
        // put its result on the status line; a failed copy is added to it. A tick button shows a tick
        // or "Busy" from the real result (it used to tick even when nothing was copied).
        private async void CopyAndReport(string? text, string? message = null, Button? tickButton = null, string? tickLabel = null)
        {
            var error = await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(text ?? string.Empty);
            if (ViewModel is null) return;
            if (error is null)
            {
                // A result line (not a "Copied …" message) now says it went to the clipboard too.
                ViewModel.StatusMessage = MarkSmith.Services.CopyStatusText.Succeeded(message ?? ViewModel.StatusMessage);
            }
            else
            {
                ViewModel.StatusMessage = MarkSmith.Services.CopyStatusText.Failed(message ?? ViewModel.StatusMessage, error);
            }
            if (tickButton is null) return;
            if (tickLabel is null) MarkSmith.Services.CopyFeedback.ApplyButtonTick(tickButton, error);
            else MarkSmith.Services.CopyFeedback.ApplyButtonTick(tickButton, error, tickLabel);
        }

        /// <summary>One row of the colour-scheme flyout: the name and its swatches.</summary>
        public sealed class PaletteChoice
        {
            public string Name { get; init; } = "";
            public List<SolidColorBrush> Swatches { get; init; } = new();
        }

        public ShapeDesignStudioViewModel ViewModel { get; }
        public event EventHandler<string>? InsertToDocumentRequested;

        // Shape drag (moves the whole selection). The pointer's total travel is compared with what
        // the group actually moved, so a drag held against the canvas edge doesn't leave the shapes
        // trailing behind the pointer once it comes back.
        private ShapeCanvasItemViewModel? _dragShape;
        private Point _dragStart;
        private bool _dragMoved;
        private double _dragAppliedX, _dragAppliedY;

        // Rubber-band selection on empty canvas
        private bool _marquee;
        private bool _marqueeAdditive;
        private Point _marqueeStart;

        // Resize & rotation handles
        private ShapeCanvasItemViewModel? _adornedShape;
        private readonly List<(Microsoft.UI.Xaml.Shapes.Rectangle Handle, ShapeDesignStudioViewModel.ResizeEdges Edges)> _handles = new();
        private Microsoft.UI.Xaml.Shapes.Line? _rotateStem;
        private Microsoft.UI.Xaml.Shapes.Ellipse? _rotateHandle;
        private Microsoft.UI.Xaml.Shapes.Line? _rotateReferenceGuide;
        private Microsoft.UI.Xaml.Shapes.Path? _rotateArcOverlay;
        private Border? _rotateAngleBadge;
        private TextBlock? _rotateAngleBadgeText;
        private Microsoft.UI.Xaml.Shapes.Line? _snapGuideX;
        private Microsoft.UI.Xaml.Shapes.Line? _snapGuideY;
        private Microsoft.UI.Xaml.Shapes.Line? _crosshairLineX;
        private Microsoft.UI.Xaml.Shapes.Line? _crosshairLineY;
        private Border? _cursorHudBadge;
        private TextBlock? _cursorHudText;
        private bool _rotating;
        private bool _rotated;
        private bool _multiRotating;
        private bool _multiRotated;
        private Point _groupCentroid;
        private double _initialGroupHandleAngle;
        private List<(ShapeCanvasItemViewModel Shape, double X, double Y, double Width, double Height, int Rotation, double CenterOffsetX, double CenterOffsetY)> _initialGroupTransforms = new();
        private ShapeDesignStudioViewModel.ResizeEdges _resizeEdges;
        private (double X, double Y, double W, double H) _resizeFrom;
        private Point _resizeStart;
        private bool _resized;
        private Border? _resizeDimensionBadge;
        private TextBlock? _resizeDimensionBadgeText;
        private Microsoft.UI.Xaml.Shapes.Line? _resizeAspectGuide;

        // Drag-to-draw with an armed tool
        private bool _drawing;
        private Point _drawStart;

        private ShapeCanvasItemViewModel? _hoverShape;
        private ShapeCanvasItemViewModel? _inspectorUndoFor;
        private readonly List<PaletteChoice> _paletteChoices = new();
        private bool _syncingPalette;
        private string? _armedStatus;

        public ShapeDesignStudioWindow()
        {
            this.InitializeComponent();
            // A long status line is cut off at the window edge; hovering it shows the whole message.
            TrimmedTextToolTip.Attach(StatusBarText);
            LiveAnnounce.Track(StatusBarText);
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            SizeForDisplay();
            ViewModel = new ShapeDesignStudioViewModel();
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            TitleBarInsets.Reserve(this, AppTitleBar); // keep the action buttons clear of min/max/close
            CaptionButtons.Follow(this, AppTitleBar);
            // RightTapped is mouse/pen/touch only: Shift+F10 and the Menu key open the same menus.
            KeyboardContextMenu.BridgeAll(
                (SelectionBoundsPill, OnSelectionBoundsPillRightTapped),
                (SelectionSummaryButton, OnSelectionSummaryRightTapped),
                (ClearCanvasButton, OnClearCanvasButtonRightTapped),
                (ExportDropDownButton, OnExportDropDownButtonRightTapped),
                (InsertIntoDocumentButton, OnInsertIntoDocumentButtonRightTapped),
                (PresetCategoryCombo, OnPresetCategoryComboRightTapped),
                (PresetList, OnPresetListRightTapped),
                (PaletteGrid, OnPaletteGridRightTapped),
                (PickImageButton, OnImageCardRightTapped),
                (FuseMosaicDensity, OnFuseMosaicDensityRightTapped),
                (FuseMosaicPadding, OnFuseMosaicPaddingRightTapped),
                (FuseLineDensity, OnFuseLineDensityRightTapped),
                (FuseEdgeSensitivity, OnFuseEdgeSensitivityRightTapped),
                (FuseStrokeWidth, OnFuseStrokeWidthRightTapped),
                (ConvertButton, OnConvertButtonRightTapped),
                (ToolUndoButton, OnToolStripRightTapped),
                (ToolRedoButton, OnToolStripRightTapped),
                (AlignLeftButton, OnAlignButtonRightTapped),
                (AlignCenterButton, OnAlignButtonRightTapped),
                (AlignRightButton, OnAlignButtonRightTapped),
                (AlignTopButton, OnAlignButtonRightTapped),
                (AlignMiddleButton, OnAlignButtonRightTapped),
                (AlignBottomButton, OnAlignButtonRightTapped),
                (DistributeHorizontalButton, OnAlignButtonRightTapped),
                (DistributeVerticalButton, OnAlignButtonRightTapped),
                (BringToFrontButton, OnToolStripRightTapped),
                (SendToBackButton, OnToolStripRightTapped),
                (RotateLeftButton, OnRotateButtonRightTapped),
                (RotateRightButton, OnRotateButtonRightTapped),
                (DuplicateToolButton, OnToolStripRightTapped),
                (DeleteSelectionToolButton, OnToolStripRightTapped),
                (GridSnapButton, OnGridSnapButtonRightTapped),
                (CenterCanvasButton, OnCenterCanvasButtonRightTapped),
                (DistributeCanvasButton, OnDistributeCanvasButtonRightTapped),
                (PaletteButton, OnPaletteButtonRightTapped),
                (FillSwatchButton, OnFillSwatchButtonRightTapped),
                (RotationSlider, OnRotationSliderRightTapped),
                (ResetRotationButton, OnResetRotationButtonRightTapped),
                (RotationReadoutButton, OnResetRotationButtonRightTapped),
                (ZoomButton, OnZoomButtonRightTapped),
                (MultiRotateHandle, OnMultiRotateHandleRightTapped),
                (MultiSelectionBadge, OnMultiSelectionBadgeRightTapped));
            // Undo at the start of history, Delete with the last selection, or Clear on the last
            // shapes disabled the button holding focus and dropped the keyboard out of the window.
            FocusRescue.Attach(() => CanvasScroller, ToolUndoButton, ToolRedoButton);
            FocusRescue.Attach(() => CanvasScroller, AlignLeftButton, AlignCenterButton, AlignRightButton,
                AlignTopButton, AlignMiddleButton, AlignBottomButton, DistributeHorizontalButton,
                DistributeVerticalButton, BringToFrontButton, SendToBackButton, RotateLeftButton,
                RotateRightButton, DuplicateToolButton, DeleteSelectionToolButton);
            FocusRescue.Attach(() => CanvasScroller, ClearCanvasButton);
            // Options menus that keep Cut / Copy / Paste over a selection and open from Shift+F10;
            // their RightTapped handlers replaced the text menu and were mouse only.
            TextBoxMenu.Attach(LabelTextBox, FillLabelTextBoxMenu);
            TextBoxMenu.Attach(FuseImagePathBox, FillFuseImagePathBoxMenu);
            TextBoxEndScroll.Attach(FuseImagePathBox);
            TextBoxMenu.Attach(FillHexBox, FillFillHexBoxMenu);
            // The number boxes' menus go on the TextBox inside each one's template.
            TextBoxMenu.AttachInner(PositionXNumberBox, FillPositionNumberBoxMenu);
            TextBoxMenu.AttachInner(PositionYNumberBox, FillPositionNumberBoxMenu);
            TextBoxMenu.AttachInner(SizeWidthNumberBox, FillSizeNumberBoxMenu);
            TextBoxMenu.AttachInner(SizeHeightNumberBox, FillSizeNumberBoxMenu);
            TextBoxMenu.AttachInner(StrokeWeightNumberBox, FillStrokeWeightMenu);
            CloseGuard.Attach(this, () => ViewModel.HasUnkeptWork, () =>
            {
                ViewModel.InsertIntoDocument();
                return true;
            }, "diagram", alsoExportsOrCopies: true);
            this.RootGrid.DataContext = ViewModel;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.InsertToDocumentRequested += (s, block) => InsertToDocumentRequested?.Invoke(this, block);
            HoverPolish.Track(this.RootGrid);
            this.RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;
            InspectorPanel.GettingFocus += OnInspectorGettingFocus;
            BuildPaletteChoices();
            BuildResizeHandles();
            MarqueeRect.Fill = new SolidColorBrush(AccentColor(0x33));
            SetCursor(MainCanvas, Microsoft.UI.Input.InputSystemCursorShape.Arrow);
            if (StatusBarText != null && !string.IsNullOrEmpty(ViewModel.StatusMessage))
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusBarText, ViewModel.StatusMessage);
            SyncSelectionSummaryButton();
            // Open with the canvas focused (no focus ring) so Ctrl+Z / Del work at once and the
            // first title-bar button doesn't come up wearing a keyboard-focus rectangle.
            this.RootGrid.Loaded += (_, _) => CanvasScroller.Focus(FocusState.Programmatic);
            // Seed the bounds pill's readout and spoken name; it otherwise waits for the first selection change.
            this.RootGrid.Loaded += (_, _) => UpdateSelectionBoundsPill();
            this.RootGrid.ActualThemeChanged += (_, _) =>
            {
                foreach (var (s, p) in _shapePaths)
                    ApplyShapeVisual(p, s, ReferenceEquals(s, _hoverShape));
                // The snap grid's lines take their stroke when drawn; redraw them in the new theme.
                UpdateVisualGridOverlay();
            };
            if (CompAdaptive is not null)
            {
                CompAdaptive.IsChecked = true;
            }
            if (MultiRotateHandle != null)
            {
                SetCursor(MultiRotateHandle, Microsoft.UI.Input.InputSystemCursorShape.Hand);
                MultiRotateHandle.PointerPressed += OnMultiRotateHandlePointerPressed;
                MultiRotateHandle.PointerMoved += OnMultiRotateHandlePointerMoved;
                MultiRotateHandle.PointerReleased += OnMultiRotateHandlePointerReleased;
                MultiRotateHandle.PointerCaptureLost += OnMultiRotateHandlePointerCaptureLost;
                MultiRotateHandle.PointerWheelChanged += OnMultiRotateHandlePointerWheelChanged;
                MultiRotateHandle.DoubleTapped += OnMultiRotateHandleDoubleTapped;
                MultiRotateHandle.RightTapped += OnMultiRotateHandleRightTapped;
                MultiRotateHandle.PointerEntered += (_, _) => ThemeBrush.Set(MultiRotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "AccentFillColorSecondaryBrush");
                MultiRotateHandle.PointerExited += (_, _) => ThemeBrush.Set(MultiRotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
            }
        }

        // Sized in DIPs (AppWindow sizes are physical pixels, so the default opened cramped on a
        // scaled display) with a floor that keeps the toolbar, both side panes and a usable canvas
        // on screen. Without one the window could be squeezed until the canvas vanished.
        private void SizeForDisplay()
        {
            try
            {
                var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
                if (scale <= 0) scale = 1;
                var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
                    Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
                AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    Math.Min((int)(1440 * scale), work.Width), Math.Min((int)(880 * scale), work.Height)));
                if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    presenter.PreferredMinimumWidth = Math.Min((int)(1180 * scale), work.Width);
                    presenter.PreferredMinimumHeight = Math.Min((int)(620 * scale), work.Height);
                }
            }
            catch { /* best effort: the default size still works */ }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // The colour-scheme button drops its name when the toolbar is too narrow for it; it used to
        // slide over the Duplicate and Delete buttons instead. The strip's one-line width is summed from
        // its children: measuring the WrapRow itself at infinite width here would leave that unwrapped
        // size behind for the next arrange.
        private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            double stripWidth = 0;
            int shown = 0;
            foreach (var child in ToolStrip.Children)
            {
                if (child.Visibility != Visibility.Visible || child.DesiredSize.Width <= 0) continue;
                stripWidth += child.DesiredSize.Width;
                shown++;
            }
            stripWidth += Math.Max(0, shown - 1) * ToolStrip.HorizontalSpacing;
            bool roomy = e.NewSize.Width - 16 >= stripWidth + 180;
            PaletteNameText.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- zoom ----

        private static readonly double[] ZoomSteps = { 0.2, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.25, 1.5, 2, 3, 4 };

        private void OnCanvasViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            ZoomText.Text = $"{Math.Round(CanvasScroller.ZoomFactor * 100)}%";
            UpdateAdorner(); // handles stay the same size on screen at any zoom
        }

        /// <summary>Shows the whole diagram: zooms out (never in past 100%) until it fits the view.
        /// Presets are laid out up to ~720 px wide, so on a narrower canvas their right side used to
        /// open cut off with nothing on screen to say so.</summary>
        private void FitCanvasToContent()
        {
            double vw = CanvasScroller.ViewportWidth, vh = CanvasScroller.ViewportHeight;
            if (vw <= 0 || vh <= 0) return;
            if (ViewModel.Shapes.Count == 0 || ViewModel.IsDense)
            {
                CanvasScroller.ChangeView(0, 0, 1f);
                return;
            }
            double right = ViewModel.Shapes.Max(s => ShapeDesignStudioViewModel.RotatedBounds(s).Right) + 24;
            double bottom = ViewModel.Shapes.Max(s => ShapeDesignStudioViewModel.RotatedBounds(s).Bottom) + 24;
            double zoom = Math.Clamp(Math.Min(vw / right, vh / bottom), CanvasScroller.MinZoomFactor, 1.0);
            CanvasScroller.ChangeView(0, 0, (float)zoom);
        }

        private void FitCanvasAfterLayout() => DispatcherQueue.TryEnqueue(FitCanvasToContent);

        private void ZoomTo(double factor)
        {
            double vw = CanvasScroller.ViewportWidth, vh = CanvasScroller.ViewportHeight, z = CanvasScroller.ZoomFactor;
            factor = Math.Clamp(factor, CanvasScroller.MinZoomFactor, CanvasScroller.MaxZoomFactor);
            // Keep the middle of the view where it is.
            double cx = (CanvasScroller.HorizontalOffset + vw / 2) / z, cy = (CanvasScroller.VerticalOffset + vh / 2) / z;
            CanvasScroller.ChangeView(Math.Max(0, cx * factor - vw / 2), Math.Max(0, cy * factor - vh / 2), (float)factor);
        }

        private void ZoomStep(int direction)
        {
            double z = CanvasScroller.ZoomFactor;
            double next = direction > 0
                ? ZoomSteps.FirstOrDefault(f => f > z + 0.001, ZoomSteps[^1])
                : ZoomSteps.LastOrDefault(f => f < z - 0.001, ZoomSteps[0]);
            ZoomTo(next);
        }

        private void OnZoomFitClick(object sender, RoutedEventArgs e) => FitCanvasToContent();
        private void OnZoomActualClick(object sender, RoutedEventArgs e) => ZoomTo(1);
        private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomStep(+1);
        private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomStep(-1);
        private void OnZoom50Click(object sender, RoutedEventArgs e) => ZoomTo(0.50);
        private void OnZoom75Click(object sender, RoutedEventArgs e) => ZoomTo(0.75);
        private void OnZoom100Click(object sender, RoutedEventArgs e) => ZoomTo(1.00);
        private void OnZoom150Click(object sender, RoutedEventArgs e) => ZoomTo(1.50);
        private void OnZoom200Click(object sender, RoutedEventArgs e) => ZoomTo(2.00);

        private void OnZoomButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ZoomButton.Flyout?.ShowAt(ZoomButton);
        }

        private void OnStatusBarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var summary = $"{ViewModel.StatusMessage} {ViewModel.SelectionSummary}".Trim();
            CopyAndReport(summary, "Copied status to clipboard");
        }

        // Two clicks already opened the menu; don't also run the status bar's double-click copy.
        private void OnSelectionSummaryButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

        private void OnSelectionSummaryButtonClick(object sender, RoutedEventArgs e) =>
            KeyboardContextMenu.Invoke(SelectionSummaryButton, OnSelectionSummaryRightTapped);

        // Named after the summary, and hidden while it is blank (an empty canvas) so Tab doesn't stop
        // on an empty link. Focus moves to the canvas first if the button had it.
        private void SyncSelectionSummaryButton()
        {
            if (SelectionSummaryButton is null) return;
            var summary = ViewModel.SelectionSummary ?? "";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SelectionSummaryButton, summary);
            bool show = summary.Length > 0;
            if (!show && SelectionSummaryButton.FocusState != FocusState.Unfocused)
                CanvasScroller.Focus(FocusState.Programmatic);
            SelectionSummaryButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnSelectionSummaryRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (SelectionSummaryButton is null) return;
            e.Handled = true;
            var flyout = new MenuFlyout();
            bool any = ViewModel.HasShapes;
            bool hasSelection = ViewModel.SelectionCount > 0;

            var summary = ViewModel.SelectionSummary ?? string.Empty;
            flyout.Items.Add(ShapeMenuItem(!string.IsNullOrEmpty(summary) ? $"Copy summary ({summary})" : "Copy selection summary", "\uE946", () =>
            {
                CopyAndReport(summary, $"Copied selection summary: {summary}");
            }, enabled: !string.IsNullOrEmpty(summary)));

            flyout.Items.Add(ShapeMenuItem("Select all shapes", "\uE8B3", () => ViewModel.SelectAllCommand.Execute(null), "Ctrl+A", enabled: any && !ViewModel.IsDense));
            flyout.Items.Add(ShapeMenuItem("Clear selection", "\uE894", () => ViewModel.ClearSelection(), "Esc", enabled: hasSelection));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Duplicate selected", "\uE8C8", () => ViewModel.DuplicateSelectedCommand.Execute(null), "Ctrl+D", enabled: hasSelection));
            flyout.Items.Add(ShapeMenuItem("Delete selected", "\uE74D", () => ViewModel.RemoveSelectedCommand.Execute(null), "Del", enabled: hasSelection));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Bring to front", "\uE74A", () => ViewModel.BringToFrontCommand.Execute(null), "Ctrl+Shift+]", enabled: hasSelection));
            flyout.Items.Add(ShapeMenuItem("Send to back", "\uE74B", () => ViewModel.SendToBackCommand.Execute(null), "Ctrl+Shift+[", enabled: hasSelection));

            flyout.ShowAt(SelectionSummaryButton, KeyboardContextMenu.PointIn(e, SelectionSummaryButton));
        }

        private void OnStatusBarRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var target = sender as FrameworkElement ?? RootGrid;
            ShowStatusBarContextMenu(target, e.GetPosition(target));
        }

        private void ShowStatusBarContextMenu(FrameworkElement target, Point? position)
        {
            var flyout = new MenuFlyout();
            bool any = ViewModel.HasShapes;

            flyout.Items.Add(ShapeMenuItem("Copy status message", "\uE8C8", () =>
            {
                CopyAndReport(ViewModel.StatusMessage ?? "", "Copied status message to clipboard");
            }));

            flyout.Items.Add(ShapeMenuItem("Copy selection summary", "\uE946", () =>
            {
                CopyAndReport(ViewModel.SelectionSummary ?? "", "Copied selection summary to clipboard");
            }));

            flyout.Items.Add(ShapeMenuItem("Copy shapes as Markdown", "\uE8C8", () => OnCopyMarkdownClick(this, new RoutedEventArgs()), enabled: any));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Select all shapes", "\uE8B3", () => ViewModel.SelectAllCommand.Execute(null), "Ctrl+A", enabled: any && !ViewModel.IsDense));
            flyout.Items.Add(ShapeMenuItem("Fit diagram", "\uE9A6", FitCanvasToContent, "Ctrl+0", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Actual size (100%)", "\uE71E", () => ZoomTo(1.0), "Ctrl+1"));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Clear canvas", "\uE74D", () => OnClearClick(this, new RoutedEventArgs()), enabled: any));

            if (position.HasValue) flyout.ShowAt(target, position.Value);
            else flyout.ShowAt(target);
        }

        // ---- preset miniatures ----

        // Built once per preset and colour scheme on a throwaway studio; the list virtualises, so
        // only the rows scrolled into view ever pay for it.
        private static readonly Dictionary<string, IReadOnlyList<ShapeCanvasItemViewModel>> ThumbCache = new();
        private readonly HashSet<Canvas> _presetThumbs = new();

        private void OnPresetThumbLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Canvas c) return;
            _presetThumbs.Add(c);
            RenderPresetThumb(c);
        }

        private void OnPresetThumbUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Canvas c) _presetThumbs.Remove(c);
        }

        private void OnPresetThumbDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Canvas c) RenderPresetThumb(c);
        }

        private void RenderPresetThumb(Canvas c)
        {
            if (c.DataContext is not DiagramPreset preset || ViewModel is null) return;
            string palette = ViewModel.SelectedPaletteName ?? "Office Blue";
            string key = preset.Name + "|" + palette;
            if (c.Tag as string == key) return;
            c.Tag = key;
            c.Children.Clear();
            if (!ThumbCache.TryGetValue(key, out var shapes))
            {
                try { shapes = ShapeDesignStudioViewModel.PreviewPreset(preset, palette); }
                catch { shapes = Array.Empty<ShapeCanvasItemViewModel>(); }
                ThumbCache[key] = shapes;
            }
            if (shapes.Count == 0) return;

            double minX = shapes.Min(s => s.X), minY = shapes.Min(s => s.Y);
            double width = Math.Max(1, shapes.Max(s => s.X + s.Width) - minX);
            double height = Math.Max(1, shapes.Max(s => s.Y + s.Height) - minY);
            c.Width = width;
            c.Height = height;
            // The miniature is drawn at roughly 1/12 scale; a connector's real 2 pt stroke would vanish.
            double hairline = Math.Max(width / 54, height / 36);
            foreach (var s in shapes)
            {
                var p = new Microsoft.UI.Xaml.Shapes.Path { Width = Math.Max(1, s.Width), Height = Math.Max(1, s.Height), IsHitTestVisible = false };
                if (s.PathPoints is { Count: >= 2 })
                {
                    p.Stretch = Stretch.None;
                    p.Data = BuildPolylineGeometry(s.PathPoints, s.Width, s.Height);
                    p.Stroke = BrushFromHex(s.Fill);
                    p.StrokeThickness = Math.Max(s.StrokeWidthPt, hairline);
                }
                else
                {
                    var outline = PresetGeometry.Outline(s.Prst, s.Width, s.Height);
                    bool roundRect = string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase);
                    p.Stretch = outline is not null || roundRect ? Stretch.None : Stretch.Fill;
                    p.Data = outline is not null ? MakePolygonGeometry(outline)
                           : roundRect ? BuildRoundRectGeometry(s.Width, s.Height)
                           : MarkSmith.Converters.ShapeGeometries.For(s.Prst);
                    p.Fill = BrushFromHex(s.Fill);
                }
                if (s.Rotation != 0)
                {
                    p.RenderTransformOrigin = new Point(0.5, 0.5);
                    p.RenderTransform = new RotateTransform { Angle = s.Rotation };
                }
                Canvas.SetLeft(p, s.X - minX);
                Canvas.SetTop(p, s.Y - minY);
                c.Children.Add(p);
            }
        }

        // ---- Shapes tab palette ----

        /// <summary>Smallest tile the shape name still fits in (two lines of 11 px text).</summary>
        private const double MinPaletteTile = 78;

        private void OnPaletteGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PaletteGrid.ItemsPanelRoot is not ItemsWrapGrid panel || e.NewSize.Width <= 0) return;
            int columns = Math.Max(2, (int)(e.NewSize.Width / MinPaletteTile));
            panel.ItemWidth = Math.Floor(e.NewSize.Width / columns);
        }

        // ---- left pane tabs ----

        private void OnLeftTabsChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            var selected = sender.SelectedItem;
            PresetsPanel.Visibility = selected == PresetsTab ? Visibility.Visible : Visibility.Collapsed;
            ShapesPanel.Visibility = selected == ShapesTab ? Visibility.Visible : Visibility.Collapsed;
            PicturePanel.Visibility = selected == PictureTab ? Visibility.Visible : Visibility.Collapsed;
            // Leaving the Shapes tab puts a half-chosen tool down.
            if (selected != ShapesTab) ViewModel.ArmedTool = null;
        }

        private void OnLeftTabsRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (LeftTabs is null) return;
            e.Handled = true;
            var flyout = new MenuFlyout();

            flyout.Items.Add(ShapeMenuItem("Presets tab (Ctrl+1)", "\uF0E2", () =>
            {
                if (PresetsTab is not null) LeftTabs.SelectedItem = PresetsTab;
            }));

            flyout.Items.Add(ShapeMenuItem("Shapes library tab (Ctrl+2)", "\uE790", () =>
            {
                if (ShapesTab is not null) LeftTabs.SelectedItem = ShapesTab;
            }));

            flyout.Items.Add(ShapeMenuItem("Picture to vector tab (Ctrl+3)", "\uEB9F", () =>
            {
                if (PictureTab is not null) LeftTabs.SelectedItem = PictureTab;
            }));

            flyout.Items.Add(new MenuFlyoutSeparator());

            bool any = ViewModel.HasShapes;
            flyout.Items.Add(ShapeMenuItem("Copy shapes as Markdown", "\uE8C8", () => OnCopyMarkdownClick(this, new RoutedEventArgs()), "Ctrl+Shift+C", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Insert into document", "\uE8A5", () => ViewModel.InsertIntoDocumentCommand.Execute(null), enabled: any));

            flyout.ShowAt(LeftTabs, e.GetPosition(LeftTabs));
        }

        private void OnToolStripRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (ToolStrip is null) return;
            e.Handled = true;
            var flyout = new MenuFlyout();

            flyout.Items.Add(ShapeMenuItem("Undo", "\uE7A7", () => ViewModel.UndoCommand.Execute(null), "Ctrl+Z", enabled: ViewModel.CanUndo));
            flyout.Items.Add(ShapeMenuItem("Redo", "\uE7A6", () => ViewModel.RedoCommand.Execute(null), "Ctrl+Y", enabled: ViewModel.CanRedo));

            flyout.Items.Add(new MenuFlyoutSeparator());

            bool hasSelection = ViewModel.HasSelection;
            bool any = ViewModel.HasShapes;

            if (ViewModel.CanAlign)
            {
                var alignSub = new MenuFlyoutSubItem
                {
                    Text = "Align selection",
                    Icon = new FontIcon { Glyph = "\uE8E4" }
                };
                alignSub.Items.Add(ShapeMenuItem("Align left", "\uE8E4", () => ViewModel.AlignLeftCommand.Execute(null)));
                alignSub.Items.Add(ShapeMenuItem("Align centre", "\uE8E3", () => ViewModel.AlignCenterCommand.Execute(null)));
                alignSub.Items.Add(ShapeMenuItem("Align right", "\uE8E2", () => ViewModel.AlignRightCommand.Execute(null)));
                alignSub.Items.Add(new MenuFlyoutSeparator());
                alignSub.Items.Add(ShapeMenuItem("Align top", "\uE74A", () => ViewModel.AlignTopCommand.Execute(null)));
                alignSub.Items.Add(ShapeMenuItem("Align middle", "\uE8E3", () => ViewModel.AlignMiddleCommand.Execute(null)));
                alignSub.Items.Add(ShapeMenuItem("Align bottom", "\uE74B", () => ViewModel.AlignBottomCommand.Execute(null)));
                flyout.Items.Add(alignSub);
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            flyout.Items.Add(ShapeMenuItem("Duplicate selected", "\uE8C8", () => ViewModel.DuplicateSelectedCommand.Execute(null), "Ctrl+D", enabled: hasSelection));
            flyout.Items.Add(ShapeMenuItem("Delete selected", "\uE74D", () => ViewModel.RemoveSelectedCommand.Execute(null), "Del", enabled: hasSelection));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Select all shapes", "\uE8B3", () => ViewModel.SelectAllCommand.Execute(null), "Ctrl+A", enabled: any && !ViewModel.IsDense));
            flyout.Items.Add(ShapeMenuItem("Fit diagram", "\uE9A6", FitCanvasToContent, "Ctrl+0", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Actual size (100%)", "\uE71E", () => ZoomTo(1.0), "Ctrl+1"));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Clear canvas", "\uE74D", () => OnClearClick(this, new RoutedEventArgs()), enabled: any));

            var target = sender as FrameworkElement ?? ToolStrip;
            flyout.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
        }

        // ---- colour schemes ----

        private void BuildPaletteChoices()
        {
            foreach (var name in ViewModel.PaletteNames)
            {
                var colours = ShapeDesignStudioViewModel.ColorPalettes.TryGetValue(name, out var c) ? c : Array.Empty<string>();
                _paletteChoices.Add(new PaletteChoice { Name = name, Swatches = colours.Select(BrushFromHex).ToList() });
            }
            PaletteList.ItemsSource = _paletteChoices;
            SyncPaletteUi();
        }

        private void SyncPaletteUi()
        {
            _syncingPalette = true;
            try
            {
                PaletteList.SelectedItem = _paletteChoices.FirstOrDefault(p => p.Name == ViewModel.SelectedPaletteName);
                PaletteButtonSwatches.Children.Clear();
                var choice = PaletteList.SelectedItem as PaletteChoice;
                foreach (var b in (choice?.Swatches ?? new List<SolidColorBrush>()).Take(4))
                {
                    var swatch = new Border
                    {
                        Width = 8,
                        Height = 14,
                        CornerRadius = new CornerRadius(2),
                        Background = b,
                        BorderThickness = new Thickness(1)
                    };
                    ThemeBrush.Set(swatch, Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                    PaletteButtonSwatches.Children.Add(swatch);
                }
            }
            finally { _syncingPalette = false; }
        }

        private void OnPaletteListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingPalette || PaletteList.SelectedItem is not PaletteChoice choice) return;
            ViewModel.SelectedPaletteName = choice.Name;
            if (ViewModel.HasShapes && !ViewModel.IsDense) ViewModel.ApplyPaletteThemeCommand.Execute(null);
            else ViewModel.StatusMessage = $"Colour scheme: {choice.Name} — presets and new shapes will use it.";
            PaletteFlyout.Hide();
        }

        private void OnPaletteButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (PaletteButton is null) return;
            e.Handled = true;
            var flyout = new MenuFlyout();

            foreach (var choice in _paletteChoices)
            {
                bool isSelected = string.Equals(ViewModel.SelectedPaletteName, choice.Name, StringComparison.OrdinalIgnoreCase);
                var item = ShapeMenuItem(isSelected ? $"{choice.Name} (active)" : choice.Name, isSelected ? "\uE73E" : "\uE790", () =>
                {
                    ViewModel.SelectedPaletteName = choice.Name;
                    if (ViewModel.HasShapes && !ViewModel.IsDense) ViewModel.ApplyPaletteThemeCommand.Execute(null);
                    else ViewModel.StatusMessage = $"Colour scheme: {choice.Name} — presets and new shapes will use it.";
                });
                flyout.Items.Add(item);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            var copyNameItem = ShapeMenuItem($"Copy scheme name ({ViewModel.SelectedPaletteName})", "\uE8C8", () =>
            {
                var name = ViewModel.SelectedPaletteName ?? string.Empty;
                CopyAndReport(name, $"Copied scheme name: {name}");
            });
            flyout.Items.Add(copyNameItem);

            flyout.ShowAt(PaletteButton, KeyboardContextMenu.PointIn(e, PaletteButton));
        }

        // ---- keyboard ----
        // Del delete · Ctrl+D duplicate · Ctrl+A select all · Ctrl+Z / Ctrl+Y undo/redo · Esc cancels the
        // armed tool, then the selection · arrows nudge 1 px (Shift: 10 px) while the canvas has focus.
        // Text fields keep their own keys, but Ctrl+F and Escape route globally.
        private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var focused = FocusManager.GetFocusedElement(this.RootGrid.XamlRoot);
            // An open menu or flyout routes its keys through here too (its popup sits outside RootGrid).
            // They are its own: Esc closes it, Delete and Ctrl+C / V / Z act inside it. Esc used to
            // clear the selection behind a still-open menu, and Delete removed the selected shapes.
            if (focused is DependencyObject f && !IsWithinRootGrid(f)) return;
            bool ctrl = IsDown(VirtualKey.Control);
            bool shift = IsDown(VirtualKey.Shift);

            // Ctrl+F jumps to presets tab from anywhere
            if (ctrl && e.Key == VirtualKey.F)
            {
                LeftTabs.SelectedItem = PresetsTab;
                PresetCategoryCombo?.Focus(FocusState.Programmatic);
                ViewModel.StatusMessage = "Preset gallery focused — choose a category or diagram preset.";
                e.Handled = true;
                return;
            }

            // Escape in any text box unfocuses and returns to the canvas
            if (e.Key == VirtualKey.Escape && focused is TextBox)
            {
                CanvasScroller.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }

            if (focused is TextBox or PasswordBox or AutoSuggestBox) return;
            if (focused is DependencyObject d && IsInsideNumberBox(d)) return;
            // An open list owns its keys: Esc closes it, arrows/Home/End move through it.
            if (focused is ComboBoxItem || focused is ComboBox { IsDropDownOpen: true }) return;
            // Delete and Ctrl+A in the preset list, shape palette, colour schemes or a slider aren't
            // about the canvas: Delete on a preset deleted the selected shapes. The Shapes list lists
            // the canvas shapes themselves, so it keeps both.
            bool inChoice = ChoiceKeys.FocusIsInChoiceControl(this.RootGrid.XamlRoot, ShapesList);

            if (ctrl && e.Key == VirtualKey.C && !shift)
            {
                OnCopyMarkdownClick(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == VirtualKey.V && !shift)
            {
                OnLoadMarkdownClick(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (IsDown(VirtualKey.Menu))
            {
                if (e.Key is VirtualKey.Number1 or VirtualKey.NumberPad1)
                {
                    LeftTabs.SelectedItem = PresetsTab;
                    e.Handled = true;
                    return;
                }
                if (e.Key is VirtualKey.Number2 or VirtualKey.NumberPad2)
                {
                    LeftTabs.SelectedItem = ShapesTab;
                    e.Handled = true;
                    return;
                }
                if (e.Key is VirtualKey.Number3 or VirtualKey.NumberPad3)
                {
                    LeftTabs.SelectedItem = PictureTab;
                    e.Handled = true;
                    return;
                }
            }
            if (ctrl && e.Key == VirtualKey.Tab)
            {
                int curIdx = LeftTabs.SelectedItem == PresetsTab ? 0 : LeftTabs.SelectedItem == ShapesTab ? 1 : 2;
                int nextIdx = shift ? (curIdx + 2) % 3 : (curIdx + 1) % 3;
                LeftTabs.SelectedItem = nextIdx switch { 0 => PresetsTab, 1 => ShapesTab, _ => PictureTab };
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case VirtualKey.Z when ctrl && shift:
                case VirtualKey.Y when ctrl:
                    ViewModel.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.Z when ctrl:
                    ViewModel.UndoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.Number0 or VirtualKey.NumberPad0 when ctrl:
                    FitCanvasToContent();
                    e.Handled = true;
                    return;
                case VirtualKey.Number1 or VirtualKey.NumberPad1 when ctrl:
                    ZoomTo(1);
                    e.Handled = true;
                    return;
                case (VirtualKey)187 or VirtualKey.Add when ctrl: // '=' and '+'
                    ZoomStep(+1);
                    e.Handled = true;
                    return;
                case (VirtualKey)189 or VirtualKey.Subtract when ctrl: // '-'
                    ZoomStep(-1);
                    e.Handled = true;
                    return;
                case VirtualKey.A when ctrl && !inChoice:
                    ViewModel.SelectAllCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.D when ctrl && !inChoice && ViewModel.HasSelection:
                    ExecuteDuplicateWithHudFeedback();
                    e.Handled = true;
                    return;
                case VirtualKey.D when ctrl && !inChoice && !ViewModel.HasSelection:
                    ViewModel.StatusMessage = "Nothing to duplicate: select a shape on the canvas first.";
                    e.Handled = true;
                    return;
                case (VirtualKey)221 when ctrl && shift && !inChoice && ViewModel.HasSelection: // Ctrl+Shift+]
                    ViewModel.BringToFrontCommand.Execute(null);
                    e.Handled = true;
                    return;
                case (VirtualKey)221 when ctrl && !inChoice && ViewModel.HasSelection: // Ctrl+]
                    ViewModel.BringForwardCommand.Execute(null);
                    e.Handled = true;
                    return;
                case (VirtualKey)219 when ctrl && shift && !inChoice && ViewModel.HasSelection: // Ctrl+Shift+[
                    ViewModel.SendToBackCommand.Execute(null);
                    e.Handled = true;
                    return;
                case (VirtualKey)219 when ctrl && !inChoice && ViewModel.HasSelection: // Ctrl+[
                    ViewModel.SendBackwardCommand.Execute(null);
                    e.Handled = true;
                    return;
                case (VirtualKey)221 or (VirtualKey)219 when ctrl && !inChoice && !ViewModel.HasSelection:
                    ViewModel.StatusMessage = "Nothing to reorder: select a shape on the canvas first.";
                    e.Handled = true;
                    return;
                case VirtualKey.F2 when ViewModel.SelectedShape is { IsLine: false }:
                    FocusLabelEditor();
                    e.Handled = true;
                    return;
                // A toolbar control with its own menu opens that menu, not the canvas one.
                case VirtualKey.Application when !KeyboardContextMenu.IsFromBridgedControl(e):
                case VirtualKey.F10 when shift && !KeyboardContextMenu.IsFromBridgedControl(e):
                    if (ViewModel.SelectedShape is { } selected && _shapePaths.TryGetValue(selected, out var path) && path.Parent is FrameworkElement host)
                        ShowShapeContextMenu(host, new Point(host.ActualWidth / 2, host.ActualHeight / 2));
                    else if (ViewModel.SelectedShape is not null)
                        ShowShapeContextMenu(MainCanvas, new Point(120, 120));
                    else
                        ShowCanvasContextMenu(new Point(160, 140));
                    e.Handled = true;
                    return;
                case (VirtualKey.Delete or VirtualKey.Back) when !inChoice && ViewModel.HasSelection:
                    ViewModel.RemoveSelectedCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.Delete when !inChoice && !ViewModel.HasSelection:
                    ViewModel.StatusMessage = ViewModel.IsEmpty
                        ? "Nothing to delete: the canvas is empty."
                        : "Nothing to delete: select a shape on the canvas first.";
                    e.Handled = true;
                    return;
                case VirtualKey.Escape when ViewModel.IsPlacing:
                    ViewModel.ArmedTool = null;
                    ViewModel.StatusMessage = "Placing cancelled.";
                    e.Handled = true;
                    return;
                case VirtualKey.Escape when ViewModel.HasSelection:
                    ViewModel.ClearSelection();
                    e.Handled = true;
                    return;
                case VirtualKey.E when IsDown(VirtualKey.Menu) && ViewModel.SelectedShape is { } selLine && selLine.PathPoints is { Count: >= 2 }:
                    AutoRouteConnectorAroundObstacles(selLine);
                    e.Handled = true;
                    return;
            }

            if (IsDown(VirtualKey.Menu) && ViewModel.HasSelection &&
                e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Number0 or VirtualKey.NumberPad0)
            {
                if (e.Key is VirtualKey.Number0 or VirtualKey.NumberPad0)
                {
                    ViewModel.ResetRotation();
                }
                else
                {
                    int delta = (shift ? 90 : 15) * (e.Key == VirtualKey.Left ? -1 : 1);
                    ViewModel.RotateSelectionBy(delta);
                }
                e.Handled = true;
                return;
            }

            if (ReferenceEquals(focused, CanvasScroller) && ViewModel.HasSelection &&
                e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            {
                double step = shift ? 10 : 1;
                if (!e.KeyStatus.WasKeyDown) ViewModel.RecordUndo(); // one undo step per key press, not per repeat
                ViewModel.NudgeSelection(
                    e.Key == VirtualKey.Left ? -step : e.Key == VirtualKey.Right ? step : 0,
                    e.Key == VirtualKey.Up ? -step : e.Key == VirtualKey.Down ? step : 0);
                e.Handled = true;
            }
        }

        private static bool IsDown(VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        private bool IsWithinRootGrid(DependencyObject? obj)
        {
            for (; obj is not null; obj = VisualTreeHelper.GetParent(obj))
                if (ReferenceEquals(obj, RootGrid)) return true;
            return false;
        }

        private static bool IsInsideNumberBox(DependencyObject? obj)
        {
            while (obj != null)
            {
                if (obj is NumberBox) return true;
                obj = VisualTreeHelper.GetParent(obj);
            }
            return false;
        }

        private void CommitFillHexBox()
        {
            if (ViewModel.SelectedShape is not { } shape || FillHexBox is null) return;
            string normalized = ShapeCanvasItemViewModel.NormalizeHexInput(FillHexBox.Text, shape.Fill);
            shape.Fill = normalized;
            if (FillHexBox.Text != shape.Fill) FillHexBox.Text = shape.Fill;
        }

        private void OnFillHexBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Escape)
            {
                CommitFillHexBox();
                CanvasScroller.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        }

        private void OnFillHexBoxLostFocus(object sender, RoutedEventArgs e) => CommitFillHexBox();

        // Inspector edits (type, position, size, fill, rotation, label) get one undo step per shape:
        // the canvas is recorded the first time focus enters the inspector for a given selection.
        private void OnInspectorGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            var shape = ViewModel.SelectedShape;
            if (shape is null || ReferenceEquals(shape, _inspectorUndoFor)) return;
            _inspectorUndoFor = shape;
            ViewModel.RecordUndo();
        }

        private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ShapeDesignStudioViewModel.SelectedShape):
                    if (!ReferenceEquals(ViewModel.SelectedShape, _inspectorUndoFor)) _inspectorUndoFor = null;
                    if (ViewModel.SelectedShape is { } sel) ShapesList.ScrollIntoView(sel);
                    TrackAdornedShape(ViewModel.SelectedShape);
                    UpdateCornerRadiusButtonState();
                    UpdateInspectorLayerUi();
                    UpdateSelectionBoundsPill();
                    return;
                case nameof(ShapeDesignStudioViewModel.SelectionCount):
                case nameof(ShapeDesignStudioViewModel.IsDense):
                    UpdateAdorner();
                    UpdateSelectionBoundsPill();
                    return;
                case nameof(ShapeDesignStudioViewModel.SelectionSummary):
                    SyncSelectionSummaryButton();
                    return;
                case nameof(ShapeDesignStudioViewModel.SelectedPaletteName):
                    SyncPaletteUi();
                    foreach (var thumb in _presetThumbs) RenderPresetThumb(thumb);
                    return;
                case nameof(ShapeDesignStudioViewModel.ArmedTool):
                    SetCursor(MainCanvas, ViewModel.IsPlacing ? Microsoft.UI.Input.InputSystemCursorShape.Cross : Microsoft.UI.Input.InputSystemCursorShape.Arrow);
                    UpdateAdorner();
                    if (ViewModel.ArmedTool is { } tool)
                    {
                        _armedStatus = $"{ShapeCanvasItemViewModel.DisplayNameFor(tool)} — click the canvas to place it, or drag to draw it.";
                        ViewModel.StatusMessage = _armedStatus;
                    }
                    else if (ViewModel.StatusMessage == _armedStatus)
                    {
                        // Put down without placing (tab switch): don't leave the instruction behind.
                        ViewModel.StatusMessage = "Ready.";
                    }
                    return;
                case nameof(ShapeDesignStudioViewModel.StatusMessage):
                    if (StatusBarText != null && !string.IsNullOrEmpty(ViewModel.StatusMessage))
                    {
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusBarText, ViewModel.StatusMessage);
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(StatusBarText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
                    }
                    return;
                case nameof(ShapeDesignStudioViewModel.PreviewPng):
                    break;
                default:
                    return;
            }

            try
            {
                var bytes = ViewModel.PreviewPng;
                if (bytes == null)
                {
                    DensePreview.Source = null;
                    return;
                }
                var bmp = await BitmapFromBytesAsync(bytes);
                // A newer trace (or ClearAll) may have replaced the bytes while we were decoding —
                // never let a stale bitmap win over the current state.
                if (!ReferenceEquals(bytes, ViewModel.PreviewPng)) return;
                DensePreview.Source = bmp;
            }
            catch { /* decode failure — preview stays blank, studio keeps working */ }
        }

        private static async System.Threading.Tasks.Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage> BitmapFromBytesAsync(byte[] bytes)
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bmp.SetSourceAsync(stream);
            return bmp;
        }

        // ---- canvas: select, place, drag-to-draw ----

        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            CanvasScroller.Focus(FocusState.Pointer);
            var pt = e.GetCurrentPoint(MainCanvas);
            var pos = pt.Position;
            if (pt.Properties.IsRightButtonPressed)
            {
                if (ViewModel.IsPlacing)
                {
                    ViewModel.ArmedTool = null;
                    ViewModel.StatusMessage = "Placing cancelled.";
                    e.Handled = true;
                    return;
                }
                ShowCanvasContextMenu(pos);
                e.Handled = true;
                return;
            }
            if (ViewModel.ArmedTool is null)
            {
                // Select tool: a click on empty canvas clears the selection; a drag draws a
                // rubber band that selects every shape it touches (Ctrl/Shift adds to it).
                _marqueeAdditive = IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift);
                if (!_marqueeAdditive) ViewModel.ClearSelection();
                if (ViewModel.IsDense) { e.Handled = true; return; }
                _marquee = true;
                _marqueeStart = pos;
                MainCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            _drawing = true;
            _drawStart = pos;
            Canvas.SetLeft(DrawGhost, pos.X);
            Canvas.SetTop(DrawGhost, pos.Y);
            DrawGhost.Width = 0;
            DrawGhost.Height = 0;
            MainCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_marquee)
            {
                var m = DragRect(_marqueeStart, e.GetCurrentPoint(MainCanvas).Position);
                if (m.Width < 3 && m.Height < 3) return;
                MarqueeRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(MarqueeRect, m.X);
                Canvas.SetTop(MarqueeRect, m.Y);
                MarqueeRect.Width = m.Width;
                MarqueeRect.Height = m.Height;
                MarqueeRect.StrokeThickness = 1 / CanvasScroller.ZoomFactor;
                e.Handled = true;
                return;
            }
            if (!_drawing) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            var r = DragRect(_drawStart, cur);
            if (r.Width < 4 && r.Height < 4) return;
            DrawGhost.Visibility = Visibility.Visible;
            Canvas.SetLeft(DrawGhost, r.X);
            Canvas.SetTop(DrawGhost, r.Y);
            DrawGhost.Width = r.Width;
            DrawGhost.Height = r.Height;
            e.Handled = true;
        }

        private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_marquee)
            {
                // Finish first: releasing capture raises PointerCaptureLost synchronously, which
                // cancels the band.
                FinishMarquee(e.GetCurrentPoint(MainCanvas).Position);
                MainCanvas.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
                return;
            }
            if (!_drawing) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            MainCanvas.ReleasePointerCapture(e.Pointer);
            FinishDraw(cur);
            e.Handled = true;
        }

        private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _marquee = false;
            MarqueeRect.Visibility = Visibility.Collapsed;
            if (!_drawing) return;
            _drawing = false;
            DrawGhost.Visibility = Visibility.Collapsed;
        }

        private void FinishMarquee(Point end)
        {
            bool dragged = MarqueeRect.Visibility == Visibility.Visible;
            _marquee = false;
            MarqueeRect.Visibility = Visibility.Collapsed;
            if (!dragged) return; // a plain click: the selection was already cleared on press
            var r = DragRect(_marqueeStart, end);
            int hit = ViewModel.SelectInRect(r.X, r.Y, r.Width, r.Height, _marqueeAdditive);
            int n = ViewModel.SelectionCount;
            ViewModel.StatusMessage = hit == 0 && n == 0 ? "No shapes there — drag across a shape to select it."
                : n == 1 ? $"Selected {ViewModel.SelectedShape?.DisplayName.ToLowerInvariant()}"
                : $"Selected {n} shapes";
        }

        private void FinishDraw(Point end)
        {
            _drawing = false;
            DrawGhost.Visibility = Visibility.Collapsed;
            var tool = ViewModel.ArmedTool;
            if (tool is null) return;

            var r = DragRect(_drawStart, end);
            ViewModel.RecordUndo();
            if (r.Width >= 12 && r.Height >= 12)
            {
                // Dragged out: exactly the rectangle the user drew.
                ViewModel.AddShapeAt(tool, r.X, r.Y, r.Width, r.Height);
            }
            else
            {
                // Click: the default size, centred on the click.
                ViewModel.AddShapeAt(tool, Math.Max(0, _drawStart.X - 60), Math.Max(0, _drawStart.Y - 35));
            }
            // One shape per pick: the tool goes back to Select so the next click selects instead
            // of stamping another copy.
            ViewModel.ArmedTool = null;
        }

        private static Rect DragRect(Point a, Point b) =>
            new(Math.Max(0, Math.Min(a.X, b.X)), Math.Max(0, Math.Min(a.Y, b.Y)), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

        // ---- shapes: click / Ctrl+click / drag the selection / context menus ----

        private void OnShapePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // While placing, a click on top of a shape still places (let it reach the canvas).
            if (ViewModel.IsPlacing) return;
            if (sender is not FrameworkElement fe || fe.DataContext is not ShapeCanvasItemViewModel shape) return;
            CanvasScroller.Focus(FocusState.Pointer);

            var pt = e.GetCurrentPoint(fe);
            if (pt.Properties.IsRightButtonPressed)
            {
                ViewModel.ClickSelect(shape);
                ShowShapeContextMenu(fe, pt.Position);
                e.Handled = true;
                return;
            }

            if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift))
            {
                ViewModel.ToggleSelection(shape);
                e.Handled = true;
                return;
            }

            ViewModel.ClickSelect(shape);
            _dragShape = shape;
            _dragMoved = false;
            _dragAppliedX = _dragAppliedY = 0;
            _dragStart = e.GetCurrentPoint(MainCanvas).Position;
            fe.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnShapeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ShapeCanvasItemViewModel shape) return;
            if (IsDown(VirtualKey.Menu))
            {
                e.Handled = true;
                ViewModel.RecordUndo();
                shape.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(360 - shape.Rotation);
                ViewModel.StatusMessage = $"Flipped {shape.DisplayName.ToLowerInvariant()} horizontally ({shape.Rotation}°) · Ctrl+Z to undo";
                UpdateAdorner();
                return;
            }
            ViewModel.ClickSelect(shape);
            if (LabelTextBox is not null)
            {
                LabelTextBox.Focus(FocusState.Programmatic);
                LabelTextBox.SelectAll();
                ViewModel.StatusMessage = $"Editing label for {shape.Name} · Press Enter or Esc when done";
            }
            e.Handled = true;
        }

        private void OnLabelTextBoxDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (LabelTextBox is null) return;
            e.Handled = true;
            LabelTextBox.SelectAll();
        }

        private void OnLabelTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                CanvasScroller?.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        }

        private void FillLabelTextBoxMenu(MenuFlyout flyout)
        {
            if (LabelTextBox is null) return;

            string currentText = LabelTextBox.Text ?? string.Empty;
            bool hasText = !string.IsNullOrEmpty(currentText);

            flyout.Items.Add(ShapeMenuItem("Copy text", "\uE8C8", () =>
            {
                CopyAndReport(currentText, "Copied label to clipboard");
            }, "Ctrl+C", enabled: hasText));

            flyout.Items.Add(ShapeMenuItem("Paste text", "\uE77F", async () =>
            {
                try
                {
                    var dpView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (dpView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    {
                        var text = await dpView.GetTextAsync();
                        if (ViewModel.SelectedShape is not null)
                        {
                            ViewModel.RecordUndo();
                            ViewModel.SelectedShape.Text = text;
                            LabelTextBox.Text = text;
                            ViewModel.StatusMessage = "Pasted label from clipboard";
                        }
                    }
                }
                catch { }
            }, "Ctrl+V"));

            flyout.Items.Add(ShapeMenuItem("Clear label", "\uE894", () =>
            {
                if (ViewModel.SelectedShape is not null)
                {
                    ViewModel.RecordUndo();
                    ViewModel.SelectedShape.Text = string.Empty;
                    LabelTextBox.Text = string.Empty;
                    ViewModel.StatusMessage = "Cleared shape label";
                }
            }, enabled: hasText));

            flyout.Items.Add(new MenuFlyoutSeparator());

            var casingSub = new MenuFlyoutSubItem
            {
                Text = "Change case",
                Icon = new FontIcon { Glyph = "\uE8D2" },
                IsEnabled = hasText
            };
            casingSub.Items.Add(ShapeMenuItem("UPPERCASE", "\uE8D2", () =>
            {
                if (ViewModel.SelectedShape is not null)
                {
                    ViewModel.RecordUndo();
                    var upper = currentText.ToUpperInvariant();
                    ViewModel.SelectedShape.Text = upper;
                    LabelTextBox.Text = upper;
                }
            }));
            casingSub.Items.Add(ShapeMenuItem("lowercase", "\uE8D2", () =>
            {
                if (ViewModel.SelectedShape is not null)
                {
                    ViewModel.RecordUndo();
                    var lower = currentText.ToLowerInvariant();
                    ViewModel.SelectedShape.Text = lower;
                    LabelTextBox.Text = lower;
                }
            }));
            casingSub.Items.Add(ShapeMenuItem("Title Case", "\uE8D2", () =>
            {
                if (ViewModel.SelectedShape is not null)
                {
                    ViewModel.RecordUndo();
                    var title = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(currentText.ToLower());
                    ViewModel.SelectedShape.Text = title;
                    LabelTextBox.Text = title;
                }
            }));
            flyout.Items.Add(casingSub);

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Select all text", "\uE8B3", () =>
            {
                LabelTextBox.Focus(FocusState.Programmatic);
                LabelTextBox.SelectAll();
            }, "Ctrl+A", enabled: hasText));
        }

        private void OnShapesListItemRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ShapeCanvasItemViewModel shape) return;
            ViewModel.ClickSelect(shape);
            ShowShapeContextMenu(fe, e.GetPosition(fe));
            e.Handled = true;
        }

        private void OnShapesListKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is not { } selected) return;

            bool ctrl = IsDown(VirtualKey.Control);
            bool shift = IsDown(VirtualKey.Shift);

            if (e.Key == VirtualKey.Application || (e.Key == VirtualKey.F10 && shift))
            {
                var anchor = (e.OriginalSource as FrameworkElement) ?? ShapesList;
                ShowShapeContextMenu(anchor, new Point(48, 24));
                e.Handled = true;
            }
            else if (e.Key is VirtualKey.Delete or VirtualKey.Back)
            {
                ViewModel.RemoveSelectedCommand.Execute(null);
                e.Handled = true;
            }
            else if (ctrl && e.Key == VirtualKey.D)
            {
                ExecuteDuplicateWithHudFeedback();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.F2 && !selected.IsLine)
            {
                FocusLabelEditor();
                e.Handled = true;
            }
        }

        private void FocusLabelEditor()
        {
            if (ViewModel.SelectedShape is null || ViewModel.SelectedShape.IsLine || LabelTextBox is null) return;
            LabelTextBox.Focus(FocusState.Programmatic);
            LabelTextBox.SelectAll();
        }

        private static MenuFlyoutItem ShapeMenuItem(string text, string glyph, Action onClick, string? shortcut = null, bool enabled = true)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph },
                IsEnabled = enabled
            };
            if (!string.IsNullOrEmpty(shortcut)) item.KeyboardAcceleratorTextOverride = shortcut;
            item.Click += (_, _) => onClick();
            return item;
        }

        private void ShowShapeContextMenu(FrameworkElement anchor, Point position)
        {
            if (ViewModel.SelectedShape is null) return;
            var flyout = new MenuFlyout();
            bool isLine = ViewModel.SelectedShape.IsLine;

            if (!isLine)
            {
                flyout.Items.Add(ShapeMenuItem("Edit label", "\uE70F", FocusLabelEditor, "F2"));
            }
            flyout.Items.Add(ShapeMenuItem("Duplicate", "\uE8C8", () => ViewModel.DuplicateSelectedCommand.Execute(null), "Ctrl+D"));
            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Bring to front", "\uE74A", () => ViewModel.BringToFrontCommand.Execute(null), "Ctrl+Shift+]"));
            flyout.Items.Add(ShapeMenuItem("Bring forward", "\uE74A", () => ViewModel.BringForwardCommand.Execute(null), "Ctrl+]"));
            flyout.Items.Add(ShapeMenuItem("Send backward", "\uE74B", () => ViewModel.SendBackwardCommand.Execute(null), "Ctrl+["));
            flyout.Items.Add(ShapeMenuItem("Send to back", "\uE74B", () => ViewModel.SendToBackCommand.Execute(null), "Ctrl+Shift+["));

            if (!isLine)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(ShapeMenuItem("Rotate 90° clockwise", "\uE7AD", () => ViewModel.RotateClockwiseCommand.Execute(null), "Alt+Shift+Right"));
                flyout.Items.Add(ShapeMenuItem("Rotate 90° counter-clockwise", "\uE80C", () => ViewModel.RotateCounterClockwiseCommand.Execute(null), "Alt+Shift+Left"));
                flyout.Items.Add(ShapeMenuItem("Reset rotation (0°)", "\uE777", () => ViewModel.ResetRotationCommand.Execute(null), "Alt+0",
                    enabled: ShapeDesignStudioViewModel.WrapRotationAngle(ViewModel.SelectedShape.Rotation) != 0));
            }

            if (ViewModel.CanAlign)
            {
                var alignSub = new MenuFlyoutSubItem
                {
                    Text = "Align selection",
                    Icon = new FontIcon { Glyph = "\uE8E4" }
                };
                alignSub.Items.Add(ShapeMenuItem("Align left", "\uE8E4", () => ViewModel.AlignLeftCommand.Execute(null)));
                alignSub.Items.Add(ShapeMenuItem("Align centre", "\uE8E3", () => ViewModel.AlignCenterCommand.Execute(null)));
                alignSub.Items.Add(ShapeMenuItem("Align right", "\uE8E2", () => ViewModel.AlignRightCommand.Execute(null)));
                alignSub.Items.Add(new MenuFlyoutSeparator());
                alignSub.Items.Add(ShapeMenuItem("Align top", "\uE74A", () => ViewModel.AlignTopCommand.Execute(null)));
                alignSub.Items.Add(ShapeMenuItem("Align middle", "\uE8E3", () => ViewModel.AlignMiddleCommand.Execute(null)));
                alignSub.Items.Add(ShapeMenuItem("Align bottom", "\uE74B", () => ViewModel.AlignBottomCommand.Execute(null)));
                if (ViewModel.CanDistribute)
                {
                    alignSub.Items.Add(new MenuFlyoutSeparator());
                    alignSub.Items.Add(ShapeMenuItem("Space left edges evenly", "\uE8AB", () => ViewModel.DistributeHorizontalCommand.Execute(null)));
                    alignSub.Items.Add(ShapeMenuItem("Space top edges evenly", "\uE8CB", () => ViewModel.DistributeVerticalCommand.Execute(null)));
                }
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(alignSub);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Delete", "\uE74D", () => ViewModel.RemoveSelectedCommand.Execute(null), "Del"));

            flyout.ShowAt(anchor, position);
        }

        private void ShowCanvasContextMenu(Point position)
        {
            var flyout = new MenuFlyout();
            bool any = ViewModel.HasShapes;

            if (ViewModel.HasSelection)
            {
                flyout.Items.Add(ShapeMenuItem("Duplicate selection", "\uE8C8", () => ViewModel.DuplicateSelectedCommand.Execute(null), "Ctrl+D"));
                flyout.Items.Add(ShapeMenuItem("Delete selection", "\uE74D", () => ViewModel.RemoveSelectedCommand.Execute(null), "Del"));

                var layerSub = new MenuFlyoutSubItem
                {
                    Text = "Layer order",
                    Icon = new FontIcon { Glyph = "\uE74A" }
                };
                layerSub.Items.Add(ShapeMenuItem("Bring to front", "\uE74A", () => ViewModel.BringToFrontCommand.Execute(null), "Ctrl+Shift+]"));
                layerSub.Items.Add(ShapeMenuItem("Bring forward", "\uE74A", () => ViewModel.BringForwardCommand.Execute(null), "Ctrl+]"));
                layerSub.Items.Add(ShapeMenuItem("Send backward", "\uE74B", () => ViewModel.SendBackwardCommand.Execute(null), "Ctrl+["));
                layerSub.Items.Add(ShapeMenuItem("Send to back", "\uE74B", () => ViewModel.SendToBackCommand.Execute(null), "Ctrl+Shift+["));
                flyout.Items.Add(layerSub);

                if (ViewModel.CanAlign)
                {
                    var alignSub = new MenuFlyoutSubItem
                    {
                        Text = "Align selection",
                        Icon = new FontIcon { Glyph = "\uE8E4" }
                    };
                    alignSub.Items.Add(ShapeMenuItem("Align left", "\uE8E4", () => ViewModel.AlignLeftCommand.Execute(null)));
                    alignSub.Items.Add(ShapeMenuItem("Align centre", "\uE8E3", () => ViewModel.AlignCenterCommand.Execute(null)));
                    alignSub.Items.Add(ShapeMenuItem("Align right", "\uE8E2", () => ViewModel.AlignRightCommand.Execute(null)));
                    alignSub.Items.Add(new MenuFlyoutSeparator());
                    alignSub.Items.Add(ShapeMenuItem("Align top", "\uE74A", () => ViewModel.AlignTopCommand.Execute(null)));
                    alignSub.Items.Add(ShapeMenuItem("Align middle", "\uE8E3", () => ViewModel.AlignMiddleCommand.Execute(null)));
                    alignSub.Items.Add(ShapeMenuItem("Align bottom", "\uE74B", () => ViewModel.AlignBottomCommand.Execute(null)));
                    if (ViewModel.CanDistribute)
                    {
                        alignSub.Items.Add(new MenuFlyoutSeparator());
                        alignSub.Items.Add(ShapeMenuItem("Space left edges evenly", "\uE8AB", () => ViewModel.DistributeHorizontalCommand.Execute(null)));
                        alignSub.Items.Add(ShapeMenuItem("Space top edges evenly", "\uE8CB", () => ViewModel.DistributeVerticalCommand.Execute(null)));
                    }
                    flyout.Items.Add(alignSub);
                }

                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            var insertSub = new MenuFlyoutSubItem
            {
                Text = "Insert shape",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            insertSub.Items.Add(ShapeMenuItem("Process (Rectangle)", "\uE735", () => ViewModel.AddShapeAt("rect", position.X, position.Y)));
            insertSub.Items.Add(ShapeMenuItem("Terminal (Rounded rectangle)", "\uE735", () => ViewModel.AddShapeAt("roundrect", position.X, position.Y)));
            insertSub.Items.Add(ShapeMenuItem("Circle (Ellipse)", "\uE707", () => ViewModel.AddShapeAt("ellipse", position.X, position.Y)));
            insertSub.Items.Add(ShapeMenuItem("Decision (Diamond)", "\uF0E2", () => ViewModel.AddShapeAt("diamond", position.X, position.Y)));
            insertSub.Items.Add(ShapeMenuItem("Data store (Cylinder)", "\uE81E", () => ViewModel.AddShapeAt("cylinder", position.X, position.Y)));
            insertSub.Items.Add(ShapeMenuItem("Directional (Chevron)", "\uE76C", () => ViewModel.AddShapeAt("chevron", position.X, position.Y)));
            flyout.Items.Add(insertSub);
            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Paste shapes from clipboard", "\uE77F", () => OnLoadMarkdownClick(this, new RoutedEventArgs())));
            flyout.Items.Add(ShapeMenuItem("Copy shapes as Markdown", "\uE8C8", () => OnCopyMarkdownClick(this, new RoutedEventArgs()), enabled: any));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Select all", "\uE8B3", () => ViewModel.SelectAllCommand.Execute(null), "Ctrl+A", enabled: any && !ViewModel.IsDense));

            if (any && ViewModel.HasSelection)
            {
                flyout.Items.Add(ShapeMenuItem("Invert selection", "\uE8B3", () =>
                {
                    var selected = ViewModel.Selection.ToHashSet();
                    ViewModel.ClearSelection();
                    foreach (var s in ViewModel.Shapes)
                    {
                        if (!selected.Contains(s)) ViewModel.ToggleSelection(s);
                    }
                }));
                flyout.Items.Add(ShapeMenuItem("Deselect all", "\uE894", () => ViewModel.ClearSelection(), "Esc"));
            }

            flyout.Items.Add(ShapeMenuItem("Fit diagram", "\uE9A6", FitCanvasToContent, "Ctrl+0", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Actual size (100%)", "\uE71E", () => ZoomTo(1), "Ctrl+1"));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Clear canvas", "\uE74D", () => OnClearClick(this, new RoutedEventArgs()), enabled: any));

            flyout.ShowAt(MainCanvas, position);
        }

        private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.Handled) return;
            var pos = e.GetPosition(MainCanvas);
            ShowCanvasContextMenu(pos);
            e.Handled = true;
        }

        private void EnsureSnapGuides()
        {
            if (_snapGuideX == null)
            {
                _snapGuideX = new Microsoft.UI.Xaml.Shapes.Line
                {
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                // The accent follows the studio's theme; an Application resource keeps the start-up theme's.
                ThemeBrush.Set(_snapGuideX, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
                MainCanvas.Children.Add(_snapGuideX);
            }
            if (_snapGuideY == null)
            {
                _snapGuideY = new Microsoft.UI.Xaml.Shapes.Line
                {
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                // The accent follows the studio's theme; an Application resource keeps the start-up theme's.
                ThemeBrush.Set(_snapGuideY, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
                MainCanvas.Children.Add(_snapGuideY);
            }
            if (_crosshairLineX == null)
            {
                _crosshairLineX = new Microsoft.UI.Xaml.Shapes.Line
                {
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    Opacity = 0.5,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                // The accent follows the studio's theme; an Application resource keeps the start-up theme's.
                ThemeBrush.Set(_crosshairLineX, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
                MainCanvas.Children.Add(_crosshairLineX);
            }
            if (_crosshairLineY == null)
            {
                _crosshairLineY = new Microsoft.UI.Xaml.Shapes.Line
                {
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    Opacity = 0.5,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                // The accent follows the studio's theme; an Application resource keeps the start-up theme's.
                ThemeBrush.Set(_crosshairLineY, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
                MainCanvas.Children.Add(_crosshairLineY);
            }
            if (_cursorHudBadge == null)
            {
                _cursorHudText = new TextBlock
                {
                    FontSize = 10.5,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                // Was a fixed near-black badge with white text in both themes; it now matches the
                // studio's surfaces (and high contrast) like the guides around it.
                ThemeBrush.Set(_cursorHudText, TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                _cursorHudBadge = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 2, 6, 2),
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
                    Child = _cursorHudText
                };
                ThemeBrush.Set(_cursorHudBadge, Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                ThemeBrush.Set(_cursorHudBadge, Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
                MainCanvas.Children.Add(_cursorHudBadge);
            }
        }

        private void UpdateCursorHud(Point pos, string text)
        {
            EnsureSnapGuides();
            if (_cursorHudBadge == null || _cursorHudText == null) return;
            double z = Math.Max(0.05, CanvasScroller.ZoomFactor);
            _cursorHudText.Text = text;
            _cursorHudText.FontSize = Math.Max(9.0, 10.5 / Math.Sqrt(z));
            _cursorHudBadge.BorderThickness = new Thickness(1.0 / z);
            _cursorHudBadge.CornerRadius = new CornerRadius(6.0 / z);
            _cursorHudBadge.Padding = new Thickness(6.0 / z, 2.0 / z, 6.0 / z, 2.0 / z);
            _cursorHudBadge.Visibility = Visibility.Visible;
            Canvas.SetLeft(_cursorHudBadge, pos.X + (14.0 / z));
            Canvas.SetTop(_cursorHudBadge, pos.Y + (14.0 / z));
        }

        private void UpdateCrosshairs(Point pos)
        {
            EnsureSnapGuides();
            if (_crosshairLineX == null || _crosshairLineY == null) return;
            double z = Math.Max(0.05, CanvasScroller.ZoomFactor);
            _crosshairLineX.X1 = 0;
            _crosshairLineX.X2 = Math.Max(MainCanvas.ActualWidth, 5000);
            _crosshairLineX.Y1 = _crosshairLineX.Y2 = pos.Y;
            _crosshairLineX.StrokeThickness = 1.0 / z;
            _crosshairLineX.Visibility = Visibility.Visible;

            _crosshairLineY.Y1 = 0;
            _crosshairLineY.Y2 = Math.Max(MainCanvas.ActualHeight, 5000);
            _crosshairLineY.X1 = _crosshairLineY.X2 = pos.X;
            _crosshairLineY.StrokeThickness = 1.0 / z;
            _crosshairLineY.Visibility = Visibility.Visible;
        }

        private void HideCursorHud()
        {
            if (_cursorHudBadge != null) _cursorHudBadge.Visibility = Visibility.Collapsed;
            if (_crosshairLineX != null) _crosshairLineX.Visibility = Visibility.Collapsed;
            if (_crosshairLineY != null) _crosshairLineY.Visibility = Visibility.Collapsed;
        }

        private int _gridSnapInterval = 0; // 0 = off, 10 = 10px, 20 = 20px
        private bool _showVisualGrid = false;

        private void OnGridSnapButtonClick(object sender, RoutedEventArgs e)
        {
            GridSnapFlyout?.ShowAt(sender as FrameworkElement ?? GridSnapButton);
        }

        private void OnGridSnapButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            GridSnapFlyout?.ShowAt(sender as FrameworkElement ?? GridSnapButton);
        }

        private void OnGridSnapOffClick(object sender, RoutedEventArgs e) => SetGridSnapMode(0, _showVisualGrid);
        private void OnGridSnap10Click(object sender, RoutedEventArgs e) => SetGridSnapMode(10, _showVisualGrid);
        private void OnGridSnap20Click(object sender, RoutedEventArgs e) => SetGridSnapMode(20, _showVisualGrid);

        private void OnToggleVisualGridClick(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem item)
            {
                SetGridSnapMode(_gridSnapInterval == 0 ? 20 : _gridSnapInterval, item.IsChecked);
            }
        }

        private bool _snapToCanvasCenter = true;
        private bool _snapToShapeEdges = true;

        private void OnToggleSnapToCenterClick(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem item)
            {
                _snapToCanvasCenter = item.IsChecked;
                ViewModel.StatusMessage = _snapToCanvasCenter
                    ? "Snap to canvas center guidelines: Enabled"
                    : "Snap to canvas center guidelines: Disabled";
            }
        }

        private void OnToggleSnapToShapesClick(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem item)
            {
                _snapToShapeEdges = item.IsChecked;
                ViewModel.StatusMessage = _snapToShapeEdges
                    ? "Snap to adjacent shape guidelines: Enabled"
                    : "Snap to adjacent shape guidelines: Disabled";
            }
        }

        private void OnCenterCanvasButtonClick(object sender, RoutedEventArgs e)
        {
            ViewModel.CenterDiagramOnCanvas(horizontal: true, vertical: true);
            CenterCanvasFlyout?.Hide();
        }

        private void OnCenterCanvasButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            CenterCanvasFlyout?.ShowAt(sender as FrameworkElement ?? CenterCanvasButton);
        }

        private void OnCenterBothClick(object sender, RoutedEventArgs e) => ViewModel.CenterDiagramOnCanvas(horizontal: true, vertical: true);
        private void OnCenterHorizontallyClick(object sender, RoutedEventArgs e) => ViewModel.CenterDiagramOnCanvas(horizontal: true, vertical: false);
        private void OnCenterVerticallyClick(object sender, RoutedEventArgs e) => ViewModel.CenterDiagramOnCanvas(horizontal: false, vertical: true);
        private void OnResetTopLeftClick(object sender, RoutedEventArgs e) => ViewModel.ResetDiagramToTopLeft(padX: 60, padY: 60);

        private void OnDistributeCanvasButtonClick(object sender, RoutedEventArgs e)
        {
            ViewModel.DistributeDiagramOnCanvas(vertical: false);
            DistributeCanvasFlyout?.Hide();
        }

        private void OnDistributeCanvasButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            DistributeCanvasFlyout?.ShowAt(sender as FrameworkElement ?? DistributeCanvasButton);
        }

        private void OnDistributeHorizontallyClick(object sender, RoutedEventArgs e) => ViewModel.DistributeDiagramOnCanvas(vertical: false);
        private void OnDistributeVerticallyClick(object sender, RoutedEventArgs e) => ViewModel.DistributeDiagramOnCanvas(vertical: true);
        private void OnAutoTidyGridClick(object sender, RoutedEventArgs e) => ViewModel.TidyDiagramIntoGrid();

        private void SetGridSnapMode(int interval, bool showGrid)
        {
            _gridSnapInterval = interval;
            _showVisualGrid = showGrid;
            if (ToggleVisualGridItem != null) ToggleVisualGridItem.IsChecked = showGrid;
            if (GridSnapLabel != null)
            {
                GridSnapLabel.Text = interval > 0 ? $"Snap · {interval}px" : "Snap · Off";
            }
            UpdateVisualGridOverlay();

            string status = interval > 0
                ? $"Canvas magnetic grid snap set to {interval}px{(showGrid ? " (grid visible)" : "")}"
                : "Canvas grid snap disabled";
            ViewModel.StatusMessage = status;

            if (ViewModel.SelectedShape is { } s)
            {
                Point p = new Point(s.X + s.Width / 2, s.Y);
                UpdateCursorHud(p, $"Grid: {(interval > 0 ? $"{interval}px" : "Off")}");
                _duplicateHudTimer?.Stop();
                _duplicateHudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _duplicateHudTimer.Tick += (_, _) => { _duplicateHudTimer?.Stop(); HideCursorHud(); };
                _duplicateHudTimer.Start();
            }
            if (GridSnapButton != null) CopyFeedback.ApplyButtonTick(GridSnapButton, null);
        }

        private void UpdateVisualGridOverlay()
        {
            if (GridOverlayCanvas == null) return;
            if (!_showVisualGrid || _gridSnapInterval <= 0)
            {
                GridOverlayCanvas.Visibility = Visibility.Collapsed;
                GridOverlayCanvas.Children.Clear();
                return;
            }
            GridOverlayCanvas.Visibility = Visibility.Visible;
            GridOverlayCanvas.Children.Clear();

            int step = Math.Max(20, _gridSnapInterval * 2);
            var strokeBrush = ThemeBrush.For(GridOverlayCanvas, "CardStrokeColorDefaultBrush");

            for (int y = step; y < 1800; y += step)
            {
                var line = new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = 0,
                    X2 = 2200,
                    Y1 = y,
                    Y2 = y,
                    Stroke = strokeBrush,
                    StrokeThickness = 0.5,
                    StrokeDashArray = new DoubleCollection { 2, 6 },
                    Opacity = 0.35,
                    IsHitTestVisible = false
                };
                GridOverlayCanvas.Children.Add(line);
            }
            for (int x = step; x < 2200; x += step)
            {
                var line = new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 0,
                    Y2 = 1800,
                    Stroke = strokeBrush,
                    StrokeThickness = 0.5,
                    StrokeDashArray = new DoubleCollection { 2, 6 },
                    Opacity = 0.35,
                    IsHitTestVisible = false
                };
                GridOverlayCanvas.Children.Add(line);
            }
        }

        private DispatcherTimer? _duplicateHudTimer;

        private void OnDuplicateButtonClick(object sender, RoutedEventArgs e)
        {
            ShowDuplicateHudFeedback();
        }

        private void ExecuteDuplicateWithHudFeedback()
        {
            if (!ViewModel.HasSelection)
            {
                ViewModel.StatusMessage = "Nothing to duplicate: select a shape on the canvas first.";
                return;
            }
            ViewModel.DuplicateSelectedCommand.Execute(null);
            ShowDuplicateHudFeedback();
        }

        private void ShowDuplicateHudFeedback()
        {
            var clone = ViewModel.SelectedShape;
            if (clone != null)
            {
                Point hudPos = new Point(clone.X + clone.Width / 2, clone.Y);
                UpdateCursorHud(hudPos, $"Duplicated (+20px) · {clone.DisplayName}");
                _duplicateHudTimer?.Stop();
                _duplicateHudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
                _duplicateHudTimer.Tick += (_, _) =>
                {
                    _duplicateHudTimer?.Stop();
                    HideCursorHud();
                };
                _duplicateHudTimer.Start();
            }
        }

        private void OnShapePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_dragShape == null) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            double dx = cur.X - _dragStart.X;
            double dy = cur.Y - _dragStart.Y;
            if (!_dragMoved)
            {
                if (Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return; // a click, not a drag
                _dragMoved = true;
                if (IsDown(VirtualKey.Menu))
                {
                    ViewModel.DuplicateSelectedCommand.Execute(null);
                    ViewModel.StatusMessage = "Cloned selection (Alt+drag) · Repositioning duplicate";
                }
                else
                {
                    ViewModel.RecordUndo();
                }
            }
            // dx/dy are the pointer's travel since the press; move by whatever is still owed.
            var (mx, my) = ViewModel.NudgeSelection(dx - _dragAppliedX, dy - _dragAppliedY);
            _dragAppliedX += mx;
            _dragAppliedY += my;

            EnsureSnapGuides();
            double z = Math.Max(0.05, CanvasScroller.ZoomFactor);
            double snapThreshold = 6.0 / z;

            var others = ViewModel.Shapes.Where(s => !s.IsSelected).ToList();
            bool snappedX = false;
            bool snappedY = false;

            double targetLeft = _dragShape.X;
            double targetCenter = _dragShape.X + _dragShape.Width / 2.0;
            double targetRight = _dragShape.X + _dragShape.Width;
            double targetTop = _dragShape.Y;
            double targetMiddle = _dragShape.Y + _dragShape.Height / 2.0;
            double targetBottom = _dragShape.Y + _dragShape.Height;

            var selShapes = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (selShapes.Count > 1)
            {
                targetLeft = selShapes.Min(s => s.X);
                targetRight = selShapes.Max(s => s.X + s.Width);
                targetCenter = (targetLeft + targetRight) / 2.0;
                targetTop = selShapes.Min(s => s.Y);
                targetBottom = selShapes.Max(s => s.Y + s.Height);
                targetMiddle = (targetTop + targetBottom) / 2.0;
            }

            // 1. Center guidelines magnetic snap (virtual canvas is 2200 x 1800)
            const double canvasCenterX = 1100.0;
            const double canvasCenterY = 900.0;
            if (_snapToCanvasCenter)
            {
                if (Math.Abs(targetCenter - canvasCenterX) <= snapThreshold)
                {
                    double deltaX = canvasCenterX - targetCenter;
                    var (smx, _) = ViewModel.NudgeSelection(deltaX, 0);
                    _dragAppliedX += smx;
                    if (_snapGuideX != null)
                    {
                        _snapGuideX.X1 = _snapGuideX.X2 = canvasCenterX;
                        _snapGuideX.Y1 = 0;
                        _snapGuideX.Y2 = 1800;
                        _snapGuideX.StrokeThickness = 1.2 / z;
                        _snapGuideX.Visibility = Visibility.Visible;
                    }
                    snappedX = true;
                }

                if (Math.Abs(targetMiddle - canvasCenterY) <= snapThreshold)
                {
                    double deltaY = canvasCenterY - targetMiddle;
                    var (_, smy) = ViewModel.NudgeSelection(0, deltaY);
                    _dragAppliedY += smy;
                    if (_snapGuideY != null)
                    {
                        _snapGuideY.Y1 = _snapGuideY.Y2 = canvasCenterY;
                        _snapGuideY.X1 = 0;
                        _snapGuideY.X2 = 2200;
                        _snapGuideY.StrokeThickness = 1.2 / z;
                        _snapGuideY.Visibility = Visibility.Visible;
                    }
                    snappedY = true;
                }
            }

            // 2. Adjacent shape guidelines magnetic snap
            if (_snapToShapeEdges && others.Count > 0)
            {
                foreach (var o in others)
                {
                    double oLeft = o.X;
                    double oCenter = o.X + o.Width / 2.0;
                    double oRight = o.X + o.Width;
                    double oTop = o.Y;
                    double oMiddle = o.Y + o.Height / 2.0;
                    double oBottom = o.Y + o.Height;

                    if (!snappedX)
                    {
                        double[] dragX = { targetLeft, targetCenter, targetRight };
                        double[] otherX = { oLeft, oCenter, oRight };
                        foreach (var dxVal in dragX)
                        {
                            foreach (var oxVal in otherX)
                            {
                                if (Math.Abs(dxVal - oxVal) <= snapThreshold)
                                {
                                    double deltaX = oxVal - dxVal;
                                    var (smx, _) = ViewModel.NudgeSelection(deltaX, 0);
                                    _dragAppliedX += smx;
                                    if (_snapGuideX != null)
                                    {
                                        _snapGuideX.X1 = _snapGuideX.X2 = oxVal;
                                        _snapGuideX.Y1 = Math.Min(targetTop, oTop) - 30;
                                        _snapGuideX.Y2 = Math.Max(targetBottom, oBottom) + 30;
                                        _snapGuideX.StrokeThickness = 1.0 / z;
                                        _snapGuideX.Visibility = Visibility.Visible;
                                    }
                                    snappedX = true;
                                    break;
                                }
                            }
                            if (snappedX) break;
                        }
                    }

                    if (!snappedY)
                    {
                        double[] dragY = { targetTop, targetMiddle, targetBottom };
                        double[] otherY = { oTop, oMiddle, oBottom };
                        foreach (var dyVal in dragY)
                        {
                            foreach (var oyVal in otherY)
                            {
                                if (Math.Abs(dyVal - oyVal) <= snapThreshold)
                                {
                                    double deltaY = oyVal - dyVal;
                                    var (_, smy) = ViewModel.NudgeSelection(0, deltaY);
                                    _dragAppliedY += smy;
                                    if (_snapGuideY != null)
                                    {
                                        _snapGuideY.Y1 = _snapGuideY.Y2 = oyVal;
                                        _snapGuideY.X1 = Math.Min(targetLeft, oLeft) - 30;
                                        _snapGuideY.X2 = Math.Max(targetRight, oRight) + 30;
                                        _snapGuideY.StrokeThickness = 1.0 / z;
                                        _snapGuideY.Visibility = Visibility.Visible;
                                    }
                                    snappedY = true;
                                    break;
                                }
                            }
                            if (snappedY) break;
                        }
                    }

                    if (snappedX && snappedY) break;
                }
            }

            if (!snappedX && _gridSnapInterval > 0)
            {
                double targetGridX = Math.Round(_dragShape.X / _gridSnapInterval) * _gridSnapInterval;
                if (Math.Abs(targetGridX - _dragShape.X) <= snapThreshold)
                {
                    double deltaX = targetGridX - _dragShape.X;
                    var (smx, _) = ViewModel.NudgeSelection(deltaX, 0);
                    _dragAppliedX += smx;
                    snappedX = true;
                }
            }
            if (!snappedY && _gridSnapInterval > 0)
            {
                double targetGridY = Math.Round(_dragShape.Y / _gridSnapInterval) * _gridSnapInterval;
                if (Math.Abs(targetGridY - _dragShape.Y) <= snapThreshold)
                {
                    double deltaY = targetGridY - _dragShape.Y;
                    var (_, smy) = ViewModel.NudgeSelection(0, deltaY);
                    _dragAppliedY += smy;
                    snappedY = true;
                }
            }

            if (!snappedX && _snapGuideX != null) _snapGuideX.Visibility = Visibility.Collapsed;
            if (!snappedY && _snapGuideY != null) _snapGuideY.Visibility = Visibility.Collapsed;

            if (_dragMoved)
            {
                UpdateCrosshairs(cur);
                string hud = ViewModel.SelectionCount > 1
                    ? $"({(int)Math.Round(cur.X)}, {(int)Math.Round(cur.Y)}) · {CountText.Of(ViewModel.SelectionCount, "shape")}"
                    : $"X: {(int)Math.Round(_dragShape.X)}  Y: {(int)Math.Round(_dragShape.Y)}";
                UpdateCursorHud(cur, hud);
            }

            UpdateMultiSelectionAdorner();
            e.Handled = true;
        }

        private void OnShapePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_dragShape == null) return;
            if (sender is FrameworkElement fe) fe.ReleasePointerCapture(e.Pointer);
            if (_snapGuideX != null) _snapGuideX.Visibility = Visibility.Collapsed;
            if (_snapGuideY != null) _snapGuideY.Visibility = Visibility.Collapsed;
            HideCursorHud();
            if (_dragMoved)
            {
                int n = ViewModel.SelectionCount;
                ViewModel.StatusMessage = n > 1 ? $"Moved {n} shapes" : $"Moved {_dragShape.DisplayName.ToLowerInvariant()} to ({_dragShape.X:F0}, {_dragShape.Y:F0})";
            }
            _dragShape = null;
            e.Handled = true;
        }

        private void OnShapePointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ShapeCanvasItemViewModel s)
            {
                SetCursor(fe, ViewModel.IsPlacing ? Microsoft.UI.Input.InputSystemCursorShape.Cross : Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                _hoverShape = s;
                if (_shapePaths.TryGetValue(s, out var p)) ApplyShapeVisual(p, s, hovered: true);
            }
        }

        private void OnShapePointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ShapeCanvasItemViewModel s)
            {
                if (ReferenceEquals(_hoverShape, s)) _hoverShape = null;
                if (_shapePaths.TryGetValue(s, out var p)) ApplyShapeVisual(p, s);
            }
        }

        // item -> the Path its template loaded. Paths live INSIDE the ItemsControl item
        // templates (never as direct children of MainCanvas), so this map is the only way to
        // reach a shape's visual for live updates (fill/preset edits repaint immediately).
        private readonly Dictionary<ShapeCanvasItemViewModel, Microsoft.UI.Xaml.Shapes.Path> _shapePaths = new();
        // Reverse map so Unloaded never has to read DataContext: during a template swap (ClearAll →
        // regenerate) WinUI raises Unloaded on Paths it is already tearing down, and touching their
        // DataContext threw a COMException that took the whole app down.
        private readonly Dictionary<Microsoft.UI.Xaml.Shapes.Path, ShapeCanvasItemViewModel> _pathItems = new();

        private void TrackShapePath(Microsoft.UI.Xaml.Shapes.Path p, ShapeCanvasItemViewModel s)
        {
            if (_pathItems.TryGetValue(p, out var previous) && !ReferenceEquals(previous, s))
            {
                previous.PropertyChanged -= OnShapeItemChanged;
                if (_shapePaths.TryGetValue(previous, out var prevPath) && ReferenceEquals(prevPath, p))
                    _shapePaths.Remove(previous);
            }
            s.PropertyChanged -= OnShapeItemChanged;
            s.PropertyChanged += OnShapeItemChanged;
            _shapePaths[s] = p;
            _pathItems[p] = s;
            p.Unloaded -= OnShapePathUnloaded;
            p.Unloaded += OnShapePathUnloaded;
        }

        private void OnShapeLoaded(object sender, RoutedEventArgs e)
        {
            // Set geometry + fill from the item in code — the XamlReader-based geometry
            // builder is not reliable inside a value converter at template-load time.
            if (sender is Microsoft.UI.Xaml.Shapes.Path p && p.DataContext is ShapeCanvasItemViewModel s)
            {
                ApplyShapeVisual(p, s);
                TrackShapePath(p, s);
            }
        }

        private void OnShapeDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Microsoft.UI.Xaml.Shapes.Path p && args.NewValue is ShapeCanvasItemViewModel s)
            {
                ApplyShapeVisual(p, s);
                TrackShapePath(p, s);
            }
        }

        private void OnShapePathUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Shapes.Path p)
            {
                if (p.IsLoaded) return;
                p.Unloaded -= OnShapePathUnloaded;
                if (_pathItems.Remove(p, out var s))
                {
                    if (_shapePaths.TryGetValue(s, out var current) && ReferenceEquals(current, p))
                    {
                        _shapePaths.Remove(s);
                        s.PropertyChanged -= OnShapeItemChanged;
                    }
                }
            }
        }

        private static readonly DoubleCollection SelectionDash = new() { 4, 2 };

        /// <summary>(Re)paint one shape's Path from its item — geometry, fill, stroke, selection, hover.</summary>
        private static void ApplyShapeVisual(Microsoft.UI.Xaml.Shapes.Path p, ShapeCanvasItemViewModel s, bool hovered = false)
        {
            bool isLine = s.PathPoints is { Count: >= 2 };
            bool isRoundRect = string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase);
            var outline = isLine ? null : PresetGeometry.Outline(s.Prst, s.Width, s.Height);
            try
            {
                // Lines are drawn at their real pixel size with no stretch. Stretch="Fill" scales the
                // geometry's bounds to the element, and a straight connector's bounds are zero-wide
                // (or zero-tall) — the degenerate scale threw horizontal/vertical connectors off
                // their boxes (the org chart's tree lines floated across the canvas).
                // Rounded rectangles are also built at real size: stretching a 100×100 template
                // squashed the corners into a pillow on any wide shape. Word's roundRect corner is
                // 1/6 of the shorter side, so that is what the canvas draws too.
                // Chevrons, trapezoids and hexagons are built at real size too, with Word's own
                // proportions (PresetGeometry): a stretched template gave a wide chevron a notch twice
                // as deep as the exported one, so the canvas and the document disagreed.
                p.Stretch = isLine || isRoundRect || outline is not null ? Stretch.None : Stretch.Fill;
                p.Data = isLine ? BuildPolylineGeometry(s.PathPoints!, s.Width, s.Height)
                       : isRoundRect ? BuildRoundRectGeometry(s.Width, s.Height)
                       : outline is not null ? MakePolygonGeometry(outline)
                       : MarkSmith.Converters.ShapeGeometries.For(s.Prst);
                // A line's box is as thin as 2 px, and the Path is clipped to it, so any weight above
                // that drew as 2 px (the 3 pt timeline, and every Weight edit). Grow the Path past
                // the box by half the widest stroke it can get and shift the geometry to match.
                double pad = isLine ? Math.Ceiling(Math.Max(2, s.StrokeWidthPt) / 2) + 1 : 0;
                p.Margin = new Thickness(-pad);
                if (isLine) p.Data.Transform = new TranslateTransform { X = pad, Y = pad };
            }
            catch { }
            try
            {
                p.Fill = isLine ? null : BrushFromHex(s.Fill);
                string contrast = p.ActualTheme == ElementTheme.Light ? "1F1F1F" : "FFFFFF";
                if (s.IsSelected)
                {
                    // Selection: a dashed outline in a contrasting colour (light on the dark canvas,
                    // dark on the light one) so the selected shape is unmistakable on any fill.
                    p.Stroke = BrushFromHex(contrast);
                    p.StrokeThickness = isLine ? Math.Max(2, s.StrokeWidthPt) : 2;
                    p.StrokeDashArray = isLine ? null : SelectionDash;
                }
                else if (hovered)
                {
                    p.Stroke = BrushFromHex(contrast);
                    p.StrokeThickness = isLine ? Math.Max(2, s.StrokeWidthPt) : 1.5;
                    p.StrokeDashArray = null;
                }
                else
                {
                    p.Stroke = BrushFromHex(s.Fill);
                    p.StrokeThickness = isLine ? Math.Max(1, s.StrokeWidthPt) : 1.5;
                    p.StrokeDashArray = null;
                }
            }
            catch { }
        }

        private void OnShapeItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not ShapeCanvasItemViewModel s) return;
            bool sized = e.PropertyName == nameof(s.Width) || e.PropertyName == nameof(s.Height);
            bool realSizeGeometry = s.PathPoints is { Count: >= 2 } || string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase)
                || PresetGeometry.Outline(s.Prst, 1, 1) is not null;
            if ((e.PropertyName == nameof(s.Fill) || e.PropertyName == nameof(s.Prst) ||
                 e.PropertyName == nameof(s.IsSelected) || e.PropertyName == nameof(s.PathPoints) ||
                 e.PropertyName == nameof(s.StrokeWidthPt) || (sized && realSizeGeometry)) &&
                _shapePaths.TryGetValue(s, out var path))
            {
                ApplyShapeVisual(path, s, hovered: ReferenceEquals(s, _hoverShape));
            }
        }

        private static Geometry BuildRoundRectGeometry(double width, double height)
        {
            double w = Math.Max(1, width), h = Math.Max(1, height);
            double r = PresetGeometry.RoundRectRadius(w, h);
            var fig = new PathFigure { StartPoint = new Point(r, 0), IsClosed = true, IsFilled = true };
            fig.Segments.Add(new LineSegment { Point = new Point(w - r, 0) });
            fig.Segments.Add(new ArcSegment { Point = new Point(w, r), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
            fig.Segments.Add(new LineSegment { Point = new Point(w, h - r) });
            fig.Segments.Add(new ArcSegment { Point = new Point(w - r, h), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
            fig.Segments.Add(new LineSegment { Point = new Point(r, h) });
            fig.Segments.Add(new ArcSegment { Point = new Point(0, h - r), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
            fig.Segments.Add(new LineSegment { Point = new Point(0, r) });
            fig.Segments.Add(new ArcSegment { Point = new Point(r, 0), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        private static PathGeometry MakePolygonGeometry(IReadOnlyList<(double X, double Y)> pts)
        {
            var figure = new PathFigure { StartPoint = new Point(pts[0].X, pts[0].Y), IsClosed = true, IsFilled = true };
            for (int i = 1; i < pts.Count; i++) figure.Segments.Add(new LineSegment { Point = new Point(pts[i].X, pts[i].Y) });
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            return geo;
        }

        /// <summary>Maps a 0..100 local-space polyline onto the item's actual width × height.</summary>
        private static Geometry BuildPolylineGeometry(List<(double X, double Y)> pts, double width, double height)
        {
            double sx = Math.Max(0, width) / 100.0, sy = Math.Max(0, height) / 100.0;
            var scaled = new List<(double X, double Y)>(pts.Count);
            foreach (var (x, y) in pts) scaled.Add((x * sx, y * sy));
            return MakePolylineGeometry(scaled);
        }

        private static PathGeometry MakePolylineGeometry(List<(double X, double Y)> pts)
        {
            if (pts == null || pts.Count == 0) return new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(pts[0].X, pts[0].Y), IsFilled = false };
            for (int i = 1; i < pts.Count; i++)
            {
                figure.Segments.Add(new LineSegment { Point = new Point(pts[i].X, pts[i].Y) });
            }
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            return geo;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SolidColorBrush> BrushCache = new();

        private static SolidColorBrush BrushFromHex(string hex)
        {
            hex = (hex ?? "0078D4").Trim().TrimStart('#');
            if (hex.Length != 6) hex = "0078D4";
            return BrushCache.GetOrAdd(hex, static h =>
            {
                try
                {
                    byte r = Convert.ToByte(h.Substring(0, 2), 16);
                    byte g = Convert.ToByte(h.Substring(2, 2), 16);
                    byte b = Convert.ToByte(h.Substring(4, 2), 16);
                    return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
                }
                catch
                {
                    return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
                }
            });
        }

        // ---- resize & rotation handles ----
        // One unlocked shape gets a frame with eight resize handles plus a top rotation stem and
        // handle; dragging a resize handle resizes the shape in its rotated local frame (Shift keeps
        // proportions on a corner), and dragging the rotation handle turns the shape around its
        // centre (Shift snaps to 15° steps, double-clicking resets to 0°).

        private const double HandleSize = 9;

        private void BuildResizeHandles()
        {
            _rotateStem = new Microsoft.UI.Xaml.Shapes.Line
            {
                StrokeThickness = 1.25,
                IsHitTestVisible = false,
            };
            ThemeBrush.Set(_rotateStem, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
            SelectionAdorner.Children.Add(_rotateStem);

            var E = ShapeDesignStudioViewModel.ResizeEdges.None;
            var L = ShapeDesignStudioViewModel.ResizeEdges.Left;
            var T = ShapeDesignStudioViewModel.ResizeEdges.Top;
            var R = ShapeDesignStudioViewModel.ResizeEdges.Right;
            var B = ShapeDesignStudioViewModel.ResizeEdges.Bottom;
            var specs = new (ShapeDesignStudioViewModel.ResizeEdges Edges, Microsoft.UI.Input.InputSystemCursorShape Cursor)[]
            {
                (L | T, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast),
                (T | E, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth),
                (R | T, Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest),
                (R | E, Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast),
                (R | B, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast),
                (B | E, Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth),
                (L | B, Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest),
                (L | E, Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast),
            };
            foreach (var (edges, cursor) in specs)
            {
                var h = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = HandleSize, Height = HandleSize, RadiusX = 1.5, RadiusY = 1.5,
                    StrokeThickness = 1.25,
                };
                ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "SolidBackgroundFillColorPrimaryBrush");
                ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
                string label = ShapeDesignStudioViewModel.DescribeResizeHandle(edges, 0);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(h, label);
                ToolTipService.SetToolTip(h, label);
                SetCursor(h, cursor);
                h.PointerEntered += (_, _) => ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "AccentFillColorSecondaryBrush");
                h.PointerExited += (_, _) => ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "SolidBackgroundFillColorPrimaryBrush");
                h.PointerPressed += OnHandlePointerPressed;
                h.PointerMoved += OnHandlePointerMoved;
                h.PointerReleased += OnHandlePointerReleased;
                h.DoubleTapped += OnHandleDoubleTapped;
                h.RightTapped += OnHandleRightTapped;
                h.PointerCaptureLost += (_, _) => { _resizeEdges = ShapeDesignStudioViewModel.ResizeEdges.None; HideResizeVisuals(); };
                SelectionAdorner.Children.Add(h);
                _handles.Add((h, edges));
            }

            _rotateHandle = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = HandleSize + 2,
                Height = HandleSize + 2,
                StrokeThickness = 1.5,
            };
            ThemeBrush.Set(_rotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "SolidBackgroundFillColorPrimaryBrush");
            ThemeBrush.Set(_rotateHandle, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_rotateHandle, "Rotate shape · Drag to turn, Shift snaps to 15°, scroll wheel turns, double-click resets to 0°, right-click for angle presets");
            ToolTipService.SetToolTip(_rotateHandle, "Drag to rotate · Shift snaps to 15° · Scroll wheel to turn · Double-click resets to 0° · Right-click for presets");
            SetCursor(_rotateHandle, Microsoft.UI.Input.InputSystemCursorShape.Hand);
            _rotateHandle.PointerEntered += (_, _) => ThemeBrush.Set(_rotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "AccentFillColorSecondaryBrush");
            _rotateHandle.PointerExited += (_, _) => ThemeBrush.Set(_rotateHandle, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "SolidBackgroundFillColorPrimaryBrush");
            _rotateHandle.PointerPressed += OnRotateHandlePointerPressed;
            _rotateHandle.PointerMoved += OnRotateHandlePointerMoved;
            _rotateHandle.PointerReleased += OnRotateHandlePointerReleased;
            _rotateHandle.PointerWheelChanged += OnRotateHandlePointerWheelChanged;
            _rotateHandle.DoubleTapped += OnRotateHandleDoubleTapped;
            _rotateHandle.RightTapped += OnRotateHandleRightTapped;
            _rotateHandle.PointerCaptureLost += (_, _) => { _rotating = false; HideRotateVisuals(); };
            SelectionAdorner.Children.Add(_rotateHandle);

            _rotateReferenceGuide = new Microsoft.UI.Xaml.Shapes.Line
            {
                Stroke = new SolidColorBrush(AccentColor(0x80)),
                StrokeThickness = 1.0,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            SelectionAdorner.Children.Add(_rotateReferenceGuide);

            _rotateArcOverlay = new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = new SolidColorBrush(AccentColor(0xFF)),
                Fill = new SolidColorBrush(AccentColor(0x22)),
                StrokeThickness = 1.25,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            SelectionAdorner.Children.Add(_rotateArcOverlay);

            _rotateAngleBadgeText = new TextBlock
            {
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _rotateAngleBadge = new Border
            {
                BorderBrush = new SolidColorBrush(AccentColor(0xCC)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Child = _rotateAngleBadgeText
            };
            // The angle and size badges match the cursor HUD: themed surface, accent border.
            ThemeBrush.Set(_rotateAngleBadgeText, TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            ThemeBrush.Set(_rotateAngleBadge, Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
            SelectionAdorner.Children.Add(_rotateAngleBadge);

            _resizeAspectGuide = new Microsoft.UI.Xaml.Shapes.Line
            {
                Stroke = new SolidColorBrush(AccentColor(0x80)),
                StrokeThickness = 1.0,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            SelectionAdorner.Children.Add(_resizeAspectGuide);

            _resizeDimensionBadgeText = new TextBlock
            {
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _resizeDimensionBadge = new Border
            {
                BorderBrush = new SolidColorBrush(AccentColor(0xCC)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Child = _resizeDimensionBadgeText
            };
            ThemeBrush.Set(_resizeDimensionBadgeText, TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            ThemeBrush.Set(_resizeDimensionBadge, Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
            SelectionAdorner.Children.Add(_resizeDimensionBadge);
        }

        private void TrackAdornedShape(ShapeCanvasItemViewModel? shape)
        {
            if (_adornedShape is not null) _adornedShape.PropertyChanged -= OnAdornedShapeChanged;
            _adornedShape = shape;
            if (_adornedShape is not null) _adornedShape.PropertyChanged += OnAdornedShapeChanged;
            UpdateAdorner();
        }

        private void OnAdornedShapeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ShapeCanvasItemViewModel.X) or nameof(ShapeCanvasItemViewModel.Y)
                or nameof(ShapeCanvasItemViewModel.Width) or nameof(ShapeCanvasItemViewModel.Height)
                or nameof(ShapeCanvasItemViewModel.Rotation) or nameof(ShapeCanvasItemViewModel.PathPoints))
                UpdateAdorner();
        }

        private void UpdateAdorner()
        {
            var s = _adornedShape;
            // Turned shapes get handles too: the frame and handles turn with the shape, and a drag
            // is read in the shape's own frame (ShapeDesignStudioViewModel.ResizeRotatedRect).
            bool show = s is not null && !ViewModel.IsDense && !ViewModel.IsPlacing && ViewModel.SelectionCount == 1
                        && s.PathPoints is not { Count: >= 2 } && ViewModel.Shapes.Contains(s);
            SelectionAdorner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            UpdatePointHandles(s);
            UpdateCornerRadiusButtonState();
            if (!show || s is null)
            {
                HideRotateVisuals();
                return;
            }

            double z = Math.Max(0.05, CanvasScroller.ZoomFactor);
            double size = HandleSize / z, half = size / 2;
            Canvas.SetLeft(SelectionFrame, s.X);
            Canvas.SetTop(SelectionFrame, s.Y);
            SelectionFrame.Width = Math.Max(0, s.Width);
            SelectionFrame.Height = Math.Max(0, s.Height);
            SelectionFrame.StrokeThickness = 1 / z;
            SelectionFrame.RenderTransformOrigin = new Point(0.5, 0.5);
            SelectionFrame.RenderTransform = s.Rotation % 360 == 0 ? null : new RotateTransform { Angle = s.Rotation };
            foreach (var (h, edges) in _handles)
            {
                var (x, y) = ShapeDesignStudioViewModel.HandlePosition(s.X, s.Y, s.Width, s.Height, s.Rotation, edges);
                h.Width = h.Height = size;
                h.RenderTransformOrigin = new Point(0.5, 0.5);
                h.RenderTransform = s.Rotation % 360 == 0 ? null : new RotateTransform { Angle = s.Rotation };
                SetCursor(h, ShapeDesignStudioViewModel.HandleCursorAxis(edges, s.Rotation) switch
                {
                    0 => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
                    1 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
                    2 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
                    _ => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
                });
                string handleDesc = ShapeDesignStudioViewModel.DescribeResizeHandle(edges, s.Rotation);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(h, handleDesc);
                ToolTipService.SetToolTip(h, handleDesc);
                h.StrokeThickness = 1.25 / z;
                // Edge handles hide on a shape too small to tell them from the corners.
                bool edge = edges is ShapeDesignStudioViewModel.ResizeEdges.Left or ShapeDesignStudioViewModel.ResizeEdges.Right
                    ? s.Height * z < 28 : edges is ShapeDesignStudioViewModel.ResizeEdges.Top or ShapeDesignStudioViewModel.ResizeEdges.Bottom && s.Width * z < 28;
                h.Visibility = edge ? Visibility.Collapsed : Visibility.Visible;
                Canvas.SetLeft(h, x - half);
                Canvas.SetTop(h, y - half);
            }

            if (_rotateStem is not null && _rotateHandle is not null)
            {
                _rotateStem.Visibility = Visibility.Visible;
                _rotateHandle.Visibility = Visibility.Visible;
                var (topX, topY) = ShapeDesignStudioViewModel.HandlePosition(
                    s.X, s.Y, s.Width, s.Height, s.Rotation, ShapeDesignStudioViewModel.ResizeEdges.Top);
                var (rotX, rotY) = ShapeDesignStudioViewModel.RotationHandlePosition(
                    s.X, s.Y, s.Width, s.Height, s.Rotation, ShapeDesignStudioViewModel.RotationHandleStemLength / z);
                _rotateStem.X1 = topX;
                _rotateStem.Y1 = topY;
                _rotateStem.X2 = rotX;
                _rotateStem.Y2 = rotY;
                _rotateStem.StrokeThickness = 1.25 / z;

                double rotSize = (HandleSize + 2) / z;
                _rotateHandle.Width = _rotateHandle.Height = rotSize;
                _rotateHandle.StrokeThickness = 1.5 / z;
                Canvas.SetLeft(_rotateHandle, rotX - rotSize / 2);
                Canvas.SetTop(_rotateHandle, rotY - rotSize / 2);
                int normAngle = ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation);
                string rotTip = $"Rotate shape ({normAngle}°) · Drag to turn, Shift snaps to 15°, double-click resets to 0°";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_rotateHandle, rotTip);
                ToolTipService.SetToolTip(_rotateHandle, rotTip);
            }
            UpdateMultiSelectionAdorner();
        }

        private void UpdateMultiSelectionAdorner()
        {
            if (MultiSelectionAdorner is null || MultiSelectionFrame is null || MultiSelectionBadge is null || MultiSelectionBadgeText is null) return;

            bool show = !ViewModel.IsDense && !ViewModel.IsPlacing && ViewModel.SelectionCount >= 2;
            if (!show)
            {
                MultiSelectionAdorner.Visibility = Visibility.Collapsed;
                return;
            }

            var selected = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (selected.Count < 2)
            {
                MultiSelectionAdorner.Visibility = Visibility.Collapsed;
                return;
            }

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in selected)
            {
                var (l, t, r, b) = ShapeDesignStudioViewModel.RotatedBounds(s);
                if (l < minX) minX = l;
                if (t < minY) minY = t;
                if (r > maxX) maxX = r;
                if (b > maxY) maxY = b;
            }

            double totalW = Math.Max(0, maxX - minX);
            double totalH = Math.Max(0, maxY - minY);
            double z = Math.Max(0.05, CanvasScroller.ZoomFactor);

            MultiSelectionAdorner.Visibility = Visibility.Visible;
            Canvas.SetLeft(MultiSelectionFrame, minX);
            Canvas.SetTop(MultiSelectionFrame, minY);
            MultiSelectionFrame.Width = totalW;
            MultiSelectionFrame.Height = totalH;
            MultiSelectionFrame.StrokeThickness = 1.0 / z;

            if (MultiRotateStem != null && MultiRotateHandle != null)
            {
                double cx = minX + totalW / 2.0;
                double cy = minY;
                double stemLength = 22.0 / z;
                double hSize = Math.Max(7.0, 10.0 / Math.Sqrt(z));

                MultiRotateStem.X1 = cx;
                MultiRotateStem.Y1 = cy;
                MultiRotateStem.X2 = cx;
                MultiRotateStem.Y2 = cy - stemLength;
                MultiRotateStem.StrokeThickness = 1.0 / z;

                MultiRotateHandle.Width = hSize;
                MultiRotateHandle.Height = hSize;
                MultiRotateHandle.StrokeThickness = 1.0 / z;
                Canvas.SetLeft(MultiRotateHandle, cx - hSize / 2.0);
                Canvas.SetTop(MultiRotateHandle, cy - stemLength - hSize / 2.0);
            }

            string badgeSummary = $"{CountText.Of(selected.Count, "shape")} · {(int)Math.Round(totalW)} × {(int)Math.Round(totalH)} px";
            MultiSelectionBadgeText.Text = badgeSummary;
            string fullTip = $"Selection: {badgeSummary} · Double-click to auto-align & distribute, Right-click for options";
            // The name is the readout; the mouse instructions stay in the tooltip.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MultiSelectionBadge, $"Selection: {badgeSummary}");
            ToolTipService.SetToolTip(MultiSelectionBadge, fullTip);

            Canvas.SetLeft(MultiSelectionBadge, minX);
            Canvas.SetTop(MultiSelectionBadge, Math.Max(0, minY - 28 / z));

            if (MultiAlignRibbon != null)
            {
                MultiAlignRibbon.Visibility = Visibility.Visible;
                double ribbonW = 230;
                double ribbonX = minX + Math.Max(0, (totalW - ribbonW) / 2.0);
                double ribbonY = minY + totalH + 8 / z;
                if (ribbonY + 32 / z > MainCanvas.Height && minY - 36 / z > 0)
                {
                    ribbonY = Math.Max(0, minY - 36 / z);
                }
                Canvas.SetLeft(MultiAlignRibbon, Math.Max(0, ribbonX));
                Canvas.SetTop(MultiAlignRibbon, Math.Max(0, ribbonY));

                if (MultiDistributeHBtn != null) MultiDistributeHBtn.IsEnabled = selected.Count >= 3;
                if (MultiDistributeVBtn != null) MultiDistributeVBtn.IsEnabled = selected.Count >= 3;
            }

            if (DistributionSpacingBadgesCanvas != null)
            {
                if (selected.Count < 3)
                {
                    DistributionSpacingBadgesCanvas.Visibility = Visibility.Collapsed;
                }
                else
                {
                    DistributionSpacingBadgesCanvas.Visibility = Visibility.Visible;
                    UpdateDistributionSpacingBadges(selected, totalW, totalH, z);
                }
            }
        }

        // What a spacing badge's double-click / right-click act on, refreshed on every update.
        private sealed record SpacingBadgeState(bool Horizontal, double AverageGap);

        private void UpdateDistributionSpacingBadges(List<ShapeCanvasItemViewModel> selected, double totalW, double totalH, double z)
        {
            if (DistributionSpacingBadgesCanvas == null) return;

            bool isHorizontal = totalW >= totalH;
            var ordered = isHorizontal
                ? selected.Select(s => (Shape: s, Bounds: ShapeDesignStudioViewModel.RotatedBounds(s)))
                          .OrderBy(t => t.Bounds.Left + (t.Bounds.Right - t.Bounds.Left) / 2.0).ToList()
                : selected.Select(s => (Shape: s, Bounds: ShapeDesignStudioViewModel.RotatedBounds(s)))
                          .OrderBy(t => t.Bounds.Top + (t.Bounds.Bottom - t.Bounds.Top) / 2.0).ToList();

            int pairs = ordered.Count - 1;
            if (pairs <= 0)
            {
                DistributionSpacingBadgesCanvas.Visibility = Visibility.Collapsed;
                return;
            }

            var gaps = new double[pairs];
            double sumGap = 0;
            for (int i = 0; i < pairs; i++)
            {
                gaps[i] = isHorizontal
                    ? ordered[i + 1].Bounds.Left - ordered[i].Bounds.Right
                    : ordered[i + 1].Bounds.Top - ordered[i].Bounds.Bottom;
                sumGap += gaps[i];
            }
            double avgGap = sumGap / pairs;
            bool isEquidistant = gaps.All(g => Math.Abs(g - avgGap) <= 1.5);

            while (DistributionSpacingBadgesCanvas.Children.Count < pairs)
            {
                var badge = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1)
                };
                var tb = new TextBlock { FontSize = 10 };
                // ThemeBrush follows the studio's theme; a raw Resources lookup kept the brush of
                // whichever theme was active when the badge was made.
                ThemeBrush.Set(badge, Border.BackgroundProperty, "AcrylicBackgroundFillColorDefaultBrush");
                badge.Child = tb;
                // Badges are reused across selections, so the handlers read the current axis and
                // gap from Tag (set below) instead of capturing the first selection's. Double-click
                // equalises the gaps the badges measure, as the tooltip says; it used to space left
                // edges and overwrite the VM's line with "Distributed N shapes evenly".
                badge.DoubleTapped += (s, e) =>
                {
                    e.Handled = true;
                    if (selected.Count < 3)
                    {
                        ViewModel.StatusMessage = "Select at least 3 shapes to space their edges evenly.";
                        return;
                    }
                    if (s is not Border { Tag: SpacingBadgeState state } || ViewModel == null) return;
                    var (_, _, summary) = ViewModel.DistributeShapesEvenly(state.Horizontal ? DistributionAxis.Horizontal : DistributionAxis.Vertical);
                    ReportEdit(summary);
                    UpdateAdorner();
                    UpdateMultiSelectionAdorner();
                };
                badge.RightTapped += (s, e) =>
                {
                    e.Handled = true;
                    if (s is not Border { Tag: SpacingBadgeState state }) return;
                    ShowSpacingPresetsMenu(e.GetPosition(MainCanvas), state.Horizontal, state.AverageGap);
                };
                DistributionSpacingBadgesCanvas.Children.Add(badge);
            }

            for (int i = 0; i < DistributionSpacingBadgesCanvas.Children.Count; i++)
            {
                var b = (Border)DistributionSpacingBadgesCanvas.Children[i];
                if (i >= pairs)
                {
                    b.Visibility = Visibility.Collapsed;
                    continue;
                }
                b.Visibility = Visibility.Visible;
                b.Tag = new SpacingBadgeState(isHorizontal, avgGap);
                var tb = (TextBlock)b.Child;
                double gap = gaps[i];
                tb.Text = isEquidistant ? $"= {gap:F0} px" : $"d = {gap:F0} px";

                if (isEquidistant)
                {
                    ThemeBrush.Set(b, Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                    ThemeBrush.Set(tb, TextBlock.ForegroundProperty, "AccentFillColorDefaultBrush");
                }
                else
                {
                    ThemeBrush.Set(b, Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
                    ThemeBrush.Set(tb, TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                }

                ToolTipService.SetToolTip(b, $"Spacing: {gap:F0} px · Double-click to equalize spacing, Right-click for spacing presets");

                double midX = isHorizontal
                    ? (ordered[i].Bounds.Right + ordered[i + 1].Bounds.Left) / 2.0
                    : (ordered[i].Bounds.Left + ordered[i].Bounds.Right) / 2.0;
                double midY = isHorizontal
                    ? (ordered[i].Bounds.Top + ordered[i].Bounds.Bottom) / 2.0
                    : (ordered[i].Bounds.Bottom + ordered[i + 1].Bounds.Top) / 2.0;

                Canvas.SetLeft(b, midX - 20);
                Canvas.SetTop(b, midY - 10);
            }
        }

        private void ShowSpacingPresetsMenu(Point position, bool isHorizontal, double currentGap)
        {
            var menu = new MenuFlyout();
            var title = new MenuFlyoutItem
            {
                Text = $"Equalize {(isHorizontal ? "horizontal" : "vertical")} spacing",
                Icon = new FontIcon { Glyph = "\uE8E3" }
            };
            title.Click += (_, _) =>
            {
                if (isHorizontal) ViewModel.DistributeHorizontalCommand.Execute(null);
                else ViewModel.DistributeVerticalCommand.Execute(null);
                UpdateMultiSelectionAdorner();
            };
            menu.Items.Add(title);
            menu.Items.Add(new MenuFlyoutSeparator());

            int[] presets = { 8, 16, 24, 32, 48, 64 };
            foreach (var p in presets)
            {
                var item = new MenuFlyoutItem
                {
                    Text = $"Set spacing: {p} px",
                    Icon = new FontIcon { Glyph = Math.Abs(currentGap - p) < 1.5 ? "\uE73E" : "\uE71F" }
                };
                item.Click += (_, _) =>
                {
                    ApplyExactSpacing(isHorizontal, p);
                };
                menu.Items.Add(item);
            }
            menu.ShowAt(MainCanvas, position);
        }

        private void ApplyExactSpacing(bool isHorizontal, double targetSpacing)
        {
            var selected = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (selected.Count < 2) return;
            ViewModel.RecordUndo();

            var ordered = isHorizontal
                ? selected.OrderBy(s => s.X).ToList()
                : selected.OrderBy(s => s.Y).ToList();

            for (int i = 1; i < ordered.Count; i++)
            {
                if (isHorizontal)
                {
                    ordered[i].X = ordered[i - 1].X + ordered[i - 1].Width + targetSpacing;
                }
                else
                {
                    ordered[i].Y = ordered[i - 1].Y + ordered[i - 1].Height + targetSpacing;
                }
            }
            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"Set {targetSpacing} px spacing across {CountText.Of(selected.Count, "shape")} · Ctrl+Z to undo";
        }

        private void OnMultiSelectionBadgeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (ViewModel.CanAlign)
            {
                ViewModel.AlignCenterCommand.Execute(null);
                if (ViewModel.CanDistribute)
                {
                    ViewModel.DistributeVerticalCommand.Execute(null);
                }
                UpdateMultiSelectionAdorner();
                ViewModel.StatusMessage = $"Aligned & distributed {CountText.Of(ViewModel.SelectionCount, "shape")} · Ctrl+Z to undo";
            }
        }

        private void OnMultiSelectionBadgeRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowMultiSelectionContextMenu(sender is FrameworkElement fe ? KeyboardContextMenu.PointIn(e, fe) : e.GetPosition(MainCanvas));
        }

        private void OnMultiAlignLeftClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AlignLeft();
            UpdateMultiSelectionAdorner();
        }

        private void OnMultiAlignCenterClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AlignCenter();
            UpdateMultiSelectionAdorner();
        }

        private void OnMultiAlignRightClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AlignRight();
            UpdateMultiSelectionAdorner();
        }

        private void OnMultiAlignTopClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AlignTop();
            UpdateMultiSelectionAdorner();
        }

        private void OnMultiAlignMiddleClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AlignMiddle();
            UpdateMultiSelectionAdorner();
        }

        private void OnMultiAlignBottomClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AlignBottom();
            UpdateMultiSelectionAdorner();
        }

        private void OnMultiDistributeHClick(object sender, RoutedEventArgs e)
        {
            ViewModel.DistributeHorizontal();
            UpdateMultiSelectionAdorner();
        }

        private void OnMultiDistributeVClick(object sender, RoutedEventArgs e)
        {
            ViewModel.DistributeVertical();
            UpdateMultiSelectionAdorner();
        }

        private void OnMorphRectClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is { } s)
            {
                s.Prst = "rect";
                UpdateCornerRadiusButtonState();
            }
        }

        private void OnMorphRoundRectClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is { } s)
            {
                s.Prst = "roundrect";
                UpdateCornerRadiusButtonState();
            }
        }

        private void OnMorphEllipseClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is { } s)
            {
                s.Prst = "ellipse";
                UpdateCornerRadiusButtonState();
            }
        }

        private void OnMorphDiamondClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is { } s)
            {
                s.Prst = "diamond";
                UpdateCornerRadiusButtonState();
            }
        }

        private void OnMorphHexagonClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is { } s)
            {
                s.Prst = "hexagon";
                UpdateCornerRadiusButtonState();
            }
        }

        private void OnCornerRadiusToggleClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is { } s)
            {
                s.Prst = string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase) ? "rect" : "roundrect";
                UpdateCornerRadiusButtonState();
            }
        }

        private void UpdateCornerRadiusButtonState()
        {
            if (CornerRadiusText == null || ViewModel.SelectedShape == null) return;
            bool isRound = string.Equals(ViewModel.SelectedShape.Prst, "roundrect", StringComparison.OrdinalIgnoreCase);
            CornerRadiusText.Text = isRound ? "Sharp" : "Round";
        }

        private void UpdateInspectorLayerUi()
        {
            if (InspectorLayerPositionText == null) return;
            var shape = ViewModel.SelectedShape;
            if (shape == null || ViewModel.Shapes.Count == 0)
            {
                InspectorLayerPositionText.Text = "No shape";
                if (InspectorBringToFrontBtn != null) InspectorBringToFrontBtn.IsEnabled = false;
                if (InspectorBringForwardBtn != null) InspectorBringForwardBtn.IsEnabled = false;
                if (InspectorSendBackwardBtn != null) InspectorSendBackwardBtn.IsEnabled = false;
                if (InspectorSendToBackBtn != null) InspectorSendToBackBtn.IsEnabled = false;
                return;
            }

            int index = ViewModel.Shapes.IndexOf(shape);
            int total = ViewModel.Shapes.Count;
            InspectorLayerPositionText.Text = $"Layer {index + 1} of {total}";

            if (InspectorBringToFrontBtn != null) InspectorBringToFrontBtn.IsEnabled = index < total - 1;
            if (InspectorBringForwardBtn != null) InspectorBringForwardBtn.IsEnabled = index < total - 1;
            if (InspectorSendBackwardBtn != null) InspectorSendBackwardBtn.IsEnabled = index > 0;
            if (InspectorSendToBackBtn != null) InspectorSendToBackBtn.IsEnabled = index > 0;
        }

        // At the top or bottom of the stack the pressed Layer button disables itself (Send backward
        // on layer 1, Bring to front on the top layer), and WinUI moved keyboard focus out of the
        // inspector to the status bar. Keep it on the next Layer button that still applies.
        private void PressLayerButton(Button? pressed, System.Windows.Input.ICommand command)
        {
            var how = pressed?.FocusState ?? FocusState.Unfocused;
            command.Execute(null);
            UpdateInspectorLayerUi();
            if (pressed is null || how == FocusState.Unfocused || pressed.IsEnabled) return;
            Control[] layer = { InspectorBringToFrontBtn, InspectorBringForwardBtn, InspectorSendBackwardBtn, InspectorSendToBackBtn };
            Control target = FocusRescue.NextAvailable(layer, pressed) ?? CanvasScroller;
            target.Focus(how == FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard);
        }

        private void OnInspectorBringToFrontClick(object sender, RoutedEventArgs e) =>
            PressLayerButton(sender as Button, ViewModel.BringToFrontCommand);

        private void OnInspectorBringForwardClick(object sender, RoutedEventArgs e) =>
            PressLayerButton(sender as Button, ViewModel.BringForwardCommand);

        private void OnInspectorSendBackwardClick(object sender, RoutedEventArgs e) =>
            PressLayerButton(sender as Button, ViewModel.SendBackwardCommand);

        private void OnInspectorSendToBackClick(object sender, RoutedEventArgs e) =>
            PressLayerButton(sender as Button, ViewModel.SendToBackCommand);

        private void OnInspectorFlipHClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is not { } s) return;
            ViewModel.RecordUndo();
            s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(360 - s.Rotation);
            ViewModel.StatusMessage = $"Flipped {s.DisplayName.ToLowerInvariant()} horizontally";
            UpdateAdorner();
        }

        private void OnInspectorFlipVClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is not { } s) return;
            ViewModel.RecordUndo();
            s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(180 - s.Rotation);
            ViewModel.StatusMessage = $"Flipped {s.DisplayName.ToLowerInvariant()} vertically";
            UpdateAdorner();
        }

        private void OnInspectorRotateCwClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is null) return;
            ViewModel.RotateClockwiseCommand.Execute(null);
            UpdateAdorner();
        }

        private void OnInspectorRotateCcwClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is null) return;
            ViewModel.RotateCounterClockwiseCommand.Execute(null);
            UpdateAdorner();
        }

        private void OnInspectorSquareAspectClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is not { } s) return;
            ViewModel.RecordUndo();
            double sz = Math.Round((s.Width + s.Height) / 2.0);
            s.Width = s.Height = sz;
            ViewModel.StatusMessage = $"Set {s.DisplayName.ToLowerInvariant()} to 1:1 aspect ({sz:F0} × {sz:F0} px)";
            UpdateAdorner();
        }

        private void ShowMultiSelectionContextMenu(Point position)
        {
            var flyout = new MenuFlyout();

            if (ViewModel.CanAlign)
            {
                var alignSub = new MenuFlyoutSubItem
                {
                    Text = "Align shapes",
                    Icon = new FontIcon { Glyph = "\uE8E4" }
                };
                alignSub.Items.Add(ShapeMenuItem("Align left", "\uE8E4", () => { ViewModel.AlignLeftCommand.Execute(null); UpdateMultiSelectionAdorner(); }));
                alignSub.Items.Add(ShapeMenuItem("Align centre", "\uE8E3", () => { ViewModel.AlignCenterCommand.Execute(null); UpdateMultiSelectionAdorner(); }));
                alignSub.Items.Add(ShapeMenuItem("Align right", "\uE8E2", () => { ViewModel.AlignRightCommand.Execute(null); UpdateMultiSelectionAdorner(); }));
                alignSub.Items.Add(new MenuFlyoutSeparator());
                alignSub.Items.Add(ShapeMenuItem("Align top", "\uE74A", () => { ViewModel.AlignTopCommand.Execute(null); UpdateMultiSelectionAdorner(); }));
                alignSub.Items.Add(ShapeMenuItem("Align middle", "\uE8E3", () => { ViewModel.AlignMiddleCommand.Execute(null); UpdateMultiSelectionAdorner(); }));
                alignSub.Items.Add(ShapeMenuItem("Align bottom", "\uE74B", () => { ViewModel.AlignBottomCommand.Execute(null); UpdateMultiSelectionAdorner(); }));
                flyout.Items.Add(alignSub);
            }

            if (ViewModel.CanDistribute)
            {
                var distSub = new MenuFlyoutSubItem
                {
                    Text = "Distribute shapes",
                    Icon = new FontIcon { Glyph = "\uE8E3" }
                };
                distSub.Items.Add(ShapeMenuItem("Space left edges evenly", "\uE8AB", () => { ViewModel.DistributeHorizontalCommand.Execute(null); UpdateMultiSelectionAdorner(); }));
                distSub.Items.Add(ShapeMenuItem("Space top edges evenly", "\uE8CB", () => { ViewModel.DistributeVerticalCommand.Execute(null); UpdateMultiSelectionAdorner(); }));
                flyout.Items.Add(distSub);
            }

            var rotSub = new MenuFlyoutSubItem
            {
                Text = "Rotate group",
                Icon = new FontIcon { Glyph = "\uE7AD" }
            };
            rotSub.Items.Add(ShapeMenuItem("Rotate 90° CW", "\uE7AD", () => { ViewModel.RotateClockwise(); UpdateMultiSelectionAdorner(); }, "Ctrl+]"));
            rotSub.Items.Add(ShapeMenuItem("Rotate 90° CCW", "\uE7AD", () => { ViewModel.RotateCounterClockwise(); UpdateMultiSelectionAdorner(); }, "Ctrl+["));
            rotSub.Items.Add(ShapeMenuItem("Reset rotation (0°)", "\uE777", () => { ViewModel.ResetRotation(); UpdateMultiSelectionAdorner(); }));
            flyout.Items.Add(rotSub);

            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Duplicate selection", "\uE8C8", () => ViewModel.DuplicateSelectedCommand.Execute(null), "Ctrl+D"));
            flyout.Items.Add(ShapeMenuItem("Delete selection", "\uE74D", () => ViewModel.RemoveSelectedCommand.Execute(null), "Del"));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Deselect all", "\uE894", () => { ViewModel.ClearSelection(); UpdateAdorner(); }, "Esc"));

            flyout.ShowAt(MainCanvas, position);
        }

        private void OnMultiRotateHandlePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var selected = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (selected.Count < 2) return;
            CanvasScroller.Focus(FocusState.Pointer);
            _multiRotating = true;
            _multiRotated = false;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in selected)
            {
                var (l, t, r, b) = ShapeDesignStudioViewModel.RotatedBounds(s);
                if (l < minX) minX = l;
                if (t < minY) minY = t;
                if (r > maxX) maxX = r;
                if (b > maxY) maxY = b;
            }
            _groupCentroid = new Point(minX + (maxX - minX) / 2.0, minY + (maxY - minY) / 2.0);

            var cur = e.GetCurrentPoint(MainCanvas).Position;
            _initialGroupHandleAngle = Math.Atan2(cur.Y - _groupCentroid.Y, cur.X - _groupCentroid.X) * 180.0 / Math.PI;

            _initialGroupTransforms = selected.Select(s => (
                Shape: s,
                X: s.X,
                Y: s.Y,
                Width: s.Width,
                Height: s.Height,
                Rotation: s.Rotation,
                CenterOffsetX: (s.X + s.Width / 2.0) - _groupCentroid.X,
                CenterOffsetY: (s.Y + s.Height / 2.0) - _groupCentroid.Y
            )).ToList();

            if (sender is UIElement h) h.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnMultiRotateHandlePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_multiRotating || _initialGroupTransforms.Count < 2) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            double curAngle = Math.Atan2(cur.Y - _groupCentroid.Y, cur.X - _groupCentroid.X) * 180.0 / Math.PI;
            double deltaAngle = curAngle - _initialGroupHandleAngle;

            bool snap = IsDown(VirtualKey.Shift);
            if (snap)
            {
                deltaAngle = Math.Round(deltaAngle / 15.0) * 15.0;
            }

            if (!_multiRotated)
            {
                if (Math.Abs(deltaAngle) < 1.0) return;
                _multiRotated = true;
                ViewModel.RecordUndo();
            }

            double rad = deltaAngle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            foreach (var t in _initialGroupTransforms)
            {
                double ncx = _groupCentroid.X + (t.CenterOffsetX * cos - t.CenterOffsetY * sin);
                double ncy = _groupCentroid.Y + (t.CenterOffsetX * sin + t.CenterOffsetY * cos);
                t.Shape.X = ncx - t.Width / 2.0;
                t.Shape.Y = ncy - t.Height / 2.0;
                double newRot = (t.Rotation + deltaAngle) % 360.0;
                if (newRot < 0) newRot += 360.0;
                t.Shape.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle((int)Math.Round(newRot));
            }

            UpdateMultiSelectionAdorner();
            if (MultiSelectionBadgeText != null)
            {
                MultiSelectionBadgeText.Text = $"{CountText.Of(_initialGroupTransforms.Count, "shape")} · {deltaAngle:+0;-0;0}° rotated{(snap ? " (15° snap)" : "")}";
            }
            e.Handled = true;
        }

        private void OnMultiRotateHandlePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_multiRotating) return;
            if (sender is UIElement h) h.ReleasePointerCapture(e.Pointer);
            if (_multiRotated)
            {
                ViewModel.StatusMessage = $"Rotated group of {CountText.Of(_initialGroupTransforms.Count, "shape")} · Ctrl+Z to undo";
            }
            _multiRotating = false;
            _initialGroupTransforms.Clear();
            UpdateMultiSelectionAdorner();
            e.Handled = true;
        }

        private void OnMultiRotateHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _multiRotating = false;
            _initialGroupTransforms.Clear();
            UpdateMultiSelectionAdorner();
        }

        private void OnMultiRotateHandleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var selected = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (selected.Count < 2) return;
            ViewModel.RecordUndo();
            foreach (var s in selected)
            {
                s.Rotation = 0;
            }
            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"Reset rotation on {CountText.Of(selected.Count, "shape")} · Ctrl+Z to undo";
        }

        private void OnMultiRotateHandlePointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var selected = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (selected.Count < 2) return;
            var pt = e.GetCurrentPoint(MultiRotateHandle);
            int delta = pt.Properties.MouseWheelDelta;
            if (delta == 0) return;
            e.Handled = true;

            bool shift = IsDown(VirtualKey.Shift);
            double step = shift ? 15.0 : 1.0;
            double deltaAngle = delta > 0 ? step : -step;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in selected)
            {
                var (l, t, r, b) = ShapeDesignStudioViewModel.RotatedBounds(s);
                if (l < minX) minX = l;
                if (t < minY) minY = t;
                if (r > maxX) maxX = r;
                if (b > maxY) maxY = b;
            }
            var centroid = new Point(minX + (maxX - minX) / 2.0, minY + (maxY - minY) / 2.0);

            ViewModel.RecordUndo();
            double rad = deltaAngle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            foreach (var s in selected)
            {
                double offX = (s.X + s.Width / 2.0) - centroid.X;
                double offY = (s.Y + s.Height / 2.0) - centroid.Y;
                double ncx = centroid.X + (offX * cos - offY * sin);
                double ncy = centroid.Y + (offX * sin + offY * cos);
                s.X = ncx - s.Width / 2.0;
                s.Y = ncy - s.Height / 2.0;
                double newRot = (s.Rotation + deltaAngle) % 360.0;
                if (newRot < 0) newRot += 360.0;
                s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle((int)Math.Round(newRot));
            }

            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"Nudged group rotation: {deltaAngle:+0;-0;0}° · {CountText.Of(selected.Count, "shape")}";
        }

        private void OnMultiRotateHandleRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var selected = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (selected.Count < 2) return;
            e.Handled = true;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in selected)
            {
                var (l, t, r, b) = ShapeDesignStudioViewModel.RotatedBounds(s);
                if (l < minX) minX = l;
                if (t < minY) minY = t;
                if (r > maxX) maxX = r;
                if (b > maxY) maxY = b;
            }
            var centroid = new Point(minX + (maxX - minX) / 2.0, minY + (maxY - minY) / 2.0);

            Action<double> applyGroupRotation = (deg) =>
            {
                ViewModel.RecordUndo();
                double rad = deg * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);
                foreach (var s in selected)
                {
                    double offX = (s.X + s.Width / 2.0) - centroid.X;
                    double offY = (s.Y + s.Height / 2.0) - centroid.Y;
                    double ncx = centroid.X + (offX * cos - offY * sin);
                    double ncy = centroid.Y + (offX * sin + offY * cos);
                    s.X = ncx - s.Width / 2.0;
                    s.Y = ncy - s.Height / 2.0;
                    double newRot = (s.Rotation + deg) % 360.0;
                    if (newRot < 0) newRot += 360.0;
                    s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle((int)Math.Round(newRot));
                }
                UpdateMultiSelectionAdorner();
                ViewModel.StatusMessage = $"Rotated group by {deg:0}°";
            };

            var flyout = new MenuFlyout();
            flyout.Items.Add(ShapeMenuItem("Reset group rotation (0°)", "\uE777", () =>
            {
                ViewModel.RecordUndo();
                foreach (var s in selected) s.Rotation = 0;
                UpdateMultiSelectionAdorner();
                ViewModel.StatusMessage = $"Reset rotation on {CountText.Of(selected.Count, "shape")}";
            }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Rotate 90° clockwise", "\uE7AD", () => applyGroupRotation(90)));
            flyout.Items.Add(ShapeMenuItem("Rotate 90° counter-clockwise", "\uE7A6", () => applyGroupRotation(-90)));
            flyout.Items.Add(ShapeMenuItem("Rotate 180°", "\uE777", () => applyGroupRotation(180)));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Flip horizontally (mirror)", "\uE741", () =>
            {
                ViewModel.RecordUndo();
                foreach (var s in selected)
                {
                    double offX = (s.X + s.Width / 2.0) - centroid.X;
                    s.X = centroid.X - offX - s.Width / 2.0;
                    s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle((int)Math.Round(360.0 - s.Rotation));
                }
                UpdateMultiSelectionAdorner();
                ViewModel.StatusMessage = $"Flipped {CountText.Of(selected.Count, "shape")} horizontally";
            }));

            if (MultiRotateHandle != null)
                flyout.ShowAt(MultiRotateHandle, KeyboardContextMenu.PointIn(e, MultiRotateHandle));
        }

        private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is not FrameworkElement fe || !ReferenceEquals(fe, MainCanvas))
            {
                return;
            }
            e.Handled = true;
            if (!ViewModel.HasShapes || ViewModel.IsDense) return;

            if (ViewModel.SelectionCount == 0)
            {
                ViewModel.SelectAll();
                ViewModel.StatusMessage = $"Selected all {CountText.Of(ViewModel.Shapes.Count, "shape")} · Double-click canvas to clear";
            }
            else
            {
                ViewModel.ClearSelection();
                ViewModel.StatusMessage = "Cleared selection.";
            }
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnHandlePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Shapes.Rectangle h || _adornedShape is not { } s) return;
            CanvasScroller.Focus(FocusState.Pointer);
            _resizeEdges = _handles.First(x => ReferenceEquals(x.Handle, h)).Edges;
            _resizeFrom = (s.X, s.Y, s.Width, s.Height);
            _resizeStart = e.GetCurrentPoint(MainCanvas).Position;
            _resized = false;
            h.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnHandlePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_resizeEdges == ShapeDesignStudioViewModel.ResizeEdges.None || _adornedShape is not { } s) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            double dx = cur.X - _resizeStart.X, dy = cur.Y - _resizeStart.Y;
            if (!_resized)
            {
                if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
                _resized = true;
                ViewModel.RecordUndo();
            }
            bool keepAspect = IsDown(VirtualKey.Shift);
            ViewModel.ResizeShape(s, _resizeFrom, _resizeEdges, dx, dy, keepAspect: keepAspect);
            ViewModel.StatusMessage = ShapeDesignStudioViewModel.FormatResizeStatus(s, completed: false);
            UpdateResizeVisuals(s, cur, keepAspect);
            e.Handled = true;
        }

        private void OnHandlePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_resizeEdges == ShapeDesignStudioViewModel.ResizeEdges.None) return;
            HideResizeVisuals();
            if (sender is UIElement h) h.ReleasePointerCapture(e.Pointer);
            if (_resized && _adornedShape is { } s)
                ViewModel.StatusMessage = ShapeDesignStudioViewModel.FormatResizeStatus(s, completed: true);
            _resizeEdges = ShapeDesignStudioViewModel.ResizeEdges.None;
            e.Handled = true;
        }

        private void OnRotateHandlePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Shapes.Ellipse h || _adornedShape is null) return;
            CanvasScroller.Focus(FocusState.Pointer);
            _rotating = true;
            _rotated = false;
            h.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnRotateHandlePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_rotating || _adornedShape is not { } s) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            double cx = s.X + s.Width / 2.0, cy = s.Y + s.Height / 2.0;
            bool snap = IsDown(VirtualKey.Shift);
            int nextAngle = ShapeDesignStudioViewModel.ComputeRotationAngle(cx, cy, cur.X, cur.Y, snapTo15: snap);
            if (!_rotated)
            {
                if (nextAngle == ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation)) return;
                _rotated = true;
                ViewModel.RecordUndo();
            }
            s.Rotation = nextAngle;
            UpdateRotateVisuals(s, nextAngle, snap);
            UpdateCursorHud(cur, $"Angle: {nextAngle}°{(snap ? " [15° snap]" : "")}");
            ViewModel.StatusMessage = $"Rotating {s.DisplayName.ToLowerInvariant()}: {nextAngle}°{(snap ? " (15° snap)" : "")}";
            e.Handled = true;
        }

        private void OnRotateHandlePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_rotating) return;
            HideRotateVisuals();
            if (sender is UIElement h) h.ReleasePointerCapture(e.Pointer);
            if (_rotated && _adornedShape is { } s)
                ViewModel.StatusMessage = $"Rotated {s.DisplayName.ToLowerInvariant()} to {ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation)}° · Ctrl+Z to undo";
            _rotating = false;
            e.Handled = true;
        }

        private void OnRotateHandleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            _rotating = false;
            HideRotateVisuals();
            if (_adornedShape is not null)
            {
                ViewModel.ResetRotation();
                e.Handled = true;
            }
        }

        private void OnRotateHandlePointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_adornedShape is not { } s) return;
            var pt = e.GetCurrentPoint(_rotateHandle);
            int delta = pt.Properties.MouseWheelDelta;
            if (delta == 0) return;

            e.Handled = true;
            bool shift = IsDown(VirtualKey.Shift);
            bool alt = IsDown(VirtualKey.Menu);
            int step = shift ? 15 : (alt ? 1 : 5);
            int change = (delta > 0 ? step : -step);

            ViewModel.RecordUndo();
            int curAngle = ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation);
            int rawNext = ShapeDesignStudioViewModel.WrapRotationAngle(curAngle + change);
            int nextAngle = rawNext;
            bool snapped = false;

            int[] detents = { 0, 45, 90, 135, 180, 225, 270, 315, 360 };
            foreach (var d in detents)
            {
                int normD = d % 360;
                if (Math.Abs(rawNext - normD) <= 2 || Math.Abs(rawNext - (normD + 360)) <= 2)
                {
                    nextAngle = normD;
                    snapped = true;
                    break;
                }
            }

            s.Rotation = nextAngle;
            UpdateRotateVisuals(s, nextAngle, snapped || shift);
            ViewModel.StatusMessage = $"Turned {s.DisplayName.ToLowerInvariant()} to {nextAngle}° via wheel{(snapped ? " (magnetic snap)" : (shift ? " (15° snap)" : (alt ? " (fine 1°)" : " (5° step)")))} · Ctrl+Z to undo";
        }

        private void OnRotateHandleRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_adornedShape is not { } s) return;
            e.Handled = true;
            var flyout = new MenuFlyout();

            flyout.Items.Add(ShapeMenuItem("Reset rotation (0°)", "\uE777", () => ViewModel.ResetRotationCommand.Execute(null), "Alt+0",
                enabled: ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation) != 0));
            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Rotate 90° clockwise", "\uE7AD", () => ViewModel.RotateClockwiseCommand.Execute(null), "Alt+Shift+Right"));
            flyout.Items.Add(ShapeMenuItem("Rotate 90° counter-clockwise", "\uE7A7", () => ViewModel.RotateCounterClockwiseCommand.Execute(null), "Alt+Shift+Left"));
            flyout.Items.Add(ShapeMenuItem("Rotate 180°", "\uE7A6", () =>
            {
                ViewModel.RecordUndo();
                s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation + 180);
                ViewModel.StatusMessage = $"Rotated {s.DisplayName.ToLowerInvariant()} 180° to {s.Rotation}°";
            }));

            var angleSub = new MenuFlyoutSubItem
            {
                Text = "Preset angles",
                Icon = new FontIcon { Glyph = "\uE809" }
            };
            int[] presetAngles = { 0, 15, 30, 45, 60, 90, 120, 135, 180, 225, 270, 315 };
            foreach (int angle in presetAngles)
            {
                int a = angle;
                angleSub.Items.Add(ShapeMenuItem($"{a}°", "\uE7B5", () =>
                {
                    ViewModel.RecordUndo();
                    s.Rotation = a;
                    ViewModel.StatusMessage = $"Set {s.DisplayName.ToLowerInvariant()} rotation to {a}°";
                }));
            }
            flyout.Items.Add(angleSub);
            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Flip horizontal", "\uE8E2", () =>
            {
                ViewModel.RecordUndo();
                s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(360 - s.Rotation);
                ViewModel.StatusMessage = $"Flipped {s.DisplayName.ToLowerInvariant()} horizontally";
            }));
            flyout.Items.Add(ShapeMenuItem("Flip vertical", "\uE8E1", () =>
            {
                ViewModel.RecordUndo();
                s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(180 - s.Rotation);
                ViewModel.StatusMessage = $"Flipped {s.DisplayName.ToLowerInvariant()} vertically";
            }));
            flyout.Items.Add(ShapeMenuItem($"Copy angle ({ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation)}°)", "\uE8C8", () =>
            {
                int cur = ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation);
                CopyAndReport($"{cur}°", $"Copied angle: {cur}°");
            }));

            flyout.ShowAt(_rotateHandle, e.GetPosition(_rotateHandle));
        }

        private void HideRotateVisuals()
        {
            if (_rotateArcOverlay != null) _rotateArcOverlay.Visibility = Visibility.Collapsed;
            if (_rotateAngleBadge != null) _rotateAngleBadge.Visibility = Visibility.Collapsed;
            if (_rotateReferenceGuide != null) _rotateReferenceGuide.Visibility = Visibility.Collapsed;
            HideCursorHud();
        }

        private void UpdateRotateVisuals(ShapeCanvasItemViewModel s, int angle, bool snapped)
        {
            if (_rotateArcOverlay == null || _rotateAngleBadge == null || _rotateReferenceGuide == null || _rotateAngleBadgeText == null) return;
            double z = Math.Max(0.1, CanvasScroller.ZoomFactor);
            double cx = s.X + s.Width / 2.0;
            double cy = s.Y + s.Height / 2.0;

            int wrapped = ShapeDesignStudioViewModel.WrapRotationAngle(angle);
            double radius = Math.Max(24.0, (s.Height / 2.0) + (ShapeDesignStudioViewModel.RotationHandleStemLength / z));

            // Reference top guide (0°)
            _rotateReferenceGuide.X1 = cx;
            _rotateReferenceGuide.Y1 = cy;
            _rotateReferenceGuide.X2 = cx;
            _rotateReferenceGuide.Y2 = cy - radius;
            _rotateReferenceGuide.StrokeThickness = 1.0 / z;
            _rotateReferenceGuide.Visibility = Visibility.Visible;

            // Rotation angle badge
            var (rotX, rotY) = ShapeDesignStudioViewModel.RotationHandlePosition(
                s.X, s.Y, s.Width, s.Height, angle, ShapeDesignStudioViewModel.RotationHandleStemLength / z);

            _rotateAngleBadgeText.Text = $"{wrapped}°{(snapped ? " (15°)" : "")}";
            _rotateAngleBadgeText.FontSize = Math.Max(9.0, 10.5 / Math.Sqrt(z));
            Canvas.SetLeft(_rotateAngleBadge, rotX - 26 / z);
            Canvas.SetTop(_rotateAngleBadge, rotY - 32 / z);
            _rotateAngleBadge.BorderThickness = new Thickness(1.0 / z);
            _rotateAngleBadge.Visibility = Visibility.Visible;

            if (wrapped == 0)
            {
                _rotateArcOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            // Arc overlay geometry from 0° (top: -90°) to angle
            double startAngleRad = -Math.PI / 2.0; // 0° is straight up
            double endAngleRad = startAngleRad + (wrapped * Math.PI / 180.0);

            double startX = cx + radius * Math.Cos(startAngleRad);
            double startY = cy + radius * Math.Sin(startAngleRad);
            double endX = cx + radius * Math.Cos(endAngleRad);
            double endY = cy + radius * Math.Sin(endAngleRad);

            bool isLargeArc = wrapped > 180;

            var fig = new PathFigure
            {
                StartPoint = new Point(cx, cy),
                IsClosed = true
            };
            fig.Segments.Add(new LineSegment { Point = new Point(startX, startY) });
            fig.Segments.Add(new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(radius, radius),
                IsLargeArc = isLargeArc,
                SweepDirection = SweepDirection.Clockwise
            });

            var geo = new PathGeometry();
            geo.Figures.Add(fig);

            _rotateArcOverlay.Data = geo;
            _rotateArcOverlay.StrokeThickness = 1.25 / z;
            _rotateArcOverlay.Visibility = Visibility.Visible;
        }

        private void OnHandleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Shapes.Rectangle h || _adornedShape is not { } s) return;
            e.Handled = true;
            ViewModel.RecordUndo();
            double avg = Math.Round((s.Width + s.Height) / 2.0);
            s.Width = avg;
            s.Height = avg;
            ViewModel.StatusMessage = $"Snapped {s.DisplayName.ToLowerInvariant()} to 1:1 square ({avg:F0} × {avg:F0} px) · Ctrl+Z to undo";
            UpdateAdorner();
        }

        private void OnHandleRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Shapes.Rectangle h || _adornedShape is not { } s) return;
            e.Handled = true;
            var flyout = new MenuFlyout();

            flyout.Items.Add(ShapeMenuItem("Square 1:1", "\uE739", () =>
            {
                ViewModel.RecordUndo();
                double sz = Math.Round((s.Width + s.Height) / 2.0);
                s.Width = s.Height = sz;
                ViewModel.StatusMessage = $"Set {s.DisplayName.ToLowerInvariant()} to 1:1 aspect ({sz:F0} × {sz:F0} px)";
                UpdateAdorner();
            }));

            flyout.Items.Add(ShapeMenuItem("Standard 4:3", "\uE7C3", () =>
            {
                ViewModel.RecordUndo();
                s.Height = Math.Round(s.Width * 0.75);
                ViewModel.StatusMessage = $"Set {s.DisplayName.ToLowerInvariant()} to 4:3 aspect ({s.Width:F0} × {s.Height:F0} px)";
                UpdateAdorner();
            }));

            flyout.Items.Add(ShapeMenuItem("Widescreen 16:9", "\uE7F4", () =>
            {
                ViewModel.RecordUndo();
                s.Height = Math.Round(s.Width * 9.0 / 16.0);
                ViewModel.StatusMessage = $"Set {s.DisplayName.ToLowerInvariant()} to 16:9 aspect ({s.Width:F0} × {s.Height:F0} px)";
                UpdateAdorner();
            }));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Double size (200%)", "\uE71F", () =>
            {
                ViewModel.RecordUndo();
                s.Width = Math.Round(s.Width * 2.0);
                s.Height = Math.Round(s.Height * 2.0);
                ViewModel.StatusMessage = $"Scaled {s.DisplayName.ToLowerInvariant()} to 200% ({s.Width:F0} × {s.Height:F0} px)";
                UpdateAdorner();
            }));

            flyout.Items.Add(ShapeMenuItem("Half size (50%)", "\uE740", () =>
            {
                ViewModel.RecordUndo();
                s.Width = Math.Max(10.0, Math.Round(s.Width * 0.5));
                s.Height = Math.Max(10.0, Math.Round(s.Height * 0.5));
                ViewModel.StatusMessage = $"Scaled {s.DisplayName.ToLowerInvariant()} to 50% ({s.Width:F0} × {s.Height:F0} px)";
                UpdateAdorner();
            }));

            flyout.ShowAt(h, e.GetPosition(h));
        }

        private void UpdateResizeVisuals(ShapeCanvasItemViewModel s, Point cur, bool keepAspect)
        {
            if (_resizeDimensionBadge == null || _resizeDimensionBadgeText == null || _resizeAspectGuide == null) return;
            double z = Math.Max(0.05, CanvasScroller.ZoomFactor);

            string ratioText = "";
            if (s.Height > 0)
            {
                double ratio = s.Width / s.Height;
                if (Math.Abs(ratio - 1.0) < 0.04) ratioText = " (1:1)";
                else if (Math.Abs(ratio - 4.0 / 3.0) < 0.04) ratioText = " (4:3)";
                else if (Math.Abs(ratio - 16.0 / 9.0) < 0.04) ratioText = " (16:9)";
                else if (Math.Abs(ratio - 2.0) < 0.04) ratioText = " (2:1)";
            }

            _resizeDimensionBadgeText.Text = $"{(int)Math.Round(s.Width)} × {(int)Math.Round(s.Height)} px{ratioText}";
            _resizeDimensionBadgeText.FontSize = Math.Max(9.0, 10.5 / Math.Sqrt(z));
            _resizeDimensionBadge.BorderThickness = new Thickness(1.0 / z);
            _resizeDimensionBadge.CornerRadius = new CornerRadius(10.0 / z);
            _resizeDimensionBadge.Padding = new Thickness(7.0 / z, 2.0 / z, 7.0 / z, 2.0 / z);
            _resizeDimensionBadge.Visibility = Visibility.Visible;
            Canvas.SetLeft(_resizeDimensionBadge, cur.X + (12.0 / z));
            Canvas.SetTop(_resizeDimensionBadge, cur.Y - (24.0 / z));

            if (keepAspect)
            {
                _resizeAspectGuide.X1 = s.X;
                _resizeAspectGuide.Y1 = s.Y;
                _resizeAspectGuide.X2 = s.X + s.Width;
                _resizeAspectGuide.Y2 = s.Y + s.Height;
                _resizeAspectGuide.StrokeThickness = 1.0 / z;
                _resizeAspectGuide.Visibility = Visibility.Visible;
            }
            else
            {
                _resizeAspectGuide.Visibility = Visibility.Collapsed;
            }
        }

        private void HideResizeVisuals()
        {
            if (_resizeDimensionBadge != null) _resizeDimensionBadge.Visibility = Visibility.Collapsed;
            if (_resizeAspectGuide != null) _resizeAspectGuide.Visibility = Visibility.Collapsed;
        }

        // ---- connector ends ----
        // A selected line shows a round handle on each of its points. Dragging an end re-routes
        // the line, snapping onto the side or centre of a shape it's dropped near (that shape's
        // point is ringed while it would snap); a bend of an elbow line moves freely. Alt drops
        // it exactly where the pointer is.

        private Canvas? _pointAdorner;
        private Microsoft.UI.Xaml.Shapes.Ellipse? _snapRing;
        private readonly List<Microsoft.UI.Xaml.Shapes.Ellipse> _pointHandles = new();
        private int _pointIndex = -1;
        private bool _pointMoved;

        private void EnsurePointAdorner()
        {
            if (_pointAdorner is not null) return;
            _pointAdorner = new Canvas { Visibility = Visibility.Collapsed };
            _snapRing = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            ThemeBrush.Set(_snapRing, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
            ThemeBrush.Set(_snapRing, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "AccentFillColorTertiaryBrush");
            _pointAdorner.Children.Add(_snapRing);
            MainCanvas.Children.Add(_pointAdorner);
        }

        private void UpdatePointHandles(ShapeCanvasItemViewModel? s)
        {
            bool show = s is not null && !ViewModel.IsDense && !ViewModel.IsPlacing && ViewModel.SelectionCount == 1
                        && s.PathPoints is { Count: >= 2 } && ViewModel.Shapes.Contains(s);
            if (!show && _pointAdorner is null) return;
            EnsurePointAdorner();
            _pointAdorner!.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show || s is null) return;

            var points = ShapeDesignStudioViewModel.ConnectorPoints(s);
            while (_pointHandles.Count < points.Count)
            {
                var h = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    StrokeThickness = 1.25,
                };
                ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "SolidBackgroundFillColorPrimaryBrush");
                ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
                ToolTipService.SetToolTip(h, "Drag to re-route · it snaps to a shape's side or centre (Alt: no snap) · Double-click to reset/reverse · Right-click for options");
                SetCursor(h, Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                h.PointerEntered += (_, _) => { if (_pointIndex < 0) ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "AccentFillColorSecondaryBrush"); };
                h.PointerExited += (_, _) => { if (_pointIndex < 0) ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "SolidBackgroundFillColorPrimaryBrush"); };
                h.PointerPressed += OnPointHandlePressed;
                h.PointerMoved += OnPointHandleMoved;
                h.PointerReleased += OnPointHandleReleased;
                h.DoubleTapped += OnPointHandleDoubleTapped;
                h.RightTapped += OnPointHandleRightTapped;
                h.PointerCaptureLost += (_, _) => { _pointIndex = -1; ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "SolidBackgroundFillColorPrimaryBrush"); if (_snapRing is not null) _snapRing.Visibility = Visibility.Collapsed; };
                _pointAdorner.Children.Add(h);
                _pointHandles.Add(h);
            }
            double z = Math.Max(0.05, CanvasScroller.ZoomFactor);
            double size = (HandleSize + 2) / z;
            for (int i = 0; i < _pointHandles.Count; i++)
            {
                var h = _pointHandles[i];
                if (i >= points.Count) { h.Visibility = Visibility.Collapsed; continue; }
                bool end = i == 0 || i == points.Count - 1;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(h, i == 0 ? "Line start" : i == points.Count - 1 ? "Line end" : $"Bend {i}");
                h.Visibility = Visibility.Visible;
                h.Width = h.Height = end ? size : size * 0.8;
                h.StrokeThickness = Math.Max(1.25, 1.5 / z);
                Canvas.SetLeft(h, points[i].X - h.Width / 2);
                Canvas.SetTop(h, points[i].Y - h.Height / 2);
            }
        }

        private void OnPointHandlePressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Shapes.Ellipse h || _adornedShape is null) return;
            CanvasScroller.Focus(FocusState.Pointer);
            _pointIndex = _pointHandles.IndexOf(h);
            _pointMoved = false;
            h.CapturePointer(e.Pointer);
            ThemeBrush.Set(h, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
            e.Handled = true;
        }

        private void OnPointHandleMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_pointIndex < 0 || _adornedShape is not { } line) return;
            var cur = e.GetCurrentPoint(MainCanvas).Position;
            if (!_pointMoved)
            {
                _pointMoved = true;
                ViewModel.RecordUndo();
            }
            bool snap = !IsDown(VirtualKey.Menu);
            var target = ViewModel.MoveConnectorPoint(line, _pointIndex, cur.X, cur.Y, snap);
            if (_snapRing is not null)
            {
                var pts = ShapeDesignStudioViewModel.ConnectorPoints(line);
                double z = Math.Max(0.05, CanvasScroller.ZoomFactor), r = 9 / z;
                _snapRing.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
                _snapRing.Width = _snapRing.Height = r * 2;
                _snapRing.StrokeThickness = Math.Max(1.5, 2.0 / z);
                if (_pointIndex < pts.Count)
                {
                    Canvas.SetLeft(_snapRing, pts[_pointIndex].X - r);
                    Canvas.SetTop(_snapRing, pts[_pointIndex].Y - r);
                }
                if (target is not null)
                {
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_snapRing, $"Snapped to {target.DisplayName.ToLowerInvariant()}");
                }
            }
            ViewModel.StatusMessage = target is null ? "Re-routing the line" : $"Snapped to {target.DisplayName.ToLowerInvariant()}";
            e.Handled = true;
        }

        private void OnPointHandleReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_pointIndex < 0) return;
            if (sender is UIElement h)
            {
                h.ReleasePointerCapture(e.Pointer);
                if (h is Microsoft.UI.Xaml.Shapes.Ellipse ell)
                    ThemeBrush.Set(ell, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "SolidBackgroundFillColorPrimaryBrush");
            }
            if (_snapRing is not null) _snapRing.Visibility = Visibility.Collapsed;
            if (_pointMoved) ViewModel.StatusMessage = "Line re-routed · Ctrl+Z to undo";
            _pointIndex = -1;
            e.Handled = true;
        }

        private void OnPointHandleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Shapes.Ellipse h || _adornedShape is not { } line) return;
            int idx = _pointHandles.IndexOf(h);
            var points = ShapeDesignStudioViewModel.ConnectorPoints(line);
            if (idx < 0 || idx >= points.Count) return;

            e.Handled = true;
            ViewModel.RecordUndo();

            bool isEnd = idx == 0 || idx == points.Count - 1;
            if (isEnd)
            {
                points.Reverse();
                ShapeDesignStudioViewModel.SetConnectorPoints(line, points);
                ViewModel.StatusMessage = "Connector reversed (start and end swapped) · Ctrl+Z to undo";
            }
            else
            {
                if (points.Count == 3)
                {
                    double midX = (points[0].X + points[2].X) / 2.0;
                    double midY = (points[0].Y + points[2].Y) / 2.0;
                    points[idx] = (midX, midY);
                    ShapeDesignStudioViewModel.SetConnectorPoints(line, points);
                    ViewModel.StatusMessage = "Bend point centered between line ends · Ctrl+Z to undo";
                }
                else
                {
                    double midX = (points[idx - 1].X + points[idx + 1].X) / 2.0;
                    double midY = (points[idx - 1].Y + points[idx + 1].Y) / 2.0;
                    points[idx] = (midX, midY);
                    ShapeDesignStudioViewModel.SetConnectorPoints(line, points);
                    ViewModel.StatusMessage = $"Bend {idx} centered between neighbours · Ctrl+Z to undo";
                }
            }
            UpdatePointHandles(line);
        }

        private void OnPointHandleRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Shapes.Ellipse h || _adornedShape is not { } line) return;
            int idx = _pointHandles.IndexOf(h);
            var points = ShapeDesignStudioViewModel.ConnectorPoints(line);
            if (idx < 0 || idx >= points.Count) return;

            e.Handled = true;
            var flyout = new MenuFlyout();
            bool isEnd = idx == 0 || idx == points.Count - 1;
            string ptLabel = idx == 0 ? "Line start" : idx == points.Count - 1 ? "Line end" : $"Bend {idx}";

            flyout.Items.Add(ShapeMenuItem("Reverse direction (swap ends)", "\uE747", () =>
            {
                ViewModel.RecordUndo();
                var pts = ShapeDesignStudioViewModel.ConnectorPoints(line);
                pts.Reverse();
                ShapeDesignStudioViewModel.SetConnectorPoints(line, pts);
                ViewModel.StatusMessage = "Connector reversed · Ctrl+Z to undo";
                UpdatePointHandles(line);
            }));

            flyout.Items.Add(ShapeMenuItem("Auto-route around obstacles (Elbow Corridor)", "\uE748", () =>
            {
                AutoRouteConnectorAroundObstacles(line);
            }));

            if (!isEnd)
            {
                flyout.Items.Add(ShapeMenuItem("Center bend between ends", "\uE790", () =>
                {
                    ViewModel.RecordUndo();
                    var pts = ShapeDesignStudioViewModel.ConnectorPoints(line);
                    if (idx < pts.Count)
                    {
                        pts[idx] = ((pts[0].X + pts[^1].X) / 2.0, (pts[0].Y + pts[^1].Y) / 2.0);
                        ShapeDesignStudioViewModel.SetConnectorPoints(line, pts);
                        ViewModel.StatusMessage = "Bend centered · Ctrl+Z to undo";
                        UpdatePointHandles(line);
                    }
                }));

                flyout.Items.Add(ShapeMenuItem("Align horizontally with start", "\uE783", () =>
                {
                    ViewModel.RecordUndo();
                    var pts = ShapeDesignStudioViewModel.ConnectorPoints(line);
                    if (idx < pts.Count)
                    {
                        pts[idx] = (pts[idx].X, pts[0].Y);
                        ShapeDesignStudioViewModel.SetConnectorPoints(line, pts);
                        ViewModel.StatusMessage = "Bend aligned horizontally with start · Ctrl+Z to undo";
                        UpdatePointHandles(line);
                    }
                }));

                flyout.Items.Add(ShapeMenuItem("Align vertically with end", "\uE784", () =>
                {
                    ViewModel.RecordUndo();
                    var pts = ShapeDesignStudioViewModel.ConnectorPoints(line);
                    if (idx < pts.Count)
                    {
                        pts[idx] = (pts[^1].X, pts[idx].Y);
                        ShapeDesignStudioViewModel.SetConnectorPoints(line, pts);
                        ViewModel.StatusMessage = "Bend aligned vertically with end · Ctrl+Z to undo";
                        UpdatePointHandles(line);
                    }
                }));

                if (points.Count > 2)
                {
                    flyout.Items.Add(ShapeMenuItem("Straighten line (remove bend)", "\uE74D", () =>
                    {
                        ViewModel.RecordUndo();
                        var pts = ShapeDesignStudioViewModel.ConnectorPoints(line);
                        if (pts.Count > idx)
                        {
                            pts.RemoveAt(idx);
                            ShapeDesignStudioViewModel.SetConnectorPoints(line, pts);
                            ViewModel.StatusMessage = "Bend removed (straightened) · Ctrl+Z to undo";
                            UpdatePointHandles(line);
                        }
                    }));
                }
            }
            else
            {
                flyout.Items.Add(ShapeMenuItem("Snap to nearest shape anchor", "\uE71B", () =>
                {
                    var pt = points[idx];
                    if (ViewModel.SnapTarget(pt.X, pt.Y, line, radius: 48) is { } target)
                    {
                        ViewModel.RecordUndo();
                        var res = ViewModel.MoveConnectorPoint(line, idx, target.X, target.Y, snap: true);
                        ViewModel.StatusMessage = res != null ? $"Snapped to {res.DisplayName.ToLowerInvariant()}" : "Snapped to shape";
                        UpdatePointHandles(line);
                    }
                    else
                    {
                        ViewModel.StatusMessage = "No shape anchor found within 48 px";
                    }
                }));
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            var curPt = points[idx];
            flyout.Items.Add(ShapeMenuItem($"Copy {ptLabel.ToLowerInvariant()} coordinates ({curPt.X:F0}, {curPt.Y:F0})", "\uE8C8", () =>
            {
                CopyAndReport($"({curPt.X:F0}, {curPt.Y:F0})", $"Copied coordinates ({curPt.X:F0}, {curPt.Y:F0}) to clipboard");
            }));

            flyout.ShowAt(h, e.GetPosition(h));
        }

        private void AutoRouteConnectorAroundObstacles(ShapeCanvasItemViewModel line)
        {
            var pts = ShapeDesignStudioViewModel.ConnectorPoints(line);
            if (pts.Count < 2) return;

            var pStart = pts[0];
            var pEnd = pts[^1];

            ShapeCanvasItemViewModel? startShape = ViewModel.SnapTarget(pStart.X, pStart.Y, line, radius: 24)?.Shape;
            ShapeCanvasItemViewModel? endShape = ViewModel.SnapTarget(pEnd.X, pEnd.Y, line, radius: 24)?.Shape;

            double clearance = 16.0;

            double minX = Math.Min(pStart.X, pEnd.X);
            double maxX = Math.Max(pStart.X, pEnd.X);
            double minY = Math.Min(pStart.Y, pEnd.Y);
            double maxY = Math.Max(pStart.Y, pEnd.Y);

            var obstacles = ViewModel.Shapes
                .Where(s => !ReferenceEquals(s, line) &&
                            !ReferenceEquals(s, startShape) &&
                            !ReferenceEquals(s, endShape) &&
                            (s.PathPoints == null || s.PathPoints.Count < 2))
                .Select(s => (Shape: s, Bounds: ShapeDesignStudioViewModel.RotatedBounds(s)))
                .Where(t => t.Bounds.Right > minX + 5 && t.Bounds.Left < maxX - 5 &&
                            t.Bounds.Bottom > minY + 5 && t.Bounds.Top < maxY - 5)
                .ToList();

            var newPoints = new List<(double X, double Y)>();
            newPoints.Add(pStart);

            if (obstacles.Count > 0)
            {
                var obs = obstacles.OrderByDescending(o => (o.Bounds.Right - o.Bounds.Left) * (o.Bounds.Bottom - o.Bounds.Top)).First();
                var b = obs.Bounds;

                bool isHorizontal = Math.Abs(pEnd.X - pStart.X) >= Math.Abs(pEnd.Y - pStart.Y);

                if (isHorizontal)
                {
                    double detourYTop = b.Top - clearance;
                    double detourYBottom = b.Bottom + clearance;

                    double avgY = (pStart.Y + pEnd.Y) / 2.0;
                    double chosenY = Math.Abs(detourYTop - avgY) <= Math.Abs(detourYBottom - avgY) ? detourYTop : detourYBottom;

                    double midX1 = pStart.X < pEnd.X ? Math.Min(pStart.X + 24, b.Left - clearance) : Math.Max(pStart.X - 24, b.Right + clearance);
                    double midX2 = pStart.X < pEnd.X ? Math.Max(pEnd.X - 24, b.Right + clearance) : Math.Min(pEnd.X + 24, b.Left - clearance);

                    newPoints.Add((midX1, pStart.Y));
                    newPoints.Add((midX1, chosenY));
                    newPoints.Add((midX2, chosenY));
                    newPoints.Add((midX2, pEnd.Y));
                }
                else
                {
                    double detourXLeft = b.Left - clearance;
                    double detourXRight = b.Right + clearance;

                    double avgX = (pStart.X + pEnd.X) / 2.0;
                    double chosenX = Math.Abs(detourXLeft - avgX) <= Math.Abs(detourXRight - avgX) ? detourXLeft : detourXRight;

                    double midY1 = pStart.Y < pEnd.Y ? Math.Min(pStart.Y + 24, b.Top - clearance) : Math.Max(pStart.Y - 24, b.Bottom + clearance);
                    double midY2 = pStart.Y < pEnd.Y ? Math.Max(pEnd.Y - 24, b.Bottom + clearance) : Math.Min(pEnd.Y + 24, b.Top - clearance);

                    newPoints.Add((pStart.X, midY1));
                    newPoints.Add((chosenX, midY1));
                    newPoints.Add((chosenX, midY2));
                    newPoints.Add((pEnd.X, midY2));
                }

                ViewModel.StatusMessage = $"Auto-routed connector around '{obs.Shape.DisplayName}' with {clearance:F0} px corridor · Ctrl+Z to undo";
            }
            else
            {
                bool isHorizontal = Math.Abs(pEnd.X - pStart.X) >= Math.Abs(pEnd.Y - pStart.Y);
                if (isHorizontal)
                {
                    double midX = (pStart.X + pEnd.X) / 2.0;
                    newPoints.Add((midX, pStart.Y));
                    newPoints.Add((midX, pEnd.Y));
                }
                else
                {
                    double midY = (pStart.Y + pEnd.Y) / 2.0;
                    newPoints.Add((pStart.X, midY));
                    newPoints.Add((pEnd.X, midY));
                }
                ViewModel.StatusMessage = "Auto-routed connector with clean orthogonal corridor · Ctrl+Z to undo";
            }

            newPoints.Add(pEnd);

            ViewModel.RecordUndo();
            ShapeDesignStudioViewModel.SetConnectorPoints(line, newPoints);
            UpdatePointHandles(line);
        }

        // ---- cursors ----
        // UIElement.ProtectedCursor is protected in WinUI 3; reflection is the usual way to set it on
        // elements we don't subclass.
        private static readonly System.Reflection.PropertyInfo? CursorProperty =
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static void SetCursor(UIElement element, Microsoft.UI.Input.InputSystemCursorShape shape)
        {
            try { CursorProperty?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape)); }
            catch { /* cosmetic only */ }
        }

        private static Windows.UI.Color AccentColor(byte alpha)
        {
            var c = Application.Current.Resources.TryGetValue("SystemAccentColor", out var v) && v is Windows.UI.Color accent
                ? accent : Windows.UI.Color.FromArgb(255, 0, 120, 212);
            return Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        // ---- inspector: fill / line colour picker ----

        private void OnFillFlyoutOpening(object? sender, object e)
        {
            bool isLine = ViewModel.SelectedShape?.IsLine == true;
            FillFlyoutTitle.Text = isLine ? "Line colour" : "Fill colour";
            var hex = (ViewModel.SelectedShape?.Fill ?? "0078D4").TrimStart('#');
            if (hex.Length == 6)
            {
                try
                {
                    FillPicker.Color = Windows.UI.Color.FromArgb(255,
                        Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
                    FillFlyoutHexText.Text = $"#{hex.ToUpperInvariant()}";
                }
                catch { }
            }
            RebuildFillFlyoutSwatches(hex);
            RebuildRecentColorsSwatches(hex);
        }

        private void OnFillPickerColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            var c = args.NewColor;
            if (FillFlyoutHexText is not null)
                FillFlyoutHexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private static readonly List<string> _recentColors = new() { "0078D4", "107C41", "D83B01", "5C2D91", "008272", "D13438" };

        private static void AddRecentColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return;
            string clean = hex.Trim().TrimStart('#').ToUpperInvariant();
            if (clean.Length != 6) return;
            _recentColors.Remove(clean);
            _recentColors.Insert(0, clean);
            if (_recentColors.Count > 8) _recentColors.RemoveAt(_recentColors.Count - 1);
        }

        private void RebuildRecentColorsSwatches(string activeHex)
        {
            if (RecentColorsSwatches is null) return;
            RecentColorsSwatches.Children.Clear();
            if (RecentColorsContainer is not null)
            {
                RecentColorsContainer.Visibility = _recentColors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (string hex in _recentColors)
            {
                bool active = string.Equals(hex, activeHex, StringComparison.OrdinalIgnoreCase);
                var btn = new Button
                {
                    Width = 26,
                    Height = 22,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    Background = BrushFromHex(hex),
                    BorderThickness = new Thickness(active ? 2 : 1),
                };
                if (active)
                    btn.BorderBrush = new SolidColorBrush(AccentColor(0xFF));
                else
                    ThemeBrush.Set(btn, Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                ToolTipService.SetToolTip(btn, $"#{hex} (Recent · Right-click or Shift+F10 for options)");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"Apply recent colour #{hex}");
                string capturedHex = hex;
                int capturedIndex = RecentColorsSwatches.Children.Count;
                btn.Click += (_, _) =>
                {
                    if (ViewModel.SelectedShape is { } s)
                    {
                        ViewModel.RecordUndo();
                        s.Fill = capturedHex;
                        AddRecentColor(capturedHex);
                        ViewModel.StatusMessage = $"{(s.IsLine ? "Line" : "Fill")} colour #{s.Fill}";
                        FillFlyout.Hide();
                    }
                };
                RightTappedEventHandler openMenu = (s, e) =>
                {
                    e.Handled = true;
                    // Keyboard users get a focus rectangle on the swatch that takes its place.
                    var how = KeyboardContextMenu.IsKeyboardInvocation ? FocusState.Keyboard : FocusState.Programmatic;
                    var menu = new MenuFlyout();
                    var copy = new MenuFlyoutItem { Text = $"Copy #{capturedHex}", Icon = new FontIcon { Glyph = "\uE8C8" } };
                    copy.Click += (_, _) =>
                    {
                        CopyAndReport($"#{capturedHex}", $"Copied #{capturedHex} to clipboard");
                    };
                    menu.Items.Add(copy);
                    var remove = new MenuFlyoutItem { Text = "Remove from recent", Icon = new FontIcon { Glyph = "\uE74D" } };
                    remove.Click += (_, _) =>
                    {
                        _recentColors.Remove(capturedHex);
                        RebuildRecentColorsSwatches(activeHex);
                        ViewModel.StatusMessage = $"Removed #{capturedHex} from recent colours.";
                        // The menu hands focus back to the swatch it came from, which the rebuild
                        // removed: land on the swatch now in its place, else the colour picker.
                        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                            () => FocusRecentSwatchOrPicker(capturedIndex, how));
                    };
                    menu.Items.Add(remove);
                    KeyboardContextMenu.ShowAt(menu, btn, e);
                };
                btn.RightTapped += openMenu;
                // Shift+F10 / the Menu key open the same menu (RightTapped is pointer-only).
                KeyboardContextMenu.Bridge(btn, openMenu);
                RecentColorsSwatches.Children.Add(btn);
            }
            // The swatches live in the fill flyout's popup, outside the window's tracked tree.
            HoverPolish.Apply(RecentColorsSwatches);
        }

        private void FocusRecentSwatchOrPicker(int index, FocusState how)
        {
            if (RecentColorsSwatches is null || !FillFlyout.IsOpen) return;
            var swatches = RecentColorsSwatches.Children;
            if (swatches.Count > 0 && swatches[Math.Clamp(index, 0, swatches.Count - 1)] is Control swatch
                && swatch.Focus(how)) return;
            FillPicker.Focus(how);
        }

        private void OnClearRecentColorsClick(object sender, RoutedEventArgs e)
        {
            // Clearing collapses the "Recent colours" row, Clear link included: a keyboard press
            // would leave focus on nothing inside the flyout, so hand it to the colour picker.
            if (ClearRecentColorsBtn.FocusState != FocusState.Unfocused)
                FillPicker.Focus(ClearRecentColorsBtn.FocusState == FocusState.Pointer ? FocusState.Programmatic : FocusState.Keyboard);
            _recentColors.Clear();
            RebuildRecentColorsSwatches("");
            ViewModel.StatusMessage = "Cleared recent colours cache.";
        }

        private void RebuildFillFlyoutSwatches(string activeHex)
        {
            if (FillFlyoutSwatches is null) return;
            FillFlyoutSwatches.Children.Clear();
            foreach (string rawHex in ViewModel.GetPaletteColors())
            {
                string hex = rawHex.TrimStart('#').ToUpperInvariant();
                bool active = string.Equals(hex, activeHex, StringComparison.OrdinalIgnoreCase);
                var btn = new Button
                {
                    Width = 26,
                    Height = 22,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    Background = BrushFromHex(hex),
                    BorderThickness = new Thickness(active ? 2 : 1),
                };
                if (active)
                    btn.BorderBrush = new SolidColorBrush(AccentColor(0xFF));
                else
                    ThemeBrush.Set(btn, Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                ToolTipService.SetToolTip(btn, $"#{hex} ({ViewModel.SelectedPaletteName})");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"Apply palette colour #{hex}");
                string capturedHex = hex;
                btn.Click += (_, _) =>
                {
                    if (ViewModel.SelectedShape is { } s)
                    {
                        ViewModel.RecordUndo();
                        s.Fill = capturedHex;
                        AddRecentColor(capturedHex);
                        ViewModel.StatusMessage = $"{(s.IsLine ? "Line" : "Fill")} colour #{s.Fill}";
                        FillFlyout.Hide();
                    }
                };
                FillFlyoutSwatches.Children.Add(btn);
            }
            HoverPolish.Apply(FillFlyoutSwatches);
        }

        private void OnFillPickerApply(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedShape is { } shape)
            {
                var c = FillPicker.Color;
                ViewModel.RecordUndo();
                shape.Fill = $"{c.R:X2}{c.G:X2}{c.B:X2}";
                AddRecentColor(shape.Fill);
                ViewModel.StatusMessage = $"{(shape.IsLine ? "Line" : "Fill")} colour #{shape.Fill}";
            }
            FillFlyout.Hide();
        }

        private void OnFillSwatchButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (ViewModel.SelectedShape is not { } shape) return;
            ShowFillQuickPaletteMenu(sender as FrameworkElement ?? FillSwatchButton, shape, KeyboardContextMenu.PointIn(e, sender as UIElement));
        }

        private void FillFillHexBoxMenu(MenuFlyout flyout)
        {
            if (ViewModel.SelectedShape is { } shape) FillFillQuickPaletteMenu(flyout, shape);
        }

        private void ShowFillQuickPaletteMenu(FrameworkElement target, ShapeCanvasItemViewModel shape, Windows.Foundation.Point? position = null)
        {
            var flyout = new MenuFlyout();
            FillFillQuickPaletteMenu(flyout, shape);
            if (position.HasValue)
                flyout.ShowAt(target, position.Value);
            else
                flyout.ShowAt(target);
        }

        private void FillFillQuickPaletteMenu(MenuFlyout flyout, ShapeCanvasItemViewModel shape)
        {
            bool isLine = shape.IsLine;
            string kind = isLine ? "line" : "fill";
            string currentHex = (shape.Fill ?? (isLine ? "666666" : "0078D4")).TrimStart('#').ToUpperInvariant();

            flyout.Items.Add(ShapeMenuItem($"Copy #{currentHex}", "\uE8C8", () =>
            {
                CopyAndReport($"#{currentHex}", $"Copied #{currentHex} to clipboard.");
            }));

            flyout.Items.Add(ShapeMenuItem("Paste hex colour", "\uE77F", async () =>
            {
                try
                {
                    var dpv = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (dpv.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    {
                        var text = (await dpv.GetTextAsync())?.Trim().TrimStart('#');
                        if (text is { Length: 6 } && System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9a-fA-F]{6}$"))
                        {
                            ViewModel.RecordUndo();
                            shape.Fill = text.ToUpperInvariant();
                            ViewModel.StatusMessage = $"{(isLine ? "Line" : "Fill")} colour #{shape.Fill}";
                        }
                    }
                }
                catch { }
            }));

            flyout.Items.Add(ShapeMenuItem($"Pick {kind} colour…", "\uE790", () =>
            {
                FillFlyout.ShowAt(FillSwatchButton);
            }));

            flyout.Items.Add(new MenuFlyoutSeparator());

            var presetsSub = new MenuFlyoutSubItem
            {
                Text = "Quick colours",
                Icon = new FontIcon { Glyph = "\uE7B5" }
            };
            var presets = new (string Name, string Hex)[]
            {
                ("Accent Blue", "0078D4"),
                ("Deep Charcoal", "202020"),
                ("Pure White", "FFFFFF"),
                ("Forest Green", "107C41"),
                ("Brick Red", "D83B01"),
                ("Bright Gold", "FFB900"),
                ("Royal Purple", "5C2D91"),
                ("Slate Gray", "6B7280"),
            };
            foreach (var (name, phex) in presets)
            {
                presetsSub.Items.Add(ShapeMenuItem($"{name} (#{phex})", "\uF13E", () =>
                {
                    ViewModel.RecordUndo();
                    shape.Fill = phex;
                    ViewModel.StatusMessage = $"{(isLine ? "Line" : "Fill")} colour #{phex} ({name})";
                }));
            }
            flyout.Items.Add(presetsSub);

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Invert colour", "\uE7AC", () =>
            {
                try
                {
                    byte r = Convert.ToByte(currentHex[..2], 16);
                    byte g = Convert.ToByte(currentHex[2..4], 16);
                    byte b = Convert.ToByte(currentHex[4..6], 16);
                    string inv = $"{255 - r:X2}{255 - g:X2}{255 - b:X2}";
                    ViewModel.RecordUndo();
                    shape.Fill = inv;
                    ViewModel.StatusMessage = $"Inverted {(isLine ? "line" : "fill")} colour to #{inv}";
                }
                catch { }
            }));

            flyout.Items.Add(ShapeMenuItem(isLine ? "Reset to default stroke (#666666)" : "Reset to default fill (#0078D4)", "\uE777", () =>
            {
                ViewModel.RecordUndo();
                shape.Fill = isLine ? "666666" : "0078D4";
                ViewModel.StatusMessage = $"Reset {(isLine ? "line" : "fill")} colour";
            }));
        }

        private void OnResetRotationButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement target) return;
            var flyout = new MenuFlyout();

            flyout.Items.Add(ShapeMenuItem("Reset rotation (0°)", "\uE777", () => ViewModel.ResetRotation(), "Alt+0"));
            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Rotate 45° clockwise", "\uE7AD", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation + 45);
                    ViewModel.StatusMessage = $"Rotated to {s.Rotation:0}°";
                }
            }));

            flyout.Items.Add(ShapeMenuItem("Rotate 90° clockwise", "\uE7AD", () => ViewModel.RotateClockwiseCommand.Execute(null), "Alt+Shift+Right"));

            flyout.Items.Add(ShapeMenuItem("Rotate 180° (Flip around)", "\uE7AD", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation + 180);
                    ViewModel.StatusMessage = $"Rotated to {s.Rotation:0}°";
                }
            }));

            flyout.Items.Add(ShapeMenuItem("Rotate 90° counter-clockwise", "\uE80C", () => ViewModel.RotateCounterClockwiseCommand.Execute(null), "Alt+Shift+Left"));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Copy rotation angle", "\uE8C8", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    CopyAndReport($"{s.Rotation:0}°", $"Copied angle {s.Rotation:0}° to clipboard.");
                }
            }));

            flyout.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
        }

        private void OnRotationSliderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ViewModel.ResetRotation();
        }

        private void OnRotationReadoutButtonClick(object sender, RoutedEventArgs e) =>
            KeyboardContextMenu.Invoke(RotationReadoutButton, OnResetRotationButtonRightTapped);

        private void OnRotationSliderRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement target) return;
            OnResetRotationButtonRightTapped(target, e);
        }

        private void OnStrokeWeightDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is DependencyObject d && FindDescendant<TextBox>(d) is { } tb)
            {
                tb.SelectAll();
            }
        }

        private void FillStrokeWeightMenu(MenuFlyout flyout)
        {
            void SetWeight(double pt)
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.StrokeWidthPt = pt;
                    ViewModel.StatusMessage = $"Set line weight to {pt:0.##} pt";
                }
            }

            flyout.Items.Add(ShapeMenuItem("Hairline (0.5 pt)", "\uE8FD", () => SetWeight(0.5)));
            flyout.Items.Add(ShapeMenuItem("Thin (1.0 pt)", "\uE8FD", () => SetWeight(1.0)));
            flyout.Items.Add(ShapeMenuItem("Regular (1.5 pt)", "\uE8FD", () => SetWeight(1.5)));
            flyout.Items.Add(ShapeMenuItem("Medium (2.0 pt)", "\uE8FD", () => SetWeight(2.0)));
            flyout.Items.Add(ShapeMenuItem("Thick (3.0 pt)", "\uE8FD", () => SetWeight(3.0)));
            flyout.Items.Add(ShapeMenuItem("Extra Thick (4.5 pt)", "\uE8FD", () => SetWeight(4.5)));
            flyout.Items.Add(ShapeMenuItem("Heavy (6.0 pt)", "\uE8FD", () => SetWeight(6.0)));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Reset default (1.5 pt)", "\uE777", () => SetWeight(1.5)));
            flyout.Items.Add(ShapeMenuItem("Copy line weight", "\uE8C8", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    CopyAndReport($"{s.StrokeWidthPt:0.##} pt", $"Copied line weight {s.StrokeWidthPt:0.##} pt to clipboard.");
                }
            }));
        }

        private void OnNumberBoxDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is DependencyObject d && FindDescendant<TextBox>(d) is { } tb)
            {
                tb.SelectAll();
            }
        }

        private void FillPositionNumberBoxMenu(MenuFlyout flyout)
        {
            flyout.Items.Add(ShapeMenuItem("Align to left edge (X = 0)", "\uE8E4", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.X = 0;
                    ViewModel.StatusMessage = "Moved shape to left edge (X = 0)";
                }
            }));

            flyout.Items.Add(ShapeMenuItem("Align to top edge (Y = 0)", "\uE74A", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.Y = 0;
                    ViewModel.StatusMessage = "Moved shape to top edge (Y = 0)";
                }
            }));

            flyout.Items.Add(ShapeMenuItem("Center horizontally", "\uE8E3", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.X = Math.Max(0, (1200 - s.Width) / 2);
                    ViewModel.StatusMessage = $"Centered horizontally at X = {s.X:0}";
                }
            }));

            flyout.Items.Add(ShapeMenuItem("Center vertically", "\uE8E3", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.Y = Math.Max(0, (800 - s.Height) / 2);
                    ViewModel.StatusMessage = $"Centered vertically at Y = {s.Y:0}";
                }
            }));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Copy position", "\uE8C8", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    CopyAndReport($"X={s.X:0}, Y={s.Y:0}", $"Copied position (X={s.X:0}, Y={s.Y:0})");
                }
            }));
        }

        private void FillSizeNumberBoxMenu(MenuFlyout flyout)
        {
            void SetSize(double w, double h)
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.Width = Math.Max(4, w);
                    s.Height = Math.Max(4, h);
                    ViewModel.StatusMessage = $"Resized shape to {w:0} × {h:0} px";
                }
            }

            flyout.Items.Add(ShapeMenuItem("Make square (equal width & height)", "\uE737", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    var side = Math.Max(s.Width, s.Height);
                    SetSize(side, side);
                }
            }));

            flyout.Items.Add(ShapeMenuItem("Compact preset (120 × 80)", "\uE737", () => SetSize(120, 80)));
            flyout.Items.Add(ShapeMenuItem("Standard preset (160 × 100)", "\uE737", () => SetSize(160, 100)));
            flyout.Items.Add(ShapeMenuItem("Wide preset (220 × 100)", "\uE737", () => SetSize(220, 100)));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Reset default size (140 × 90)", "\uE777", () => SetSize(140, 90)));
            flyout.Items.Add(ShapeMenuItem("Copy dimensions", "\uE8C8", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    CopyAndReport($"W={s.Width:0}, H={s.Height:0}", $"Copied dimensions ({s.Width:0} × {s.Height:0})");
                }
            }));
        }

        private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                if (FindDescendant<T>(child) is { } found) return found;
            }
            return null;
        }

        private void OnAlignButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            bool canAlign = ViewModel.CanAlign;
            bool hasSelection = ViewModel.HasSelection;
            bool any = ViewModel.HasShapes;

            var horizSub = new MenuFlyoutSubItem
            {
                Text = "Horizontal alignment",
                Icon = new FontIcon { Glyph = "\uE8E3" }
            };
            horizSub.Items.Add(ShapeMenuItem("Align left", "\uE8E4", () => ViewModel.AlignLeftCommand.Execute(null), enabled: canAlign));
            horizSub.Items.Add(ShapeMenuItem("Align centre", "\uE8E3", () => ViewModel.AlignCenterCommand.Execute(null), enabled: canAlign));
            horizSub.Items.Add(ShapeMenuItem("Align right", "\uE8E2", () => ViewModel.AlignRightCommand.Execute(null), enabled: canAlign));
            horizSub.Items.Add(new MenuFlyoutSeparator());
            horizSub.Items.Add(ShapeMenuItem("Space left edges evenly", "\uE8AB", () => ViewModel.DistributeHorizontalCommand.Execute(null), enabled: canAlign));
            flyout.Items.Add(horizSub);

            var vertSub = new MenuFlyoutSubItem
            {
                Text = "Vertical alignment",
                Icon = new FontIcon { Glyph = "\uE8E3" }
            };
            vertSub.Items.Add(ShapeMenuItem("Align top", "\uE74A", () => ViewModel.AlignTopCommand.Execute(null), enabled: canAlign));
            vertSub.Items.Add(ShapeMenuItem("Align middle", "\uE8E3", () => ViewModel.AlignMiddleCommand.Execute(null), enabled: canAlign));
            vertSub.Items.Add(ShapeMenuItem("Align bottom", "\uE74B", () => ViewModel.AlignBottomCommand.Execute(null), enabled: canAlign));
            vertSub.Items.Add(new MenuFlyoutSeparator());
            vertSub.Items.Add(ShapeMenuItem("Space top edges evenly", "\uE8CB", () => ViewModel.DistributeVerticalCommand.Execute(null), enabled: canAlign));
            flyout.Items.Add(vertSub);

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Fit diagram in window", "\uE9A6", FitCanvasToContent, "Ctrl+0", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Duplicate selection", "\uE8C8", () => ViewModel.DuplicateSelectedCommand.Execute(null), "Ctrl+D", enabled: hasSelection));
            flyout.Items.Add(ShapeMenuItem("Delete selection", "\uE74D", () => ViewModel.RemoveSelectedCommand.Execute(null), "Del", enabled: hasSelection));

            var target = sender as FrameworkElement ?? ToolStrip;
            flyout.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
        }

        private void OnRotateButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            bool hasSelection = ViewModel.HasSelectedShape;

            flyout.Items.Add(ShapeMenuItem("Rotate 90° clockwise", "\uE7AD", () => ViewModel.RotateClockwiseCommand.Execute(null), "Alt+Shift+Right", enabled: hasSelection));
            flyout.Items.Add(ShapeMenuItem("Rotate 90° counter-clockwise", "\uE80C", () => ViewModel.RotateCounterClockwiseCommand.Execute(null), "Alt+Shift+Left", enabled: hasSelection));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Rotate 45° clockwise", "\uE7AD", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation + 45);
                    ViewModel.StatusMessage = $"Rotated to {s.Rotation:0}°";
                }
            }, enabled: hasSelection));

            flyout.Items.Add(ShapeMenuItem("Rotate 180° (Flip around)", "\uE7AD", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation + 180);
                    ViewModel.StatusMessage = $"Rotated to {s.Rotation:0}°";
                }
            }, enabled: hasSelection));

            flyout.Items.Add(ShapeMenuItem("Rotate 45° counter-clockwise", "\uE80C", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    ViewModel.RecordUndo();
                    s.Rotation = ShapeDesignStudioViewModel.WrapRotationAngle(s.Rotation - 45);
                    ViewModel.StatusMessage = $"Rotated to {s.Rotation:0}°";
                }
            }, enabled: hasSelection));

            flyout.Items.Add(ShapeMenuItem("Reset rotation (0°)", "\uE777", () => ViewModel.ResetRotation(), "Alt+0", enabled: hasSelection));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Copy rotation angle", "\uE8C8", () =>
            {
                if (ViewModel.SelectedShape is { } s)
                {
                    CopyAndReport($"{s.Rotation:0}°", $"Copied angle {s.Rotation:0}° to clipboard.");
                }
            }, enabled: hasSelection));

            var target = sender as FrameworkElement ?? ToolStrip;
            flyout.ShowAt(target, KeyboardContextMenu.PointIn(e, target));
        }

        // ---- export / markdown ----

        private void OnExportMenuOpening(object? sender, object e)
        {
            bool any = ViewModel.HasShapes;
            ExportDocxItem.IsEnabled = any;
            ExportDotxItem.IsEnabled = any;
            CopyMarkdownItem.IsEnabled = any;
        }

        private void OnExportDocxClick(object sender, RoutedEventArgs e) => _ = ExportAsync(template: false);

        private void OnExportDotxClick(object sender, RoutedEventArgs e) => _ = ExportAsync(template: true);

        /// <summary>Ask where to save, then export. The old export wrote a timestamped file straight
        /// to the Desktop with no choice of name or folder.</summary>
        private async System.Threading.Tasks.Task ExportAsync(bool template)
        {
            if (!ViewModel.HasShapes)
            {
                ViewModel.StatusMessage = "No shapes on the canvas to export: add a shape or pick a preset first.";
                return;
            }
            try
            {
                var path = await NativeFilePicker.PickSaveFileAsync(
                    this, template ? "Export the shapes as a Word template" : "Export the shapes as a Word document",
                    NativeFilePicker.Purpose.Exports,
                    ShapeDesignStudioViewModel.SuggestedExportName(template),
                    new[] { template ? Models.FileType.Of("Word template", ".dotx") : Models.FileType.Of("Word document", ".docx") },
                    okLabel: "Export");
                if (string.IsNullOrEmpty(path)) return;
                await ViewModel.ExportToWordAsync(template, path);
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Export failed: {ex.Message}";
            }
        }

        private async void OnCopyMarkdownClick(object sender, RoutedEventArgs e)
        {
            // Ctrl+C reaches this from anywhere on the canvas; an empty canvas used to copy an empty
            // block and report "Copied 0 shapes".
            if (ViewModel.Shapes.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes on the canvas to copy: add a shape or pick a preset first.";
                return;
            }
            try
            {
                // SnapshotComposed carries Text/TextColor too — the copy must round-trip the
                // exact same payload that export writes, or labels vanish on paste.
                var block = MarkSmith.Core.Composer.ShapeMarkdownCodec.Serialize(ViewModel.SnapshotComposed());
                if (await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(block) is string error)
                {
                    // Not kept: the shapes aren't anywhere else yet, so closing should still ask.
                    ViewModel.StatusMessage = $"The shapes weren't copied. {error}";
                    return;
                }
                ViewModel.MarkKept();
                ViewModel.StatusMessage = $"Copied {CountText.Of(ViewModel.Shapes.Count, "shape")} as a :::shapes Markdown block.";
                if (ExportDropDownButton is not null)
                {
                    MarkSmith.Services.CopyFeedback.ApplyButtonTick(ExportDropDownButton, null);
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Couldn't copy: {ex.Message}";
            }
        }

        private async void OnLoadMarkdownClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dpv = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dpv.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    // Await the clipboard — a sync .GetResult() here can deadlock the UI thread
                    // against the clipboard's own async completion.
                    var text = await dpv.GetTextAsync();
                    await ViewModel.LoadMarkdownAsync(text);
                    FitCanvasAfterLayout();
                }
                else
                {
                    ViewModel.StatusMessage = "The clipboard has no text — copy a :::shapes block first.";
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Couldn't load: {ex.Message}";
            }
        }

        // Clear is undoable now (Ctrl+Z), so it no longer interrupts with a confirmation dialog.
        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.HasShapes) return;
            int n = ViewModel.Shapes.Count;
            ViewModel.ClearAllCommand.Execute(null);
            ViewModel.StatusMessage = $"Cleared {CountText.Of(n, "shape")} · Ctrl+Z to undo";
        }

        // ---- presets ----

        private void OnPresetItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is DiagramPreset preset)
            {
                ViewModel.ApplyPreset(preset);
                FitCanvasAfterLayout();
            }
        }

        private void OnPresetCategoryComboKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.Down)
            {
                PresetList?.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                ViewModel.SelectedPresetCategory = "All Categories";
                e.Handled = true;
            }
        }

        private static int IndexOfCategory(IReadOnlyList<string> list, string? value)
        {
            if (value is null) return -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private void OnPresetCategoryComboDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var categories = ViewModel.PresetCategories;
            if (categories.Count == 0) return;
            int idx = IndexOfCategory(categories, ViewModel.SelectedPresetCategory);
            int next = (idx + 1) % categories.Count;
            ViewModel.SelectedPresetCategory = categories[next];
            ViewModel.StatusMessage = $"Preset category: {categories[next]} ({CountText.Of(ViewModel.FilteredPresets.Count, "preset")})";
        }

        private void OnPresetCategoryComboRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (PresetCategoryCombo is null) return;
            e.Handled = true;
            // Shift+F10 with the list open: close it first so the menu is not drawn over it.
            if (PresetCategoryCombo.IsDropDownOpen) PresetCategoryCombo.IsDropDownOpen = false;
            var flyout = new MenuFlyout();

            foreach (var category in ViewModel.PresetCategories)
            {
                var glyph = category switch
                {
                    "All Categories" => "\uE71D",
                    "Hierarchy & Structure" => "\uF003",
                    "Process & Workflow" => "\uE81E",
                    "Cycles & Loops" => "\uE777",
                    "Matrices & Strategy" => "\uE8D3",
                    "Relationships & Venns" => "\uE8AB",
                    "Roadmaps & Timelines" => "\uE787",
                    "Architecture & Cloud" => "\uE753",
                    "Funnels & Pipelines" => "\uE8E5",
                    "Lists & Dashboards" => "\uEA37",
                    _ => "\uF0E2"
                };
                bool isSelected = string.Equals(ViewModel.SelectedPresetCategory, category, StringComparison.OrdinalIgnoreCase);
                var item = ShapeMenuItem(isSelected ? $"{category} (active)" : category, glyph, () =>
                {
                    ViewModel.SelectedPresetCategory = category;
                });
                flyout.Items.Add(item);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            var nextItem = ShapeMenuItem("Next category", "\uE76C", () =>
            {
                var categories = ViewModel.PresetCategories;
                if (categories.Count == 0) return;
                int idx = IndexOfCategory(categories, ViewModel.SelectedPresetCategory);
                int next = (idx + 1) % categories.Count;
                ViewModel.SelectedPresetCategory = categories[next];
            }, "Ctrl+Tab");
            flyout.Items.Add(nextItem);

            var prevItem = ShapeMenuItem("Previous category", "\uE76B", () =>
            {
                var categories = ViewModel.PresetCategories;
                if (categories.Count == 0) return;
                int idx = IndexOfCategory(categories, ViewModel.SelectedPresetCategory);
                int prev = (idx - 1 + categories.Count) % categories.Count;
                ViewModel.SelectedPresetCategory = categories[prev];
            }, "Ctrl+Shift+Tab");
            flyout.Items.Add(prevItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var copyCatItem = ShapeMenuItem("Copy category name", "\uE8C8", () =>
            {
                CopyAndReport(ViewModel.SelectedPresetCategory ?? "All", $"Copied '{ViewModel.SelectedPresetCategory ?? "All"}' to clipboard");
            });
            flyout.Items.Add(copyCatItem);

            var focusListItem = ShapeMenuItem("Browse preset diagrams", "\uE8B3", () =>
            {
                PresetList?.Focus(FocusState.Programmatic);
            }, "Enter");
            flyout.Items.Add(focusListItem);

            var resetItem = ShapeMenuItem("Reset category to All", "\uE72C", () =>
            {
                ViewModel.SelectedPresetCategory = "All";
            }, "Esc");
            flyout.Items.Add(resetItem);

            flyout.ShowAt(PresetCategoryCombo, KeyboardContextMenu.PointIn(e, PresetCategoryCombo));
        }

        private void OnPresetListRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (PresetList is null) return;
            e.Handled = true;
            var flyout = new MenuFlyout();
            bool any = ViewModel.HasShapes;

            flyout.Items.Add(ShapeMenuItem("Apply first preset", "\uE710", () =>
            {
                var first = ViewModel.FilteredPresets.FirstOrDefault();
                if (first is not null)
                {
                    ViewModel.ApplyPreset(first);
                    FitCanvasAfterLayout();
                }
            }));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Select all shapes", "\uE8B3", () => ViewModel.SelectAllCommand.Execute(null), "Ctrl+A", enabled: any && !ViewModel.IsDense));
            flyout.Items.Add(ShapeMenuItem("Copy shapes as Markdown", "\uE8C8", () => OnCopyMarkdownClick(this, new RoutedEventArgs()), "Ctrl+Shift+C", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Insert into document", "\uE8A5", () => ViewModel.InsertIntoDocumentCommand.Execute(null), enabled: any));
            flyout.Items.Add(ShapeMenuItem("Clear canvas", "\uE74D", () => OnClearClick(this, new RoutedEventArgs()), enabled: any));

            flyout.ShowAt(PresetList, KeyboardContextMenu.PointIn(e, PresetList));
        }

        private void OnPaletteGridDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (PaletteGrid is null) return;
            string currentTool = (PaletteGrid.SelectedItem as string) ?? ViewModel.ArmedTool ?? "roundrect";
            e.Handled = true;

            double cx = 220, cy = 160;
            if (ViewModel.SelectedShape is { } sel)
            {
                cx = sel.X + sel.Width + 20;
                cy = sel.Y;
            }
            ViewModel.AddShapeAt(currentTool, cx, cy);
            ViewModel.StatusMessage = $"Inserted '{currentTool}' shape at ({cx:F0}, {cy:F0}) · Drag to position";
        }

        private void OnPaletteGridRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (PaletteGrid is null) return;
            e.Handled = true;
            var flyout = new MenuFlyout();
            bool any = ViewModel.HasShapes;

            string currentTool = ViewModel.ArmedTool ?? "roundrect";
            flyout.Items.Add(ShapeMenuItem($"Arm '{currentTool}' to draw", "\uE790", () =>
            {
                ViewModel.ArmedTool = currentTool;
                ViewModel.StatusMessage = $"Armed '{currentTool}'. Click canvas to drop or drag to draw.";
            }));

            flyout.Items.Add(ShapeMenuItem($"Insert '{currentTool}' at center", "\uE710", () =>
            {
                ViewModel.AddShapeAt(currentTool, 220, 160);
                ViewModel.StatusMessage = $"Inserted '{currentTool}' shape.";
            }));

            var insertPresetSubmenu = new MenuFlyoutSubItem
            {
                Text = $"Insert '{currentTool}' with preset dimensions",
                Icon = new FontIcon { Glyph = "\uE790" }
            };
            insertPresetSubmenu.Items.Add(ShapeMenuItem("Standard (120 × 80)", "\uE710", () =>
            {
                ViewModel.AddShapeAt(currentTool, 220, 160, 120, 80);
                ViewModel.StatusMessage = $"Inserted standard 120×80 '{currentTool}'.";
            }));
            insertPresetSubmenu.Items.Add(ShapeMenuItem("Square (100 × 100)", "\uE710", () =>
            {
                ViewModel.AddShapeAt(currentTool, 220, 160, 100, 100);
                ViewModel.StatusMessage = $"Inserted square 100×100 '{currentTool}'.";
            }));
            insertPresetSubmenu.Items.Add(ShapeMenuItem("Wide Card (200 × 80)", "\uE710", () =>
            {
                ViewModel.AddShapeAt(currentTool, 220, 160, 200, 80);
                ViewModel.StatusMessage = $"Inserted wide 200×80 '{currentTool}'.";
            }));
            insertPresetSubmenu.Items.Add(ShapeMenuItem("Compact (80 × 50)", "\uE710", () =>
            {
                ViewModel.AddShapeAt(currentTool, 220, 160, 80, 50);
                ViewModel.StatusMessage = $"Inserted compact 80×50 '{currentTool}'.";
            }));
            insertPresetSubmenu.Items.Add(ShapeMenuItem("Banner (300 × 60)", "\uE710", () =>
            {
                ViewModel.AddShapeAt(currentTool, 220, 160, 300, 60);
                ViewModel.StatusMessage = $"Inserted banner 300×60 '{currentTool}'.";
            }));
            flyout.Items.Add(insertPresetSubmenu);

            if (ViewModel.IsPlacing)
            {
                flyout.Items.Add(ShapeMenuItem("Disarm tool (Select mode)", "\uE894", () =>
                {
                    ViewModel.ArmedTool = null;
                    ViewModel.StatusMessage = "Selection mode active.";
                }, "Esc"));
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Select all shapes", "\uE8B3", () => ViewModel.SelectAllCommand.Execute(null), "Ctrl+A", enabled: any && !ViewModel.IsDense));
            flyout.Items.Add(ShapeMenuItem("Copy shapes as Markdown", "\uE8C8", () => OnCopyMarkdownClick(this, new RoutedEventArgs()), "Ctrl+Shift+C", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Fit diagram in window", "\uE9A6", FitCanvasToContent, "Ctrl+0", enabled: any));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Switch to Presets tab", "\uF0E2", () =>
            {
                if (LeftTabs != null && PresetsTab != null) LeftTabs.SelectedItem = PresetsTab;
            }, "Ctrl+1"));

            flyout.Items.Add(ShapeMenuItem("Switch to Picture to vector tab", "\uEB9F", () =>
            {
                if (LeftTabs != null && PictureTab != null) LeftTabs.SelectedItem = PictureTab;
            }, "Ctrl+3"));

            flyout.ShowAt(PaletteGrid, KeyboardContextMenu.PointIn(e, PaletteGrid));
        }

        // ---- picture → vector ----

        private string? _composeImagePath;

        private void OnStrokeWidthChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (StrokeWidthReadout != null) StrokeWidthReadout.Text = e.NewValue.ToString("0.0");
        }

        private async void OnPickImageClick(object sender, RoutedEventArgs e)
        {
            var path = await NativeFilePicker.PickOpenFileAsync(
                this, "Choose a picture to turn into shapes", NativeFilePicker.Purpose.Images,
                new[] { Models.FileType.Of("Images", ".png", ".jpg", ".jpeg", ".bmp", ".gif") },
                okLabel: "Use this picture", start: StartFolder.Pictures);
            if (string.IsNullOrEmpty(path)) return;
            UseImagePath(path);
        }

        // Smart tiles picks from the full set by picture content, so the manual
        // tile-shape boxes only apply while it is off — grey them out to say so.
        private void OnCompAdaptiveChanged(object sender, RoutedEventArgs e)
        {
            var cb = (sender as CheckBox) ?? CompAdaptive;
            if (cb is null) return;
            if (CompRoundRect is null || CompRect is null || CompEllipse is null || CompChevron is null
                || CompDiamond is null || CompHexagon is null || CompTriangle is null || CompCloud is null)
                return;
            bool manual = cb.IsChecked != true;
            CompRoundRect.IsEnabled = manual;
            CompRect.IsEnabled = manual;
            CompEllipse.IsEnabled = manual;
            CompChevron.IsEnabled = manual;
            CompDiamond.IsEnabled = manual;
            CompHexagon.IsEnabled = manual;
            CompTriangle.IsEnabled = manual;
            CompCloud.IsEnabled = manual;
        }

                // Escape hatch for a hanging shell dialog (cloud folder / shell extension):
        // paste the file path instead of browsing to it.
        private void OnUseImagePathClick(object sender, RoutedEventArgs e)
        {
            var path = (FuseImagePathBox.Text ?? "").Trim().Trim('"');
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                ViewModel.StatusMessage = "That path doesn't point to a file. Copy it from the Explorer address bar, including the file name.";
                return;
            }
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
            {
                ViewModel.StatusMessage = $"“{ext}” isn't a supported picture (png, jpg, bmp, gif).";
                return;
            }
            UseImagePath(path);
        }

        private void UseImagePath(string path)
        {
            _composeImagePath = path;
            FuseImageLabel.Text = System.IO.Path.GetFileName(path);
            ToolTipService.SetToolTip(FuseImageLabel, path);
            PickImageButton.Content = "Change…";
            ViewModel.HasImage = true;
            try
            {
                FuseThumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
            }
            catch { }
            ViewModel.StatusMessage = $"Picture ready: {System.IO.Path.GetFileName(path)} — adjust the layers, then Convert.";
        }

        private void OnImageCardRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();

            flyout.Items.Add(ShapeMenuItem("Choose image file…", "\uE8B7", () => OnPickImageClick(this, new RoutedEventArgs())));

            flyout.Items.Add(ShapeMenuItem("Paste image path from clipboard", "\uE77F", async () =>
            {
                try
                {
                    var pkg = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (pkg.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    {
                        var text = await pkg.GetTextAsync();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            FuseImagePathBox.Text = text.Trim('\"', ' ');
                            OnUseImagePathClick(this, new RoutedEventArgs());
                        }
                    }
                }
                catch { }
            }));

            flyout.Items.Add(ShapeMenuItem("Copy image path", "\uE8C8", () =>
            {
                var path = FuseImagePathBox?.Text ?? _composeImagePath ?? "";
                if (!string.IsNullOrEmpty(path))
                {
                    CopyAndReport(path, "Copied image path to clipboard.");
                }
            }, enabled: !string.IsNullOrEmpty(FuseImagePathBox?.Text) || !string.IsNullOrEmpty(_composeImagePath)));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Trace & vectorize image", "\uEB9F", () => OnFuseImageClick(this, new RoutedEventArgs()), enabled: ViewModel.HasImage));

            flyout.Items.Add(ShapeMenuItem("Reset tracing sliders to defaults", "\uE777", () =>
            {
                FuseMosaicDensity.Value = 96;
                FuseMosaicPadding.Value = -6;
                FuseLineDensity.Value = 120;
                ViewModel.StatusMessage = "Reset mosaic and line sliders to default density.";
            }));

            flyout.Items.Add(ShapeMenuItem("Clear image", "\uE74D", () =>
            {
                _composeImagePath = null;
                FuseThumb.Source = null;
                FuseImageLabel.Text = "No image chosen";
                ToolTipService.SetToolTip(FuseImageLabel, null);
                PickImageButton.Content = "Choose image…";
                if (FuseImagePathBox != null) FuseImagePathBox.Text = string.Empty;
                ViewModel.HasImage = false;
                ViewModel.StatusMessage = "Cleared chosen image.";
            }, enabled: ViewModel.HasImage));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Switch to Presets tab", "\uF0E2", () =>
            {
                if (LeftTabs != null && PresetsTab != null) LeftTabs.SelectedItem = PresetsTab;
            }, "Ctrl+1"));

            flyout.Items.Add(ShapeMenuItem("Switch to Shapes library", "\uE790", () =>
            {
                if (LeftTabs != null && ShapesTab != null) LeftTabs.SelectedItem = ShapesTab;
            }, "Ctrl+2"));

            if (sender is FrameworkElement fe)
                flyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else if (ImageCardBorder is not null)
                flyout.ShowAt(ImageCardBorder, KeyboardContextMenu.PointIn(e, ImageCardBorder));
            else
                flyout.ShowAt(this.Content, KeyboardContextMenu.PointIn(e, this.Content));
        }

        private void OnInsertIntoDocumentButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (InsertIntoDocumentButton is null) return;
            e.Handled = true;
            var flyout = new MenuFlyout();
            bool any = ViewModel.Shapes.Count > 0;

            flyout.Items.Add(ShapeMenuItem("Insert into document", "\uE8A5", () => ViewModel.InsertIntoDocumentCommand.Execute(null), "Ctrl+Enter", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Copy shapes as Markdown", "\uE8C8", () => OnCopyMarkdownClick(this, new RoutedEventArgs()), "Ctrl+Shift+C", enabled: any));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Select all shapes", "\uE8B3", () => ViewModel.SelectAllCommand.Execute(null), "Ctrl+A", enabled: any && !ViewModel.IsDense));
            flyout.Items.Add(ShapeMenuItem("Fit diagram in window", "\uE9A6", FitCanvasToContent, "Ctrl+0", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Clear canvas", "\uE74D", () => OnClearClick(this, new RoutedEventArgs()), enabled: any));

            if (sender is FrameworkElement fe)
                flyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else
                flyout.ShowAt(InsertIntoDocumentButton, KeyboardContextMenu.PointIn(e, InsertIntoDocumentButton));
        }

        private void OnConvertButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (ConvertButton is null) return;
            e.Handled = true;
            var flyout = new MenuFlyout();

            flyout.Items.Add(ShapeMenuItem("Convert picture to shapes", "\uE945", () => OnFuseImageClick(this, new RoutedEventArgs()), enabled: ViewModel.HasImage));
            flyout.Items.Add(ShapeMenuItem("Choose image file…", "\uE8B7", () => OnPickImageClick(this, new RoutedEventArgs())));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Reset tracing sliders to defaults", "\uE777", () =>
            {
                FuseMosaicDensity.Value = 96;
                FuseMosaicPadding.Value = -6;
                FuseLineDensity.Value = 120;
                ViewModel.StatusMessage = "Reset mosaic and line sliders to default density.";
            }));

            flyout.Items.Add(ShapeMenuItem("Clear image", "\uE74D", () =>
            {
                _composeImagePath = null;
                FuseThumb.Source = null;
                FuseImageLabel.Text = "No image chosen";
                ToolTipService.SetToolTip(FuseImageLabel, null);
                PickImageButton.Content = "Choose image…";
                if (FuseImagePathBox != null) FuseImagePathBox.Text = string.Empty;
                ViewModel.HasImage = false;
                ViewModel.StatusMessage = "Cleared chosen image.";
            }, enabled: ViewModel.HasImage));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Switch to Shapes library", "\uE790", () =>
            {
                if (LeftTabs != null && ShapesTab != null) LeftTabs.SelectedItem = ShapesTab;
            }, "Ctrl+2"));

            flyout.Items.Add(ShapeMenuItem("Switch to Presets tab", "\uF0E2", () =>
            {
                if (LeftTabs != null && PresetsTab != null) LeftTabs.SelectedItem = PresetsTab;
            }, "Ctrl+1"));

            if (sender is FrameworkElement fe)
                flyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else
                flyout.ShowAt(ConvertButton, KeyboardContextMenu.PointIn(e, ConvertButton));
        }

        private void OnClearCanvasButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            bool any = ViewModel.Shapes.Count > 0;

            flyout.Items.Add(ShapeMenuItem("Clear canvas", "\uE74D", () => OnClearClick(this, new RoutedEventArgs()), enabled: any));
            flyout.Items.Add(ShapeMenuItem("Undo clear", "\uE7A7", () => ViewModel.UndoCommand.Execute(null), "Ctrl+Z", enabled: ViewModel.CanUndo));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Select all shapes", "\uE8B3", () => ViewModel.SelectAllCommand.Execute(null), "Ctrl+A", enabled: any && !ViewModel.IsDense));
            flyout.Items.Add(ShapeMenuItem("Fit diagram in window", "\uE9A6", FitCanvasToContent, "Ctrl+0", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Copy shapes as Markdown", "\uE8C8", () => OnCopyMarkdownClick(this, new RoutedEventArgs()), "Ctrl+Shift+C", enabled: any));

            var anchor = sender as FrameworkElement ?? (FrameworkElement)ClearCanvasButton;
            flyout.ShowAt(anchor, KeyboardContextMenu.PointIn(e, anchor));
        }

        private void OnExportDropDownButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            bool any = ViewModel.Shapes.Count > 0;

            flyout.Items.Add(ShapeMenuItem("Word document (.docx)…", "\uE8A5", () => OnExportDocxClick(this, new RoutedEventArgs()), enabled: any));
            flyout.Items.Add(ShapeMenuItem("Word template (.dotx)…", "\uE8F1", () => OnExportDotxClick(this, new RoutedEventArgs()), enabled: any));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Copy as Markdown", "\uE8C8", () => OnCopyMarkdownClick(this, new RoutedEventArgs()), "Ctrl+Shift+C", enabled: any));
            flyout.Items.Add(ShapeMenuItem("Load Markdown from clipboard", "\uE77F", () => OnLoadMarkdownClick(this, new RoutedEventArgs()), "Ctrl+V"));

            var anchor = sender as FrameworkElement ?? (FrameworkElement)ExportDropDownButton;
            flyout.ShowAt(anchor, KeyboardContextMenu.PointIn(e, anchor));
        }

        private async void OnFuseImageClick(object sender, RoutedEventArgs e)        {
            if (string.IsNullOrWhiteSpace(_composeImagePath))
            {
                ViewModel.StatusMessage = "Choose a picture first.";
                return;
            }

            int mosaicDensity = (int)FuseMosaicDensity.Value;
            int lineDensity = (int)FuseLineDensity.Value;

            if (mosaicDensity <= 0 && lineDensity <= 0)
            {
                ViewModel.StatusMessage = "Both densities are 0 — raise the shape mosaic or the line art density.";
                return;
            }

            var shapes = new List<string>();
            bool adaptive = CompAdaptive.IsChecked == true;
            if (adaptive)
            {
                // Smart tiles choose from the full set by picture content; the boxes below
                // only apply when Smart tiles is off.
                shapes.Add("auto");
            }
            else
            {
            if (CompRoundRect.IsChecked == true) shapes.Add("roundrect");
            if (CompRect.IsChecked == true) shapes.Add("rect");
            if (CompEllipse.IsChecked == true) shapes.Add("ellipse");
            if (CompChevron.IsChecked == true) shapes.Add("chevron");
            if (CompDiamond.IsChecked == true) shapes.Add("diamond");
            if (CompHexagon.IsChecked == true) shapes.Add("hexagon");
            if (CompTriangle.IsChecked == true) shapes.Add("triangle");
            if (CompCloud.IsChecked == true) shapes.Add("cloud");
            }

            var lineMode = (FuseModePicker.SelectedItem as ComboBoxItem)?.Tag is string tag &&
                           Enum.TryParse<MarkSmith.Core.Composer.LineTraceMode>(tag, out var parsed)
                ? parsed
                : MarkSmith.Core.Composer.LineTraceMode.CrossHatch;

            await ViewModel.ComposeHybridFusionAsync(
                _composeImagePath,
                mosaicDensity,
                lineDensity,
                shapes,
                lineMode,
                FuseMonochrome.IsChecked ?? true,
                (int)FuseEdgeSensitivity.Value,
                FuseStrokeWidth.Value,
                FuseMosaicPadding.Value,
                CompAdaptive.IsChecked == true);
            FitCanvasAfterLayout();
        }

        private void OnFuseImagePathBoxDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            FuseImagePathBox?.SelectAll();
        }

        private void FillFuseImagePathBoxMenu(MenuFlyout flyout)
        {
            if (FuseImagePathBox is null) return;

            flyout.Items.Add(ShapeMenuItem("Paste image path from clipboard", "\uE77F", async () =>
            {
                try
                {
                    var pkg = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (pkg.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    {
                        var text = await pkg.GetTextAsync();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            FuseImagePathBox.Text = text.Trim('\"', ' ');
                            OnUseImagePathClick(this, new RoutedEventArgs());
                        }
                    }
                }
                catch { }
            }));

            flyout.Items.Add(ShapeMenuItem("Choose image file…", "\uE8B7", () => OnPickImageClick(this, new RoutedEventArgs())));
            flyout.Items.Add(ShapeMenuItem("Select all text", "\uE8D2", () => FuseImagePathBox.SelectAll(), enabled: !string.IsNullOrEmpty(FuseImagePathBox.Text)));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Copy image path", "\uE8C8", () =>
            {
                if (!string.IsNullOrEmpty(FuseImagePathBox.Text))
                {
                    CopyAndReport(FuseImagePathBox.Text, "Copied image path to clipboard.");
                }
            }, enabled: !string.IsNullOrEmpty(FuseImagePathBox.Text)));

            flyout.Items.Add(ShapeMenuItem("Clear image path", "\uE894", () =>
            {
                FuseImagePathBox.Text = string.Empty;
                ViewModel.StatusMessage = "Cleared image path.";
            }, enabled: !string.IsNullOrEmpty(FuseImagePathBox.Text)));
        }

        private void OnFuseMosaicDensityDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            FuseMosaicDensity.Value = 96;
            ViewModel.StatusMessage = "Reset mosaic density to default 96.";
        }

        private void OnFuseMosaicDensityRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            flyout.Items.Add(ShapeMenuItem("Reset to default (96)", "\uE777", () => { FuseMosaicDensity.Value = 96; ViewModel.StatusMessage = "Set mosaic density to 96."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Minimal mosaic (24)", "\uE80A", () => { FuseMosaicDensity.Value = 24; ViewModel.StatusMessage = "Set mosaic density to 24."; }));
            flyout.Items.Add(ShapeMenuItem("Low density (48)", "\uE80A", () => { FuseMosaicDensity.Value = 48; ViewModel.StatusMessage = "Set mosaic density to 48."; }));
            flyout.Items.Add(ShapeMenuItem("Medium density (72)", "\uE80A", () => { FuseMosaicDensity.Value = 72; ViewModel.StatusMessage = "Set mosaic density to 72."; }));
            flyout.Items.Add(ShapeMenuItem("High density (96 - default)", "\uE80A", () => { FuseMosaicDensity.Value = 96; ViewModel.StatusMessage = "Set mosaic density to 96."; }));
            flyout.Items.Add(ShapeMenuItem("Maximum density (120)", "\uE80A", () => { FuseMosaicDensity.Value = 120; ViewModel.StatusMessage = "Set mosaic density to 120."; }));
            flyout.Items.Add(ShapeMenuItem("Skip mosaic layer (0)", "\uE711", () => { FuseMosaicDensity.Value = 0; ViewModel.StatusMessage = "Disabled shape mosaic layer (density 0)."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Copy density value", "\uE8C8", () =>
            {
                CopyAndReport(FuseMosaicDensity.Value.ToString("F0"), $"Copied mosaic density {FuseMosaicDensity.Value:F0} to clipboard.");
            }));
            if (sender is FrameworkElement fe) flyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else flyout.ShowAt(FuseMosaicDensity, KeyboardContextMenu.PointIn(e, FuseMosaicDensity));
        }

        private void OnFuseMosaicPaddingDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            FuseMosaicPadding.Value = -6;
            ViewModel.StatusMessage = "Reset tile spacing to default -6.";
        }

        private void OnFuseMosaicPaddingRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            flyout.Items.Add(ShapeMenuItem("Reset to default (-6)", "\uE777", () => { FuseMosaicPadding.Value = -6; ViewModel.StatusMessage = "Set tile spacing to -6."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Maximum overlap (-20)", "\uE80A", () => { FuseMosaicPadding.Value = -20; ViewModel.StatusMessage = "Set tile spacing to -20."; }));
            flyout.Items.Add(ShapeMenuItem("Strong overlap (-10)", "\uE80A", () => { FuseMosaicPadding.Value = -10; ViewModel.StatusMessage = "Set tile spacing to -10."; }));
            flyout.Items.Add(ShapeMenuItem("Default tight overlap (-6)", "\uE80A", () => { FuseMosaicPadding.Value = -6; ViewModel.StatusMessage = "Set tile spacing to -6."; }));
            flyout.Items.Add(ShapeMenuItem("Zero gap / touching (0)", "\uE80A", () => { FuseMosaicPadding.Value = 0; ViewModel.StatusMessage = "Set tile spacing to 0."; }));
            flyout.Items.Add(ShapeMenuItem("Small gaps (+5)", "\uE80A", () => { FuseMosaicPadding.Value = 5; ViewModel.StatusMessage = "Set tile spacing to +5."; }));
            flyout.Items.Add(ShapeMenuItem("Medium gaps (+10)", "\uE80A", () => { FuseMosaicPadding.Value = 10; ViewModel.StatusMessage = "Set tile spacing to +10."; }));
            flyout.Items.Add(ShapeMenuItem("Wide gaps (+20)", "\uE80A", () => { FuseMosaicPadding.Value = 20; ViewModel.StatusMessage = "Set tile spacing to +20."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Copy tile spacing value", "\uE8C8", () =>
            {
                CopyAndReport(FuseMosaicPadding.Value.ToString("F0"), $"Copied tile spacing {FuseMosaicPadding.Value:F0} to clipboard.");
            }));
            if (sender is FrameworkElement fe) flyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else flyout.ShowAt(FuseMosaicPadding, KeyboardContextMenu.PointIn(e, FuseMosaicPadding));
        }

        private void OnFuseLineDensityDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            FuseLineDensity.Value = 120;
            ViewModel.StatusMessage = "Reset line density to default 120.";
        }

        private void OnFuseLineDensityRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            flyout.Items.Add(ShapeMenuItem("Reset to default (120)", "\uE777", () => { FuseLineDensity.Value = 120; ViewModel.StatusMessage = "Set line density to 120."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Light tracing (60)", "\uE80A", () => { FuseLineDensity.Value = 60; ViewModel.StatusMessage = "Set line density to 60."; }));
            flyout.Items.Add(ShapeMenuItem("Standard line art (120 - default)", "\uE80A", () => { FuseLineDensity.Value = 120; ViewModel.StatusMessage = "Set line density to 120."; }));
            flyout.Items.Add(ShapeMenuItem("Dense contours (240)", "\uE80A", () => { FuseLineDensity.Value = 240; ViewModel.StatusMessage = "Set line density to 240."; }));
            flyout.Items.Add(ShapeMenuItem("Fine etching (600)", "\uE80A", () => { FuseLineDensity.Value = 600; ViewModel.StatusMessage = "Set line density to 600."; }));
            flyout.Items.Add(ShapeMenuItem("Ultra detailed (1200)", "\uE80A", () => { FuseLineDensity.Value = 1200; ViewModel.StatusMessage = "Set line density to 1200."; }));
            flyout.Items.Add(ShapeMenuItem("Skip line-art layer (0)", "\uE711", () => { FuseLineDensity.Value = 0; ViewModel.StatusMessage = "Disabled line-art layer (density 0)."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Copy line density value", "\uE8C8", () =>
            {
                CopyAndReport(FuseLineDensity.Value.ToString("F0"), $"Copied line density {FuseLineDensity.Value:F0} to clipboard.");
            }));
            if (sender is FrameworkElement fe) flyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else flyout.ShowAt(FuseLineDensity, KeyboardContextMenu.PointIn(e, FuseLineDensity));
        }

        private void OnFuseEdgeSensitivityDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            FuseEdgeSensitivity.Value = 30;
            ViewModel.StatusMessage = "Reset detail threshold to default 30.";
        }

        private void OnFuseEdgeSensitivityRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            flyout.Items.Add(ShapeMenuItem("Reset to default (30)", "\uE777", () => { FuseEdgeSensitivity.Value = 30; ViewModel.StatusMessage = "Set detail threshold to 30."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Ultra fine textures (10)", "\uE80A", () => { FuseEdgeSensitivity.Value = 10; ViewModel.StatusMessage = "Set detail threshold to 10."; }));
            flyout.Items.Add(ShapeMenuItem("Balanced detail (20)", "\uE80A", () => { FuseEdgeSensitivity.Value = 20; ViewModel.StatusMessage = "Set detail threshold to 20."; }));
            flyout.Items.Add(ShapeMenuItem("Standard outlines (30 - default)", "\uE80A", () => { FuseEdgeSensitivity.Value = 30; ViewModel.StatusMessage = "Set detail threshold to 30."; }));
            flyout.Items.Add(ShapeMenuItem("Major contours (50)", "\uE80A", () => { FuseEdgeSensitivity.Value = 50; ViewModel.StatusMessage = "Set detail threshold to 50."; }));
            flyout.Items.Add(ShapeMenuItem("Bold boundaries only (80)", "\uE80A", () => { FuseEdgeSensitivity.Value = 80; ViewModel.StatusMessage = "Set detail threshold to 80."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Copy detail threshold", "\uE8C8", () =>
            {
                CopyAndReport(FuseEdgeSensitivity.Value.ToString("F0"), $"Copied detail threshold {FuseEdgeSensitivity.Value:F0} to clipboard.");
            }));
            if (sender is FrameworkElement fe) flyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else flyout.ShowAt(FuseEdgeSensitivity, KeyboardContextMenu.PointIn(e, FuseEdgeSensitivity));
        }

        private void OnFuseStrokeWidthDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            FuseStrokeWidth.Value = 1.5;
            ViewModel.StatusMessage = "Reset stroke width to default 1.5 pt.";
        }

        private void OnFuseStrokeWidthRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            flyout.Items.Add(ShapeMenuItem("Reset to default (1.5 pt)", "\uE777", () => { FuseStrokeWidth.Value = 1.5; ViewModel.StatusMessage = "Set stroke width to 1.5 pt."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Hairline (0.5 pt)", "\uE80A", () => { FuseStrokeWidth.Value = 0.5; ViewModel.StatusMessage = "Set stroke width to 0.5 pt."; }));
            flyout.Items.Add(ShapeMenuItem("Fine (1.0 pt)", "\uE80A", () => { FuseStrokeWidth.Value = 1.0; ViewModel.StatusMessage = "Set stroke width to 1.0 pt."; }));
            flyout.Items.Add(ShapeMenuItem("Medium (1.5 pt - default)", "\uE80A", () => { FuseStrokeWidth.Value = 1.5; ViewModel.StatusMessage = "Set stroke width to 1.5 pt."; }));
            flyout.Items.Add(ShapeMenuItem("Heavy (2.0 pt)", "\uE80A", () => { FuseStrokeWidth.Value = 2.0; ViewModel.StatusMessage = "Set stroke width to 2.0 pt."; }));
            flyout.Items.Add(ShapeMenuItem("Bold (3.0 pt)", "\uE80A", () => { FuseStrokeWidth.Value = 3.0; ViewModel.StatusMessage = "Set stroke width to 3.0 pt."; }));
            flyout.Items.Add(ShapeMenuItem("Poster (5.0 pt)", "\uE80A", () => { FuseStrokeWidth.Value = 5.0; ViewModel.StatusMessage = "Set stroke width to 5.0 pt."; }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(ShapeMenuItem("Copy stroke width", "\uE8C8", () =>
            {
                CopyAndReport($"{FuseStrokeWidth.Value:F1} pt", $"Copied stroke width {FuseStrokeWidth.Value:F1} pt to clipboard.");
            }));
            if (sender is FrameworkElement fe) flyout.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
            else flyout.ShowAt(FuseStrokeWidth, KeyboardContextMenu.PointIn(e, FuseStrokeWidth));
        }

        private void OnCanvasEmptyStateDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ViewModel.AddShapeAt("roundrect", 220, 160);
            ViewModel.StatusMessage = "Added starter rounded rectangle to canvas.";
        }

        private void OnCanvasEmptyStateRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var flyout = new MenuFlyout();

            flyout.Items.Add(ShapeMenuItem("Add starter rectangle", "\uE735", () =>
            {
                ViewModel.AddShapeAt("rect", 220, 160);
                ViewModel.StatusMessage = "Added starter rectangle to canvas.";
            }));

            flyout.Items.Add(ShapeMenuItem("Add starter rounded rectangle", "\uE735", () =>
            {
                ViewModel.AddShapeAt("roundrect", 220, 160);
                ViewModel.StatusMessage = "Added starter rounded rectangle to canvas.";
            }));

            flyout.Items.Add(ShapeMenuItem("Add starter circle", "\uE707", () =>
            {
                ViewModel.AddShapeAt("ellipse", 220, 160);
                ViewModel.StatusMessage = "Added starter circle to canvas.";
            }));

            flyout.Items.Add(ShapeMenuItem("Add starter decision diamond", "\uF0E2", () =>
            {
                ViewModel.AddShapeAt("diamond", 220, 160);
                ViewModel.StatusMessage = "Added starter diamond to canvas.";
            }));

            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(ShapeMenuItem("Browse preset diagrams…", "\uF0E2", () =>
            {
                if (LeftTabs != null && PresetsTab != null) LeftTabs.SelectedItem = PresetsTab;
            }, "Ctrl+1"));

            flyout.Items.Add(ShapeMenuItem("Convert picture to shapes…", "\uEB9F", () =>
            {
                if (LeftTabs != null && PictureTab != null) LeftTabs.SelectedItem = PictureTab;
            }, "Ctrl+3"));

            flyout.Items.Add(ShapeMenuItem("Paste :::shapes block from clipboard", "\uE77F", () => OnLoadMarkdownClick(this, new RoutedEventArgs()), "Ctrl+V"));

            if (sender is FrameworkElement fe) flyout.ShowAt(fe, e.GetPosition(fe));
            else if (CanvasEmptyStatePanel is not null) flyout.ShowAt(CanvasEmptyStatePanel, e.GetPosition(CanvasEmptyStatePanel));
            else flyout.ShowAt(this.Content, e.GetPosition(this.Content));
        }

        private void UpdateSelectionBoundsPill()
        {
            if (SelectionBoundsText is null) return;
            var count = ViewModel.SelectionCount;
            if (count == 0)
            {
                var total = ViewModel.Shapes.Count;
                SelectionBoundsText.Text = $"Shapes: {total} · Canvas 2200×1800";
            }
            else if (count == 1 && ViewModel.SelectedShape is { } s)
            {
                int w = (int)Math.Round(s.Width);
                int h = (int)Math.Round(s.Height);
                int x = (int)Math.Round(s.X);
                int y = (int)Math.Round(s.Y);
                SelectionBoundsText.Text = $"1 shape · {w}×{h} at ({x},{y})";
            }
            else
            {
                var sel = ViewModel.Shapes.Where(sh => sh.IsSelected).ToList();
                if (sel.Count > 0)
                {
                    double minX = sel.Min(sh => sh.X);
                    double minY = sel.Min(sh => sh.Y);
                    double maxX = sel.Max(sh => sh.X + sh.Width);
                    double maxY = sel.Max(sh => sh.Y + sh.Height);
                    int bw = (int)Math.Round(maxX - minX);
                    int bh = (int)Math.Round(maxY - minY);
                    SelectionBoundsText.Text = $"{CountText.Of(sel.Count, "shape")} · {bw}×{bh} bounds";
                }
                else
                {
                    SelectionBoundsText.Text = $"{CountText.Of(count, "shape")} selected";
                }
            }
            // The pill is a button now: screen readers hear the readout, not a fixed label.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SelectionBoundsPill, $"Selection bounds: {SelectionBoundsText.Text}");
        }

        private void OnSelectionBoundsPillRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (SelectionBoundsFlyout != null && SelectionBoundsPill != null)
            {
                SelectionBoundsFlyout.ShowAt(SelectionBoundsPill, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, SelectionBoundsPill) });
            }
        }

        // These three used to return silently with nothing to act on, and to record an undo step and
        // report "Centered/Squared/Reset 3 shapes" even when nothing moved, so Ctrl+Z then seemed to do
        // nothing. They now say why nothing happened and only record undo for a real change.
        private void OnSelectionCenterCanvasClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes on the canvas to center.";
                return;
            }

            double minX = sel.Min(s => s.X);
            double minY = sel.Min(s => s.Y);
            double maxX = sel.Max(s => s.X + s.Width);
            double maxY = sel.Max(s => s.Y + s.Height);
            double currentCenterX = (minX + maxX) / 2.0;
            double currentCenterY = (minY + maxY) / 2.0;

            const double canvasCenterX = 1100.0;
            const double canvasCenterY = 900.0;
            double dx = canvasCenterX - currentCenterX;
            double dy = canvasCenterY - currentCenterY;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
            {
                ViewModel.StatusMessage = sel.Count == 1 ? "The shape is already centered on the canvas." : "These shapes are already centered on the canvas.";
                return;
            }

            ViewModel.RecordUndo();
            foreach (var s in sel)
            {
                s.X += dx;
                s.Y += dy;
            }
            UpdateAdorner();
            UpdateSelectionBoundsPill();
            ViewModel.StatusMessage = $"Centered {CountText.Of(sel.Count, "shape")} on canvas · Ctrl+Z to undo";
        }

        private void OnSelectionMakeSquareClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0 && ViewModel.SelectedShape != null) sel.Add(ViewModel.SelectedShape);
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "Select a shape to make it square.";
                return;
            }

            var changing = sel.Where(s => Math.Abs(s.Width - s.Height) >= 0.5).ToList();
            if (changing.Count == 0)
            {
                ViewModel.StatusMessage = sel.Count == 1 ? "The shape is already square." : "These shapes are already square.";
                return;
            }

            ViewModel.RecordUndo();
            foreach (var s in changing)
            {
                double side = Math.Max(s.Width, s.Height);
                s.Width = side;
                s.Height = side;
            }
            UpdateAdorner();
            UpdateSelectionBoundsPill();
            ViewModel.StatusMessage = $"Squared {CountText.Of(changing.Count, "shape")} to equal width and height · Ctrl+Z to undo";
        }

        private void OnSelectionResetRotationClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0 && ViewModel.SelectedShape != null) sel.Add(ViewModel.SelectedShape);
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "Select a shape to reset its rotation.";
                return;
            }

            var changing = sel.Where(s => s.Rotation != 0).ToList();
            if (changing.Count == 0)
            {
                ViewModel.StatusMessage = sel.Count == 1 ? "The shape isn't rotated." : "None of these shapes are rotated.";
                return;
            }

            ViewModel.RecordUndo();
            foreach (var s in changing)
            {
                s.Rotation = 0;
            }
            UpdateAdorner();
            UpdateSelectionBoundsPill();
            ViewModel.StatusMessage = $"Reset rotation to 0° for {CountText.Of(changing.Count, "shape")} · Ctrl+Z to undo";
        }

        private void OnSelectionCopyBoundsClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                CopyAndReport("Canvas 2200x1800");
                return;
            }

            double minX = sel.Min(s => s.X);
            double minY = sel.Min(s => s.Y);
            double maxX = sel.Max(s => s.X + s.Width);
            double maxY = sel.Max(s => s.Y + s.Height);
            string geom = $"X: {minX:F0}, Y: {minY:F0}, Width: {(maxX - minX):F0}, Height: {(maxY - minY):F0}";
            CopyAndReport(geom, $"Copied geometry: {geom}");
        }

        private void OnSelectionBringForwardClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.BringForwardCommand.CanExecute(null))
            {
                ViewModel.BringForwardCommand.Execute(null);
                UpdateAdorner();
                UpdateSelectionBoundsPill();
                ViewModel.StatusMessage = "Brought selection forward one layer (Ctrl+]).";
            }
        }

        private void OnSelectionSendBackwardClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SendBackwardCommand.CanExecute(null))
            {
                ViewModel.SendBackwardCommand.Execute(null);
                UpdateAdorner();
                UpdateSelectionBoundsPill();
                ViewModel.StatusMessage = "Sent selection backward one layer (Ctrl+[).";
            }
        }

        private void OnSelectionBringToFrontClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.BringToFrontCommand.CanExecute(null))
            {
                ViewModel.BringToFrontCommand.Execute(null);
                UpdateAdorner();
                UpdateSelectionBoundsPill();
                ViewModel.StatusMessage = "Brought selection to top of layer stack (Ctrl+Shift+]).";
            }
        }

        private void OnSelectionSendToBackClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SendToBackCommand.CanExecute(null))
            {
                ViewModel.SendToBackCommand.Execute(null);
                UpdateAdorner();
                UpdateSelectionBoundsPill();
                ViewModel.StatusMessage = "Sent selection to bottom of layer stack (Ctrl+Shift+[).";
            }
        }

        // These used to overwrite the VM's line with "…with equal spacing", which was wrong (they
        // space left / top edges, not gaps) and claimed success when fewer than 3 shapes were selected.
        private void OnSelectionDistributeHorizontallyClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectionCount < 3)
            {
                ViewModel.StatusMessage = "Select at least 3 shapes to space their left edges evenly.";
                return;
            }
            ViewModel.DistributeHorizontal();
            UpdateMultiSelectionAdorner();
            UpdateSelectionBoundsPill();
        }

        private void OnSelectionDistributeVerticallyClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectionCount < 3)
            {
                ViewModel.StatusMessage = "Select at least 3 shapes to space their top edges evenly.";
                return;
            }
            ViewModel.DistributeVertical();
            UpdateMultiSelectionAdorner();
            UpdateSelectionBoundsPill();
        }

        private void OnSelectionFitInsideCanvasClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            bool wholeCanvas = sel.Count == 0;
            if (wholeCanvas) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                // Fit inside canvas did nothing on an empty canvas; say why (like Center and Snap to grid).
                ViewModel.StatusMessage = "No shapes on the canvas to fit.";
                return;
            }

            double minX = sel.Min(s => s.X);
            double minY = sel.Min(s => s.Y);
            double maxX = sel.Max(s => s.X + s.Width);
            double maxY = sel.Max(s => s.Y + s.Height);

            const double canvasW = 2200.0;
            const double canvasH = 1800.0;
            const double padding = 40.0;

            var (dx, dy) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.ComputeClampOffset(
                minX, minY, maxX, maxY, canvasW, canvasH, padding);

            if (Math.Abs(dx) > 0.001 || Math.Abs(dy) > 0.001)
            {
                ViewModel.RecordUndo();
                foreach (var s in sel)
                {
                    s.X += dx;
                    s.Y += dy;
                }
                UpdateAdorner();
                UpdateMultiSelectionAdorner();
                UpdateSelectionBoundsPill();
                // Used to report the raw clamp offsets, with no undo hint.
                ViewModel.StatusMessage = $"Moved {MarkSmith.Services.CountText.Of(sel.Count, "shape")} inside the canvas · Ctrl+Z to undo";
            }
            else
            {
                ViewModel.StatusMessage = wholeCanvas
                    ? "Every shape is already inside the canvas."
                    : sel.Count == 1 ? "The shape is already inside the canvas." : "The selection is already inside the canvas.";
            }
        }

        private void OnSelectionSnapToGridClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes to snap to grid.";
                return;
            }

            ViewModel.RecordUndo();
            int movedCount = 0;
            foreach (var s in sel)
            {
                var (snappedX, snappedY) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.SnapPointToGrid(s.X, s.Y, 10.0);
                if (Math.Abs(s.X - snappedX) > 0.001 || Math.Abs(s.Y - snappedY) > 0.001)
                {
                    s.X = snappedX;
                    s.Y = snappedY;
                    movedCount++;
                }
            }

            UpdateAdorner();
            UpdateMultiSelectionAdorner();
            UpdateSelectionBoundsPill();
            ViewModel.StatusMessage = movedCount > 0
                ? $"✓ Snapped {CountText.Of(sel.Count, "shape")} to 10px grid ({CountText.Of(movedCount, "shape")} aligned)."
                : "All selected shapes are already aligned to 10px grid.";
        }

        private void OnSelectionStandardizeRatioClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes to standardize aspect ratio.";
                return;
            }

            ViewModel.RecordUndo();
            int updatedCount = 0;
            foreach (var s in sel)
            {
                var (_, newH) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.ApplyStandardAspectRatio(s.Width, s.Height, 16.0 / 9.0);
                if (Math.Abs(s.Height - newH) > 0.001)
                {
                    s.Height = newH;
                    updatedCount++;
                }
            }

            UpdateAdorner();
            UpdateMultiSelectionAdorner();
            UpdateSelectionBoundsPill();
            ViewModel.StatusMessage = updatedCount > 0
                ? $"✓ Standardized {CountText.Of(updatedCount, "shape")} to 16:9 widescreen aspect ratio."
                : "Selected shapes already conform to 16:9 aspect ratio.";
        }

        private void OnSelectionCycleRotationClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes to rotate.";
                return;
            }

            ViewModel.RecordUndo();
            int baseRot = sel.Count > 0 ? sel[0].Rotation : 0;
            var (nextRot, label) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.CycleCardinalRotation(baseRot);
            foreach (var s in sel)
            {
                s.Rotation = nextRot;
            }

            UpdateAdorner();
            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"✓ Rotated {CountText.Of(sel.Count, "shape")} to {nextRot}° ({label}).";
        }

        private void OnSelectionToggleCornerStyleClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes to change corner style.";
                return;
            }

            ViewModel.RecordUndo();
            var first = sel[0];
            var (nextPrst, label) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.ToggleCornerStyle(first.Prst);
            int updatedCount = 0;
            foreach (var s in sel)
            {
                if (!s.IsLine)
                {
                    s.Prst = nextPrst;
                    updatedCount++;
                }
            }

            UpdateAdorner();
            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"✓ Set {CountText.Of(updatedCount, "shape")} to {label}.";
        }

        private string _currentStrokeDash = "solid";

        private void OnSelectionCycleStrokeDashClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes to change stroke dash style.";
                return;
            }

            ViewModel.RecordUndo();
            var (nextDash, label) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.CycleStrokeDashStyle(_currentStrokeDash);
            _currentStrokeDash = nextDash;

            int updatedCount = 0;
            foreach (var s in sel)
            {
                if (_shapePaths.TryGetValue(s, out var path))
                {
                    path.StrokeDashArray = nextDash switch
                    {
                        "dashed" => new DoubleCollection { 4, 2 },
                        "dotted" => new DoubleCollection { 2, 2 },
                        _ => null
                    };
                    updatedCount++;
                }
            }

            UpdateAdorner();
            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"✓ Set {CountText.Of(updatedCount, "shape")} to {label}.";
        }

        private double _currentShapeOpacity = 1.0;

        private void OnSelectionCycleOpacityClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes to change opacity.";
                return;
            }

            ViewModel.RecordUndo();
            var (nextOpacity, label) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.CycleShapeOpacity(_currentShapeOpacity);
            _currentShapeOpacity = nextOpacity;

            int updatedCount = 0;
            foreach (var s in sel)
            {
                if (_shapePaths.TryGetValue(s, out var path))
                {
                    path.Opacity = nextOpacity;
                    updatedCount++;
                }
            }

            UpdateAdorner();
            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"✓ Set {CountText.Of(updatedCount, "shape")} to {label}.";
        }

        private double _currentShadowBlurRadius = 0.0;

        private void OnSelectionCycleShadowBlurClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes to change shadow blur.";
                return;
            }

            ViewModel.RecordUndo();
            var (nextBlur, label) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.CycleShadowBlurRadius(_currentShadowBlurRadius);
            _currentShadowBlurRadius = nextBlur;

            int updatedCount = 0;
            foreach (var s in sel)
            {
                if (_shapePaths.TryGetValue(s, out var path))
                {
                    updatedCount++;
                }
            }

            UpdateAdorner();
            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"✓ Set {CountText.Of(updatedCount, "shape")} to {label}.";
        }

        private double _currentCornerRadius = 0.0;

        private void OnSelectionCycleCornerRadiusClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes to change corner radius.";
                return;
            }

            ViewModel.RecordUndo();
            var (nextRadius, label) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.CycleCornerRadius(_currentCornerRadius);
            _currentCornerRadius = nextRadius;

            int updatedCount = 0;
            foreach (var s in sel)
            {
                if (_shapePaths.TryGetValue(s, out var path))
                {
                    updatedCount++;
                }
            }

            UpdateAdorner();
            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"✓ Set {CountText.Of(updatedCount, "shape")} to {label}.";
        }

        private double _currentStrokeDashOffset = 0.0;

        private void OnSelectionCycleStrokeDashOffsetClick(object sender, RoutedEventArgs e)
        {
            var sel = ViewModel.Shapes.Where(s => s.IsSelected).ToList();
            if (sel.Count == 0) sel = ViewModel.Shapes.ToList();
            if (sel.Count == 0)
            {
                ViewModel.StatusMessage = "No shapes to change dash offset.";
                return;
            }

            ViewModel.RecordUndo();
            var (nextOffset, label) = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.CycleStrokeDashOffset(_currentStrokeDashOffset);
            _currentStrokeDashOffset = nextOffset;

            int updatedCount = 0;
            foreach (var s in sel)
            {
                if (_shapePaths.TryGetValue(s, out var path))
                {
                    path.StrokeDashOffset = nextOffset;
                    updatedCount++;
                }
            }

            UpdateAdorner();
            UpdateMultiSelectionAdorner();
            ViewModel.StatusMessage = $"✓ Set {CountText.Of(updatedCount, "shape")} to {label}.";
        }

        private void OnSelectionReverseZOrderClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            ViewModel.ReverseSelectedZOrder();
            var summary = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.FormatZOrderSummary(ViewModel.Shapes, ViewModel.Shapes.Where(s => s.IsSelected));
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionNormalizePrecisionClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (adjusted, total, maxDelta) = ViewModel.NormalizeShapePrecision(1.0);
            var summary = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.FormatPrecisionNormalizationSummary(adjusted, total, 1.0, maxDelta);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionMatchSizeClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (resized, total, targetW, targetH, refLabel) = ViewModel.MatchSelectedShapeDimensions(matchWidth: true, matchHeight: true);
            var summary = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.FormatDimensionMatchSummary(resized, total, targetW, targetH, refLabel);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionFlipHorizontalClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (flipped, pivotX, pivotY) = ViewModel.FlipSelectedShapes(horizontal: true, vertical: false);
            var summary = MarkSmith.ViewModels.ShapeStudio.ShapeDesignStudioViewModel.FormatFlipTransformSummary(flipped, horizontal: true, vertical: false, pivotX, pivotY);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionSelectSimilarKindClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (matched, total, kind, fill, summary) = ViewModel.SelectSimilarShapes(matchKind: true, matchFill: false);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionSelectSimilarFillClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (matched, total, kind, fill, summary) = ViewModel.SelectSimilarShapes(matchKind: false, matchFill: true);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionResolveOverlapsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (nudged, total, summary) = ViewModel.ResolveShapeOverlaps(16.0);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionDistributeEvenlyHorizClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (distCount, total, summary) = ViewModel.DistributeShapesEvenly(DistributionAxis.Horizontal);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionDistributeEvenlyVertClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (distCount, total, summary) = ViewModel.DistributeShapesEvenly(DistributionAxis.Vertical);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionScaleProportionalClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (scaledCount, total, factor, summary) = ViewModel.ScaleSelectedShapesProportionally(1.25);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionDistributeRadialClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (arrangedCount, rx, ry, summary) = ViewModel.DistributeShapesRadially();
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionAuditLayerStackClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditLayerStackIndices();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditGridSnapClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditGridAlignment(8.0);
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionQuantizeGridClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (targets, modified, summary) = ViewModel.QuantizeShapeCoordinates(8.0);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionAuditAspectRatioClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditAspectRatioProportions();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionLockAspectRatioSquareClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (targets, modified, summary) = ViewModel.EnforceAspectRatioLock(1.0, "1:1 Square");
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionAuditSpacingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditSpacingDistribution();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionDistributeSpacingHorizontalClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (targets, modified, gap, summary) = ViewModel.DistributeSpacingUniformly(DistributionAxis.Horizontal);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionDistributeSpacingVerticalClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var (targets, modified, gap, summary) = ViewModel.DistributeSpacingUniformly(DistributionAxis.Vertical);
            ReportEdit(summary);
            UpdateAdorner();
            UpdateMultiSelectionAdorner();
        }

        private void OnSelectionAuditOcclusionClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditOcclusionAndOverlap();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditBoundaryBleedClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditBoundaryMarginsAndBleed();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditTextContrastClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeTextContrast();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditGoldenRatioClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditGoldenRatioProgression();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditGoldenSpiralClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditGoldenSpiralProgression();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditColorHarmonyClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditColorPaletteHarmony();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditIsometricClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditIsometricGridAlignment();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditSymmetryClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditDiagramSymmetry();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditSpacingUniformityClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditSpacingUniformityAndCollisions();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditOrthogonalRoutingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditOrthogonalRoutingAndIntersections();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditGridSnapConformanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditGridSnapConformance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditVisualWeightClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditVisualWeightAndOpticalCentroid();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditConnectorCurvatureClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditConnectorCurvatureAndTangents();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditAspectRatioScalingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditAspectRatioAndPrimitiveScaling();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditStrokeAlignmentClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditStrokeAlignmentAndCornerRadius();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditConnectorObstaclesClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditConnectorObstacleCollisions();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditCompoundGroupsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditCompoundGroupBoundsAndStacking();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditConnectorPortSnappingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditConnectorPortSnapping();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditCanvasSpatialBalanceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditCanvasSpatialBalance();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditShapeCollinearityClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeCollinearityAndSpacing();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditVisualFlowDirectionalityClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditVisualFlowDirectionality();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditAspectRatioGoldenRatioClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeAspectRatioAndGoldenRatio();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditCornerRadiusConsistencyClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeCornerRadiusConsistency();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditZIndexOcclusionClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeZIndexLayeringAndOcclusion();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditConnectorArrowheadUniformityClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditConnectorArrowheadUniformity();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditStrokeWeightAndDashClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditStrokeWeightAndDashUniformity();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditFillColorPaletteHarmonyClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditFillColorPaletteHarmony();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditAspectRatioHarmonyClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeAspectRatioHarmony();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditConnectorRoutesClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditConnectorRouteDirectness();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditLayerOcclusionClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditLayerStackingAndOcclusion();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditAlignmentSnappingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditAlignmentAndGridSnapping();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditConnectionPortRoutingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditConnectionPortOrthogonality();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditVisualDensityMarginsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditVisualDensityAndMargins();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditZOrderOcclusionClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditZOrderAndOcclusion();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditConnectorOrthogonalBendsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditConnectorOrthogonalBends();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditAlignmentGuidesSnappingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditAlignmentGuidesAndSnapping();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditGroupHierarchyPaddingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditGroupHierarchyAndPadding();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditLayeringZOrderOcclusionClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditLayeringZOrderAndOcclusion();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditVisualWeightContrastClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeVisualWeightAndContrast();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditConnectorAnchorSnappingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditConnectorAnchorSnappingAndOrthogonality();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditShapeAspectRatioClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeAspectRatioUniformityAndProportions();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditColorContrastClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeColorContrastAccessibility();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }

        private void OnSelectionAuditShapeZOrderStackingClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var result = ViewModel.AuditShapeZOrderStackingAndOcclusion();
            CopyAndReport(result.Summary, $"✓ {result.Summary}");
        }
    private void OnSelectionAuditShapeTextPaddingClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditShapeTextPaddingAndOverflow(selectionOnly: ViewModel.Shapes.Any(s => s.IsSelected));
        CopyAndReport(result.Summary, $"✓ {result.Summary}");
    }

    private void OnSelectionAuditConnectorArrowheadsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditConnectorArrowheadAndPortAttachments(selectionOnly: ViewModel.Shapes.Any(s => s.IsSelected));
        CopyAndReport(result.Summary, $"✓ {result.Summary}");
    }

    private void OnSelectionAuditShapeStrokeDashClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditShapeStrokeDashUniformity(selectionOnly: ViewModel.Shapes.Any(s => s.IsSelected));
        CopyAndReport(result.Summary, $"✓ {result.Summary}");
    }



    private void OnSelectionAuditShapeElevationShadowClick(object sender, RoutedEventArgs e)

    {

        if (ViewModel == null) return;

        var result = ViewModel.AuditShapeElevationAndShadowHarmony(selectionOnly: ViewModel.Shapes.Any(s => s.IsSelected));

        CopyAndReport(result.Summary, $"✓ {result.Summary}");

    }


    private void OnSelectionAuditShapeGroupNestingClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditShapeGroupNestingHierarchy(selectionOnly: ViewModel.Shapes.Any(s => s.IsSelected));
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }
    private void OnSelectionAuditObliqueAlignmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditObliqueProjectionAlignment();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditDimetricAlignmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditDimetricProjectionAlignment();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditTrimetricAlignmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditTrimetricProjectionAlignment();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditBezierHandleSymmetryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditBezierHandleSymmetry();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditDiagramViewportBleedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditDiagramViewportBleedSafety();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditDiagramCoordinateSnapGridAlignmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditDiagramCoordinateSnapGridAlignment();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditShapeAspectRatioDistortionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditShapeAspectRatioDistortion();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditOrthogonalConnectorWaypointsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditOrthogonalConnectorWaypoints();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditDiagramZIndexStackingClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditDiagramZIndexStacking();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditDiagramTextContrastClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditDiagramTextContrast();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditDiagramShadowConsistencyClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditDiagramShadowConsistency();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditShapeOcclusionAndStackingOrderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditShapeOcclusionAndStackingOrder();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }



    private void OnSelectionAuditMagneticSnappingGuidesClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditMagneticSnappingGuides();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditIsometricGridSnappingClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditIsometricGridSnapping();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }


    private void OnSelectionAuditConnectorRoutingAlignmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditConnectorAnchorSnapping();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditIsometricAlignmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditIsometricProjectionAlignment();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }

    private void OnSelectionAuditRadialAlignmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var result = ViewModel.AuditRadialAndConcentricAlignment();
        CopyAndReport(result.Summary, $"\u2713 {result.Summary}");
    }
    }
}
