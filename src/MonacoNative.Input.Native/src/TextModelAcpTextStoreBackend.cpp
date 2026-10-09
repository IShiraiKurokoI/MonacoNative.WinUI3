#include "MonacoNative/Input/Native/TextModelAcpTextStoreBackend.h"

#include <algorithm>
#include <limits>
#include <stdexcept>
#include <utility>

namespace MonacoNative::Input::Native
{
    namespace
    {
        constexpr auto kMaxAcp = static_cast<std::size_t>(std::numeric_limits<std::int32_t>::max());

        std::int32_t ClampToAcp(std::int64_t value) noexcept
        {
            return static_cast<std::int32_t>(std::clamp<std::int64_t>(
                value,
                0,
                std::numeric_limits<std::int32_t>::max()));
        }
    }

    TextModelAcpTextStoreBackend::TextModelAcpTextStoreBackend(
        std::shared_ptr<Core::TextModel> model,
        std::shared_ptr<Core::CursorModel> cursor)
        : model_(std::move(model)), cursor_(std::move(cursor))
    {
        if (!model_)
        {
            throw std::invalid_argument("TextModel ACP backend requires a model");
        }
        if (!IsAcpAddressable())
        {
            throw std::length_error("TextModel exceeds the ITextStoreACP LONG address space");
        }

        modelChangeToken_ = model_->OnDidChangeContent().Add([this](const Core::ModelContentChangedEvent& event)
        {
            OnModelChanged(event);
        });
        if (cursor_)
        {
            cursorSelectionToken_ = cursor_->OnDidChangeSelection().Add([this](const std::vector<Core::Selection>& selections)
            {
                OnCursorSelectionChanged(selections);
            });
            selection_ = SelectionFromCursor();
        }
    }

    TextModelAcpTextStoreBackend::~TextModelAcpTextStoreBackend()
    {
        if (cursor_ && cursorSelectionToken_ != 0)
        {
            cursor_->OnDidChangeSelection().Remove(cursorSelectionToken_);
        }
        if (model_ && modelChangeToken_ != 0)
        {
            model_->OnDidChangeContent().Remove(modelChangeToken_);
        }
    }

    std::int32_t TextModelAcpTextStoreBackend::Length() const noexcept
    {
        if (!model_)
        {
            return 0;
        }
        const auto length = model_->Length();
        if (length > kMaxAcp)
        {
            // ITextStoreACP exposes LONG ACPs. A model that grows beyond that
            // range is not addressable by TSF; operations are rejected by
            // IsValidRange/Replace. Saturating here keeps this noexcept API
            // from wrapping into a negative ACP.
            return std::numeric_limits<std::int32_t>::max();
        }
        return static_cast<std::int32_t>(length);
    }

    std::u16string TextModelAcpTextStoreBackend::GetText(std::int32_t start, std::int32_t end) const
    {
        if (!IsValidRange(start, end))
        {
            throw std::out_of_range("ACP text range is outside the TextModel");
        }

        return model_->GetValue(
            static_cast<std::size_t>(start),
            static_cast<std::size_t>(end - start));
    }

    AcpSelection TextModelAcpTextStoreBackend::GetSelection() const noexcept
    {
        return cursor_ ? SelectionFromCursor() : selection_;
    }

    bool TextModelAcpTextStoreBackend::SetSelection(const AcpSelection& selection) noexcept
    {
        if (!IsValidRange(selection.start, selection.end))
        {
            return false;
        }

        selection_ = selection;
        if (!cursor_)
        {
            return true;
        }

        ++suppressCursorSelectionNotifications_;
        const auto applied = ApplySelectionToCursor(selection);
        --suppressCursorSelectionNotifications_;
        if (applied)
        {
            selection_ = SelectionFromCursor();
        }
        return applied;
    }

