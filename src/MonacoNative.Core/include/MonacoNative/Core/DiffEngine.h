#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace MonacoNative::Core
{
    enum class DiffKind : std::uint8_t
    {
        Equal,
        Insert,
        Delete,
    };

    struct DiffLine final
    {
        DiffKind kind{DiffKind::Equal};
        std::int32_t originalLine{};
        std::int32_t modifiedLine{};
        std::u16string text{};
    };

    struct DiffInnerChange final
    {
        std::int32_t originalLine{};
        std::int32_t originalStartColumn{};
        std::int32_t originalEndColumn{};
        std::int32_t modifiedLine{};
        std::int32_t modifiedStartColumn{};
        std::int32_t modifiedEndColumn{};
    };

    // Line intervals use 1-based, end-exclusive coordinates. A zero-length interval
    // represents an insertion/deletion point, matching Monaco's diff line mappings.
    struct DiffChange final
    {
        std::int32_t originalStartLine{};
        std::int32_t originalEndLineExclusive{};
        std::int32_t modifiedStartLine{};
        std::int32_t modifiedEndLineExclusive{};
        std::vector<DiffInnerChange> innerChanges{};
    };

    struct DiffMove final
    {
        std::int32_t originalStartLine{};
        std::int32_t originalEndLineExclusive{};
        std::int32_t modifiedStartLine{};
        std::int32_t modifiedEndLineExclusive{};
    };

    struct DiffOptions final
    {
        bool ignoreTrimWhitespace{true};
        bool computeMoves{true};
    };

    struct DiffResult final
    {
        std::vector<DiffLine> lines{};
        std::vector<DiffChange> changes{};
        std::vector<DiffMove> moves{};
    };

    class DiffEngine final
    {
    public:
        [[nodiscard]] static DiffResult ComputeDiff(
            std::u16string_view original,
            std::u16string_view modified,
            DiffOptions options = {});

        // Compatibility surface retained for existing callers and tests.
        [[nodiscard]] static std::vector<DiffLine> ComputeLineDiff(
            std::u16string_view original,
            std::u16string_view modified);
    };
}
