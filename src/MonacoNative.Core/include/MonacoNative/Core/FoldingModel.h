#pragma once

#include "Event.h"
#include "TextModel.h"

#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace MonacoNative::Core
{
    using FoldingRangeId = std::uint64_t;

    struct FoldingRangeCandidate final
    {
        std::int32_t startLineNumber{1};
        std::int32_t endLineNumber{1};
        std::string kind{};

        friend bool operator==(const FoldingRangeCandidate&, const FoldingRangeCandidate&) = default;
    };

    struct FoldingRange final
    {
        FoldingRangeId id{};
        std::int32_t startLineNumber{1};
        std::int32_t endLineNumber{1};
        bool collapsed{};
        std::string kind{};

        friend bool operator==(const FoldingRange&, const FoldingRange&) = default;
    };

    struct FoldingChangedEvent final
    {
        std::vector<FoldingRange> ranges{};
    };

    struct FoldingHiddenArea final
    {
        std::int32_t startLineNumber{1};
        std::int32_t endLineNumber{1};

        friend bool operator==(const FoldingHiddenArea&, const FoldingHiddenArea&) = default;
    };

    class IFoldingRangeProvider
    {
    public:
        virtual ~IFoldingRangeProvider() = default;
        [[nodiscard]] virtual std::vector<FoldingRangeCandidate> Compute(const TextModel& model) const = 0;
    };

    class IndentationFoldingRangeProvider final : public IFoldingRangeProvider
    {
    public:
        explicit IndentationFoldingRangeProvider(std::int32_t tabSize = 4) noexcept;
        [[nodiscard]] std::vector<FoldingRangeCandidate> Compute(const TextModel& model) const override;

    private:
        std::int32_t tabSize_{4};
    };

    class FoldingModel final
    {
    public:
        explicit FoldingModel(std::shared_ptr<TextModel> model);
        ~FoldingModel();

        FoldingModel(const FoldingModel&) = delete;
        FoldingModel& operator=(const FoldingModel&) = delete;
        FoldingModel(FoldingModel&&) = delete;
        FoldingModel& operator=(FoldingModel&&) = delete;

        void SetRanges(std::vector<FoldingRangeCandidate> ranges);
        void UpdateFromProvider(const IFoldingRangeProvider& provider);
        [[nodiscard]] const std::vector<FoldingRange>& Ranges() const noexcept;
        [[nodiscard]] std::vector<FoldingHiddenArea> HiddenAreas() const;

        bool SetCollapsed(FoldingRangeId id, bool collapsed);
        bool Toggle(FoldingRangeId id);
        std::size_t SetCollapsedAtLine(std::int32_t lineNumber, bool collapsed, bool recursive = false);
        void CollapseAll();
        void ExpandAll();

        [[nodiscard]] EventSource<FoldingChangedEvent>& OnDidChange() noexcept;

    private:
        struct TrackedRange final
        {
            FoldingRange range{};
            std::size_t startOffset{};
            std::size_t endOffset{};
        };

        void HandleModelChange(const ModelContentChangedEvent& event);
        void RebuildPublicRangesFromOffsets();
        void EmitChanged();
        [[nodiscard]] static std::size_t TransformOffset(
            std::size_t offset,
            bool moveAfterInsertionAtBoundary,
            const std::vector<ModelContentChange>& changes) noexcept;

        std::shared_ptr<TextModel> model_{};
        EventSource<ModelContentChangedEvent>::Token modelChangeToken_{};
        std::vector<TrackedRange> tracked_{};
        std::vector<FoldingRange> ranges_{};
        FoldingRangeId nextId_{1};
        EventSource<FoldingChangedEvent> didChange_{};
    };
}