    bool TextModelAcpTextStoreBackend::Replace(
        std::int32_t start,
        std::int32_t end,
        std::u16string_view text,
        AcpTextChange& change)
    {
        if (!IsValidRange(start, end) || !IsAcpAddressable())
        {
            return false;
        }

        const auto currentLength = model_->Length();
        const auto removed = static_cast<std::size_t>(end - start);
        if (text.size() > kMaxAcp || currentLength - removed > kMaxAcp - text.size())
        {
            return false;
        }

        // TSF edits bypass the managed MonacoTextModel.Replace path. Preserve
        // the actual pre-edit ACP selection, including its active-end direction.
        const auto previousSelection = GetSelection();
        ++suppressExternalNotifications_;
        ++suppressCursorSelectionNotifications_;
        try
        {
            model_->ApplyOffsetEdits({Core::OffsetEdit{
                static_cast<std::size_t>(start),
                static_cast<std::size_t>(end),
                std::u16string(text),
            }});
        }
        catch (...)
        {
            --suppressCursorSelectionNotifications_;
            --suppressExternalNotifications_;
            throw;
        }
        --suppressCursorSelectionNotifications_;
        --suppressExternalNotifications_;

        const auto newEnd = start + static_cast<std::int32_t>(text.size());
        change = AcpTextChange{start, end, newEnd};
        const Core::UndoSelection before{
            static_cast<std::size_t>(previousSelection.activeEndAtStart ? previousSelection.end : previousSelection.start),
            static_cast<std::size_t>(previousSelection.activeEndAtStart ? previousSelection.start : previousSelection.end),
        };
        const auto afterOffset = static_cast<std::size_t>(newEnd);
        (void)model_->SetLatestUndoSelections({before}, {{afterOffset, afterOffset}});

        // The model event fires synchronously during ApplyOffsetEdits and may
        // transform the previous selection. A TSF replacement owns the final
        // caret result, so collapse after the edit returns.
        const AcpSelection collapsed{newEnd, newEnd, false, false};
        selection_ = collapsed;
        if (cursor_)
        {
            ++suppressCursorSelectionNotifications_;
            (void)ApplySelectionToCursor(collapsed);
            --suppressCursorSelectionNotifications_;
            selection_ = SelectionFromCursor();
        }
        return true;
    }


    IAcpTextStoreBackend::ExternalChangeToken TextModelAcpTextStoreBackend::AddExternalChangeHandler(ExternalChangeHandler handler)
    {
        if (!handler)
        {
            return 0;
        }
        const auto token = nextExternalChangeToken_++;
        externalChangeHandlers_.emplace(token, std::move(handler));
        return token;
    }

    void TextModelAcpTextStoreBackend::RemoveExternalChangeHandler(ExternalChangeToken token) noexcept
    {
        externalChangeHandlers_.erase(token);
    }

    bool TextModelAcpTextStoreBackend::IsValidRange(std::int32_t start, std::int32_t end) const noexcept
    {
        if (!model_ || !IsAcpAddressable())
        {
            return false;
        }
        return start >= 0 && end >= start && static_cast<std::size_t>(end) <= model_->Length();
    }

    bool TextModelAcpTextStoreBackend::IsAcpAddressable() const noexcept
    {
        return model_ && model_->Length() <= kMaxAcp;
    }

    void TextModelAcpTextStoreBackend::OnModelChanged(const Core::ModelContentChangedEvent& event) noexcept
    {
        if (!IsAcpAddressable())
        {
            selection_ = {};
            return;
        }

        const auto previousSelection = GetSelection();
        auto transformedSelection = previousSelection;
        if (transformedSelection.start == transformedSelection.end)
        {
            const auto caret = TransformOffset(transformedSelection.start, true, event);
            transformedSelection.start = caret;
            transformedSelection.end = caret;
        }
        else
        {
            transformedSelection.start = TransformOffset(transformedSelection.start, false, event);
            transformedSelection.end = TransformOffset(transformedSelection.end, true, event);
            if (transformedSelection.end < transformedSelection.start)
            {
                std::swap(transformedSelection.start, transformedSelection.end);
                transformedSelection.activeEndAtStart = !transformedSelection.activeEndAtStart;
            }
        }

        const auto length = Length();
        transformedSelection.start = std::clamp(transformedSelection.start, 0, length);
        transformedSelection.end = std::clamp(transformedSelection.end, transformedSelection.start, length);
        selection_ = transformedSelection;
        if (cursor_)
        {
            ++suppressCursorSelectionNotifications_;
            (void)ApplySelectionToCursor(transformedSelection);
            --suppressCursorSelectionNotifications_;
            selection_ = SelectionFromCursor();
        }

        if (suppressExternalNotifications_ != 0 || externalChangeHandlers_.empty())
        {
            return;
        }

        AcpExternalChangeEvent external{};
        external.selectionChanged =
            previousSelection.start != selection_.start ||
            previousSelection.end != selection_.end ||
            previousSelection.activeEndAtStart != selection_.activeEndAtStart ||
            previousSelection.interimCharacter != selection_.interimCharacter;
        external.textChanges.reserve(event.changes.size());
        for (const auto& change : event.changes)
        {
            if (change.rangeOffset > kMaxAcp || change.rangeLength > kMaxAcp || change.text.size() > kMaxAcp)
            {
                continue;
            }
            const auto start = static_cast<std::int32_t>(change.rangeOffset);
            const auto oldEnd64 = static_cast<std::uint64_t>(change.rangeOffset) + static_cast<std::uint64_t>(change.rangeLength);
            const auto newEnd64 = static_cast<std::uint64_t>(change.rangeOffset) + static_cast<std::uint64_t>(change.text.size());
            if (oldEnd64 > static_cast<std::uint64_t>(std::numeric_limits<std::int32_t>::max()) ||
                newEnd64 > static_cast<std::uint64_t>(std::numeric_limits<std::int32_t>::max()))
            {
                continue;
            }
            external.textChanges.push_back(AcpTextChange{
                start,
                static_cast<std::int32_t>(oldEnd64),
                static_cast<std::int32_t>(newEnd64),
            });
        }

        if (external.textChanges.empty() && !external.selectionChanged)
        {
            return;
        }

        NotifyExternalChange(external);
    }

