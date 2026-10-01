#include "TestHarness.h"
#include "MonacoNative/Core/ViewModel.h"

#include <memory>
#include <random>
#include <stdexcept>
#include <string>
#include <vector>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    void WrappingAndCoordinateMapping()
    {
        auto model = std::make_shared<TextModel>(u"0123456789\nabc");
        ViewModel view(model);
        view.SetWrapColumn(4);
        MN_CHECK_EQ(view.ViewLineCount(), 4);
        MN_CHECK_EQ(view.GetViewLine(1).text, std::u16string(u"0123"));
        MN_CHECK_EQ(view.GetViewLine(2).text, std::u16string(u"4567"));
        MN_CHECK_EQ(view.GetViewLine(3).text, std::u16string(u"89"));
        MN_CHECK_EQ(view.GetViewLine(4).modelLineNumber, 2);

        MN_CHECK_EQ(view.ConvertModelPositionToViewPosition(Position{1, 5}), (ViewPosition{2, 1}));
        MN_CHECK_EQ(view.ConvertModelPositionToViewPosition(Position{1, 6}), (ViewPosition{2, 2}));
        MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(ViewPosition{2, 2}), (Position{1, 6}));
        MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(ViewPosition{99, 99}), (Position{2, 4}));
        MN_CHECK_THROWS((void)view.GetViewLine(0), std::out_of_range);
        MN_CHECK_THROWS((void)view.GetViewLine(5), std::out_of_range);
    }

    void MappingRoundTrips()
    {
        auto model = std::make_shared<TextModel>(u"0123456789\na\nABCDE");
        ViewModel view(model);
        view.SetWrapColumn(3);
        for (std::int32_t line = 1; line <= static_cast<std::int32_t>(model->GetLineCount()); ++line)
        {
            for (std::int32_t column = 1; column <= model->GetLineMaxColumn(line); ++column)
            {
                const Position position{line, column};
                const auto projected = view.ConvertModelPositionToViewPosition(position);
                MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(projected), position);
            }
        }
    }

    void RebuildsWhenModelChanges()
    {
        auto model = std::make_shared<TextModel>(u"abc");
        ViewModel view(model);
        view.SetWrapColumn(2);
        MN_CHECK_EQ(view.ViewLineCount(), 2);
        model->ApplyOffsetEdits({OffsetEdit{3, 3, u"\ndefg"}});
        MN_CHECK_EQ(view.ViewLineCount(), 4);
        MN_CHECK_EQ(view.GetViewLine(3).modelLineNumber, 2);
        MN_CHECK_EQ(view.GetViewLine(3).text, std::u16string(u"de"));
        MN_CHECK_EQ(view.GetViewLine(4).text, std::u16string(u"fg"));
        MN_CHECK(model->Undo());
        MN_CHECK_EQ(view.ViewLineCount(), 2);
    }

    void HiddenAreas()
    {
        auto model = std::make_shared<TextModel>(u"one\ntwo\nthree\nfour\nfive");
        ViewModel view(model);
        view.SetHiddenAreas({HiddenArea{4, 3}, HiddenArea{2, 2}});
        MN_CHECK_EQ(view.HiddenAreas().size(), 1U);
        MN_CHECK_EQ(view.HiddenAreas().front(), (HiddenArea{2, 4}));
        MN_CHECK(view.IsModelLineHidden(3));
        MN_CHECK(!view.IsModelLineHidden(1));
        MN_CHECK_EQ(view.ViewLineCount(), 2);
        MN_CHECK_EQ(view.GetViewLine(1).modelLineNumber, 1);
        MN_CHECK_EQ(view.GetViewLine(2).modelLineNumber, 5);

        const auto hiddenProjection = view.ConvertModelPositionToViewPosition(Position{3, 2});
        MN_CHECK_EQ(hiddenProjection, (ViewPosition{1, 4}));
        MN_CHECK_EQ(view.ConvertModelPositionToViewPosition(Position{5, 2}), (ViewPosition{2, 2}));

        view.SetHiddenAreas({});
        MN_CHECK_EQ(view.ViewLineCount(), 5);
    }

    void ViewZonesAndVerticalLayout()
    {
        auto model = std::make_shared<TextModel>(u"a\nb\nc");
        ViewModel view(model);
        view.SetLineHeight(20.0);
        const auto before = view.AddViewZone(0, 5.0);
        const auto afterFirst = view.AddViewZone(1, 10.0);
        const auto afterSecond = view.AddViewZone(2, 7.0);
        MN_CHECK(before != 0 && afterFirst != 0 && afterSecond != 0);
        MN_CHECK_EQ(view.GetVerticalOffsetForViewLine(1), 5.0);
        MN_CHECK_EQ(view.GetVerticalOffsetForViewLine(2), 35.0);
        MN_CHECK_EQ(view.GetVerticalOffsetForViewLine(3), 62.0);
        MN_CHECK_EQ(view.TotalHeight(), 82.0);
        MN_CHECK_EQ(view.GetViewLineAtVerticalOffset(0.0), 1);
        MN_CHECK_EQ(view.GetViewLineAtVerticalOffset(30.0), 2);
        MN_CHECK_EQ(view.GetViewLineAtVerticalOffset(60.0), 3);
        MN_CHECK_EQ(view.GetViewLineAtVerticalOffset(1000.0), 3);

        MN_CHECK(view.RemoveViewZone(afterFirst));
        MN_CHECK(!view.RemoveViewZone(afterFirst));
        MN_CHECK_EQ(view.GetVerticalOffsetForViewLine(2), 25.0);
        view.ClearViewZones();
        MN_CHECK(view.ViewZones().empty());
        MN_CHECK_EQ(view.TotalHeight(), 60.0);
    }


    class ProportionalMeasurer final : public ITextMeasurer
    {
    public:
        [[nodiscard]] double Measure(std::u16string_view text) const override
        {
            double width = 0.0;
            for (const auto ch : text)
            {
                width += ch == u'W' ? 3.0 : 1.0;
            }
            return width;
        }
    };

    void MeasuredWrappingUsesInjectedMeasurer()
    {
        auto model = std::make_shared<TextModel>(u"aaWbb");
        ViewModel view(model);
        view.SetTextMeasurer(std::make_shared<ProportionalMeasurer>());
        view.SetWrapColumn(1); // Measured wrapping takes precedence.
        view.SetWrapWidth(4.0);
        MN_CHECK_EQ(view.ViewLineCount(), 3);
        MN_CHECK_EQ(view.GetViewLine(1).text, std::u16string(u"aa"));
        MN_CHECK_EQ(view.GetViewLine(2).text, std::u16string(u"Wb"));
        MN_CHECK_EQ(view.GetViewLine(3).text, std::u16string(u"b"));
        MN_CHECK_EQ(view.ConvertModelPositionToViewPosition(Position{1, 3}), (ViewPosition{2, 1}));
        MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(ViewPosition{2, 2}), (Position{1, 4}));
    }


    class CountingMeasurer final : public ITextMeasurer
    {
    public:
        [[nodiscard]] double Measure(std::u16string_view text) const override
        {
            ++calls;
            return static_cast<double>(text.size());
        }

        mutable std::size_t calls{0};
    };

    void MeasuredWrappingUsesLogarithmicSearch()
    {
        auto model = std::make_shared<TextModel>(std::u16string(512, u'x'));
        ViewModel view(model);
        auto measurer = std::make_shared<CountingMeasurer>();
        view.SetTextMeasurer(measurer);
        measurer->calls = 0; // Ignore the rebuild performed by SetTextMeasurer itself.
        view.SetWrapWidth(32.0);
        MN_CHECK_EQ(view.ViewLineCount(), 16);
        // 16 wrapped segments should require roughly 16*log2(512) layout measurements,
        // not 512+ progressively longer prefix measurements. Leave generous headroom.
        MN_CHECK(measurer->calls < 200);
    }

    void WrappingDoesNotSplitCommonGraphemeClusters()
    {
        auto model = std::make_shared<TextModel>(u"A\U0001F600B");
        ViewModel view(model);
        view.SetWrapColumn(1);
        MN_CHECK_EQ(view.ViewLineCount(), 3);
        MN_CHECK_EQ(view.GetViewLine(1).text, std::u16string(u"A"));
        MN_CHECK_EQ(view.GetViewLine(2).text, std::u16string(u"\U0001F600"));
        MN_CHECK_EQ(view.GetViewLine(3).text, std::u16string(u"B"));
        MN_CHECK_EQ(view.ConvertModelPositionToViewPosition(Position{1, 2}), (ViewPosition{2, 1}));
        MN_CHECK_EQ(view.ConvertModelPositionToViewPosition(Position{1, 4}), (ViewPosition{3, 1}));
    }

    void InjectedTextSurvivesMeasuredWrapping()
    {
        auto model = std::make_shared<TextModel>(u"aaWbb\ntail");
        ViewModel view(model);
        view.SetTextMeasurer(std::make_shared<ProportionalMeasurer>());
        view.SetWrapWidth(4.0);
        MN_CHECK_EQ(view.ViewLineCount(), 4);

        const auto id = view.AddInjectedText(Position{2, 1}, u"<hint>");
        MN_CHECK(id != 0);
        MN_CHECK(view.ViewLineCount() > 4);
        std::u16string projected;
        for (std::int32_t line = 1; line <= view.ViewLineCount(); ++line)
        {
            const auto fragment = view.GetViewLine(line);
            if (fragment.modelLineNumber == 2)
            {
                projected += fragment.text;
            }
        }
        MN_CHECK_EQ(projected, std::u16string(u"<hint>tail"));
        MN_CHECK_EQ(model->GetLineContent(2), std::u16string(u"tail"));
        view.SetHiddenAreas({HiddenArea{2, 2}});
        MN_CHECK_EQ(view.ViewLineCount(), 3);
    }

    void InjectedTextProjectsAndMapsWithoutChangingModel()
    {
        auto model = std::make_shared<TextModel>(u"abc");
        ViewModel view(model);
        const auto id = view.AddInjectedText(Position{1, 2}, u"[x]", InjectedTextAffinity::MoveAfterInsertion);
        MN_CHECK(id != 0);
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"abc"));
        MN_CHECK_EQ(view.GetViewLine(1).text, std::u16string(u"a[x]bc"));
        MN_CHECK_EQ(view.ConvertModelPositionToViewPosition(Position{1, 2}), (ViewPosition{1, 2}));
        MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(ViewPosition{1, 3}), (Position{1, 2}));
        MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(ViewPosition{1, 5}), (Position{1, 2}));
        MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(ViewPosition{1, 6}), (Position{1, 3}));

        MN_CHECK(view.UpdateInjectedText(id, u"hint"));
        MN_CHECK_EQ(view.GetViewLine(1).text, std::u16string(u"ahintbc"));
        MN_CHECK_THROWS(view.UpdateInjectedText(id, u"bad\ntext"), std::invalid_argument);
        MN_CHECK(view.RemoveInjectedText(id));
        MN_CHECK(!view.RemoveInjectedText(id));
        MN_CHECK_EQ(view.GetViewLine(1).text, std::u16string(u"abc"));
    }

    void InjectedTextAnchorsTrackEditsWithAffinity()
    {
        auto model = std::make_shared<TextModel>(u"abc");
        ViewModel view(model);
        const auto before = view.AddInjectedText(Position{1, 2}, u"<", InjectedTextAffinity::StayBeforeInsertion, 0);
        const auto after = view.AddInjectedText(Position{1, 2}, u">", InjectedTextAffinity::MoveAfterInsertion, 1);
        MN_CHECK(before != after);

        model->ApplyOffsetEdits({OffsetEdit{1, 1, u"ZZ"}});
        const auto injections = view.InjectedTexts();
        MN_CHECK_EQ(injections.size(), 2U);
        MN_CHECK_EQ(injections[0].position, (Position{1, 2}));
        MN_CHECK_EQ(injections[1].position, (Position{1, 4}));
        MN_CHECK_EQ(view.GetViewLine(1).text, std::u16string(u"a<ZZ>bc"));

        MN_CHECK(model->Undo());
        const auto undone = view.InjectedTexts();
        MN_CHECK_EQ(undone[0].position, (Position{1, 2}));
        MN_CHECK_EQ(undone[1].position, (Position{1, 2}));
    }

    void CombinedProjectionRoundTripsVisibleModelPositions()
    {
        auto model = std::make_shared<TextModel>(u"root\nabcdef\nhidden\ntail");
        auto folding = std::make_shared<FoldingModel>(model);
        folding->SetRanges({{2, 3, "manual"}});
        ViewModel view(model);
        view.SetFoldingModel(folding);
        const auto hint = view.AddInjectedText(Position{2, 4}, u"<H>");
        MN_CHECK(hint != 0);
        view.SetWrapWidth(4.0);
        view.SetTextMeasurer(std::make_shared<FixedAdvanceTextMeasurer>(1.0));
        const auto zone = view.AddViewZone(2, 7.0);
        MN_CHECK(zone != 0);

        for (std::int32_t line : {1, 2, 4})
        {
            for (std::int32_t column = 1; column <= model->GetLineMaxColumn(line); ++column)
            {
                const Position position{line, column};
                const auto projected = view.ConvertModelPositionToViewPosition(position);
                MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(projected), position);
            }
        }

        MN_CHECK(folding->SetCollapsed(folding->Ranges()[0].id, true));
        MN_CHECK(view.IsModelLineHidden(3));
        MN_CHECK_EQ(view.ConvertModelPositionToViewPosition(Position{3, 2}).lineNumber,
            view.ConvertModelPositionToViewPosition(Position{2, model->GetLineMaxColumn(2)}).lineNumber);
    }


    void RevealPoliciesRespectWrappingZonesAndViewport()
    {
        auto model = std::make_shared<TextModel>(u"aaaa\nbbbb\ncccc\ndddd");
        ViewModel view(model);
        view.SetWrapColumn(2);
        view.SetLineHeight(10.0);
        const auto zone = view.AddViewZone(2, 5.0);
        MN_CHECK(zone != 0);

        const auto viewRange = view.GetViewLineRangeForModelRange(Range{3, 1, 3, 4});
        MN_CHECK_EQ(viewRange, (std::pair<std::int32_t, std::int32_t>{5, 6}));
        MN_CHECK_EQ(view.GetVerticalOffsetForViewLine(5), 45.0);
        MN_CHECK_EQ(view.GetScrollTopToRevealRange(Range{3, 1, 3, 4}, 30.0, 0.0, RevealStrategy::Minimal), 35.0);
        MN_CHECK_EQ(view.GetScrollTopToRevealRange(Range{3, 1, 3, 4}, 30.0, 0.0, RevealStrategy::Top), 45.0);
        MN_CHECK_EQ(view.GetScrollTopToRevealRange(Range{3, 1, 3, 4}, 30.0, 0.0, RevealStrategy::Center), 40.0);
        MN_CHECK_EQ(view.GetScrollTopToRevealRange(Range{3, 1, 3, 4}, 30.0, 0.0, RevealStrategy::Bottom), 35.0);
        MN_CHECK_EQ(view.GetScrollTopToRevealRange(Range{1, 1, 1, 1}, 30.0, 20.0, RevealStrategy::Minimal), 0.0);
    }


    void SingleLineTypingDoesNotRemeasureWholeDocument()
    {
        std::u16string text;
        for (int i = 0; i < 500; ++i)
        {
            if (i != 0)
            {
                text.push_back(u'\n');
            }
            text.append(u"abcdefghij");
        }

        auto model = std::make_shared<TextModel>(std::move(text));
        ViewModel view(model);
        auto measurer = std::make_shared<CountingMeasurer>();
        view.SetTextMeasurer(measurer);
        view.SetWrapWidth(200.0);
        measurer->calls = 0;

        const auto offset = model->GetOffsetAt(Position{250, 5});
        model->ApplyOffsetEdits({OffsetEdit{offset, offset, u"X"}});
        MN_CHECK_EQ(view.GetViewLine(250).text, std::u16string(u"abcdXefghij"));
        const auto stats = view.LastProjectionUpdateStats();
        MN_CHECK_EQ(stats.replacementModelLines, 1U);
        MN_CHECK_EQ(stats.suffixViewLineMetadataRewrites, 0U);
        MN_CHECK_EQ(stats.projectionSlotMoves, 0U);
        MN_CHECK(measurer->calls < 20);
    }


    void SingleLineWrapTopologyChangeStaysLocal()
    {
        std::u16string text;
        for (int i = 0; i < 800; ++i)
        {
            if (i != 0)
            {
                text.push_back(u'\n');
            }
            text.append(u"abcd");
        }

        auto model = std::make_shared<TextModel>(std::move(text));
        ViewModel view(model);
        auto measurer = std::make_shared<CountingMeasurer>();
        view.SetTextMeasurer(measurer);
        view.SetWrapWidth(2.0);
        measurer->calls = 0;

        const auto offset = model->GetOffsetAt(Position{400, 5});
        model->ApplyOffsetEdits({OffsetEdit{offset, offset, u"e"}});
        MN_CHECK_EQ(view.GetViewLine(view.ConvertModelPositionToViewPosition(Position{400, 1}).lineNumber).text, std::u16string(u"ab"));
        MN_CHECK_EQ(view.ConvertModelPositionToViewPosition(Position{401, 1}).lineNumber,
            view.ConvertModelPositionToViewPosition(Position{400, model->GetLineMaxColumn(400)}).lineNumber + 1);
        const auto stats = view.LastProjectionUpdateStats();
        MN_CHECK_EQ(stats.replacementModelLines, 1U);
        MN_CHECK_EQ(stats.suffixViewLineMetadataRewrites, 0U);
        MN_CHECK_EQ(stats.projectionSlotMoves, 0U);
        MN_CHECK(measurer->calls < 30);
    }

    void StructuralLineInsertDeleteKeepsProjectionLocal()
    {
        std::u16string text;
        for (int i = 0; i < 1200; ++i)
        {
            if (i != 0)
            {
                text.push_back(u'\n');
            }
            text.append(u"abcdefgh");
        }

        auto model = std::make_shared<TextModel>(std::move(text));
        ViewModel view(model);
        auto measurer = std::make_shared<CountingMeasurer>();
        view.SetTextMeasurer(measurer);
        view.SetWrapWidth(4.0);
        measurer->calls = 0;

        const auto split = model->GetOffsetAt(Position{600, 5});
        model->ApplyOffsetEdits({OffsetEdit{split, split, u"\nZZ"}});
        MN_CHECK_EQ(model->GetLineCount(), 1201U);
        MN_CHECK_EQ(view.GetViewLine(view.ConvertModelPositionToViewPosition(Position{600, 1}).lineNumber).text, std::u16string(u"abcd"));
        MN_CHECK_EQ(view.GetViewLine(view.ConvertModelPositionToViewPosition(Position{601, 1}).lineNumber).text, std::u16string(u"ZZef"));
        MN_CHECK_EQ(model->GetLineContent(602), std::u16string(u"abcdefgh"));
        const auto insertStats = view.LastProjectionUpdateStats();
        MN_CHECK_EQ(insertStats.suffixViewLineMetadataRewrites, 0U);
        MN_CHECK(insertStats.projectionSlotMoves > 0U);
        MN_CHECK_EQ(view.GetViewLine(view.ConvertModelPositionToViewPosition(Position{1001, 3}).lineNumber).modelLineNumber, 1001);
        MN_CHECK(measurer->calls < 60);

        measurer->calls = 0;
        const auto joinStart = model->GetOffsetAt(Position{600, model->GetLineMaxColumn(600)});
        const auto joinEnd = model->GetOffsetAt(Position{601, 3});
        model->ApplyOffsetEdits({OffsetEdit{joinStart, joinEnd, u""}});
        MN_CHECK_EQ(model->GetLineCount(), 1200U);
        MN_CHECK_EQ(model->GetLineContent(600), std::u16string(u"abcdefgh"));
        const auto deleteStats = view.LastProjectionUpdateStats();
        MN_CHECK_EQ(deleteStats.suffixViewLineMetadataRewrites, 0U);
        MN_CHECK(deleteStats.projectionSlotMoves > 0U);
        MN_CHECK_EQ(view.GetViewLine(view.ConvertModelPositionToViewPosition(Position{1000, 3}).lineNumber).modelLineNumber, 1000);
        MN_CHECK(measurer->calls < 60);
    }

    void StructuralEditsPreserveZonesFoldingAndInjectedText()
    {
        auto model = std::make_shared<TextModel>(u"root\nabcdefgh\nhidden\ntail");
        auto folding = std::make_shared<FoldingModel>(model);
        folding->SetRanges({{2, 3, "manual"}});
        ViewModel view(model);
        view.SetFoldingModel(folding);
        view.SetWrapColumn(3);
        view.SetLineHeight(10.0);
        const auto injection = view.AddInjectedText(Position{2, 5}, u"<H>");
        const auto zone = view.AddViewZone(3, 7.0);
        MN_CHECK(injection != 0 && zone != 0);
        MN_CHECK(folding->SetCollapsed(folding->Ranges()[0].id, true));
        MN_CHECK(view.IsModelLineHidden(3));

        const auto insertAt = model->GetOffsetAt(Position{1, model->GetLineMaxColumn(1)});
        model->ApplyOffsetEdits({OffsetEdit{insertAt, insertAt, u"\nnew"}});
        MN_CHECK_EQ(model->GetLineCount(), 5U);
        MN_CHECK(view.IsModelLineHidden(4));
        MN_CHECK_EQ(view.ViewZones().front().afterLineNumber, 4);
        const auto injections = view.InjectedTexts();
        MN_CHECK_EQ(injections.size(), 1U);
        MN_CHECK_EQ(injections.front().position.lineNumber, 3);

        for (std::int32_t line : {1, 2, 3, 5})
        {
            for (std::int32_t column = 1; column <= model->GetLineMaxColumn(line); ++column)
            {
                const Position position{line, column};
                const auto projected = view.ConvertModelPositionToViewPosition(position);
                MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(projected), position);
            }
        }
    }

    void RandomizedProjectionRoundTripsAfterSplices()
    {
        auto model = std::make_shared<TextModel>(u"aa\nbbbb\ncccccc\ndd\neeee");
        ViewModel view(model);
        view.SetWrapColumn(3);
        std::mt19937 rng(0xBADC0DEu);

        for (int step = 0; step < 80; ++step)
        {
            const auto lineCount = static_cast<std::int32_t>(model->GetLineCount());
            const auto line = 1 + static_cast<std::int32_t>(rng() % static_cast<std::uint32_t>(lineCount));
            const auto maxColumn = model->GetLineMaxColumn(line);
            const auto column = 1 + static_cast<std::int32_t>(rng() % static_cast<std::uint32_t>(maxColumn));
            const auto offset = model->GetOffsetAt(Position{line, column});
            if ((step % 4) == 0)
            {
                model->ApplyOffsetEdits({OffsetEdit{offset, offset, u"X\nY"}});
            }
            else if ((step % 7) == 0 && model->GetLineCount() > 2 && line < static_cast<std::int32_t>(model->GetLineCount()))
            {
                const auto nextLine = line + 1;
                const auto eraseEnd = model->GetOffsetAt(Position{nextLine, 1});
                model->ApplyOffsetEdits({OffsetEdit{offset, eraseEnd, u"Z"}});
            }
            else
            {
                model->ApplyOffsetEdits({OffsetEdit{offset, offset, u"q"}});
            }

            for (std::int32_t probeLine = 1; probeLine <= static_cast<std::int32_t>(model->GetLineCount()); ++probeLine)
            {
                const auto probeMax = model->GetLineMaxColumn(probeLine);
                for (const auto probeColumn : {1, probeMax})
                {
                    const Position position{probeLine, probeColumn};
                    const auto projected = view.ConvertModelPositionToViewPosition(position);
                    MN_CHECK_EQ(view.ConvertViewPositionToModelPosition(projected), position);
                }
            }
        }
    }

    void VerticalPrefixMetadataTracksStructuralSplices()
    {
        auto model = std::make_shared<TextModel>(u"aa\nbbbb\ncc");
        ViewModel view(model);
        view.SetWrapColumn(2);
        view.SetLineHeight(10.0);
        const auto zone = view.AddViewZone(2, 5.0);
        MN_CHECK(zone != 0);
        MN_CHECK_EQ(view.TotalHeight(), 45.0);

        const auto insertion = model->GetOffsetAt(Position{1, model->GetLineMaxColumn(1)});
        model->ApplyOffsetEdits({OffsetEdit{insertion, insertion, u"\nxx"}});
        MN_CHECK_EQ(view.ViewZones().front().afterLineNumber, 3);
        MN_CHECK_EQ(view.TotalHeight(), 55.0);
        const auto line4 = view.ConvertModelPositionToViewPosition(Position{4, 1}).lineNumber;
        MN_CHECK_EQ(view.GetVerticalOffsetForViewLine(line4), 45.0);
        MN_CHECK_EQ(view.GetViewLineAtVerticalOffset(44.0), line4);
    }

    void HiddenZoneAnchorsDoNotCreateGaps()
    {
        auto model = std::make_shared<TextModel>(u"a\nb\nc");
        ViewModel view(model);
        view.SetLineHeight(10.0);
        const auto hiddenZone = view.AddViewZone(2, 50.0);
        MN_CHECK(hiddenZone != 0);
        view.SetHiddenAreas({HiddenArea{2, 2}});
        MN_CHECK_EQ(view.ViewLineCount(), 2);
        MN_CHECK_EQ(view.GetVerticalOffsetForViewLine(1), 0.0);
        MN_CHECK_EQ(view.GetVerticalOffsetForViewLine(2), 10.0);
        MN_CHECK_EQ(view.TotalHeight(), 20.0);
    }
}

