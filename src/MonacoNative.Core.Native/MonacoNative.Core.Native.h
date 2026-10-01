#pragma once
#include <stddef.h>
#include <stdint.h>

#define MN_ABI_VERSION_MAJOR 1u
#define MN_ABI_VERSION_MINOR 2u
#define MN_ABI_VERSION_PATCH 0u
#define MN_ABI_VERSION_PACKED ((MN_ABI_VERSION_MAJOR << 16) | (MN_ABI_VERSION_MINOR << 8) | MN_ABI_VERSION_PATCH)

#ifdef _WIN32
using mn_char16 = wchar_t;
#define MN_CALL __cdecl
#ifdef MONACONATIVE_CORE_NATIVE_EXPORTS
#define MN_API extern "C" __declspec(dllexport)
#else
#define MN_API extern "C" __declspec(dllimport)
#endif
#else
using mn_char16 = char16_t;
#define MN_CALL
#define MN_API extern "C" __attribute__((visibility("default")))
#endif

static_assert(sizeof(mn_char16) == 2, "MonacoNative ABI requires a 16-bit UTF-16 code unit type.");

using mn_model_handle = void*;
using mn_view_handle = void*;
using mn_keybinding_handle = void*;
using mn_diff_handle = void*;
using mn_measure_text_callback = double (MN_CALL *)(void* context, const mn_char16* text, size_t length);

using mn_tsf_handle = void*;

struct mn_tsf_rect
{
    int32_t left;
    int32_t top;
    int32_t right;
    int32_t bottom;
};

using mn_tsf_get_selection_callback = int (MN_CALL *)(void* context, int32_t* start, int32_t* end, int* active_end_at_start);
using mn_tsf_set_selection_callback = int (MN_CALL *)(void* context, int32_t start, int32_t end, int active_end_at_start);
using mn_tsf_has_layout_callback = int (MN_CALL *)(void* context);
using mn_tsf_point_to_acp_callback = int (MN_CALL *)(void* context, int32_t x, int32_t y, uint32_t flags, int32_t* acp);
using mn_tsf_text_rect_callback = int (MN_CALL *)(void* context, int32_t start, int32_t end, mn_tsf_rect* rect, int* clipped);
using mn_tsf_screen_rect_callback = int (MN_CALL *)(void* context, mn_tsf_rect* rect);
using mn_tsf_composition_callback = void (MN_CALL *)(void* context, int32_t phase, int32_t start, int32_t end);
using mn_tsf_model_changed_callback = void (MN_CALL *)(void* context, int32_t start_offset, int32_t old_end_offset, int32_t new_end_offset, int32_t start_line, int32_t start_column, int32_t old_end_line, int32_t old_end_column, int32_t new_end_line, int32_t new_end_column);

struct mn_tsf_callbacks
{
    uint32_t struct_size;
    void* context;
    mn_tsf_get_selection_callback get_selection;
    mn_tsf_set_selection_callback set_selection;
    mn_tsf_has_layout_callback has_layout;
    mn_tsf_point_to_acp_callback point_to_acp;
    mn_tsf_text_rect_callback text_rect;
    mn_tsf_screen_rect_callback screen_rect;
    mn_tsf_composition_callback composition;
    mn_tsf_model_changed_callback model_changed;
};

struct mn_range
{
    int32_t start_line;
    int32_t start_column;
    int32_t end_line;
    int32_t end_column;
};

struct mn_bracket_match
{
    int32_t bracket_line;
    int32_t bracket_column;
    int32_t match_line;
    int32_t match_column;
};

struct mn_view_line_info
{
    int32_t view_line_number;
    int32_t model_line_number;
    int32_t start_column;
    int32_t end_column;
    int32_t wrapped_index;
};

struct mn_hidden_area
{
    int32_t start_line_number;
    int32_t end_line_number;
};

struct mn_diff_change
{
    int32_t original_start_line;
    int32_t original_end_line_exclusive;
    int32_t modified_start_line;
    int32_t modified_end_line_exclusive;
    size_t inner_change_count;
};

