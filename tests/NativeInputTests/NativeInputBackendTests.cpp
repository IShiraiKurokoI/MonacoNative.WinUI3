#include "TestHarness.h"
#include "MonacoNative/Input/Native/MemoryAcpTextStoreBackend.h"
#include "MonacoNative/Input/Native/TextModelAcpTextStoreBackend.h"
#include "MonacoNative/Core/CursorModel.h"
#include "MonacoNative/Core/TextModel.h"

#include <cstdint>
#include <memory>
#include <string>

using MonacoNative::Input::Native::AcpSelection;
using MonacoNative::Input::Native::AcpTextChange;
using MonacoNative::Input::Native::MemoryAcpTextStoreBackend;
using MonacoNative::Input::Native::TextModelAcpTextStoreBackend;
using MonacoNative::Core::CursorModel;
using MonacoNative::Core::OffsetEdit;
using MonacoNative::Core::TextModel;
using MonacoNative::Tests::Suite;

namespace
{
    void Utf16AcpCoordinates()
    {
        MemoryAcpTextStoreBackend backend(u"A\U0001F600B\u0301C");
        MN_CHECK_EQ(backend.Length(), 6);
        MN_CHECK_EQ(backend.GetText(1, 3), std::u16string(u"\U0001F600"));
        MN_CHECK_EQ(backend.GetText(3, 5), std::u16string(u"B\u0301"));
    }

    void SelectionValidation()
    {
        MemoryAcpTextStoreBackend backend(u"abcdef");
        MN_CHECK(backend.SetSelection(AcpSelection{1, 4, true, false}));
        const auto selection = backend.GetSelection();
        MN_CHECK_EQ(selection.start, 1);
        MN_CHECK_EQ(selection.end, 4);
        MN_CHECK(selection.activeEndAtStart);
        MN_CHECK(!backend.SetSelection(AcpSelection{-1, 1, false, false}));
        MN_CHECK(!backend.SetSelection(AcpSelection{2, 99, false, false}));
    }

    void ReplaceCollapsesSelectionAndReportsChange()
    {
        MemoryAcpTextStoreBackend backend(u"abcdef");
        MN_CHECK(backend.SetSelection(AcpSelection{2, 5, false, false}));
        AcpTextChange change{};
        MN_CHECK(backend.Replace(2, 5, u"XYZW", change));
        MN_CHECK_EQ(backend.Value(), std::u16string(u"abXYZWf"));
        MN_CHECK_EQ(change.start, 2);
        MN_CHECK_EQ(change.oldEnd, 5);
        MN_CHECK_EQ(change.newEnd, 6);
        const auto selection = backend.GetSelection();
        MN_CHECK_EQ(selection.start, 6);
        MN_CHECK_EQ(selection.end, 6);
    }

    void InvalidRangesDoNotMutate()
    {
        MemoryAcpTextStoreBackend backend(u"abcdef");
        AcpTextChange change{};
        MN_CHECK(!backend.Replace(5, 2, u"x", change));
        MN_CHECK(!backend.Replace(0, 99, u"x", change));
        MN_CHECK_EQ(backend.Value(), std::u16string(u"abcdef"));
        MN_CHECK_THROWS((void)backend.GetText(-1, 2), std::out_of_range);
    }

    void TextModelBackendReadsPieceTreeRanges()
    {
        auto model = std::make_shared<TextModel>(u"alpha\nA\U0001F600B\r\nomega");
        TextModelAcpTextStoreBackend backend(model);
        MN_CHECK_EQ(backend.Length(), static_cast<std::int32_t>(model->Length()));
        MN_CHECK_EQ(backend.GetText(6, 10), std::u16string(u"A\U0001F600B"));
        MN_CHECK_EQ(backend.GetText(10, 12), std::u16string(u"\r\n"));
    }

