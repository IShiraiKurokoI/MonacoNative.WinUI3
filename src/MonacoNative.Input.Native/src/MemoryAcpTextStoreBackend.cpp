#include "MonacoNative/Input/Native/MemoryAcpTextStoreBackend.h"

#include <algorithm>
#include <limits>
#include <stdexcept>

namespace MonacoNative::Input::Native
{
    MemoryAcpTextStoreBackend::MemoryAcpTextStoreBackend(std::u16string text)
        : text_(std::move(text))
    {
        if (text_.size() > static_cast<std::size_t>(std::numeric_limits<std::int32_t>::max()))
        {
            throw std::length_error("ACP text store exceeds LONG address space");
        }
    }

    std::int32_t MemoryAcpTextStoreBackend::Length() const noexcept
    {
        return static_cast<std::int32_t>(text_.size());
    }

    std::u16string MemoryAcpTextStoreBackend::GetText(std::int32_t start, std::int32_t end) const
    {
        if (!IsValidRange(start, end))
        {
            throw std::out_of_range("ACP text range is outside the document");
        }

        const auto count = static_cast<std::size_t>(end - start);
        return text_.substr(static_cast<std::size_t>(start), count);
    }

    AcpSelection MemoryAcpTextStoreBackend::GetSelection() const noexcept
    {
        return selection_;
    }

    bool MemoryAcpTextStoreBackend::SetSelection(const AcpSelection& selection) noexcept
    {
        if (!IsValidRange(selection.start, selection.end))
        {
            return false;
        }

        selection_ = selection;
        return true;
    }

    bool MemoryAcpTextStoreBackend::Replace(
        std::int32_t start,
        std::int32_t end,
        std::u16string_view text,
        AcpTextChange& change)
    {
        if (!IsValidRange(start, end))
        {
            return false;
        }

        const auto removed = static_cast<std::size_t>(end - start);
        const auto newSize = text_.size() - removed + text.size();
        if (newSize > static_cast<std::size_t>(std::numeric_limits<std::int32_t>::max()))
        {
            return false;
        }

        text_.replace(
            static_cast<std::size_t>(start),
            removed,
            text.empty() ? u"" : text.data(),
            text.size());

        const auto newEnd = start + static_cast<std::int32_t>(text.size());
        change = AcpTextChange{start, end, newEnd};
        selection_ = AcpSelection{newEnd, newEnd, false, false};
        return true;
    }

    bool MemoryAcpTextStoreBackend::IsValidRange(std::int32_t start, std::int32_t end) const noexcept
    {
        return start >= 0 && end >= start && end <= Length();
    }
}