struct mn_diff_inner_change
{
    int32_t original_line;
    int32_t original_start_column;
    int32_t original_end_column;
    int32_t modified_line;
    int32_t modified_start_column;
    int32_t modified_end_column;
};

struct mn_diff_move
{
    int32_t original_start_line;
    int32_t original_end_line_exclusive;
    int32_t modified_start_line;
    int32_t modified_end_line_exclusive;
};

struct mn_abi_info
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t abi_major;
    uint32_t abi_minor;
    uint32_t abi_patch;
    uint32_t pointer_size;
    uint32_t char16_size;
    uint32_t range_size;
    uint32_t bracket_match_size;
    uint32_t view_line_info_size;
    uint32_t hidden_area_size;
    uint32_t diff_change_size;
    uint32_t diff_inner_change_size;
    uint32_t diff_move_size;
};

// Keybinding resolution snapshot. String pointers are owned by the
// keybinding handle and remain valid until the next operation on that handle that resolves
// or resets chord state. Callers must copy them if they need a longer lifetime.
struct mn_keybinding_resolution
{
    uint32_t struct_size;
    int32_t kind;
    int32_t prevent_default;
    const char* command_id;
    const char* chord_label;
};


// Current native ABI discovery. The packed version is 0x00MMmmpp.
MN_API uint32_t MN_CALL mn_abi_version();
MN_API int MN_CALL mn_abi_get_info(mn_abi_info* info);

MN_API mn_model_handle MN_CALL mn_model_create(const mn_char16* text, const char* language_id);
MN_API void MN_CALL mn_model_destroy(mn_model_handle handle);
MN_API size_t MN_CALL mn_model_length(mn_model_handle handle);
MN_API size_t MN_CALL mn_model_line_count(mn_model_handle handle);
MN_API uint64_t MN_CALL mn_model_version_id(mn_model_handle handle);
MN_API int MN_CALL mn_model_can_undo(mn_model_handle handle);
MN_API int MN_CALL mn_model_can_redo(mn_model_handle handle);
MN_API int MN_CALL mn_model_undo(mn_model_handle handle);
MN_API int MN_CALL mn_model_redo(mn_model_handle handle);
MN_API int MN_CALL mn_model_begin_undo_transaction(mn_model_handle handle);
MN_API int MN_CALL mn_model_end_undo_transaction(mn_model_handle handle);
MN_API int MN_CALL mn_model_rollback_undo_transaction(mn_model_handle handle);
MN_API int MN_CALL mn_model_push_undo_stop(mn_model_handle handle);
MN_API int MN_CALL mn_model_in_undo_transaction(mn_model_handle handle);
MN_API int MN_CALL mn_model_set_text(mn_model_handle handle, const mn_char16* text);
MN_API size_t MN_CALL mn_model_copy_text(mn_model_handle handle, mn_char16* buffer, size_t buffer_chars);
MN_API size_t MN_CALL mn_model_copy_text_range(mn_model_handle handle, size_t start, size_t length, mn_char16* buffer, size_t buffer_chars);
MN_API size_t MN_CALL mn_model_copy_line(mn_model_handle handle, int32_t line_number, mn_char16* buffer, size_t buffer_chars);
MN_API int32_t MN_CALL mn_model_line_max_column(mn_model_handle handle, int32_t line_number);
MN_API int MN_CALL mn_model_replace(mn_model_handle handle, size_t start, size_t end, const mn_char16* text);
MN_API size_t MN_CALL mn_model_replace_all(mn_model_handle handle, const mn_char16* query, const mn_char16* replacement, int match_case, int whole_word, size_t limit);
MN_API int MN_CALL mn_model_set_language(mn_model_handle handle, const char* language_id);
MN_API size_t MN_CALL mn_model_copy_language(mn_model_handle handle, char* buffer, size_t buffer_bytes);
MN_API int MN_CALL mn_model_get_position_at(mn_model_handle handle, size_t offset, int32_t* line_number, int32_t* column);
MN_API int MN_CALL mn_model_get_offset_at(mn_model_handle handle, int32_t line_number, int32_t column, size_t* offset);
MN_API int MN_CALL mn_model_get_eol(mn_model_handle handle);
MN_API int MN_CALL mn_model_set_eol(mn_model_handle handle, int eol);
MN_API size_t MN_CALL mn_model_find_matches(mn_model_handle handle, const mn_char16* query, int match_case, int whole_word, size_t limit, mn_range* ranges, size_t range_capacity);
MN_API int MN_CALL mn_model_find_matching_bracket(mn_model_handle handle, int32_t line_number, int32_t column, mn_bracket_match* result);


