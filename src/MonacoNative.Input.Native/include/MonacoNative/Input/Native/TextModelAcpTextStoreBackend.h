#pragma once

#include "MonacoNative/Input/Native/AcpTextStoreBackend.h"
#include "MonacoNative/Core/CursorModel.h"
#include "MonacoNative/Core/TextModel.h"

#include <cstdint>
#include <memory>
#include <unordered_map>

namespace MonacoNative::Input::Native
{
    // ACP adapter over MonacoNative::Core::TextModel.
    //
    // ACP positions are UTF-16 code-unit offsets, which intentionally match
    // TextModel/PieceTree offsets. Text is read by range directly from the
    // model; the backend never mirrors or snapshots the full document.
    class TextModelAcpTextStoreBackend final : public IAcpTextStoreBackend
    {
    public:
        explicit TextModelAcpTextStoreBackend(
            std::shared_ptr<Core::TextModel> model,
            std::shared_ptr<Core::CursorModel> cursor = {});
        ~TextModelAcpTextStoreBackend() override;

        TextModelAcpTextStoreBackend(const TextModelAcpTextStoreBackend&) = delete;
        TextModelAcpTextStoreBackend& operator=(const TextModelAcpTextStoreBackend&) = delete;
        TextModelAcpTextStoreBackend(TextModelAcpTextStoreBackend&&) = delete;
        TextModelAcpTextStoreBackend& operator=(TextModelAcpTextStoreBackend&&) = delete;

        [[nodiscard]] std::int32_t Length() const noexcept override;
        [[nodiscard]] std::u16string GetText(std::int32_t start, std::int32_t end) const override;
        [[nodiscard]] AcpSelection GetSelection() const noexcept override;
        bool SetSelection(const AcpSelection& selection) noexcept override;
        bool Replace(
            std::int32_t start,
            std::int32_t end,
            std::u16string_view text,
            AcpTextChange& change) override;
        [[nodiscard]] ExternalChangeToken AddExternalChangeHandler(ExternalChangeHandler handler) override;
        void RemoveExternalChangeHandler(ExternalChangeToken token) noexcept override;

        [[nodiscard]] const std::shared_ptr<Core::TextModel>& Model() const noexcept { return model_; }
        [[nodiscard]] const std::shared_ptr<Core::CursorModel>& Cursor() const noexcept { return cursor_; }

    private:
        [[nodiscard]] bool IsValidRange(std::int32_t start, std::int32_t end) const noexcept;
        [[nodiscard]] bool IsAcpAddressable() const noexcept;
        void OnModelChanged(const Core::ModelContentChangedEvent& event) noexcept;
        void OnCursorSelectionChanged(const std::vector<Core::Selection>& selections) noexcept;
        [[nodiscard]] AcpSelection SelectionFromCursor() const noexcept;
        bool ApplySelectionToCursor(const AcpSelection& selection) noexcept;
        void NotifyExternalChange(const AcpExternalChangeEvent& event) noexcept;
        [[nodiscard]] static std::int32_t TransformOffset(
            std::int32_t offset,
            bool affinityAfter,
            const Core::ModelContentChangedEvent& event) noexcept;

        std::shared_ptr<Core::TextModel> model_{};
        std::shared_ptr<Core::CursorModel> cursor_{};
        Core::EventSource<Core::ModelContentChangedEvent>::Token modelChangeToken_{};
        Core::EventSource<std::vector<Core::Selection>>::Token cursorSelectionToken_{};
        AcpSelection selection_{};
        ExternalChangeToken nextExternalChangeToken_{1};
        std::unordered_map<ExternalChangeToken, ExternalChangeHandler> externalChangeHandlers_{};
        std::uint32_t suppressExternalNotifications_{};
        std::uint32_t suppressCursorSelectionNotifications_{};
    };
}
