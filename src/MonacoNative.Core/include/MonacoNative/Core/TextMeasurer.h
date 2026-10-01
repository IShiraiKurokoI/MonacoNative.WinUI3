#pragma once

#include <algorithm>
#include <cmath>
#include <string_view>

namespace MonacoNative::Core
{
    class ITextMeasurer
    {
    public:
        virtual ~ITextMeasurer() = default;
        [[nodiscard]] virtual double Measure(std::u16string_view text) const = 0;
    };

    class FixedAdvanceTextMeasurer final : public ITextMeasurer
    {
    public:
        explicit FixedAdvanceTextMeasurer(double advance = 1.0) noexcept
            : advance_(std::max(0.01, std::isfinite(advance) ? advance : 1.0))
        {
        }

        [[nodiscard]] double Advance() const noexcept
        {
            return advance_;
        }

        [[nodiscard]] double Measure(std::u16string_view text) const override
        {
            return static_cast<double>(text.size()) * advance_;
        }

    private:
        double advance_{1.0};
    };
}
