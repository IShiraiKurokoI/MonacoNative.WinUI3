#pragma once
#include "MonacoNative.Language.Native.h"
#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace mn::language
{
inline std::string quote(std::string_view value)
{
    constexpr char hex[] = "0123456789abcdef";
    std::string out = "\"";
    for (unsigned char c : value)
    {
        switch (c)
        {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (c < 32) { out += "\\u00"; out += hex[c >> 4]; out += hex[c & 15]; }
            else out += static_cast<char>(c);
        }
    }
    return out + '"';
}
inline std::string snippet_escape(std::string_view text)
{
    std::string result;
    for (char c : text) { if (c == '\\' || c == '$' || c == '}') result += '\\'; result += c; }
    return result;
}
inline void append(std::string& array, const std::string& value)
{
    if (array.size() > 1) array += ',';
    array += value;
}
inline int output(char** result, std::string_view text) noexcept
{
    if (!result) return 0;
    *result = static_cast<char*>(std::malloc(text.size() + 1));
    if (!*result) return 0;
    std::memcpy(*result, text.data(), text.size());
    (*result)[text.size()] = '\0';
    return 1;
}
inline int fail(char** result, std::string_view message) noexcept
{
    if (result) { try { output(result, "{\"error\":" + quote(message) + "}"); } catch (...) { *result = nullptr; } }
    return 0;
}
inline void validate(const mn_language_document* docs, uint32_t count)
{
    if (!docs || count == 0 || count > 4096) throw std::invalid_argument("One active document and at most 4095 additional snapshots are required.");
    size_t total = 0;
    for (uint32_t i = 0; i < count; ++i)
    {
        if (!docs[i].path || !*docs[i].path || (!docs[i].utf8 && docs[i].byte_length))
            throw std::invalid_argument("Invalid language document.");
        if (docs[i].byte_length > 32 * 1024 * 1024) throw std::length_error("A source snapshot exceeds 32 MiB.");
        total += docs[i].byte_length;
        if (total > 128 * 1024 * 1024) throw std::length_error("Language request exceeds 128 MiB.");
    }
}
inline bool word_byte(unsigned char c) { return c >= 128 || c == '_' || c == '$' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'); }
inline std::pair<size_t, size_t> word_range(std::string_view text, size_t offset)
{
    offset = std::min(offset, text.size());
    size_t start = offset, end = offset;
    while (start && word_byte(static_cast<unsigned char>(text[start - 1]))) --start;
    while (end < text.size() && word_byte(static_cast<unsigned char>(text[end]))) ++end;
    return {start, end};
}
inline std::string range_json(size_t start, size_t end)
{
    return "\"start\":" + std::to_string(start) + ",\"end\":" + std::to_string(end);
}
inline void utf8_append(std::string& out, uint32_t cp)
{
    if (cp <= 0x7f) out += static_cast<char>(cp);
    else if (cp <= 0x7ff) { out += static_cast<char>(0xc0 | (cp >> 6)); out += static_cast<char>(0x80 | (cp & 63)); }
    else if (cp <= 0xffff) { out += static_cast<char>(0xe0 | (cp >> 12)); out += static_cast<char>(0x80 | ((cp >> 6) & 63)); out += static_cast<char>(0x80 | (cp & 63)); }
    else { out += static_cast<char>(0xf0 | (cp >> 18)); out += static_cast<char>(0x80 | ((cp >> 12) & 63)); out += static_cast<char>(0x80 | ((cp >> 6) & 63)); out += static_cast<char>(0x80 | (cp & 63)); }
}
// UTF-8/UTF-16 boundaries are kept explicitly; compiler byte columns must not be
// passed to Monaco as UTF-16 columns (CJK and astral characters differ).
struct Text
{
    std::string utf8;
    std::u16string utf16;
    std::vector<size_t> bytes;
    explicit Text(std::string_view source) : utf8(source)
    {
        for (size_t i = 0; i < source.size();)
        {
            const size_t begin = i;
            uint32_t c = static_cast<unsigned char>(source[i++]);
            unsigned more = 0;
            if (c >= 0xc2 && c <= 0xdf) { c &= 31; more = 1; }
            else if (c >= 0xe0 && c <= 0xef) { c &= 15; more = 2; }
            else if (c >= 0xf0 && c <= 0xf4) { c &= 7; more = 3; }
            else if (c >= 128) c = 0xfffd;
            bool valid = i + more <= source.size();
            for (unsigned j = 0; valid && j < more; ++j)
            {
                const auto b = static_cast<unsigned char>(source[i + j]);
                if ((b & 0xc0) != 0x80) valid = false;
                else c = (c << 6) | (b & 63);
            }
            if (valid && more) i += more;
            if (!valid || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff) || (more == 1 && c < 0x80) || (more == 2 && c < 0x800) || (more == 3 && c < 0x10000)) c = 0xfffd;
            bytes.push_back(begin);
            if (c >= 0x10000) { c -= 0x10000; utf16 += static_cast<char16_t>(0xd800 + (c >> 10)); bytes.push_back(begin); utf16 += static_cast<char16_t>(0xdc00 + (c & 1023)); }
            else utf16 += static_cast<char16_t>(c);
        }
        bytes.push_back(source.size());
    }
    size_t to_byte(size_t offset) const { return bytes[std::min(offset, utf16.size())]; }
    size_t to_u16(size_t offset) const
    {
        auto it = std::lower_bound(bytes.begin(), bytes.end(), std::min(offset, utf8.size()));
        return static_cast<size_t>(it - bytes.begin());
    }
    std::string slice(size_t start, size_t end) const { return utf8.substr(to_byte(start), to_byte(end) - to_byte(start)); }
};
}
