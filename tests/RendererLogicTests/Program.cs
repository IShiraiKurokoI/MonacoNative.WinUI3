using MonacoNative.Renderer.WinUI3;

internal readonly record struct TestInterval(int Id, int Start, int End);

internal static class Program
{
    private static int _failed;

    public static int Main()
    {
        Run("IntelliSense selection: arrow wrapping, paging, empty list", () =>
        {
            var state = new SuggestionSelection();
            state.Reset(4);
            Equal(0, state.SelectedIndex);
            state.Move(-1);
            Equal(3, state.SelectedIndex);
            state.Move(1);
            Equal(0, state.SelectedIndex);
            state.Move(2);
            Equal(2, state.SelectedIndex);
            state.Page(9);
            Equal(3, state.SelectedIndex);
            state.Page(-9);
            Equal(0, state.SelectedIndex);
            state.Select(1);
            Equal(1, state.SelectedIndex);
            state.Select(99);
            Equal(1, state.SelectedIndex);
            state.Reset(0);
            state.Move(1);
            Equal(-1, state.SelectedIndex);
            state.Reset(1);
            state.Move(-9);
            Equal(0, state.SelectedIndex);
        });

        Run("cursor policy: text, gutter, overview, minimap, outside, overlays", () =>
        {
            static EditorPointerCursorKind At(double x, double y, bool overlay = false, bool minimap = true) =>
                EditorPointerCursorPolicy.Resolve(x, y, 1000, 600, 68, minimap ? 906 : 992,
                    914, minimap, overlay);
            Equal(EditorPointerCursorKind.Text, At(200, 100));
            Equal(EditorPointerCursorKind.Text, At(905, 599));
            Equal(EditorPointerCursorKind.Arrow, At(50, 100));
            Equal(EditorPointerCursorKind.Arrow, At(910, 100));
            Equal(EditorPointerCursorKind.Minimap, At(915, 100));
            Equal(EditorPointerCursorKind.Arrow, At(200, -1));
            Equal(EditorPointerCursorKind.Arrow, At(200, 100, overlay: true));
            Equal(EditorPointerCursorKind.Text, At(910, 100, minimap: false));
        });

        Run("folding: nested braces, indentation, strings, regions", () =>
        {
            var folds = EditorFoldingRanges.Compute(new[]
            {
                "void f() {", "  if (yes) {", "    call();", "  }", "}",
                "#region utilities", "const char* text = \"{ not a fold }\";", "#endregion",
                "def f():", "    first()", "    second()", "after()",
            });
            Equal(5, folds[1]);
            Equal(4, folds[2]);
            Equal(8, folds[6]);
            Equal(11, folds[9]);
            True(!folds.ContainsKey(7), "Braces inside quoted strings must be ignored.");
        });

        Run("find regex: case, whole word, selection bounds and replacement capture groups", () =>
        {
            const string source = "foo Foo food 42 foO";
            var (all, error) = EditorFindEngine.SearchRegex(source, "foo", false, false);
            True(error is null, "Valid search must not error.");
            Equal(4, all.Count);
            var (whole, _) = EditorFindEngine.SearchRegex(source, "foo", false, true);
            Equal(3, whole.Count);
            var (caseSensitive, _) = EditorFindEngine.SearchRegex(source, "foo", true, true);
            Equal(1, caseSensitive.Count);
            var (inSelection, _) = EditorFindEngine.SearchRegex(source, "foo", false, true, 4, 14);
            Equal(1, inSelection.Count);
            var (invalid, invalidError) = EditorFindEngine.SearchRegex(source, "[", true, false);
            Equal(0, invalid.Count);
            True(!string.IsNullOrEmpty(invalidError), "Invalid regex must be reported, not thrown.");
            var (captures, _) = EditorFindEngine.SearchRegex("foo123", @"([a-z]+)(\d+)", true, false);
            Equal(1, captures.Count);
            Equal("123-foo", EditorFindEngine.ExpandReplacement("foo123", "$2-$1", captures[0].RegexMatch, false));
            Equal("BAR", EditorFindEngine.ExpandReplacement("FOO", "bar", null, true));
            Equal("Bar", EditorFindEngine.ExpandReplacement("Foo", "bar", null, true));
        });

        Run("method profiler: off-path, nested inclusive/exclusive, allocation, reset, export", () =>
        {
            var profiler = new EditorMethodProfiler();
            True(profiler.Measure("off") is null, "Disabled profiler must allocate no scope.");
            profiler.Enabled = true;
            using (profiler.Measure("outer"))
            {
                using (profiler.Measure("inner"))
                {
                    _ = new byte[128];
                }
            }
            var snap = profiler.Snapshot();
            Equal(2, snap.Methods.Length);
            var outer = snap.Methods.Single(m => m.Name == "outer");
            var inner = snap.Methods.Single(m => m.Name == "inner");
            Equal(1L, outer.Calls);
            Equal(1L, inner.Calls);
            True(outer.TotalInclusiveMs >= outer.TotalExclusiveMs, "Inclusive timing must be >= self timing.");
            True(outer.TotalInclusiveMs >= inner.TotalInclusiveMs, "Parent must include inner timing.");
            True(outer.TotalAllocatedBytes >= outer.ExclusiveAllocatedBytes, "Nested allocation cannot exceed inclusive allocation.");
            // A method name with a comma and quotes must remain one valid CSV field.
            using (profiler.Measure("csv, \"quoted\"")) { }
            var path = Path.Combine(Path.GetTempPath(), "monaconative-profile-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var files = profiler.Export(path, reset: true);
                True(File.Exists(files.Json) && File.Exists(files.Csv), "Profile files missing.");
                True(File.ReadAllText(files.Json).Contains("TotalExclusiveMs", StringComparison.Ordinal), "JSON fields missing.");
                var csvContent = File.ReadAllText(files.Csv);
                True(csvContent.Contains("inner", StringComparison.Ordinal), "CSV entry missing.");
                True(csvContent.Contains("\"csv, \"\"quoted\"\"\",", StringComparison.Ordinal),
                    "CSV method names containing quotes and commas must be escaped.");
                Equal(0, profiler.Snapshot().Methods.Length);
            }
            finally { Directory.Delete(path, recursive: true); }
        });

        Run("single-line typing refreshes one minimap line", () =>
        {
            var lines = new List<string> { "alpha", "  beta", "gamma", "delta" };
            var cache = new MinimapLineCache();
            var reads = 0;
            cache.Ensure(lines.Count, line => { reads++; return lines[line - 1]; }, 1);
            Equal(4, reads);

            lines[1] = "    beta++";
            cache.ApplyChange(2, 2, 2, lines.Count, 2, fullInvalidation: false);
            reads = 0;
            cache.Ensure(lines.Count, line => { reads++; return lines[line - 1]; }, 2);
            Equal(1, reads);
            Equal(new MinimapLineSummary(10, 4), cache.Lines[1]);
        });

        Run("minimap previews preserve indentation and token gaps without rescanning on cache hits", () =>
        {
            var lines = new List<string> { "    const answer = 42;", "// explanatory comment" };
            var cache = new MinimapLineCache();
            var reads = 0;
            cache.Ensure(lines.Count, line => { reads++; return lines[line - 1]; }, 1);
            var first = cache.PreviewAt(0);
            True(first.Count >= 4, "Expected visual runs for words/operators/numbers.");
            Equal(4, first[0].Start);
            True(first[0].Length < first[first.Count - 1].Start - first[0].Start,
                "Spaces between tokens should survive as gaps.");
            Equal((byte)2, cache.PreviewAt(1)[0].Kind);
            var afterFirstRenderReads = reads;
            cache.PreviewAt(0);
            Equal(afterFirstRenderReads, reads);
            lines[0] = "  let result = 9;";
            cache.ApplyChange(1, 1, 1, lines.Count, 2, fullInvalidation: false);
            cache.Ensure(lines.Count, line => { reads++; return lines[line - 1]; }, 2);
            Equal(2, cache.PreviewAt(0)[0].Start);
        });

        Run("line insertion splices minimap cache", () =>
        {
            var lines = new List<string> { "a", "  B", "    X", "c", "d" };
            var cache = Seed(new[] { "a", "b", "c", "d" }, 10);
            cache.ApplyChange(2, 2, 3, lines.Count, 11, fullInvalidation: false);
            var reads = 0;
            cache.Ensure(lines.Count, line => { reads++; return lines[line - 1]; }, 11);
            Equal(2, reads);
            Equal(5, cache.Lines.Count);
            Equal(new MinimapLineSummary(3, 2), cache.Lines[1]);
            Equal(new MinimapLineSummary(5, 4), cache.Lines[2]);
            Equal(new MinimapLineSummary(1, 0), cache.Lines[3]);
        });

        Run("pending dirty range shifts across earlier structural edit", () =>
        {
            var cache = Seed(new[] { "a", "b", "c", "d", "e" }, 20);
            cache.ApplyChange(5, 5, 5, 5, 21, fullInvalidation: false); // line 5 pending
            cache.ApplyChange(2, 2, 3, 6, 22, fullInvalidation: false); // insert before it
            var lines = new List<string> { "a", "B", "X", "c", "d", "EE" };
            var touched = new List<int>();
            cache.Ensure(lines.Count, line => { touched.Add(line); return lines[line - 1]; }, 22);
            True(touched.SequenceEqual(new[] { 2, 3, 4, 5, 6 }), "Expected a conservative merged dirty interval after structural shift.");
            Equal(new MinimapLineSummary(2, 0), cache.Lines[5]);
        });

        Run("full invalidation rebuilds all lines", () =>
        {
            var cache = Seed(new[] { "a", "b", "c" }, 30);
            cache.ApplyChange(1, 1, 1, 3, 31, fullInvalidation: true);
            var lines = new[] { "aa", "bb", "cc" };
            var reads = 0;
            cache.Ensure(lines.Length, line => { reads++; return lines[line - 1]; }, 31);
            Equal(3, reads);
        });



        Run("minimap row cache makes steady scrolling document-size independent", () =>
        {
            var summaries = Enumerable.Range(1, 100_000)
                .Select(line => new MinimapLineSummary(20 + (line % 80), line % 12))
                .ToArray();
            var rows = new MinimapRowCache();
            rows.Ensure(summaries, 900);
            rows.ResetStatistics();
            for (var i = 0; i < 1_000; ++i)
                rows.Ensure(summaries, 900);
            var stats = rows.Statistics;
            Equal(1_000L, stats.CacheHits);
            Equal(0L, stats.RowsRebuilt);
            Equal(0L, stats.SourceLinesScanned);
        });

        Run("single-line minimap update rebuilds one compressed row", () =>
        {
            var summaries = Enumerable.Range(1, 100_000)
                .Select(line => new MinimapLineSummary(20 + (line % 80), line % 12))
                .ToArray();
            var rows = new MinimapRowCache();
            rows.Ensure(summaries, 900);
            rows.ResetStatistics();
            rows.NotifyModelChanged(50_000, 50_000, 50_000, 100_000, fullInvalidation: false);
            rows.Ensure(summaries, 900);
            var stats = rows.Statistics;
            Equal(1L, stats.RowsRebuilt);
            True(stats.SourceLinesScanned <= 120, $"Expected <= 120 source summaries, got {stats.SourceLinesScanned}.");
        });

        Run("line range index prunes sparse decorations", () =>
        {
            var items = Enumerable.Range(0, 10_000)
                .Select(i => new TestInterval(i, i * 10 + 1, i * 10 + 2))
                .ToArray();
            var index = new LineRangeIndex<TestInterval>(items, static item => (item.Start, item.End));
            var matches = index.Query(50_001, 50_005);
            Equal(1, matches.Count);
            Equal(5_000, matches[0].Id);
            True(index.Statistics.LastNodesVisited <= 32,
                $"Expected logarithmic sparse lookup, visited {index.Statistics.LastNodesVisited} nodes.");
        });

        Run("line range index returns multi-line overlaps in source order", () =>
        {
            var items = new[]
            {
                new TestInterval(10, 8, 20),
                new TestInterval(11, 1, 3),
                new TestInterval(12, 12, 12),
                new TestInterval(13, 5, 9),
            };
            var index = new LineRangeIndex<TestInterval>(items, static item => (item.Start, item.End));
            var matches = index.Query(9, 12);
            True(matches.Select(item => item.Id).SequenceEqual(new[] { 10, 12, 13 }),
                "Expected only overlapping intervals and caller snapshot order.");
        });

        Console.WriteLine(_failed == 0 ? "Renderer logic tests PASS" : $"Renderer logic tests FAIL: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static MinimapLineCache Seed(IReadOnlyList<string> lines, ulong version)
    {
        var cache = new MinimapLineCache();
        cache.Ensure(lines.Count, line => lines[line - 1], version);
        return cache;
    }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failed++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
