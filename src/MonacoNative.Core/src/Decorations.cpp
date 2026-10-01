#include "MonacoNative/Core/Decorations.h"

#include <algorithm>

namespace MonacoNative::Core
{
    DecorationId DecorationCollection::Add(Range range, DecorationOptions options)
    {
        const auto id = nextId_++;
        decorations_.push_back(Decoration{id, range, std::move(options)});
        return id;
    }

    bool DecorationCollection::SetRange(DecorationId id, Range range)
    {
        const auto iterator = std::find_if(decorations_.begin(), decorations_.end(), [id](const Decoration& decoration)
        {
            return decoration.id == id;
        });
        if (iterator == decorations_.end())
        {
            return false;
        }
        iterator->range = range;
        return true;
    }

    bool DecorationCollection::Remove(DecorationId id)
    {
        const auto iterator = std::find_if(decorations_.begin(), decorations_.end(), [id](const Decoration& decoration)
        {
            return decoration.id == id;
        });
        if (iterator == decorations_.end())
        {
            return false;
        }
        decorations_.erase(iterator);
        return true;
    }

    void DecorationCollection::Clear()
    {
        decorations_.clear();
    }

    std::optional<Decoration> DecorationCollection::Get(DecorationId id) const
    {
        const auto iterator = std::find_if(decorations_.begin(), decorations_.end(), [id](const Decoration& decoration)
        {
            return decoration.id == id;
        });
        if (iterator == decorations_.end())
        {
            return std::nullopt;
        }
        return *iterator;
    }

    std::vector<Decoration> DecorationCollection::QueryLine(std::int32_t lineNumber) const
    {
        std::vector<Decoration> output;
        for (const auto& decoration : decorations_)
        {
            if (lineNumber >= decoration.range.startLineNumber && lineNumber <= decoration.range.endLineNumber)
            {
                output.push_back(decoration);
            }
        }
        return output;
    }

    const std::vector<Decoration>& DecorationCollection::All() const noexcept
    {
        return decorations_;
    }
}
