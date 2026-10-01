#pragma once

#include "TextTypes.h"

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace MonacoNative::Core
{
    using DecorationId = std::uint64_t;

    struct DecorationOptions final
    {
        std::string className{};
        std::string inlineClassName{};
        bool isWholeLine{};
        DecorationStickiness stickiness{DecorationStickiness::AlwaysGrowsWhenTypingAtEdges};
    };

    struct Decoration final
    {
        DecorationId id{};
        Range range{};
        DecorationOptions options{};
    };

    class DecorationCollection final
    {
    public:
        [[nodiscard]] DecorationId Add(Range range, DecorationOptions options = {});
        bool Remove(DecorationId id);
        bool SetRange(DecorationId id, Range range);
        void Clear();
        [[nodiscard]] std::optional<Decoration> Get(DecorationId id) const;
        [[nodiscard]] std::vector<Decoration> QueryLine(std::int32_t lineNumber) const;
        [[nodiscard]] const std::vector<Decoration>& All() const noexcept;

    private:
        DecorationId nextId_{1};
        std::vector<Decoration> decorations_{};
    };
}
