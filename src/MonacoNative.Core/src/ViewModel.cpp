#include "MonacoNative/Core/ViewModel.h"
#include "MonacoNative/Core/UnicodeSegmentation.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <numeric>
#include <stdexcept>
#include <tuple>

namespace MonacoNative::Core
{
    namespace
    {
        bool HasLineBreak(std::u16string_view text) noexcept
        {
            return text.find(u'\r') != std::u16string_view::npos || text.find(u'\n') != std::u16string_view::npos;
        }

        std::int32_t CountLineFeeds(std::u16string_view text) noexcept
        {
            return static_cast<std::int32_t>(std::count(text.begin(), text.end(), u'\n'));
        }
    }

    ViewModel::ViewModel(std::shared_ptr<TextModel> model)
        : model_(std::move(model))
    {
        if (!model_)
        {
            throw std::invalid_argument("ViewModel requires a TextModel.");
        }
        modelChangeToken_ = model_->OnDidChangeContent().Add([this](const ModelContentChangedEvent& event)
        {
            HandleModelChange(event);
        });
        Rebuild();
    }

    ViewModel::~ViewModel()
    {
        if (foldingModel_ && foldingChangeToken_ != 0)
        {
            foldingModel_->OnDidChange().Remove(foldingChangeToken_);
        }
        if (model_ && modelChangeToken_ != 0)
        {
            model_->OnDidChangeContent().Remove(modelChangeToken_);
        }
    }

    void ViewModel::SetWrapColumn(std::int32_t wrapColumn)
    {
        const auto normalized = std::max(0, wrapColumn);
        if (wrapColumn_ == normalized)
        {
            return;
        }
        const auto previous = wrapColumn_;
        wrapColumn_ = normalized;
        // Measured wrapping supersedes a column limit; changing the inactive
        // setting must not reproject even one line.
        if (wrapWidth_ <= 0.0)
        {
            ReflowWrappingChange(wrapWidth_, previous);
        }
    }

    std::int32_t ViewModel::WrapColumn() const noexcept
    {
        return wrapColumn_;
    }

    void ViewModel::SetWrapWidth(double wrapWidth)
    {
        const auto normalized = std::isfinite(wrapWidth) ? std::max(0.0, wrapWidth) : 0.0;
        if (wrapWidth_ == normalized)
        {
            return;
        }
        const auto previous = wrapWidth_;
        wrapWidth_ = normalized;
        ReflowWrappingChange(previous, wrapColumn_);
    }

    double ViewModel::WrapWidth() const noexcept
    {
        return wrapWidth_;
    }

    void ViewModel::SetTextMeasurer(std::shared_ptr<ITextMeasurer> measurer)
    {
        if (!measurer)
        {
            measurer = std::make_shared<FixedAdvanceTextMeasurer>();
        }
        if (textMeasurer_ == measurer)
        {
            return;
        }
        textMeasurer_ = std::move(measurer);
        Rebuild();
    }

    const std::shared_ptr<ITextMeasurer>& ViewModel::TextMeasurer() const noexcept
    {
        return textMeasurer_;
    }

    void ViewModel::SetLineHeight(double lineHeight)
    {
        const auto normalized = std::max(1.0, std::isfinite(lineHeight) ? lineHeight : 20.0);
        if (lineHeight_ == normalized)
        {
            return;
        }
        const auto previous = lineHeight_;
        lineHeight_ = normalized;
        // A font-independent line-height change affects only the vertical
        // prefix sums, not text shaping, line breaks, folding or model mapping.
        auto heights = modelLineBlockHeights_.Values();
        const auto counts = modelLineViewCounts_.Values();
        for (std::size_t i = 0; i < heights.size(); ++i)
        {
            heights[i] += static_cast<double>(counts[i]) * (normalized - previous);
        }
        modelLineBlockHeights_.Reset(heights);
        totalHeight_ = leadingZoneHeight_ + modelLineBlockHeights_.Total();
        lastProjectionUpdateStats_ = ViewProjectionUpdateStats{0, 0, 0, 0, counts.size()};
    }

    double ViewModel::LineHeight() const noexcept
    {
        return lineHeight_;
    }

    void ViewModel::SetHiddenAreas(std::vector<HiddenArea> areas)
    {
        explicitHiddenAreas_ = std::move(areas);
        RefreshHiddenAreasIncrementally();
    }

    const std::vector<HiddenArea>& ViewModel::HiddenAreas() const noexcept
    {
        return hiddenAreas_;
    }

    bool ViewModel::IsModelLineHidden(std::int32_t modelLineNumber) const noexcept
    {
        for (const auto& area : hiddenAreas_)
        {
            if (modelLineNumber < area.startLineNumber)
            {
                return false;
            }
            if (modelLineNumber <= area.endLineNumber)
            {
                return true;
            }
        }
        return false;
    }

    void ViewModel::SetFoldingModel(std::shared_ptr<FoldingModel> foldingModel)
    {
        if (foldingModel_ == foldingModel)
        {
            return;
        }
        if (foldingModel_ && foldingChangeToken_ != 0)
        {
            foldingModel_->OnDidChange().Remove(foldingChangeToken_);
        }
        foldingModel_ = std::move(foldingModel);
        foldingChangeToken_ = 0;
        if (foldingModel_)
        {
            foldingChangeToken_ = foldingModel_->OnDidChange().Add([this](const FoldingChangedEvent&)
            {
                // FoldingModel can receive the same model event before or after this view.
                // If its projection has advanced to a model version we have not handled yet,
                // defer visibility work until HandleModelChange has spliced the line sequence.
                if (model_->VersionId() != lastProcessedModelVersion_)
                {
                    pendingFoldingProjectionChange_ = true;
                    return;
                }
                RefreshHiddenAreasIncrementally();
            });
        }
        RefreshHiddenAreasIncrementally();
    }

    const std::shared_ptr<FoldingModel>& ViewModel::GetFoldingModel() const noexcept
    {
        return foldingModel_;
    }

    InjectedTextId ViewModel::AddInjectedText(
        Position position,
        std::u16string content,
        InjectedTextAffinity affinity,
        std::int32_t order)
    {
        if (HasLineBreak(content))
        {
            throw std::invalid_argument("Injected text must remain on one visual line.");
        }
        const auto normalized = model_->GetPositionAt(model_->GetOffsetAt(position));
        const auto id = nextInjectedTextId_++;
        injectedTexts_.push_back(TrackedInjectedText{
            id,
            model_->GetOffsetAt(normalized),
            std::move(content),
            affinity,
            order,
        });
        RefreshModelLine(normalized.lineNumber);
        return id;
    }

