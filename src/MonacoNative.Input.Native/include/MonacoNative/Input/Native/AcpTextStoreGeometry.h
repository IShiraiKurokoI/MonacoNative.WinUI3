#pragma once

#include <cstdint>
#include <memory>

namespace MonacoNative::Input::Native
{
    struct AcpScreenPoint final
    {
        std::int32_t x{};
        std::int32_t y{};
    };

    struct AcpScreenRect final
    {
        std::int32_t left{};
        std::int32_t top{};
        std::int32_t right{};
        std::int32_t bottom{};
    };

    // Host-provided geometry for a single active editor view. Coordinates are
    // desktop screen pixels because TSF's ITextStoreACP geometry contract is
    // screen-coordinate based. The implementation is intentionally independent
    // from WinUI/Win2D so the COM text store does not depend on the renderer.
    class IAcpTextStoreGeometry
    {
    public:
        virtual ~IAcpTextStoreGeometry() = default;

        [[nodiscard]] virtual bool HasLayout() const noexcept = 0;
        [[nodiscard]] virtual bool GetAcpFromScreenPoint(
            AcpScreenPoint point,
            std::uint32_t flags,
            std::int32_t& acp) const noexcept = 0;
        [[nodiscard]] virtual bool GetTextScreenRect(
            std::int32_t start,
            std::int32_t end,
            AcpScreenRect& rect,
            bool& clipped) const noexcept = 0;
        [[nodiscard]] virtual bool GetScreenRect(AcpScreenRect& rect) const noexcept = 0;
    };
}
