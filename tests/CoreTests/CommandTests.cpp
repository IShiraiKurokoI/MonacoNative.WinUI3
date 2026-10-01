#include "TestHarness.h"
#include "MonacoNative/Core/Commands.h"
#include "MonacoNative/Core/TextModel.h"

#include <chrono>
#include <iostream>
#include <stdexcept>
#include <string>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    void ContextExpressions()
    {
        ContextKeyService context;
        context.Set("editorTextFocus", true);
        context.Set("editorReadonly", false);
        context.Set("languageId", std::string("cpp"));
        context.Set("selectionCount", static_cast<std::int64_t>(2));
        context.Set("fontSize", 14.5);

        MN_CHECK(context.Evaluate("editorTextFocus && !editorReadonly"));
        MN_CHECK(context.Evaluate("languageId == 'cpp' && selectionCount >= 2"));
        MN_CHECK(context.Evaluate("fontSize > 14 && fontSize < 15"));
        MN_CHECK(context.Evaluate("languageId =~ '^c(pp)?$'"));
        MN_CHECK(context.Evaluate("missingKey == ''"));
        MN_CHECK(!context.Evaluate("missingKey"));
        MN_CHECK(!context.Evaluate("editorReadonly || languageId == 'python'"));
        MN_CHECK(context.Evaluate("(editorTextFocus && languageId == cpp) || editorReadonly"));
        MN_CHECK_THROWS((void)context.Evaluate("editorTextFocus &&"), std::invalid_argument);

        const auto version = context.Version();
        context.Set("editorTextFocus", true);
        MN_CHECK_EQ(context.Version(), version);
        MN_CHECK(context.Reset("languageId"));
        MN_CHECK(!context.Contains("languageId"));
    }

    void CommandRegistryConditions()
    {
        ContextKeyService context;
        context.Set("enabled", true);
        CommandRegistry registry;
        int calls = 0;
        const auto token = registry.Register(
            CommandDescriptor{"editor.test", "Test", "Edit", "enabled"},
            [&]() { ++calls; return true; });
        MN_CHECK(token != 0);
        MN_CHECK(registry.Contains("editor.test"));
        MN_CHECK(registry.CanExecute("editor.test", context));
        MN_CHECK(registry.Execute("editor.test", context));
        MN_CHECK_EQ(calls, 1);
        context.Set("enabled", false);
        MN_CHECK(!registry.CanExecute("editor.test", context));
        MN_CHECK(!registry.Execute("editor.test", context));
        MN_CHECK(registry.VisibleCommands(context).empty());
        MN_CHECK_THROWS(
            (void)registry.Register(CommandDescriptor{"editor.test", "Other", {}, {}}, [] { return true; }),
            std::invalid_argument);
        MN_CHECK(registry.Unregister(token));
        MN_CHECK(!registry.Unregister(token));
    }

    void ChordParsingAndFormatting()
    {
        const auto chord = KeybindingResolver::ParseChord("Ctrl+K Ctrl+Shift+C");
        MN_CHECK_EQ(chord.size(), 2U);
        MN_CHECK_EQ(chord[0].key, std::string("K"));
        MN_CHECK((chord[0].modifiers & KeyModifierCtrl) != 0);
        MN_CHECK((chord[1].modifiers & KeyModifierShift) != 0);
        MN_CHECK_EQ(KeybindingResolver::FormatChord(chord), std::string("Ctrl+K Ctrl+Shift+C"));
        MN_CHECK_THROWS((void)KeybindingResolver::ParseChord("Ctrl+Bogus+K"), std::invalid_argument);
        MN_CHECK_THROWS((void)KeybindingResolver::ParseChord(""), std::invalid_argument);
    }

    void KeybindingResolutionAndWeights()
    {
        ContextKeyService context;
        context.Set("editorTextFocus", true);
        context.Set("languageId", std::string("cpp"));
        KeybindingResolver resolver;

        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+S"), "file.save.low", "editorTextFocus", 10, true});
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+S"), "file.save.high", "editorTextFocus", 100, true});
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+K Ctrl+C"), "comment.line", "languageId == 'cpp'", 50, true});
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+K Ctrl+U"), "comment.remove", "languageId == 'cpp'", 50, true});

        auto result = resolver.Resolve(KeyStroke{"s", KeyModifierCtrl}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::Command);
        MN_CHECK_EQ(result.commandId, std::string("file.save.high"));

        result = resolver.Resolve(KeyStroke{"K", KeyModifierCtrl}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::MoreChordsNeeded);
        MN_CHECK(resolver.InChordMode());
        MN_CHECK_EQ(resolver.PendingChordLabel(), std::string("Ctrl+K"));

        result = resolver.Resolve(KeyStroke{"c", KeyModifierCtrl}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::Command);
        MN_CHECK_EQ(result.commandId, std::string("comment.line"));
        MN_CHECK(!resolver.InChordMode());

        (void)resolver.Resolve(KeyStroke{"K", KeyModifierCtrl}, context);
        result = resolver.Resolve(KeyStroke{"X", KeyModifierCtrl}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::ChordCancelled);
        MN_CHECK(!resolver.InChordMode());

        context.Set("languageId", std::string("plaintext"));
        result = resolver.Resolve(KeyStroke{"K", KeyModifierCtrl}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::NoMatch);
    }

    void LaterRegistrationWinsAtSameWeight()
    {
        ContextKeyService context;
        KeybindingResolver resolver;
        const auto first = resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("F2"), "rename.first", {}, 10, true});
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("F2"), "rename.second", {}, 10, true});
        auto result = resolver.Resolve(KeyStroke{"F2", 0}, context);
        MN_CHECK_EQ(result.commandId, std::string("rename.second"));
        MN_CHECK(resolver.Unregister(first));
    }

    void HigherPriorityChordShadowsLowerExact()
    {
        ContextKeyService context;
        context.Set("editorTextFocus", true);
        KeybindingResolver resolver;
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+K"), "low.exact", "editorTextFocus", 10, true});
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+K C"), "high.chord", "editorTextFocus", 100, true});

        auto result = resolver.Resolve(KeyStroke{"K", KeyModifierCtrl}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::MoreChordsNeeded);
        MN_CHECK_EQ(result.chordLabel, std::string("Ctrl+K"));
        result = resolver.Resolve(KeyStroke{"C", KeyModifierNone}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::Command);
        MN_CHECK_EQ(result.commandId, std::string("high.chord"));
    }

    void ContextFallbackAndCache()
    {
        ContextKeyService context;
        context.Set("enabled", false);
        KeybindingResolver resolver;
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("F2"), "fallback", {}, 10, true});
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("F2"), "guarded", "enabled", 100, true});

        auto result = resolver.Resolve(KeyStroke{"F2", 0}, context);
        MN_CHECK_EQ(result.commandId, std::string("fallback"));
        MN_CHECK_EQ(resolver.LastFirstChordCandidateCount(), 2U);
        MN_CHECK_EQ(resolver.LastPrefixCandidateCount(), 2U);
        MN_CHECK_EQ(resolver.LastContextEvaluationCount(), 2U);

        result = resolver.Resolve(KeyStroke{"F2", 0}, context);
        MN_CHECK_EQ(result.commandId, std::string("fallback"));
        MN_CHECK_EQ(resolver.LastContextEvaluationCount(), 0U);

        context.Set("enabled", true);
        result = resolver.Resolve(KeyStroke{"F2", 0}, context);
        MN_CHECK_EQ(result.commandId, std::string("guarded"));
        MN_CHECK_EQ(resolver.LastContextEvaluationCount(), 1U);
    }

    void FirstChordCandidatePruning()
    {
        ContextKeyService context;
        KeybindingResolver resolver;
        constexpr int unrelated = 10000;
        for (int i = 0; i < unrelated; ++i)
        {
            resolver.Register(KeybindingRule{
                std::vector<KeyStroke>{KeyStroke{"UNRELATED" + std::to_string(i), KeyModifierCtrl}},
                "cmd.unrelated." + std::to_string(i), {}, 1, true});
        }
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+K Ctrl+C"), "comment.line", {}, 100, true});
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+K Ctrl+U"), "comment.remove", {}, 100, true});

        MN_CHECK_EQ(resolver.RuleCount(), static_cast<std::size_t>(unrelated + 2));
        auto result = resolver.Resolve(KeyStroke{"K", KeyModifierCtrl}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::MoreChordsNeeded);
        MN_CHECK_EQ(resolver.LastFirstChordCandidateCount(), 2U);
        MN_CHECK(resolver.LastPrefixCandidateCount() <= 2U);
    }

    void KeyAliasesAndChordReset()
    {
        ContextKeyService context;
        KeybindingResolver resolver;
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+Period"), "quickfix", {}, 100, true});
        auto result = resolver.Resolve(KeyStroke{"OEM_PERIOD", KeyModifierCtrl}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::Command);
        MN_CHECK_EQ(result.commandId, std::string("quickfix"));

        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+K C"), "plain.second", {}, 100, true});
        result = resolver.Resolve(KeyStroke{"K", KeyModifierCtrl}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::MoreChordsNeeded);
        resolver.ResetChord(); // Same state transition used by the WinUI 5-second timeout.
        result = resolver.Resolve(KeyStroke{"C", KeyModifierNone}, context);
        MN_CHECK_EQ(result.kind, KeybindingResultKind::NoMatch);
    }

    void KeybindingThroughputSmoke()
    {
        ContextKeyService context;
        context.Set("editorTextFocus", true);
        KeybindingResolver resolver;
        for (int i = 0; i < 5000; ++i)
        {
            resolver.Register(KeybindingRule{
                std::vector<KeyStroke>{KeyStroke{"KEY" + std::to_string(i), KeyModifierCtrl}},
                "cmd." + std::to_string(i), {}, 1, true});
        }
        resolver.Register(KeybindingRule{KeybindingResolver::ParseChord("Ctrl+S"), "file.save", "editorTextFocus", 100, true});

        constexpr int iterations = 100000;
        const auto start = std::chrono::steady_clock::now();
        for (int i = 0; i < iterations; ++i)
        {
            const auto result = resolver.Resolve(KeyStroke{"S", KeyModifierCtrl}, context);
            MN_CHECK_EQ(result.kind, KeybindingResultKind::Command);
        }
        const auto elapsed = std::chrono::steady_clock::now() - start;
        const auto micros = std::chrono::duration_cast<std::chrono::microseconds>(elapsed).count();
        MN_CHECK_EQ(resolver.LastFirstChordCandidateCount(), 1U);
        std::cout << "[INFO] keybinding throughput smoke: " << iterations << " resolves in " << micros << " us\n";
    }

    void UndoTransactionGroupsMultipleEdits()
    {
        TextModel model(u"abc");
        model.BeginUndoTransaction();
        model.ApplyOffsetEdits({OffsetEdit{3, 3, u"1"}});
        model.ApplyOffsetEdits({OffsetEdit{4, 4, u"2"}});
        model.EndUndoTransaction();
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abc12"));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abc"));
        MN_CHECK(!model.CanUndo());
        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abc12"));
    }

    void NestedTransactionsAndUndoStop()
    {
        TextModel model(u"");
        model.BeginUndoTransaction();
        model.ApplyOffsetEdits({OffsetEdit{0, 0, u"a"}});
        model.BeginUndoTransaction();
        model.ApplyOffsetEdits({OffsetEdit{1, 1, u"b"}});
        model.EndUndoTransaction();
        model.PushUndoStop();
        model.ApplyOffsetEdits({OffsetEdit{2, 2, u"c"}});
        model.EndUndoTransaction();
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"abc"));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"ab"));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u""));
    }

    void TransactionRollbackRestoresTextAndHistory()
    {
        TextModel model(u"base");
        model.ApplyOffsetEdits({OffsetEdit{4, 4, u"!"}});
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"base!"));
        model.BeginUndoTransaction();
        model.ApplyOffsetEdits({OffsetEdit{0, 0, u"x"}});
        model.PushUndoStop();
        model.ApplyOffsetEdits({OffsetEdit{1, 1, u"y"}});
        MN_CHECK(model.InUndoTransaction());
        MN_CHECK(model.RollbackUndoTransaction());
        MN_CHECK(!model.InUndoTransaction());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"base!"));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetValue(), std::u16string(u"base"));
    }
}

int main()
{
    Suite suite("CommandTests");
    suite.Run("context expressions", ContextExpressions);
    suite.Run("command registry conditions", CommandRegistryConditions);
    suite.Run("chord parsing and formatting", ChordParsingAndFormatting);
    suite.Run("keybinding resolution and weights", KeybindingResolutionAndWeights);
    suite.Run("later keybinding registration wins", LaterRegistrationWinsAtSameWeight);
    suite.Run("higher priority chord shadows lower exact", HigherPriorityChordShadowsLowerExact);
    suite.Run("keybinding context fallback and cache", ContextFallbackAndCache);
    suite.Run("first chord candidate pruning", FirstChordCandidatePruning);
    suite.Run("key aliases and chord reset", KeyAliasesAndChordReset);
    suite.Run("keybinding throughput smoke", KeybindingThroughputSmoke);
    suite.Run("undo transaction grouping", UndoTransactionGroupsMultipleEdits);
    suite.Run("nested transactions and undo stop", NestedTransactionsAndUndoStop);
    suite.Run("transaction rollback", TransactionRollbackRestoresTextAndHistory);
    return suite.Finish();
}