    bool ViewModel::UpdateInjectedText(InjectedTextId id, std::u16string content)
    {
        if (HasLineBreak(content))
        {
            throw std::invalid_argument("Injected text must remain on one visual line.");
        }
        const auto iterator = std::find_if(injectedTexts_.begin(), injectedTexts_.end(), [id](const auto& item)
        {
            return item.id == id;
        });
        if (iterator == injectedTexts_.end())
        {
            return false;
        }
        const auto lineNumber = model_->GetPositionAt(std::min(iterator->offset, model_->Length())).lineNumber;
        iterator->content = std::move(content);
        RefreshModelLine(lineNumber);
        return true;
    }

    bool ViewModel::RemoveInjectedText(InjectedTextId id)
    {
        const auto iterator = std::find_if(injectedTexts_.begin(), injectedTexts_.end(), [id](const auto& item)
        {
            return item.id == id;
        });
        if (iterator == injectedTexts_.end())
        {
            return false;
        }
        const auto lineNumber = model_->GetPositionAt(std::min(iterator->offset, model_->Length())).lineNumber;
        injectedTexts_.erase(iterator);
        RefreshModelLine(lineNumber);
        return true;
    }

    void ViewModel::ClearInjectedText()
    {
        if (injectedTexts_.empty())
        {
            return;
        }
        std::vector<std::int32_t> affectedLines;
        affectedLines.reserve(injectedTexts_.size());
        for (const auto& item : injectedTexts_)
        {
            affectedLines.push_back(model_->GetPositionAt(std::min(item.offset, model_->Length())).lineNumber);
        }
        std::sort(affectedLines.begin(), affectedLines.end());
        affectedLines.erase(std::unique(affectedLines.begin(), affectedLines.end()), affectedLines.end());
        injectedTexts_.clear();
        for (const auto lineNumber : affectedLines)
        {
            RefreshModelLine(lineNumber);
        }
    }

    std::vector<InjectedText> ViewModel::InjectedTexts() const
    {
        std::vector<InjectedText> result;
        result.reserve(injectedTexts_.size());
        for (const auto& item : injectedTexts_)
        {
            result.push_back(InjectedText{
                item.id,
                model_->GetPositionAt(std::min(item.offset, model_->Length())),
                item.content,
                item.affinity,
                item.order,
            });
        }
        std::stable_sort(result.begin(), result.end(), [](const auto& left, const auto& right)
        {
            return std::tie(left.position.lineNumber, left.position.column, left.order, left.id) <
                std::tie(right.position.lineNumber, right.position.column, right.order, right.id);
        });
        return result;
    }

    ViewZoneId ViewModel::AddViewZone(std::int32_t afterLineNumber, double height)
    {
        const auto lineCount = static_cast<std::int32_t>(model_->GetLineCount());
        const auto normalizedLine = std::clamp(afterLineNumber, 0, lineCount);
        const auto normalizedHeight = std::max(0.0, std::isfinite(height) ? height : 0.0);
        const auto id = nextViewZoneId_++;
        viewZones_.push_back(ViewZone{id, normalizedLine, normalizedHeight});
        std::stable_sort(viewZones_.begin(), viewZones_.end(), [](const ViewZone& left, const ViewZone& right)
        {
            return std::tie(left.afterLineNumber, left.id) < std::tie(right.afterLineNumber, right.id);
        });
        RefreshViewZoneHeightForLine(normalizedLine);
        return id;
    }

    bool ViewModel::RemoveViewZone(ViewZoneId id)
    {
        const auto iterator = std::find_if(viewZones_.begin(), viewZones_.end(), [id](const ViewZone& zone)
        {
            return zone.id == id;
        });
        if (iterator == viewZones_.end())
        {
            return false;
        }
        const auto lineNumber = iterator->afterLineNumber;
        viewZones_.erase(iterator);
        RefreshViewZoneHeightForLine(lineNumber);
        return true;
    }

    void ViewModel::ClearViewZones()
    {
        if (viewZones_.empty())
        {
            return;
        }
        std::vector<std::int32_t> affectedLines;
        affectedLines.reserve(viewZones_.size());
        for (const auto& zone : viewZones_)
        {
            affectedLines.push_back(zone.afterLineNumber);
        }
        std::sort(affectedLines.begin(), affectedLines.end());
        affectedLines.erase(std::unique(affectedLines.begin(), affectedLines.end()), affectedLines.end());
        viewZones_.clear();
        for (const auto lineNumber : affectedLines)
        {
            RefreshViewZoneHeightForLine(lineNumber);
        }
    }

    const std::vector<ViewZone>& ViewModel::ViewZones() const noexcept
    {
        return viewZones_;
    }

    void ViewModel::Rebuild()
    {
        NormalizeHiddenAreas();
        std::vector<std::vector<ViewLine>> rebuiltModelLineViewLines;
        std::vector<std::int64_t> lineViewCounts;
        std::vector<double> lineBlockHeights;
        const auto lineCount = static_cast<std::int32_t>(model_->GetLineCount());
        rebuiltModelLineViewLines.resize(static_cast<std::size_t>(lineCount));
        lineViewCounts.reserve(static_cast<std::size_t>(lineCount));
        lineBlockHeights.reserve(static_cast<std::size_t>(lineCount));

        leadingZoneHeight_ = ZoneHeightAfterLine(0);
        std::int32_t viewLineNumber = 1;
        for (std::int32_t modelLine = 1; modelLine <= lineCount; ++modelLine)
        {
            auto& outputLines = rebuiltModelLineViewLines[static_cast<std::size_t>(modelLine - 1)];
            if (IsModelLineHidden(modelLine))
            {
                lineViewCounts.push_back(0);
                lineBlockHeights.push_back(0.0);
                continue;
            }

            const auto projected = BuildProjectedLine(modelLine);
            const auto segments = ComputeWrapSegments(projected.text);
            outputLines.reserve(segments.size());
            std::int32_t wrappedIndex = 0;
            for (const auto& [start, end] : segments)
            {
                const auto length = end - start;
                ViewLine line{};
                line.viewLineNumber = viewLineNumber++;
                line.modelLineNumber = modelLine;
                line.startColumn = projected.boundaryModelColumns[start];
                line.endColumn = projected.boundaryModelColumns[end];
                line.wrappedIndex = wrappedIndex++;
                line.text = projected.text.substr(start, length);
                line.boundaryModelColumns.assign(
                    projected.boundaryModelColumns.begin() + static_cast<std::ptrdiff_t>(start),
                    projected.boundaryModelColumns.begin() + static_cast<std::ptrdiff_t>(end + 1));
                line.canonicalModelBoundaries.assign(
                    projected.canonicalModelBoundaries.begin() + static_cast<std::ptrdiff_t>(start),
                    projected.canonicalModelBoundaries.begin() + static_cast<std::ptrdiff_t>(end + 1));
                outputLines.push_back(std::move(line));
            }

            const auto count = static_cast<std::int64_t>(segments.size());
            lineViewCounts.push_back(count);
            lineBlockHeights.push_back(static_cast<double>(count) * lineHeight_ + ZoneHeightAfterLine(modelLine));
        }

        // Match Monaco's invariant that a view always exposes at least one line even if
        // every configured hidden area covers the model.
        const auto totalViewLines = std::accumulate(lineViewCounts.begin(), lineViewCounts.end(), std::int64_t{});
        if (totalViewLines == 0 && !rebuiltModelLineViewLines.empty())
        {
            rebuiltModelLineViewLines.front().push_back(ViewLine{1, 1, 1, 1, 0, {}, {1}, {true}});
            lineViewCounts[0] = 1;
            lineBlockHeights[0] = lineHeight_;
        }

        modelLineViewLines_ = std::move(rebuiltModelLineViewLines);
        modelLineViewCounts_.Reset(lineViewCounts);
        modelLineBlockHeights_.Reset(lineBlockHeights);
        lastProjectionUpdateStats_ = {};
        totalHeight_ = leadingZoneHeight_ + modelLineBlockHeights_.Total();
        lastProcessedModelVersion_ = model_->VersionId();
        pendingFoldingProjectionChange_ = false;
    }

