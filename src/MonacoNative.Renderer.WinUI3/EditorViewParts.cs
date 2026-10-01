using System.Diagnostics;
using Microsoft.Graphics.Canvas;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Immutable viewport inputs used by dynamic editor view parts. Keeping these inputs
/// explicit lets each part decide whether a cached command list is still reusable.
/// </summary>
internal readonly record struct EditorViewPartContext(
    int FirstViewLine,
    int LastViewLine,
    double ScrollTop,
    double ScrollLeft,
    double ViewportWidth,
    double ViewportHeight);

/// <summary>
/// Small ViewPart-style rendering unit for dynamic editor chrome. A part owns its
/// command-list cache and invalidation state; MonacoEditor only schedules frames and
/// composites prepared parts.
/// </summary>
internal readonly record struct EditorViewPartStatistics(
    long PrepareCalls,
    long CacheHits,
    long Rebuilds,
    long CompositeCalls,
    long TotalPrepareTicks,
    long LastPrepareTicks);

internal abstract class EditorCommandListViewPart : IDisposable
{
    private CanvasCommandList? _commands;
    private CanvasDevice? _device;
    private EditorViewPartContext _preparedContext;
    private bool _hasPreparedContext;
    private bool _dirty = true;
    private long _prepareCalls;
    private long _cacheHits;
    private long _rebuilds;
    private long _compositeCalls;
    private long _totalPrepareTicks;
    private long _lastPrepareTicks;

    public bool IsDirty => _dirty;

    public EditorViewPartStatistics Statistics => new(
        _prepareCalls, _cacheHits, _rebuilds, _compositeCalls,
        _totalPrepareTicks, _lastPrepareTicks);

    public void ResetStatistics()
    {
        _prepareCalls = 0;
        _cacheHits = 0;
        _rebuilds = 0;
        _compositeCalls = 0;
        _totalPrepareTicks = 0;
        _lastPrepareTicks = 0;
    }

    public void Invalidate() => _dirty = true;

    public void Prepare(CanvasDevice device, EditorViewPartContext context)
    {
        _prepareCalls++;
        if (!_dirty &&
            ReferenceEquals(_device, device) &&
            _hasPreparedContext &&
            _preparedContext.Equals(context))
        {
            _cacheHits++;
            _lastPrepareTicks = 0;
            return;
        }

        var start = Stopwatch.GetTimestamp();
        _commands?.Dispose();
        _commands = new CanvasCommandList(device);
        using (var drawingSession = _commands.CreateDrawingSession())
            Draw(drawingSession, context);

        _device = device;
        _preparedContext = context;
        _hasPreparedContext = true;
        _dirty = false;
        _lastPrepareTicks = Stopwatch.GetTimestamp() - start;
        _totalPrepareTicks += _lastPrepareTicks;
        _rebuilds++;
    }

    public void Composite(CanvasDrawingSession drawingSession)
    {
        _compositeCalls++;
        if (_commands is not null)
            drawingSession.DrawImage(_commands);
    }

    public void ReleaseGraphicsResources()
    {
        _commands?.Dispose();
        _commands = null;
        _device = null;
        _hasPreparedContext = false;
        _dirty = true;
    }

    protected abstract void Draw(CanvasDrawingSession drawingSession, EditorViewPartContext context);

    public void Dispose() => ReleaseGraphicsResources();
}

internal sealed class SelectionViewPart : EditorCommandListViewPart
{
    private readonly Action<CanvasDrawingSession, EditorViewPartContext> _draw;

    public SelectionViewPart(Action<CanvasDrawingSession, EditorViewPartContext> draw)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
    }

    protected override void Draw(CanvasDrawingSession drawingSession, EditorViewPartContext context) =>
        _draw(drawingSession, context);
}

internal sealed class DecorationsViewPart : EditorCommandListViewPart
{
    private readonly Action<CanvasDrawingSession, EditorViewPartContext> _draw;

    public DecorationsViewPart(Action<CanvasDrawingSession, EditorViewPartContext> draw)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
    }

    protected override void Draw(CanvasDrawingSession drawingSession, EditorViewPartContext context) =>
        _draw(drawingSession, context);
}

internal sealed class DiagnosticsViewPart : EditorCommandListViewPart
{
    private readonly Action<CanvasDrawingSession, EditorViewPartContext> _draw;

    public DiagnosticsViewPart(Action<CanvasDrawingSession, EditorViewPartContext> draw)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
    }

    protected override void Draw(CanvasDrawingSession drawingSession, EditorViewPartContext context) =>
        _draw(drawingSession, context);
}

internal sealed class CaretViewPart : EditorCommandListViewPart
{
    private readonly Action<CanvasDrawingSession, EditorViewPartContext> _draw;

    public CaretViewPart(Action<CanvasDrawingSession, EditorViewPartContext> draw)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
    }

    protected override void Draw(CanvasDrawingSession drawingSession, EditorViewPartContext context) =>
        _draw(drawingSession, context);
}

internal sealed class BracketViewPart : EditorCommandListViewPart
{
    private readonly Action<CanvasDrawingSession, EditorViewPartContext> _draw;

    public BracketViewPart(Action<CanvasDrawingSession, EditorViewPartContext> draw)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
    }

    protected override void Draw(CanvasDrawingSession drawingSession, EditorViewPartContext context) =>
        _draw(drawingSession, context);
}

internal sealed class OverviewViewPart : EditorCommandListViewPart
{
    private readonly Action<CanvasDrawingSession, EditorViewPartContext> _draw;

    public OverviewViewPart(Action<CanvasDrawingSession, EditorViewPartContext> draw)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
    }

    protected override void Draw(CanvasDrawingSession drawingSession, EditorViewPartContext context) =>
        _draw(drawingSession, context);
}

