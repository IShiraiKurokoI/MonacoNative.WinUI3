using System.Diagnostics;

namespace MonacoNative.Renderer.WinUI3;

internal readonly record struct RendererPerformanceSnapshot(
    long RenderRequests,
    long CoalescedRenderRequests,
    long Frames,
    long TextLayerRebuilds,
    long VisibleTextLinesVisited,
    long TotalFrameTicks,
    long TotalTextLayerTicks,
    long LastFrameTicks,
    long LastTextLayerTicks)
{
    public double TotalFrameMilliseconds => TotalFrameTicks * 1000.0 / Stopwatch.Frequency;
    public double TotalTextLayerMilliseconds => TotalTextLayerTicks * 1000.0 / Stopwatch.Frequency;
    public double LastFrameMilliseconds => LastFrameTicks * 1000.0 / Stopwatch.Frequency;
    public double LastTextLayerMilliseconds => LastTextLayerTicks * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Low-overhead renderer counters used by renderer performance probes and runtime diagnostics.
/// Timings are descriptive only; release gates use deterministic work counts.
/// </summary>
internal sealed class RendererPerformanceCounters
{
    private long _renderRequests;
    private long _coalescedRenderRequests;
    private long _frames;
    private long _textLayerRebuilds;
    private long _visibleTextLinesVisited;
    private long _totalFrameTicks;
    private long _totalTextLayerTicks;
    private long _lastFrameTicks;
    private long _lastTextLayerTicks;

    public void NoteRenderRequest(bool coalesced)
    {
        _renderRequests++;
        if (coalesced)
            _coalescedRenderRequests++;
    }

    public void NoteFrame(long elapsedTicks)
    {
        _frames++;
        _lastFrameTicks = elapsedTicks;
        _totalFrameTicks += elapsedTicks;
    }

    public void NoteTextLayer(long elapsedTicks, int visibleLinesVisited)
    {
        _textLayerRebuilds++;
        _lastTextLayerTicks = elapsedTicks;
        _totalTextLayerTicks += elapsedTicks;
        _visibleTextLinesVisited += Math.Max(0, visibleLinesVisited);
    }

    public RendererPerformanceSnapshot Snapshot(bool reset = false)
    {
        var snapshot = new RendererPerformanceSnapshot(
            _renderRequests,
            _coalescedRenderRequests,
            _frames,
            _textLayerRebuilds,
            _visibleTextLinesVisited,
            _totalFrameTicks,
            _totalTextLayerTicks,
            _lastFrameTicks,
            _lastTextLayerTicks);
        if (reset)
            Reset();
        return snapshot;
    }

    public void Reset()
    {
        _renderRequests = 0;
        _coalescedRenderRequests = 0;
        _frames = 0;
        _textLayerRebuilds = 0;
        _visibleTextLinesVisited = 0;
        _totalFrameTicks = 0;
        _totalTextLayerTicks = 0;
        _lastFrameTicks = 0;
        _lastTextLayerTicks = 0;
    }
}