    void ViewModel::ReflowWrappingChange(double previousWrapWidth, std::int32_t previousWrapColumn)
    {
        // Keep the existing projected model lines intact and only recalculate
        // those whose wrapping could actually change. This avoids the old full
        // Rebuild() path, with its model text copies, injection walks, grapheme
        // segmentation, and DirectWrite calls for every short, unaffected line.
        // Counts and block heights are reindexed together after all changes.
        if (modelLineViewLines_.size() != model_->GetLineCount())
        {
            Rebuild();
            return;
        }

        // Most viewport resizes affect only a few long lines. Keep their
        // prefix-sum nodes stable; rebuilding both trees would turn even a
        // single affected model line into an O(document length) tree update.
        struct CountChange final
        {
            std::size_t modelIndex{};
            std::int64_t oldCount{};
            std::int64_t newCount{};
        };
        std::vector<CountChange> countChanges;
        const bool hasMeasuredWrap = wrapWidth_ > 0.0 && textMeasurer_;
        const bool hasColumnWrap = !hasMeasuredWrap && wrapColumn_ > 0;
        std::uint64_t recomputed = 0;
        std::uint64_t recomputedFragments = 0;
        std::uint64_t reused = 0;
        for (std::size_t i = 0; i < modelLineViewLines_.size(); ++i)
        {
            auto& fragments = modelLineViewLines_[i];
            if (fragments.empty())
            {
                ++reused;
                continue;
            }

            bool unchanged = false;
            if (fragments.size() == 1)
            {
                auto& only = fragments.front();
                // A single view line is guaranteed to stay single as the same
                // measured wrap width or the same column limit is increased.
                if (hasMeasuredWrap && previousWrapWidth > 0.0 &&
                    wrapWidth_ >= previousWrapWidth)
                {
                    unchanged = true;
                }
                else if (hasColumnWrap && previousWrapWidth <= 0.0 &&
                    previousWrapColumn > 0 && wrapColumn_ >= previousWrapColumn)
                {
                    unchanged = true;
                }
                else if (!hasMeasuredWrap && !hasColumnWrap)
                {
                    unchanged = true;
                }
                else if (hasColumnWrap && only.text.size() <= static_cast<std::size_t>(wrapColumn_))
                {
                    // UTF-16 length bounds grapheme count from above.
                    unchanged = true;
                }
                else if (hasMeasuredWrap)
                {
                    if (only.fullProjectedWidth < 0.0)
                    {
                        only.fullProjectedWidth = textMeasurer_->Measure(only.text);
                    }
                    unchanged = only.fullProjectedWidth <= wrapWidth_;
                }
            }

            if (unchanged)
            {
                ++reused;
                continue;
            }

            // Joining the old visual fragments reconstructs the exact model
            // projection (including injected-text boundary affinities) without
            // rereading the document or resolving injections a second time.
            ProjectedLine projected;
            std::size_t fullLength = 0;
            for (const auto& fragment : fragments)
            {
                fullLength += fragment.text.size();
            }
            projected.text.reserve(fullLength);
            projected.boundaryModelColumns.reserve(fullLength + 1);
            projected.canonicalModelBoundaries.reserve(fullLength + 1);
            for (const auto& fragment : fragments)
            {
                const auto skipBoundary = !projected.boundaryModelColumns.empty() ? 1 : 0;
                projected.text += fragment.text;
                projected.boundaryModelColumns.insert(
                    projected.boundaryModelColumns.end(),
                    fragment.boundaryModelColumns.begin() + skipBoundary,
                    fragment.boundaryModelColumns.end());
                projected.canonicalModelBoundaries.insert(
                    projected.canonicalModelBoundaries.end(),
                    fragment.canonicalModelBoundaries.begin() + skipBoundary,
                    fragment.canonicalModelBoundaries.end());
            }

            const auto segments = ComputeWrapSegments(projected.text);
            const double cachedWidth = fragments.front().fullProjectedWidth;
            std::vector<ViewLine> updated;
            updated.reserve(segments.size());
            for (std::size_t index = 0; index < segments.size(); ++index)
            {
                const auto [begin, end] = segments[index];
                ViewLine line{};
                line.modelLineNumber = static_cast<std::int32_t>(i + 1);
                line.startColumn = projected.boundaryModelColumns[begin];
                line.endColumn = projected.boundaryModelColumns[end];
                line.wrappedIndex = static_cast<std::int32_t>(index);
                line.text = projected.text.substr(begin, end - begin);
                line.boundaryModelColumns.assign(
                    projected.boundaryModelColumns.begin() + static_cast<std::ptrdiff_t>(begin),
                    projected.boundaryModelColumns.begin() + static_cast<std::ptrdiff_t>(end + 1));
                line.canonicalModelBoundaries.assign(
                    projected.canonicalModelBoundaries.begin() + static_cast<std::ptrdiff_t>(begin),
                    projected.canonicalModelBoundaries.begin() + static_cast<std::ptrdiff_t>(end + 1));
                if (index == 0)
                {
                    line.fullProjectedWidth = cachedWidth;
                }
                updated.push_back(std::move(line));
            }
            ++recomputed;
            recomputedFragments += updated.size();
            const auto previousCount = static_cast<std::int64_t>(fragments.size());
            const auto newCount = static_cast<std::int64_t>(updated.size());
            if (newCount != previousCount)
            {
                countChanges.push_back(CountChange{i, previousCount, newCount});
            }
            fragments = std::move(updated);
        }

        if (!countChanges.empty())
        {
            if (countChanges.size() * 8 < modelLineViewLines_.size())
            {
                // Sparse change: O(changed lines * log(model lines)).
                for (const auto& change : countChanges)
                {
                    modelLineViewCounts_.SetValue(change.modelIndex, change.newCount);
                    modelLineBlockHeights_.SetValue(change.modelIndex,
                        modelLineBlockHeights_.GetValue(change.modelIndex) +
                        static_cast<double>(change.newCount - change.oldCount) * lineHeight_);
                }
            }
            else
            {
                // Dense change: bulk build the count/height prefix indexes once.
                auto counts = modelLineViewCounts_.Values();
                auto heights = modelLineBlockHeights_.Values();
                for (const auto& change : countChanges)
                {
                    counts[change.modelIndex] = change.newCount;
                    heights[change.modelIndex] +=
                        static_cast<double>(change.newCount - change.oldCount) * lineHeight_;
                }
                modelLineViewCounts_.Reset(counts);
                modelLineBlockHeights_.Reset(heights);
            }
            totalHeight_ = leadingZoneHeight_ + modelLineBlockHeights_.Total();
        }
        lastProjectionUpdateStats_ = ViewProjectionUpdateStats{
            recomputed, recomputedFragments, 0, 0, reused,
        };
    }

