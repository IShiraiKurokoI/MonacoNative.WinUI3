#include "MonacoNative/Core/UnicodeSegmentation.h"

#include <algorithm>
#include <array>
#include <cstdint>

namespace MonacoNative::Core::Unicode
{
    namespace
    {
        struct CodePoint final
        {
            char32_t value{};
            std::size_t length{1};
        };

        constexpr bool In(char32_t cp, char32_t first, char32_t last) noexcept
        {
            return cp >= first && cp <= last;
        }

        bool IsHighSurrogate(char16_t value) noexcept { return value >= 0xD800 && value <= 0xDBFF; }
        bool IsLowSurrogate(char16_t value) noexcept { return value >= 0xDC00 && value <= 0xDFFF; }

        CodePoint DecodeAt(std::u16string_view text, std::size_t offset) noexcept
        {
            if (offset >= text.size()) return {};
            const auto first = text[offset];
            if (IsHighSurrogate(first) && offset + 1 < text.size() && IsLowSurrogate(text[offset + 1]))
            {
                const auto high = static_cast<char32_t>(first - 0xD800);
                const auto low = static_cast<char32_t>(text[offset + 1] - 0xDC00);
                return CodePoint{0x10000 + ((high << 10U) | low), 2};
            }
            return CodePoint{static_cast<char32_t>(first), 1};
        }

        std::size_t PreviousCodePointStart(std::u16string_view text, std::size_t offset) noexcept
        {
            if (offset == 0) return 0;
            auto result = std::min(offset, text.size()) - 1;
            if (result > 0 && IsLowSurrogate(text[result]) && IsHighSurrogate(text[result - 1])) --result;
            return result;
        }

        enum class GraphemeClass : std::uint8_t
        {
            Other, CR, LF, Control, Extend, ZWJ, RegionalIndicator, Prepend, SpacingMark,
            L, V, T, LV, LVT, ExtendedPictographic, IndicConsonant, IndicLinker
        };

        bool IsControl(char32_t cp) noexcept
        {
            return In(cp, 0x0000, 0x001F) || In(cp, 0x007F, 0x009F) || cp == 0x00AD ||
                cp == 0x061C || cp == 0x180E || In(cp, 0x200B, 0x200F) ||
                In(cp, 0x2028, 0x202E) || In(cp, 0x2060, 0x206F) || cp == 0xFEFF;
        }

