using System.Runtime.InteropServices;

namespace MonacoNative.Core;

public enum MonacoInjectedTextAffinity
{
    StayBeforeInsertion = 0,
    MoveAfterInsertion = 1,
}

public enum MonacoRevealStrategy
{
    Minimal = 0,
    Top = 1,
    Center = 2,
    Bottom = 3,
}

public readonly record struct MonacoViewPosition(int LineNumber, int Column);
public readonly record struct MonacoHiddenArea(int StartLineNumber, int EndLineNumber);
public readonly record struct MonacoViewLineInfo(
    int ViewLineNumber,
    int ModelLineNumber,
    int StartColumn,
    int EndColumn,
    int WrappedIndex,
    string Text,
    double VerticalOffset);

/// <summary>
/// Managed owner for the native text projection model. A view keeps the underlying
/// native TextModel alive through a shared_ptr even when the managed text model is later disposed.
/// </summary>
public sealed class MonacoViewModel : IDisposable
{
    public delegate double TextMeasureHandler(string text);

    private readonly MonacoTextModel _model;
    private nint _handle;
    private NativeMethods.MeasureTextCallback? _nativeMeasureCallback;
    private GCHandle _measureHandle;
    private bool _hasMeasureHandle;

    public MonacoViewModel(MonacoTextModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _handle = NativeMethods.mn_view_create(model.NativeHandle);
        if (_handle == 0)
            throw new InvalidOperationException("Failed to create MonacoNative view model.");
    }

    public MonacoTextModel Model => _model;
    public int ViewLineCount => NativeMethods.mn_view_line_count(Handle);
    public double TotalHeight => NativeMethods.mn_view_total_height(Handle);

    public void SetWrapColumn(int value) => Check(NativeMethods.mn_view_set_wrap_column(Handle, value), nameof(SetWrapColumn));
    public void SetWrapWidth(double value) => Check(NativeMethods.mn_view_set_wrap_width(Handle, value), nameof(SetWrapWidth));
    public void SetLineHeight(double value) => Check(NativeMethods.mn_view_set_line_height(Handle, value), nameof(SetLineHeight));
    public void Rebuild() => Check(NativeMethods.mn_view_rebuild(Handle), nameof(Rebuild));

    public unsafe void SetTextMeasurer(TextMeasureHandler? handler)
    {
        if (handler is null)
        {
            Check(NativeMethods.mn_view_set_measure_callback(Handle, null, 0), nameof(SetTextMeasurer));
            FreeMeasureHandle();
            _nativeMeasureCallback = null;
            return;
        }

        var newHandle = GCHandle.Alloc(handler);
        _nativeMeasureCallback ??= static (context, text, length) =>
        {
            // Never allow a managed exception to cross the unmanaged callback boundary.
            // A zero width is a safe failure value; the renderer can recover on the next rebuild.
            try
            {
                if (context == 0)
                    return 0.0;
                var target = GCHandle.FromIntPtr(context).Target as TextMeasureHandler;
                return target?.Invoke(new string(text, 0, checked((int)length))) ?? 0.0;
            }
            catch
            {
                return 0.0;
            }
        };

        try
        {
            Check(NativeMethods.mn_view_set_measure_callback(
                Handle,
                _nativeMeasureCallback,
                GCHandle.ToIntPtr(newHandle)),
                nameof(SetTextMeasurer));
        }
        catch
        {
            newHandle.Free();
            throw;
        }

        FreeMeasureHandle();
        _measureHandle = newHandle;
        _hasMeasureHandle = true;
    }

    public void RebuildWithTextMeasurer() => Rebuild();

    public unsafe string GetViewLineText(int viewLineNumber)
    {
        var required = NativeMethods.mn_view_copy_line(Handle, viewLineNumber, null, 0);
        if (required == 0)
            throw new ArgumentOutOfRangeException(nameof(viewLineNumber));
        if (required == 1)
            return string.Empty;
        var chars = new char[checked((int)required)];
        fixed (char* ptr = chars)
        {
            if (NativeMethods.mn_view_copy_line(Handle, viewLineNumber, ptr, required) == 0)
                throw new InvalidOperationException("Native view line copy failed.");
        }
        return new string(chars, 0, checked((int)required - 1));
    }

