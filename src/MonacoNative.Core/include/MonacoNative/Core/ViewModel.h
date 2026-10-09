#pragma once

#include "FoldingModel.h"
#include "TextMeasurer.h"
#include "TextModel.h"
#include "SpliceablePrefixSum.h"

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace MonacoNative::Core
{
    struct ViewLine final
    {
        // Absolute identities are lookup-time metadata. The authoritative projection is
        // stored per model line, matching Monaco's modelLineProjections + prefix-count
        // design, so local edits never renumber an untouched flattened suffix.
        mutable std::int32_t viewLineNumber{};
        mutable std::int32_t modelLineNumber{};
        std::int32_t startColumn{};
        std::int32_t endColumn{};
        std::int32_t wrappedIndex{};
        std::u16string text{};
        // One entry per UTF-16 boundary in text (text.size() + 1). Injected-text
        // boundaries intentionally map to the same model column as their anchor.
        std::vector<std::int32_t> boundaryModelColumns{};
        // True only for the canonical visual boundary of a real model position.
        std::vector<bool> canonicalModelBoundaries{};
        // Width of the complete projected model line, cached on its first fragment.
        // -1 means unknown. Valid until this model line or its measurer changes.
        double fullProjectedWidth{-1.0};
    };

    struct ViewPosition final
    {
        std::int32_t lineNumber{1};
        std::int32_t column{1};

        friend bool operator==(const ViewPosition&, const ViewPosition&) = default;
    };

    struct HiddenArea final
    {
        std::int32_t startLineNumber{1};
        std::int32_t endLineNumber{1};

        friend bool operator==(const HiddenArea&, const HiddenArea&) = default;
    };

    using ViewZoneId = std::uint64_t;

    struct ViewZone final
    {
        ViewZoneId id{};
        std::int32_t afterLineNumber{};
        double height{};
    };

    using InjectedTextId = std::uint64_t;

    enum class RevealStrategy : std::uint8_t
    {
        Minimal,
        Top,
        Center,
        Bottom,
    };

    enum class InjectedTextAffinity : std::uint8_t
    {
        StayBeforeInsertion,
        MoveAfterInsertion,
    };

    struct InjectedText final
    {
        InjectedTextId id{};
        Position position{};
        std::u16string content{};
        InjectedTextAffinity affinity{InjectedTextAffinity::MoveAfterInsertion};
        std::int32_t order{};
    };

    struct ViewProjectionUpdateStats final
    {
        std::uint64_t replacementModelLines{};
        std::uint64_t replacementViewLines{};
        std::uint64_t suffixViewLineMetadataRewrites{};
        std::uint64_t projectionSlotMoves{};
        std::uint64_t reusedModelLines{};
    };

    class ViewModel final
    {
    public:
        explicit ViewModel(std::shared_ptr<TextModel> model);
        ~ViewModel();

        ViewModel(const ViewModel&) = delete;
        ViewModel& operator=(const ViewModel&) = delete;
        ViewModel(ViewModel&&) = delete;
        ViewModel& operator=(ViewModel&&) = delete;

        // Compatibility wrapping mode: wraps after approximately N grapheme clusters.
        void SetWrapColumn(std::int32_t wrapColumn);
        [[nodiscard]] std::int32_t WrapColumn() const noexcept;

        // Measured wrapping takes precedence when width > 0. The measurer is intentionally
        // platform-neutral; a DirectWrite implementation can be supplied by the WinUI layer.
        void SetWrapWidth(double wrapWidth);
        [[nodiscard]] double WrapWidth() const noexcept;
        void SetTextMeasurer(std::shared_ptr<ITextMeasurer> measurer);
        [[nodiscard]] const std::shared_ptr<ITextMeasurer>& TextMeasurer() const noexcept;

        void SetLineHeight(double lineHeight);
        [[nodiscard]] double LineHeight() const noexcept;

        void SetHiddenAreas(std::vector<HiddenArea> areas);
        [[nodiscard]] const std::vector<HiddenArea>& HiddenAreas() const noexcept;
        [[nodiscard]] bool IsModelLineHidden(std::int32_t modelLineNumber) const noexcept;

        void SetFoldingModel(std::shared_ptr<FoldingModel> foldingModel);
        [[nodiscard]] const std::shared_ptr<FoldingModel>& GetFoldingModel() const noexcept;

        [[nodiscard]] InjectedTextId AddInjectedText(
            Position position,
            std::u16string content,
            InjectedTextAffinity affinity = InjectedTextAffinity::MoveAfterInsertion,
            std::int32_t order = 0);
        bool UpdateInjectedText(InjectedTextId id, std::u16string content);
        bool RemoveInjectedText(InjectedTextId id);
        void ClearInjectedText();
        [[nodiscard]] std::vector<InjectedText> InjectedTexts() const;

        [[nodiscard]] ViewZoneId AddViewZone(std::int32_t afterLineNumber, double height);
        bool RemoveViewZone(ViewZoneId id);
        void ClearViewZones();
        [[nodiscard]] const std::vector<ViewZone>& ViewZones() const noexcept;

        void Rebuild();
        [[nodiscard]] std::int32_t ViewLineCount() const noexcept;
        [[nodiscard]] const ViewLine& GetViewLine(std::int32_t viewLineNumber) const;
        [[nodiscard]] ViewPosition ConvertModelPositionToViewPosition(Position position) const;
        [[nodiscard]] Position ConvertViewPositionToModelPosition(ViewPosition position) const;
        [[nodiscard]] double GetVerticalOffsetForViewLine(std::int32_t viewLineNumber) const noexcept;
        [[nodiscard]] std::int32_t GetViewLineAtVerticalOffset(double offset) const noexcept;
        [[nodiscard]] std::pair<std::int32_t, std::int32_t> GetViewLineRangeForModelRange(Range range) const;
        [[nodiscard]] double GetScrollTopToRevealRange(
            Range range,
            double viewportHeight,
            double currentScrollTop,
            RevealStrategy strategy = RevealStrategy::Minimal,
            double padding = 0.0) const;
        [[nodiscard]] double TotalHeight() const noexcept;
        [[nodiscard]] ViewProjectionUpdateStats LastProjectionUpdateStats() const noexcept;

    private:
        struct TrackedInjectedText final
        {
            InjectedTextId id{};
            std::size_t offset{};
            std::u16string content{};
            InjectedTextAffinity affinity{InjectedTextAffinity::MoveAfterInsertion};
            std::int32_t order{};
        };

        struct ProjectedLine final
        {
            std::u16string text{};
            std::vector<std::int32_t> boundaryModelColumns{};
            std::vector<bool> canonicalModelBoundaries{};
        };

        struct LineChangeGroup final
        {
            std::int32_t oldStartLine{};
            std::int32_t oldEndLine{};
            std::int32_t lineDelta{};
        };

        void HandleModelChange(const ModelContentChangedEvent& event);
        [[nodiscard]] bool TryApplyIncrementalModelChanges(const ModelContentChangedEvent& event);
        [[nodiscard]] std::vector<LineChangeGroup> BuildLineChangeGroups(const ModelContentChangedEvent& event) const;
        void ApplyProjectionSplice(std::int32_t startLine, std::int32_t oldModelLineCount, std::int32_t newModelLineCount);
        void ReflowWrappingChange(double previousWrapWidth, std::int32_t previousWrapColumn);
        void RefreshModelLine(std::int32_t modelLineNumber);
        void RefreshHiddenAreasIncrementally();
        void ReconcileVisibilityWithHiddenAreas();
        void TransformViewZoneAnchors(const std::vector<LineChangeGroup>& groups);
        void RefreshViewZoneHeightForLine(std::int32_t modelLineNumber);
        [[nodiscard]] std::pair<std::int32_t, std::int32_t> GetModelLineViewRange(std::int32_t modelLineNumber) const;
        [[nodiscard]] std::size_t GetModelIndexForViewLine(std::int32_t viewLineNumber) const;
        void NormalizeHiddenAreas();
        [[nodiscard]] bool IsLineVisible(std::int32_t lineNumber) const noexcept;
        [[nodiscard]] double ZoneHeightAfterLine(std::int32_t lineNumber) const noexcept;
        [[nodiscard]] ProjectedLine BuildProjectedLine(std::int32_t modelLineNumber) const;
        [[nodiscard]] std::vector<std::pair<std::size_t, std::size_t>> ComputeWrapSegments(std::u16string_view text) const;
        [[nodiscard]] static std::size_t TransformTrackedOffset(
            std::size_t offset,
            InjectedTextAffinity affinity,
            const std::vector<ModelContentChange>& changes) noexcept;

        std::shared_ptr<TextModel> model_{};
        EventSource<ModelContentChangedEvent>::Token modelChangeToken_{};
        std::int32_t wrapColumn_{0};
        double wrapWidth_{0.0};
        std::shared_ptr<ITextMeasurer> textMeasurer_{std::make_shared<FixedAdvanceTextMeasurer>()};
        double lineHeight_{20.0};
        std::vector<HiddenArea> explicitHiddenAreas_{};
        std::vector<HiddenArea> hiddenAreas_{};
        std::shared_ptr<FoldingModel> foldingModel_{};
        EventSource<FoldingChangedEvent>::Token foldingChangeToken_{};
        std::vector<TrackedInjectedText> injectedTexts_{};
        InjectedTextId nextInjectedTextId_{1};
        std::vector<ViewZone> viewZones_{};
        ViewZoneId nextViewZoneId_{1};
        std::vector<std::vector<ViewLine>> modelLineViewLines_{};
        SpliceablePrefixSum<std::int64_t> modelLineViewCounts_{};
        SpliceablePrefixSum<double> modelLineBlockHeights_{};
        double leadingZoneHeight_{};
        double totalHeight_{};
        std::uint64_t lastProcessedModelVersion_{};
        bool pendingFoldingProjectionChange_{};
        ViewProjectionUpdateStats lastProjectionUpdateStats_{};
    };
}
