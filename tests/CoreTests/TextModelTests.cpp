#include "TestHarness.h"
#include "MonacoNative/Core/TextModel.h"

#include <cstddef>
#include <stdexcept>
#include <string>
#include <vector>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    void ValueAndCoordinates()
    {
        TextModel model(u"alpha\r\nbeta\ngamma", "cpp");
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"alpha\r\nbeta\ngamma"));
        MN_CHECK_EQ(model.GetValue(5, 4), std::u16string(u"\r\nbe"));
        MN_CHECK_EQ(model.GetValueInRange(Range{1, 3, 2, 3}), std::u16string(u"pha\r\nbe"));
        MN_CHECK_EQ(model.GetValueInRange(Range{2, 3, 1, 3}), std::u16string(u"pha\r\nbe"));
        MN_CHECK_EQ(model.GetLineContent(2), std::u16string(u"beta"));
        MN_CHECK_EQ(model.GetLineMaxColumn(2), 5);
        MN_CHECK_EQ(model.GetLineCount(), 3U);
        MN_CHECK_EQ(model.GetPositionAt(model.GetOffsetAt(Position{3, 4})), (Position{3, 4}));
        MN_CHECK_EQ(model.LanguageId(), std::string_view("cpp"));
        model.SetLanguageId("json");
        MN_CHECK_EQ(model.LanguageId(), std::string_view("json"));
    }

    void BatchedEditsEventsAndHistory()
    {
        TextModel model(u"abcdef", "plaintext");
        std::vector<ModelContentChangedEvent> events;
        const auto token = model.OnDidChangeContent().Add([&](const ModelContentChangedEvent& event)
        {
            events.push_back(event);
        });

        model.ApplyOffsetEdits({
            OffsetEdit{4, 5, u"YZ"},
            OffsetEdit{1, 3, u"X"},
        });
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"aXdYZf"));
        MN_CHECK_EQ(model.VersionId(), 2U);
        MN_CHECK(model.CanUndo());
        MN_CHECK(!model.CanRedo());
        MN_CHECK_EQ(events.size(), 1U);
        MN_CHECK_EQ(events[0].changes.size(), 2U);
        MN_CHECK_EQ(events[0].changes[0].rangeOffset, 1U);
        MN_CHECK_EQ(events[0].changes[0].rangeLength, 2U);
        MN_CHECK_EQ(events[0].changes[0].text, std::u16string(u"X"));
        MN_CHECK(!events[0].isUndoing && !events[0].isRedoing);

        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abcdef"));
        MN_CHECK_EQ(events.size(), 2U);
        MN_CHECK(events.back().isUndoing);
        MN_CHECK(model.CanRedo());

        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"aXdYZf"));
        MN_CHECK_EQ(events.size(), 3U);
        MN_CHECK(events.back().isRedoing);
        MN_CHECK(!model.Redo());

        model.OnDidChangeContent().Remove(token);
        model.ApplyOffsetEdits({OffsetEdit{0, 0, u"!"}});
        MN_CHECK_EQ(events.size(), 3U);
    }

    void InvalidEditBatches()
    {
        TextModel model(u"abcdef");
        MN_CHECK_THROWS(model.ApplyOffsetEdits({OffsetEdit{4, 3, u"x"}}), std::out_of_range);
        MN_CHECK_THROWS(model.ApplyOffsetEdits({OffsetEdit{0, 7, u"x"}}), std::out_of_range);
        MN_CHECK_THROWS(model.ApplyOffsetEdits({OffsetEdit{1, 4, u"x"}, OffsetEdit{3, 5, u"y"}}), std::invalid_argument);
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abcdef"));
    }

    void SetValueResetsHistory()
    {
        TextModel model(u"abc");
        model.ApplyOffsetEdits({OffsetEdit{1, 2, u"X"}});
        MN_CHECK(model.CanUndo());
        const auto version = model.VersionId();
        model.SetValue(u"replacement");
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"replacement"));
        MN_CHECK_EQ(model.VersionId(), version + 1);
        MN_CHECK(!model.CanUndo());
        MN_CHECK(!model.CanRedo());
    }

    void EolAndIndentation()
    {
        TextModel model(u"\talpha\r\n  beta\r\n(gamma)", "cpp");
        MN_CHECK_EQ(model.GetEOL(), EndOfLineSequence::CRLF);
        MN_CHECK_EQ(model.GetLineIndentation(1), std::u16string(u"\t"));
        MN_CHECK_EQ(model.GetLineIndentation(2), std::u16string(u"  "));
        MN_CHECK_EQ(model.NormalizeIndentation(u"\t  ", 4, true), std::u16string(u"      "));
        MN_CHECK_EQ(model.NormalizeIndentation(u"      ", 4, false), std::u16string(u"\t  "));

        model.SetEOL(EndOfLineSequence::LF);
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"\talpha\n  beta\n(gamma)"));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"\talpha\r\n  beta\r\n(gamma)"));

        TextModel mixed(u"a\rb\r\nc\n");
        mixed.SetEOL(EndOfLineSequence::CRLF);
        MN_CHECK_EQ(mixed.GetValue(), std::u16string(u"a\r\nb\r\nc\r\n"));
    }

    void SearchAndReplaceAll()
    {
        TextModel model(u"Alpha alpha alphabet ALPHA", "plaintext");
        const auto matches = model.FindMatches(u"alpha", false, true);
        MN_CHECK_EQ(matches.size(), 3U);
        MN_CHECK_EQ(matches[0].text, std::u16string(u"Alpha"));
        MN_CHECK_EQ(matches[1].text, std::u16string(u"alpha"));
        MN_CHECK_EQ(matches[2].text, std::u16string(u"ALPHA"));
        MN_CHECK_EQ(model.FindMatches(u"alpha", true, true).size(), 1U);
        MN_CHECK_EQ(model.FindMatches(u"alpha", false, true, 2).size(), 2U);
        MN_CHECK(model.FindMatches(u"alp", false, true).empty());

        const auto replaced = model.ReplaceAll(u"alpha", u"X", false, true, 2);
        MN_CHECK_EQ(replaced, 2U);
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"X X alphabet ALPHA"));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"Alpha alpha alphabet ALPHA"));
        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"X X alphabet ALPHA"));
    }

    void BracketsAndSnapshots()
    {
        TextModel model(u"a(b(c)d)e\n[x]", "cpp");
        auto match = model.FindMatchingBracket(Position{1, 2});
        MN_CHECK(match.has_value());
        MN_CHECK_EQ(match->bracket, (Position{1, 2}));
        MN_CHECK_EQ(match->match, (Position{1, 8}));

        match = model.FindMatchingBracket(Position{2, 4});
        MN_CHECK(match.has_value());
        MN_CHECK_EQ(match->bracket, (Position{2, 3}));
        MN_CHECK_EQ(match->match, (Position{2, 1}));
        MN_CHECK(!model.FindMatchingBracket(Position{1, 1}).has_value());

        const auto snapshot = model.CreateSnapshot();
        const auto snapshotText = snapshot.text;
        model.ApplyOffsetEdits({OffsetEdit{0, 1, u"Z"}});
        MN_CHECK_EQ(snapshot.text, snapshotText);
        MN_CHECK(snapshot.versionId < model.VersionId());
        MN_CHECK_EQ(snapshot.languageId, std::string("cpp"));
    }

    void UndoRedoRestoresCaretAtEachEdit()
    {
        TextModel model(u"ab\ncd");
        MN_CHECK(model.LastRestoredSelections().empty());

        model.ApplyOffsetEdits({OffsetEdit{5, 5, u"X"}});
        MN_CHECK(model.SetLatestUndoSelections({{5, 5}}, {{6, 6}}));
        model.ApplyOffsetEdits({OffsetEdit{6, 6, u"\nq"}});
        MN_CHECK(model.SetLatestUndoSelections({{6, 6}}, {{8, 8}}));
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"ab\ncdX\nq"));

        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{6, 6}}));
        MN_CHECK_EQ(model.GetPositionAt(model.LastRestoredSelections()[0].active), (Position{2, 4}));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{5, 5}}));
        MN_CHECK_EQ(model.GetPositionAt(model.LastRestoredSelections()[0].active), (Position{2, 3}));
        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{6, 6}}));
        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{8, 8}}));
        MN_CHECK_EQ(model.GetPositionAt(model.LastRestoredSelections()[0].active), (Position{3, 2}));
    }

    void UndoRedoSelectionDirectionAndDelete()
    {
        TextModel model(u"abcdef");
        // Reversed selection replacing "cde".
        model.ApplyOffsetEdits({OffsetEdit{2, 5, u"Z"}});
        MN_CHECK(model.SetLatestUndoSelections({{5, 2}}, {{3, 3}}));
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abZf"));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abcdef"));
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{5, 2}}));
        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{3, 3}}));

        // Backspace records the original caret after the deleted character.
        model.ApplyOffsetEdits({OffsetEdit{1, 2, u""}});
        MN_CHECK(model.SetLatestUndoSelections({{2, 2}}, {{1, 1}}));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{2, 2}}));
        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{1, 1}}));
        MN_CHECK(model.Undo());
        // New editing branch invalidates redo without leaking the old cursor.
        model.ApplyOffsetEdits({OffsetEdit{0, 0, u"!"}});
        MN_CHECK(!model.CanRedo());
        MN_CHECK(model.LastRestoredSelections().empty());
    }

    void UndoRedoTransactionAndMultipleCarets()
    {
        TextModel model(u"abcdefgh");
        model.BeginUndoTransaction();
        model.ApplyOffsetEdits({OffsetEdit{6, 6, u"Z"}});
        MN_CHECK(model.SetLatestUndoSelections({{2, 2}, {6, 6}}, {{2, 2}, {7, 7}}));
        model.ApplyOffsetEdits({OffsetEdit{2, 2, u"Y"}});
        // Preserve the FIRST pre-transaction snapshot, even though later
        // reverse-order edits come from a different document revision.
        MN_CHECK(model.SetLatestUndoSelections({{99, 99}}, {{3, 3}}));
        model.EndUndoTransaction();
        MN_CHECK(model.SetLatestUndoAfterSelections({{3, 3}, {8, 8}}));
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abYcdefZgh"));

        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abcdefgh"));
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{2, 2}, {6, 6}}));
        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abYcdefZgh"));
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{3, 3}, {8, 8}}));
    }

    void UndoCursorFallbackAndHistoryReset()
    {
        TextModel model(u"abc");
        // A headless client with no view still gets deterministic cursor offsets.
        model.ApplyOffsetEdits({OffsetEdit{3, 3, u"😀"}});
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{3, 3}}));
        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.LastRestoredSelections(), (std::vector<UndoSelection>{{5, 5}}));
        model.SetValue(u"new");
        MN_CHECK(model.LastRestoredSelections().empty());
        MN_CHECK(!model.CanUndo());
        MN_CHECK(!model.SetLatestUndoSelections({{1, 1}}, {{2, 2}}));
    }
}

int main()
{
    Suite suite("TextModelTests");
    suite.Run("value and coordinates", ValueAndCoordinates);
    suite.Run("batched edits, events, and history", BatchedEditsEventsAndHistory);
    suite.Run("invalid edit batches", InvalidEditBatches);
    suite.Run("SetValue resets history", SetValueResetsHistory);
    suite.Run("EOL and indentation", EolAndIndentation);
    suite.Run("search and replace all", SearchAndReplaceAll);
    suite.Run("brackets and snapshots", BracketsAndSnapshots);
    suite.Run("undo/redo restores each caret position", UndoRedoRestoresCaretAtEachEdit);
    suite.Run("undo/redo selection direction and deletion", UndoRedoSelectionDirectionAndDelete);
    suite.Run("undo/redo transaction and multi-caret", UndoRedoTransactionAndMultipleCarets);
    suite.Run("undo cursor fallback and history reset", UndoCursorFallbackAndHistoryReset);
    return suite.Finish();
}