    public unsafe MonacoViewLineInfo GetViewLine(int viewLineNumber)
    {
        NativeMethods.NativeViewLineInfo info = default;
        if (NativeMethods.mn_view_get_line_info(Handle, viewLineNumber, &info) == 0)
            throw new ArgumentOutOfRangeException(nameof(viewLineNumber));
        return new MonacoViewLineInfo(
            info.ViewLineNumber,
            info.ModelLineNumber,
            info.StartColumn,
            info.EndColumn,
            info.WrappedIndex,
            GetViewLineText(viewLineNumber),
            NativeMethods.mn_view_get_vertical_offset(Handle, viewLineNumber));
    }

    public int GetViewLineAtVerticalOffset(double offset) => NativeMethods.mn_view_line_at_vertical_offset(Handle, offset);

    public MonacoViewPosition ModelToView(MonacoPosition position)
    {
        if (NativeMethods.mn_view_model_to_view(Handle, position.LineNumber, position.Column, out var line, out var column) == 0)
            throw new ArgumentOutOfRangeException(nameof(position));
        return new MonacoViewPosition(line, column);
    }

    public MonacoPosition ViewToModel(MonacoViewPosition position)
    {
        if (NativeMethods.mn_view_view_to_model(Handle, position.LineNumber, position.Column, out var line, out var column) == 0)
            throw new ArgumentOutOfRangeException(nameof(position));
        return new MonacoPosition(line, column);
    }

    public unsafe void SetHiddenAreas(IReadOnlyList<MonacoHiddenArea>? areas)
    {
        if (areas is null || areas.Count == 0)
        {
            Check(NativeMethods.mn_view_set_hidden_areas(Handle, null, 0), nameof(SetHiddenAreas));
            return;
        }
        var native = new NativeMethods.NativeHiddenArea[areas.Count];
        for (var i = 0; i < areas.Count; i++)
            native[i] = new NativeMethods.NativeHiddenArea { StartLineNumber = areas[i].StartLineNumber, EndLineNumber = areas[i].EndLineNumber };
        fixed (NativeMethods.NativeHiddenArea* ptr = native)
            Check(NativeMethods.mn_view_set_hidden_areas(Handle, ptr, (nuint)native.Length), nameof(SetHiddenAreas));
    }

    public ulong AddViewZone(int afterLineNumber, double height) => NativeMethods.mn_view_add_view_zone(Handle, afterLineNumber, height);
    public bool RemoveViewZone(ulong id) => NativeMethods.mn_view_remove_view_zone(Handle, id) != 0;
    public void ClearViewZones() => NativeMethods.mn_view_clear_view_zones(Handle);

    public ulong AddInjectedText(MonacoPosition position, string text, MonacoInjectedTextAffinity affinity = MonacoInjectedTextAffinity.MoveAfterInsertion, int order = 0) =>
        NativeMethods.mn_view_add_injected_text(Handle, position.LineNumber, position.Column, text ?? string.Empty, (int)affinity, order);
    public bool RemoveInjectedText(ulong id) => NativeMethods.mn_view_remove_injected_text(Handle, id) != 0;
    public void ClearInjectedText() => NativeMethods.mn_view_clear_injected_text(Handle);

    public double GetScrollTopToReveal(MonacoRange range, double viewportHeight, double currentScrollTop, MonacoRevealStrategy strategy = MonacoRevealStrategy.Minimal, double padding = 0)
    {
        var native = new NativeMethods.NativeRange
        {
            StartLine = range.StartLineNumber,
            StartColumn = range.StartColumn,
            EndLine = range.EndLineNumber,
            EndColumn = range.EndColumn,
        };
        return NativeMethods.mn_view_scroll_top_to_reveal(Handle, native, viewportHeight, currentScrollTop, (int)strategy, padding);
    }

    private void FreeMeasureHandle()
    {
        if (!_hasMeasureHandle)
            return;
        _measureHandle.Free();
        _hasMeasureHandle = false;
    }

    private static void Check(int value, string operation)
    {
        if (value == 0)
            throw new InvalidOperationException($"Native {operation} failed.");
    }

    private nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(MonacoViewModel));

    public void Dispose()
    {
        if (_handle == 0)
            return;
        NativeMethods.mn_view_destroy(_handle);
        _handle = 0;
        FreeMeasureHandle();
        _nativeMeasureCallback = null;
        GC.SuppressFinalize(this);
    }

    ~MonacoViewModel()
    {
        if (_handle != 0)
            NativeMethods.mn_view_destroy(_handle);
        FreeMeasureHandle();
    }
}
