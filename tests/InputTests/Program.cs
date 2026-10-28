using MonacoNative.I18n;
using MonacoNative.Input;

namespace MonacoNative.Input.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Run(string name, Action action)
    {
        try
        {
            action();
            ++_passed;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            ++_failed;
            Console.Error.WriteLine($"[FAIL] {name}: {ex}");
        }
    }

    private static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected {expected}, got {actual}.");
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static int Main()
    {
        Run("pinned CJK and emoji corpus", () =>
        {
            ImeConformanceMatrix.Validate();
            Equal(10, ImeConformanceMatrix.Cases.Count);
            Equal(5, ImeConformanceMatrix.Cases.Single(x => x.Id == "emoji-zwj").Utf16Length);
            Equal(1, ImeConformanceMatrix.Cases.Single(x => x.Id == "emoji-zwj").TextElementCount);
            True(ImeConformanceMatrix.Cases.Count(x => x.RequiresComposition) >= 5, "Expected composition-heavy CJK cases.");
        });

        Run("composition overlap replacement and default gaps", () =>
        {
            var state = new ImeCompositionState();
            state.Begin(new ImeTextRange(10, 20), 100);
            state.ApplyFormat(new ImeCompositionSegment(new ImeTextRange(12, 18), ImeCompositionSegmentKind.Converted, ImeUnderlineStyle.Dotted), 100);
            state.ApplyFormat(new ImeCompositionSegment(new ImeTextRange(15, 17), ImeCompositionSegmentKind.TargetConverted, ImeUnderlineStyle.Thick), 100);
            var actual = state.GetRenderableSegments();
            Equal(5, actual.Count);
            Equal(new ImeTextRange(10, 12), actual[0].Range);
            Equal(new ImeTextRange(12, 15), actual[1].Range);
            Equal(new ImeTextRange(15, 17), actual[2].Range);
            True(actual[2].IsTarget, "Target segment lost.");
            Equal(new ImeTextRange(17, 18), actual[3].Range);
            Equal(new ImeTextRange(18, 20), actual[4].Range);
        });

        Run("composition range tracks incremental server edits", () =>
        {
            var state = new ImeCompositionState();
            state.Begin(new ImeTextRange(5, 5), 10);
            state.ApplyTextUpdate(new ImeTextRange(5, 5), 3, 13);
            Equal(new ImeTextRange(5, 8), state.CompositionRange);
            state.ApplyTextUpdate(new ImeTextRange(6, 7), 2, 14);
            Equal(new ImeTextRange(5, 9), state.CompositionRange);
            state.ApplyTextUpdate(new ImeTextRange(5, 9), 1, 11);
            Equal(new ImeTextRange(5, 6), state.CompositionRange);
        });

        Run("randomized composition segments remain ordered and gap-free", () =>
        {
            var random = new Random(0x51A6E2);
            for (var iteration = 0; iteration < 500; ++iteration)
            {
                var length = random.Next(1, 120);
                var start = random.Next(0, length);
                var end = random.Next(start + 1, length + 1);
                var state = new ImeCompositionState();
                state.Begin(new ImeTextRange(start, end), length);
                for (var edit = 0; edit < 40; ++edit)
                {
                    var a = random.Next(start, end);
                    var b = random.Next(a + 1, end + 1);
                    var kind = (ImeCompositionSegmentKind)random.Next(1, 5);
                    var style = (ImeUnderlineStyle)random.Next(1, 20);
                    state.ApplyFormat(new ImeCompositionSegment(new ImeTextRange(a, b), kind, style), length);
                }
                var segments = state.GetRenderableSegments();
                True(segments.Count > 0, "Active non-empty composition must render.");
                Equal(start, segments[0].Range.Start);
                Equal(end, segments[^1].Range.End);
                for (var i = 1; i < segments.Count; ++i)
                    Equal(segments[i - 1].Range.End, segments[i].Range.Start, "Composition segments must be gap-free and non-overlapping.");
            }
        });

        Run("randomized ACP update planning remains in bounds", () =>
        {
            var random = new Random(0xC0E7E57);
            var samples = new[] { "", "x", "中文", "😀", "👩‍💻", "한글", "にほん" };
            for (var i = 0; i < 5000; ++i)
            {
                var modelLength = random.Next(0, 200);
                var start = random.Next(-100, 300);
                var end = random.Next(-100, 300);
                var text = samples[random.Next(samples.Length)];
                var selectionStart = random.Next(-100, 400);
                var selectionEnd = random.Next(-100, 400);
                var plan = ImeTextUpdatePlanner.Create(modelLength, start, end, text, selectionStart, selectionEnd);
                True(plan.ReplaceRange.Start >= 0 && plan.ReplaceRange.End <= modelLength, "Replace range escaped model.");
                var resultingLength = modelLength - plan.ReplaceRange.Length + text.Length;
                True(plan.NewSelection.Start >= 0 && plan.NewSelection.End <= resultingLength, "Selection escaped resulting model.");
                True(plan.CompositionRange.Start >= 0 && plan.CompositionRange.End <= resultingLength, "Composition escaped resulting model.");
            }
        });

        Run("fallback CharacterReceived preserves surrogate pairs", () =>
        {
            var accumulator = new Utf16CharacterAccumulator();
            Equal<string?>(null, accumulator.Push('\uD83D'));
            Equal("😀", accumulator.Push('\uDE00'));
            Equal("x", accumulator.Push('x'));
            Equal("�", accumulator.Push('\uDE00'));
            Equal<string?>(null, accumulator.Push('\uD83D'));
            Equal("�y", accumulator.Push('y'));
        });

        Run("IME projection excludes injected text from composition decoration", () =>
        {
            // Visual text: abXYZcd, where XYZ is injected at model ACP offset 2.
            var boundaries = new[] { 0, 1, 2, 2, 2, 2, 3, 4 };
            var all = ImeProjectionFragments.GetModelBackedFragments(boundaries, new ImeTextRange(0, 4));
            Equal(2, all.Count);
            Equal(new ImeVisualFragment(0, 2), all[0]);
            Equal(new ImeVisualFragment(5, 7), all[1]);

            var middle = ImeProjectionFragments.GetModelBackedFragments(boundaries, new ImeTextRange(1, 3));
            Equal(2, middle.Count);
            Equal(new ImeVisualFragment(1, 2), middle[0]);
            Equal(new ImeVisualFragment(5, 6), middle[1]);
        });

        Run("candidate geometry union", () =>
        {
            var value = ImeGeometry.Union(new[]
            {
                new ImeRect(10, 20, 30, 10),
                new ImeRect(5, 30, 100, 10),
                new ImeRect(20, 40, 10, 10),
            });
            Equal(new ImeRect(5, 20, 100, 30), value);
        });

        Run("bidi base direction is independent and first-strong", () =>
        {
            Equal(BidiBaseDirection.LeftToRight, BidiTextDirectionResolver.Resolve("hello שלום"));
            Equal(BidiBaseDirection.RightToLeft, BidiTextDirectionResolver.Resolve("שלום hello"));
            Equal(BidiBaseDirection.RightToLeft, BidiTextDirectionResolver.Resolve("  مرحبا world"));
            Equal(BidiBaseDirection.RightToLeft, BidiTextDirectionResolver.Resolve("\U0001E900abc"));
            Equal(BidiBaseDirection.RightToLeft, BidiTextDirectionResolver.Resolve("123 שלום"));
            Equal(BidiBaseDirection.LeftToRight, BidiTextDirectionResolver.Resolve("١٢٣ hello"));
            Equal(BidiBaseDirection.RightToLeft, BidiTextDirectionResolver.Resolve("١٢٣ مرحبا"));
            Equal(BidiBaseDirection.RightToLeft, BidiTextDirectionResolver.Resolve("\u200Fabc"));
            Equal(BidiBaseDirection.LeftToRight, BidiTextDirectionResolver.Resolve("\u200Eשלום"));
            Equal(BidiBaseDirection.LeftToRight, BidiTextDirectionResolver.Resolve("   …"));
        });

        Run("i18n locale keys remain usable from input UI", () =>
        {
            var en = new MonacoLocalizationService("en-US");
            var zh = new MonacoLocalizationService("zh-CN");
            Equal("native TSF composition", en.GetString("monacoNative.sample.ime.nativeTsf"));
            Equal("输入法目标分段", zh.GetString("ime.composition.target"));
            Equal("Trigger Suggest", en.GetString("editor.action.triggerSuggest"));
            Equal("重命名符号", zh.GetString("editor.action.rename"));
            Equal("全部替换", zh.GetString("monacoNative.find.replaceAll"));
            Equal("Hide unchanged regions", en.GetString("monacoNative.diff.hideUnchanged"));
            Equal("还原此差异", zh.GetString("monacoNative.diff.revertChange"));
            var ja = new MonacoLocalizationService("ja-JP");
            var ko = new MonacoLocalizationService("ko-KR");
            var tabMenuKeys = new[]
            {
                "monacoNative.tabs.close", "monacoNative.tabs.closeOthers", "monacoNative.tabs.closeToRight",
                "monacoNative.tabs.closeSaved", "monacoNative.tabs.closeAll", "monacoNative.tabs.save",
                "monacoNative.tabs.saveAs", "monacoNative.tabs.compareSelected", "monacoNative.tabs.copyPath",
                "monacoNative.tabs.copyRelativePath", "monacoNative.tabs.revealFileInExplorer",
                "monacoNative.tabs.revealInExplorerView", "monacoNative.tabs.openInIntegratedTerminal",
                "monacoNative.tabs.reopenEditorWith", "monacoNative.tabs.keepOpen", "monacoNative.tabs.pin",
                "monacoNative.tabs.unpin", "monacoNative.tabs.splitRight", "monacoNative.tabs.splitAndMove",
                "monacoNative.tabs.splitLeft", "monacoNative.tabs.splitUp", "monacoNative.tabs.splitDown",
                "monacoNative.tabs.moveAbove", "monacoNative.tabs.moveBelow", "monacoNative.tabs.moveLeft",
                "monacoNative.tabs.moveRight", "monacoNative.tabs.splitInGroup", "monacoNative.tabs.joinInGroup",
                "monacoNative.tabs.moveIntoNewWindow", "monacoNative.tabs.copyIntoNewWindow", "monacoNative.tabs.share",
            };
            foreach (var key in tabMenuKeys)
            {
                var english = en.GetString(key);
                True(!string.IsNullOrWhiteSpace(english) && english != key, $"English tab-menu resource missing: {key}");
                True(zh.GetString(key) != english, $"Chinese tab-menu translation missing: {key}");
                True(ja.GetString(key) != english, $"Japanese tab-menu translation missing: {key}");
                True(ko.GetString(key) != english, $"Korean tab-menu translation missing: {key}");
            }
            Equal("元に戻す", ja.GetString("undo"));
            Equal("실행 취소", ko.GetString("undo"));
            ja.SetCulture("ja");
            Equal("ja-JP", ja.CultureName);
            ko.SetCulture("ko");
            Equal("ko-KR", ko.CultureName);
            en.SetCulture("fr-FR");
            Equal("en-US", en.CultureName);
        });

        Console.WriteLine($"Input/i18n tests: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
