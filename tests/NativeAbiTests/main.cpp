#include "TestHarness.h"
#include "MonacoNative.Core.Native.h"

#include <array>
#include <cstddef>
#include <iostream>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

using MonacoNative::Tests::Suite;

namespace
{
    // The public ABI uses 16-bit wchar_t on Windows and char16_t elsewhere.
    // Convert UTF-16 code units instead of reinterpreting pointers or relying
    // on implicit char16_t* -> wchar_t* conversions (which MSVC rejects).
    std::basic_string<mn_char16> AbiText(std::u16string_view text)
    {
        std::basic_string<mn_char16> result;
        result.reserve(text.size());
        for (const char16_t codeUnit : text)
        {
            result.push_back(static_cast<mn_char16>(codeUnit));
        }
        return result;
    }

    std::u16string CopyText(mn_model_handle handle)
    {
        const auto required = mn_model_copy_text(handle, nullptr, 0);
        MN_CHECK(required > 0);
        std::vector<mn_char16> buffer(required);
        MN_CHECK_EQ(mn_model_copy_text(handle, buffer.data(), buffer.size()), required);
        std::u16string result;
        for (std::size_t i = 0; i + 1 < required; ++i)
        {
            result.push_back(static_cast<char16_t>(buffer[i]));
        }
        return result;
    }

    std::u16string CopyRange(mn_model_handle handle, std::size_t start, std::size_t length)
    {
        const auto required = mn_model_copy_text_range(handle, start, length, nullptr, 0);
        MN_CHECK(required > 0);
        std::vector<mn_char16> buffer(required);
        MN_CHECK_EQ(mn_model_copy_text_range(handle, start, length, buffer.data(), buffer.size()), required);
        std::u16string result;
        for (std::size_t i = 0; i + 1 < required; ++i)
        {
            result.push_back(static_cast<char16_t>(buffer[i]));
        }
        return result;
    }

    std::string CopyLanguage(mn_model_handle handle)
    {
        const auto required = mn_model_copy_language(handle, nullptr, 0);
        MN_CHECK(required > 0);
        std::vector<char> buffer(required);
        MN_CHECK_EQ(mn_model_copy_language(handle, buffer.data(), buffer.size()), required);
        return std::string(buffer.data());
    }



    std::string CopyResolvedCommand(mn_keybinding_handle handle)
    {
        const auto required = mn_keybinding_copy_resolved_command(handle, nullptr, 0);
        MN_CHECK(required > 0);
        std::vector<char> buffer(required);
        MN_CHECK_EQ(mn_keybinding_copy_resolved_command(handle, buffer.data(), buffer.size()), required);
        return std::string(buffer.data());
    }

    std::string CopyPendingChord(mn_keybinding_handle handle)
    {
        const auto required = mn_keybinding_copy_pending_chord(handle, nullptr, 0);
        MN_CHECK(required > 0);
        std::vector<char> buffer(required);
        MN_CHECK_EQ(mn_keybinding_copy_pending_chord(handle, buffer.data(), buffer.size()), required);
        return std::string(buffer.data());
    }

