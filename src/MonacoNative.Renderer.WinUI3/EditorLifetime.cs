namespace MonacoNative.Renderer.WinUI3;

internal enum EditorLifetimeState
{
    Running,
    Stopping,
    Disposed,
}

/// <summary>
/// Owns editor-scoped asynchronous work. The editor stops producers first,
/// then gives already-running operations one shared, bounded drain window.
/// </summary>
internal sealed class EditorLifetime : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = new();
    private readonly CancellationTokenSource _shutdown = new();
    private EditorLifetimeState _state;

    public EditorLifetimeState State
    {
        get { lock (_gate) return _state; }
    }

    public bool IsRunning => State == EditorLifetimeState.Running;
    public bool IsStopping => State != EditorLifetimeState.Running;
    public CancellationToken ShutdownToken => _shutdown.Token;

    public bool BeginShutdown()
    {
        lock (_gate)
        {
            if (_state != EditorLifetimeState.Running)
                return false;
            _state = EditorLifetimeState.Stopping;
        }

        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    public Task Track(Task task)
    {
        TrackCore(task);
        return task;
    }

    public void TrackDetached(Task task)
        => TrackCore(task);

    private void TrackCore(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (_state != EditorLifetimeState.Disposed && !task.IsCompleted)
                _tasks.Add(task);
        }

        _ = ObserveAndRemoveAsync(task);
    }

    public async ValueTask DrainAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return;

        Task[] pending;
        lock (_gate)
            pending = _tasks.Where(task => !task.IsCompleted).ToArray();

        if (pending.Length == 0)
            return;

        var all = ObserveManyAsync(pending);
        await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(true);
    }

    private async Task ObserveAndRemoveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (IsStopping) { }
        catch (ObjectDisposedException) when (IsStopping) { }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Editor background task failed: {error}");
        }
        finally
        {
            lock (_gate) _tasks.Remove(task);
        }
    }

    private async Task ObserveManyAsync(IEnumerable<Task> tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) when (IsStopping) { }
        catch (ObjectDisposedException) when (IsStopping) { }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Editor shutdown task failed: {error}");
        }
    }

    public void Dispose()
    {
        BeginShutdown();
        lock (_gate)
        {
            if (_state == EditorLifetimeState.Disposed)
                return;
            _state = EditorLifetimeState.Disposed;
        }
        _shutdown.Dispose();
    }
}
