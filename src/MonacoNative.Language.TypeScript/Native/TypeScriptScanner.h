// Adapted from Microsoft TypeScript 5.8.3. Copyright (c) Microsoft Corporation.
// Apache-2.0; full attribution and license are in THIRD_PARTY_NOTICES.md.
#pragma once
#include "NativeLanguageSupport.h"
#include "TypeScriptTables.h"
#include <charconv>
#include <cmath>
#include <sstream>
#include <unordered_map>
#include <type_traits>
#include <utility>

namespace mn::typescript
{
// Adapted from TypeScript 5.8.3 scanner.ts. Token IDs and Unicode tables are
// upstream values. Regex grammar validation is NOT ported. Parser-facing JSX,
// JSDoc and speculative scanning are ported; this is not a JSX parser/type checker.
// The scanner operates on UTF-16; the editor bridge alone converts byte offsets.
struct ScanError { size_t start, end; std::string message; };
struct Token { int kind = sk::Unknown; size_t start = 0, end = 0; std::string value; unsigned flags = 0; };
inline bool line_break(int c) { return c == 10 || c == 13 || c == 0x2028 || c == 0x2029; }
inline bool space(int c)
{
    return c == 9 || c == 11 || c == 12 || c == 32 || c == 133 || c == 160 || c == 5760 ||
        (c >= 8192 && c <= 8203) || c == 8239 || c == 8287 || c == 12288 || c == 65279;
}
inline bool digit(int c) { return c >= '0' && c <= '9'; }
inline int hex_value(int c) { return digit(c) ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }
template<size_t N> inline bool in_map(int c, const uint32_t (&map)[N])
{
    if (c < 0) return false;
    size_t low = 0, high = N / 2;
    while (low < high)
    {
        const auto mid = (low + high) / 2;
        if (static_cast<uint32_t>(c) < map[mid * 2]) high = mid;
        else if (static_cast<uint32_t>(c) > map[mid * 2 + 1]) low = mid + 1;
        else return true;
    }
    return false;
}
inline bool id_start(int c) { return c == '$' || c == '_' || in_map(c, unicodeESNextIdentifierStart); }
inline bool id_part(int c) { return c == '$' || c == '_' || c == 0x200c || c == 0x200d || in_map(c, unicodeESNextIdentifierPart); }
inline std::string codepoint(int cp)
{
    std::string result;
    mn::language::utf8_append(result, static_cast<uint32_t>(cp));
    return result;
}
inline std::string number_string(double value)
{
    if (std::isinf(value)) return "Infinity";
    if (std::isnan(value)) return "NaN";
    if (value == 0) return "0";
    char data[128];
    auto converted = std::to_chars(data, data + sizeof(data), value, std::chars_format::general);
    std::string s(data, converted.ptr);
    auto e = s.find('e');
    if (e == std::string::npos) return s;
    int exponent = std::stoi(s.substr(e + 1));
    std::string mantissa = s.substr(0, e);
    if (exponent >= -6 && exponent < 21)
    {
        const auto dot = mantissa.find('.');
        if (dot != std::string::npos) mantissa.erase(dot, 1);
        const auto point = exponent + 1;
        if (point <= 0) return "0." + std::string(static_cast<size_t>(-point), '0') + mantissa;
        if (static_cast<size_t>(point) >= mantissa.size()) return mantissa + std::string(static_cast<size_t>(point) - mantissa.size(), '0');
        mantissa.insert(static_cast<size_t>(point), 1, '.'); return mantissa;
    }
    return mantissa + "e" + (exponent >= 0 ? "+" : "") + std::to_string(exponent);
}
inline std::string decimal_bigint(std::string_view digits, int base)
{
    std::string result = "0";
    for (int c : digits)
    {
        int carry = hex_value(c);
        for (size_t j = result.size(); j-- > 0;)
        { int v = (result[j] - '0') * base + carry; result[j] = static_cast<char>('0' + v % 10); carry = v / 10; }
        while (carry) { result.insert(result.begin(), static_cast<char>('0' + carry % 10)); carry /= 10; }
    }
    return result;
}
class Scanner
{
    const mn::language::Text& source;
    const std::u16string& text;
    bool skip_trivia;
    size_t scan_end;
    bool jsx_mode = false;
    int ch(size_t index) const { return index < scan_end ? text[index] : -1; }
    int cp(size_t index) const
    {
        const int c = ch(index), next = ch(index + 1);
        return c >= 0xd800 && c <= 0xdbff && next >= 0xdc00 && next <= 0xdfff ? 0x10000 + ((c - 0xd800) << 10) + next - 0xdc00 : c;
    }
    static size_t width(int c) { return c > 0xffff ? 2U : 1U; }
    void error(std::string message, size_t start, size_t length = 1) { errors.push_back({start, std::min(start + length, scan_end), std::move(message)}); }
    std::string fragment(int base, bool decimal = false)
    {
        bool separatorAllowed = false, previousSeparator = false;
        std::string value;
        while (pos < scan_end)
        {
            int c = ch(pos);
            if (c == '_')
            {
                flags |= tf::ContainsSeparator;
                if (!separatorAllowed)
                {
                    if (decimal) flags |= tf::ContainsInvalidSeparator;
                    error(previousSeparator ? "Multiple consecutive numeric separators are not permitted." : "Numeric separators are not allowed here.", pos);
                }
                previousSeparator = true; separatorAllowed = false; ++pos; continue;
            }
            const int d = hex_value(c);
            if (d < 0 || d >= base) break;
            value += static_cast<char>(c >= 'A' && c <= 'F' ? c + 32 : c);
            ++pos; separatorAllowed = true; previousSeparator = false;
        }
        if (pos && ch(pos - 1) == '_')
        { if (decimal) flags |= tf::ContainsInvalidSeparator; error("Numeric separators are not allowed here.", pos - 1); }
        return value;
    }
    int suffix(std::string value, int base)
    {
        if (ch(pos) == 'n')
        {
            ++pos;
            if (base == 2 || base == 8) token_value = decimal_bigint(value.substr(2), base) + "n";
            else token_value = value + "n";
            return sk::BigIntLiteral;
        }
        double number = 0;
        if (base != 10) { for (size_t i = 2; i < value.size(); ++i) number = number * base + hex_value(value[i]); }
        else { char* end = nullptr; number = std::strtod(value.c_str(), &end); }
        token_value = number_string(number);
        return sk::NumericLiteral;
    }
    void number_identifier(size_t numericStart, bool scientific = false)
    {
        if (!id_start(cp(pos))) return;
        const auto saved = pos;
        identifier_parts();
        if (pos == saved + 1 && ch(saved) == 'n') error(scientific ? "A bigint literal cannot use exponential notation." : "A bigint literal must be an integer.", numericStart, pos - numericStart);
        else { error("An identifier or keyword cannot immediately follow a numeric literal.", saved, pos - saved); pos = saved; }
    }
    int scan_number()
    {
        const auto start = pos;
        std::string main;
        if (ch(pos) == '0')
        {
            if (pos + 2 < scan_end && (ch(pos + 1) == 'x' || ch(pos + 1) == 'X' || ch(pos + 1) == 'b' || ch(pos + 1) == 'B' || ch(pos + 1) == 'o' || ch(pos + 1) == 'O'))
            {
                const auto prefix = ch(pos + 1) | 32; pos += 2;
                const int base = prefix == 'x' ? 16 : prefix == 'b' ? 2 : 8;
                auto value = fragment(base);
                if (value.empty()) { error(base == 16 ? "Hexadecimal digit expected." : base == 2 ? "Binary digit expected." : "Octal digit expected.", pos, 0); value = "0"; }
                flags |= base == 16 ? tf::HexSpecifier : base == 2 ? tf::BinarySpecifier : tf::OctalSpecifier;
                return suffix(std::string("0") + static_cast<char>(prefix) + value, base);
            }
            ++pos;
            if (ch(pos) == '_') { flags |= tf::ContainsSeparator | tf::ContainsInvalidSeparator; error("Numeric separators are not allowed here.", pos); --pos; main = fragment(10, true); }
            else
            {
                const auto rest = pos; bool octal = true;
                while (digit(ch(pos))) { if (ch(pos) > '7') octal = false; ++pos; }
                if (pos == rest) main = "0";
                else if (octal)
                {
                    double value = 0; for (auto i = rest; i < pos; ++i) value = value * 8 + ch(i) - '0';
                    token_value = number_string(value); flags |= tf::Octal;
                    error("Octal literals are not allowed. Use the 0o prefix.", start, pos - start); return sk::NumericLiteral;
                }
                else { flags |= tf::ContainsLeadingZero; main = source.slice(start, pos); }
            }
        }
        else main = fragment(10, true);
        bool decimal = false; std::string fraction, scientific;
        if (ch(pos) == '.') { ++pos; decimal = true; fraction = fragment(10, true); }
        auto end = pos;
        if (ch(pos) == 'e' || ch(pos) == 'E')
        {
            const auto e = pos++; flags |= tf::Scientific;
            if (ch(pos) == '+' || ch(pos) == '-') ++pos;
            const auto digits = pos; const auto tail = fragment(10, true);
            if (tail.empty()) error("Digit expected.", pos, 0);
            else { scientific = source.slice(e, digits) + tail; end = pos; }
        }
        std::string result = flags & tf::ContainsSeparator ? main + (fraction.empty() ? "" : "." + fraction) + scientific : source.slice(start, end);
        if (flags & tf::ContainsLeadingZero)
        { error("Decimals with leading zeros are not allowed.", start, end - start); token_value = number_string(std::strtod(result.c_str(), nullptr)); return sk::NumericLiteral; }
        if (decimal || (flags & tf::Scientific))
        { number_identifier(start, !decimal && (flags & tf::Scientific)); token_value = number_string(std::strtod(result.c_str(), nullptr)); return sk::NumericLiteral; }
        const int literalKind = suffix(result, 10); number_identifier(start); return literalKind;
    }
    int peek_escape(bool extended) const
    {
        if (ch(pos) != '\\' || ch(pos + 1) != 'u') return -1;
        size_t p = pos + 2;
        if (extended) { if (ch(p++) != '{') return -1; }
        else if (p + 4 > scan_end) return -1;
        int value = 0; size_t count = 0;
        while (p < scan_end && hex_value(ch(p)) >= 0 && (extended || count < 4))
        { if (value <= 0x10ffff) value = value * 16 + hex_value(ch(p)); ++p; ++count; }
        return count && (extended || count == 4) ? value : -1;
    }
    std::string extended_escape(bool report)
    {
        const auto start = pos; pos += 3; const auto digits = pos;
        uint32_t value = 0; bool any = false;
        while (hex_value(ch(pos)) >= 0) { if (value <= 0x10ffff) value = value * 16 + static_cast<unsigned>(hex_value(ch(pos))); ++pos; any = true; }
        bool invalid = !any || value > 0x10ffff;
        if (!any && report) error("Hexadecimal digit expected.", pos, 0);
        else if (value > 0x10ffff && report) error("An extended Unicode escape value must be between 0x0 and 0x10FFFF.", digits, pos - digits);
        if (ch(pos) == '}') ++pos;
        else { invalid = true; if (report) error(pos == scan_end ? "Unexpected end of text." : "Unterminated Unicode escape sequence.", pos, 0); }
        if (invalid) { flags |= tf::ContainsInvalidEscape; return source.slice(start, pos); }
        flags |= tf::ExtendedUnicodeEscape; return codepoint(static_cast<int>(value));
    }
    std::string escape(bool report)
    {
        const auto start = pos++;
        if (pos >= scan_end) { error("Unexpected end of text.", pos, 0); return ""; }
        int c = ch(pos++);
        if (c >= '0' && c <= '7')
        {
            if (c == '0' && !digit(ch(pos))) return std::string(1, '\0');
            if (c <= '3' && ch(pos) >= '0' && ch(pos) <= '7') ++pos;
            if (ch(pos) >= '0' && ch(pos) <= '7') ++pos;
            flags |= tf::ContainsInvalidEscape;
            if (report) { error("Octal escape sequences are not allowed.", start, pos - start); int value = 0; for (auto i = start + 1; i < pos; ++i) value = value * 8 + ch(i) - '0'; return codepoint(value); }
            return source.slice(start, pos);
        }
        switch (c)
        {
        case '8': case '9': flags |= tf::ContainsInvalidEscape; if (report) { error("Escape sequence is not allowed.", start, 2); return codepoint(c); } return source.slice(start, pos);
        case 'b': return "\b"; case 't': return "\t"; case 'n': return "\n";
        case 'v': return "\v"; case 'f': return "\f"; case 'r': return "\r";
        case 'u': case 'x':
        {
            if (c == 'u' && ch(pos) == '{') { pos = start; return extended_escape(report); }
            const auto finish = start + (c == 'u' ? 6U : 4U); unsigned value = 0;
            while (pos < finish)
            {
                const int d = hex_value(ch(pos));
                if (d < 0) { flags |= tf::ContainsInvalidEscape; if (report) error("Hexadecimal digit expected.", pos, 0); return source.slice(start, pos); }
                value = value * 16 + static_cast<unsigned>(d); ++pos;
            }
            flags |= c == 'u' ? tf::UnicodeEscape : tf::HexEscape;
            // UTF-16 surrogate escapes are represented as JSON escapes by the output
            // serializer only when paired. Lone-surrogate value fidelity is not claimed.
            if (value >= 0xd800 && value <= 0xdbff && ch(pos) == '\\' && ch(pos + 1) == 'u')
            {
                const auto low = peek_escape(false);
                if (low >= 0xdc00 && low <= 0xdfff) { pos += 6; return codepoint(0x10000 + ((static_cast<int>(value) - 0xd800) << 10) + low - 0xdc00); }
            }
            return codepoint(value >= 0xd800 && value <= 0xdfff ? 0xfffd : static_cast<int>(value));
        }
        case '\r': if (ch(pos) == '\n') ++pos; return "";
        case '\n': case 0x2028: case 0x2029: return "";
        default: return codepoint(c);
        }
    }
    std::string scan_string(bool jsx = false)
    {
        const int quote = ch(pos++); std::string value; auto start = pos;
        while (true)
        {
            if (pos >= scan_end || (!jsx && (ch(pos) == '\n' || ch(pos) == '\r')))
            { value += source.slice(start, pos); flags |= tf::Unterminated; error("Unterminated string literal.", pos, 0); break; }
            if (ch(pos) == quote) { value += source.slice(start, pos++); break; }
            if (ch(pos) == '\\' && !jsx) { value += source.slice(start, pos); value += escape(true); start = pos; }
            else ++pos;
        }
        return value;
    }
    int template_token(bool report)
    {
        const bool first = ch(pos) == '`'; ++pos; auto start = pos; std::string value;
        while (true)
        {
            if (pos >= scan_end)
            { value += source.slice(start, pos); flags |= tf::Unterminated; error("Unterminated template literal.", pos, 0); token_value = value; return first ? sk::NoSubstitutionTemplateLiteral : sk::TemplateTail; }
            if (ch(pos) == '`') { value += source.slice(start, pos++); token_value = value; return first ? sk::NoSubstitutionTemplateLiteral : sk::TemplateTail; }
            if (ch(pos) == '$' && ch(pos + 1) == '{') { value += source.slice(start, pos); pos += 2; token_value = value; return first ? sk::TemplateHead : sk::TemplateMiddle; }
            if (ch(pos) == '\\') { value += source.slice(start, pos); value += escape(report); start = pos; }
            else if (ch(pos) == '\r') { value += source.slice(start, pos); ++pos; if (ch(pos) == '\n') ++pos; value += '\n'; start = pos; }
            else ++pos;
        }
    }
    std::string identifier_parts()
    {
        std::string result; auto start = pos;
        while (pos < scan_end)
        {
            int c = cp(pos);
            if (id_part(c)) pos += width(c);
            else if (c == '\\')
            {
                c = peek_escape(true);
                if (c >= 0 && id_part(c))
                {
                    // Preserve the 5.8.3 branch order, including its extended-escape
                    // accumulation behavior. Differences are guarded by oracle fixtures.
                    result += extended_escape(true); start = pos; continue;
                }
                c = peek_escape(false);
                if (c < 0 || !id_part(c)) break;
                flags |= tf::UnicodeEscape; result += source.slice(start, pos); result += codepoint(c); pos += 6; start = pos;
            }
            else break;
        }
        return result + source.slice(start, pos);
    }
    int identifier_kind() const
    {
        for (const auto& pair : keywordTokens) if (pair.first == token_value) return pair.second;
        return sk::Identifier;
    }
    bool matches(std::string_view pattern) const
    {
        if (pos + pattern.size() > scan_end) return false;
        for (size_t i = 0; i < pattern.size(); ++i) if (text[pos + i] != static_cast<unsigned char>(pattern[i])) return false;
        return true;
    }
    bool conflict_at(size_t at) const
    {
        if (at && !line_break(ch(at - 1))) return false;
        if (at + 7 >= scan_end) return false;
        for (size_t i = 1; i < 7; ++i) if (ch(at + i) != ch(at)) return false;
        return ch(at) == '=' || ch(at + 7) == ' ';
    }
    void scan_conflict()
    {
        error("Merge conflict marker encountered.", pos, 7);
        const auto first = ch(pos);
        if (first == '<' || first == '>')
            while (pos < scan_end && !line_break(ch(pos))) ++pos;
        else
            while (pos < scan_end)
            {
                const auto c = ch(pos);
                if ((c == '=' || c == '>') && c != first && conflict_at(pos)) break;
                ++pos;
            }
    }
    struct SavedState
    {
        size_t pos, start, full, end;
        int kind;
        unsigned flags;
        std::string value;
    };
    SavedState save() const { return {pos, token_start, full_start, scan_end, kind, flags, token_value}; }
    void restore(SavedState&& state)
    {
        pos = state.pos; token_start = state.start; full_start = state.full; scan_end = state.end;
        kind = state.kind; flags = state.flags; token_value = std::move(state.value);
    }
    struct StateGuard
    {
        Scanner& scanner;
        SavedState state;
        bool active = true;
        explicit StateGuard(Scanner& value) : scanner(value), state(value.save()) {}
        ~StateGuard() { if (active) scanner.restore(std::move(state)); }
    };
public:
    size_t pos = 0, token_start = 0, full_start = 0;
    int kind = sk::Unknown;
    unsigned flags = 0;
    std::string token_value;
    std::vector<ScanError> errors;
    Scanner(const mn::language::Text& value, bool skip = false, bool jsx = false)
        : source(value), text(value.utf16), skip_trivia(skip), scan_end(value.utf16.size()), jsx_mode(jsx) {}
    Token current() const { return {kind, token_start, pos, token_value, flags}; }
    Token scan()
    {
        full_start = pos;
        flags = 0;
        while (true)
        {
            token_start = pos;
            if (pos >= scan_end) { kind = sk::EndOfFileToken; return current(); }
            const int c = cp(pos);
            if (pos == 0 && matches("#!"))
            { while (pos < scan_end && !line_break(ch(pos))) ++pos; if (skip_trivia) continue; kind = sk::ShebangTrivia; return current(); }
            if (c == '\n' || c == '\r')
            {
                flags |= tf::PrecedingLineBreak; ++pos;
                if (skip_trivia) continue;
                if (c == '\r' && ch(pos) == '\n') ++pos;
                kind = sk::NewLineTrivia; return current();
            }
            if (c == 133) { ++pos; continue; } // Default-switch whitespace in upstream is skipped even without skipTrivia.
            if (space(c))
            { do { ++pos; } while (space(ch(pos))); if (skip_trivia) continue; kind = sk::WhitespaceTrivia; return current(); }
            if (c == 0x2028 || c == 0x2029) { flags |= tf::PrecedingLineBreak; ++pos; continue; }
            if (matches("//"))
            { pos += 2; while (pos < scan_end && !line_break(ch(pos))) ++pos; if (skip_trivia) continue; kind = sk::SingleLineCommentTrivia; return current(); }
            if (matches("/*"))
            {
                pos += 2; const bool jsdoc = ch(pos) == '*' && ch(pos + 1) != '/'; bool closed = false;
                while (pos < scan_end)
                {
                    if (ch(pos) == '*' && ch(pos + 1) == '/') { pos += 2; closed = true; break; }
                    if (line_break(ch(pos))) flags |= tf::PrecedingLineBreak;
                    ++pos;
                }
                if (jsdoc) flags |= tf::PrecedingJSDocComment;
                if (!closed) { error("'*/' expected.", pos, 0); if (!skip_trivia) flags |= tf::Unterminated; }
                if (skip_trivia) continue;
                kind = sk::MultiLineCommentTrivia; return current();
            }
            if (c == '\'' || c == '"') { token_value = scan_string(); kind = sk::StringLiteral; return current(); }
            if (c == '`') { kind = template_token(false); return current(); }
            if (digit(c) || (c == '.' && digit(ch(pos + 1)))) { kind = scan_number(); return current(); }
            if (id_start(c))
            {
                pos += width(c); while (id_part(cp(pos))) pos += width(cp(pos));
                token_value = source.slice(token_start, pos);
                if (ch(pos) == '\\') token_value += identifier_parts();
                kind = identifier_kind(); return current();
            }
            if (c == '\\' || c == '#')
            {
                const bool isPrivate = c == '#';
                if (isPrivate)
                {
                    if (ch(pos + 1) == '!') { error("'#!' can only be used at the start of a file.", pos, 2); ++pos; kind = sk::Unknown; return current(); }
                    ++pos;
                }
                int cooked = peek_escape(true);
                if (id_start(cooked)) { token_value = (isPrivate ? "#" : "") + extended_escape(true) + identifier_parts(); kind = isPrivate ? sk::PrivateIdentifier : identifier_kind(); return current(); }
                cooked = peek_escape(false);
                if (id_start(cooked)) { flags |= tf::UnicodeEscape; pos += 6; token_value = (isPrivate ? "#" : "") + codepoint(cooked) + identifier_parts(); kind = isPrivate ? sk::PrivateIdentifier : identifier_kind(); return current(); }
                if (isPrivate && id_start(cp(pos))) { token_value = "#" + identifier_parts(); kind = sk::PrivateIdentifier; return current(); }
                error("Invalid character.", token_start);
                if (!isPrivate) ++pos;
                token_value = isPrivate ? "#" : token_value; kind = isPrivate ? sk::PrivateIdentifier : sk::Unknown; return current();
            }
            if (c == 0xfffd) { error("File appears to be binary.", 0, 0); pos = scan_end; kind = sk::NonTextFileMarkerTrivia; return current(); }
            if ((c == '<' || c == '>' || c == '=' || c == '|') && conflict_at(pos))
            {
                scan_conflict();
                if (skip_trivia) continue;
                kind = sk::ConflictMarkerTrivia; return current();
            }
            if (jsx_mode && c == '<' && ch(pos + 1) == '/' && ch(pos + 2) != '*')
            { pos += 2; kind = sk::LessThanSlashToken; return current(); }
            // A greater-than is scanned alone; the parser opts into shift/relational rescanning.
            if (c == '>') { ++pos; kind = sk::GreaterThanToken; return current(); }
            bool found = false;
            for (const auto& pair : punctuationTokens)
            {
                if (pair.first == "`" || pair.first == "#" || pair.first == "</") continue;
                if (pair.first == "?." && digit(ch(pos + 2))) continue;
                if (matches(pair.first)) { pos += pair.first.size(); kind = pair.second; found = true; break; }
            }
            if (!found) { error("Invalid character.", pos, width(c)); pos += width(c); kind = sk::Unknown; }
            return current();
        }
    }
    Token rescan_template(bool tagged = false) { pos = token_start; kind = template_token(!tagged); return current(); }
    Token rescan_greater()
    {
        if (kind != sk::GreaterThanToken) return current();
        pos = token_start;
        for (const auto& pair : punctuationTokens)
            if (pair.first[0] == '>' && matches(pair.first)) { pos += pair.first.size(); kind = pair.second; return current(); }
        ++pos; return current();
    }
    Token rescan_slash()
    {
        if (kind != sk::SlashToken && kind != sk::SlashEqualsToken) return current();
        pos = token_start + 1; bool escaped = false, inClass = false;
        while (pos < scan_end && !line_break(ch(pos)))
        {
            int c = ch(pos);
            if (escaped) escaped = false;
            else if (c == '/' && !inClass) break;
            else if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '\\') escaped = true;
            ++pos;
        }
        if (ch(pos) != '/')
        {
            flags |= tf::Unterminated;
            const auto bodyEnd = pos;
            pos = token_start + 1; escaped = false;
            size_t classDepth = 0, groupDepth = 0; bool inQuantifier = false;
            while (pos < bodyEnd)
            {
                const auto c = ch(pos);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '[') ++classDepth;
                else if (c == ']' && classDepth) --classDepth;
                else if (!classDepth)
                {
                    if (c == '{') inQuantifier = true;
                    else if (c == '}' && inQuantifier) inQuantifier = false;
                    else if (!inQuantifier)
                    {
                        if (c == '(') ++groupDepth;
                        else if (c == ')' && groupDepth) --groupDepth;
                        else if (c == ')' || c == ']' || c == '}') break;
                    }
                }
                ++pos;
            }
            while (pos > token_start + 1 && (space(ch(pos - 1)) || line_break(ch(pos - 1)) || ch(pos - 1) == ';')) --pos;
            error("Unterminated regular expression literal.", token_start, pos - token_start);
        }
        else { ++pos; while (id_part(cp(pos))) pos += width(cp(pos)); }
        token_value = source.slice(token_start, pos); kind = sk::RegularExpressionLiteral; return current();
    }
    // C++ guard restores cursor state even if the callback throws (host safety extension).
    // Diagnostics are not rewound: upstream reports them through an external callback.
    template<class Callback> decltype(auto) look_ahead(Callback&& callback)
    {
        StateGuard guard(*this);
        return std::forward<Callback>(callback)();
    }
    template<class Callback> auto try_scan(Callback&& callback)
    {
        StateGuard guard(*this);
        auto result = std::forward<Callback>(callback)();
        if (static_cast<bool>(result)) guard.active = false;
        return result;
    }
    template<class Callback> decltype(auto) scan_range(size_t start, size_t length, Callback&& callback)
    {
        if (start > text.size() || length > text.size() - start) throw std::out_of_range("Invalid scanner range.");
        StateGuard guard(*this);
        scan_end = start + length;
        reset_token_state(start);
        return std::forward<Callback>(callback)();
    }
    void reset_token_state(size_t at)
    {
        if (at > scan_end) throw std::out_of_range("Invalid scanner offset.");
        pos = token_start = full_start = at; kind = sk::Unknown; token_value.clear(); flags = 0;
    }
    Token rescan_asterisk_equals()
    {
        if (kind != sk::AsteriskEqualsToken) throw std::logic_error("Expected *= before rescanning.");
        pos = token_start + 1; kind = sk::EqualsToken; return current();
    }
    Token rescan_less_than()
    {
        if (kind == sk::LessThanLessThanToken) { pos = token_start + 1; kind = sk::LessThanToken; }
        return current();
    }
    Token rescan_hash()
    {
        if (kind == sk::PrivateIdentifier) { pos = token_start + 1; kind = sk::HashToken; }
        return current();
    }
    Token rescan_question()
    {
        if (kind != sk::QuestionQuestionToken) throw std::logic_error("Expected ?? before rescanning.");
        pos = token_start + 1; kind = sk::QuestionToken; return current();
    }
    Token scan_jsx_token(bool multiline = true)
    {
        full_start = token_start = pos;
        if (pos >= scan_end) { kind = sk::EndOfFileToken; return current(); }
        if (ch(pos) == '<')
        {
            if (ch(pos + 1) == '/') { pos += 2; kind = sk::LessThanSlashToken; }
            else { ++pos; kind = sk::LessThanToken; }
            return current();
        }
        if (ch(pos) == '{') { ++pos; kind = sk::OpenBraceToken; return current(); }
        // The sentinel is intentionally an absolute offset, including upstream's first-line behavior.
        std::ptrdiff_t firstNonWhitespace = 0;
        while (pos < scan_end)
        {
            const auto c = ch(pos);
            if (c == '{') break;
            if (c == '<')
            {
                if (conflict_at(pos)) { scan_conflict(); kind = sk::ConflictMarkerTrivia; return current(); }
                break;
            }
            if (c == '>') error("Unexpected token. Did you mean `{'>'}` or `&gt;`?", pos);
            if (c == '}') error("Unexpected token. Did you mean `{'}'}` or `&rbrace;`?", pos);
            if (line_break(c) && firstNonWhitespace == 0) firstNonWhitespace = -1;
            else if (!multiline && line_break(c) && firstNonWhitespace > 0) break;
            else if (!space(c) && !line_break(c)) firstNonWhitespace = static_cast<std::ptrdiff_t>(pos);
            ++pos;
        }
        token_value = source.slice(full_start, pos);
        auto value = current();
        value.kind = firstNonWhitespace == -1 ? sk::JsxTextAllWhiteSpaces : sk::JsxText;
        return value;
    }
    Token rescan_jsx_token(bool multiline = true)
    { pos = token_start = full_start; auto value = scan_jsx_token(multiline); kind = value.kind; return value; }
    Token scan_jsx_identifier()
    {
        if (kind >= sk::Identifier)
        {
            while (pos < scan_end)
            {
                if (ch(pos) == '-') { token_value += '-'; ++pos; continue; }
                const auto old = pos; token_value += identifier_parts();
                if (pos == old) break;
            }
            // getIdentifierToken updates the stored token as well as the return value.
            kind = identifier_kind(); return current();
        }
        return current();
    }
    Token scan_jsx_attribute_value()
    {
        full_start = pos;
        if (ch(pos) == '"' || ch(pos) == '\'') { token_value = scan_string(true); kind = sk::StringLiteral; return current(); }
        return scan();
    }
    Token rescan_jsx_attribute_value()
    { pos = token_start = full_start; return scan_jsx_attribute_value(); }
    Token scan_jsdoc_token()
    {
        full_start = token_start = pos; flags = 0;
        if (pos >= scan_end) { kind = sk::EndOfFileToken; return current(); }
        const auto c = cp(pos); pos += width(c);
        switch (c)
        {
        case '\t': case '\v': case '\f': case ' ':
            while (pos < scan_end && space(ch(pos))) ++pos;
            kind = sk::WhitespaceTrivia; return current();
        case '\r':
            if (ch(pos) == '\n') ++pos;
            [[fallthrough]];
        case '\n': flags |= tf::PrecedingLineBreak; kind = sk::NewLineTrivia; return current();
        case '@': kind = sk::AtToken; return current();
        case '*': kind = sk::AsteriskToken; return current();
        case '{': kind = sk::OpenBraceToken; return current();
        case '}': kind = sk::CloseBraceToken; return current();
        case '[': kind = sk::OpenBracketToken; return current();
        case ']': kind = sk::CloseBracketToken; return current();
        case '(': kind = sk::OpenParenToken; return current();
        case ')': kind = sk::CloseParenToken; return current();
        case '<': kind = sk::LessThanToken; return current();
        case '>': kind = sk::GreaterThanToken; return current();
        case '=': kind = sk::EqualsToken; return current();
        case ',': kind = sk::CommaToken; return current();
        case '.': kind = sk::DotToken; return current();
        case '`': kind = sk::BacktickToken; return current();
        case '#': kind = sk::HashToken; return current();
        case '\\':
        {
            --pos;
            auto cooked = peek_escape(true);
            if (id_start(cooked)) { token_value = extended_escape(true) + identifier_parts(); kind = identifier_kind(); return current(); }
            cooked = peek_escape(false);
            if (id_start(cooked)) { pos += 6; flags |= tf::UnicodeEscape; token_value = codepoint(cooked) + identifier_parts(); kind = identifier_kind(); return current(); }
            ++pos; kind = sk::Unknown; return current();
        }
        }
        if (id_start(c))
        {
            // Intentional bounded-loop fix: old upstream precedence can loop forever at a trailing '-'.
            while (pos < scan_end && (id_part(cp(pos)) || cp(pos) == '-')) pos += width(cp(pos));
            token_value = source.slice(token_start, pos);
            if (ch(pos) == '\\') token_value += identifier_parts();
            kind = identifier_kind(); return current();
        }
        kind = sk::Unknown; return current();
    }
    Token scan_jsdoc_comment_text(bool in_backticks = false)
    {
        full_start = token_start = pos; flags = 0;
        if (pos >= scan_end) { kind = sk::EndOfFileToken; return current(); }
        for (; pos < scan_end && !line_break(ch(pos)) && ch(pos) != '`'; ++pos)
        {
            if (!in_backticks)
            {
                if (ch(pos) == '{') break;
                if (ch(pos) == '@' && pos && space(ch(pos - 1)) &&
                    !(pos + 1 < scan_end && (space(ch(pos + 1)) || line_break(ch(pos + 1))))) break;
            }
        }
        if (pos == token_start) return scan_jsdoc_token();
        token_value = source.slice(token_start, pos); kind = sk::JSDocCommentTextToken; return current();
    }

};
}
