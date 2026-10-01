#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace MonacoNative::Core
{
    struct Position final
    {
        std::int32_t lineNumber{1};
        std::int32_t column{1};

        friend bool operator==(const Position&, const Position&) = default;
    };

    struct Range final
    {
        std::int32_t startLineNumber{1};
        std::int32_t startColumn{1};
        std::int32_t endLineNumber{1};
        std::int32_t endColumn{1};

        [[nodiscard]] bool IsEmpty() const noexcept
        {
            return startLineNumber == endLineNumber && startColumn == endColumn;
        }

        friend bool operator==(const Range&, const Range&) = default;
    };

    struct Selection final
    {
        Position anchor{};
        Position active{};

        [[nodiscard]] bool IsEmpty() const noexcept
        {
            return anchor == active;
        }

        friend bool operator==(const Selection&, const Selection&) = default;
    };

    struct TextEdit final
    {
        Range range{};
        std::u16string text{};
    };

    struct OffsetEdit final
    {
        std::size_t start{};
        std::size_t end{};
        std::u16string text{};
    };

    enum class DecorationStickiness
    {
        AlwaysGrowsWhenTypingAtEdges,
        NeverGrowsWhenTypingAtEdges,
        GrowsOnlyWhenTypingBefore,
        GrowsOnlyWhenTypingAfter,
    };

    struct ModelContentChange final
    {
        Range range{};
        std::size_t rangeOffset{};
        std::size_t rangeLength{};
        std::u16string text{};
    };

    struct ModelContentChangedEvent final
    {
        std::uint64_t versionId{};
        bool isUndoing{};
        bool isRedoing{};
        std::vector<ModelContentChange> changes{};
    };
}