        bool IsExtend(char32_t cp) noexcept
        {
            // Mark ranges plus variation selectors, emoji modifiers, enclosing keycap and tag chars.
            return In(cp, 0x0300, 0x036F) || In(cp, 0x0483, 0x0489) || In(cp, 0x0591, 0x05BD) || cp == 0x05BF ||
                In(cp, 0x05C1, 0x05C2) || In(cp, 0x05C4, 0x05C5) || cp == 0x05C7 ||
                In(cp, 0x0610, 0x061A) || In(cp, 0x064B, 0x065F) || cp == 0x0670 || In(cp, 0x06D6, 0x06ED) ||
                In(cp, 0x0711, 0x0711) || In(cp, 0x0730, 0x074A) || In(cp, 0x07A6, 0x07B0) ||
                In(cp, 0x07EB, 0x07F3) || In(cp, 0x0816, 0x082D) || In(cp, 0x0859, 0x085B) ||
                In(cp, 0x08D3, 0x0902) || In(cp, 0x093A, 0x093C) || cp == 0x094D || In(cp, 0x0951, 0x0957) ||
                In(cp, 0x0962, 0x0963) || In(cp, 0x0981, 0x0981) || cp == 0x09BC || cp == 0x09CD ||
                In(cp, 0x09E2, 0x09E3) || cp == 0x0A01 || cp == 0x0A02 || cp == 0x0A3C || cp == 0x0A4D ||
                In(cp, 0x0A51, 0x0A51) || In(cp, 0x0A70, 0x0A71) || cp == 0x0A75 ||
                In(cp, 0x0A81, 0x0A82) || cp == 0x0ABC || cp == 0x0ACD || In(cp, 0x0AE2, 0x0AE3) ||
                In(cp, 0x0B01, 0x0B01) || cp == 0x0B3C || cp == 0x0B4D || In(cp, 0x0B56, 0x0B56) ||
                In(cp, 0x0B62, 0x0B63) || cp == 0x0BCD || In(cp, 0x0C00, 0x0C00) || cp == 0x0C04 || cp == 0x0C3C ||
                In(cp, 0x0C3E, 0x0C40) || In(cp, 0x0C46, 0x0C48) || In(cp, 0x0C4A, 0x0C4D) || In(cp, 0x0C55, 0x0C56) ||
                In(cp, 0x0C62, 0x0C63) || cp == 0x0D00 || cp == 0x0D01 || In(cp, 0x0D3B, 0x0D3C) || cp == 0x0D4D ||
                In(cp, 0x0D62, 0x0D63) || cp == 0x0DCA || In(cp, 0x0DD2, 0x0DD4) || cp == 0x0DD6 ||
                cp == 0x0E31 || In(cp, 0x0E34, 0x0E3A) || In(cp, 0x0E47, 0x0E4E) || cp == 0x0EB1 ||
                In(cp, 0x0EB4, 0x0EBC) || In(cp, 0x0EC8, 0x0ECE) || In(cp, 0x0F18, 0x0F19) || cp == 0x0F35 ||
                cp == 0x0F37 || cp == 0x0F39 || In(cp, 0x0F71, 0x0F84) || In(cp, 0x0F86, 0x0F87) ||
                In(cp, 0x0F8D, 0x0FBC) || cp == 0x0FC6 || In(cp, 0x102D, 0x1030) || In(cp, 0x1032, 0x1037) ||
                In(cp, 0x1039, 0x103A) || In(cp, 0x1058, 0x1059) || In(cp, 0x105E, 0x1060) || In(cp, 0x1071, 0x1074) ||
                cp == 0x1082 || In(cp, 0x1085, 0x1086) || cp == 0x108D || cp == 0x109D ||
                In(cp, 0x135D, 0x135F) || In(cp, 0x1712, 0x1715) || In(cp, 0x1732, 0x1734) ||
                In(cp, 0x1752, 0x1753) || In(cp, 0x1772, 0x1773) || In(cp, 0x17B4, 0x17B5) || In(cp, 0x17B7, 0x17BD) ||
                cp == 0x17C6 || In(cp, 0x17C9, 0x17D3) || cp == 0x17DD || In(cp, 0x180B, 0x180D) || cp == 0x180F ||
                cp == 0x1885 || cp == 0x1886 || cp == 0x18A9 || In(cp, 0x1920, 0x1922) || In(cp, 0x1927, 0x1928) ||
                cp == 0x1932 || In(cp, 0x1939, 0x193B) || In(cp, 0x1A17, 0x1A18) || cp == 0x1A1B || cp == 0x1A56 ||
                In(cp, 0x1A58, 0x1A5E) || cp == 0x1A60 || cp == 0x1A62 || In(cp, 0x1A65, 0x1A6C) || In(cp, 0x1A73, 0x1A7C) ||
                cp == 0x1A7F || In(cp, 0x1AB0, 0x1ACE) || In(cp, 0x1B00, 0x1B03) || cp == 0x1B34 || cp == 0x1B36 ||
                In(cp, 0x1B38, 0x1B3A) || cp == 0x1B3C || cp == 0x1B42 || In(cp, 0x1B6B, 0x1B73) ||
                In(cp, 0x1B80, 0x1B81) || In(cp, 0x1BA2, 0x1BA5) || In(cp, 0x1BA8, 0x1BA9) || In(cp, 0x1BAB, 0x1BAD) ||
                cp == 0x1BE6 || In(cp, 0x1BE8, 0x1BE9) || cp == 0x1BED || In(cp, 0x1BEF, 0x1BF1) ||
                In(cp, 0x1C2C, 0x1C33) || In(cp, 0x1C36, 0x1C37) || In(cp, 0x1CD0, 0x1CD2) || In(cp, 0x1CD4, 0x1CE0) ||
                In(cp, 0x1CE2, 0x1CE8) || cp == 0x1CED || cp == 0x1CF4 || In(cp, 0x1CF8, 0x1CF9) ||
                In(cp, 0x1DC0, 0x1DFF) || In(cp, 0x20D0, 0x20F0) || In(cp, 0x2CEF, 0x2CF1) ||
                cp == 0x2D7F || In(cp, 0x2DE0, 0x2DFF) || In(cp, 0x302A, 0x302F) || cp == 0x3099 || cp == 0x309A ||
                In(cp, 0xA66F, 0xA672) || In(cp, 0xA674, 0xA67D) || In(cp, 0xA69E, 0xA69F) ||
                In(cp, 0xA6F0, 0xA6F1) || cp == 0xA802 || cp == 0xA806 || cp == 0xA80B || In(cp, 0xA825, 0xA826) ||
                cp == 0xA82C || In(cp, 0xA8C4, 0xA8C5) || In(cp, 0xA8E0, 0xA8F1) || In(cp, 0xA926, 0xA92D) ||
                In(cp, 0xA947, 0xA951) || In(cp, 0xA980, 0xA982) || cp == 0xA9B3 || In(cp, 0xA9B6, 0xA9B9) ||
                cp == 0xA9BC || cp == 0xA9E5 || In(cp, 0xAA29, 0xAA2E) || In(cp, 0xAA31, 0xAA32) || In(cp, 0xAA35, 0xAA36) ||
                cp == 0xAA43 || cp == 0xAA4C || cp == 0xAA7C || cp == 0xAAB0 || In(cp, 0xAAB2, 0xAAB4) ||
                In(cp, 0xAAB7, 0xAAB8) || In(cp, 0xAABE, 0xAABF) || cp == 0xAAC1 || In(cp, 0xAAEC, 0xAAED) || cp == 0xAAF6 ||
                cp == 0xABE5 || cp == 0xABE8 || cp == 0xABED || cp == 0xFB1E || In(cp, 0xFE00, 0xFE0F) ||
                In(cp, 0xFE20, 0xFE2F) || In(cp, 0x101FD, 0x101FD) || In(cp, 0x102E0, 0x102E0) ||
                In(cp, 0x10376, 0x1037A) || In(cp, 0x10A01, 0x10A03) || In(cp, 0x10A05, 0x10A06) ||
                In(cp, 0x10A0C, 0x10A0F) || In(cp, 0x10A38, 0x10A3A) || cp == 0x10A3F || In(cp, 0x10AE5, 0x10AE6) ||
                In(cp, 0x10D24, 0x10D27) || In(cp, 0x10EAB, 0x10EAC) || In(cp, 0x10F46, 0x10F50) ||
                In(cp, 0x11001, 0x11001) || In(cp, 0x11038, 0x11046) || In(cp, 0x11070, 0x11070) || In(cp, 0x11073, 0x11074) ||
                In(cp, 0x1107F, 0x11081) || In(cp, 0x110B3, 0x110B6) || In(cp, 0x110B9, 0x110BA) ||
                In(cp, 0x11100, 0x11102) || In(cp, 0x11127, 0x1112B) || In(cp, 0x1112D, 0x11134) || cp == 0x11173 ||
                In(cp, 0x11180, 0x11181) || In(cp, 0x111B6, 0x111BE) || cp == 0x111C9 || In(cp, 0x111CA, 0x111CC) ||
                In(cp, 0x1122F, 0x11231) || cp == 0x11234 || In(cp, 0x11236, 0x11237) || cp == 0x1123E || cp == 0x11241 ||
                cp == 0x112DF || In(cp, 0x112E3, 0x112EA) || In(cp, 0x11300, 0x11301) || cp == 0x1133B || cp == 0x1133C ||
                cp == 0x11340 || cp == 0x11366 || In(cp, 0x11370, 0x11374) || In(cp, 0x11438, 0x1143F) ||
                In(cp, 0x11442, 0x11444) || cp == 0x11446 || cp == 0x1145E || In(cp, 0x114B3, 0x114B8) || cp == 0x114BA ||
                In(cp, 0x114BF, 0x114C0) || In(cp, 0x114C2, 0x114C3) || In(cp, 0x115B2, 0x115B5) || In(cp, 0x115BC, 0x115BD) ||
                cp == 0x115BF || cp == 0x115C0 || In(cp, 0x115DC, 0x115DD) || In(cp, 0x11633, 0x1163A) || cp == 0x1163D ||
                cp == 0x1163F || cp == 0x11640 || cp == 0x116AB || cp == 0x116AD || In(cp, 0x116B0, 0x116B5) || cp == 0x116B7 ||
                In(cp, 0x1171D, 0x1171F) || In(cp, 0x11722, 0x11725) || In(cp, 0x11727, 0x1172B) ||
                In(cp, 0x1182F, 0x11837) || cp == 0x11839 || cp == 0x1183A || In(cp, 0x1193B, 0x1193C) || cp == 0x1193E ||
                cp == 0x11943 || In(cp, 0x119D4, 0x119D7) || In(cp, 0x119DA, 0x119DB) || cp == 0x119E0 ||
                In(cp, 0x11A01, 0x11A0A) || In(cp, 0x11A33, 0x11A38) || In(cp, 0x11A3B, 0x11A3E) || cp == 0x11A47 ||
                In(cp, 0x11A51, 0x11A56) || In(cp, 0x11A59, 0x11A5B) || cp == 0x11A8A || In(cp, 0x11A8B, 0x11A96) ||
                In(cp, 0x11A98, 0x11A99) || In(cp, 0x11C30, 0x11C36) || In(cp, 0x11C38, 0x11C3D) || cp == 0x11C3F ||
                In(cp, 0x11C92, 0x11CA7) || In(cp, 0x11CAA, 0x11CB0) || In(cp, 0x11CB2, 0x11CB3) || In(cp, 0x11CB5, 0x11CB6) ||
                In(cp, 0x11D31, 0x11D36) || cp == 0x11D3A || In(cp, 0x11D3C, 0x11D3D) || cp == 0x11D3F || cp == 0x11D40 ||
                cp == 0x11D42 || cp == 0x11D44 || cp == 0x11D45 || cp == 0x11D47 || In(cp, 0x11D90, 0x11D91) || cp == 0x11D95 ||
                cp == 0x11D97 || In(cp, 0x11EF3, 0x11EF4) || In(cp, 0x13440, 0x13440) || In(cp, 0x13447, 0x13455) ||
                In(cp, 0x16AF0, 0x16AF4) || In(cp, 0x16B30, 0x16B36) || In(cp, 0x16F4F, 0x16F4F) || In(cp, 0x16F8F, 0x16F92) ||
                In(cp, 0x1BC9D, 0x1BC9E) || In(cp, 0x1CF00, 0x1CF2D) || In(cp, 0x1CF30, 0x1CF46) ||
                In(cp, 0x1D167, 0x1D169) || In(cp, 0x1D17B, 0x1D182) || In(cp, 0x1D185, 0x1D18B) || In(cp, 0x1D1AA, 0x1D1AD) ||
                In(cp, 0x1D242, 0x1D244) || In(cp, 0x1DA00, 0x1DA36) || In(cp, 0x1DA3B, 0x1DA6C) || cp == 0x1DA75 ||
                cp == 0x1DA84 || In(cp, 0x1DA9B, 0x1DA9F) || In(cp, 0x1DAA1, 0x1DAAF) || In(cp, 0x1E000, 0x1E02A) ||
                In(cp, 0x1E08F, 0x1E08F) || In(cp, 0x1E130, 0x1E136) || cp == 0x1E2AE || In(cp, 0x1E2EC, 0x1E2EF) ||
                In(cp, 0x1E4EC, 0x1E4EF) || In(cp, 0x1E8D0, 0x1E8D6) || In(cp, 0x1E944, 0x1E94A) ||
                In(cp, 0xE0100, 0xE01EF) || In(cp, 0x1F3FB, 0x1F3FF) || cp == 0x20E3 || In(cp, 0xE0020, 0xE007F);
        }