    std::int32_t ViewModel::ViewLineCount() const noexcept
    {
        return static_cast<std::int32_t>(modelLineViewCounts_.Total());
    }

    std::size_t ViewModel::GetModelIndexForViewLine(std::int32_t viewLineNumber) const
    {
        if (viewLineNumber < 1 || viewLineNumber > ViewLineCount())
        {
            throw std::out_of_range("View line number is invalid.");
        }
        return modelLineViewCounts_.FindIndexByCumulativeOffset(static_cast<std::int64_t>(viewLineNumber - 1));
    }

    const ViewLine& ViewModel::GetViewLine(std::int32_t viewLineNumber) const
    {
        const auto modelIndex = GetModelIndexForViewLine(viewLineNumber);
        const auto before = modelLineViewCounts_.PrefixSum(modelIndex);
        const auto wrappedIndex = static_cast<std::size_t>(static_cast<std::int64_t>(viewLineNumber - 1) - before);
        const auto& line = modelLineViewLines_[modelIndex][wrappedIndex];
        line.viewLineNumber = viewLineNumber;
        line.modelLineNumber = static_cast<std::int32_t>(modelIndex + 1);
        return line;
    }

    std::pair<std::int32_t, std::int32_t> ViewModel::GetModelLineViewRange(std::int32_t modelLineNumber) const
    {
        if (modelLineNumber < 1 || static_cast<std::size_t>(modelLineNumber) > modelLineViewCounts_.Size())
        {
            return {0, 0};
        }
        const auto index = static_cast<std::size_t>(modelLineNumber - 1);
        const auto count = modelLineViewCounts_.GetValue(index);
        if (count <= 0)
        {
            return {0, 0};
        }
        const auto before = modelLineViewCounts_.PrefixSum(index);
        return {
            static_cast<std::int32_t>(before + 1),
            static_cast<std::int32_t>(before + count),
        };
    }

    ViewPosition ViewModel::ConvertModelPositionToViewPosition(Position position) const
    {
        const auto normalized = model_->GetPositionAt(model_->GetOffsetAt(position));
        const auto modelLine = normalized.lineNumber;
        const auto range = GetModelLineViewRange(modelLine);
        if (range.first == 0)
        {
            // Prefix metadata makes hidden-line projection independent of hidden-range size.
            const auto previousViewCount = modelLineViewCounts_.PrefixSum(static_cast<std::size_t>(modelLine - 1));
            if (previousViewCount > 0)
            {
                const auto previousViewLineNumber = static_cast<std::int32_t>(previousViewCount);
                const auto& viewLine = GetViewLine(previousViewLineNumber);
                return ViewPosition{previousViewLineNumber, static_cast<std::int32_t>(viewLine.text.size() + 1)};
            }

            const auto throughCurrent = modelLineViewCounts_.PrefixSum(static_cast<std::size_t>(modelLine));
            if (throughCurrent < modelLineViewCounts_.Total())
            {
                return ViewPosition{static_cast<std::int32_t>(throughCurrent + 1), 1};
            }
        }
        else
        {
            const auto& projectedLines = modelLineViewLines_[static_cast<std::size_t>(modelLine - 1)];
            // Fragments and boundary columns are monotone in model coordinates.
            // Find both with binary search instead of scanning every preceding
            // UTF-16 boundary of a very long line on cursor/selection updates.
            const auto first = std::lower_bound(projectedLines.begin(), projectedLines.end(),
                normalized.column, [](const ViewLine& item, std::int32_t column)
                {
                    return item.endColumn < column;
                });
            for (auto candidate = first; candidate != projectedLines.end() &&
                 candidate->startColumn <= normalized.column; ++candidate)
            {
                const auto wrapped = static_cast<std::size_t>(candidate - projectedLines.begin());
                const auto viewLineNumber = range.first + static_cast<std::int32_t>(wrapped);
                const auto& viewLine = *candidate;
                const auto boundary = std::lower_bound(
                    viewLine.boundaryModelColumns.begin(), viewLine.boundaryModelColumns.end(), normalized.column);
                for (auto entry = boundary;
                     entry != viewLine.boundaryModelColumns.end() && *entry == normalized.column; ++entry)
                {
                    const auto index = static_cast<std::size_t>(entry - viewLine.boundaryModelColumns.begin());
                    if (!viewLine.canonicalModelBoundaries[index])
                    {
                        continue;
                    }
                    if (index == viewLine.text.size() && wrapped + 1 < projectedLines.size())
                    {
                        const auto& next = projectedLines[wrapped + 1];
                        if (!next.boundaryModelColumns.empty() && next.boundaryModelColumns.front() == normalized.column &&
                            !next.canonicalModelBoundaries.empty() && next.canonicalModelBoundaries.front())
                        {
                            continue;
                        }
                    }
                    return ViewPosition{viewLineNumber, static_cast<std::int32_t>(index + 1)};
                }
            }
        }

        const auto lastViewLineNumber = ViewLineCount();
        const auto& last = GetViewLine(lastViewLineNumber);
        return ViewPosition{lastViewLineNumber, static_cast<std::int32_t>(last.text.size() + 1)};
    }

