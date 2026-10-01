using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MonacoNative.Core;
using MonacoNative.Input;
using WindowsGlobalizationLanguage = Windows.Globalization.Language;

namespace MonacoNative.Renderer.WinUI3;

internal readonly record struct NativeTsfRect(int Left, int Top, int Right, int Bottom);

internal sealed unsafe class NativeTsfInputController : IEditorTextInputController
{
    private const string NativeLibrary = "MonacoNative.Core.Native";
    private readonly MonacoTextModel _model;
    private readonly Func<nint> _hwndProvider;
    private readonly Func<(int Start, int End, bool ActiveEndAtStart)> _getSelection;
    private readonly Action<int, int, bool> _setSelection;
    private readonly Func<int, int, uint, (bool Success, int Acp)> _pointToAcp;
    private readonly Func<int, int, (bool Success, NativeTsfRect Rect, bool Clipped)> _textRect;
    private readonly Func<(bool Success, NativeTsfRect Rect)> _screenRect;
    private readonly Func<bool> _hasLayout;
    private readonly DispatcherQueue _dispatcher;
    private readonly ImeCompositionState _composition = new();

    private GCHandle _selfHandle;
    private nint _nativeHandle;
    private bool _disposed;
    private bool _hasFocus;
    private bool _readOnly;
    private bool _focusFailed;
    private bool _applyingServerUpdate;
    private string _lastLanguageTag = string.Empty;

    public NativeTsfInputController(
        MonacoTextModel model,
        Func<nint> hwndProvider,
        Func<(int Start, int End, bool ActiveEndAtStart)> getSelection,
        Action<int, int, bool> setSelection,
        Func<int, int, uint, (bool Success, int Acp)> pointToAcp,
        Func<int, int, (bool Success, NativeTsfRect Rect, bool Clipped)> textRect,
        Func<(bool Success, NativeTsfRect Rect)> screenRect,
        Func<bool> hasLayout,
        DispatcherQueue dispatcher)
    {
        _model = model;
        _hwndProvider = hwndProvider;
        _getSelection = getSelection;
        _setSelection = setSelection;
        _pointToAcp = pointToAcp;
        _textRect = textRect;
        _screenRect = screenRect;
        _hasLayout = hasLayout;
        _dispatcher = dispatcher;
    }

    public ImeInputBackend Backend => ImeInputBackend.NativeTsf;

    public event EventHandler? InputLanguageChanged;
    public event EventHandler? CompositionStateChanged;

