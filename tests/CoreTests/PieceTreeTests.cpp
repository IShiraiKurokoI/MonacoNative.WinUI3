#include "TestHarness.h"
#include "MonacoNative/Core/PieceTreeTextBuffer.h"

#include <algorithm>
#include <cstddef>
#include <random>
#include <stdexcept>
#include <string>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    void BasicEditing()
    {
        PieceTreeTextBuffer buffer(u"alpha\nbeta\ngamma");
        MN_CHECK_EQ(buffer.Length(), 16U);
        MN_CHECK_EQ(buffer.LineCount(), 3U);
        MN_CHECK_EQ(buffer.GetLineContent(2), std::u16string(u"beta"));
        MN_CHECK_EQ(buffer.GetText(4, 6), std::u16string(u"a\nbeta"));

        buffer.Insert(5, u"!");
        MN_CHECK_EQ(buffer.GetText(), std::u16string(u"alpha!\nbeta\ngamma"));
        buffer.Erase(5, 6);
        MN_CHECK_EQ(buffer.GetText(), std::u16string(u"alpha\nbeta\ngamma"));
        buffer.Replace(6, 10, u"B");
        MN_CHECK_EQ(buffer.GetText(), std::u16string(u"alpha\nB\ngamma"));
    }

    void EmptyAndLineEndings()
    {
        PieceTreeTextBuffer empty;
        MN_CHECK_EQ(empty.Length(), 0U);
        MN_CHECK_EQ(empty.LineCount(), 1U);
        MN_CHECK_EQ(empty.GetLineContent(1), std::u16string());
        MN_CHECK_EQ(empty.GetPositionAt(100), (Position{1, 1}));

        PieceTreeTextBuffer buffer(u"a\r\nb\n");
        MN_CHECK_EQ(buffer.LineCount(), 3U);
        MN_CHECK_EQ(buffer.GetLineContent(1), std::u16string(u"a"));
        MN_CHECK_EQ(buffer.GetLineContent(2), std::u16string(u"b"));
        MN_CHECK_EQ(buffer.GetLineContent(3), std::u16string());
        MN_CHECK_EQ(buffer.GetOffsetAt(Position{1, 99}), 1U);
        MN_CHECK_EQ(buffer.GetOffsetAt(Position{99, 99}), 5U);
        MN_CHECK_EQ(buffer.GetPositionAt(3), (Position{2, 1}));
    }

    void InvalidRanges()
    {
        PieceTreeTextBuffer buffer(u"abc");
        MN_CHECK_THROWS(buffer.Insert(4, u"x"), std::out_of_range);
        MN_CHECK_THROWS(buffer.Erase(2, 1), std::out_of_range);
        MN_CHECK_THROWS(buffer.Erase(0, 4), std::out_of_range);
        MN_CHECK_THROWS((void)buffer.GetText(4, 1), std::out_of_range);
        MN_CHECK_THROWS((void)buffer.GetLineContent(0), std::out_of_range);
        MN_CHECK_THROWS((void)buffer.GetLineContent(2), std::out_of_range);
    }

    void RepeatedPieceSplits()
    {
        PieceTreeTextBuffer buffer(u"0123456789");
        for (int i = 0; i < 100; ++i)
        {
            buffer.Insert(5, u"X");
        }
        MN_CHECK_EQ(buffer.Length(), 110U);
        MN_CHECK_EQ(buffer.GetText(0, 5), std::u16string(u"01234"));
        MN_CHECK_EQ(buffer.GetText(105, 5), std::u16string(u"56789"));
        buffer.Erase(5, 105);
        MN_CHECK_EQ(buffer.GetText(), std::u16string(u"0123456789"));
    }

    void DeterministicFuzz()
    {
        constexpr int seeds = 12;
        constexpr int iterationsPerSeed = 2500;
        const std::u16string alphabet = u"abXY\n\r\t\u4E2D\u0301\xD83D\xDE00";

        for (int seed = 0; seed < seeds; ++seed)
        {
            PieceTreeTextBuffer buffer;
            std::u16string reference;
            std::mt19937 rng(0x4D4E5700U + static_cast<unsigned>(seed));

            for (int iteration = 0; iteration < iterationsPerSeed; ++iteration)
            {
                const auto operation = static_cast<int>(rng() % 3U);
                const auto start = reference.empty() ? 0U : static_cast<std::size_t>(rng() % (reference.size() + 1));
                const auto end = start + (reference.empty() ? 0U : static_cast<std::size_t>(rng() % (reference.size() - start + 1)));
                std::u16string payload;
                const auto payloadLength = static_cast<std::size_t>(rng() % 7U);
                for (std::size_t i = 0; i < payloadLength; ++i)
                {
                    payload.push_back(alphabet[static_cast<std::size_t>(rng() % alphabet.size())]);
                }

                if (operation == 0)
                {
                    buffer.Insert(start, payload);
                    reference.insert(start, payload);
                }
                else if (operation == 1)
                {
                    buffer.Erase(start, end);
                    reference.erase(start, end - start);
                }
                else
                {
                    buffer.Replace(start, end, payload);
                    reference.replace(start, end - start, payload);
                }

                MN_CHECK_EQ(buffer.GetText(), reference);
                MN_CHECK_EQ(buffer.Length(), reference.size());

                const auto rangeStart = reference.empty() ? 0U : static_cast<std::size_t>(rng() % (reference.size() + 1));
                const auto rangeLength = static_cast<std::size_t>(rng() % 20U);
                MN_CHECK_EQ(buffer.GetText(rangeStart, rangeLength), reference.substr(rangeStart, rangeLength));

                if (iteration % 50 == 0)
                {
                    std::vector<std::size_t> lineStarts{0};
                    for (std::size_t i = 0; i < reference.size(); ++i)
                    {
                        if (reference[i] == u'\n')
                        {
                            lineStarts.push_back(i + 1);
                        }
                    }
                    MN_CHECK_EQ(buffer.LineCount(), lineStarts.size());

                    const auto offset = reference.empty() ? 0U : static_cast<std::size_t>(rng() % (reference.size() + 1));
                    const auto upper = std::upper_bound(lineStarts.begin(), lineStarts.end(), offset);
                    const auto lineIndex = upper == lineStarts.begin() ? 0U : static_cast<std::size_t>(std::distance(lineStarts.begin(), upper) - 1);
                    const Position expectedPosition{
                        static_cast<std::int32_t>(lineIndex + 1),
                        static_cast<std::int32_t>(offset - lineStarts[lineIndex] + 1)};
                    MN_CHECK_EQ(buffer.GetPositionAt(offset), expectedPosition);

                    const auto lineNumber = static_cast<std::int32_t>(1 + (rng() % lineStarts.size()));
                    const auto startOffset = lineStarts[static_cast<std::size_t>(lineNumber - 1)];
                    const auto rawEnd = static_cast<std::size_t>(lineNumber) < lineStarts.size()
                        ? lineStarts[static_cast<std::size_t>(lineNumber)] - 1
                        : reference.size();
                    auto contentEnd = rawEnd;
                    if (contentEnd > startOffset && reference[contentEnd - 1] == u'\r')
                    {
                        --contentEnd;
                    }
                    MN_CHECK_EQ(buffer.GetLineContent(lineNumber), reference.substr(startOffset, contentEnd - startOffset));
                    MN_CHECK_EQ(buffer.GetLineLength(lineNumber), contentEnd - startOffset);
                    MN_CHECK_EQ(buffer.GetOffsetAt(Position{lineNumber, 99999}), contentEnd);
                }
            }
        }
    }
}

int main()
{
    Suite suite("PieceTreeTests");
    suite.Run("basic editing", BasicEditing);
    suite.Run("empty document and line endings", EmptyAndLineEndings);
    suite.Run("invalid ranges", InvalidRanges);
    suite.Run("repeated piece splits", RepeatedPieceSplits);
    suite.Run("deterministic fuzz", DeterministicFuzz);
    return suite.Finish();
}
