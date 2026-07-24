using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MdToPdf.Services;

public sealed class WinUiWebRenderHost : IWebRenderHost
{
    private readonly MainWindow _window;
    private readonly WebView2 _webView;
    private readonly UIElement _spinner;
    private readonly CompositeTransform _spinnerTransform;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _spinTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _previewDebounce;
    private readonly ViewModels.MainViewModel _viewModel;

    private bool _mermaidHarvestActive;
    private bool _isPreviewRefreshing;
    private bool _pendingPreviewRefresh;
    private string? _lastRenderedMarkdown;
    private bool _spinActive;
    private bool _spinNavDone;
    private double _spinPhase;
    private int _spinMode;
    private readonly List<string> _sessionLogFiles = new();

    private const double SpinDt = 0.016; // roughly 60fps tick
    private const double SpinMinSec = 0.5;

    public WinUiWebRenderHost(
        MainWindow window,
        WebView2 webView,
        UIElement spinner,
        CompositeTransform spinnerTransform,
        Microsoft.UI.Dispatching.DispatcherQueueTimer spinTimer,
        Microsoft.UI.Dispatching.DispatcherQueueTimer previewDebounce,
        ViewModels.MainViewModel viewModel)
    {
        _window = window;
        _webView = webView;
        _spinner = spinner;
        _spinnerTransform = spinnerTransform;
        _spinTimer = spinTimer;
        _previewDebounce = previewDebounce;
        _viewModel = viewModel;

        _spinTimer.Tick += (_, _) => OnSpinTick();
    }

    public Task<bool> EnsureReadyAsync() => _window.EnsurePreviewWebViewAsync();

    public Task NavigateToStringAsync(string html)
    {
        var core = _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        var tcs = new TaskCompletionSource();
        void OnNavigationCompleted(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnNavigationCompleted;
            tcs.TrySetResult();
        }
        core.NavigationCompleted += OnNavigationCompleted;
        core.NavigateToString(html);
        return tcs.Task;
    }

    public async Task<string?> ExecuteScriptAsync(string javaScript)
    {
        var core = _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        return await core.ExecuteScriptAsync(javaScript);
    }

    public async Task<bool> PrintToPdfAsync(string outputPath, PdfPageSetup setup)
    {
        var core = _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        var printSettings = core.Environment.CreatePrintSettings();
        printSettings.ShouldPrintBackgrounds = setup.PrintBackgrounds;
        printSettings.ShouldPrintHeaderAndFooter = false;
        printSettings.PageWidth = setup.PageWidthIn;
        printSettings.PageHeight = setup.PageHeightIn;
        printSettings.MarginTop = setup.MarginTopIn;
        printSettings.MarginBottom = setup.MarginBottomIn;
        printSettings.MarginLeft = setup.MarginLeftIn;
        printSettings.MarginRight = setup.MarginRightIn;
        return await core.PrintToPdfAsync(outputPath, printSettings);
    }

    public Task BeginHarvestAsync()
    {
        _mermaidHarvestActive = true;
        _previewDebounce.Stop();
        return Task.CompletedTask;
    }

    public async Task EndHarvestAsync()
    {
        _mermaidHarvestActive = false;
        await RefreshPreviewAsync(false);
    }

