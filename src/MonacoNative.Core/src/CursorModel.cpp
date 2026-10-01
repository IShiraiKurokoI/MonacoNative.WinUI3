#include "MonacoNative/Core/CursorModel.h"
#include "MonacoNative/Core/UnicodeSegmentation.h"

#include <algorithm>
#include <stdexcept>
#include <tuple>

namespace MonacoNative::Core
{
    namespace
    {
        std::size_t MapOffsetAfterDeletions(std::size_t offset, const std::vector<OffsetEdit>& edits) noexcept
        {
            std::size_t removedBefore = 0;
            for (const auto& edit : edits)
            {
                if (offset <= edit.start)
                {
                    return offset - std::min(offset, removedBefore);
                }
                if (offset < edit.end)
                {
                    return edit.start - removedBefore;
                }
                removedBefore += edit.end - edit.start;
            }
            return offset - std::min(offset, removedBefore);
        }
    }

    CursorModel::CursorModel(std::shared_ptr<TextModel> model)
        : model_(std::move(model))
    {
        if (!model_)
        {
            throw std::invalid_argument("CursorModel requires a TextModel.");
        }
        selections_ = {Selection{Position{1, 1}, Position{1, 1}}};
    }

    const std::vector<Selection>& CursorModel::Selections() const noexcept
    {
        return selections_;
    }

    void CursorModel::SetSelections(std::vector<Selection> selections)
    {
        const auto previous = selections_;
        if (selections.empty())
        {
            selections.push_back(Selection{Position{1, 1}, Position{1, 1}});
        }
        selections_ = std::move(selections);
        NormalizeSelections();
        preferredColumn_.reset();
        EmitSelectionChangedIfNeeded(previous);
    }

    void CursorModel::SetPrimarySelection(Selection selection)
    {
        const auto previous = selections_;
        const auto anchor = model_->GetPositionAt(model_->GetOffsetAt(selection.anchor));
        const auto active = model_->GetPositionAt(model_->GetOffsetAt(selection.active));
        selection = Selection{anchor, active};
        if (selections_.empty())
        {
            selections_.push_back(selection);
        }
        else
        {
            selections_.front() = selection;
        }
        preferredColumn_.reset();
        EmitSelectionChangedIfNeeded(previous);
    }

    Selection CursorModel::PrimarySelection() const
    {
        return selections_.empty() ? Selection{} : selections_.front();
    }

    EventSource<std::vector<Selection>>& CursorModel::OnDidChangeSelection() noexcept
    {
        return didChangeSelection_;
    }

    void CursorModel::MoveLeft(bool extendSelection)
    {
        const auto selection = PrimarySelection();
        auto offset = model_->GetOffsetAt(selection.active);
        if (!extendSelection && !selection.IsEmpty())
        {
            const auto anchor = model_->GetOffsetAt(selection.anchor);
            offset = std::min(offset, anchor);
            MovePrimaryTo(model_->GetPositionAt(offset), false);
            return;
        }
        const auto text = model_->GetValue();
        offset = Unicode::PreviousGraphemeBoundary(text, offset);
        MovePrimaryTo(model_->GetPositionAt(offset), extendSelection);
    }

    void CursorModel::MoveRight(bool extendSelection)
    {
        const auto selection = PrimarySelection();
        auto offset = model_->GetOffsetAt(selection.active);
        if (!extendSelection && !selection.IsEmpty())
        {
            const auto anchor = model_->GetOffsetAt(selection.anchor);
            offset = std::max(offset, anchor);
            MovePrimaryTo(model_->GetPositionAt(offset), false);
            return;
        }
        const auto text = model_->GetValue();
        offset = Unicode::NextGraphemeBoundary(text, offset);
        MovePrimaryTo(model_->GetPositionAt(offset), extendSelection);
    }

    void CursorModel::MoveWordLeft(bool extendSelection)
    {
        const auto selection = PrimarySelection();
        auto offset = model_->GetOffsetAt(selection.active);
        if (!extendSelection && !selection.IsEmpty())
        {
            offset = std::min(offset, model_->GetOffsetAt(selection.anchor));
        }
        else
        {
            offset = Unicode::PreviousWordBoundary(model_->GetValue(), offset);
        }
        MovePrimaryTo(model_->GetPositionAt(offset), extendSelection);
    }

