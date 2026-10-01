#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace MonacoNative::Input::Native
{
    struct AcpSelection
    {
        std::int32_t start{};
        std::int32_t end{};
        bool activeEndAtStart{};
        bool interimCharacter{};
    };

    struct AcpTextChange
    {
        std::int32_t start{};
        std::int32_t oldEnd{};
        std::int32_t newEnd{};
    };

    struct AcpExternalChangeEvent
    {
        std::vector<AcpTextChange> textChanges{};
        bool selectionChanged{};
    };

    class IAcpTextStoreBackend
    {
    public:
        using ExternalChangeToken = std::uint64_t;
        using ExternalChangeHandler = std::function<void(const AcpExternalChangeEvent&)>;

        virtual ~IAcpTextStoreBackend() = default;

        [[nodiscard]] virtual std::int32_t Length() const noexcept = 0;
        [[nodiscard]] virtual std::u16string GetText(std::int32_t start, std::int32_t end) const = 0;
        [[nodiscard]] virtual AcpSelection GetSelection() const noexcept = 0;
        virtual bool SetSelection(const AcpSelection& selection) noexcept = 0;
        [[nodiscard]] virtual bool IsReadOnly() const noexcept { return false; }
        virtual bool Replace(
            std::int32_t start,
            std::int32_t end,
            std::u16string_view text,
            AcpTextChange& change) = 0;

        // Optional notification channel for edits originating outside TSF.
        // Backends without an external mutable source may keep the default.
        [[nodiscard]] virtual ExternalChangeToken AddExternalChangeHandler(ExternalChangeHandler) { return 0; }
        virtual void RemoveExternalChangeHandler(ExternalChangeToken) noexcept {}
    };
}