    void AbiVersionContract()
    {
        MN_CHECK_EQ(mn_abi_version(), MN_ABI_VERSION_PACKED);
        mn_abi_info info{};
        info.struct_size = sizeof(info);
        MN_CHECK_EQ(mn_abi_get_info(&info), 1);
        MN_CHECK_EQ(info.abi_version, MN_ABI_VERSION_PACKED);
        MN_CHECK_EQ(info.abi_major, MN_ABI_VERSION_MAJOR);
        MN_CHECK_EQ(info.abi_minor, MN_ABI_VERSION_MINOR);
        MN_CHECK_EQ(info.abi_patch, MN_ABI_VERSION_PATCH);
        MN_CHECK_EQ(info.pointer_size, sizeof(void*));
        MN_CHECK_EQ(info.char16_size, 2U);
        MN_CHECK_EQ(info.range_size, sizeof(mn_range));
        MN_CHECK_EQ(info.bracket_match_size, sizeof(mn_bracket_match));
        MN_CHECK_EQ(info.view_line_info_size, sizeof(mn_view_line_info));
        MN_CHECK_EQ(info.hidden_area_size, sizeof(mn_hidden_area));
        MN_CHECK_EQ(info.diff_change_size, sizeof(mn_diff_change));
        MN_CHECK_EQ(info.diff_inner_change_size, sizeof(mn_diff_inner_change));
        MN_CHECK_EQ(info.diff_move_size, sizeof(mn_diff_move));

        mn_abi_info shortInfo{};
        shortInfo.struct_size = sizeof(uint32_t) * 5;
        MN_CHECK_EQ(mn_abi_get_info(&shortInfo), 1);
        MN_CHECK_EQ(shortInfo.abi_major, MN_ABI_VERSION_MAJOR);
        MN_CHECK_EQ(mn_abi_get_info(nullptr), 0);
    }

    void LifetimeAndBasicProperties()
    {
        auto handle = mn_model_create(AbiText(u"alpha\nbeta").c_str(), "cpp");
        MN_CHECK(handle != nullptr);
        MN_CHECK_EQ(mn_model_length(handle), 10U);
        MN_CHECK_EQ(mn_model_line_count(handle), 2U);
        MN_CHECK_EQ(mn_model_version_id(handle), 1U);
        MN_CHECK_EQ(CopyText(handle), std::u16string(u"alpha\nbeta"));
        MN_CHECK_EQ(CopyLanguage(handle), std::string("cpp"));
        mn_model_destroy(handle);
        mn_model_destroy(nullptr);
    }

    void CopyApisAndTruncation()
    {
        auto handle = mn_model_create(AbiText(u"a\r\nbeta").c_str(), "plaintext");
        MN_CHECK(handle != nullptr);
        MN_CHECK_EQ(CopyRange(handle, 1, 2), std::u16string(u"\r\n"));
        MN_CHECK_EQ(CopyRange(handle, 3, 100), std::u16string(u"beta"));
        MN_CHECK_EQ(mn_model_copy_text_range(handle, 999, 1, nullptr, 0), 0U);

        mn_char16 tiny[3]{static_cast<mn_char16>('x'), static_cast<mn_char16>('x'), static_cast<mn_char16>('x')};
        const auto required = mn_model_copy_text(handle, tiny, 3);
        MN_CHECK(required > 3);
        MN_CHECK_EQ(static_cast<char16_t>(tiny[0]), u'a');
        MN_CHECK_EQ(static_cast<char16_t>(tiny[1]), u'\r');
        MN_CHECK_EQ(static_cast<char16_t>(tiny[2]), u'\0');

        mn_char16 line[16]{};
        MN_CHECK_EQ(mn_model_copy_line(handle, 2, line, 16), 5U);
        MN_CHECK_EQ(static_cast<char16_t>(line[0]), u'b');
        MN_CHECK_EQ(mn_model_line_max_column(handle, 2), 5);
        MN_CHECK_EQ(mn_model_copy_line(handle, 99, nullptr, 0), 0U);
        MN_CHECK_EQ(mn_model_line_max_column(handle, 99), 0);
        mn_model_destroy(handle);
    }

