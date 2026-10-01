#include "MonacoNative/Core/Tokenizer.h"

#include <array>
#include <memory>
#include <string_view>
#include <unordered_set>

namespace MonacoNative::Core
{
    namespace
    {
        bool IsAsciiAlpha(char16_t c) noexcept
        {
            return (c >= u'a' && c <= u'z') || (c >= u'A' && c <= u'Z') || c == u'_';
        }

        bool IsAsciiDigit(char16_t c) noexcept
        {
            return c >= u'0' && c <= u'9';
        }

        bool IsIdentifierPart(char16_t c) noexcept
        {
            return IsAsciiAlpha(c) || IsAsciiDigit(c);
        }

        bool IsSpace(char16_t c) noexcept
        {
            return c == u' ' || c == u'\t' || c == u'\r' || c == u'\n';
        }

        bool IsCppKeyword(std::u16string_view value)
        {
            static const std::unordered_set<std::u16string> keywords{
                u"alignas", u"alignof", u"auto", u"bool", u"break", u"case", u"catch", u"char", u"char16_t",
                u"char32_t", u"class", u"const", u"constexpr", u"consteval", u"constinit", u"continue", u"co_await",
                u"co_return", u"co_yield", u"decltype", u"default", u"delete", u"do", u"double", u"else", u"enum",
                u"explicit", u"export", u"extern", u"false", u"float", u"for", u"friend", u"goto", u"if", u"inline",
                u"int", u"long", u"namespace", u"new", u"noexcept", u"nullptr", u"operator", u"private", u"protected",
                u"public", u"requires", u"return", u"short", u"signed", u"sizeof", u"static", u"struct", u"switch",
                u"template", u"this", u"throw", u"true", u"try", u"typedef", u"typename", u"union", u"unsigned",
                u"using", u"virtual", u"void", u"volatile", u"wchar_t", u"while"
            };
            return keywords.contains(std::u16string(value));
        }

        void Push(std::vector<Token>& output, std::size_t start, std::size_t end, TokenKind kind)
        {
            output.push_back(Token{
                static_cast<std::int32_t>(start + 1),
                static_cast<std::int32_t>(end + 1),
                kind,
            });
        }
    }

    std::vector<Token> CppTokenizer::TokenizeLine(std::u16string_view line) const
    {
        std::vector<Token> output;
        std::size_t i = 0;
        while (i < line.size())
        {
            if (IsSpace(line[i]))
            {
                ++i;
                continue;
            }
            if (i + 1 < line.size() && line[i] == u'/' && line[i + 1] == u'/')
            {
                Push(output, i, line.size(), TokenKind::Comment);
                break;
            }
            if (line[i] == u'"' || line[i] == u'\'')
            {
                const auto quote = line[i];
                const auto start = i++;
                bool escaped = false;
                while (i < line.size())
                {
                    const auto c = line[i++];
                    if (c == quote && !escaped)
                    {
                        break;
                    }
                    escaped = c == u'\\' && !escaped;
                    if (c != u'\\')
                    {
                        escaped = false;
                    }
                }
                Push(output, start, i, TokenKind::String);
                continue;
            }
            if (IsAsciiDigit(line[i]))
            {
                const auto start = i++;
                while (i < line.size() && (IsAsciiDigit(line[i]) || line[i] == u'.' ||
                    (line[i] >= u'a' && line[i] <= u'f') || (line[i] >= u'A' && line[i] <= u'F') || line[i] == u'x' || line[i] == u'X'))
                {
                    ++i;
                }
                Push(output, start, i, TokenKind::Number);
                continue;
            }
            if (IsAsciiAlpha(line[i]))
            {
                const auto start = i++;
                while (i < line.size() && IsIdentifierPart(line[i]))
                {
                    ++i;
                }
                const auto word = line.substr(start, i - start);
                Push(output, start, i, IsCppKeyword(word) ? TokenKind::Keyword : TokenKind::Identifier);
                continue;
            }

            const auto kind = std::u16string_view(u"{}[]();,.:").find(line[i]) != std::u16string_view::npos
                ? TokenKind::Punctuation
                : TokenKind::Operator;
            Push(output, i, i + 1, kind);
            ++i;
        }
        return output;
    }

    std::vector<Token> JsonTokenizer::TokenizeLine(std::u16string_view line) const
    {
        std::vector<Token> output;
        std::size_t i = 0;
        while (i < line.size())
        {
            if (IsSpace(line[i]))
            {
                ++i;
                continue;
            }
            if (line[i] == u'"')
            {
                const auto start = i++;
                bool escaped = false;
                while (i < line.size())
                {
                    const auto c = line[i++];
                    if (c == u'"' && !escaped)
                    {
                        break;
                    }
                    escaped = c == u'\\' && !escaped;
                    if (c != u'\\')
                    {
                        escaped = false;
                    }
                }
                Push(output, start, i, TokenKind::String);
                continue;
            }
            if (IsAsciiDigit(line[i]) || line[i] == u'-')
            {
                const auto start = i++;
                while (i < line.size() && (IsAsciiDigit(line[i]) || line[i] == u'.' || line[i] == u'e' || line[i] == u'E' || line[i] == u'+' || line[i] == u'-'))
                {
                    ++i;
                }
                Push(output, start, i, TokenKind::Number);
                continue;
            }
            if (IsAsciiAlpha(line[i]))
            {
                const auto start = i++;
                while (i < line.size() && IsAsciiAlpha(line[i]))
                {
                    ++i;
                }
                Push(output, start, i, TokenKind::Keyword);
                continue;
            }
            Push(output, i, i + 1, TokenKind::Punctuation);
            ++i;
        }
        return output;
    }

    std::unique_ptr<ITokenizer> CreateBuiltinTokenizer(std::string_view languageId)
    {
        if (languageId == "cpp" || languageId == "c" || languageId == "c++")
        {
            return std::make_unique<CppTokenizer>();
        }
        if (languageId == "json" || languageId == "jsonc")
        {
            return std::make_unique<JsonTokenizer>();
        }
        return nullptr;
    }
}
