#include "MonacoNative/Core/FoldingModel.h"

#include <algorithm>
#include <cctype>
#include <limits>
#include <tuple>
#include <stdexcept>
#include <unordered_map>

namespace MonacoNative::Core
{
    namespace
    {
        std::int32_t IndentationWidth(std::u16string_view text, std::int32_t tabSize) noexcept
        {
            std::int32_t width = 0;
            for (const auto ch : text)
            {
                if (ch == u' ')
                {
                    ++width;
                }
                else if (ch == u'\t')
                {
                    width += tabSize - (width % tabSize);
                }
                else
                {
                    break;
                }
            }
            return width;
        }

        bool IsBlank(std::u16string_view text) noexcept
        {
            return std::all_of(text.begin(), text.end(), [](char16_t ch)
            {
                return ch == u' ' || ch == u'\t';
            });
        }

    }

    IndentationFoldingRangeProvider::IndentationFoldingRangeProvider(std::int32_t tabSize) noexcept
        : tabSize_(std::max(1, tabSize))
    {
    }

    std::vector<FoldingRangeCandidate> IndentationFoldingRangeProvider::Compute(const TextModel& model) const
    {
        const auto lineCount = static_cast<std::int32_t>(model.GetLineCount());
        std::vector<FoldingRangeCandidate> result;
        if (lineCount <= 1)
        {
            return result;
        }

        struct NonBlankLine final
        {
            std::int32_t line{};
            std::int32_t indent{};
        };
        std::vector<NonBlankLine> nonBlank;
        nonBlank.reserve(static_cast<std::size_t>(lineCount));
        for (std::int32_t line = 1; line <= lineCount; ++line)
        {
            const auto content = model.GetLineContent(line);
            if (!IsBlank(content))
            {
                nonBlank.push_back(NonBlankLine{line, IndentationWidth(content, tabSize_)});
            }
        }

        for (std::size_t index = 0; index + 1 < nonBlank.size(); ++index)
        {
            const auto current = nonBlank[index];
            if (nonBlank[index + 1].indent <= current.indent)
            {
                continue;
            }

            auto endLine = nonBlank[index + 1].line;
            for (std::size_t next = index + 1; next < nonBlank.size(); ++next)
            {
                if (nonBlank[next].indent <= current.indent)
                {
                    break;
                }
                endLine = nonBlank[next].line;
            }
            if (endLine > current.line)
            {
                result.push_back(FoldingRangeCandidate{current.line, endLine, "indent"});
            }
        }
        return result;
    }

    FoldingModel::FoldingModel(std::shared_ptr<TextModel> model)
        : model_(std::move(model))
    {
        if (!model_)
        {
            throw std::invalid_argument("FoldingModel requires a TextModel.");
        }
        modelChangeToken_ = model_->OnDidChangeContent().Add([this](const ModelContentChangedEvent& event)
        {
            HandleModelChange(event);
        });
    }

    FoldingModel::~FoldingModel()
    {
        if (model_ && modelChangeToken_ != 0)
        {
            model_->OnDidChangeContent().Remove(modelChangeToken_);
        }
    }