    void EditingHistoryAndEol()
    {
        auto handle = mn_model_create(AbiText(u"one\r\ntwo").c_str(), "plaintext");
        MN_CHECK(handle != nullptr);
        MN_CHECK_EQ(mn_model_get_eol(handle), 1);
        MN_CHECK_EQ(mn_model_replace(handle, 0, 3, AbiText(u"ONE").c_str()), 1);
        MN_CHECK_EQ(CopyText(handle), std::u16string(u"ONE\r\ntwo"));
        MN_CHECK_EQ(mn_model_can_undo(handle), 1);
        MN_CHECK_EQ(mn_model_undo(handle), 1);
        MN_CHECK_EQ(CopyText(handle), std::u16string(u"one\r\ntwo"));
        MN_CHECK_EQ(mn_model_can_redo(handle), 1);
        MN_CHECK_EQ(mn_model_redo(handle), 1);
        MN_CHECK_EQ(CopyText(handle), std::u16string(u"ONE\r\ntwo"));

        MN_CHECK_EQ(mn_model_set_eol(handle, 0), 1);
        MN_CHECK_EQ(CopyText(handle), std::u16string(u"ONE\ntwo"));
        MN_CHECK_EQ(mn_model_get_eol(handle), 0);
        MN_CHECK_EQ(mn_model_set_eol(handle, 2), 0);
        MN_CHECK_EQ(mn_model_replace(handle, 100, 101, AbiText(u"x").c_str()), 0);
        mn_model_destroy(handle);
    }

    void SearchReplaceAndBrackets()
    {
        auto handle = mn_model_create(AbiText(u"Alpha alpha alphabet ALPHA (x)").c_str(), "plaintext");
        MN_CHECK(handle != nullptr);
        const auto count = mn_model_find_matches(handle, AbiText(u"alpha").c_str(), 0, 1, 1000, nullptr, 0);
        MN_CHECK_EQ(count, 3U);
        std::vector<mn_range> ranges(count);
        MN_CHECK_EQ(mn_model_find_matches(handle, AbiText(u"alpha").c_str(), 0, 1, 1000, ranges.data(), ranges.size()), count);
        MN_CHECK_EQ(ranges[0].start_column, 1);
        MN_CHECK_EQ(ranges[1].start_column, 7);
        MN_CHECK_EQ(mn_model_find_matches(handle, AbiText(u"alpha").c_str(), 0, 1, 2, nullptr, 0), 2U);

        MN_CHECK_EQ(mn_model_replace_all(handle, AbiText(u"alpha").c_str(), AbiText(u"X").c_str(), 0, 1, 2), 2U);
        MN_CHECK_EQ(CopyText(handle), std::u16string(u"X X alphabet ALPHA (x)"));
        MN_CHECK_EQ(mn_model_undo(handle), 1);

        mn_bracket_match bracket{};
        MN_CHECK_EQ(mn_model_find_matching_bracket(handle, 1, 28, &bracket), 1);
        MN_CHECK_EQ(bracket.bracket_column, 28);
        MN_CHECK_EQ(bracket.match_column, 30);
        MN_CHECK_EQ(mn_model_find_matching_bracket(handle, 1, 1, &bracket), 0);
        MN_CHECK_EQ(mn_model_find_matching_bracket(handle, 1, 1, nullptr), -1);
        mn_model_destroy(handle);
    }

    void LanguageAndCoordinates()
    {
        auto handle = mn_model_create(AbiText(u"abc\ndef").c_str(), nullptr);
        MN_CHECK(handle != nullptr);
        MN_CHECK_EQ(CopyLanguage(handle), std::string("plaintext"));
        MN_CHECK_EQ(mn_model_set_language(handle, "json"), 1);
        MN_CHECK_EQ(CopyLanguage(handle), std::string("json"));

        int line = 0;
        int column = 0;
        MN_CHECK_EQ(mn_model_get_position_at(handle, 5, &line, &column), 1);
        MN_CHECK_EQ(line, 2);
        MN_CHECK_EQ(column, 2);
        std::size_t offset = 0;
        MN_CHECK_EQ(mn_model_get_offset_at(handle, 2, 2, &offset), 1);
        MN_CHECK_EQ(offset, 5U);
        MN_CHECK_EQ(mn_model_get_position_at(handle, 0, nullptr, &column), 0);
        MN_CHECK_EQ(mn_model_get_offset_at(handle, 1, 1, nullptr), 0);
        mn_model_destroy(handle);
    }


    double MN_CALL MeasureDoubleWidth(void*, const mn_char16*, std::size_t length)
    {
        return static_cast<double>(length) * 2.0;
    }