        bool IsSpacingMark(char32_t cp) noexcept
        {
            return cp == 0x0903 || cp == 0x093B || In(cp, 0x093E, 0x0940) || In(cp, 0x0949, 0x094C) ||
                In(cp, 0x0982, 0x0983) || In(cp, 0x09BE, 0x09C0) || In(cp, 0x09C7, 0x09C8) || In(cp, 0x09CB, 0x09CC) ||
                cp == 0x0A03 || In(cp, 0x0A3E, 0x0A40) || In(cp, 0x0A83, 0x0A83) || In(cp, 0x0ABE, 0x0AC0) ||
                cp == 0x0AC9 || In(cp, 0x0ACB, 0x0ACC) || In(cp, 0x0B02, 0x0B03) || cp == 0x0B3E || cp == 0x0B40 ||
                In(cp, 0x0B47, 0x0B48) || In(cp, 0x0B4B, 0x0B4C) || In(cp, 0x0BBE, 0x0BBF) || In(cp, 0x0BC1, 0x0BC2) ||
                In(cp, 0x0BC6, 0x0BC8) || In(cp, 0x0BCA, 0x0BCC) || In(cp, 0x0C01, 0x0C03) || In(cp, 0x0C41, 0x0C44) ||
                In(cp, 0x0C82, 0x0C83) || cp == 0x0CBE || In(cp, 0x0CC0, 0x0CC4) || In(cp, 0x0CC7, 0x0CC8) ||
                In(cp, 0x0CCA, 0x0CCB) || In(cp, 0x0D02, 0x0D03) || In(cp, 0x0D3E, 0x0D40) || In(cp, 0x0D46, 0x0D48) ||
                In(cp, 0x0D4A, 0x0D4C) || In(cp, 0x0D82, 0x0D83) || In(cp, 0x0DCF, 0x0DD1) || In(cp, 0x0DD8, 0x0DDF) ||
                In(cp, 0x0DF2, 0x0DF3) || In(cp, 0x0F3E, 0x0F3F) || cp == 0x0F7F || In(cp, 0x102B, 0x102C) || cp == 0x1031 ||
                cp == 0x1038 || In(cp, 0x103B, 0x103C) || In(cp, 0x1056, 0x1057) || In(cp, 0x1062, 0x1064) ||
                In(cp, 0x1067, 0x106D) || cp == 0x1083 || cp == 0x1084 || In(cp, 0x1087, 0x108C) || cp == 0x108F ||
                In(cp, 0x109A, 0x109C) || In(cp, 0x17B6, 0x17B6) || In(cp, 0x17BE, 0x17C5) || In(cp, 0x17C7, 0x17C8) ||
                In(cp, 0x1923, 0x1926) || In(cp, 0x1929, 0x192B) || In(cp, 0x1930, 0x1931) || In(cp, 0x1933, 0x1938) ||
                In(cp, 0x19B0, 0x19C0) || In(cp, 0x19C8, 0x19C9) || In(cp, 0x1A19, 0x1A1A) || cp == 0x1A55 || cp == 0x1A57 ||
                cp == 0x1A61 || In(cp, 0x1A63, 0x1A64) || In(cp, 0x1A6D, 0x1A72) || cp == 0x1B04 || cp == 0x1B35 || cp == 0x1B3B ||
                In(cp, 0x1B3D, 0x1B41) || In(cp, 0x1B43, 0x1B44) || cp == 0x1B82 || cp == 0x1BA1 || In(cp, 0x1BA6, 0x1BA7) ||
                cp == 0x1BAA || cp == 0x1BE7 || In(cp, 0x1BEA, 0x1BEC) || cp == 0x1BEE || In(cp, 0x1BF2, 0x1BF3) ||
                In(cp, 0x1C24, 0x1C2B) || In(cp, 0x1C34, 0x1C35) || In(cp, 0xA823, 0xA824) || cp == 0xA827 ||
                In(cp, 0xA880, 0xA881) || In(cp, 0xA8B4, 0xA8C3) || In(cp, 0xA952, 0xA953) || cp == 0xA983 ||
                In(cp, 0xA9B4, 0xA9B5) || In(cp, 0xA9BA, 0xA9BB) || In(cp, 0xA9BD, 0xA9C0) || cp == 0xAA2F || cp == 0xAA30 ||
                In(cp, 0xAA33, 0xAA34) || cp == 0xAA4D || cp == 0xAA7B || In(cp, 0xAA7D, 0xAA7D) || cp == 0xAAEB ||
                In(cp, 0xAAEE, 0xAAEF) || cp == 0xAAF5 || In(cp, 0xABE3, 0xABE4) || In(cp, 0xABE6, 0xABE7) ||
                In(cp, 0xABE9, 0xABEA) || cp == 0xABEC || cp == 0x11000 || cp == 0x11002 || cp == 0x11082 ||
                In(cp, 0x110B0, 0x110B2) || In(cp, 0x110B7, 0x110B8) || cp == 0x1112C || cp == 0x11145 || cp == 0x11146 ||
                cp == 0x11182 || In(cp, 0x111B3, 0x111B5) || cp == 0x111BF || cp == 0x111C0 || In(cp, 0x1122C, 0x1122E) ||
                In(cp, 0x11232, 0x11233) || cp == 0x11235 || In(cp, 0x112E0, 0x112E2) || In(cp, 0x11302, 0x11303) ||
                In(cp, 0x1133E, 0x1133F) || In(cp, 0x11341, 0x11344) || In(cp, 0x11347, 0x11348) || In(cp, 0x1134B, 0x1134D) ||
                cp == 0x11357 || In(cp, 0x11362, 0x11363) || In(cp, 0x11435, 0x11437) || In(cp, 0x11440, 0x11441) || cp == 0x11445 ||
                In(cp, 0x114B0, 0x114B2) || cp == 0x114B9 || In(cp, 0x114BB, 0x114BE) || cp == 0x114C1 || In(cp, 0x115AF, 0x115B1) ||
                In(cp, 0x115B8, 0x115BB) || cp == 0x115BE || In(cp, 0x11630, 0x11632) || cp == 0x1163B || cp == 0x1163C ||
                cp == 0x1163E || cp == 0x116AC || cp == 0x116AE || cp == 0x116AF || cp == 0x116B6 || In(cp, 0x11720, 0x11721) ||
                cp == 0x11726 || In(cp, 0x1182C, 0x1182E) || cp == 0x11838 || cp == 0x11930 || In(cp, 0x11937, 0x11938) ||
                cp == 0x1193D || cp == 0x11940 || cp == 0x11942 || In(cp, 0x119D1, 0x119D3) || In(cp, 0x119DC, 0x119DF) ||
                cp == 0x119E4 || cp == 0x11A39 || In(cp, 0x11A57, 0x11A58) || cp == 0x11A97 || cp == 0x11C2F || cp == 0x11C3E ||
                cp == 0x11CA9 || cp == 0x11CB1 || cp == 0x11CB4 || In(cp, 0x11D8A, 0x11D8E) || In(cp, 0x11D93, 0x11D94) ||
                cp == 0x11D96 || In(cp, 0x11EF5, 0x11EF6);
        }