    void FoldingModel::SetRanges(std::vector<FoldingRangeCandidate> ranges)
    {
        const auto lineCount = static_cast<std::int32_t>(model_->GetLineCount());
        std::sort(ranges.begin(), ranges.end(), [](const auto& left, const auto& right)
        {
            return std::tie(left.startLineNumber, left.endLineNumber, left.kind) <
                std::tie(right.startLineNumber, right.endLineNumber, right.kind);
        });
        ranges.erase(std::unique(ranges.begin(), ranges.end()), ranges.end());

        std::vector<TrackedRange> next;
        next.reserve(ranges.size());
        for (auto candidate : ranges)
        {
            candidate.startLineNumber = std::clamp(candidate.startLineNumber, 1, lineCount);
            candidate.endLineNumber = std::clamp(candidate.endLineNumber, 1, lineCount);
            if (candidate.endLineNumber <= candidate.startLineNumber)
            {
                continue;
            }

            const auto previous = std::find_if(tracked_.begin(), tracked_.end(), [&](const TrackedRange& item)
            {
                return item.range.startLineNumber == candidate.startLineNumber &&
                    item.range.endLineNumber == candidate.endLineNumber && item.range.kind == candidate.kind;
            });
            FoldingRange range{};
            if (previous != tracked_.end())
            {
                range = previous->range;
            }
            else
            {
                range.id = nextId_++;
                range.collapsed = false;
            }
            range.startLineNumber = candidate.startLineNumber;
            range.endLineNumber = candidate.endLineNumber;
            range.kind = std::move(candidate.kind);

            const auto startOffset = model_->GetOffsetAt(Position{range.startLineNumber, 1});
            const auto endOffset = model_->GetOffsetAt(Position{range.endLineNumber, model_->GetLineMaxColumn(range.endLineNumber)});
            next.push_back(TrackedRange{std::move(range), startOffset, endOffset});
        }
        tracked_ = std::move(next);
        RebuildPublicRangesFromOffsets();
        EmitChanged();
    }

    void FoldingModel::UpdateFromProvider(const IFoldingRangeProvider& provider)
    {
        SetRanges(provider.Compute(*model_));
    }

    const std::vector<FoldingRange>& FoldingModel::Ranges() const noexcept
    {
        return ranges_;
    }

    std::vector<FoldingHiddenArea> FoldingModel::HiddenAreas() const
    {
        std::vector<FoldingHiddenArea> areas;
        for (const auto& range : ranges_)
        {
            if (range.collapsed && range.endLineNumber > range.startLineNumber)
            {
                areas.push_back(FoldingHiddenArea{range.startLineNumber + 1, range.endLineNumber});
            }
        }
        std::sort(areas.begin(), areas.end(), [](const auto& left, const auto& right)
        {
            return std::tie(left.startLineNumber, left.endLineNumber) < std::tie(right.startLineNumber, right.endLineNumber);
        });

        std::vector<FoldingHiddenArea> merged;
        for (const auto& area : areas)
        {
            if (merged.empty() || area.startLineNumber > merged.back().endLineNumber + 1)
            {
                merged.push_back(area);
            }
            else
            {
                merged.back().endLineNumber = std::max(merged.back().endLineNumber, area.endLineNumber);
            }
        }
        return merged;
    }

    bool FoldingModel::SetCollapsed(FoldingRangeId id, bool collapsed)
    {
        const auto iterator = std::find_if(tracked_.begin(), tracked_.end(), [id](const TrackedRange& item)
        {
            return item.range.id == id;
        });
        if (iterator == tracked_.end() || iterator->range.collapsed == collapsed)
        {
            return false;
        }
        iterator->range.collapsed = collapsed;
        RebuildPublicRangesFromOffsets();
        EmitChanged();
        return true;
    }

    bool FoldingModel::Toggle(FoldingRangeId id)
    {
        const auto iterator = std::find_if(tracked_.begin(), tracked_.end(), [id](const TrackedRange& item)
        {
            return item.range.id == id;
        });
        if (iterator == tracked_.end())
        {
            return false;
        }
        return SetCollapsed(id, !iterator->range.collapsed);
    }

    std::size_t FoldingModel::SetCollapsedAtLine(std::int32_t lineNumber, bool collapsed, bool recursive)
    {
        std::vector<FoldingRangeId> targets;
        std::int32_t containerEnd = lineNumber;
        for (const auto& range : ranges_)
        {
            if (range.startLineNumber == lineNumber)
            {
                containerEnd = std::max(containerEnd, range.endLineNumber);
                if (!recursive)
                {
                    targets.push_back(range.id);
                }
            }
        }
        if (recursive && containerEnd > lineNumber)
        {
            for (const auto& range : ranges_)
            {
                if (range.startLineNumber >= lineNumber && range.endLineNumber <= containerEnd)
                {
                    targets.push_back(range.id);
                }
            }
        }

        std::size_t changed = 0;
        for (const auto id : targets)
        {
            const auto iterator = std::find_if(tracked_.begin(), tracked_.end(), [id](const TrackedRange& item)
            {
                return item.range.id == id;
            });
            if (iterator != tracked_.end() && iterator->range.collapsed != collapsed)
            {
                iterator->range.collapsed = collapsed;
                ++changed;
            }
        }
        if (changed > 0)
        {
            RebuildPublicRangesFromOffsets();
            EmitChanged();
        }
        return changed;
    }