    std::u16string CopyViewLine(mn_view_handle handle, std::int32_t lineNumber)
    {
        const auto required = mn_view_copy_line(handle, lineNumber, nullptr, 0);
        MN_CHECK(required > 0);
        std::vector<mn_char16> buffer(required);
        MN_CHECK_EQ(mn_view_copy_line(handle, lineNumber, buffer.data(), buffer.size()), required);
        std::u16string result;
        for (std::size_t i = 0; i + 1 < required; ++i)
        {
            result.push_back(static_cast<char16_t>(buffer[i]));
        }
        return result;
    }

    void ViewProjectionAndMeasuredWrapping()
    {
        auto model = mn_model_create(AbiText(u"abcdef\nsecond\nthird").c_str(), "plaintext");
        MN_CHECK(model != nullptr);
        auto view = mn_view_create(model);
        MN_CHECK(view != nullptr);
        MN_CHECK_EQ(mn_view_set_line_height(view, 18.0), 1);
        MN_CHECK_EQ(mn_view_set_measure_callback(view, MeasureDoubleWidth, nullptr), 1);
        MN_CHECK_EQ(mn_view_set_wrap_width(view, 6.1), 1);
        MN_CHECK_EQ(mn_view_line_count(view), 6);
        MN_CHECK_EQ(CopyViewLine(view, 1), std::u16string(u"abc"));
        MN_CHECK_EQ(CopyViewLine(view, 2), std::u16string(u"def"));

        mn_view_line_info info{};
        MN_CHECK_EQ(mn_view_get_line_info(view, 2, &info), 1);
        MN_CHECK_EQ(info.model_line_number, 1);
        MN_CHECK_EQ(info.wrapped_index, 1);
        MN_CHECK_EQ(mn_view_get_vertical_offset(view, 2), 18.0);
        MN_CHECK_EQ(mn_view_line_at_vertical_offset(view, 18.1), 2);

        int viewLine = 0;
        int viewColumn = 0;
        MN_CHECK_EQ(mn_view_model_to_view(view, 1, 5, &viewLine, &viewColumn), 1);
        MN_CHECK_EQ(viewLine, 2);
        int modelLine = 0;
        int modelColumn = 0;
        MN_CHECK_EQ(mn_view_view_to_model(view, viewLine, viewColumn, &modelLine, &modelColumn), 1);
        MN_CHECK_EQ(modelLine, 1);
        MN_CHECK_EQ(modelColumn, 5);

        mn_hidden_area hidden{2, 2};
        MN_CHECK_EQ(mn_view_set_hidden_areas(view, &hidden, 1), 1);
        MN_CHECK_EQ(mn_view_line_count(view), 4);
        const auto injected = mn_view_add_injected_text(view, 3, 1, AbiText(u">>").c_str(), 1, 0);
        MN_CHECK(injected != 0);
        MN_CHECK_EQ(CopyViewLine(view, 3), std::u16string(u">>t"));
        MN_CHECK_EQ(mn_view_remove_injected_text(view, injected), 1);
        const auto zone = mn_view_add_view_zone(view, 1, 7.0);
        MN_CHECK(zone != 0);
        MN_CHECK(mn_view_total_height(view) >= 79.0);
        MN_CHECK_EQ(mn_view_remove_view_zone(view, zone), 1);

        const mn_range reveal{3, 1, 3, 2};
        MN_CHECK(mn_view_scroll_top_to_reveal(view, reveal, 18.0, 0.0, 1, 0.0) >= 18.0);

        // The view holds a shared model reference and remains usable after the ABI model handle dies.
        mn_model_destroy(model);
        model = nullptr;
        MN_CHECK_EQ(mn_view_rebuild(view), 1);
        MN_CHECK(mn_view_line_count(view) > 0);
        mn_view_destroy(view);
    }