        bool IsPrepend(char32_t cp) noexcept
        {
            return In(cp, 0x0600, 0x0605) || cp == 0x06DD || cp == 0x070F || cp == 0x0890 || cp == 0x0891 ||
                cp == 0x08E2 || cp == 0x0D4E || cp == 0x110BD || cp == 0x110CD || In(cp, 0x111C2, 0x111C3) ||
                cp == 0x1193F || cp == 0x11941 || cp == 0x11A3A || In(cp, 0x11A84, 0x11A89) || cp == 0x11D46;
        }

        bool IsExtendedPictographic(char32_t cp) noexcept
        {
            // Broad stable ranges are intentional: unassigned slots inside emoji blocks are
            // harmless here and make the implementation resilient to newly assigned emoji.
            return In(cp, 0x00A9, 0x00A9) || In(cp, 0x00AE, 0x00AE) || In(cp, 0x203C, 0x3299) ||
                In(cp, 0x1F000, 0x1FAFF) || In(cp, 0x1FC00, 0x1FFFD);
        }

        bool IsIndicConsonant(char32_t cp) noexcept
        {
            return In(cp, 0x0915, 0x0939) || In(cp, 0x0958, 0x095F) || In(cp, 0x0995, 0x09B9) ||
                In(cp, 0x0A15, 0x0A39) || In(cp, 0x0A95, 0x0AB9) || In(cp, 0x0B15, 0x0B39) ||
                In(cp, 0x0B95, 0x0BB9) || In(cp, 0x0C15, 0x0C39) || In(cp, 0x0C95, 0x0CB9) ||
                In(cp, 0x0D15, 0x0D3A) || In(cp, 0x0D9A, 0x0DC6);
        }

