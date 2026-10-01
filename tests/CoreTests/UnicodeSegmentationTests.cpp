#include "TestHarness.h"
#include "MonacoNative/Core/UnicodeSegmentation.h"

#include <cstdint>
#include <random>
#include <string>
#include <vector>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    std::vector<std::size_t> Boundaries(std::u16string_view text)
    {
        std::vector<std::size_t> result{0};
        std::size_t offset = 0;
        while (offset < text.size())
        {
            const auto next = Unicode::NextGraphemeBoundary(text, offset);
            MN_CHECK(next > offset);
            offset = next;
            result.push_back(offset);
        }
        return result;
    }

    void ExpectBoundaries(std::u16string_view text, std::initializer_list<std::size_t> expected)
    {
        const auto actual = Boundaries(text);
        MN_CHECK_EQ(actual.size(), expected.size());
        std::size_t i = 0;
        for (const auto value : expected)
        {
            MN_CHECK_EQ(actual[i], value);
            ++i;
        }
        for (std::size_t index = 0; index < actual.size(); ++index)
        {
            const auto boundary = actual[index];
            MN_CHECK(Unicode::IsGraphemeBoundary(text, boundary));
            if (index > 0)
                MN_CHECK_EQ(Unicode::PreviousGraphemeBoundary(text, boundary), actual[index - 1]);
        }
    }

    void CombiningAndCrLf()
    {
        ExpectBoundaries(u"a\u0301b\r\nc", {0, 2, 3, 5, 6});
    }

    void HangulSyllables()
    {
        ExpectBoundaries(u"\u1100\u1161\u11A8X", {0, 3, 4});
    }

    void EmojiZwJAndModifiers()
    {
        const std::u16string family = u"\U0001F469\u200D\U0001F4BB!";
        ExpectBoundaries(family, {0, 5, 6});
        const std::u16string toned = u"\U0001F44D\U0001F3FDx";
        ExpectBoundaries(toned, {0, 4, 5});
    }

    void RegionalIndicatorsPairFromLeft()
    {
        const std::u16string text = u"\U0001F1FA\U0001F1F8\U0001F1E8";
        ExpectBoundaries(text, {0, 4, 6});
    }

    void EmojiTagAndKeycap()
    {
        const std::u16string keycap = u"1\uFE0F\u20E3x";
        ExpectBoundaries(keycap, {0, 3, 4});
        const std::u16string tag = u"\U0001F3F4\U000E0067\U000E0062\U000E007F!";
        ExpectBoundaries(tag, {0, 8, 9});
    }

    void IndicViramaConjunct()
    {
        ExpectBoundaries(u"\u0915\u094D\u0937x", {0, 3, 4});
        // Unicode 18 revised GB9c no longer requires a preceding consonant.
        ExpectBoundaries(u"\u094D\u0937x", {0, 2, 3});
        ExpectBoundaries(u"\u094D\u0301\u0937x", {0, 3, 4});
    }

    void WordNavigationAsciiAndNumbers()
    {
        const std::u16string text = u"alpha can\u2019t 3.14";
        MN_CHECK_EQ(Unicode::NextWordBoundary(text, 0), std::size_t{6});
        MN_CHECK_EQ(Unicode::NextWordBoundary(text, 5), std::size_t{6});
        MN_CHECK_EQ(Unicode::NextWordBoundary(text, 6), std::size_t{12});
        MN_CHECK_EQ(Unicode::PreviousWordBoundary(text, 11), std::size_t{6});
        MN_CHECK_EQ(Unicode::NextWordBoundary(text, 12), text.size());
    }

    void WordNavigationCjkDoesNotCollapseParagraph()
    {
        // Escape the code points so the contract is not affected by the MSVC source code page.
        const std::u16string text = u"\u4E2D\u6587\u6D4B\u8BD5";
        MN_CHECK_EQ(Unicode::NextWordBoundary(text, 0), std::size_t{1});
        MN_CHECK_EQ(Unicode::NextWordBoundary(text, 1), std::size_t{2});
        MN_CHECK_EQ(Unicode::PreviousWordBoundary(text, 4), std::size_t{3});
    }

    void RandomBoundarySymmetry()
    {
        std::mt19937 rng(0x29A57A10U);
        const std::vector<std::u16string> atoms{
            u"a", u"Z", u" ", u"\r\n", u"\u0301", u"\u1100", u"\u1161", u"\u11A8",
            u"\u0915", u"\u094D", u"\u0937", u"\u200D", u"\U0001F469", u"\U0001F4BB",
            u"\U0001F1FA", u"\U0001F1F8", u"\U0001F44D", u"\U0001F3FD", u"中", u"文"
        };

        for (int sample = 0; sample < 500; ++sample)
        {
            std::u16string text;
            const auto count = 1U + (rng() % 40U);
            for (std::uint32_t i = 0; i < count; ++i)
                text += atoms[rng() % atoms.size()];

            const auto boundaries = Boundaries(text);
            MN_CHECK_EQ(boundaries.front(), std::size_t{0});
            MN_CHECK_EQ(boundaries.back(), text.size());
            for (std::size_t i = 1; i < boundaries.size(); ++i)
            {
                MN_CHECK(boundaries[i] > boundaries[i - 1]);
                MN_CHECK_EQ(Unicode::PreviousGraphemeBoundary(text, boundaries[i]), boundaries[i - 1]);
                MN_CHECK_EQ(Unicode::NextGraphemeBoundary(text, boundaries[i - 1]), boundaries[i]);
                const auto boundary = boundaries[i];
                if (boundary > 0 && boundary < text.size())
                    MN_CHECK(!(text[boundary - 1] >= 0xD800 && text[boundary - 1] <= 0xDBFF && text[boundary] >= 0xDC00 && text[boundary] <= 0xDFFF));
            }
        }
    }
}

int main()
{
    Suite suite("unicode segmentation");
    suite.Run("combining and CRLF", CombiningAndCrLf);
    suite.Run("Hangul syllables", HangulSyllables);
    suite.Run("emoji ZWJ and modifiers", EmojiZwJAndModifiers);
    suite.Run("regional indicator pairing", RegionalIndicatorsPairFromLeft);
    suite.Run("emoji tag and keycap", EmojiTagAndKeycap);
    suite.Run("Indic virama conjunct", IndicViramaConjunct);
    suite.Run("word navigation ASCII and numbers", WordNavigationAsciiAndNumbers);
    suite.Run("word navigation CJK", WordNavigationCjkDoesNotCollapseParagraph);
    suite.Run("random boundary symmetry", RandomBoundarySymmetry);
    return suite.Finish();
}
