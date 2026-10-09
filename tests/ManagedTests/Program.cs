using MonacoNative.Core;
using MonacoNative.I18n;
using MonacoNative.Input;

namespace MonacoNative.Managed.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Run(string name, Action action)
    {
        try
        {
            action();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.Error.WriteLine($"[FAIL] {name}: {ex}");
        }
    }

    private static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected {expected}, got {actual}.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static int Main()
    {
        Run("minimal text-change calculator", () =>
        {
            Equal(new MonacoTextChange(1, 1, "X"), MonacoTextChangeCalculator.Compute("ab", "aXb"));
            Equal(new MonacoTextChange(1, 2, ""), MonacoTextChangeCalculator.Compute("ab", "a"));
            Equal(new MonacoTextChange(1, 2, "X"), MonacoTextChangeCalculator.Compute("abc", "aXc"));
            Equal(new MonacoTextChange(0, 0, ""), MonacoTextChangeCalculator.Compute(null, null));
            Equal(new MonacoTextChange(2, 2, "😀"), MonacoTextChangeCalculator.Compute("ab", "ab😀"));
        });

        Run("construction and basic properties", () =>
        {
            using var model = new MonacoTextModel("alpha\r\nbeta", "cpp");
            Equal((nuint)11, model.Length);
            Equal((nuint)2, model.LineCount);
            Equal("cpp", model.LanguageId);
            Equal("beta", model.GetLineContent(2));
            Equal(5, model.GetLineMaxColumn(2));
            Equal("\r\n", model.GetTextRange(5, 2));
            Equal("pha\r\nbe", model.GetValueInRange(new MonacoRange(1, 3, 2, 3)));
        });

        Run("position and offset conversion", () =>
        {
            using var model = new MonacoTextModel("abc\ndef");
            Equal(new MonacoPosition(2, 2), model.GetPositionAt(5));
            Equal((nuint)5, model.GetOffsetAt(new MonacoPosition(2, 2)));
        });

        Run("find, replace all, undo and redo", () =>
        {
            using var model = new MonacoTextModel("Alpha alpha alphabet ALPHA");
            var matches = model.FindMatches("alpha", matchCase: false, wholeWord: true);
            Equal(3, matches.Count);
            Equal((nuint)2, model.ReplaceAll("alpha", "X", matchCase: false, wholeWord: true, limit: 2));
            Equal("X X alphabet ALPHA", model.Text);
            True(model.CanUndo, "Expected undo to be available.");
            True(model.Undo(), "Undo failed.");
            Equal("Alpha alpha alphabet ALPHA", model.Text);
            True(model.Redo(), "Redo failed.");
            Equal("X X alphabet ALPHA", model.Text);
        });

        Run("undo/redo restores exact caret positions across multiple lines", () =>
        {
            using var model = new MonacoTextModel("ab\ncd");
            IReadOnlyList<MonacoUndoSelection> carets = new[] { new MonacoUndoSelection(5, 5) };
            model.UndoSelectionProvider = () => carets;
            model.Replace(5, 5, "X");
            carets = new[] { new MonacoUndoSelection(6, 6) };
            model.CommitUndoSelectionsAfterEdit();
            model.Replace(6, 6, "\nq");
            carets = new[] { new MonacoUndoSelection(8, 8) };
            model.CommitUndoSelectionsAfterEdit();

            True(model.Undo(), "Undo newline failed.");
            Equal(new MonacoUndoSelection(6, 6), model.RestoredUndoSelections[0]);
            Equal(new MonacoPosition(2, 4), model.GetPositionAt(model.RestoredUndoSelections[0].Active));
            True(model.Undo(), "Undo inserted character failed.");
            Equal(new MonacoUndoSelection(5, 5), model.RestoredUndoSelections[0]);
            True(model.Redo(), "Redo inserted character failed.");
            Equal(new MonacoUndoSelection(6, 6), model.RestoredUndoSelections[0]);
            True(model.Redo(), "Redo newline failed.");
            Equal(new MonacoUndoSelection(8, 8), model.RestoredUndoSelections[0]);
            Equal(new MonacoPosition(3, 2), model.GetPositionAt(model.RestoredUndoSelections[0].Active));
        });

        Run("undo/redo restores reversed selections and backspace caret", () =>
        {
            using var model = new MonacoTextModel("abcdef");
            IReadOnlyList<MonacoUndoSelection> carets = new[] { new MonacoUndoSelection(5, 2) };
            model.UndoSelectionProvider = () => carets;
            model.Replace(2, 5, "Z");
            carets = new[] { new MonacoUndoSelection(3, 3) };
            model.CommitUndoSelectionsAfterEdit();
            True(model.Undo(), "Undo replacement failed.");
            Equal(new MonacoUndoSelection(5, 2), model.RestoredUndoSelections[0]);
            True(model.Redo(), "Redo replacement failed.");
            Equal(new MonacoUndoSelection(3, 3), model.RestoredUndoSelections[0]);

            model.Text = "ab";
            carets = new[] { new MonacoUndoSelection(2, 2) };
            model.Replace(1, 2, "");
            carets = new[] { new MonacoUndoSelection(1, 1) };
            model.CommitUndoSelectionsAfterEdit();
            True(model.Undo(), "Undo backspace failed.");
            Equal(new MonacoUndoSelection(2, 2), model.RestoredUndoSelections[0]);
            True(model.Redo(), "Redo backspace failed.");
            Equal(new MonacoUndoSelection(1, 1), model.RestoredUndoSelections[0]);
        });

        Run("undo/redo restores multiple carets after a grouped edit", () =>
        {
            using var model = new MonacoTextModel("abcdefgh");
            IReadOnlyList<MonacoUndoSelection> carets = new[]
            {
                new MonacoUndoSelection(2, 2), new MonacoUndoSelection(6, 6)
            };
            model.UndoSelectionProvider = () => carets;
            using (var transaction = model.BeginUndoTransaction())
            {
                model.Replace(6, 6, "Z");
                model.Replace(2, 2, "Y");
                transaction.Complete();
            }
            carets = new[] { new MonacoUndoSelection(3, 3), new MonacoUndoSelection(8, 8) };
            model.CommitUndoSelectionsAfterEdit();
            True(model.Undo(), "Undo multi-caret transaction failed.");
            Equal(new MonacoUndoSelection(2, 2), model.RestoredUndoSelections[0]);
            Equal(new MonacoUndoSelection(6, 6), model.RestoredUndoSelections[1]);
            True(model.Redo(), "Redo multi-caret transaction failed.");
            Equal(new MonacoUndoSelection(3, 3), model.RestoredUndoSelections[0]);
            Equal(new MonacoUndoSelection(8, 8), model.RestoredUndoSelections[1]);
        });

        Run("EOL conversion", () =>
        {
            using var model = new MonacoTextModel("a\r\nb\r\n");
            Equal(MonacoEndOfLine.CrLf, model.EndOfLine);
            model.EndOfLine = MonacoEndOfLine.Lf;
            Equal("a\nb\n", model.Text);
            Equal(MonacoEndOfLine.Lf, model.EndOfLine);
        });

        Run("bracket matching", () =>
        {
            using var model = new MonacoTextModel("a(b(c)d)e");
            var match = model.FindMatchingBracket(new MonacoPosition(1, 2));
            True(match.HasValue, "Expected a bracket match.");
            Equal(new MonacoPosition(1, 2), match!.Value.Bracket);
            Equal(new MonacoPosition(1, 8), match.Value.Match);
        });



        Run("managed model change notifications expose incremental invalidation", () =>
        {
            using var model = new MonacoTextModel("abc\ndef");
            MonacoModelChangedEventArgs? last = null;
            var notifications = 0;
            model.Changed += (_, e) => { last = e; notifications++; };
            model.Replace(1, 2, "XYZ");
            Equal(1, notifications);
            True(last is not null && !last.IsFullInvalidation, "Replace should expose an incremental invalidation.");
            Equal((nuint)1, last!.StartOffset);
            Equal(1, last.StartLineNumber);
            Equal((nuint)4, last.NewEndOffset);
            Equal(1, last.OldEndLineNumber);
            Equal(1, last.NewEndLineNumber);

            var rangeStart = model.GetOffsetAt(new MonacoPosition(1, 1));
            var rangeEnd = model.GetOffsetAt(new MonacoPosition(2, 2));
            model.Replace(rangeStart, rangeEnd, "one\ntwo\nthree");
            Equal(1, last.StartLineNumber);
            Equal(2, last.OldEndLineNumber);
            Equal(3, last.NewEndLineNumber);

            model.EndOfLine = MonacoEndOfLine.CrLf;
            True(last.IsFullInvalidation, "EOL conversion should invalidate the full projection.");
        });

        Run("native view projection and DirectWrite-style measurement callback", () =>
        {
            using var model = new MonacoTextModel("aaWbb\ntail");
            using var view = new MonacoViewModel(model);
            var callbackCount = 0;
            view.SetTextMeasurer(text =>
            {
                callbackCount++;
                return text.Sum(ch => ch == 'W' ? 3.0 : 1.0);
            });
            view.SetLineHeight(20.0);
            view.SetWrapWidth(4.0);
            Equal(4, view.ViewLineCount);
            Equal("aa", view.GetViewLineText(1));
            Equal("Wb", view.GetViewLineText(2));
            Equal("b", view.GetViewLineText(3));
            Equal(new MonacoViewPosition(2, 1), view.ModelToView(new MonacoPosition(1, 3)));
            Equal(new MonacoPosition(1, 4), view.ViewToModel(new MonacoViewPosition(2, 2)));
            True(callbackCount > 0, "Expected the native view model to invoke the managed measurement callback.");

            var injected = view.AddInjectedText(new MonacoPosition(2, 1), "<hint>");
            True(injected != 0, "Expected an injected-text id.");
            True(view.ViewLineCount > 4, "Injected text should span multiple wrapped view lines.");
            // Measured wrapping can split an injected label across multiple visual lines.
            // Reassemble only the view fragments belonging to the second model line.
            var projected = string.Concat(Enumerable.Range(1, view.ViewLineCount)
                .Select(view.GetViewLine)
                .Where(line => line.ModelLineNumber == 2)
                .Select(line => line.Text));
            Equal("<hint>tail", projected);
            Equal("aaWbb\ntail", model.Text, "Visual injected text must not modify the underlying model.");
            view.SetHiddenAreas(new[] { new MonacoHiddenArea(2, 2) });
            Equal(3, view.ViewLineCount);
        });

        Run("managed measurement callback exceptions stay behind ABI boundary", () =>
        {
            using var model = new MonacoTextModel("abcdef");
            using var view = new MonacoViewModel(model);
            view.SetTextMeasurer(_ => throw new InvalidOperationException("synthetic renderer failure"));
            view.SetWrapWidth(3.0);
            True(view.ViewLineCount >= 1, "The native callback boundary should survive managed measurement exceptions.");
            view.SetTextMeasurer(null);
        });

        Run("undo transaction groups edits and supports rollback", () =>
        {
            using var model = new MonacoTextModel("abc");
            using (var transaction = model.BeginUndoTransaction())
            {
                model.Replace(3, 3, "1");
                model.Replace(4, 4, "2");
                transaction.Complete();
            }
            Equal("abc12", model.Text);
            True(model.Undo(), "Grouped undo failed.");
            Equal("abc", model.Text);

            using (model.BeginUndoTransaction())
            {
                model.Replace(0, 0, "x");
            }
            Equal("abc", model.Text, "Incomplete transaction should roll back on dispose.");
        });

        Run("managed context keys and chord resolver", () =>
        {
            using var keys = new MonacoKeybindingService();
            keys.SetContext("editorTextFocus", true);
            keys.SetContext("languageId", "cpp");
            True(keys.ContextMatches("editorTextFocus && languageId == 'cpp'"), "Context expression should match.");
            keys.Register("comment.line", "Ctrl+K Ctrl+C", "languageId == 'cpp'", 100);
            var first = keys.Resolve("K", MonacoKeyModifiers.Ctrl);
            Equal(MonacoKeybindingResultKind.MoreChordsNeeded, first.Kind);
            Equal("Ctrl+K", first.PendingChord);
            var second = keys.Resolve("C", MonacoKeyModifiers.Ctrl);
            Equal(MonacoKeybindingResultKind.Command, second.Kind);
            Equal("comment.line", second.CommandId);
        });


        Run("IME composition segments normalize overlaps and fill gaps", () =>
        {
            var state = new ImeCompositionState();
            state.Begin(new ImeTextRange(2, 10), 20);
            state.ApplyFormat(new ImeCompositionSegment(
                new ImeTextRange(4, 7),
                ImeCompositionSegmentKind.Converted,
                ImeUnderlineStyle.Dotted), 20);
            state.ApplyFormat(new ImeCompositionSegment(
                new ImeTextRange(6, 9),
                ImeCompositionSegmentKind.TargetConverted,
                ImeUnderlineStyle.Thick), 20);
            var segments = state.GetRenderableSegments();
            Equal(4, segments.Count);
            Equal(new ImeTextRange(2, 4), segments[0].Range);
            Equal(ImeCompositionSegmentKind.Unconverted, segments[0].Kind);
            Equal(new ImeTextRange(4, 6), segments[1].Range);
            Equal(ImeCompositionSegmentKind.Converted, segments[1].Kind);
            Equal(new ImeTextRange(6, 9), segments[2].Range);
            True(segments[2].IsTarget, "Expected target composition segment.");
            Equal(new ImeTextRange(9, 10), segments[3].Range);
            state.Complete();
            Equal(0, state.GetRenderableSegments().Count);
        });

        Run("IME text update planner uses UTF-16 ACP offsets", () =>
        {
            // A😀B is four UTF-16 code units. Replace the surrogate pair at [1,3).
            var plan = ImeTextUpdatePlanner.Create(4, 1, 3, "中文", 3, 3);
            Equal(new ImeTextRange(1, 3), plan.ReplaceRange);
            Equal("中文", plan.Text);
            Equal(new ImeTextRange(3, 3), plan.NewSelection);
            Equal(new ImeTextRange(1, 3), plan.CompositionRange);

            var clamped = ImeTextUpdatePlanner.Create(3, -10, 99, "x", 99, 99);
            Equal(new ImeTextRange(0, 3), clamped.ReplaceRange);
            Equal(new ImeTextRange(1, 1), clamped.NewSelection);
        });

        Run("IME projection fragments exclude injected visual text", () =>
        {
            var fragments = ImeProjectionFragments.GetModelBackedFragments(
                new[] { 0, 1, 2, 2, 2, 3 },
                new ImeTextRange(0, 3));
            Equal(2, fragments.Count);
            Equal(new ImeVisualFragment(0, 2), fragments[0]);
            Equal(new ImeVisualFragment(4, 5), fragments[1]);
        });

        Run("IME candidate geometry unions wrapped fragments", () =>
        {
            var union = ImeGeometry.Union(new[]
            {
                new ImeRect(100, 40, 20, 22),
                new ImeRect(80, 62, 60, 22),
                new ImeRect(80, 84, 15, 22),
            });
            Equal(new ImeRect(80, 40, 60, 66), union);
        });

        Run("CJK and emoji IME conformance corpus", () =>
        {
            ImeConformanceMatrix.Validate();
            True(ImeConformanceMatrix.Cases.Any(c => c.Id == "zh-pinyin" && c.RequiresComposition), "Missing Pinyin case.");
            True(ImeConformanceMatrix.Cases.Any(c => c.Id == "ja-hiragana-kanji"), "Missing Japanese IME case.");
            True(ImeConformanceMatrix.Cases.Any(c => c.Id == "ko-hangul"), "Missing Korean IME case.");
            Equal(1, ImeConformanceMatrix.Cases.Single(c => c.Id == "emoji-zwj").TextElementCount);
        });

        Run("Current native ABI version and layout", () =>
        {
            MonacoNativeAbi.EnsureCompatible();
            var abi = MonacoNativeAbi.GetInfo();
            Equal(MonacoNativeAbi.Major, abi.Major);
            Equal(MonacoNativeAbi.Minor, abi.Minor);
            Equal(MonacoNativeAbi.Patch, abi.Patch);
            Equal(MonacoNativeAbi.PackedVersion, abi.PackedVersion);
            Equal((uint)8, abi.PointerSize);
            Equal((uint)2, abi.Char16Size);
            Equal((uint)16, abi.RangeSize);
            Equal((uint)24, abi.DiffChangeSize);
        });

        Run("Authoritative Unicode segmentation", () =>
        {
            const string emoji = "A👩‍💻B";
            Equal(1, MonacoUnicode.NextGraphemeBoundary(emoji, 0));
            Equal(6, MonacoUnicode.NextGraphemeBoundary(emoji, 1));
            Equal(1, MonacoUnicode.PreviousGraphemeBoundary(emoji, 6));
            const string words = "one  two...three";
            Equal(5, MonacoUnicode.NextWordBoundary(words, 0));
            Equal(8, MonacoUnicode.NextWordBoundary(words, 5));
            Equal(8, MonacoUnicode.PreviousWordBoundary(words, 11));
        });

        Run("Managed advanced diff projection", () =>
        {
            var diff = MonacoDiffService.Compute(
                "head\na1\na2\nb1\nb2\nend",
                "head\nb1\nb2\na1\na2\nend",
                new MonacoDiffOptions(IgnoreTrimWhitespace: false, ComputeMoves: true));
            True(diff.Changes.Count >= 2, "Expected multiple diff hunks.");
            True(diff.Moves.Count >= 1, "Expected moved-block detection.");

            var inner = MonacoDiffService.Compute(
                "alpha beta",
                "alpha BETA",
                new MonacoDiffOptions(IgnoreTrimWhitespace: false, ComputeMoves: false));
            Equal(1, inner.Changes.Count);
            True(inner.Changes[0].InnerChanges.Count >= 1, "Expected character-level inner changes.");

            var whitespace = MonacoDiffService.Compute("  value  ", "value");
            True(whitespace.IsIdentical, "Trim-only whitespace should be ignored by default.");
        });

        Run("Monaco-aligned localization resources and fallback", () =>
        {
            var nls = new MonacoLocalizationService("zh-CN");
            Equal("撤销", nls.GetString("undo"));
            Equal("添加行注释", nls.GetString("editor.action.addCommentLine"));
            Equal("切换自动换行", nls.GetString("editor.action.toggleWordWrap"));
            Equal("切换呈现空白字符", nls.GetString("editor.action.toggleRenderWhitespace"));
            Equal("切换缩略图", nls.GetString("editor.action.toggleMinimap"));
            nls.SetCulture("en-US");
            Equal("Copy", nls.GetString("editor.action.clipboardCopyAction"));
            nls.SetCulture("fr-FR");
            Equal("en-US", nls.CultureName, "Unsupported locales should canonicalize to the English fallback.");
            Equal("Paste", nls.GetString("editor.action.clipboardPasteAction"), "Unknown locales must fall back to the upstream English source label.");
            nls.SetCulture("zh-Hans-CN");
            Equal("zh-CN", nls.CultureName);
            nls.SetCulture("ja");
            Equal("ja-JP", nls.CultureName);
            Equal("コピー", nls.GetString("editor.action.clipboardCopyAction"));
            nls.SetCulture("ko");
            Equal("ko-KR", nls.CultureName);
            Equal("붙여넣기", nls.GetString("editor.action.clipboardPasteAction"));
            Equal("missing.operation", nls.GetString("missing.operation"));
        });

        Run("disposed handle protection", () =>
        {
            var model = new MonacoTextModel("abc");
            model.Dispose();
            try
            {
                _ = model.Text;
                throw new InvalidOperationException("Expected ObjectDisposedException.");
            }
            catch (ObjectDisposedException)
            {
            }
        });

        Console.WriteLine($"Managed smoke tests: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