        bool IsIndicLinker(char32_t cp) noexcept
        {
            return cp == 0x094D || cp == 0x09CD || cp == 0x0A4D || cp == 0x0ACD || cp == 0x0B4D ||
                cp == 0x0BCD || cp == 0x0C4D || cp == 0x0CCD || cp == 0x0D4D;
        }

        GraphemeClass ClassifyGrapheme(char32_t cp) noexcept
        {
            if (cp == 0x000D) return GraphemeClass::CR;
            if (cp == 0x000A) return GraphemeClass::LF;
            if (cp == 0x200D) return GraphemeClass::ZWJ;
            if (In(cp, 0x1F1E6, 0x1F1FF)) return GraphemeClass::RegionalIndicator;
            if (IsIndicLinker(cp)) return GraphemeClass::IndicLinker;
            if (IsExtend(cp)) return GraphemeClass::Extend;
            if (IsSpacingMark(cp)) return GraphemeClass::SpacingMark;
            if (IsPrepend(cp)) return GraphemeClass::Prepend;
            if (IsControl(cp)) return GraphemeClass::Control;
            if (In(cp, 0x1100, 0x115F) || In(cp, 0xA960, 0xA97C)) return GraphemeClass::L;
            if (In(cp, 0x1160, 0x11A7) || In(cp, 0xD7B0, 0xD7C6)) return GraphemeClass::V;
            if (In(cp, 0x11A8, 0x11FF) || In(cp, 0xD7CB, 0xD7FB)) return GraphemeClass::T;
            if (In(cp, 0xAC00, 0xD7A3)) return ((cp - 0xAC00) % 28 == 0) ? GraphemeClass::LV : GraphemeClass::LVT;
            if (IsIndicConsonant(cp)) return GraphemeClass::IndicConsonant;
            if (IsExtendedPictographic(cp)) return GraphemeClass::ExtendedPictographic;
            return GraphemeClass::Other;
        }