    void ViewNullContract()
    {
        MN_CHECK(mn_view_create(nullptr) == nullptr);
        MN_CHECK_EQ(mn_view_line_count(nullptr), 0);
        MN_CHECK_EQ(mn_view_set_wrap_width(nullptr, 100.0), 0);
        MN_CHECK_EQ(mn_view_set_measure_callback(nullptr, MeasureDoubleWidth, nullptr), 0);
        MN_CHECK_EQ(mn_view_copy_line(nullptr, 1, nullptr, 0), 0U);
        MN_CHECK_EQ(mn_view_get_line_info(nullptr, 1, nullptr), 0);
        mn_view_destroy(nullptr);
    }


    void UndoTransactionAbi()
    {
        auto model = mn_model_create(AbiText(u"abc").c_str(), "plaintext");
        MN_CHECK(model != nullptr);
        MN_CHECK_EQ(mn_model_begin_undo_transaction(model), 1);
        MN_CHECK_EQ(mn_model_in_undo_transaction(model), 1);
        MN_CHECK_EQ(mn_model_replace(model, 3, 3, AbiText(u"1").c_str()), 1);
        MN_CHECK_EQ(mn_model_replace(model, 4, 4, AbiText(u"2").c_str()), 1);
        MN_CHECK_EQ(mn_model_end_undo_transaction(model), 1);
        MN_CHECK_EQ(mn_model_in_undo_transaction(model), 0);
        MN_CHECK_EQ(CopyText(model), std::u16string(u"abc12"));
        MN_CHECK_EQ(mn_model_undo(model), 1);
        MN_CHECK_EQ(CopyText(model), std::u16string(u"abc"));

        MN_CHECK_EQ(mn_model_begin_undo_transaction(model), 1);
        MN_CHECK_EQ(mn_model_replace(model, 0, 0, AbiText(u"x").c_str()), 1);
        MN_CHECK_EQ(mn_model_rollback_undo_transaction(model), 1);
        MN_CHECK_EQ(CopyText(model), std::u16string(u"abc"));
        MN_CHECK_EQ(mn_model_push_undo_stop(model), 1);
        mn_model_destroy(model);
    }

    void DiffAbi()
    {
        auto diff = mn_diff_compute(
            AbiText(u"head\na1\na2\nb1\nb2\nend").c_str(),
            AbiText(u"head\nb1\nb2\na1\na2\nend").c_str(),
            0,
            1);
        MN_CHECK(diff != nullptr);
        MN_CHECK(mn_diff_change_count(diff) >= 2U);
        mn_diff_change change{};
        MN_CHECK_EQ(mn_diff_get_change(diff, 0, &change), 1);
        MN_CHECK(change.original_start_line >= 1);
        MN_CHECK(change.original_end_line_exclusive >= change.original_start_line);
        MN_CHECK(mn_diff_move_count(diff) >= 1U);
        mn_diff_move move{};
        MN_CHECK_EQ(mn_diff_get_move(diff, 0, &move), 1);
        MN_CHECK(move.original_end_line_exclusive > move.original_start_line);
        MN_CHECK_EQ(mn_diff_get_change(diff, 9999, &change), 0);
        mn_diff_destroy(diff);
        mn_diff_destroy(nullptr);

        diff = mn_diff_compute(AbiText(u"alpha beta").c_str(), AbiText(u"alpha BETA").c_str(), 0, 0);
        MN_CHECK(diff != nullptr);
        MN_CHECK_EQ(mn_diff_change_count(diff), 1U);
        MN_CHECK_EQ(mn_diff_get_change(diff, 0, &change), 1);
        MN_CHECK(change.inner_change_count >= 1U);
        mn_diff_inner_change inner{};
        MN_CHECK_EQ(mn_diff_get_inner_change(diff, 0, 0, &inner), 1);
        MN_CHECK(inner.original_end_column > inner.original_start_column);
        mn_diff_destroy(diff);

        MN_CHECK_EQ(mn_diff_change_count(nullptr), 0U);
        MN_CHECK_EQ(mn_diff_move_count(nullptr), 0U);
        MN_CHECK_EQ(mn_diff_get_change(nullptr, 0, &change), 0);
        MN_CHECK_EQ(mn_diff_get_inner_change(nullptr, 0, 0, &inner), 0);
        MN_CHECK_EQ(mn_diff_get_move(nullptr, 0, &move), 0);
    }


