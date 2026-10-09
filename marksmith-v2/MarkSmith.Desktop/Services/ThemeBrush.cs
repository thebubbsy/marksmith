using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Services;

/// <summary>
/// The code-side equivalent of <c>{ThemeResource Key}</c>. <c>Application.Current.Resources[key]</c>
/// returns the brush of the theme the app STARTED in, so anything coloured that way kept its old
/// colours after Windows switched between light and dark while the app was open (the status line,
/// the find count, the line-number gutter, the expanded editor bar's dividers), and ignored any
/// element with its own theme (a light Galaxy map, a dialog themed to its window).
///
/// <see cref="Get"/> resolves a key for a given theme from the WinUI theme dictionaries;
/// <see cref="Set"/> / <see cref="Themed{T}"/> also keep the property in step with the element's
/// ActualTheme from then on. Use these for every theme brush set from code.
/// </summary>
public static class ThemeBrush
{
    private sealed class Bindings
    {
        public readonly Dictionary<DependencyProperty, string> Keys = new();
    }

    private static readonly ConditionalWeakTable<FrameworkElement, Bindings> Bound = new();
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();

    /// <summary>The brush <paramref name="key"/> names in <paramref name="theme"/> (Default = the app's).</summary>
    public static Brush Get(string key, ElementTheme theme)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return Fallback;

        // High contrast replaces both themes; the plain lookup already honours it.
        if (theme != ElementTheme.Default && !SafeHighContrast())
        {
            // WinUI's own dictionary calls dark "Default"; apps usually say "Dark".
            var names = theme == ElementTheme.Dark ? new[] { "Dark", "Default" } : new[] { "Light" };
            if (FindInThemes(resources, key, names, depth: 0) is Brush themed) return themed;
        }
        return resources.TryGetValue(key, out var plain) && plain is Brush brush ? brush : Fallback;
    }

    /// <summary>The brush <paramref name="key"/> names in the theme <paramref name="scope"/> is drawn in.</summary>
    public static Brush For(FrameworkElement scope, string key) => Get(key, scope.ActualTheme);

    /// <summary>Sets <paramref name="property"/> to the theme brush <paramref name="key"/> and keeps it
    /// following the element's theme. Setting another key for the same property replaces it.</summary>
    public static void Set(FrameworkElement element, DependencyProperty property, string key)
    {
        var bindings = Bound.GetValue(element, e =>
        {
            e.ActualThemeChanged += (s, _) => Refresh((FrameworkElement)s);
            // A new element reports the app's theme until it joins a tree with its own.
            e.Loaded += (s, _) => Refresh((FrameworkElement)s);
            return new Bindings();
        });
        bindings.Keys[property] = key;
        element.SetValue(property, For(element, key));
    }

    /// <summary>Stops following the theme for <paramref name="property"/> (call before setting a
    /// fixed value, or the next theme change puts the theme brush back).</summary>
    public static void Clear(FrameworkElement element, DependencyProperty property)
    {
        if (Bound.TryGetValue(element, out var bindings)) bindings.Keys.Remove(property);
    }

    /// <summary><see cref="Set"/> for object initialisers: <c>new TextBlock { … }.Themed(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush")</c>.</summary>
    public static T Themed<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        Set(element, property, key);
        return element;
    }

    private static void Refresh(FrameworkElement element)
    {
        if (!Bound.TryGetValue(element, out var bindings)) return;
        foreach (var (property, key) in bindings.Keys)
            element.SetValue(property, For(element, key));
    }

    private static object? FindInThemes(ResourceDictionary dictionary, string key, string[] themeNames, int depth)
    {
        foreach (var name in themeNames)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(name, out var t) && t is ResourceDictionary theme
                && theme.TryGetValue(key, out var value))
                return value;
        }
        // App.xaml's own theme dictionaries win over XamlControlsResources', like a ThemeResource.
        if (depth < 4)
        {
            for (int i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                if (FindInThemes(dictionary.MergedDictionaries[i], key, themeNames, depth + 1) is { } found)
                    return found;
            }
        }
        return null;
    }

    private static bool SafeHighContrast()
    {
        try { return Accessibility.HighContrast; }
        catch { return false; }
    }

    private static readonly SolidColorBrush Fallback = new(Microsoft.UI.Colors.Gray);
}

/// <summary>
/// Test hook: <c>MARKSMITH_THEME_FLIP=&lt;seconds&gt;</c> flips every window with a custom title bar
/// between light and dark on that interval, the way a Windows theme switch flips a running app.
/// Like MARKSMITH_THEME it is for test instances (a run can't switch Windows' own theme unattended),
/// not a user setting.
/// </summary>
internal static class ThemeFlipTestHook
{
    private static readonly List<WeakReference<Window>> Tracked = new();
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    public static void Track(Window window)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("MARKSMITH_THEME_FLIP"), out var seconds) || seconds <= 0)
            return;
        Tracked.Add(new WeakReference<Window>(window));
        if (_timer is not null) return;
        _timer = window.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Tick += (_, _) =>
        {
            foreach (var weak in Tracked)
            {
                if (weak.TryGetTarget(out var w) && w.Content is FrameworkElement root)
                    root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            }
        };
        _timer.Start();
    }
}