    Position ViewModel::ConvertViewPositionToModelPosition(ViewPosition position) const
    {
        const auto viewLineNumber = std::clamp(position.lineNumber, 1, ViewLineCount());
        const auto modelIndex = GetModelIndexForViewLine(viewLineNumber);
        const auto before = modelLineViewCounts_.PrefixSum(modelIndex);
        const auto wrappedIndex = static_cast<std::size_t>(static_cast<std::int64_t>(viewLineNumber - 1) - before);
        const auto& viewLine = modelLineViewLines_[modelIndex][wrappedIndex];
        const auto localBoundary = static_cast<std::size_t>(std::clamp(
            position.column - 1,
            0,
            static_cast<std::int32_t>(viewLine.text.size())));
        const auto modelColumn = viewLine.boundaryModelColumns[localBoundary];
        return model_->GetPositionAt(model_->GetOffsetAt(Position{static_cast<std::int32_t>(modelIndex + 1), modelColumn}));
    }

    double ViewModel::GetVerticalOffsetForViewLine(std::int32_t viewLineNumber) const noexcept
    {
        if (ViewLineCount() <= 0)
        {
            return 0.0;
        }
        const auto clamped = std::clamp(viewLineNumber, 1, ViewLineCount());
        const auto modelIndex = modelLineViewCounts_.FindIndexByCumulativeOffset(static_cast<std::int64_t>(clamped - 1));
        const auto before = modelLineViewCounts_.PrefixSum(modelIndex);
        const auto wrappedIndex = static_cast<std::int64_t>(clamped - 1) - before;
        return leadingZoneHeight_ + modelLineBlockHeights_.PrefixSum(modelIndex) +
            static_cast<double>(wrappedIndex) * lineHeight_;
    }

    std::int32_t ViewModel::GetViewLineAtVerticalOffset(double offset) const noexcept
    {
        if (ViewLineCount() <= 0)
        {
            return 1;
        }
        if (!std::isfinite(offset) || offset <= leadingZoneHeight_)
        {
            return 1;
        }
        if (offset >= totalHeight_)
        {
            return ViewLineCount();
        }

        const auto adjusted = offset - leadingZoneHeight_;
        const auto blockTotal = modelLineBlockHeights_.Total();
        if (!(adjusted < blockTotal))
        {
            return ViewLineCount();
        }

        const auto modelIndex = modelLineBlockHeights_.FindIndexByCumulativeOffset(adjusted);
        const auto beforeHeight = modelLineBlockHeights_.PrefixSum(modelIndex);
        const auto localOffset = adjusted - beforeHeight;
        const auto viewCount = modelLineViewCounts_.GetValue(modelIndex);
        const auto textHeight = static_cast<double>(viewCount) * lineHeight_;
        if (localOffset >= textHeight)
        {
            const auto next = modelLineViewCounts_.PrefixSum(modelIndex + 1) + 1;
            return std::clamp(static_cast<std::int32_t>(next), 1, ViewLineCount());
        }

        const auto wrappedIndex = static_cast<std::int64_t>(std::floor(localOffset / lineHeight_));
        const auto first = modelLineViewCounts_.PrefixSum(modelIndex) + 1;
        return std::clamp(static_cast<std::int32_t>(first + wrappedIndex), 1, ViewLineCount());
    }

    std::pair<std::int32_t, std::int32_t> ViewModel::GetViewLineRangeForModelRange(Range range) const
    {
        const auto startOffset = model_->GetOffsetAt(Position{range.startLineNumber, range.startColumn});
        const auto endOffset = model_->GetOffsetAt(Position{range.endLineNumber, range.endColumn});
        const auto first = ConvertModelPositionToViewPosition(model_->GetPositionAt(std::min(startOffset, endOffset))).lineNumber;
        const auto last = ConvertModelPositionToViewPosition(model_->GetPositionAt(std::max(startOffset, endOffset))).lineNumber;
        return {std::min(first, last), std::max(first, last)};
    }

    double ViewModel::GetScrollTopToRevealRange(
        Range range,
        double viewportHeight,
        double currentScrollTop,
        RevealStrategy strategy,
        double padding) const
    {
        const auto [firstLine, lastLine] = GetViewLineRangeForModelRange(range);
        const auto viewport = std::max(1.0, std::isfinite(viewportHeight) ? viewportHeight : lineHeight_);
        const auto safePadding = std::max(0.0, std::isfinite(padding) ? padding : 0.0);
        const auto targetTop = std::max(0.0, GetVerticalOffsetForViewLine(firstLine) - safePadding);
        const auto targetBottom = std::min(totalHeight_, GetVerticalOffsetForViewLine(lastLine) + lineHeight_ + safePadding);
        const auto maxScrollTop = std::max(0.0, totalHeight_ - viewport);
        const auto current = std::clamp(
            std::isfinite(currentScrollTop) ? currentScrollTop : 0.0,
            0.0,
            maxScrollTop);

        double desired = current;
        switch (strategy)
        {
        case RevealStrategy::Top:
            desired = targetTop;
            break;
        case RevealStrategy::Center:
            desired = ((targetTop + targetBottom) * 0.5) - (viewport * 0.5);
            break;
        case RevealStrategy::Bottom:
            desired = targetBottom - viewport;
            break;
        case RevealStrategy::Minimal:
        default:
            if (targetBottom - targetTop >= viewport)
            {
                desired = targetTop;
            }
            else if (targetTop < current)
            {
                desired = targetTop;
            }
            else if (targetBottom > current + viewport)
            {
                desired = targetBottom - viewport;
            }
            break;
        }
        return std::clamp(desired, 0.0, maxScrollTop);
    }

    double ViewModel::TotalHeight() const noexcept
    {
        return totalHeight_;
    }

    ViewProjectionUpdateStats ViewModel::LastProjectionUpdateStats() const noexcept
    {
        return lastProjectionUpdateStats_;
    }

    void ViewModel::HandleModelChange(const ModelContentChangedEvent& event)
    {
        for (auto& item : injectedTexts_)
        {
            item.offset = TransformTrackedOffset(item.offset, item.affinity, event.changes);
            item.offset = std::min(item.offset, model_->Length());
        }

        if (!TryApplyIncrementalModelChanges(event))
        {
            Rebuild();
            return;
        }

        lastProcessedModelVersion_ = event.versionId;
        pendingFoldingProjectionChange_ = false;
    }

