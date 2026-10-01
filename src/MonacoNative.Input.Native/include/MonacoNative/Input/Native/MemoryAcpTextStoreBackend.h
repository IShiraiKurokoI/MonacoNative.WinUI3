#pragma once

#include "MonacoNative/Input/Native/AcpTextStoreBackend.h"

#include <string>
#include <string_view>

namespace MonacoNative::Input::Native
{
    class MemoryAcpTextStoreBackend final : public IAcpTextStoreBackend
    {
    public:
        explicit MemoryAcpTextStoreBackend(std::u16string text = {});

        [[nodiscard]] std::int32_t Length() const noexcept override;
        [[nodiscard]] std::u16string GetText(std::int32_t start, std::int32_t end) const override;
        [[nodiscard]] AcpSelection GetSelection() const noexcept override;
        bool SetSelection(const AcpSelection& selection) noexcept override;
        bool Replace(
            std::int32_t start,
            std::int32_t end,
            std::u16string_view text,
            AcpTextChange& change) override;

        [[nodiscard]] const std::u16string& Value() const noexcept { return text_; }

    private:
        [[nodiscard]] bool IsValidRange(std::int32_t start, std::int32_t end) const noexcept;

        std::u16string text_;
        AcpSelection selection_{};
    };
}