int main()
{
    Suite suite("ViewModelTests");
    suite.Run("wrapping and coordinate mapping", WrappingAndCoordinateMapping);
    suite.Run("mapping round trips", MappingRoundTrips);
    suite.Run("automatic rebuild on model changes", RebuildsWhenModelChanges);
    suite.Run("hidden areas", HiddenAreas);
    suite.Run("view zones and vertical layout", ViewZonesAndVerticalLayout);
    suite.Run("hidden view-zone anchors", HiddenZoneAnchorsDoNotCreateGaps);
    suite.Run("measured wrapping", MeasuredWrappingUsesInjectedMeasurer);
    suite.Run("measured wrapping search complexity", MeasuredWrappingUsesLogarithmicSearch);
    suite.Run("single-line typing is local", SingleLineTypingDoesNotRemeasureWholeDocument);
    suite.Run("single-line wrap topology stays local", SingleLineWrapTopologyChangeStaysLocal);
    suite.Run("structural line edits stay local", StructuralLineInsertDeleteKeepsProjectionLocal);
    suite.Run("structural fold/injected/zone projection", StructuralEditsPreserveZonesFoldingAndInjectedText);
    suite.Run("randomized projection splices", RandomizedProjectionRoundTripsAfterSplices);
    suite.Run("vertical prefix metadata splices", VerticalPrefixMetadataTracksStructuralSplices);
    suite.Run("grapheme-safe wrapping", WrappingDoesNotSplitCommonGraphemeClusters);
    suite.Run("injected text with measured wrapping", InjectedTextSurvivesMeasuredWrapping);
    suite.Run("injected text projection", InjectedTextProjectsAndMapsWithoutChangingModel);
    suite.Run("injected text edit tracking", InjectedTextAnchorsTrackEditsWithAffinity);
    suite.Run("combined projection round trips", CombinedProjectionRoundTripsVisibleModelPositions);
    suite.Run("reveal policies", RevealPoliciesRespectWrappingZonesAndViewport);
    return suite.Finish();
}