        bool IsHardBreak(GraphemeClass value) noexcept
        {
            return value == GraphemeClass::CR || value == GraphemeClass::LF || value == GraphemeClass::Control;
        }

        bool HasExtendedPictographicBeforeZwj(std::u16string_view text, std::size_t zwjStart) noexcept
        {
            auto cursor = zwjStart;
            while (cursor > 0)
            {
                const auto previous = PreviousCodePointStart(text, cursor);
                const auto cls = ClassifyGrapheme(DecodeAt(text, previous).value);
                if (cls == GraphemeClass::Extend) { cursor = previous; continue; }
                return cls == GraphemeClass::ExtendedPictographic;
            }
            return false;
        }

        bool IsIndicConjunctBoundarySuppressed(std::u16string_view text, std::size_t rightStart) noexcept
        {
            // Unicode 18 / UAX #29 rev. 49 GB9c:
            //     InCB=Linker InCB=Extend* x InCB=Consonant
            // Unlike Unicode 17, the rule no longer requires a preceding consonant. The
            // compact editor profile classifies the Indic viramas/linkers separately and
            // accepts ordinary Extend characters between the linker and consonant.
            if (ClassifyGrapheme(DecodeAt(text, rightStart).value) != GraphemeClass::IndicConsonant) return false;
            auto cursor = rightStart;
            while (cursor > 0)
            {
                const auto previous = PreviousCodePointStart(text, cursor);
                const auto cls = ClassifyGrapheme(DecodeAt(text, previous).value);
                if (cls == GraphemeClass::IndicLinker) return true;
                if (cls != GraphemeClass::Extend) return false;
                cursor = previous;
            }
            return false;
        }