    void TextModelAcpTextStoreBackend::OnCursorSelectionChanged(const std::vector<Core::Selection>&) noexcept
    {
        if (!cursor_ || suppressCursorSelectionNotifications_ != 0)
        {
            return;
        }

        const auto current = SelectionFromCursor();
        if (current.start == selection_.start &&
            current.end == selection_.end &&
            current.activeEndAtStart == selection_.activeEndAtStart &&
            current.interimCharacter == selection_.interimCharacter)
        {
            return;
        }
        selection_ = current;
        if (suppressExternalNotifications_ == 0)
        {
            NotifyExternalChange(AcpExternalChangeEvent{{}, true});
        }
    }

    AcpSelection TextModelAcpTextStoreBackend::SelectionFromCursor() const noexcept
    {
        if (!cursor_ || !model_)
        {
            return selection_;
        }
        try
        {
            const auto current = cursor_->PrimarySelection();
            const auto anchor = static_cast<std::int32_t>(model_->GetOffsetAt(current.anchor));
            const auto active = static_cast<std::int32_t>(model_->GetOffsetAt(current.active));
            return AcpSelection{
                std::min(anchor, active),
                std::max(anchor, active),
                active < anchor,
                selection_.interimCharacter,
            };
        }
        catch (...)
        {
            return selection_;
        }
    }

    bool TextModelAcpTextStoreBackend::ApplySelectionToCursor(const AcpSelection& selection) noexcept
    {
        if (!cursor_ || !model_ || !IsValidRange(selection.start, selection.end))
        {
            return false;
        }
        try
        {
            const auto start = model_->GetPositionAt(static_cast<std::size_t>(selection.start));
            const auto end = model_->GetPositionAt(static_cast<std::size_t>(selection.end));
            const Core::Selection value = selection.activeEndAtStart
                ? Core::Selection{end, start}
                : Core::Selection{start, end};
            cursor_->SetPrimarySelection(value);
            return true;
        }
        catch (...)
        {
            return false;
        }
    }

    void TextModelAcpTextStoreBackend::NotifyExternalChange(const AcpExternalChangeEvent& event) noexcept
    {
        if (externalChangeHandlers_.empty())
        {
            return;
        }
        std::vector<ExternalChangeHandler> handlers;
        handlers.reserve(externalChangeHandlers_.size());
        for (const auto& [_, handler] : externalChangeHandlers_)
        {
            handlers.push_back(handler);
        }
        for (const auto& handler : handlers)
        {
            handler(event);
        }
    }

    std::int32_t TextModelAcpTextStoreBackend::TransformOffset(
        std::int32_t offset,
        bool affinityAfter,
        const Core::ModelContentChangedEvent& event) noexcept
    {
        std::int64_t cumulativeDelta = 0;
        const auto originalOffset = static_cast<std::int64_t>(offset);

        for (const auto& change : event.changes)
        {
            const auto oldStart = static_cast<std::int64_t>(change.rangeOffset);
            const auto oldEnd = oldStart + static_cast<std::int64_t>(change.rangeLength);
            const auto newLength = static_cast<std::int64_t>(change.text.size());
            const auto delta = newLength - static_cast<std::int64_t>(change.rangeLength);

            if (originalOffset < oldStart)
            {
                break;
            }

            if (originalOffset > oldEnd || (originalOffset == oldEnd && oldStart != oldEnd))
            {
                cumulativeDelta += delta;
                continue;
            }

            if (originalOffset == oldStart && oldStart == oldEnd)
            {
                return ClampToAcp(oldStart + cumulativeDelta + (affinityAfter ? newLength : 0));
            }

            if (originalOffset == oldStart)
            {
                return ClampToAcp(oldStart + cumulativeDelta + (affinityAfter ? newLength : 0));
            }

            if (originalOffset <= oldEnd)
            {
                return ClampToAcp(oldStart + cumulativeDelta + (affinityAfter ? newLength : 0));
            }
        }

        return ClampToAcp(originalOffset + cumulativeDelta);
    }
}