    public bool IsAvailable => _nativeHandle != 0 && !_focusFailed;
    public bool HasInputFocus => _hasFocus;
    public ImeInputCapability Capability => IsAvailable ? ImeInputCapability.NativeTsf : ImeInputCapability.BasicCharacterFallback;
    public bool IsApplyingServerUpdate => _applyingServerUpdate;
    public bool IsComposing => _composition.IsComposing;
    public ImeTextRange CompositionRange => _composition.CompositionRange;
    public IReadOnlyList<ImeCompositionSegment> CompositionSegments => _composition.GetRenderableSegments();
    public ImeTextRange? ConversionTargetRange => null;
    // ITfKeystrokeMgr consumes only keys claimed by the active TSF service. Keys it does
    // not eat must still reach CharacterReceived so plain Latin/direct input keeps working.
    public bool SuppressCharacterReceived => false;
    public string InputLanguageTag
    {
        get
        {
            try { return WindowsGlobalizationLanguage.CurrentInputMethodLanguageTag ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    public bool Initialize()
    {
        ThrowIfDisposed();
        if (_nativeHandle != 0)
            return true;

        var hwnd = _hwndProvider();
        if (hwnd == 0)
            return false;

        _selfHandle = GCHandle.Alloc(this);
        var callbacks = new NativeCallbacks
        {
            StructSize = (uint)sizeof(NativeCallbacks),
            Context = GCHandle.ToIntPtr(_selfHandle),
            GetSelection = (nint)(delegate* unmanaged[Cdecl]<nint, int*, int*, int*, int>)&GetSelectionCallback,
            SetSelection = (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int, int>)&SetSelectionCallback,
            HasLayout = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&HasLayoutCallback,
            PointToAcp = (nint)(delegate* unmanaged[Cdecl]<nint, int, int, uint, int*, int>)&PointToAcpCallback,
            TextRect = (nint)(delegate* unmanaged[Cdecl]<nint, int, int, NativeRect*, int*, int>)&TextRectCallback,
            ScreenRect = (nint)(delegate* unmanaged[Cdecl]<nint, NativeRect*, int>)&ScreenRectCallback,
            Composition = (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int, void>)&CompositionCallback,
            ModelChanged = (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int, int, int, int, int, int, int, void>)&ModelChangedCallback,
        };

        _nativeHandle = NativeMethods.mn_tsf_create(_model.NativeHandle, hwnd, ref callbacks);
        if (_nativeHandle == 0)
        {
            _selfHandle.Free();
            return false;
        }

        NativeMethods.mn_tsf_set_read_only(_nativeHandle, _readOnly ? 1 : 0);
        _lastLanguageTag = InputLanguageTag;
        return true;
    }

    public void SetReadOnly(bool value)
    {
        _readOnly = value;
        if (_nativeHandle != 0)
            NativeMethods.mn_tsf_set_read_only(_nativeHandle, value ? 1 : 0);
    }

    public void NotifyFocusEnter()
    {
        if (_nativeHandle == 0)
            return;
        _hasFocus = NativeMethods.mn_tsf_focus_enter(_nativeHandle) != 0;
        _focusFailed = !_hasFocus;
        RaiseLanguageChangedIfNeeded();
    }

    public void NotifyFocusLeave()
    {
        _hasFocus = false;
        _focusFailed = false;
        if (_nativeHandle != 0)
            _ = NativeMethods.mn_tsf_focus_leave(_nativeHandle);
        if (_composition.IsComposing)
        {
            _composition.Complete();
            CompositionStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void NotifySelectionChanged()
    {
        if (_applyingServerUpdate)
            return;
        if (_nativeHandle != 0)
            NativeMethods.mn_tsf_notify_selection_changed(_nativeHandle);
    }

    // The native ACP backend observes TextModel content changes directly. Managed edits already
    // raise MonacoTextModel.Changed themselves, so no mirrored text notification is required here.
    public void NotifyModelChanged(MonacoModelChangedEventArgs change) { }

    public bool TryHandleKeyDown(uint virtualKey, uint messageLParam)
    {
        if (_nativeHandle == 0 || !_hasFocus || _focusFailed)
            return false;
        return NativeMethods.mn_tsf_key_down(_nativeHandle, virtualKey, messageLParam) != 0;
    }

    public bool TryHandleKeyUp(uint virtualKey, uint messageLParam)
    {
        if (_nativeHandle == 0 || !_hasFocus || _focusFailed)
            return false;
        return NativeMethods.mn_tsf_key_up(_nativeHandle, virtualKey, messageLParam) != 0;
    }

    // Geometry callbacks always read the current WinUI/Win2D layout on demand.
    public void NotifyLayoutChanged() { }

    private void RaiseLanguageChangedIfNeeded()
    {
        var current = InputLanguageTag;
        if (string.Equals(current, _lastLanguageTag, StringComparison.Ordinal))
            return;
        _lastLanguageTag = current;
        InputLanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static NativeTsfInputController? FromContext(nint context)
    {
        if (context == 0)
            return null;
        try { return GCHandle.FromIntPtr(context).Target as NativeTsfInputController; }
        catch { return null; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetSelectionCallback(nint context, int* start, int* end, int* activeEndAtStart)
    {
        var self = FromContext(context);
        if (self is null || start is null || end is null || activeEndAtStart is null)
            return 0;
        try
        {
            var selection = self._getSelection();
            *start = selection.Start;
            *end = selection.End;
            *activeEndAtStart = selection.ActiveEndAtStart ? 1 : 0;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetSelectionCallback(nint context, int start, int end, int activeEndAtStart)
    {
        var self = FromContext(context);
        if (self is null)
            return 0;
        try
        {
            self._applyingServerUpdate = true;
            try
            {
                self._setSelection(start, end, activeEndAtStart != 0);
            }
            finally
            {
                self._applyingServerUpdate = false;
            }
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HasLayoutCallback(nint context)
    {
        var self = FromContext(context);
        try { return self is not null && self._hasLayout() ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int PointToAcpCallback(nint context, int x, int y, uint flags, int* acp)
    {
        var self = FromContext(context);
        if (self is null || acp is null)
            return 0;
        try
        {
            var value = self._pointToAcp(x, y, flags);
            if (!value.Success) return 0;
            *acp = value.Acp;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TextRectCallback(nint context, int start, int end, NativeRect* rect, int* clipped)
    {
        var self = FromContext(context);
        if (self is null || rect is null || clipped is null)
            return 0;
        try
        {
            var value = self._textRect(start, end);
            if (!value.Success) return 0;
            *rect = NativeRect.From(value.Rect);
            *clipped = value.Clipped ? 1 : 0;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ScreenRectCallback(nint context, NativeRect* rect)
    {
        var self = FromContext(context);
        if (self is null || rect is null)
            return 0;
        try
        {
            var value = self._screenRect();
            if (!value.Success) return 0;
            *rect = NativeRect.From(value.Rect);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CompositionCallback(nint context, int phase, int start, int end)
    {
        var self = FromContext(context);
        if (self is null)
            return;
        try
        {
            var length = checked((int)Math.Min(self._model.Length, (nuint)int.MaxValue));
            var range = new ImeTextRange(start, end).Normalize(length);
            switch (phase)
            {
            case 1:
                self._composition.Begin(range, length);
                break;
            case 2:
                if (!self._composition.IsComposing)
                    self._composition.Begin(range, length);
                else
                    self._composition.SetCompositionRange(range, length);
                break;
            case 3:
                self._composition.Complete();
                break;
            default:
                return;
            }
            self.CompositionStateChanged?.Invoke(self, EventArgs.Empty);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ModelChangedCallback(
        nint context,
        int startOffset,
        int oldEndOffset,
        int newEndOffset,
        int startLine,
        int startColumn,
        int oldEndLine,
        int oldEndColumn,
        int newEndLine,
        int newEndColumn)
    {
        var self = FromContext(context);
        if (self is null)
            return;

        // Do not reenter renderer/model consumers from inside TextModel's native event dispatch.
        _ = self._dispatcher.TryEnqueue(() =>
        {
            if (self._disposed)
                return;
            self._applyingServerUpdate = true;
            try
            {
                self._model.NotifyNativeTsfEdit(
                    (nuint)Math.Max(0, startOffset),
                    (nuint)Math.Max(0, oldEndOffset),
                    (nuint)Math.Max(0, newEndOffset),
                    startLine,
                    startColumn,
                    oldEndLine,
                    oldEndColumn,
                    newEndLine,
                    newEndColumn);
            }
            finally
            {
                self._applyingServerUpdate = false;
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hasFocus = false;
        _focusFailed = false;
        if (_nativeHandle != 0)
        {
            NativeMethods.mn_tsf_destroy(_nativeHandle);
            _nativeHandle = 0;
        }
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
        _composition.Complete();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public static NativeRect From(NativeTsfRect value) => new()
        {
            Left = value.Left,
            Top = value.Top,
            Right = value.Right,
            Bottom = value.Bottom,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCallbacks
    {
        public uint StructSize;
        public nint Context;
        public nint GetSelection;
        public nint SetSelection;
        public nint HasLayout;
        public nint PointToAcp;
        public nint TextRect;
        public nint ScreenRect;
        public nint Composition;
        public nint ModelChanged;
    }

    private static class NativeMethods
    {
        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern nint mn_tsf_create(nint model, nint hwnd, ref NativeCallbacks callbacks);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void mn_tsf_destroy(nint handle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int mn_tsf_focus_enter(nint handle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int mn_tsf_focus_leave(nint handle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void mn_tsf_notify_selection_changed(nint handle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void mn_tsf_set_read_only(nint handle, int readOnly);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int mn_tsf_key_down(nint handle, uint virtualKey, uint messageLParam);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int mn_tsf_key_up(nint handle, uint virtualKey, uint messageLParam);
    }
}