    void UnicodeSegmentationAbi()
    {
        const std::u16string text = u"A\U0001F469\u200D\U0001F4BBB";
        const auto nativeText = AbiText(text);
        const auto* ptr = nativeText.data();
        MN_CHECK_EQ(mn_unicode_next_grapheme_boundary(ptr, text.size(), 0), 1U);
        MN_CHECK_EQ(mn_unicode_next_grapheme_boundary(ptr, text.size(), 1), 6U);
        MN_CHECK_EQ(mn_unicode_previous_grapheme_boundary(ptr, text.size(), 6), 1U);

        const std::u16string words = u"one  two...three";
        const auto nativeWords = AbiText(words);
        const auto* wordPtr = nativeWords.data();
        MN_CHECK_EQ(mn_unicode_next_word_boundary(wordPtr, words.size(), 0), 5U);
        MN_CHECK_EQ(mn_unicode_next_word_boundary(wordPtr, words.size(), 5), 8U);
        MN_CHECK_EQ(mn_unicode_previous_word_boundary(wordPtr, words.size(), 11), 8U);
        MN_CHECK_EQ(mn_unicode_next_grapheme_boundary(nullptr, 0, 0), 0U);
    }

    void KeybindingAbi()
    {
        auto service = mn_keybinding_create();
        MN_CHECK(service != nullptr);
        MN_CHECK_EQ(mn_keybinding_set_context_bool(service, "editorTextFocus", 1), 1);
        MN_CHECK_EQ(mn_keybinding_set_context_string(service, "languageId", "cpp"), 1);
        MN_CHECK_EQ(mn_keybinding_context_matches(service, "editorTextFocus && languageId == 'cpp'"), 1);
        const auto save = mn_keybinding_register(service, "file.save", "Ctrl+S", "editorTextFocus", 10, 1);
        const auto comment = mn_keybinding_register(service, "comment.line", "Ctrl+K Ctrl+C", "languageId == 'cpp'", 20, 1);
        MN_CHECK(save != 0);
        MN_CHECK(comment != 0);

        int preventDefault = 0;
        MN_CHECK_EQ(mn_keybinding_resolve(service, "S", 1, &preventDefault), 1);
        MN_CHECK_EQ(preventDefault, 1);
        MN_CHECK_EQ(CopyResolvedCommand(service), std::string("file.save"));

        MN_CHECK_EQ(mn_keybinding_resolve(service, "K", 1, &preventDefault), 2);
        MN_CHECK_EQ(mn_keybinding_in_chord_mode(service), 1);
        MN_CHECK_EQ(CopyPendingChord(service), std::string("Ctrl+K"));
        MN_CHECK_EQ(mn_keybinding_resolve(service, "C", 1, &preventDefault), 1);
        MN_CHECK_EQ(CopyResolvedCommand(service), std::string("comment.line"));
        MN_CHECK_EQ(mn_keybinding_in_chord_mode(service), 0);

        mn_keybinding_resolution snapshot{};
        snapshot.struct_size = sizeof(snapshot);
        MN_CHECK_EQ(mn_keybinding_resolve_snapshot(service, "S", 1, &snapshot), 1);
        MN_CHECK_EQ(snapshot.kind, 1);
        MN_CHECK_EQ(snapshot.prevent_default, 1);
        MN_CHECK(snapshot.command_id != nullptr);
        MN_CHECK_EQ(std::string(snapshot.command_id), std::string("file.save"));
        MN_CHECK(snapshot.chord_label != nullptr);
        MN_CHECK_EQ(std::string(snapshot.chord_label), std::string("Ctrl+S"));

        MN_CHECK_EQ(mn_keybinding_unregister(service, save), 1);
        MN_CHECK_EQ(mn_keybinding_unregister(service, save), 0);
        MN_CHECK_EQ(mn_keybinding_reset_context(service, "languageId"), 1);
        mn_keybinding_reset_chord(service);
        mn_keybinding_destroy(service);
    }