    void TextModelBackendReplaceUsesModelUndoAndEvents()
    {
        auto model = std::make_shared<TextModel>(u"abcdef");
        TextModelAcpTextStoreBackend backend(model);
        MN_CHECK(backend.SetSelection(AcpSelection{2, 5, false, false}));

        std::size_t eventCount = 0;
        std::size_t eventOffset = 0;
        std::size_t eventLength = 0;
        std::u16string eventText;
        const auto token = model->OnDidChangeContent().Add([&](const auto& event)
        {
            ++eventCount;
            if (!event.changes.empty())
            {
                eventOffset = event.changes[0].rangeOffset;
                eventLength = event.changes[0].rangeLength;
                eventText = event.changes[0].text;
            }
        });

        AcpTextChange change{};
        MN_CHECK(backend.Replace(2, 5, u"XYZW", change));
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"abXYZWf"));
        MN_CHECK_EQ(change.start, 2);
        MN_CHECK_EQ(change.oldEnd, 5);
        MN_CHECK_EQ(change.newEnd, 6);
        MN_CHECK_EQ(eventCount, static_cast<std::size_t>(1));
        MN_CHECK_EQ(eventOffset, static_cast<std::size_t>(2));
        MN_CHECK_EQ(eventLength, static_cast<std::size_t>(3));
        MN_CHECK_EQ(eventText, std::u16string(u"XYZW"));
        MN_CHECK(model->CanUndo());
        MN_CHECK(model->Undo());
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"abcdef"));
        MN_CHECK_EQ(model->LastRestoredSelections(), (std::vector<MonacoNative::Core::UndoSelection>{{2, 5}}));
        MN_CHECK(model->Redo());
        MN_CHECK_EQ(model->LastRestoredSelections(), (std::vector<MonacoNative::Core::UndoSelection>{{6, 6}}));
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"abXYZWf"));

        model->OnDidChangeContent().Remove(token);
    }

    void TextModelBackendTracksExternalModelEdits()
    {
        auto model = std::make_shared<TextModel>(u"abcdef");
        TextModelAcpTextStoreBackend backend(model);
        MN_CHECK(backend.SetSelection(AcpSelection{4, 4, false, false}));

        model->ApplyOffsetEdits({OffsetEdit{0, 0, u"ZZ"}});
        auto selection = backend.GetSelection();
        MN_CHECK_EQ(selection.start, 6);
        MN_CHECK_EQ(selection.end, 6);

        model->ApplyOffsetEdits({OffsetEdit{2, 5, u"Q"}});
        selection = backend.GetSelection();
        MN_CHECK_EQ(selection.start, 4);
        MN_CHECK_EQ(selection.end, 4);
    }

    void TextModelBackendPreservesUtf16AcpOffsets()
    {
        auto model = std::make_shared<TextModel>(u"A\U0001F600BC");
        TextModelAcpTextStoreBackend backend(model);
        MN_CHECK_EQ(backend.Length(), 5);
        MN_CHECK(backend.SetSelection(AcpSelection{3, 4, false, false}));
        AcpTextChange change{};
        MN_CHECK(backend.Replace(3, 4, u"Z", change));
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"A\U0001F600ZC"));
        MN_CHECK_EQ(change.start, 3);
        MN_CHECK_EQ(change.newEnd, 4);
        MN_CHECK(model->Undo());
        MN_CHECK_EQ(model->LastRestoredSelections(), (std::vector<MonacoNative::Core::UndoSelection>{{3, 4}}));
        MN_CHECK(model->Redo());
        MN_CHECK_EQ(model->LastRestoredSelections(), (std::vector<MonacoNative::Core::UndoSelection>{{4, 4}}));
    }
    void TextModelBackendReportsOnlyExternalChanges()
    {
        auto model = std::make_shared<TextModel>(u"abcdef");
        TextModelAcpTextStoreBackend backend(model);
        MN_CHECK(backend.SetSelection(AcpSelection{3, 3, false, false}));

        std::size_t callbackCount = 0;
        MonacoNative::Input::Native::AcpExternalChangeEvent observed{};
        const auto token = backend.AddExternalChangeHandler([&](const auto& event)
        {
            ++callbackCount;
            observed = event;
        });

        AcpTextChange ownChange{};
        MN_CHECK(backend.Replace(3, 3, u"X", ownChange));
        MN_CHECK_EQ(callbackCount, static_cast<std::size_t>(0));

        model->ApplyOffsetEdits({OffsetEdit{0, 0, u"ZZ"}});
        MN_CHECK_EQ(callbackCount, static_cast<std::size_t>(1));
        MN_CHECK_EQ(observed.textChanges.size(), static_cast<std::size_t>(1));
        MN_CHECK_EQ(observed.textChanges[0].start, 0);
        MN_CHECK_EQ(observed.textChanges[0].oldEnd, 0);
        MN_CHECK_EQ(observed.textChanges[0].newEnd, 2);
        MN_CHECK(observed.selectionChanged);

        backend.RemoveExternalChangeHandler(token);
    }

    void TextModelBackendBindsRealCursorSelection()
    {
        auto model = std::make_shared<TextModel>(u"alpha beta");
        auto cursor = std::make_shared<CursorModel>(model);
        TextModelAcpTextStoreBackend backend(model, cursor);

        std::size_t callbacks = 0;
        bool sawSelectionOnly = false;
        const auto token = backend.AddExternalChangeHandler([&](const auto& event)
        {
            ++callbacks;
            sawSelectionOnly = event.selectionChanged && event.textChanges.empty();
        });

        MN_CHECK(backend.SetSelection(AcpSelection{6, 10, true, false}));
        auto primary = cursor->PrimarySelection();
        MN_CHECK_EQ(model->GetOffsetAt(primary.anchor), static_cast<std::size_t>(10));
        MN_CHECK_EQ(model->GetOffsetAt(primary.active), static_cast<std::size_t>(6));
        MN_CHECK_EQ(callbacks, static_cast<std::size_t>(0));

        cursor->SetPrimarySelection({model->GetPositionAt(1), model->GetPositionAt(5)});
        const auto selection = backend.GetSelection();
        MN_CHECK_EQ(selection.start, 1);
        MN_CHECK_EQ(selection.end, 5);
        MN_CHECK(!selection.activeEndAtStart);
        MN_CHECK_EQ(callbacks, static_cast<std::size_t>(1));
        MN_CHECK(sawSelectionOnly);

        backend.RemoveExternalChangeHandler(token);
    }

    void CursorSelectionTracksExternalModelChangesThroughBackend()
    {
        auto model = std::make_shared<TextModel>(u"abcdef");
        auto cursor = std::make_shared<CursorModel>(model);
        cursor->SetPrimarySelection({model->GetPositionAt(4), model->GetPositionAt(4)});
        TextModelAcpTextStoreBackend backend(model, cursor);

        model->ApplyOffsetEdits({OffsetEdit{0, 0, u"ZZ"}});
        const auto primary = cursor->PrimarySelection();
        MN_CHECK_EQ(model->GetOffsetAt(primary.active), static_cast<std::size_t>(6));
        const auto selection = backend.GetSelection();
        MN_CHECK_EQ(selection.start, 6);
        MN_CHECK_EQ(selection.end, 6);
    }

}

int main()
{
    Suite suite("NativeInputBackendTests");
    suite.Run("UTF-16 ACP coordinates", Utf16AcpCoordinates);
    suite.Run("selection validation", SelectionValidation);
    suite.Run("replace collapses selection and reports change", ReplaceCollapsesSelectionAndReportsChange);
    suite.Run("invalid ranges do not mutate", InvalidRangesDoNotMutate);
    suite.Run("TextModel backend reads PieceTree ranges", TextModelBackendReadsPieceTreeRanges);
    suite.Run("TextModel backend replace uses model undo and events", TextModelBackendReplaceUsesModelUndoAndEvents);
    suite.Run("TextModel backend tracks external model edits", TextModelBackendTracksExternalModelEdits);
    suite.Run("TextModel backend preserves UTF-16 ACP offsets", TextModelBackendPreservesUtf16AcpOffsets);
    suite.Run("TextModel backend reports only external changes", TextModelBackendReportsOnlyExternalChanges);
    suite.Run("TextModel backend binds real CursorModel selection", TextModelBackendBindsRealCursorSelection);
    suite.Run("Cursor selection tracks external model changes", CursorSelectionTracksExternalModelChangesThroughBackend);
    return suite.Finish();
}
