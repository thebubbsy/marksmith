using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Views;

// The first-run guided tour: 7 visibility-switched pages (NOT a FlipView — see the note in the
// XAML: FlipView inside a detached UserControl fails Application.LoadComponent on WASDK 1.6)
// walking through Source, Style, diagrams/math, Export, and automation/Pro. Shown once
// automatically and replayable from the title bar. Raises Completed when the user finishes or
// skips; the hosting ContentDialog closes on that.
public sealed partial class WelcomeTour : UserControl
{
    public event EventHandler? Completed;

    // True when the user finished with the "Open the sample" card. Skip, Esc and "Start with
    // my own" leave it false.
    public bool LoadSampleRequested { get; private set; }

    private StackPanel[] _pages = Array.Empty<StackPanel>();
    private int _index;
    private Storyboard? _transition;

    private static readonly string[] PageTitles =
    {
        "Welcome to MarkSmith",
        "Source",
        "Style",
        "Preview & Export",
        "Diagrams & math",
        "Automation & Pro",
        "You're all set",
    };

    public static IReadOnlyList<string> StepTitles => PageTitles;

    public static string FormatStepBadge(int index, int total)
    {
        var safeTotal = Math.Max(1, total);
        var clamped = Math.Clamp(index, 0, safeTotal - 1);
        return $"Step {clamped + 1} of {safeTotal}";
    }

    public static bool TryParseSlideNumberKey(Windows.System.VirtualKey key, out int slideIndex)
    {
        if (key >= Windows.System.VirtualKey.Number1 && key <= Windows.System.VirtualKey.Number7)
        {
            slideIndex = (int)key - (int)Windows.System.VirtualKey.Number1;
            return true;
        }
        if (key >= Windows.System.VirtualKey.NumberPad1 && key <= Windows.System.VirtualKey.NumberPad7)
        {
            slideIndex = (int)key - (int)Windows.System.VirtualKey.NumberPad1;
            return true;
        }
        slideIndex = -1;
        return false;
    }

    // editorHasDocument: the tour was replayed with work in the editor. The sample never replaces
    // a document, so its card says so and stays disabled instead of silently doing nothing.
    public WelcomeTour(bool editorHasDocument = false)
    {
        InitializeComponent();
        // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.
        KeyboardContextMenu.BridgeAll(
            (Step1Card, OnStepCardRightTapped),
            (Step2Card, OnStepCardRightTapped),
            (Step3Card, OnStepCardRightTapped),
            (SampleCard, OnSampleCardRightTapped),
            (BlankCard, OnBlankCardRightTapped),
            (SkipButton, OnSkipButtonRightTapped),
            (BackButton, OnBackButtonRightTapped),
            (NextButton, OnNextButtonRightTapped));
        PlanText.Text = $"{ProGate.FreePlanIncludes} {ProGate.ProPlanAdds} {ProGate.TrialSummary}";
        if (editorHasDocument)
        {
            SampleCard.IsEnabled = false;
            SampleCaption.Text = "Your editor already has a document, and the sample never replaces your work. Clear the editor to try it.";
        }
        // Page4 (3 · Preview & Export) runs before Page3 (Diagrams & math) so the numbered
        // pipeline reads 1, 2, 3 in order and the unnumbered extras follow it.
        _pages = new[] { Page0, Page1, Page2, Page4, Page3, Page5, Page6 };
        foreach (var page in _pages) page.RenderTransform = new TranslateTransform();
        Show(0);
        HoverPolish.Track(this);
        // Handled-too: the Back/Next buttons and the PipsPager mark arrow keys handled when they
        // have focus, but Left/Right should page the tour from anywhere inside it.
        AddHandler(KeyDownEvent, new KeyEventHandler(OnTourKeyDown), true);
        RightTapped += OnTourRightTapped;
        Loaded += (_, _) => FitHostToTallestPage();
        SizeChanged += (_, _) => FitHostToTallestPage();
        Unloaded += (_, _) => _transition?.Stop();
    }