    std::vector<ViewModel::LineChangeGroup> ViewModel::BuildLineChangeGroups(const ModelContentChangedEvent& event) const
    {
        std::vector<const ModelContentChange*> changes;
        changes.reserve(event.changes.size());
        for (const auto& change : event.changes)
        {
            changes.push_back(&change);
        }
        std::sort(changes.begin(), changes.end(), [](const auto* left, const auto* right)
        {
            return left->rangeOffset < right->rangeOffset;
        });

        std::vector<LineChangeGroup> groups;
        for (const auto* change : changes)
        {
            const auto removedLineFeeds = change->range.endLineNumber - change->range.startLineNumber;
            const auto delta = CountLineFeeds(change->text) - removedLineFeeds;
            if (groups.empty() || change->range.startLineNumber > groups.back().oldEndLine)
            {
                groups.push_back(LineChangeGroup{
                    change->range.startLineNumber,
                    change->range.endLineNumber,
                    delta,
                });
            }
            else
            {
                auto& group = groups.back();
                group.oldEndLine = std::max(group.oldEndLine, change->range.endLineNumber);
                group.lineDelta += delta;
            }
        }
        return groups;
    }

    bool ViewModel::TryApplyIncrementalModelChanges(const ModelContentChangedEvent& event)
    {
        if (event.changes.empty() || modelLineViewCounts_.Size() == 0)
        {
            return false;
        }

        const auto groups = BuildLineChangeGroups(event);
        if (groups.empty())
        {
            return false;
        }

        const auto oldLineCount = static_cast<std::int64_t>(modelLineViewCounts_.Size());
        std::int64_t totalLineDelta = 0;
        for (const auto& group : groups)
        {
            totalLineDelta += group.lineDelta;
        }
        if (oldLineCount + totalLineDelta != static_cast<std::int64_t>(model_->GetLineCount()))
        {
            return false;
        }

        TransformViewZoneAnchors(groups);
        if (pendingFoldingProjectionChange_)
        {
            NormalizeHiddenAreas();
        }

        std::int32_t cumulativeLineDelta = 0;
        for (const auto& group : groups)
        {
            const auto currentStartLine = group.oldStartLine + cumulativeLineDelta;
            const auto oldModelLineCount = group.oldEndLine - group.oldStartLine + 1;
            const auto newModelLineCount = oldModelLineCount + group.lineDelta;
            if (currentStartLine < 1 || oldModelLineCount < 1 || newModelLineCount < 1 ||
                static_cast<std::size_t>(currentStartLine - 1 + oldModelLineCount) > modelLineViewCounts_.Size())
            {
                return false;
            }
            ApplyProjectionSplice(currentStartLine, oldModelLineCount, newModelLineCount);
            cumulativeLineDelta += group.lineDelta;
        }

        if (modelLineViewCounts_.Size() != model_->GetLineCount())
        {
            return false;
        }

        // Absolute hidden areas keep their configured line numbers; folding areas track
        // model offsets. Reconcile only visibility mismatches, without reconstructing
        // unaffected projected text.
        if (!explicitHiddenAreas_.empty() || !hiddenAreas_.empty() || pendingFoldingProjectionChange_)
        {
            NormalizeHiddenAreas();
            ReconcileVisibilityWithHiddenAreas();
        }

        if (ViewLineCount() <= 0)
        {
            Rebuild();
        }
        totalHeight_ = leadingZoneHeight_ + modelLineBlockHeights_.Total();
        return true;
    }

    void ViewModel::ApplyProjectionSplice(
        std::int32_t startLine,
        std::int32_t oldModelLineCount,
        std::int32_t newModelLineCount)
    {
        const auto modelIndex = static_cast<std::size_t>(startLine - 1);
        const auto prefixBefore = modelLineViewCounts_.PrefixSum(modelIndex);

        std::vector<std::vector<ViewLine>> replacementProjections;
        std::vector<std::int64_t> replacementCounts;
        std::vector<double> replacementHeights;
        replacementProjections.resize(static_cast<std::size_t>(newModelLineCount));
        replacementCounts.reserve(static_cast<std::size_t>(newModelLineCount));
        replacementHeights.reserve(static_cast<std::size_t>(newModelLineCount));

        auto nextViewLineNumber = static_cast<std::int32_t>(prefixBefore + 1);
        std::uint64_t replacementViewLineCount = 0;
        for (std::int32_t lineOffset = 0; lineOffset < newModelLineCount; ++lineOffset)
        {
            const auto modelLine = startLine + lineOffset;
            auto& outputLines = replacementProjections[static_cast<std::size_t>(lineOffset)];
            if (!IsLineVisible(modelLine))
            {
                replacementCounts.push_back(0);
                replacementHeights.push_back(0.0);
                continue;
            }

            const auto projected = BuildProjectedLine(modelLine);
            const auto segments = ComputeWrapSegments(projected.text);
            outputLines.reserve(segments.size());
            replacementCounts.push_back(static_cast<std::int64_t>(segments.size()));
            replacementHeights.push_back(static_cast<double>(segments.size()) * lineHeight_ + ZoneHeightAfterLine(modelLine));
            replacementViewLineCount += static_cast<std::uint64_t>(segments.size());
            std::int32_t wrappedIndex = 0;
            for (const auto& [start, end] : segments)
            {
                const auto length = end - start;
                ViewLine line{};
                line.viewLineNumber = nextViewLineNumber++;
                line.modelLineNumber = modelLine;
                line.startColumn = projected.boundaryModelColumns[start];
                line.endColumn = projected.boundaryModelColumns[end];
                line.wrappedIndex = wrappedIndex++;
                line.text = projected.text.substr(start, length);
                line.boundaryModelColumns.assign(
                    projected.boundaryModelColumns.begin() + static_cast<std::ptrdiff_t>(start),
                    projected.boundaryModelColumns.begin() + static_cast<std::ptrdiff_t>(end + 1));
                line.canonicalModelBoundaries.assign(
                    projected.canonicalModelBoundaries.begin() + static_cast<std::ptrdiff_t>(start),
                    projected.canonicalModelBoundaries.begin() + static_cast<std::ptrdiff_t>(end + 1));
                outputLines.push_back(std::move(line));
            }
        }

        const auto oldProjectionSize = modelLineViewLines_.size();
        std::uint64_t projectionSlotMoves = 0;
        if (oldModelLineCount == newModelLineCount)
        {
            for (std::int32_t i = 0; i < newModelLineCount; ++i)
            {
                modelLineViewLines_[modelIndex + static_cast<std::size_t>(i)] =
                    std::move(replacementProjections[static_cast<std::size_t>(i)]);
            }
        }
        else
        {
            const auto suffixStart = modelIndex + static_cast<std::size_t>(oldModelLineCount);
            projectionSlotMoves = static_cast<std::uint64_t>(oldProjectionSize - suffixStart);
            modelLineViewLines_.erase(
                modelLineViewLines_.begin() + static_cast<std::ptrdiff_t>(modelIndex),
                modelLineViewLines_.begin() + static_cast<std::ptrdiff_t>(suffixStart));
            modelLineViewLines_.insert(
                modelLineViewLines_.begin() + static_cast<std::ptrdiff_t>(modelIndex),
                std::make_move_iterator(replacementProjections.begin()),
                std::make_move_iterator(replacementProjections.end()));
        }

        modelLineViewCounts_.Splice(
            modelIndex,
            static_cast<std::size_t>(oldModelLineCount),
            replacementCounts);
        modelLineBlockHeights_.Splice(
            modelIndex,
            static_cast<std::size_t>(oldModelLineCount),
            replacementHeights);

        lastProjectionUpdateStats_ = ViewProjectionUpdateStats{
            static_cast<std::uint64_t>(newModelLineCount),
            replacementViewLineCount,
            0,
            projectionSlotMoves,
        };
        totalHeight_ = leadingZoneHeight_ + modelLineBlockHeights_.Total();
    }

