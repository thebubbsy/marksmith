using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Ocr;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Streams;
using Windows.UI;

namespace MarkSmith.Controls;

/// <summary>
/// Live visual OCR &amp; document reader: displays the user's document, shows the engine breaking
/// apart the page into structural elements (XY-cut headings, lists, paragraphs, pictures, and
/// Mermaid/ShapeForge diagrams), identifies words across the page, pieces the blocks together in
/// reading order into Markdown, and hands off cleanly to the editor.
/// </summary>
public sealed partial class OcrExperienceOverlay : UserControl, IOcrObserver
{
    private static readonly FontFamily MonoFont = new("Consolas");

    private static readonly SolidColorBrush WordFlashFillBrush = new(Color.FromArgb(110, 26, 140, 255));
    private static readonly SolidColorBrush WordFlashStrokeBrush = new(Color.FromArgb(230, 71, 163, 255));
    private static readonly SolidColorBrush WordSettledFillBrush = new(Color.FromArgb(34, 26, 140, 255));
    private static readonly SolidColorBrush WordSettledStrokeBrush = new(Color.FromArgb(120, 26, 140, 255));

    private static readonly SolidColorBrush ActiveBlockStrokeBrush = new(Color.FromArgb(245, 26, 140, 255));
    private static readonly SolidColorBrush ActiveBlockFillBrush = new(Color.FromArgb(52, 26, 140, 255));
    private static readonly SolidColorBrush DoneBlockStrokeBrush = new(Color.FromArgb(150, 16, 185, 129));
    private static readonly SolidColorBrush DoneBlockFillBrush = new(Color.FromArgb(24, 16, 185, 129));
    private static readonly SolidColorBrush CutGuideStrokeBrush = new(Color.FromArgb(130, 26, 140, 255));

    private readonly DispatcherQueue _dispatcher;
    private readonly object _sync = new();
    private readonly Queue<(OcrPagePreview Preview, OcrPageTrace Trace)> _readyPages = new();
    private readonly Stopwatch _stage0Stopwatch = new();
    private OcrPagePreview? _pendingPreview;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _idleLaserCts;
    private Task? _stage0Task;
    private Task? _playbackTask;
    private bool _sessionActive;
    private bool _stage0ShownForCurrentSession;
    private bool _explicitCompletionAwaited;
    private bool _finalSheetShown;
    private int _pagesStarted;
    private int _pagesRead;
    private int _imagePixelWidth = 1;
    private int _imagePixelHeight = 1;
    private int _totalElements;
    private int _totalVisuals;
    private int _totalWords;
    private int _totalBlocks;
    private readonly List<string> _assembledPages = new();
    private readonly List<(FrameworkElement Element, OcrNormRect Box)> _positionedOverlays = new();
    private readonly List<(Line Line, float NormY)> _cutLines = new();
    private readonly List<Border> _blockFrames = new();
    private float _lastLaserNormY;

    /// <summary>Raised after the overlay finishes fading out to the editor.</summary>
    public event Action? Completed;