    void NullHandleContract()
    {
        MN_CHECK_EQ(mn_model_length(nullptr), 0U);
        MN_CHECK_EQ(mn_model_line_count(nullptr), 0U);
        MN_CHECK_EQ(mn_model_version_id(nullptr), 0U);
        MN_CHECK_EQ(mn_model_can_undo(nullptr), 0);
        MN_CHECK_EQ(mn_model_can_redo(nullptr), 0);
        MN_CHECK_EQ(mn_model_undo(nullptr), 0);
        MN_CHECK_EQ(mn_model_redo(nullptr), 0);
        MN_CHECK_EQ(mn_model_begin_undo_transaction(nullptr), 0);
        MN_CHECK_EQ(mn_model_end_undo_transaction(nullptr), 0);
        MN_CHECK_EQ(mn_model_rollback_undo_transaction(nullptr), 0);
        MN_CHECK_EQ(mn_model_push_undo_stop(nullptr), 0);
        MN_CHECK_EQ(mn_model_in_undo_transaction(nullptr), 0);
        MN_CHECK_EQ(mn_model_set_text(nullptr, AbiText(u"x").c_str()), 0);
        MN_CHECK_EQ(mn_model_copy_text(nullptr, nullptr, 0), 0U);
        MN_CHECK_EQ(mn_model_copy_language(nullptr, nullptr, 0), 0U);
        MN_CHECK_EQ(mn_model_get_eol(nullptr), -1);
        MN_CHECK_EQ(mn_model_set_eol(nullptr, 0), 0);
    }
}

// Each case can run in a fresh process. On Windows this prevents shared
// process state from masking defects in later cases, and a hung ABI call now
// identifies the exact contract case in CTest rather than stalling for 25 min.
int main(int argc, char* argv[])
{
    std::cerr << "[ABI] main() reached; native DLL loaded\n" << std::flush;
    struct Case
    {
        const char* id;
        const char* name;
        void (*run)();
    };
    constexpr std::array<Case, 13> cases{{
        {"version", "current native ABI version and layout", AbiVersionContract},
        {"lifetime", "lifetime and properties", LifetimeAndBasicProperties},
        {"copy", "copy APIs and truncation", CopyApisAndTruncation},
        {"editing", "editing, history, and EOL", EditingHistoryAndEol},
        {"search", "search, replace, and brackets", SearchReplaceAndBrackets},
        {"language", "language and coordinates", LanguageAndCoordinates},
        {"view", "view projection and measured wrapping", ViewProjectionAndMeasuredWrapping},
        {"view_null", "view null contract", ViewNullContract},
        {"undo", "undo transaction ABI", UndoTransactionAbi},
        {"diff", "diff ABI", DiffAbi},
        {"unicode", "unicode segmentation ABI", UnicodeSegmentationAbi},
        {"keybinding", "keybinding ABI", KeybindingAbi},
        {"null", "null-handle contract", NullHandleContract},
    }};

    if (argc != 1 && (argc != 3 || std::string_view(argv[1]) != "--case"))
    {
        std::cerr << "Usage: MonacoNative.NativeAbi.Tests [--case <id>]\n";
        return 2;
    }

    const std::string_view selected = (argc == 3) ? std::string_view(argv[2]) : std::string_view{};
    Suite suite("NativeAbiTests");
    bool found = selected.empty();
    for (const auto& test : cases)
    {
        if (!selected.empty() && selected != test.id) continue;
        found = true;
        std::cerr << "[RUN] " << test.id << " - " << test.name << '\n' << std::flush;
        suite.Run(test.name, test.run);
    }
    if (!found)
    {
        std::cerr << "Unknown ABI test case: " << selected << '\n';
        return 2;
    }
    return suite.Finish();
}
