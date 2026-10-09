using System.Runtime.InteropServices;

namespace MonacoNative.Core;

public readonly record struct MonacoNativeAbiInfo(
    uint PackedVersion,
    uint Major,
    uint Minor,
    uint Patch,
    uint PointerSize,
    uint Char16Size,
    uint RangeSize,
    uint BracketMatchSize,
    uint ViewLineInfoSize,
    uint HiddenAreaSize,
    uint DiffChangeSize,
    uint DiffInnerChangeSize,
    uint DiffMoveSize);

public static class MonacoNativeAbi
{
    public const uint Major = 1;
    public const uint Minor = 3;
    public const uint Patch = 0;
    public const uint PackedVersion = (Major << 16) | (Minor << 8) | Patch;

    public static MonacoNativeAbiInfo GetInfo()
    {
        var native = new NativeMethods.NativeAbiInfo
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeMethods.NativeAbiInfo>()),
        };
        if (NativeMethods.mn_abi_get_info(ref native) == 0)
            throw new InvalidOperationException("Failed to query MonacoNative ABI information.");

        Validate(native);
        return new MonacoNativeAbiInfo(
            native.AbiVersion, native.AbiMajor, native.AbiMinor, native.AbiPatch,
            native.PointerSize, native.Char16Size, native.RangeSize, native.BracketMatchSize,
            native.ViewLineInfoSize, native.HiddenAreaSize, native.DiffChangeSize,
            native.DiffInnerChangeSize, native.DiffMoveSize);
    }

    public static void EnsureCompatible() => _ = GetInfo();

    private static void Validate(NativeMethods.NativeAbiInfo native)
    {
        if (native.StructSize < Marshal.SizeOf<NativeMethods.NativeAbiInfo>())
            throw new PlatformNotSupportedException($"MonacoNative ABI info is too small: native={native.StructSize}, managed={Marshal.SizeOf<NativeMethods.NativeAbiInfo>()}.");
        var encoded = (native.AbiMajor << 16) | (native.AbiMinor << 8) | native.AbiPatch;
        if (native.AbiVersion != encoded)
            throw new PlatformNotSupportedException($"MonacoNative ABI version fields are inconsistent: packed=0x{native.AbiVersion:X8}, fields=0x{encoded:X8}.");
        if (native.AbiVersion != PackedVersion)
            throw new PlatformNotSupportedException($"MonacoNative requires native ABI {Major}.{Minor}.{Patch} (0x{PackedVersion:X8}); found {native.AbiMajor}.{native.AbiMinor}.{native.AbiPatch} (0x{native.AbiVersion:X8}). Rebuild and deploy the managed and native components together.");

        static uint SizeOf<T>() where T : struct => checked((uint)Marshal.SizeOf<T>());
        var expected = new (string Name, uint Native, uint Managed)[]
        {
            ("pointer", native.PointerSize, checked((uint)IntPtr.Size)),
            ("char16", native.Char16Size, sizeof(char)),
            ("mn_range", native.RangeSize, SizeOf<NativeMethods.NativeRange>()),
            ("mn_bracket_match", native.BracketMatchSize, SizeOf<NativeMethods.NativeBracketMatch>()),
            ("mn_view_line_info", native.ViewLineInfoSize, SizeOf<NativeMethods.NativeViewLineInfo>()),
            ("mn_hidden_area", native.HiddenAreaSize, SizeOf<NativeMethods.NativeHiddenArea>()),
            ("mn_diff_change", native.DiffChangeSize, SizeOf<NativeMethods.NativeDiffChange>()),
            ("mn_diff_inner_change", native.DiffInnerChangeSize, SizeOf<NativeMethods.NativeDiffInnerChange>()),
            ("mn_diff_move", native.DiffMoveSize, SizeOf<NativeMethods.NativeDiffMove>()),
        };
        foreach (var entry in expected)
        {
            if (entry.Native != entry.Managed)
                throw new PlatformNotSupportedException($"MonacoNative ABI layout mismatch for {entry.Name}: native={entry.Native}, managed={entry.Managed}.");
        }
    }
}