    public OcrExperienceOverlay()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Services.HoverPolish.Apply(this);
    }

    /// <summary>True when an import/OCR animation session is currently active.</summary>
    public bool HasActiveSession
    {
        get { lock (_sync) return _sessionActive; }
    }

    /// <summary>
    /// Marks the beginning of an explicit interactive import session before background file reading starts,
    /// ensuring the overlay waits for the caller's <see cref="PlayImportExperienceAndFadeToEditorAsync"/>
    /// rather than auto-closing early on multi-step imports.
    /// </summary>
    public void BeginImportSession()
    {
        lock (_sync)
        {
            _sessionActive = false;
            EnsureActiveSessionLocked(explicitAwait: true);
        }
    }

    private CancellationToken EnsureActiveSessionLocked(bool explicitAwait)
    {
        if (!_sessionActive || _sessionCts is null || _sessionCts.IsCancellationRequested)
        {
            _sessionActive = true;
            _explicitCompletionAwaited = explicitAwait;
            _finalSheetShown = false;
            _pagesStarted = 0;
            _pagesRead = 0;
            _sessionCts?.Cancel();
            _idleLaserCts?.Cancel();
            _sessionCts = new CancellationTokenSource();
            _playbackTask = null;
            _stage0Task = null;
            _stage0ShownForCurrentSession = false;
            _pendingPreview = null;
            _readyPages.Clear();
            _assembledPages.Clear();
            _totalElements = 0;
            _totalVisuals = 0;
            _totalWords = 0;
            _totalBlocks = 0;
        }
        else if (explicitAwait)
        {
            _explicitCompletionAwaited = true;
        }
        return _sessionCts.Token;
    }

    // ---- IOcrObserver (called from background OCR worker thread or UI thread) -------------------

    public void PageStarted(OcrPagePreview preview)
    {
        CancellationToken token;
        bool shouldShowStage0Now = false;
        lock (_sync)
        {
            token = EnsureActiveSessionLocked(explicitAwait: _explicitCompletionAwaited);
            _pagesStarted++;
            _pendingPreview = preview;
            if (!_stage0ShownForCurrentSession)
            {
                _stage0ShownForCurrentSession = true;
                shouldShowStage0Now = true;
            }
        }

        if (!shouldShowStage0Now) return;

        if (_dispatcher.HasThreadAccess)
        {
            if (!token.IsCancellationRequested)
                _stage0Task = ShowPageScanningStageAsync(preview, token);
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested) return;
                _stage0Task = ShowPageScanningStageAsync(preview, token);
            });
        }
    }

    public void PageRead(OcrPageTrace trace)
    {
        OcrPagePreview preview;
        CancellationToken token;
        lock (_sync)
        {
            if (!_sessionActive || _sessionCts is null) return;
            token = _sessionCts.Token;
            _pagesRead++;
            preview = _pendingPreview ?? new OcrPagePreview(
                trace.PageNumber, 0, 1, Array.Empty<byte>(), 1000, 1400, "MarkSmith OCR");
            _readyPages.Enqueue((preview, trace));
        }

        if (_dispatcher.HasThreadAccess)
        {
            StartPlaybackIfNeededOnUi(token);
        }
        else
        {
            _dispatcher.TryEnqueue(() => StartPlaybackIfNeededOnUi(token));
        }
    }

    private void StartPlaybackIfNeededOnUi(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        lock (_sync)
        {
            if (_playbackTask is null || _playbackTask.IsCompleted)
            {
                _playbackTask = RunPlaybackLoopAsync(token);
            }
        }
    }

    /// <summary>
    /// Plays the 4-stage visual breakdown and Markdown assembly animation for any imported document.
    /// If real OCR pages were already traced during this import, finishes playing those pages;
    /// otherwise synthesizes a document page preview and pixel-aligned word/element trace from
    /// <paramref name="markdown"/> so every import gets a consistent, smooth visual experience.
    /// </summary>
    public async Task PlayImportExperienceAndFadeToEditorAsync(
        string markdown,
        string sourceLabel,
        Action? applyToEditor = null,
        TimeSpan? elapsed = null)
    {
        bool needSynthesize = false;
        lock (_sync)
        {
            bool hasRealOcrPages = _sessionActive && (_pagesStarted > 0 || _readyPages.Count > 0 || _pendingPreview is not null || _stage0ShownForCurrentSession);
            if (!hasRealOcrPages && !string.IsNullOrWhiteSpace(markdown))
            {
                _sessionActive = false;
                EnsureActiveSessionLocked(explicitAwait: true);
                needSynthesize = true;
            }
            else if (_sessionActive)
            {
                _explicitCompletionAwaited = true;
            }
        }

        if (needSynthesize)
        {
            var (preview, trace) = await Task.Run(() => OcrTrace.SynthesizeFromMarkdown(markdown, sourceLabel, elapsed));
            PageStarted(preview);
            PageRead(trace);
        }

        await FinishAndFadeToEditorAsync(applyToEditor);
    }

    /// <summary>
    /// Waits for all queued page animations to complete, applies the resulting text to the editor,
    /// and smoothly closes the overlay to reveal the editor.
    /// </summary>
    public async Task FinishAndFadeToEditorAsync(Action? applyToEditor = null)
    {
        CancellationToken token;
        lock (_sync)
        {
            if (!_sessionActive)
            {
                applyToEditor?.Invoke();
                return;
            }
            _explicitCompletionAwaited = true;
            token = _sessionCts?.Token ?? CancellationToken.None;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_dispatcher.HasThreadAccess)
                {
                    StartPlaybackIfNeededOnUi(token);
                }

                Task? playback;
                lock (_sync) playback = _playbackTask;

                for (int wait = 0; playback is null && wait < 20 && !token.IsCancellationRequested; wait++)
                {
                    await Task.Delay(25, token);
                    lock (_sync) playback = _playbackTask;
                }

                if (playback is not null)
                {
                    await playback;
                }

                bool moreQueued;
                lock (_sync) moreQueued = _readyPages.Count > 0;
                if (!moreQueued) break;
            }

            bool needFinalSheet = false;
            lock (_sync)
            {
                if (!_finalSheetShown && _assembledPages.Count > 0)
                {
                    _finalSheetShown = true;
                    needFinalSheet = true;
                }
            }
            if (needFinalSheet && !token.IsCancellationRequested)
            {
                await PlayAct4FinalizeMarkdownPageAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // User pressed Esc / Skip to editor.
        }
        finally
        {
            applyToEditor?.Invoke();
            await FadeOutAndCloseAsync(token);
        }
    }

    /// <summary>Immediately closes the overlay if an import errored before completing.</summary>
    public void CancelAndHide()
    {
        lock (_sync)
        {
            _sessionActive = false;
            _explicitCompletionAwaited = false;
            _stage0ShownForCurrentSession = false;
            _finalSheetShown = false;
            _pagesStarted = 0;
            _pagesRead = 0;
            _pendingPreview = null;
            _playbackTask = null;
            _stage0Task = null;
            _sessionCts?.Cancel();
            _idleLaserCts?.Cancel();
            _readyPages.Clear();
        }
        _dispatcher.TryEnqueue(() =>
        {
            LaserBeam.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Collapsed;
            RootGrid.Opacity = 1;
            MarkdownScrollViewer.Visibility = Visibility.Visible;
            FinalMarkdownSheetBorder.Visibility = Visibility.Collapsed;
        });
    }

    // ---- Stage 0: Open Overlay & Show Initial Document Scan -------------------------------------

    private async Task ShowPageScanningStageAsync(OcrPagePreview preview, CancellationToken token)
    {
        _idleLaserCts?.Cancel();
        _idleLaserCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _stage0Stopwatch.Restart();

        if (Visibility != Visibility.Visible)
        {
            RootGrid.Opacity = 1;
            ElementsCountText.Text = "0";
            VisualsCountText.Text = "0";
            WordsCountText.Text = "0";
            BlocksCountText.Text = "0";
            MarkdownBlocksPanel.Children.Clear();
            MarkdownScrollViewer.Visibility = Visibility.Visible;
            FinalMarkdownSheetBorder.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Visible;
            SkipButton.Focus(FocusState.Programmatic);
        }

        EngineBadgeText.Text = string.IsNullOrWhiteSpace(preview.Engine) ? "MarkSmith OCR" : preview.Engine;
        PagePillText.Text = preview.PageCount > 1
            ? $"Page {preview.PageIndex + 1} of {preview.PageCount}"
            : $"Page {preview.PageNumber}";
        ScanDimensionsText.Text = $"{preview.ImageWidth} × {preview.ImageHeight} px";

        SetActiveStep(1);
        SetStatus($"Scanning & deskewing page {preview.PageNumber} with {EngineBadgeText.Text}…", 10);
        WordStreamText.Text = "Calibrating page geometry & binarising glyphs…";
        WordRateText.Text = "";
        BlocksProgressText.Text = "Analysing page structure…";

        ClearCanvasOverlays();

        if (preview.Image.Length > 0)
        {
            _imagePixelWidth = Math.Max(1, preview.ImageWidth);
            _imagePixelHeight = Math.Max(1, preview.ImageHeight);
            await LoadPreviewBitmapAsync(preview.Image);
            FitPageSheet();
        }

        // Sweep the scan line calmly at ~30fps while background OCR is still running.
        _ = RunIdleLaserSweepAsync(_idleLaserCts.Token);
    }

    private async Task LoadPreviewBitmapAsync(byte[] jpegBytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(jpegBytes.AsBuffer());
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            DocumentImage.Source = bmp;
        }
        catch
        {
            // Keep running animation even if preview image decode fails.
        }
    }

    private async Task RunIdleLaserSweepAsync(CancellationToken token)
    {
        LaserBeam.Visibility = Visibility.Visible;
        int step = 0;
        while (!token.IsCancellationRequested)
        {
            double phase = (Math.Sin(step * 0.16) + 1.0) * 0.5;
            PositionLaserAtNormalizedY((float)phase);
            step++;
            try { await Task.Delay(33, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- Main 4-Stage Deterministic Playback Loop -----------------------------------------------

    private async Task RunPlaybackLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                Task? stage0;
                lock (_sync) stage0 = _stage0Task;
                if (stage0 is not null)
                {
                    try { await stage0; } catch { }
                }

                (OcrPagePreview Preview, OcrPageTrace Trace)? nextItem = null;
                for (int wait = 0; wait < 100 && !token.IsCancellationRequested; wait++)
                {
                    lock (_sync)
                    {
                        if (_readyPages.Count > 0)
                        {
                            nextItem = _readyPages.Dequeue();
                            break;
                        }
                        if (_pagesRead >= _pagesStarted)
                        {
                            break;
                        }
                    }
                    await Task.Delay(50, token);
                }

                if (nextItem is not { } item) break;

                _idleLaserCts?.Cancel();

                if (item.Preview.PageIndex >= OcrPlaybackSchedule.MaxAnimatedPages)
                {
                    int blocks = item.Trace.Blocks.Count;
                    int visuals = item.Trace.Blocks.Count(b => b.Kind is OcrBlockKind.Picture or OcrBlockKind.Diagram or OcrBlockKind.ShapeMosaic);
                    int words = item.Trace.Words.Count;
                    _totalElements += blocks;
                    _totalVisuals += visuals;
                    _totalWords += words;
                    _totalBlocks += blocks;

                    ElementsCountText.Text = _totalElements.ToString();
                    VisualsCountText.Text = _totalVisuals.ToString();
                    WordsCountText.Text = _totalWords.ToString();
                    BlocksCountText.Text = _totalBlocks.ToString();
                    PagePillText.Text = item.Preview.PageCount > 1
                        ? $"Page {item.Preview.PageIndex + 1} of {item.Preview.PageCount}"
                        : $"Page {item.Preview.PageNumber}";

                    if (!string.IsNullOrWhiteSpace(item.Trace.Markdown))
                    {
                        _assembledPages.Add(item.Trace.Markdown);
                    }
                    continue;
                }

                int elapsedStage1 = item.Preview.PageIndex == 0
                    ? (int)_stage0Stopwatch.ElapsedMilliseconds
                    : 0;
                var schedule = OcrPlaybackSchedule.ForPage(
                    item.Trace.Blocks.Count,
                    item.Trace.Words.Count,
                    item.Preview.PageIndex,
                    item.Preview.PageCount,
                    elapsedStage1);

                if (item.Preview.PageIndex > 0 && item.Preview.Image.Length > 0)
                {
                    _imagePixelWidth = Math.Max(1, item.Preview.ImageWidth);
                    _imagePixelHeight = Math.Max(1, item.Preview.ImageHeight);
                    await LoadPreviewBitmapAsync(item.Preview.Image);
                    FitPageSheet();
                }

                EngineBadgeText.Text = string.IsNullOrWhiteSpace(item.Preview.Engine) ? "MarkSmith OCR" : item.Preview.Engine;
                PagePillText.Text = item.Preview.PageCount > 1
                    ? $"Page {item.Preview.PageIndex + 1} of {item.Preview.PageCount}"
                    : $"Page {item.Preview.PageNumber}";
                ScanDimensionsText.Text = $"{item.Preview.ImageWidth} × {item.Preview.ImageHeight} px";
                ElapsedText.Text = $"Engine read: {item.Trace.Elapsed.TotalMilliseconds:F0} ms";

                ClearCanvasOverlays();
                if (item.Preview.PageIndex == 0)
                {
                    MarkdownBlocksPanel.Children.Clear();
                    MarkdownScrollViewer.Visibility = Visibility.Visible;
                    FinalMarkdownSheetBorder.Visibility = Visibility.Collapsed;
                }

                // Stage 1: Complete the full top-to-bottom scan & deskew pass over a consistent duration
                await PlayStage1ScanSweepAsync(item.Preview, schedule, token);

                // Stage 2: Break apart the document & find structural elements
                await PlayAct1BreakApartElementsAsync(item.Trace, schedule, token);

                // Stage 3: Word stream & glyph recognition across the page
                await PlayAct2SpeedOfLightWordsAsync(item.Trace, schedule, token);

                // Stage 4: Piece together blocks of text in reading order into Markdown
                await PlayAct3PieceTogetherBlocksAsync(item.Trace, schedule, token);

                if (!string.IsNullOrWhiteSpace(item.Trace.Markdown))
                {
                    _assembledPages.Add(item.Trace.Markdown);
                }
            }

            // Final Summary Sheet: Show unified Markdown before handing off to editor
            bool showFinal = false;
            lock (_sync)
            {
                if (!token.IsCancellationRequested && !_finalSheetShown && _assembledPages.Count > 0 && _readyPages.Count == 0 && _pagesRead >= _pagesStarted)
                {
                    _finalSheetShown = true;
                    showFinal = true;
                }
            }
            if (showFinal)
            {
                await PlayAct4FinalizeMarkdownPageAsync(token);
            }

            // If triggered via ambient DefaultObserver where no caller awaited FinishAndFadeToEditorAsync,
            // auto-fade after the playback loop completes.
            await Task.Delay(80, token);
            bool shouldAutoFade;
            lock (_sync) shouldAutoFade = !_explicitCompletionAwaited && _sessionActive;
            if (shouldAutoFade)
            {
                await FadeOutAndCloseAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal when user clicks Skip to editor or presses Escape.
        }
    }

    // ---- Stage 1: Deterministic Scan & Deskew Pass ----------------------------------------------

    private async Task PlayStage1ScanSweepAsync(
        OcrPagePreview preview,
        OcrPlaybackSchedule schedule,
        CancellationToken token)
    {
        SetActiveStep(1);
        SetStatus($"Scanning & deskewing page {preview.PageNumber} with {EngineBadgeText.Text}…", 14);
        WordStreamText.Text = "Calibrating page geometry, deskew angle & optical contrast…";
        BlocksProgressText.Text = "Analysing page structure…";

        LaserBeam.Visibility = Visibility.Visible;
        const int sweepSteps = 10;
        int stepDelay = Math.Max(40, schedule.Stage1RemainingMs / sweepSteps);
        for (int s = 1; s <= sweepSteps; s++)
        {
            token.ThrowIfCancellationRequested();
            float normY = s / (float)sweepSteps;
            PositionLaserAtNormalizedY(normY);
            StageProgressBar.Value = 6 + (14.0 * s / sweepSteps);
            await Task.Delay(stepDelay, token);
        }
    }

    // ---- Stage 2: Breaking Apart the Document & Finding the Elements ----------------------------

    private async Task PlayAct1BreakApartElementsAsync(
        OcrPageTrace trace,
        OcrPlaybackSchedule schedule,
        CancellationToken token)
    {
        SetActiveStep(2);
        int blockCount = trace.Blocks.Count;
        SetStatus(blockCount == 0
            ? "Scanning page regions…"
            : $"Breaking apart document — locating {blockCount} structural element{(blockCount == 1 ? "" : "s")}…", 26);
        WordStreamText.Text = "XY-cut layout segmentation: splitting columns, headings, lists & paragraphs…";

        LaserBeam.Visibility = Visibility.Visible;
        _blockFrames.Clear();

        if (blockCount == 0)
        {
            await Task.Delay(schedule.Stage2TotalMs, token);
            return;
        }

        int steps = schedule.Stage2Steps;
        OcrNormRect? prevBox = null;

        for (int step = 0; step < steps; step++)
        {
            token.ThrowIfCancellationRequested();
            int startIdx = (int)Math.Round(step * blockCount / (double)steps);
            int endIdx = (int)Math.Round((step + 1) * blockCount / (double)steps);
            if (endIdx <= startIdx) endIdx = Math.Min(blockCount, startIdx + 1);

            OcrTraceBlock lastBlock = trace.Blocks[Math.Min(blockCount - 1, endIdx - 1)];

            for (int i = startIdx; i < endIdx; i++)
            {
                var block = trace.Blocks[i];
                if (prevBox is { } prev && block.Box.Y > prev.Bottom + 0.008f)
                {
                    float cutY = (prev.Bottom + block.Box.Y) * 0.5f;
                    AddSegmentationCutLine(cutY);
                }

                var frame = CreateElementFrame(block);
                _blockFrames.Add(frame);
                AddPositionedOverlay(frame, block.Box);

                _totalElements++;
                if (block.Kind is OcrBlockKind.Picture or OcrBlockKind.Diagram or OcrBlockKind.ShapeMosaic)
                {
                    _totalVisuals++;
                }
                prevBox = block.Box;
            }

            ElementsCountText.Text = _totalElements.ToString();
            VisualsCountText.Text = _totalVisuals.ToString();
            PositionLaserAtNormalizedY(lastBlock.Box.Bottom);

            WordStreamText.Text = lastBlock.Kind switch
            {
                OcrBlockKind.Diagram => "⬡ Flowchart diagram detected → routing to Mermaid AST + ShapeForge Word Shapes…",
                OcrBlockKind.ShapeMosaic => "❖ Graphic region detected → composing multi-shape Word mosaic via ShapeForge…",
                OcrBlockKind.Picture => "🖼 Photograph detected → cropping & linking PNG to document media…",
                OcrBlockKind.Heading => $"Located heading region #{endIdx} of {blockCount}…",
                OcrBlockKind.List => $"Located list structure #{endIdx} of {blockCount}…",
                _ => $"Segmented layout element {endIdx} of {blockCount}…",
            };
            StageProgressBar.Value = 20 + (22.0 * (step + 1) / steps);

            await Task.Delay(schedule.Stage2StepDelayMs, token);
        }

        if (schedule.Stage2HoldMs > 0)
        {
            await Task.Delay(schedule.Stage2HoldMs, token);
        }
    }

    // ---- Stage 3: Word Stream & Glyph Recognition -----------------------------------------------

    private async Task PlayAct2SpeedOfLightWordsAsync(
        OcrPageTrace trace,
        OcrPlaybackSchedule schedule,
        CancellationToken token)
    {
        SetActiveStep(3);
        int wordCount = trace.Words.Count;
        SetStatus(wordCount == 0
            ? "Analysing glyph stream…"
            : $"Recognising all {wordCount} words across the page…", 48);

        // Cap rendered word-box overlays at 56 lightweight Rectangles so the Canvas never lags.
        const int maxWordRectangles = 56;
        int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(1, wordCount) / (double)maxWordRectangles));
        int steps = schedule.Stage3Steps;
        var prevFrameRects = new List<Rectangle>(12);
        var sw = Stopwatch.StartNew();

        for (int step = 0; step < steps; step++)
        {
            token.ThrowIfCancellationRequested();

            foreach (var old in prevFrameRects)
            {
                old.Fill = WordSettledFillBrush;
                old.Stroke = WordSettledStrokeBrush;
            }
            prevFrameRects.Clear();

            int start = (int)Math.Round(step * wordCount / (double)steps);
            int end = (int)Math.Round((step + 1) * wordCount / (double)steps);

            float laserY = (step + 1) / (float)steps;
            for (int w = start; w < end; w++)
            {
                var word = trace.Words[w];
                laserY = word.Box.Bottom;
                if (w % stride == 0 && word.Box.Width > 0 && word.Box.Height > 0)
                {
                    var rect = new Rectangle
                    {
                        Fill = WordFlashFillBrush,
                        Stroke = WordFlashStrokeBrush,
                        StrokeThickness = 1,
                        RadiusX = 2,
                        RadiusY = 2,
                    };
                    AddPositionedOverlay(rect, word.Box);
                    prevFrameRects.Add(rect);
                }
            }

            PositionLaserAtNormalizedY(laserY);

            int doneThisPage = end;
            WordsCountText.Text = (_totalWords + doneThisPage).ToString();

            if (end > 0)
            {
                int sampleStart = Math.Max(0, end - 8);
                WordStreamText.Text = string.Join(" · ", trace.Words.Skip(sampleStart).Take(end - sampleStart).Select(x => x.Text));
            }
            else
            {
                WordStreamText.Text = "Scanning visual geometry & shape primitives…";
            }

            double elapsedSec = Math.Max(0.08, sw.Elapsed.TotalSeconds);
            int rate = (int)Math.Round(doneThisPage / elapsedSec);
            WordRateText.Text = wordCount > 0 ? $"{rate:N0} words/s" : "";
            StageProgressBar.Value = 42 + (26.0 * (step + 1) / steps);

            await Task.Delay(schedule.Stage3StepDelayMs, token);
        }

        foreach (var old in prevFrameRects)
        {
            old.Fill = WordSettledFillBrush;
            old.Stroke = WordSettledStrokeBrush;
        }

        _totalWords += wordCount;
        WordsCountText.Text = _totalWords.ToString();
        LaserBeam.Visibility = Visibility.Collapsed;

        if (schedule.Stage3HoldMs > 0)
        {
            await Task.Delay(schedule.Stage3HoldMs, token);
        }
    }

    // ---- Stage 4: Piecing Together the Blocks of Text in Order into Markdown --------------------

    private async Task PlayAct3PieceTogetherBlocksAsync(
        OcrPageTrace trace,
        OcrPlaybackSchedule schedule,
        CancellationToken token)
    {
        SetActiveStep(4);
        int blockCount = trace.Blocks.Count;
        SetStatus(blockCount == 0
            ? "Formatting page as Markdown…"
            : $"Piecing together {blockCount} block{(blockCount == 1 ? "" : "s")} in reading order into Markdown…", 72);

        if (blockCount == 0)
        {
            BlocksProgressText.Text = "No text blocks on this page";
            await Task.Delay(schedule.Stage4TotalMs, token);
            return;
        }

        int steps = schedule.Stage4Steps;
        int prevEndIdx = 0;

        for (int step = 0; step < steps; step++)
        {
            token.ThrowIfCancellationRequested();
            int startIdx = (int)Math.Round(step * blockCount / (double)steps);
            int endIdx = (int)Math.Round((step + 1) * blockCount / (double)steps);
            if (endIdx <= startIdx) endIdx = Math.Min(blockCount, startIdx + 1);

            // Cool previously highlighted source blocks
            for (int p = 0; p < startIdx && p < _blockFrames.Count; p++)
            {
                _blockFrames[p].BorderBrush = DoneBlockStrokeBrush;
                _blockFrames[p].Background = DoneBlockFillBrush;
                _blockFrames[p].BorderThickness = new Thickness(1.2);
            }

            // Highlight current batch of source blocks being lifted from the document canvas
            for (int curr = startIdx; curr < endIdx && curr < _blockFrames.Count; curr++)
            {
                _blockFrames[curr].BorderBrush = ActiveBlockStrokeBrush;
                _blockFrames[curr].Background = ActiveBlockFillBrush;
                _blockFrames[curr].BorderThickness = new Thickness(2.0);
            }

            var primaryBlock = trace.Blocks[startIdx];
            int batchCount = endIdx - startIdx;
            _totalBlocks += batchCount;

            PulseCenterBridge(_totalBlocks, primaryBlock.Kind);

            // Add at most 1 lightweight card per step (maximum 8 cards per page) with no UpdateLayout()
            var card = CreateMarkdownBlockCard(primaryBlock, _totalBlocks, batchCount);
            MarkdownBlocksPanel.Children.Add(card);
            MarkdownScrollViewer.ChangeView(null, 99999.0, null, disableAnimation: true);

            BlocksCountText.Text = _totalBlocks.ToString();
            BlocksProgressText.Text = $"{endIdx} / {blockCount} blocks pieced";
            StageProgressBar.Value = 68 + (26.0 * (step + 1) / steps);
            prevEndIdx = endIdx;

            await Task.Delay(schedule.Stage4StepDelayMs, token);
        }

        for (int p = 0; p < prevEndIdx && p < _blockFrames.Count; p++)
        {
            _blockFrames[p].BorderBrush = DoneBlockStrokeBrush;
            _blockFrames[p].Background = DoneBlockFillBrush;
            _blockFrames[p].BorderThickness = new Thickness(1.2);
        }

        if (schedule.Stage4HoldMs > 0)
        {
            await Task.Delay(schedule.Stage4HoldMs, token);
        }
    }

    // ---- Final Hold: Unified Assembled Markdown Sheet & Hand-Off to Editor ----------------------

    private async Task PlayAct4FinalizeMarkdownPageAsync(CancellationToken token)
    {
        var fullMarkdown = string.Join("\n\n", _assembledPages).Trim();
        FinalMarkdownTextBlock.Text = TruncateForFinalPreview(fullMarkdown);

        string visualsNote = _totalVisuals > 0
            ? $", {_totalVisuals} visual/shape element{(_totalVisuals == 1 ? "" : "s")}"
            : "";
        FinalBannerText.Text = $"✓ {_totalBlocks} block{(_totalBlocks == 1 ? "" : "s")} ({_totalWords} words{visualsNote}) assembled in Markdown — opening in editor…";

        MarkdownScrollViewer.Visibility = Visibility.Collapsed;
        FinalMarkdownSheetBorder.Visibility = Visibility.Visible;

        SetStatus("Page assembled in Markdown — handing off to the editor…", 100);
        WordStreamText.Text = $"Complete · {_totalWords} words{visualsNote} pieced into {_totalBlocks} Markdown block{(_totalBlocks == 1 ? "" : "s")}";
        BlocksProgressText.Text = "✓ Assembled in .md";

        await Task.Delay(950, token);
    }

    private static string TruncateForFinalPreview(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        const int maxLines = 22;
        const int maxChars = 1400;
        if (lines.Length <= maxLines && markdown.Length <= maxChars)
            return markdown;

        var preview = string.Join("\n", lines.Take(maxLines));
        if (preview.Length > maxChars)
            preview = preview[..maxChars];
        int remainingLines = Math.Max(1, lines.Length - maxLines);
        return $"{preview.TrimEnd()}\n\n… (+{remainingLines} more line{(remainingLines == 1 ? "" : "s")} — opening full document in editor)";
    }

    private async Task FadeOutAndCloseAsync(CancellationToken token)
    {
        _idleLaserCts?.Cancel();
        LaserBeam.Visibility = Visibility.Collapsed;

        if (!token.IsCancellationRequested)
        {
            RootGrid.Opacity = 0.72;
            await Task.Delay(60);
            RootGrid.Opacity = 0.42;
            await Task.Delay(60);
            RootGrid.Opacity = 0.14;
            await Task.Delay(60);
        }

        Visibility = Visibility.Collapsed;
        RootGrid.Opacity = 1;
        MarkdownScrollViewer.Visibility = Visibility.Visible;
        FinalMarkdownSheetBorder.Visibility = Visibility.Collapsed;

        lock (_sync)
        {
            _sessionActive = false;
            _explicitCompletionAwaited = false;
            _stage0ShownForCurrentSession = false;
            _finalSheetShown = false;
            _pagesStarted = 0;
            _pagesRead = 0;
            _pendingPreview = null;
            _playbackTask = null;
            _stage0Task = null;
            _readyPages.Clear();
        }

        Completed?.Invoke();
    }

    // ---- Visual Primitives & Direct Canvas Coordinate Layout ------------------------------------

    private void OnPageViewportSizeChanged(object sender, SizeChangedEventArgs e) => FitPageSheet();

    private void FitPageSheet()
    {
        double availW = PageViewportGrid.ActualWidth;
        double availH = PageViewportGrid.ActualHeight;
        if (availW <= 16 || availH <= 16) return;

        double aspect = _imagePixelWidth / (double)Math.Max(1, _imagePixelHeight);
        double fittedW = availW;
        double fittedH = fittedW / aspect;
        if (fittedH > availH)
        {
            fittedH = availH;
            fittedW = fittedH * aspect;
        }

        fittedW = Math.Max(40, Math.Floor(fittedW));
        fittedH = Math.Max(40, Math.Floor(fittedH));

        PageSheetGrid.Width = fittedW;
        PageSheetGrid.Height = fittedH;
        ScanOverlayCanvas.Width = fittedW;
        ScanOverlayCanvas.Height = fittedH;
        LaserCanvas.Width = fittedW;
        LaserCanvas.Height = fittedH;
        LaserBeam.Width = fittedW;

        foreach (var (element, box) in _positionedOverlays)
        {
            PositionOnCanvas(element, box, fittedW, fittedH);
        }

        foreach (var (line, normY) in _cutLines)
        {
            double y = Math.Clamp(normY * fittedH, 0, fittedH);
            line.X1 = 6;
            line.X2 = Math.Max(12, fittedW - 6);
            line.Y1 = y;
            line.Y2 = y;
        }

        PositionLaserAtNormalizedY(_lastLaserNormY);
    }

    private void AddPositionedOverlay(FrameworkElement element, OcrNormRect box)
    {
        double cw = ScanOverlayCanvas.Width > 0 ? ScanOverlayCanvas.Width : Math.Max(100, PageViewportGrid.ActualWidth);
        double ch = ScanOverlayCanvas.Height > 0 ? ScanOverlayCanvas.Height : Math.Max(100, PageViewportGrid.ActualHeight);
        PositionOnCanvas(element, box, cw, ch);
        _positionedOverlays.Add((element, box));
        ScanOverlayCanvas.Children.Add(element);
    }

    private static void PositionOnCanvas(FrameworkElement element, OcrNormRect box, double canvasW, double canvasH)
    {
        double left = Math.Clamp(box.X * canvasW - 2, 0, Math.Max(0, canvasW - 4));
        double top = Math.Clamp(box.Y * canvasH - 2, 0, Math.Max(0, canvasH - 4));
        double width = Math.Max(6, Math.Min(canvasW - left, box.Width * canvasW + 4));
        double height = Math.Max(6, Math.Min(canvasH - top, box.Height * canvasH + 4));

        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = width;
        element.Height = height;
    }

    private void AddSegmentationCutLine(float normalizedY)
    {
        double cw = ScanOverlayCanvas.Width > 0 ? ScanOverlayCanvas.Width : Math.Max(100, PageViewportGrid.ActualWidth);
        double ch = ScanOverlayCanvas.Height > 0 ? ScanOverlayCanvas.Height : Math.Max(100, PageViewportGrid.ActualHeight);
        double y = Math.Clamp(normalizedY * ch, 0, ch);
        var line = new Line
        {
            X1 = 6,
            X2 = Math.Max(12, cw - 6),
            Y1 = y,
            Y2 = y,
            Stroke = CutGuideStrokeBrush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        _cutLines.Add((line, normalizedY));
        ScanOverlayCanvas.Children.Add(line);
    }

    private static Border CreateElementFrame(OcrTraceBlock block)
    {
        var (strokeColor, fillColor, label) = block.Kind switch
        {
            OcrBlockKind.Heading => (Color.FromArgb(225, 217, 119, 6), Color.FromArgb(28, 245, 158, 11), $"H · #{block.Index + 1}"),
            OcrBlockKind.List => (Color.FromArgb(225, 5, 150, 105), Color.FromArgb(28, 16, 185, 129), $"LIST · #{block.Index + 1}"),
            OcrBlockKind.Picture => (Color.FromArgb(225, 225, 29, 72), Color.FromArgb(30, 244, 63, 94), $"PICTURE · #{block.Index + 1}"),
            OcrBlockKind.Diagram => (Color.FromArgb(225, 124, 58, 237), Color.FromArgb(32, 139, 92, 246), $"DIAGRAM · #{block.Index + 1}"),
            OcrBlockKind.ShapeMosaic => (Color.FromArgb(225, 219, 39, 119), Color.FromArgb(30, 236, 72, 153), $"SHAPE MOSAIC · #{block.Index + 1}"),
            _ => (Color.FromArgb(215, 26, 140, 255), Color.FromArgb(24, 26, 140, 255), $"BLOCK · #{block.Index + 1}"),
        };

        var tagBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(3, 0, 4, 0),
            Padding = new Thickness(4, 0, 5, 1),
            Background = new SolidColorBrush(strokeColor),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 9,
                FontFamily = MonoFont,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            }
        };

        return new Border
        {
            BorderBrush = new SolidColorBrush(strokeColor),
            Background = new SolidColorBrush(fillColor),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            Child = tagBadge,
        };
    }

    private Border CreateMarkdownBlockCard(OcrTraceBlock block, int orderNumber, int batchCount)
    {
        bool isLight = ActualTheme == ElementTheme.Light;
        var (accentColor, kindLabel, metaLabel) = block.Kind switch
        {
            OcrBlockKind.Heading => (
                isLight ? Color.FromArgb(255, 180, 83, 9) : Color.FromArgb(255, 251, 191, 36),
                "# HEADING",
                $"{block.WordCount} word{(block.WordCount == 1 ? "" : "s")}"),
            OcrBlockKind.List => (
                isLight ? Color.FromArgb(255, 5, 150, 105) : Color.FromArgb(255, 52, 211, 153),
                "• LIST",
                $"{block.WordCount} word{(block.WordCount == 1 ? "" : "s")}"),
            OcrBlockKind.Picture => (
                isLight ? Color.FromArgb(255, 190, 18, 60) : Color.FromArgb(255, 251, 113, 133),
                "🖼 PICTURE",
                "Extracted PNG"),
            OcrBlockKind.Diagram => (
                isLight ? Color.FromArgb(255, 109, 40, 217) : Color.FromArgb(255, 192, 132, 252),
                "⬡ DIAGRAM → SHAPES + MERMAID",
                block.WordCount > 0 ? $"{block.WordCount} label word{(block.WordCount == 1 ? "" : "s")}" : "Word Shapes"),
            OcrBlockKind.ShapeMosaic => (
                isLight ? Color.FromArgb(255, 190, 24, 93) : Color.FromArgb(255, 244, 114, 182),
                "❖ SHAPEFORGE MOSAIC",
                "Word Shape Mosaic"),
            _ => (
                isLight ? Color.FromArgb(255, 0, 104, 201) : Color.FromArgb(255, 71, 163, 255),
                batchCount > 1 ? $"¶ {batchCount} BLOCKS" : "¶ PARAGRAPH",
                $"{block.WordCount} word{(block.WordCount == 1 ? "" : "s")}"),
        };

        var headerRow = new Grid { ColumnSpacing = 8 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var orderPill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Background = new SolidColorBrush(Color.FromArgb(32, accentColor.R, accentColor.G, accentColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, accentColor.R, accentColor.G, accentColor.B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = $"#{orderNumber}",
                FontSize = 10.5,
                FontFamily = MonoFont,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accentColor),
            }
        };

        var kindText = new TextBlock
        {
            Text = kindLabel,
            FontSize = 10.5,
            FontFamily = MonoFont,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accentColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(kindText, 1);

        var secondaryFg = isLight
            ? Color.FromArgb(255, 92, 92, 92)
            : Color.FromArgb(255, 166, 166, 166);
        var wordsMeta = new TextBlock
        {
            Text = metaLabel,
            FontSize = 10.5,
            FontFamily = MonoFont,
            Foreground = new SolidColorBrush(secondaryFg),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(wordsMeta, 2);

        headerRow.Children.Add(orderPill);
        headerRow.Children.Add(kindText);
        headerRow.Children.Add(wordsMeta);

        var primaryFg = isLight
            ? Color.FromArgb(255, 27, 27, 27)
            : Color.FromArgb(255, 243, 243, 243);
        var mdBody = new TextBlock
        {
            Text = FormatCardPreviewMarkdown(block),
            FontFamily = MonoFont,
            FontSize = block.Kind == OcrBlockKind.Heading ? 12.5 : 11.5,
            FontWeight = block.Kind == OcrBlockKind.Heading ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(primaryFg),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 18,
        };

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(headerRow);
        if (BuildVisualFeatureShowcaseStrip(block, isLight) is { } showcase)
        {
            stack.Children.Add(showcase);
        }
        stack.Children.Add(mdBody);

        // Match MainWindow's neutral WinUI 3 secondary card surface instead of navy slate
        var cardBg = isLight
            ? Color.FromArgb(255, 249, 249, 249)
            : Color.FromArgb(255, 43, 43, 43);
        var cardStroke = isLight
            ? Color.FromArgb(255, 224, 224, 224)
            : Color.FromArgb(255, 64, 64, 64);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 8, 11, 9),
            Background = new SolidColorBrush(cardBg),
            BorderBrush = new SolidColorBrush(cardStroke),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private static FrameworkElement? BuildVisualFeatureShowcaseStrip(OcrTraceBlock block, bool isLight)
    {
        var accentBlue = isLight ? Color.FromArgb(255, 0, 104, 201) : Color.FromArgb(255, 71, 163, 255);
        if (block.Kind == OcrBlockKind.Diagram)
        {
            var diagAccent = isLight ? Color.FromArgb(255, 109, 40, 217) : Color.FromArgb(255, 192, 132, 252);
            var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            toolRow.Children.Add(CreateEngineChip("⬡ Mermaid Flowchart AST", diagAccent));
            toolRow.Children.Add(CreateEngineChip("❖ ShapeForge Word Shapes", accentBlue));
            return toolRow;
        }

        if (block.Kind == OcrBlockKind.ShapeMosaic)
        {
            var mosaicAccent = isLight ? Color.FromArgb(255, 190, 24, 93) : Color.FromArgb(255, 244, 114, 182);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(CreateEngineChip("❖ ShapeForge Picture Mode", mosaicAccent));
            row.Children.Add(CreateEngineChip("Word DrawingML Mosaic", accentBlue));
            return row;
        }

        if (block.Kind == OcrBlockKind.Picture)
        {
            var picAccent = isLight ? Color.FromArgb(255, 190, 18, 60) : Color.FromArgb(255, 251, 113, 133);
            var okAccent = isLight ? Color.FromArgb(255, 5, 150, 105) : Color.FromArgb(255, 52, 211, 153);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(CreateEngineChip("🖼 Picture Extractor", picAccent));
            row.Children.Add(CreateEngineChip("Cropped PNG → _media", okAccent));
            return row;
        }

        return null;
    }

    private static Border CreateEngineChip(string text, Color color) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 2),
        Background = new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontFamily = MonoFont,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
        }
    };

    private static string FormatCardPreviewMarkdown(OcrTraceBlock block)
    {
        if (string.IsNullOrEmpty(block.Markdown)) return string.Empty;
        if (block.Kind == OcrBlockKind.ShapeMosaic && block.Markdown.Length > 140)
        {
            var lines = block.Markdown.Split('\n');
            if (lines.Length >= 2 && lines[1].Length > 72)
            {
                return $"{lines[0].TrimEnd('\r')}\n{lines[1][..64]}… ({block.Markdown.Length:N0} chars)\n:::";
            }
        }
        if (block.Markdown.Length > 180)
        {
            return block.Markdown[..180].TrimEnd() + "…";
        }
        return block.Markdown;
    }

    private void PulseCenterBridge(int blockNumber, OcrBlockKind kind)
    {
        bool isLight = ActualTheme == ElementTheme.Light;
        var (color, targetSuffix) = kind switch
        {
            OcrBlockKind.Heading => (isLight ? Color.FromArgb(255, 180, 83, 9) : Color.FromArgb(255, 251, 191, 36), ".md"),
            OcrBlockKind.List => (isLight ? Color.FromArgb(255, 5, 150, 105) : Color.FromArgb(255, 52, 211, 153), ".md"),
            OcrBlockKind.Picture => (isLight ? Color.FromArgb(255, 190, 18, 60) : Color.FromArgb(255, 251, 113, 133), "img"),
            OcrBlockKind.Diagram => (isLight ? Color.FromArgb(255, 109, 40, 217) : Color.FromArgb(255, 192, 132, 252), "shapes"),
            OcrBlockKind.ShapeMosaic => (isLight ? Color.FromArgb(255, 190, 24, 93) : Color.FromArgb(255, 244, 114, 182), "mosaic"),
            _ => (isLight ? Color.FromArgb(255, 0, 104, 201) : Color.FromArgb(255, 71, 163, 255), ".md"),
        };
        BridgePulseBorder.BorderBrush = new SolidColorBrush(color);
        BridgePulseBorder.Background = new SolidColorBrush(Color.FromArgb(32, color.R, color.G, color.B));
        BridgeIcon.Foreground = new SolidColorBrush(color);
        BridgeBlockText.Text = $"#{blockNumber} → {targetSuffix}";
        BridgeBlockText.Foreground = new SolidColorBrush(color);
    }

    private void PositionLaserAtNormalizedY(float normY)
    {
        _lastLaserNormY = Math.Clamp(normY, 0f, 1f);
        double h = ScanOverlayCanvas.Height > 0 ? ScanOverlayCanvas.Height : Math.Max(60, PageViewportGrid.ActualHeight);
        LaserTransform.Y = Math.Clamp(_lastLaserNormY * h, 0, Math.Max(0, h - 3));
    }

    private void ClearCanvasOverlays()
    {
        _positionedOverlays.Clear();
        _cutLines.Clear();
        _blockFrames.Clear();
        ScanOverlayCanvas.Children.Clear();
    }

    private void SetActiveStep(int step)
    {
        bool isLight = ActualTheme == ElementTheme.Light;
        StyleStepPill(Step1Pill, Step1Icon, Step1Text, step == 1, step > 1, isLight);
        StyleStepPill(Step2Pill, Step2Icon, Step2Text, step == 2, step > 2, isLight);
        StyleStepPill(Step3Pill, Step3Icon, Step3Text, step == 3, step > 3, isLight);
        StyleStepPill(Step4Pill, Step4Icon, Step4Text, step == 4, step > 4, isLight);
    }

    private static void StyleStepPill(Border pill, FontIcon icon, TextBlock label, bool active, bool completed, bool isLight)
    {
        var accent = isLight ? Color.FromArgb(255, 0, 104, 201) : Color.FromArgb(255, 71, 163, 255);
        var success = isLight ? Color.FromArgb(255, 5, 150, 105) : Color.FromArgb(255, 52, 211, 153);
        var primaryText = isLight ? Color.FromArgb(255, 27, 27, 27) : Color.FromArgb(255, 243, 243, 243);
        var secondaryText = isLight ? Color.FromArgb(255, 96, 96, 96) : Color.FromArgb(255, 166, 166, 166);
        var inactiveBg = isLight ? Color.FromArgb(255, 243, 243, 243) : Color.FromArgb(255, 39, 39, 39);
        var inactiveBorder = isLight ? Color.FromArgb(255, 224, 224, 224) : Color.FromArgb(255, 58, 58, 58);

        if (active)
        {
            pill.Background = new SolidColorBrush(Color.FromArgb(32, accent.R, accent.G, accent.B));
            pill.BorderBrush = new SolidColorBrush(accent);
            icon.Foreground = new SolidColorBrush(accent);
            label.Foreground = new SolidColorBrush(primaryText);
            label.FontWeight = FontWeights.SemiBold;
        }
        else if (completed)
        {
            pill.Background = new SolidColorBrush(Color.FromArgb(22, success.R, success.G, success.B));
            pill.BorderBrush = new SolidColorBrush(Color.FromArgb(110, success.R, success.G, success.B));
            icon.Foreground = new SolidColorBrush(success);
            label.Foreground = new SolidColorBrush(primaryText);
            label.FontWeight = FontWeights.Normal;
        }
        else
        {
            pill.Background = new SolidColorBrush(inactiveBg);
            pill.BorderBrush = new SolidColorBrush(inactiveBorder);
            icon.Foreground = new SolidColorBrush(secondaryText);
            label.Foreground = new SolidColorBrush(secondaryText);
            label.FontWeight = FontWeights.Normal;
        }
    }

    private void SetStatus(string status, double progress)
    {
        StageStatusText.Text = status;
        AutomationProperties.SetName(StageStatusText, status);
        StageProgressBar.Value = progress;
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => SkipAnimation();

    private void OnOverlayKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            SkipAnimation();
        }
    }

    private void SkipAnimation()
    {
        lock (_sync)
        {
            _idleLaserCts?.Cancel();
            _sessionCts?.Cancel();
        }
    }
}
