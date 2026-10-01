using System.Runtime.InteropServices;

namespace MonacoNative.Core;

[Flags]
public enum MonacoKeyModifiers : byte
{
    None = 0,
    Ctrl = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
    Meta = 1 << 3,
}

public enum MonacoKeybindingResultKind
{
    NoMatch = 0,
    Command = 1,
    MoreChordsNeeded = 2,
    ChordCancelled = 3,
}

public readonly record struct MonacoKeybindingResult(
    MonacoKeybindingResultKind Kind,
    string CommandId,
    bool PreventDefault,
    string PendingChord);

public sealed class MonacoKeybindingService : IDisposable
{
    private nint _handle;

    public MonacoKeybindingService()
    {
        MonacoNativeAbi.EnsureCompatible();
        _handle = NativeMethods.mn_keybinding_create();
        if (_handle == 0)
            throw new InvalidOperationException("Failed to create MonacoNative keybinding service.");
    }

    public bool InChordMode => NativeMethods.mn_keybinding_in_chord_mode(Handle) != 0;

    public void SetContext(string key, bool value)
    {
        if (NativeMethods.mn_keybinding_set_context_bool(Handle, key, value ? 1 : 0) == 0)
            throw new ArgumentException("Failed to set context key.", nameof(key));
    }

    public void SetContext(string key, double value)
    {
        if (NativeMethods.mn_keybinding_set_context_number(Handle, key, value) == 0)
            throw new ArgumentException("Failed to set context key.", nameof(key));
    }

    public void SetContext(string key, string value)
    {
        if (NativeMethods.mn_keybinding_set_context_string(Handle, key, value ?? string.Empty) == 0)
            throw new ArgumentException("Failed to set context key.", nameof(key));
    }

    public bool ResetContext(string key) => NativeMethods.mn_keybinding_reset_context(Handle, key) != 0;

    public bool ContextMatches(string? expression)
    {
        var result = NativeMethods.mn_keybinding_context_matches(Handle, expression ?? string.Empty);
        return result switch
        {
            0 => false,
            1 => true,
            _ => throw new ArgumentException("Invalid context-key expression.", nameof(expression)),
        };
    }

    public ulong Register(string commandId, string chord, string? when = null, int weight = 0, bool preventDefault = true)
    {
        var token = NativeMethods.mn_keybinding_register(Handle, commandId, chord, when ?? string.Empty, weight, preventDefault ? 1 : 0);
        if (token == 0)
            throw new ArgumentException("Invalid keybinding registration.");
        return token;
    }

    public bool Unregister(ulong token) => NativeMethods.mn_keybinding_unregister(Handle, token) != 0;

    public void ResetChord() => NativeMethods.mn_keybinding_reset_chord(Handle);

    public MonacoKeybindingResult Resolve(string key, MonacoKeyModifiers modifiers)
    {
        var native = new NativeMethods.NativeKeybindingResolution
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeMethods.NativeKeybindingResolution>()),
        };
        if (NativeMethods.mn_keybinding_resolve_snapshot(Handle, key, (byte)modifiers, ref native) == 0)
            throw new ArgumentException("Invalid key stroke.", nameof(key));
        if (native.Kind is < (int)MonacoKeybindingResultKind.NoMatch or > (int)MonacoKeybindingResultKind.ChordCancelled)
            throw new InvalidOperationException($"Native keybinding resolver returned invalid result kind {native.Kind}.");

        var kind = (MonacoKeybindingResultKind)native.Kind;
        var commandId = kind == MonacoKeybindingResultKind.Command && native.CommandId != 0
            ? Marshal.PtrToStringUTF8(native.CommandId) ?? string.Empty
            : string.Empty;
        var pendingChord = kind == MonacoKeybindingResultKind.MoreChordsNeeded && native.ChordLabel != 0
            ? Marshal.PtrToStringUTF8(native.ChordLabel) ?? string.Empty
            : string.Empty;
        return new MonacoKeybindingResult(kind, commandId, native.PreventDefault != 0, pendingChord);
    }

    private nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(MonacoKeybindingService));

    public void Dispose()
    {
        if (_handle == 0)
            return;
        NativeMethods.mn_keybinding_destroy(_handle);
        _handle = 0;
        GC.SuppressFinalize(this);
    }

    ~MonacoKeybindingService()
    {
        if (_handle != 0)
            NativeMethods.mn_keybinding_destroy(_handle);
    }
}