        bool ShouldBreak(std::u16string_view text, std::size_t leftStart, std::size_t rightStart) noexcept
        {
            const auto left = ClassifyGrapheme(DecodeAt(text, leftStart).value);
            const auto right = ClassifyGrapheme(DecodeAt(text, rightStart).value);
            if (left == GraphemeClass::CR && right == GraphemeClass::LF) return false; // GB3
            if (IsHardBreak(left) || IsHardBreak(right)) return true;                 // GB4/5
            if (left == GraphemeClass::L && (right == GraphemeClass::L || right == GraphemeClass::V || right == GraphemeClass::LV || right == GraphemeClass::LVT)) return false; // GB6
            if ((left == GraphemeClass::LV || left == GraphemeClass::V) && (right == GraphemeClass::V || right == GraphemeClass::T)) return false; // GB7
            if ((left == GraphemeClass::LVT || left == GraphemeClass::T) && right == GraphemeClass::T) return false; // GB8
            if (right == GraphemeClass::Extend || right == GraphemeClass::ZWJ || right == GraphemeClass::IndicLinker) return false; // GB9
            if (right == GraphemeClass::SpacingMark) return false;                   // GB9a
            if (left == GraphemeClass::Prepend) return false;                        // GB9b
            if (IsIndicConjunctBoundarySuppressed(text, rightStart)) return false;   // GB9c (Unicode 18 profile)
            if (left == GraphemeClass::ZWJ && right == GraphemeClass::ExtendedPictographic && HasExtendedPictographicBeforeZwj(text, leftStart)) return false; // GB11
            if (left == GraphemeClass::RegionalIndicator && right == GraphemeClass::RegionalIndicator)
            {
                std::size_t count = 0;
                auto cursor = leftStart;
                while (true)
                {
                    if (ClassifyGrapheme(DecodeAt(text, cursor).value) != GraphemeClass::RegionalIndicator) break;
                    ++count;
                    if (cursor == 0) break;
                    const auto previous = PreviousCodePointStart(text, cursor);
                    if (ClassifyGrapheme(DecodeAt(text, previous).value) != GraphemeClass::RegionalIndicator) break;
                    cursor = previous;
                }
                return (count % 2U) == 0U; // GB12/13: pair RI symbols from the left.
            }
            return true;
        }

        enum class WordClass : std::uint8_t { Space, Newline, ALetter, Hebrew, Numeric, Katakana, ExtendNumLet, MidLetter, MidNum, Ideograph, Other };

        bool IsAsciiLetter(char32_t cp) noexcept { return (cp >= U'A' && cp <= U'Z') || (cp >= U'a' && cp <= U'z'); }
        bool IsDigit(char32_t cp) noexcept { return (cp >= U'0' && cp <= U'9') || In(cp, 0x0660, 0x0669) || In(cp, 0x06F0, 0x06F9) || In(cp, 0x0966, 0x096F); }
        bool IsIdeograph(char32_t cp) noexcept { return In(cp, 0x3400, 0x4DBF) || In(cp, 0x4E00, 0x9FFF) || In(cp, 0xF900, 0xFAFF) || In(cp, 0x20000, 0x3134F); }
        bool IsAlphabeticBroad(char32_t cp) noexcept
        {
            return IsAsciiLetter(cp) || In(cp, 0x00C0, 0x02AF) || In(cp, 0x0370, 0x052F) ||
                In(cp, 0x0531, 0x0588) || In(cp, 0x0600, 0x06FF) || In(cp, 0x0700, 0x08FF) ||
                In(cp, 0x0900, 0x1FFF) || In(cp, 0x2C00, 0x2DFF) || In(cp, 0xA640, 0xA7FF) ||
                In(cp, 0xAB30, 0xAB6F);
        }

        WordClass ClassifyWord(char32_t cp) noexcept
        {
            if (cp == U'\r' || cp == U'\n' || cp == 0x2028 || cp == 0x2029) return WordClass::Newline;
            if (cp == U' ' || cp == U'\t' || cp == 0x00A0 || cp == 0x1680 || In(cp, 0x2000, 0x200A) || cp == 0x202F || cp == 0x205F || cp == 0x3000) return WordClass::Space;
            if (IsIdeograph(cp) || In(cp, 0x3040, 0x309F)) return WordClass::Ideograph;
            if (In(cp, 0x30A0, 0x30FF) || In(cp, 0x31F0, 0x31FF) || In(cp, 0xFF66, 0xFF9D)) return WordClass::Katakana;
            if (In(cp, 0x0590, 0x05FF)) return WordClass::Hebrew;
            if (IsDigit(cp)) return WordClass::Numeric;
            if (cp == U'_' || cp == 0x203F || cp == 0x2040 || cp == 0x2054) return WordClass::ExtendNumLet;
            if (cp == U'\'' || cp == 0x2019 || cp == U'-' || cp == 0x00B7 || cp == 0x05F4) return WordClass::MidLetter;
            if (cp == U'.' || cp == U',' || cp == U':' || cp == U';' || cp == 0x066C) return WordClass::MidNum;
            if (IsAlphabeticBroad(cp)) return WordClass::ALetter;
            return WordClass::Other;
        }

        bool CompatibleWordClasses(WordClass left, WordClass right) noexcept
        {
            if (left == WordClass::Ideograph || right == WordClass::Ideograph) return false;
            if (left == WordClass::Katakana || right == WordClass::Katakana) return left == right || left == WordClass::ExtendNumLet || right == WordClass::ExtendNumLet;
            if (left == WordClass::Numeric || right == WordClass::Numeric)
                return (left == WordClass::Numeric && right == WordClass::Numeric) ||
                    ((left == WordClass::Numeric && (right == WordClass::ALetter || right == WordClass::Hebrew || right == WordClass::ExtendNumLet)) ||
                     (right == WordClass::Numeric && (left == WordClass::ALetter || left == WordClass::Hebrew || left == WordClass::ExtendNumLet)));
            if ((left == WordClass::ALetter || left == WordClass::Hebrew || left == WordClass::ExtendNumLet) &&
                (right == WordClass::ALetter || right == WordClass::Hebrew || right == WordClass::ExtendNumLet)) return true;
            return false;
        }