    void CursorModel::MoveWordRight(bool extendSelection)
    {
        const auto selection = PrimarySelection();
        auto offset = model_->GetOffsetAt(selection.active);
        if (!extendSelection && !selection.IsEmpty())
        {
            offset = std::max(offset, model_->GetOffsetAt(selection.anchor));
        }
        else
        {
            offset = Unicode::NextWordBoundary(model_->GetValue(), offset);
        }
        MovePrimaryTo(model_->GetPositionAt(offset), extendSelection);
    }

    void CursorModel::MoveUp(bool extendSelection)
    {
        const auto active = PrimarySelection().active;
        if (!preferredColumn_)
        {
            preferredColumn_ = active.column;
        }
        const auto targetLine = std::max<std::int32_t>(1, active.lineNumber - 1);
        MovePrimaryTo(Position{targetLine, *preferredColumn_}, extendSelection, true);
    }

    void CursorModel::MoveDown(bool extendSelection)
    {
        const auto active = PrimarySelection().active;
        if (!preferredColumn_)
        {
            preferredColumn_ = active.column;
        }
        const auto targetLine = std::min<std::int32_t>(active.lineNumber + 1, static_cast<std::int32_t>(model_->GetLineCount()));
        MovePrimaryTo(Position{targetLine, *preferredColumn_}, extendSelection, true);
    }

    void CursorModel::MoveHome(bool extendSelection)
    {
        const auto active = PrimarySelection().active;
        MovePrimaryTo(Position{active.lineNumber, 1}, extendSelection);
    }

    void CursorModel::MoveEnd(bool extendSelection)
    {
        const auto active = PrimarySelection().active;
        const auto line = model_->GetLineContent(active.lineNumber);
        MovePrimaryTo(Position{active.lineNumber, static_cast<std::int32_t>(line.size() + 1)}, extendSelection);
    }

    void CursorModel::InsertText(std::u16string_view text)
    {
        const auto previous = selections_;
        preferredColumn_.reset();
        NormalizeSelections();
        std::vector<OffsetEdit> edits;
        edits.reserve(selections_.size());
        for (const auto& selection : selections_)
        {
            const auto a = model_->GetOffsetAt(selection.anchor);
            const auto b = model_->GetOffsetAt(selection.active);
            edits.push_back(OffsetEdit{std::min(a, b), std::max(a, b), std::u16string(text)});
        }

        std::sort(edits.begin(), edits.end(), [](const OffsetEdit& a, const OffsetEdit& b)
        {
            return std::tie(a.start, a.end) < std::tie(b.start, b.end);
        });

        std::vector<std::size_t> finalCarets;
        finalCarets.reserve(edits.size());
        std::ptrdiff_t delta = 0;
        for (const auto& edit : edits)
        {
            const auto postStart = static_cast<std::size_t>(static_cast<std::ptrdiff_t>(edit.start) + delta);
            finalCarets.push_back(postStart + edit.text.size());
            delta += static_cast<std::ptrdiff_t>(edit.text.size()) - static_cast<std::ptrdiff_t>(edit.end - edit.start);
        }

        model_->ApplyOffsetEdits(std::move(edits));
        selections_.clear();
        for (const auto offset : finalCarets)
        {
            const auto position = model_->GetPositionAt(offset);
            selections_.push_back(Selection{position, position});
        }
        NormalizeSelections();
        EmitSelectionChangedIfNeeded(previous);
    }

    void CursorModel::Backspace()
    {
        DeleteSelections(true, false);
    }

    void CursorModel::DeleteForward()
    {
        DeleteSelections(false, false);
    }

    void CursorModel::DeleteWordLeft()
    {
        DeleteSelections(true, true);
    }

    void CursorModel::DeleteWordRight()
    {
        DeleteSelections(false, true);
    }