    public async Task RefreshPreviewAsync(bool heavy = true)
    {
        if (_webView.CoreWebView2 is null) return;
        if (_mermaidHarvestActive) return; // snapshot renderer owns the WebView right now

        if (_isPreviewRefreshing)
        {
            _pendingPreviewRefresh = true;
            return;
        }

        _isPreviewRefreshing = true;
        _pendingPreviewRefresh = false;

        try
        {
            if (heavy) StartSpinner();

            var vm = _viewModel;
            string markdown;
            if (vm.UsePasteSource)
            {
                markdown = vm.PastedMarkdown;
            }
            else if (!string.IsNullOrWhiteSpace(vm.InputFilePath) && File.Exists(vm.InputFilePath))
            {
                // Offload file reading to background thread to prevent UI stutter
                markdown = await Task.Run(async () => await Plugins.PluginFileReader.ReadAsMarkdownAsync(vm.InputFilePath));
            }
            else
            {
                markdown = "# MarkSmith\n\nDrop a Markdown file on **1 · Source**, or switch to **Paste** and start typing.";
            }

            // Memoization: skip if the markdown hasn't changed AND this isn't a heavy refresh (e.g. theme change)
            if (!heavy && markdown == _lastRenderedMarkdown)
            {
                return;
            }

            // Move the heavy markdown parsing and HTML generation to a background thread
            var html = await Task.Run(() => 
            {
                var prepped = vm.PrepareMarkdown(markdown);
                return vm.BuildPreviewHtml(prepped, interactive: true);
            });
            
            _lastRenderedMarkdown = markdown;

            if (vm.IsDebugModeEnabled)
            {
                try
                {
                    var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith", "DebugLogs");
                    Directory.CreateDirectory(logsDir);
                    var logFile = Path.Combine(logsDir, $"Preview_Session_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("n").Substring(0, 4)}.log");
                    
                    var prompt = "Tell me everything wrong with the way we displayed the MD format in this HTML and how to resolve it:\n\n";
                    File.WriteAllText(logFile, prompt + html);
                    _sessionLogFiles.Add(logFile);
                }
                catch { }
            }

            // Heavy refreshes render blurred, then unblur when the spinner clears (see HideSpinner).
            if (heavy) html = html.Replace("<body>", "<body class=\"ms-loading\">");
            _webView.CoreWebView2.NavigateToString(html);
        }
        finally
        {
            _isPreviewRefreshing = false;
            if (_pendingPreviewRefresh)
            {
                // Trigger another refresh if changes happened while we were rendering
                _previewDebounce.Start();
            }
        }
    }

    private void StartSpinner()
    {
        if (_spinActive) { _spinNavDone = false; return; }

        _spinActive = true;
        _spinNavDone = false;
        _spinPhase = 0;
        _spinMode = 1 - _spinMode; // alternate: spin, then figure-eight, then spin…

        _spinnerTransform.TranslateX = 0;
        _spinnerTransform.TranslateY = 0;
        _spinnerTransform.Rotation = 0;

        _spinner.Visibility = Visibility.Visible;
        _spinTimer.Start();
    }

    private void OnSpinTick()
    {
        _spinPhase += SpinDt;

        if (_spinMode == 0)
        {
            // Spin the logo about its centre (RenderTransformOrigin 0.5,0.5).
            _spinnerTransform.Rotation = (_spinPhase * 300) % 360;
        }
        else
        {
            // Upright logo tracing a figure-eight (Gerono lemniscate: x = A sin t, y = (A/2) sin 2t).
            var t = _spinPhase * 2.4;
            const double a = 46;
            _spinnerTransform.TranslateX = a * Math.Sin(t);
            _spinnerTransform.TranslateY = a * 0.55 * Math.Sin(2 * t);
            _spinnerTransform.Rotation = 0; // stays upright
        }

        if (_spinNavDone && _spinPhase >= SpinMinSec) HideSpinner();
    }

    private void HideSpinner()
    {
        _spinTimer.Stop();
        _spinActive = false;
        _spinner.Visibility = Visibility.Collapsed;
        // Reveal the freshly-rendered content: the blur clears over a smooth transition as the sprite goes.
        _ = _webView.CoreWebView2?.ExecuteScriptAsync(
            "document.body && document.body.classList.remove('ms-loading')");
    }

    public void MarkNavigationDone()
    {
        _spinNavDone = true;
    }

    public (bool Hidden, Windows.Graphics.PointInt32 Pos) BeginOffscreenRender()
    {
        if (_window.AppWindow.IsVisible) return (false, default);
        var pos = _window.AppWindow.Position;
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        _window.AppWindow.Show(false); // no activation — focus stays wherever the user has it
        return (true, pos);
    }

    public void EndOffscreenRender((bool Hidden, Windows.Graphics.PointInt32 Pos) state)
    {
        if (!state.Hidden) return;
        _window.AppWindow.Hide();
        _window.AppWindow.Move(state.Pos);
        _window.AppWindow.IsShownInSwitchers = true;
    }

    public IReadOnlyList<string> SessionLogFiles => _sessionLogFiles;
}

public sealed class OffscreenScope : IDisposable
{
    private readonly WinUiWebRenderHost _host;
    private readonly (bool Hidden, Windows.Graphics.PointInt32 Pos) _state;

    public OffscreenScope(WinUiWebRenderHost host)
    {
        _host = host;
        _state = host.BeginOffscreenRender();
    }

    public void Dispose()
    {
        _host.EndOffscreenRender(_state);
    }
}