        WordClass ClusterWordClass(std::u16string_view text, std::size_t offset) noexcept
        {
            if (offset >= text.size()) return WordClass::Other;
            return ClassifyWord(DecodeAt(text, offset).value);
        }
    }

    bool IsGraphemeBoundary(std::u16string_view text, std::size_t offset) noexcept
    {
        offset = std::min(offset, text.size());
        if (offset == 0 || offset == text.size()) return true;
        const auto left = PreviousCodePointStart(text, offset);
        return ShouldBreak(text, left, offset);
    }

    std::size_t NextGraphemeBoundary(std::u16string_view text, std::size_t offset) noexcept
    {
        offset = std::min(offset, text.size());
        if (offset >= text.size()) return text.size();
        auto left = offset;
        auto next = offset + DecodeAt(text, offset).length;
        while (next < text.size() && !ShouldBreak(text, left, next))
        {
            left = next;
            next += DecodeAt(text, next).length;
        }
        return next;
    }

    std::size_t PreviousGraphemeBoundary(std::u16string_view text, std::size_t offset) noexcept
    {
        offset = std::min(offset, text.size());
        if (offset == 0) return 0;
        auto current = PreviousCodePointStart(text, offset);
        while (current > 0)
        {
            const auto left = PreviousCodePointStart(text, current);
            if (ShouldBreak(text, left, current)) break;
            current = left;
        }
        return current;
    }

    std::size_t NextWordBoundary(std::u16string_view text, std::size_t offset) noexcept
    {
        offset = std::min(offset, text.size());
        if (offset >= text.size()) return text.size();

        const auto skipWhitespace = [&](std::size_t cursor) noexcept
        {
            while (cursor < text.size())
            {
                const auto cls = ClusterWordClass(text, cursor);
                if (cls != WordClass::Space && cls != WordClass::Newline) break;
                cursor = NextGraphemeBoundary(text, cursor);
            }
            return cursor;
        };

        const auto firstClass = ClusterWordClass(text, offset);
        if (firstClass == WordClass::Space || firstClass == WordClass::Newline)
            return skipWhitespace(offset);
        if (firstClass == WordClass::Ideograph)
            return skipWhitespace(NextGraphemeBoundary(text, offset));
        if (firstClass == WordClass::Other || firstClass == WordClass::MidLetter || firstClass == WordClass::MidNum)
        {
            auto cursor = offset;
            while (cursor < text.size() && ClusterWordClass(text, cursor) == firstClass)
                cursor = NextGraphemeBoundary(text, cursor);
            return skipWhitespace(cursor);
        }

        auto previousClass = firstClass;
        offset = NextGraphemeBoundary(text, offset);
        while (offset < text.size())
        {
            const auto cls = ClusterWordClass(text, offset);
            if (CompatibleWordClasses(previousClass, cls))
            {
                previousClass = cls;
                offset = NextGraphemeBoundary(text, offset);
                continue;
            }

            if (cls == WordClass::MidLetter || cls == WordClass::MidNum)
            {
                const auto punctuationStart = offset;
                const auto afterPunctuation = NextGraphemeBoundary(text, punctuationStart);
                if (afterPunctuation < text.size())
                {
                    const auto afterClass = ClusterWordClass(text, afterPunctuation);
                    const bool joinsLetters = cls == WordClass::MidLetter &&
                        (previousClass == WordClass::ALetter || previousClass == WordClass::Hebrew) &&
                        (afterClass == WordClass::ALetter || afterClass == WordClass::Hebrew);
                    const bool joinsNumbers = cls == WordClass::MidNum && previousClass == WordClass::Numeric && afterClass == WordClass::Numeric;
                    if (joinsLetters || joinsNumbers)
                    {
                        previousClass = afterClass;
                        offset = NextGraphemeBoundary(text, afterPunctuation);
                        continue;
                    }
                }
            }
            break;
        }
        return skipWhitespace(offset);
    }

    std::size_t PreviousWordBoundary(std::u16string_view text, std::size_t offset) noexcept
    {
        offset = std::min(offset, text.size());
        while (offset > 0)
        {
            const auto start = PreviousGraphemeBoundary(text, offset);
            const auto cls = ClusterWordClass(text, start);
            if (cls != WordClass::Space && cls != WordClass::Newline) break;
            offset = start;
        }
        if (offset == 0) return 0;

        // Reverse navigation is intentionally derived from forward boundaries. It is a little
        // more work than a separate reverse rule table, but guarantees left/right symmetry.
        std::size_t previous = 0;
        std::size_t cursor = 0;
        while (cursor < offset)
        {
            const auto next = NextWordBoundary(text, cursor);
            if (next <= cursor) break;
            if (next >= offset) return cursor;
            previous = cursor;
            cursor = next;
        }
        return previous;
    }
}
