#include "TestHarness.h"
#include "MonacoNative/Core/TextModel.h"
#include "MonacoNative/Core/ViewModel.h"

#include <algorithm>
#include <cstddef>
#include <memory>
#include <random>
#include <string>
#include <tuple>
#include <vector>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    std::u16string RandomPayload(std::mt19937& rng, std::size_t maxLength = 6)
    {
        static const std::u16string alphabet = u"abcXYZ 012\n\r\t\u4E2D\u0301\xD83D\xDE00";
        std::u16string result;
        const auto length = static_cast<std::size_t>(rng() % (maxLength + 1));
        for (std::size_t i = 0; i < length; ++i)
        {
            result.push_back(alphabet[static_cast<std::size_t>(rng() % alphabet.size())]);
        }
        return result;
    }

    std::vector<OffsetEdit> RandomNonOverlappingEdits(std::mt19937& rng, const std::u16string& text)
    {
        std::vector<OffsetEdit> edits;
        const auto wanted = 1U + (rng() % 4U);
        for (unsigned attempt = 0; attempt < 40 && edits.size() < wanted; ++attempt)
        {
            const auto start = text.empty() ? 0U : static_cast<std::size_t>(rng() % (text.size() + 1));
            const auto end = start + (text.empty() ? 0U : static_cast<std::size_t>(rng() % (text.size() - start + 1)));
            const auto conflicts = std::any_of(edits.begin(), edits.end(), [&](const OffsetEdit& edit)
            {
                // Treat equal insertion points as conflicting to keep reference ordering deterministic.
                if (start == end && edit.start == edit.end)
                {
                    return start == edit.start;
                }
                return start < edit.end && edit.start < end;
            });
            if (!conflicts)
            {
                edits.push_back(OffsetEdit{start, end, RandomPayload(rng)});
            }
        }
        std::sort(edits.begin(), edits.end(), [](const OffsetEdit& left, const OffsetEdit& right)
        {
            return std::tie(left.start, left.end) < std::tie(right.start, right.end);
        });
        return edits;
    }

    void ApplyReference(std::u16string& text, const std::vector<OffsetEdit>& edits)
    {
        for (auto iterator = edits.rbegin(); iterator != edits.rend(); ++iterator)
        {
            text.replace(iterator->start, iterator->end - iterator->start, iterator->text);
        }
    }

    std::vector<std::u16string> ReferenceLines(const std::u16string& text)
    {
        std::vector<std::u16string> lines;
        std::size_t start = 0;
        for (std::size_t i = 0; i < text.size(); ++i)
        {
            if (text[i] == u'\n')
            {
                auto line = text.substr(start, i - start);
                if (!line.empty() && line.back() == u'\r')
                {
                    line.pop_back();
                }
                lines.push_back(std::move(line));
                start = i + 1;
            }
        }
        auto last = text.substr(start);
        if (!last.empty() && last.back() == u'\r')
        {
            last.pop_back();
        }
        lines.push_back(std::move(last));
        return lines;
    }

    void CheckModelAgainstReference(TextModel& model, const std::u16string& reference)
    {
        MN_CHECK_EQ(model.GetValue(), reference);
        MN_CHECK_EQ(model.Length(), reference.size());
        const auto lines = ReferenceLines(reference);
        MN_CHECK_EQ(model.GetLineCount(), lines.size());
        for (std::size_t i = 0; i < lines.size(); ++i)
        {
            MN_CHECK_EQ(model.GetLineContent(static_cast<std::int32_t>(i + 1)), lines[i]);
            const auto maxColumn = static_cast<std::int32_t>(lines[i].size() + 1);
            for (std::int32_t column = 1; column <= maxColumn; ++column)
            {
                const Position position{static_cast<std::int32_t>(i + 1), column};
                MN_CHECK_EQ(model.GetPositionAt(model.GetOffsetAt(position)), position);
            }
        }
    }

    void RandomModelEditUndoRedo()
    {
        constexpr int seeds = 10;
        constexpr int iterations = 400;
        for (int seed = 0; seed < seeds; ++seed)
        {
            std::mt19937 rng(0xC0FFEE00U + static_cast<unsigned>(seed));
            std::u16string reference = RandomPayload(rng, 30);
            TextModel model(reference);
            CheckModelAgainstReference(model, reference);

            for (int iteration = 0; iteration < iterations; ++iteration)
            {
                const auto old = reference;
                const auto edits = RandomNonOverlappingEdits(rng, reference);
                model.ApplyOffsetEdits(edits);
                ApplyReference(reference, edits);
                CheckModelAgainstReference(model, reference);

                if ((rng() % 5U) == 0U)
                {
                    MN_CHECK(model.Undo());
                    CheckModelAgainstReference(model, old);
                    MN_CHECK(model.Redo());
                    CheckModelAgainstReference(model, reference);
                }
            }
        }
    }

    void RandomDecorationValidity()
    {
        std::mt19937 rng(0xDEC0A7EU);
        TextModel model(u"0123456789\nabcdefghij\nABCDEFGHIJ");
        for (int i = 0; i < 40; ++i)
        {
            const auto start = static_cast<std::size_t>(rng() % (model.Length() + 1));
            const auto end = start + static_cast<std::size_t>(rng() % (model.Length() - start + 1));
            DecorationOptions options{};
            options.stickiness = static_cast<DecorationStickiness>(rng() % 4U);
            const auto a = model.GetPositionAt(start);
            const auto b = model.GetPositionAt(end);
            const auto id = model.Decorations().Add(Range{a.lineNumber, a.column, b.lineNumber, b.column}, options);
            MN_CHECK(id != 0);
        }

        for (int iteration = 0; iteration < 500; ++iteration)
        {
            const auto edits = RandomNonOverlappingEdits(rng, model.GetValue());
            model.ApplyOffsetEdits(edits);
            for (const auto& decoration : model.Decorations().All())
            {
                const auto start = model.GetOffsetAt(Position{decoration.range.startLineNumber, decoration.range.startColumn});
                const auto end = model.GetOffsetAt(Position{decoration.range.endLineNumber, decoration.range.endColumn});
                MN_CHECK(start <= end);
                MN_CHECK(end <= model.Length());
            }
            if ((rng() % 7U) == 0U && model.CanUndo())
            {
                MN_CHECK(model.Undo());
                MN_CHECK(model.Redo());
            }
        }
    }

    void RandomViewRoundTrips()
    {
        std::mt19937 rng(0x71E70001U);
        for (int document = 0; document < 100; ++document)
        {
            std::u16string text;
            const auto lineCount = 1 + static_cast<int>(rng() % 20U);
            for (int line = 0; line < lineCount; ++line)
            {
                const auto length = static_cast<int>(rng() % 20U);
                for (int i = 0; i < length; ++i)
                {
                    text.push_back(static_cast<char16_t>(u'a' + (rng() % 26U)));
                }
                if (line + 1 < lineCount)
                {
                    text.push_back(u'\n');
                }
            }
            auto model = std::make_shared<TextModel>(text);
            ViewModel view(model);
            view.SetWrapColumn(1 + static_cast<std::int32_t>(rng() % 8U));

            std::vector<HiddenArea> hidden;
            if (lineCount > 3 && (rng() % 2U) == 0U)
            {
                const auto first = 2 + static_cast<std::int32_t>(rng() % static_cast<unsigned>(lineCount - 2));
                const auto last = std::min<std::int32_t>(lineCount, first + static_cast<std::int32_t>(rng() % 3U));
                hidden.push_back(HiddenArea{first, last});
                view.SetHiddenAreas(hidden);
            }

            for (std::int32_t line = 1; line <= lineCount; ++line)
            {
                if (view.IsModelLineHidden(line))
                {
                    continue;
                }
                for (std::int32_t column = 1; column <= model->GetLineMaxColumn(line); ++column)
                {
                    const Position position{line, column};
                    const auto projected = view.ConvertModelPositionToViewPosition(position);
                    MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(projected), position);
                }
            }
        }
    }
    void RandomCombinedProjectionRoundTrips()
    {
        std::mt19937 rng(0x51A9E2D3U);
        for (int document = 0; document < 120; ++document)
        {
            std::u16string text;
            const auto lineCount = 3 + static_cast<int>(rng() % 12U);
            for (int line = 0; line < lineCount; ++line)
            {
                const auto length = 1 + static_cast<int>(rng() % 12U);
                for (int i = 0; i < length; ++i)
                {
                    text.push_back(static_cast<char16_t>(u'a' + (rng() % 26U)));
                }
                if (line + 1 < lineCount)
                {
                    text.push_back(u'\n');
                }
            }

            auto model = std::make_shared<TextModel>(text);
            auto folding = std::make_shared<FoldingModel>(model);
            ViewModel view(model);
            view.SetFoldingModel(folding);
            view.SetLineHeight(8.0 + static_cast<double>(rng() % 8U));
            view.SetWrapWidth(3.0 + static_cast<double>(rng() % 8U));
            view.SetTextMeasurer(std::make_shared<FixedAdvanceTextMeasurer>(1.0));

            std::vector<FoldingRangeCandidate> ranges;
            if (lineCount >= 5)
            {
                const auto start = 1 + static_cast<std::int32_t>(rng() % static_cast<unsigned>(lineCount - 2));
                const auto end = std::min<std::int32_t>(lineCount, start + 1 + static_cast<std::int32_t>(rng() % 3U));
                if (end > start)
                {
                    ranges.push_back(FoldingRangeCandidate{start, end, "property"});
                }
            }
            folding->SetRanges(ranges);
            if (!folding->Ranges().empty() && (rng() % 2U) == 0U)
            {
                MN_CHECK(folding->SetCollapsed(folding->Ranges().front().id, true));
            }

            const auto injectionCount = 1 + static_cast<int>(rng() % 8U);
            for (int i = 0; i < injectionCount; ++i)
            {
                const auto line = 1 + static_cast<std::int32_t>(rng() % static_cast<unsigned>(lineCount));
                const auto maxColumn = model->GetLineMaxColumn(line);
                const auto column = 1 + static_cast<std::int32_t>(rng() % static_cast<unsigned>(maxColumn));
                const auto affinity = (rng() % 2U) == 0U
                    ? InjectedTextAffinity::StayBeforeInsertion
                    : InjectedTextAffinity::MoveAfterInsertion;
                const auto id = view.AddInjectedText(Position{line, column}, u"<h>", affinity, i);
                MN_CHECK(id != 0);
            }

            const auto zoneLine = static_cast<std::int32_t>(rng() % static_cast<unsigned>(lineCount + 1));
            const auto zone = view.AddViewZone(zoneLine, static_cast<double>(rng() % 9U));
            MN_CHECK(zone != 0);

            for (std::int32_t line = 1; line <= lineCount; ++line)
            {
                if (view.IsModelLineHidden(line))
                {
                    continue;
                }
                for (std::int32_t column = 1; column <= model->GetLineMaxColumn(line); ++column)
                {
                    const Position position{line, column};
                    const auto projected = view.ConvertModelPositionToViewPosition(position);
                    MN_CHECK(projected.lineNumber >= 1 && projected.lineNumber <= view.ViewLineCount());
                    MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(projected), position);
                }
            }

            for (std::int32_t viewLine = 1; viewLine <= view.ViewLineCount(); ++viewLine)
            {
                const auto& line = view.GetViewLine(viewLine);
                MN_CHECK_EQ(line.boundaryModelColumns.size(), line.text.size() + 1);
                MN_CHECK_EQ(line.canonicalModelBoundaries.size(), line.text.size() + 1);
                for (std::int32_t column = 1; column <= static_cast<std::int32_t>(line.text.size() + 1); ++column)
                {
                    const auto modelPosition = view.ConvertViewPositionToModelPosition(ViewPosition{viewLine, column});
                    MN_CHECK(modelPosition.lineNumber >= 1);
                    MN_CHECK(modelPosition.lineNumber <= lineCount);
                    MN_CHECK(modelPosition.column >= 1);
                    MN_CHECK(modelPosition.column <= model->GetLineMaxColumn(modelPosition.lineNumber));
                }
            }

            const auto maxScroll = std::max(0.0, view.TotalHeight() - 25.0);
            const auto scroll = view.GetScrollTopToRevealRange(
                Range{lineCount, 1, lineCount, model->GetLineMaxColumn(lineCount)},
                25.0,
                0.0,
                RevealStrategy::Minimal,
                2.0);
            MN_CHECK(scroll >= 0.0 && scroll <= maxScroll);
        }
    }

    void FoldingRangesShiftWithPrefixEditsProperty()
    {
        std::mt19937 rng(0xF01D1A6U);
        for (int iteration = 0; iteration < 200; ++iteration)
        {
            const auto lines = 5 + static_cast<std::int32_t>(rng() % 20U);
            std::u16string text;
            for (std::int32_t line = 1; line <= lines; ++line)
            {
                text.append(u"x");
                if (line < lines)
                {
                    text.push_back(u'\n');
                }
            }
            auto model = std::make_shared<TextModel>(text);
            FoldingModel folding(model);
            const auto start = 2 + static_cast<std::int32_t>(rng() % static_cast<unsigned>(lines - 3));
            const auto end = std::min(lines, start + 1 + static_cast<std::int32_t>(rng() % 3U));
            folding.SetRanges({{start, end, "property"}});
            MN_CHECK_EQ(folding.Ranges().size(), 1U);
            const auto id = folding.Ranges().front().id;
            MN_CHECK(folding.SetCollapsed(id, true));

            const auto prefixLines = 1 + static_cast<std::int32_t>(rng() % 4U);
            std::u16string prefix;
            for (std::int32_t i = 0; i < prefixLines; ++i)
            {
                prefix.append(u"p\n");
            }
            model->ApplyOffsetEdits({OffsetEdit{0, 0, prefix}});
            MN_CHECK_EQ(folding.Ranges().size(), 1U);
            MN_CHECK_EQ(folding.Ranges().front().startLineNumber, start + prefixLines);
            MN_CHECK_EQ(folding.Ranges().front().endLineNumber, end + prefixLines);
            MN_CHECK(folding.Ranges().front().collapsed);

            MN_CHECK(model->Undo());
            MN_CHECK_EQ(folding.Ranges().front().startLineNumber, start);
            MN_CHECK_EQ(folding.Ranges().front().endLineNumber, end);
        }
    }

}

int main()
{
    Suite suite("PropertyTests");
    suite.Run("random model edit/undo/redo", RandomModelEditUndoRedo);
    suite.Run("random decoration validity", RandomDecorationValidity);
    suite.Run("random view round-trips", RandomViewRoundTrips);
    suite.Run("random combined projection round-trips", RandomCombinedProjectionRoundTrips);
    suite.Run("folding prefix edit tracking", FoldingRangesShiftWithPrefixEditsProperty);
    return suite.Finish();
}