    // Every page shares one height so the pips and buttons never jump while paging — but that
    // height is the tallest page's, not a guessed constant (a fixed 340 left a big empty band
    // under the shorter pages). Collapsed pages measure as zero, so each is measured visible.
    private void FitHostToTallestPage()
    {
        var width = PagesHost.ActualWidth;
        if (width <= 0) return;
        double tallest = 0;
        foreach (var page in _pages)
        {
            var was = page.Visibility;
            page.Visibility = Visibility.Visible;
            page.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            tallest = Math.Max(tallest, page.DesiredSize.Height);
            page.Visibility = was;
        }
        PagesHost.Height = Math.Ceiling(tallest);
    }

    private int Last => _pages.Length - 1;

    private void Show(int index)
    {
        var previous = _index;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        _index = Math.Clamp(index, 0, Last);
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == _index ? Visibility.Visible : Visibility.Collapsed;
        if (Pips.SelectedPageIndex != _index) Pips.SelectedPageIndex = _index;
        BackButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The last page's two cards are its buttons, so Next and Skip step aside there.
        var last = _index >= Last;
        NextButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        if (_index != previous) SlideIn(_pages[_index], forward: _index > previous);

        var title = _index < PageTitles.Length ? PageTitles[_index] : "";
        var badge = FormatStepBadge(_index, _pages.Length);
        if (StepBadgeText is not null) StepBadgeText.Text = badge;

        var announcement = $"{badge}: {title}";
        AutomationProperties.SetName(PagesHost, announcement);
        AutomationProperties.SetLiveSetting(PagesHost, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(Pips, $"Page {_index + 1} of {_pages.Length}");

        if (_index > 0 && _index - 1 < PageTitles.Length)
        {
            var prevTitle = PageTitles[_index - 1];
            ToolTipService.SetToolTip(BackButton, $"Previous step: {prevTitle} (Left arrow)");
            AutomationProperties.SetName(BackButton, $"Back to {prevTitle}");
        }
        if (_index < Last && _index + 1 < PageTitles.Length)
        {
            var nextTitle = PageTitles[_index + 1];
            ToolTipService.SetToolTip(NextButton, $"Next step: {nextTitle} (Right arrow)");
            AutomationProperties.SetName(NextButton, $"Next: {nextTitle}");
        }

        // Next had focus and just collapsed; hand keyboard focus to the first card rather than
        // dropping it on the dialog.
        if (last && _index != previous)
            (SampleCard.IsEnabled ? SampleCard : BlankCard).Focus(FocusState.Programmatic);
        // A welcome-slide card (or BackButton when returning to Page 0) had focus and just
        // collapsed. Left alone, focus fell to the page dots, where the arrow keys only move a
        // focus ring between dots and the tour stopped turning. Next is where the keyboard user is
        // headed anyway.
        else if (_index != previous && focused is not null &&
                 (IsInside(focused, _pages[previous]) || (_index == 0 && IsInside(focused, BackButton))))
            NextButton.Focus(FocusState.Programmatic);
    }

    // The incoming page fades in while drifting 24px from the side it came from, so paging reads
    // as moving through a sequence instead of the content snapping. Skipped when Windows'
    // "Animation effects" is off.
    private void SlideIn(StackPanel page, bool forward)
    {
        _transition?.Stop();
        if (!HoverPolish.AnimationsEnabled || page.RenderTransform is not TranslateTransform shift)
        {
            page.Opacity = 1;
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = ease };
        var slide = new DoubleAnimation { From = forward ? 24 : -24, To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, nameof(Opacity));
        Storyboard.SetTarget(slide, shift);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.X));
        _transition = new Storyboard();
        _transition.Children.Add(fade);
        _transition.Children.Add(slide);
        _transition.Begin();
    }

    private void OnTourKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.Application || (shift && !ctrl && !alt && e.Key == Windows.System.VirtualKey.F10))
        {
            // A focused button with its own menu (Skip, Next, the step cards) already opened it.
            if (KeyboardContextMenu.IsFromBridgedControl(e)) return;
            ShowTourContextMenu(null);
            e.Handled = true;
            return;
        }

        if (IsMenuFlyoutOpen()) return;

        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;

        // Direct step jump keys 1..7 (or NumberPad 1..7) when no modifier is held
        if (!ctrl && !shift && !alt && TryParseSlideNumberKey(e.Key, out var targetSlide) && targetSlide <= Last)
        {
            if (targetSlide != _index) Show(targetSlide);
            e.Handled = true;
            return;
        }

        // On Page 6, allow arrowing between SampleCard, BlankCard, and BackButton before falling back to paging:
        if (_index == Last && focused is not null)
        {
            if (e.Key == Windows.System.VirtualKey.Left && IsInside(focused, BlankCard) && SampleCard.IsEnabled)
            {
                SampleCard.Focus(FocusState.Keyboard);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Right && IsInside(focused, SampleCard))
            {
                BlankCard.Focus(FocusState.Keyboard);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Down && (IsInside(focused, SampleCard) || IsInside(focused, BlankCard)))
            {
                BackButton.Focus(FocusState.Keyboard);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Up && IsInside(focused, BackButton))
            {
                (SampleCard.IsEnabled ? SampleCard : BlankCard).Focus(FocusState.Keyboard);
                e.Handled = true;
                return;
            }
        }

        // On Page 0, allow arrowing between Step 1, Step 2, and Step 3 cards and Next/Skip buttons:
        if (_index == 0 && focused is Button { Tag: string tag } && int.TryParse(tag, out var step))
        {
            if (e.Key == Windows.System.VirtualKey.Right && step < 3)
            {
                var nextBtn = FindStepButton(step + 1);
                if (nextBtn is not null)
                {
                    nextBtn.Focus(FocusState.Keyboard);
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Left && step > 1)
            {
                var prevBtn = FindStepButton(step - 1);
                if (prevBtn is not null)
                {
                    prevBtn.Focus(FocusState.Keyboard);
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Down)
            {
                NextButton.Focus(FocusState.Keyboard);
                e.Handled = true;
                return;
            }
        }
        else if (_index == 0 && focused is not null && e.Key == Windows.System.VirtualKey.Up
                 && (IsInside(focused, NextButton) || IsInside(focused, SkipButton)))
        {
            var firstStep = FindStepButton(1);
            if (firstStep is not null)
            {
                firstStep.Focus(FocusState.Keyboard);
                e.Handled = true;
                return;
            }
        }

        // Guard Left/Right arrow navigation when focus is inside the bottom button row
        if (focused is not null && (IsInside(focused, SkipButton) || IsInside(focused, BackButton) || IsInside(focused, NextButton)))
        {
            if (e.Key == Windows.System.VirtualKey.Right)
            {
                if (IsInside(focused, SkipButton))
                {
                    (BackButton.Visibility == Visibility.Visible ? BackButton : NextButton).Focus(FocusState.Keyboard);
                    e.Handled = true;
                    return;
                }
                if (IsInside(focused, BackButton))
                {
                    NextButton.Focus(FocusState.Keyboard);
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Left)
            {
                if (IsInside(focused, NextButton) && BackButton.Visibility == Visibility.Visible)
                {
                    BackButton.Focus(FocusState.Keyboard);
                    e.Handled = true;
                    return;
                }
                if (IsInside(focused, BackButton) || (IsInside(focused, NextButton) && BackButton.Visibility != Visibility.Visible))
                {
                    SkipButton.Focus(FocusState.Keyboard);
                    e.Handled = true;
                    return;
                }
            }
        }

        // Global slide navigation: PageUp/PageDown, Home/End, Left/Right
        if ((e.Key == Windows.System.VirtualKey.Right || e.Key == Windows.System.VirtualKey.PageDown) && _index < Last)
        {
            Show(_index + 1);
            e.Handled = true;
        }
        else if ((e.Key == Windows.System.VirtualKey.Left || e.Key == Windows.System.VirtualKey.PageUp) && _index > 0)
        {
            Show(_index - 1);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Home && _index > 0)
        {
            Show(0);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.End && _index < Last)
        {
            Show(Last);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Completed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private bool IsMenuFlyoutOpen()
    {
        if (XamlRoot is null) return false;
        return VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => p.Child is not ContentDialog);
    }

    private void OnTourRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowTourContextMenu(KeyboardContextMenu.PointIn(e, this));
    }

    private void ShowTourContextMenu(Windows.Foundation.Point? position)
    {
        var menu = new MenuFlyout();

        var nextItem = new MenuFlyoutItem
        {
            Text = "Next step",
            Icon = new FontIcon { Glyph = "\uE76C" },
            IsEnabled = _index < Last
        };
        nextItem.Click += (_, _) => { if (_index < Last) Show(_index + 1); };
        menu.Items.Add(nextItem);

        var prevItem = new MenuFlyoutItem
        {
            Text = "Previous step",
            Icon = new FontIcon { Glyph = "\uE76B" },
            IsEnabled = _index > 0
        };
        prevItem.Click += (_, _) => { if (_index > 0) Show(_index - 1); };
        menu.Items.Add(prevItem);

        var jumpSub = new MenuFlyoutSubItem
        {
            Text = "Jump to step",
            Icon = new FontIcon { Glyph = "\uE8FD" }
        };
        for (var i = 0; i < PageTitles.Length && i < _pages.Length; i++)
        {
            var stepIdx = i;
            var stepItem = new ToggleMenuFlyoutItem
            {
                Text = $"{stepIdx + 1} · {PageTitles[stepIdx]}",
                IsChecked = stepIdx == _index
            };
            stepItem.Click += (_, _) => Show(stepIdx);
            jumpSub.Items.Add(stepItem);
        }
        menu.Items.Add(jumpSub);
        menu.Items.Add(new MenuFlyoutSeparator());

        if (SampleCard.IsEnabled)
        {
            var sampleItem = new MenuFlyoutItem
            {
                Text = "Open the sample document",
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            sampleItem.Click += OnSampleCard;
            menu.Items.Add(sampleItem);
        }

        var skipItem = new MenuFlyoutItem
        {
            Text = "Skip tour",
            KeyboardAcceleratorTextOverride = "Esc",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        skipItem.Click += OnSkip;
        menu.Items.Add(skipItem);

        if (position.HasValue)
            menu.ShowAt(this, position.Value);
        else
            menu.ShowAt(this);
    }

    private Button? FindStepButton(int step)
    {
        return FindButtons(Page0).FirstOrDefault(b => b.Tag is string s && int.TryParse(s, out var v) && v == step);
    }

    private static IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button b) yield return b;
            foreach (var descendant in FindButtons(child)) yield return descendant;
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private void OnPipSelected(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        if (Pips.SelectedPageIndex >= 0 && Pips.SelectedPageIndex != _index) Show(Pips.SelectedPageIndex);
    }

    private void OnStepCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var page)) Show(page);
    }

    private void OnBack(object sender, RoutedEventArgs e) => Show(_index - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index < Last) Show(_index + 1);
    }

    private void OnSampleCard(object sender, RoutedEventArgs e)
    {
        LoadSampleRequested = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void OnBlankCard(object sender, RoutedEventArgs e) => Completed?.Invoke(this, EventArgs.Empty);

    private void OnSkip(object sender, RoutedEventArgs e) => Completed?.Invoke(this, EventArgs.Empty);

    private void OnStepBadgeTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowStepBadgeMenu(StepBadgeBorder, e.GetPosition(StepBadgeBorder));
    }

    private void OnStepBadgeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowStepBadgeMenu(StepBadgeBorder, KeyboardContextMenu.PointIn(e, StepBadgeBorder));
    }

    private void ShowStepBadgeMenu(FrameworkElement anchor, Windows.Foundation.Point? position)
    {
        var menu = new MenuFlyout();

        for (int i = 0; i < PageTitles.Length; i++)
        {
            int pageIndex = i;
            var item = new MenuFlyoutItem
            {
                Text = $"{i + 1} · {PageTitles[i]}",
                Icon = i == _index ? new FontIcon { Glyph = "\uE73E" } : new FontIcon { Glyph = "\uE8A5" }
            };
            item.Click += (_, _) => Show(pageIndex);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var copySteps = new MenuFlyoutItem
        {
            Text = "Copy tour step overview",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copySteps.Click += (_, _) =>
        {
            var overview = string.Join(Environment.NewLine, PageTitles.Select((t, idx) => $"{idx + 1}. {t}"));
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(overview);
            ClipboardWriter.Set(dp);
        };
        menu.Items.Add(copySteps);

        var replay = new MenuFlyoutItem
        {
            Text = "Replay from step 1",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        replay.Click += (_, _) => Show(0);
        menu.Items.Add(replay);

        if (position.HasValue)
            menu.ShowAt(anchor, position.Value);
        else
            menu.ShowAt(anchor);
    }

    private void OnStepCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button btn) return;
        var stepNum = btn.Tag is string s && int.TryParse(s, out var v) ? v : 1;

        var menu = new MenuFlyout();
        var jumpItem = new MenuFlyoutItem
        {
            Text = $"Jump to step {stepNum}",
            Icon = new FontIcon { Glyph = "\uE8FD" }
        };
        jumpItem.Click += (_, _) => Show(stepNum);
        menu.Items.Add(jumpItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = "Copy step overview",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += (_, _) =>
        {
            var title = stepNum < PageTitles.Length ? PageTitles[stepNum] : $"Step {stepNum}";
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText($"{stepNum}. {title}");
            ClipboardWriter.Set(dp);
        };
        menu.Items.Add(copyItem);

        if (_index < Last)
        {
            var nextItem = new MenuFlyoutItem
            {
                Text = "Next step",
                KeyboardAcceleratorTextOverride = "Right",
                Icon = new FontIcon { Glyph = "\uE76C" }
            };
            nextItem.Click += (_, _) => Show(_index + 1);
            menu.Items.Add(nextItem);
        }

        var skipItem = new MenuFlyoutItem
        {
            Text = "Skip tour",
            KeyboardAcceleratorTextOverride = "Esc",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        skipItem.Click += OnSkip;
        menu.Items.Add(skipItem);

        menu.ShowAt(btn, KeyboardContextMenu.PointIn(e, btn));
    }

    private void OnSampleCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        if (SampleCard.IsEnabled)
        {
            var openSample = new MenuFlyoutItem
            {
                Text = "Open the sample document",
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            openSample.Click += OnSampleCard;
            menu.Items.Add(openSample);
        }

        var startOwn = new MenuFlyoutItem
        {
            Text = "Start with my own document",
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        startOwn.Click += OnBlankCard;
        menu.Items.Add(startOwn);

        var copyDesc = new MenuFlyoutItem
        {
            Text = "Copy sample description",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyDesc.Click += (_, _) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(SampleCaption.Text);
            ClipboardWriter.Set(dp);
        };
        menu.Items.Add(copyDesc);

        var skip = new MenuFlyoutItem
        {
            Text = "Skip tour",
            KeyboardAcceleratorTextOverride = "Esc",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        skip.Click += OnSkip;
        menu.Items.Add(skip);

        menu.ShowAt(SampleCard, KeyboardContextMenu.PointIn(e, SampleCard));
    }

    private void OnBlankCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var startOwn = new MenuFlyoutItem
        {
            Text = "Start with my own document",
            KeyboardAcceleratorTextOverride = "Enter",
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        startOwn.Click += OnBlankCard;
        menu.Items.Add(startOwn);

        if (SampleCard.IsEnabled)
        {
            var openSample = new MenuFlyoutItem
            {
                Text = "Open the sample document",
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            openSample.Click += OnSampleCard;
            menu.Items.Add(openSample);
        }

        var replay = new MenuFlyoutItem
        {
            Text = "Replay from step 1",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        replay.Click += (_, _) => Show(0);
        menu.Items.Add(replay);

        var skip = new MenuFlyoutItem
        {
            Text = "Skip tour",
            KeyboardAcceleratorTextOverride = "Esc",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        skip.Click += OnSkip;
        menu.Items.Add(skip);

        menu.ShowAt(BlankCard, KeyboardContextMenu.PointIn(e, BlankCard));
    }

    private void OnSkipButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var skip = new MenuFlyoutItem
        {
            Text = "Skip tour",
            KeyboardAcceleratorTextOverride = "Esc",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        skip.Click += OnSkip;
        menu.Items.Add(skip);

        var replay = new MenuFlyoutItem
        {
            Text = "Replay from step 1",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        replay.Click += (_, _) => Show(0);
        menu.Items.Add(replay);

        var copySteps = new MenuFlyoutItem
        {
            Text = "Copy tour step overview",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copySteps.Click += (_, _) =>
        {
            var overview = string.Join(Environment.NewLine, PageTitles.Select((t, idx) => $"{idx + 1}. {t}"));
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(overview);
            ClipboardWriter.Set(dp);
        };
        menu.Items.Add(copySteps);

        menu.ShowAt(SkipButton, KeyboardContextMenu.PointIn(e, SkipButton));
    }

    private void OnBackButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var prevItem = new MenuFlyoutItem
        {
            Text = "Previous step",
            KeyboardAcceleratorTextOverride = "Left",
            Icon = new FontIcon { Glyph = "\uE76B" },
            IsEnabled = _index > 0
        };
        prevItem.Click += OnBack;
        menu.Items.Add(prevItem);

        var firstItem = new MenuFlyoutItem
        {
            Text = "Jump to first step",
            KeyboardAcceleratorTextOverride = "Home",
            Icon = new FontIcon { Glyph = "\uE892" },
            IsEnabled = _index > 0
        };
        firstItem.Click += (_, _) => Show(0);
        menu.Items.Add(firstItem);

        var skip = new MenuFlyoutItem
        {
            Text = "Skip tour",
            KeyboardAcceleratorTextOverride = "Esc",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        skip.Click += OnSkip;
        menu.Items.Add(skip);

        menu.ShowAt(BackButton, KeyboardContextMenu.PointIn(e, BackButton));
    }

    private void OnNextButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var nextItem = new MenuFlyoutItem
        {
            Text = "Next step",
            KeyboardAcceleratorTextOverride = "Right",
            Icon = new FontIcon { Glyph = "\uE76C" },
            IsEnabled = _index < Last
        };
        nextItem.Click += OnNext;
        menu.Items.Add(nextItem);

        var lastItem = new MenuFlyoutItem
        {
            Text = "Jump to last step",
            KeyboardAcceleratorTextOverride = "End",
            Icon = new FontIcon { Glyph = "\uE893" },
            IsEnabled = _index < Last
        };
        lastItem.Click += (_, _) => Show(Last);
        menu.Items.Add(lastItem);

        var skip = new MenuFlyoutItem
        {
            Text = "Skip tour",
            KeyboardAcceleratorTextOverride = "Esc",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        skip.Click += OnSkip;
        menu.Items.Add(skip);

        menu.ShowAt(NextButton, KeyboardContextMenu.PointIn(e, NextButton));
    }
}