    void CursorModel::DeleteSelections(bool backward, bool byWord)
    {
        const auto previous = selections_;
        preferredColumn_.reset();
        NormalizeSelections();
        const auto text = model_->GetValue();
        std::vector<OffsetEdit> rawEdits;
        std::vector<std::size_t> targetOffsets;
        rawEdits.reserve(selections_.size());
        targetOffsets.reserve(selections_.size());

        for (const auto& selection : selections_)
        {
            const auto a = model_->GetOffsetAt(selection.anchor);
            const auto b = model_->GetOffsetAt(selection.active);
            auto start = std::min(a, b);
            auto end = std::max(a, b);
            if (start == end)
            {
                if (backward)
                {
                    start = byWord ? Unicode::PreviousWordBoundary(text, start) : Unicode::PreviousGraphemeBoundary(text, start);
                }
                else
                {
                    end = byWord ? Unicode::NextWordBoundary(text, end) : Unicode::NextGraphemeBoundary(text, end);
                }
            }
            targetOffsets.push_back(start);
            if (start != end)
            {
                rawEdits.push_back(OffsetEdit{start, end, {}});
            }
        }

        if (rawEdits.empty())
        {
            return;
        }

        std::sort(rawEdits.begin(), rawEdits.end(), [](const OffsetEdit& a, const OffsetEdit& b)
        {
            return std::tie(a.start, a.end) < std::tie(b.start, b.end);
        });
        std::vector<OffsetEdit> edits;
        for (const auto& edit : rawEdits)
        {
            if (edits.empty() || edit.start > edits.back().end)
            {
                edits.push_back(edit);
            }
            else
            {
                edits.back().end = std::max(edits.back().end, edit.end);
            }
        }

        std::vector<std::size_t> finalCarets;
        finalCarets.reserve(targetOffsets.size());
        for (const auto target : targetOffsets)
        {
            finalCarets.push_back(MapOffsetAfterDeletions(target, edits));
        }

        model_->ApplyOffsetEdits(edits);
        selections_.clear();
        for (const auto offset : finalCarets)
        {
            const auto position = model_->GetPositionAt(offset);
            selections_.push_back(Selection{position, position});
        }
        NormalizeSelections();
        EmitSelectionChangedIfNeeded(previous);
    }

    void CursorModel::MovePrimaryTo(Position position, bool extendSelection, bool preservePreferredColumn)
    {
        if (!preservePreferredColumn)
        {
            preferredColumn_.reset();
        }
        const auto normalized = model_->GetPositionAt(model_->GetOffsetAt(position));
        auto selection = PrimarySelection();
        if (extendSelection)
        {
            selection.active = normalized;
        }
        else
        {
            selection.anchor = normalized;
            selection.active = normalized;
        }
        const auto preferred = preferredColumn_;
        SetPrimarySelection(selection);
        if (preservePreferredColumn)
        {
            preferredColumn_ = preferred;
        }
    }

    Range CursorModel::SelectionRange(const Selection& selection) const
    {
        const auto a = model_->GetOffsetAt(selection.anchor);
        const auto b = model_->GetOffsetAt(selection.active);
        const auto start = model_->GetPositionAt(std::min(a, b));
        const auto end = model_->GetPositionAt(std::max(a, b));
        return Range{start.lineNumber, start.column, end.lineNumber, end.column};
    }

    void CursorModel::EmitSelectionChangedIfNeeded(const std::vector<Selection>& previous)
    {
        if (previous != selections_)
        {
            didChangeSelection_.Emit(selections_);
        }
    }

    void CursorModel::NormalizeSelections()
    {
        struct Entry final
        {
            std::size_t start{};
            std::size_t end{};
        };
        std::vector<Entry> entries;
        entries.reserve(selections_.size());
        for (const auto& selection : selections_)
        {
            const auto a = model_->GetOffsetAt(selection.anchor);
            const auto b = model_->GetOffsetAt(selection.active);
            entries.push_back(Entry{std::min(a, b), std::max(a, b)});
        }
        std::sort(entries.begin(), entries.end(), [](const Entry& a, const Entry& b)
        {
            return std::tie(a.start, a.end) < std::tie(b.start, b.end);
        });

        std::vector<Entry> normalized;
        for (const auto& entry : entries)
        {
            if (normalized.empty())
            {
                normalized.push_back(entry);
                continue;
            }
            auto& previous = normalized.back();
            if (entry.start == previous.start && entry.end == previous.end)
            {
                continue;
            }
            const auto previousNonEmpty = previous.start != previous.end;
            const auto currentNonEmpty = entry.start != entry.end;
            if ((previousNonEmpty || currentNonEmpty) && entry.start < previous.end)
            {
                previous.end = std::max(previous.end, entry.end);
                continue;
            }
            normalized.push_back(entry);
        }

        selections_.clear();
        for (const auto& entry : normalized)
        {
            const auto start = model_->GetPositionAt(entry.start);
            const auto end = model_->GetPositionAt(entry.end);
            selections_.push_back(Selection{start, end});
        }
        if (selections_.empty())
        {
            selections_.push_back(Selection{Position{1, 1}, Position{1, 1}});
        }
    }
}