    void ViewModel::RefreshModelLine(std::int32_t modelLineNumber)
    {
        if (modelLineNumber < 1 || static_cast<std::size_t>(modelLineNumber) > modelLineViewCounts_.Size() ||
            modelLineViewCounts_.Size() != model_->GetLineCount())
        {
            Rebuild();
            return;
        }
        ApplyProjectionSplice(modelLineNumber, 1, 1);
        if (ViewLineCount() <= 0)
        {
            Rebuild();
        }
    }

    void ViewModel::RefreshHiddenAreasIncrementally()
    {
        if (modelLineViewCounts_.Size() != model_->GetLineCount())
        {
            pendingFoldingProjectionChange_ = true;
            return;
        }
        NormalizeHiddenAreas();
        ReconcileVisibilityWithHiddenAreas();
    }

    void ViewModel::ReconcileVisibilityWithHiddenAreas()
    {
        if (modelLineViewCounts_.Size() != model_->GetLineCount())
        {
            return;
        }

        std::vector<std::pair<std::int32_t, std::int32_t>> ranges;
        std::int32_t rangeStart = 0;
        const auto lineCount = static_cast<std::int32_t>(model_->GetLineCount());
        for (std::int32_t line = 1; line <= lineCount; ++line)
        {
            const auto actualVisible = modelLineViewCounts_.GetValue(static_cast<std::size_t>(line - 1)) > 0;
            const auto desiredVisible = IsLineVisible(line);
            if (actualVisible != desiredVisible)
            {
                if (rangeStart == 0)
                {
                    rangeStart = line;
                }
            }
            else if (rangeStart != 0)
            {
                ranges.push_back({rangeStart, line - 1});
                rangeStart = 0;
            }
        }
        if (rangeStart != 0)
        {
            ranges.push_back({rangeStart, lineCount});
        }

        for (const auto& [start, end] : ranges)
        {
            const auto count = end - start + 1;
            ApplyProjectionSplice(start, count, count);
        }
        if (ViewLineCount() <= 0)
        {
            Rebuild();
        }
        totalHeight_ = leadingZoneHeight_ + modelLineBlockHeights_.Total();
    }

    void ViewModel::TransformViewZoneAnchors(const std::vector<LineChangeGroup>& groups)
    {
        if (viewZones_.empty())
        {
            return;
        }
        const auto finalLineCount = static_cast<std::int32_t>(model_->GetLineCount());
        for (auto& zone : viewZones_)
        {
            if (zone.afterLineNumber <= 0)
            {
                continue;
            }

            const auto originalLine = zone.afterLineNumber;
            std::int32_t shift = 0;
            bool mappedInsideChange = false;
            for (const auto& group : groups)
            {
                if (originalLine < group.oldStartLine)
                {
                    break;
                }
                if (originalLine > group.oldEndLine)
                {
                    shift += group.lineDelta;
                    continue;
                }

                zone.afterLineNumber = group.oldEndLine + shift + group.lineDelta;
                mappedInsideChange = true;
                break;
            }
            if (!mappedInsideChange)
            {
                zone.afterLineNumber = originalLine + shift;
            }
            zone.afterLineNumber = std::clamp(zone.afterLineNumber, 0, finalLineCount);
        }
        std::stable_sort(viewZones_.begin(), viewZones_.end(), [](const ViewZone& left, const ViewZone& right)
        {
            return std::tie(left.afterLineNumber, left.id) < std::tie(right.afterLineNumber, right.id);
        });
        leadingZoneHeight_ = ZoneHeightAfterLine(0);
    }

    void ViewModel::RefreshViewZoneHeightForLine(std::int32_t modelLineNumber)
    {
        if (modelLineNumber == 0)
        {
            leadingZoneHeight_ = ZoneHeightAfterLine(0);
            totalHeight_ = leadingZoneHeight_ + modelLineBlockHeights_.Total();
            return;
        }
        if (modelLineNumber < 1 || static_cast<std::size_t>(modelLineNumber) > modelLineViewCounts_.Size())
        {
            return;
        }
        const auto index = static_cast<std::size_t>(modelLineNumber - 1);
        const auto count = modelLineViewCounts_.GetValue(index);
        const auto height = count > 0
            ? static_cast<double>(count) * lineHeight_ + ZoneHeightAfterLine(modelLineNumber)
            : 0.0;
        modelLineBlockHeights_.SetValue(index, height);
        totalHeight_ = leadingZoneHeight_ + modelLineBlockHeights_.Total();
    }

    void ViewModel::NormalizeHiddenAreas()
    {
        const auto lineCount = static_cast<std::int32_t>(model_->GetLineCount());
        std::vector<HiddenArea> normalized = explicitHiddenAreas_;
        if (foldingModel_)
        {
            for (const auto& area : foldingModel_->HiddenAreas())
            {
                normalized.push_back(HiddenArea{area.startLineNumber, area.endLineNumber});
            }
        }

        for (auto& area : normalized)
        {
            if (area.startLineNumber > area.endLineNumber)
            {
                std::swap(area.startLineNumber, area.endLineNumber);
            }
            area.startLineNumber = std::clamp(area.startLineNumber, 1, lineCount);
            area.endLineNumber = std::clamp(area.endLineNumber, 1, lineCount);
        }
        std::sort(normalized.begin(), normalized.end(), [](const HiddenArea& left, const HiddenArea& right)
        {
            return std::tie(left.startLineNumber, left.endLineNumber) < std::tie(right.startLineNumber, right.endLineNumber);
        });

        hiddenAreas_.clear();
        for (const auto& area : normalized)
        {
            if (hiddenAreas_.empty() || area.startLineNumber > hiddenAreas_.back().endLineNumber + 1)
            {
                hiddenAreas_.push_back(area);
            }
            else
            {
                hiddenAreas_.back().endLineNumber = std::max(hiddenAreas_.back().endLineNumber, area.endLineNumber);
            }
        }
    }

