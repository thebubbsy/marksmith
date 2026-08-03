using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MarkSmith.Services;

public sealed record ExportStepMetric(string StepName, long DurationMs, long MemoryAllocatedBytes);

public sealed class ExportDiagnosticsSession : IDisposable
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly List<ExportStepMetric> _steps = new();
    private long _lastMemory = GC.GetTotalMemory(false);
    private long _lastTimeMs;

    public string ExportFormat { get; }

    public ExportDiagnosticsSession(string exportFormat)
    {
        ExportFormat = exportFormat ?? "pdf";
    }

    public void Step(string stepName)
    {
        var currentMs = _sw.ElapsedMilliseconds;
        var stepMs = currentMs - _lastTimeMs;
        _lastTimeMs = currentMs;

        var currentMem = GC.GetTotalMemory(false);
        var alloc = Math.Max(0, currentMem - _lastMemory);
        _lastMemory = currentMem;

        _steps.Add(new ExportStepMetric(stepName, stepMs, alloc));
    }

    public IReadOnlyList<ExportStepMetric> GetSteps() => _steps.ToList();

    public long TotalDurationMs => _sw.ElapsedMilliseconds;

    public void Dispose()
    {
        _sw.Stop();
    }
}

/// <summary>
/// Async Export Telemetry &amp; Detailed Metric Diagnostics (Task 26). Tracks latency breakdowns
/// and memory usage across export steps (Markdown parsing, diagram rendering, layout building, file I/O).
/// </summary>
public static class ExportDiagnosticsService
{
    public static ExportDiagnosticsSession StartSession(string exportFormat) => new(exportFormat);
}