#ifdef _WIN32
// Windows-only native TSF host. These functions keep
// document text in the existing native TextModel/PieceTree; callbacks cover only
// host-owned selection, screen geometry and composition decoration state.
MN_API mn_tsf_handle MN_CALL mn_tsf_create(mn_model_handle model, intptr_t hwnd, const mn_tsf_callbacks* callbacks);
MN_API void MN_CALL mn_tsf_destroy(mn_tsf_handle handle);
MN_API int MN_CALL mn_tsf_focus_enter(mn_tsf_handle handle);
MN_API int MN_CALL mn_tsf_focus_leave(mn_tsf_handle handle);
MN_API void MN_CALL mn_tsf_notify_selection_changed(mn_tsf_handle handle);
MN_API void MN_CALL mn_tsf_set_read_only(mn_tsf_handle handle, int read_only);
MN_API int MN_CALL mn_tsf_key_down(mn_tsf_handle handle, uint32_t virtual_key, uint32_t message_lparam);
MN_API int MN_CALL mn_tsf_key_up(mn_tsf_handle handle, uint32_t virtual_key, uint32_t message_lparam);
#endif

// Authoritative UTF-16 segmentation ABI. Offsets are UTF-16 code units.
MN_API size_t MN_CALL mn_unicode_previous_grapheme_boundary(const mn_char16* text, size_t length, size_t offset);
MN_API size_t MN_CALL mn_unicode_next_grapheme_boundary(const mn_char16* text, size_t length, size_t offset);
MN_API size_t MN_CALL mn_unicode_previous_word_boundary(const mn_char16* text, size_t length, size_t offset);
MN_API size_t MN_CALL mn_unicode_next_word_boundary(const mn_char16* text, size_t length, size_t offset);


// Native diff ABI. Results are immutable and owned by the returned handle.
MN_API mn_diff_handle MN_CALL mn_diff_compute(const mn_char16* original, const mn_char16* modified, int ignore_trim_whitespace, int compute_moves);
MN_API void MN_CALL mn_diff_destroy(mn_diff_handle handle);
MN_API size_t MN_CALL mn_diff_change_count(mn_diff_handle handle);
MN_API int MN_CALL mn_diff_get_change(mn_diff_handle handle, size_t index, mn_diff_change* change);
MN_API int MN_CALL mn_diff_get_inner_change(mn_diff_handle handle, size_t change_index, size_t inner_index, mn_diff_inner_change* change);
MN_API size_t MN_CALL mn_diff_move_count(mn_diff_handle handle);
MN_API int MN_CALL mn_diff_get_move(mn_diff_handle handle, size_t index, mn_diff_move* move);