    void FoldingModel::CollapseAll()
    {
        bool changed = false;
        for (auto& item : tracked_)
        {
            changed = changed || !item.range.collapsed;
            item.range.collapsed = true;
        }
        if (changed)
        {
            RebuildPublicRangesFromOffsets();
            EmitChanged();
        }
    }

    void FoldingModel::ExpandAll()
    {
        bool changed = false;
        for (auto& item : tracked_)
        {
            changed = changed || item.range.collapsed;
            item.range.collapsed = false;
        }
        if (changed)
        {
            RebuildPublicRangesFromOffsets();
            EmitChanged();
        }
    }

    EventSource<FoldingChangedEvent>& FoldingModel::OnDidChange() noexcept
    {
        return didChange_;
    }

    void FoldingModel::HandleModelChange(const ModelContentChangedEvent& event)
    {
        for (auto& item : tracked_)
        {
            item.startOffset = TransformOffset(item.startOffset, false, event.changes);
            item.endOffset = TransformOffset(item.endOffset, true, event.changes);
            item.startOffset = std::min(item.startOffset, model_->Length());
            item.endOffset = std::min(std::max(item.endOffset, item.startOffset), model_->Length());
        }
        RebuildPublicRangesFromOffsets();
        EmitChanged();
    }

    void FoldingModel::RebuildPublicRangesFromOffsets()
    {
        ranges_.clear();
        const auto lineCount = static_cast<std::int32_t>(model_->GetLineCount());
        for (auto& item : tracked_)
        {
            auto start = model_->GetPositionAt(std::min(item.startOffset, model_->Length())).lineNumber;
            auto end = model_->GetPositionAt(std::min(item.endOffset, model_->Length())).lineNumber;
            start = std::clamp(start, 1, lineCount);
            end = std::clamp(end, 1, lineCount);
            if (end <= start)
            {
                continue;
            }
            item.range.startLineNumber = start;
            item.range.endLineNumber = end;
            ranges_.push_back(item.range);
        }
        std::sort(ranges_.begin(), ranges_.end(), [](const FoldingRange& left, const FoldingRange& right)
        {
            return std::tie(left.startLineNumber, left.endLineNumber, left.id) <
                std::tie(right.startLineNumber, right.endLineNumber, right.id);
        });
    }

    void FoldingModel::EmitChanged()
    {
        didChange_.Emit(FoldingChangedEvent{ranges_});
    }

    std::size_t FoldingModel::TransformOffset(
        std::size_t offset,
        bool moveAfterInsertionAtBoundary,
        const std::vector<ModelContentChange>& changes) noexcept
    {
        std::ptrdiff_t delta = 0;
        for (const auto& change : changes)
        {
            const auto start = change.rangeOffset;
            const auto end = start + change.rangeLength;
            const auto inserted = change.text.size();
            if (offset < start)
            {
                break;
            }
            if (offset > end || (offset == end && change.rangeLength > 0))
            {
                delta += static_cast<std::ptrdiff_t>(inserted) - static_cast<std::ptrdiff_t>(change.rangeLength);
                continue;
            }

            const auto base = static_cast<std::ptrdiff_t>(start) + delta;
            const auto trailing = moveAfterInsertionAtBoundary ? static_cast<std::ptrdiff_t>(inserted) : 0;
            return static_cast<std::size_t>(std::max<std::ptrdiff_t>(0, base + trailing));
        }
        return static_cast<std::size_t>(std::max<std::ptrdiff_t>(0, static_cast<std::ptrdiff_t>(offset) + delta));
    }
}
