#include "TestHarness.h"
#include "MonacoNative/Core/Commands.h"
#include "MonacoNative/Core/CursorModel.h"
#include "MonacoNative/Core/TextMeasurer.h"
#include "MonacoNative/Core/TextModel.h"
#include "MonacoNative/Core/ViewModel.h"

#include <memory>
#include <string>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    void TextAtomicReplaceUndo()
    {
        auto model = std::make_shared<TextModel>(u"abc\ndef", "plaintext");
        model->ApplyOffsetEdits({OffsetEdit{1, 2, u"XYZ"}});
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"aXYZc\ndef"));
        MN_CHECK(model->Undo());
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"abc\ndef"));
    }

    void SelectionMultiCursorInsert()
    {
        auto model = std::make_shared<TextModel>(u"abcd", "plaintext");
        CursorModel cursor(model);
        cursor.SetSelections({
            Selection{Position{1, 2}, Position{1, 2}},
            Selection{Position{1, 4}, Position{1, 4}},
        });
        cursor.InsertText(u"!");
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"a!bc!d"));
        MN_CHECK_EQ(cursor.Selections().size(), 2U);
        MN_CHECK(model->Undo());
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"abcd"));
    }

    void CommandsWeightedKeybinding()
    {
        ContextKeyService context;
        context.Set("editorTextFocus", true);
        context.Set("editorReadonly", false);
        KeybindingResolver bindings;
        const auto fallback = bindings.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+K"), "editor.action.keybinding.fallback", "editorTextFocus", 10});
        const auto primary = bindings.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+K"), "editor.action.keybinding.primary", "editorTextFocus && !editorReadonly", 100});
        MN_CHECK(fallback != 0 && primary != 0);

        const auto stroke = KeybindingResolver::ParseChord("Ctrl+K").front();
        const auto resolved = bindings.Resolve(stroke, context);
        MN_CHECK_EQ(resolved.kind, KeybindingResultKind::Command);
        MN_CHECK_EQ(resolved.commandId, std::string("editor.action.keybinding.primary"));
    }

    void ProjectionHiddenInjectedWrap()
    {
        auto model = std::make_shared<TextModel>(u"root\nabcdef\nhidden\ntail", "plaintext");
        ViewModel view(model);
        view.SetHiddenAreas({HiddenArea{3, 3}});
        const auto injection = view.AddInjectedText(Position{2, 4}, u"<H>");
        MN_CHECK(injection != 0);
        view.SetTextMeasurer(std::make_shared<FixedAdvanceTextMeasurer>(1.0));
        view.SetWrapWidth(4.0);

        bool sawLine1 = false;
        bool sawLine2 = false;
        bool sawLine4 = false;
        bool sawHidden = false;
        std::u16string projectedLine2;
        for (std::int32_t line = 1; line <= view.ViewLineCount(); ++line)
        {
            const auto projected = view.GetViewLine(line);
            sawLine1 = sawLine1 || projected.modelLineNumber == 1;
            sawLine2 = sawLine2 || projected.modelLineNumber == 2;
            sawLine4 = sawLine4 || projected.modelLineNumber == 4;
            sawHidden = sawHidden || projected.modelLineNumber == 3;
            if (projected.modelLineNumber == 2) projectedLine2 += projected.text;
        }
        MN_CHECK(sawLine1 && sawLine2 && sawLine4);
        MN_CHECK(!sawHidden);
        MN_CHECK_EQ(projectedLine2, std::u16string(u"abc<H>def"));
    }
}

int main()
{
    Suite suite("Monaco parity fixtures");
    suite.Run("text atomic replace + undo", TextAtomicReplaceUndo);
    suite.Run("selection multi-cursor insert", SelectionMultiCursorInsert);
    suite.Run("commands weighted keybinding", CommandsWeightedKeybinding);
    suite.Run("projection hidden + injected + wrap", ProjectionHiddenInjectedWrap);
    return suite.Finish();
}