    bool ViewModel::IsLineVisible(std::int32_t lineNumber) const noexcept
    {
        return !IsModelLineHidden(lineNumber);
    }

    double ViewModel::ZoneHeightAfterLine(std::int32_t lineNumber) const noexcept
    {
        if (lineNumber > 0 && !IsLineVisible(lineNumber))
        {
            return 0.0;
        }
        double result = 0.0;
        for (const auto& zone : viewZones_)
        {
            if (zone.afterLineNumber == lineNumber)
            {
                result += zone.height;
            }
            else if (zone.afterLineNumber > lineNumber)
            {
                break;
            }
        }
        return result;
    }

    ViewModel::ProjectedLine ViewModel::BuildProjectedLine(std::int32_t modelLineNumber) const
    {
        const auto modelText = model_->GetLineContent(modelLineNumber);
        struct ResolvedInjection final
        {
            std::int32_t column{};
            std::int32_t order{};
            InjectedTextId id{};
            const std::u16string* content{};
        };
        std::vector<ResolvedInjection> injections;
        for (const auto& item : injectedTexts_)
        {
            const auto position = model_->GetPositionAt(std::min(item.offset, model_->Length()));
            if (position.lineNumber == modelLineNumber && !item.content.empty())
            {
                injections.push_back(ResolvedInjection{position.column, item.order, item.id, &item.content});
            }
        }
        std::stable_sort(injections.begin(), injections.end(), [](const auto& left, const auto& right)
        {
            return std::tie(left.column, left.order, left.id) < std::tie(right.column, right.order, right.id);
        });

        ProjectedLine result{};
        std::size_t injectedLength = 0;
        for (const auto& injection : injections)
        {
            injectedLength += injection.content->size();
        }
        result.text.reserve(modelText.size() + injectedLength);
        result.boundaryModelColumns.reserve(modelText.size() + injectedLength + 1);
        result.canonicalModelBoundaries.reserve(modelText.size() + injectedLength + 1);
        result.boundaryModelColumns.push_back(1);
        result.canonicalModelBoundaries.push_back(true);

        std::size_t injectionIndex = 0;
        for (std::size_t modelIndex = 0; modelIndex <= modelText.size(); ++modelIndex)
        {
            const auto column = static_cast<std::int32_t>(modelIndex + 1);
            while (injectionIndex < injections.size() && injections[injectionIndex].column == column)
            {
                for (const auto ch : *injections[injectionIndex].content)
                {
                    result.text.push_back(ch);
                    result.boundaryModelColumns.push_back(column);
                    result.canonicalModelBoundaries.push_back(false);
                }
                ++injectionIndex;
            }

            if (modelIndex < modelText.size())
            {
                result.text.push_back(modelText[modelIndex]);
                result.boundaryModelColumns.push_back(column + 1);
                result.canonicalModelBoundaries.push_back(true);
            }
        }
        return result;
    }

    std::vector<std::pair<std::size_t, std::size_t>> ViewModel::ComputeWrapSegments(std::u16string_view text) const
    {
        if (text.empty())
        {
            return {{0, 0}};
        }
        if (wrapWidth_ <= 0.0 && wrapColumn_ <= 0)
        {
            return {{0, text.size()}};
        }

        std::vector<std::size_t> boundaries{0};
        for (std::size_t offset = 0; offset < text.size(); )
        {
            const auto next = Unicode::NextGraphemeBoundary(text, offset);
            offset = next > offset ? next : offset + 1;
            boundaries.push_back(offset);
        }

        std::vector<std::pair<std::size_t, std::size_t>> segments;
        if (wrapWidth_ > 0.0 && textMeasurer_)
        {
            // CanvasTextLayout/DirectWrite measurement is comparatively expensive. Find the
            // longest fitting grapheme range with a monotonic binary search rather than
            // measuring every progressively longer prefix (O(n log n) vs O(n^2) layout work).
            std::size_t clusterStart = 0;
            while (clusterStart + 1 < boundaries.size())
            {
                const auto segmentStart = boundaries[clusterStart];
                std::size_t low = clusterStart + 1;
                std::size_t high = boundaries.size() - 1;
                std::size_t best = low; // A single grapheme is always admitted, even if oversized.

                while (low <= high)
                {
                    const auto mid = low + ((high - low) / 2);
                    const auto candidateEnd = boundaries[mid];
                    const auto width = textMeasurer_->Measure(text.substr(segmentStart, candidateEnd - segmentStart));
                    if (width <= wrapWidth_ || mid == clusterStart + 1)
                    {
                        best = mid;
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }

                segments.push_back({segmentStart, boundaries[best]});
                clusterStart = best;
            }
            return segments;
        }

        if (wrapColumn_ > 0)
        {
            const auto clustersPerLine = static_cast<std::size_t>(wrapColumn_);
            std::size_t clusterStart = 0;
            while (clusterStart + 1 < boundaries.size())
            {
                const auto clusterEnd = std::min(boundaries.size() - 1, clusterStart + clustersPerLine);
                segments.push_back({boundaries[clusterStart], boundaries[clusterEnd]});
                clusterStart = clusterEnd;
            }
            return segments;
        }

        return {{0, text.size()}};
    }

    std::size_t ViewModel::TransformTrackedOffset(
        std::size_t offset,
        InjectedTextAffinity affinity,
        const std::vector<ModelContentChange>& changes) noexcept
    {
        std::ptrdiff_t delta = 0;
        for (const auto& change : changes)
        {
            const auto start = change.rangeOffset;
            const auto end = start + change.rangeLength;
            if (offset < start)
            {
                break;
            }
            if (offset > end || (offset == end && change.rangeLength > 0))
            {
                delta += static_cast<std::ptrdiff_t>(change.text.size()) - static_cast<std::ptrdiff_t>(change.rangeLength);
                continue;
            }

            const auto base = static_cast<std::ptrdiff_t>(start) + delta;
            const auto trailing = affinity == InjectedTextAffinity::MoveAfterInsertion
                ? static_cast<std::ptrdiff_t>(change.text.size())
                : 0;
            return static_cast<std::size_t>(std::max<std::ptrdiff_t>(0, base + trailing));
        }
        return static_cast<std::size_t>(std::max<std::ptrdiff_t>(0, static_cast<std::ptrdiff_t>(offset) + delta));
    }
}