// View/projection ABI. The view owns a shared reference to the model, so it
// remains valid even if the original model handle is released first.
MN_API mn_view_handle MN_CALL mn_view_create(mn_model_handle model_handle);
MN_API void MN_CALL mn_view_destroy(mn_view_handle handle);
MN_API int MN_CALL mn_view_set_wrap_column(mn_view_handle handle, int32_t wrap_column);
MN_API int MN_CALL mn_view_set_wrap_width(mn_view_handle handle, double wrap_width);
MN_API int MN_CALL mn_view_set_line_height(mn_view_handle handle, double line_height);
MN_API int MN_CALL mn_view_set_measure_callback(mn_view_handle handle, mn_measure_text_callback callback, void* context);
MN_API int MN_CALL mn_view_rebuild(mn_view_handle handle);
MN_API int32_t MN_CALL mn_view_line_count(mn_view_handle handle);
MN_API size_t MN_CALL mn_view_copy_line(mn_view_handle handle, int32_t view_line_number, mn_char16* buffer, size_t buffer_chars);
MN_API int MN_CALL mn_view_get_line_info(mn_view_handle handle, int32_t view_line_number, mn_view_line_info* info);
MN_API double MN_CALL mn_view_get_vertical_offset(mn_view_handle handle, int32_t view_line_number);
MN_API double MN_CALL mn_view_total_height(mn_view_handle handle);
MN_API int32_t MN_CALL mn_view_line_at_vertical_offset(mn_view_handle handle, double offset);
MN_API int MN_CALL mn_view_model_to_view(mn_view_handle handle, int32_t model_line, int32_t model_column, int32_t* view_line, int32_t* view_column);
MN_API int MN_CALL mn_view_view_to_model(mn_view_handle handle, int32_t view_line, int32_t view_column, int32_t* model_line, int32_t* model_column);
MN_API int MN_CALL mn_view_set_hidden_areas(mn_view_handle handle, const mn_hidden_area* areas, size_t count);
MN_API uint64_t MN_CALL mn_view_add_view_zone(mn_view_handle handle, int32_t after_line_number, double height);
MN_API int MN_CALL mn_view_remove_view_zone(mn_view_handle handle, uint64_t id);
MN_API void MN_CALL mn_view_clear_view_zones(mn_view_handle handle);
MN_API uint64_t MN_CALL mn_view_add_injected_text(mn_view_handle handle, int32_t line_number, int32_t column, const mn_char16* text, int affinity, int32_t order);
MN_API int MN_CALL mn_view_remove_injected_text(mn_view_handle handle, uint64_t id);
MN_API void MN_CALL mn_view_clear_injected_text(mn_view_handle handle);
MN_API double MN_CALL mn_view_scroll_top_to_reveal(mn_view_handle handle, mn_range range, double viewport_height, double current_scroll_top, int strategy, double padding);


// Context-key and keybinding resolver ABI. Context strings and command ids are UTF-8.
MN_API mn_keybinding_handle MN_CALL mn_keybinding_create();
MN_API void MN_CALL mn_keybinding_destroy(mn_keybinding_handle handle);
MN_API int MN_CALL mn_keybinding_set_context_bool(mn_keybinding_handle handle, const char* key, int value);
MN_API int MN_CALL mn_keybinding_set_context_number(mn_keybinding_handle handle, const char* key, double value);
MN_API int MN_CALL mn_keybinding_set_context_string(mn_keybinding_handle handle, const char* key, const char* value);
MN_API int MN_CALL mn_keybinding_reset_context(mn_keybinding_handle handle, const char* key);
MN_API int MN_CALL mn_keybinding_context_matches(mn_keybinding_handle handle, const char* expression);
MN_API uint64_t MN_CALL mn_keybinding_register(mn_keybinding_handle handle, const char* command_id, const char* chord, const char* when_clause, int32_t weight, int prevent_default);
MN_API int MN_CALL mn_keybinding_unregister(mn_keybinding_handle handle, uint64_t token);
MN_API void MN_CALL mn_keybinding_reset_chord(mn_keybinding_handle handle);
MN_API int MN_CALL mn_keybinding_in_chord_mode(mn_keybinding_handle handle);
// Resolve result: 0 no match, 1 command, 2 waiting for more chord strokes, 3 cancelled chord, -1 error.
MN_API int MN_CALL mn_keybinding_resolve(mn_keybinding_handle handle, const char* key, uint8_t modifiers, int* prevent_default);
MN_API size_t MN_CALL mn_keybinding_copy_resolved_command(mn_keybinding_handle handle, char* buffer, size_t buffer_bytes);
MN_API size_t MN_CALL mn_keybinding_copy_pending_chord(mn_keybinding_handle handle, char* buffer, size_t buffer_bytes);
MN_API int MN_CALL mn_keybinding_resolve_snapshot(mn_keybinding_handle handle, const char* key, uint8_t modifiers, mn_keybinding_resolution* result);
