#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace MonacoNative::Core
{
    enum class TokenKind : std::uint8_t
    {
        Plain,
        Keyword,
        Number,
        String,
        Comment,
        Operator,
        Type,
        Identifier,
        Punctuation,
    };

    struct Token final
    {
        std::int32_t startColumn{1};
        std::int32_t endColumn{1};
        TokenKind kind{TokenKind::Plain};
    };

    class ITokenizer
    {
    public:
        virtual ~ITokenizer() = default;
        [[nodiscard]] virtual std::vector<Token> TokenizeLine(std::u16string_view line) const = 0;
    };

    class CppTokenizer final : public ITokenizer
    {
    public:
        [[nodiscard]] std::vector<Token> TokenizeLine(std::u16string_view line) const override;
    };

    class JsonTokenizer final : public ITokenizer
    {
    public:
        [[nodiscard]] std::vector<Token> TokenizeLine(std::u16string_view line) const override;
    };

    [[nodiscard]] std::unique_ptr<ITokenizer> CreateBuiltinTokenizer(std::string_view languageId);
}
