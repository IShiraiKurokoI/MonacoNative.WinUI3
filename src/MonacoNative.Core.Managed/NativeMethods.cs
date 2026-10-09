using System.Runtime.InteropServices;

namespace MonacoNative.Core;

internal static class NativeMethods
{

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeAbiInfo
    {
        internal uint StructSize;
        internal uint AbiVersion;
        internal uint AbiMajor;
        internal uint AbiMinor;
        internal uint AbiPatch;
        internal uint PointerSize;
        internal uint Char16Size;
        internal uint RangeSize;
        internal uint BracketMatchSize;
        internal uint ViewLineInfoSize;
        internal uint HiddenAreaSize;
        internal uint DiffChangeSize;
        internal uint DiffInnerChangeSize;
        internal uint DiffMoveSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRange
    {
        internal int StartLine;
        internal int StartColumn;
        internal int EndLine;
        internal int EndColumn;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeUndoSelection
    {
        internal nuint Anchor;
        internal nuint Active;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBracketMatch
    {
        internal int BracketLine;
        internal int BracketColumn;
        internal int MatchLine;
        internal int MatchColumn;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeViewLineInfo
    {
        internal int ViewLineNumber;
        internal int ModelLineNumber;
        internal int StartColumn;
        internal int EndColumn;
        internal int WrappedIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeHiddenArea
    {
        internal int StartLineNumber;
        internal int EndLineNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDiffChange
    {
        internal int OriginalStartLine;
        internal int OriginalEndLineExclusive;
        internal int ModifiedStartLine;
        internal int ModifiedEndLineExclusive;
        internal nuint InnerChangeCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDiffInnerChange
    {
        internal int OriginalLine;
        internal int OriginalStartColumn;
        internal int OriginalEndColumn;
        internal int ModifiedLine;
        internal int ModifiedStartColumn;
        internal int ModifiedEndColumn;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDiffMove
    {
        internal int OriginalStartLine;
        internal int OriginalEndLineExclusive;
        internal int ModifiedStartLine;
        internal int ModifiedEndLineExclusive;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeKeybindingResolution
    {
        internal uint StructSize;
        internal int Kind;
        internal int PreventDefault;
        internal nint CommandId;
        internal nint ChordLabel;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate double MeasureTextCallback(nint context, char* text, nuint length);
    private const string Library = "MonacoNative.Core.Native.dll";


    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint mn_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_abi_get_info(ref NativeAbiInfo info);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern nint mn_model_create(string text, [MarshalAs(UnmanagedType.LPUTF8Str)] string language_id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mn_model_destroy(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nuint mn_model_length(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nuint mn_model_line_count(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong mn_model_version_id(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_can_undo(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_can_redo(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_undo(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_redo(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe int mn_model_set_undo_selections(nint handle,
        NativeUndoSelection* before, nuint before_count,
        NativeUndoSelection* after, nuint after_count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe int mn_model_set_undo_after_selections(nint handle,
        NativeUndoSelection* after, nuint after_count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_model_copy_restored_selections(nint handle,
        NativeUndoSelection* selections, nuint capacity);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_begin_undo_transaction(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_end_undo_transaction(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_rollback_undo_transaction(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_push_undo_stop(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_in_undo_transaction(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int mn_model_set_text(nint handle, string text);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_model_copy_text(nint handle, char* buffer, nuint buffer_chars);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_model_copy_text_range(nint handle, nuint start, nuint length, char* buffer, nuint buffer_chars);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_model_copy_line(nint handle, int line_number, char* buffer, nuint buffer_chars);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_line_max_column(nint handle, int line_number);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int mn_model_replace(nint handle, nuint start, nuint end, string text);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern nuint mn_model_replace_all(nint handle, string query, string replacement, int match_case, int whole_word, nuint limit);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_set_language(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string language_id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_model_copy_language(nint handle, byte* buffer, nuint buffer_bytes);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_get_position_at(nint handle, nuint offset, out int line_number, out int column);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_get_offset_at(nint handle, int line_number, int column, out nuint offset);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_get_eol(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_model_set_eol(nint handle, int eol);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern unsafe nuint mn_model_find_matches(nint handle, string query, int match_case, int whole_word, nuint limit, NativeRange* ranges, nuint range_capacity);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe int mn_model_find_matching_bracket(nint handle, int line_number, int column, NativeBracketMatch* result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_unicode_previous_grapheme_boundary(char* text, nuint length, nuint offset);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_unicode_next_grapheme_boundary(char* text, nuint length, nuint offset);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_unicode_previous_word_boundary(char* text, nuint length, nuint offset);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_unicode_next_word_boundary(char* text, nuint length, nuint offset);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern nint mn_diff_compute(string original, string modified, int ignore_trim_whitespace, int compute_moves);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mn_diff_destroy(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nuint mn_diff_change_count(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_diff_get_change(nint handle, nuint index, out NativeDiffChange change);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_diff_get_inner_change(nint handle, nuint change_index, nuint inner_index, out NativeDiffInnerChange change);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nuint mn_diff_move_count(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_diff_get_move(nint handle, nuint index, out NativeDiffMove move);


    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nint mn_view_create(nint model_handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mn_view_destroy(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_set_wrap_column(nint handle, int wrap_column);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_set_wrap_width(nint handle, double wrap_width);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_set_line_height(nint handle, double line_height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_set_measure_callback(nint handle, MeasureTextCallback? callback, nint context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_rebuild(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_line_count(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_view_copy_line(nint handle, int view_line_number, char* buffer, nuint buffer_chars);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe int mn_view_get_line_info(nint handle, int view_line_number, NativeViewLineInfo* info);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern double mn_view_get_vertical_offset(nint handle, int view_line_number);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern double mn_view_total_height(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_line_at_vertical_offset(nint handle, double offset);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_model_to_view(nint handle, int model_line, int model_column, out int view_line, out int view_column);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_view_to_model(nint handle, int view_line, int view_column, out int model_line, out int model_column);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe int mn_view_set_hidden_areas(nint handle, NativeHiddenArea* areas, nuint count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong mn_view_add_view_zone(nint handle, int after_line_number, double height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_remove_view_zone(nint handle, ulong id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mn_view_clear_view_zones(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern ulong mn_view_add_injected_text(nint handle, int line_number, int column, string text, int affinity, int order);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_view_remove_injected_text(nint handle, ulong id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mn_view_clear_injected_text(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern double mn_view_scroll_top_to_reveal(nint handle, NativeRange range, double viewport_height, double current_scroll_top, int strategy, double padding);


    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nint mn_keybinding_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mn_keybinding_destroy(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_keybinding_set_context_bool(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_keybinding_set_context_number(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, double value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_keybinding_set_context_string(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_keybinding_reset_context(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_keybinding_context_matches(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong mn_keybinding_register(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string command_id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string chord,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string when_clause,
        int weight,
        int prevent_default);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_keybinding_unregister(nint handle, ulong token);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mn_keybinding_reset_chord(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_keybinding_in_chord_mode(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_keybinding_resolve(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, byte modifiers, out int prevent_default);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_keybinding_copy_resolved_command(nint handle, byte* buffer, nuint buffer_bytes);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint mn_keybinding_copy_pending_chord(nint handle, byte* buffer, nuint buffer_bytes);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mn_keybinding_resolve_snapshot(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        byte modifiers,
        ref NativeKeybindingResolution result);
}
