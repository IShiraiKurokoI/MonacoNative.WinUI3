using System.Diagnostics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using MonacoNative.Renderer.WinUI3;

internal readonly record struct TestInterval(int Id, int Start, int End);

internal sealed record BenchmarkResult(
    string Name,
    Dictionary<string, double> Metrics,
    Dictionary<string, long> Counts,
    ulong Checksum);

internal sealed record BenchmarkDocument(
    int SchemaVersion,
    string Suite,
    string Profile,
    string MonacoEditorVersion,
    string VscodeRef,
    bool SoftwareRenderer,
    IReadOnlyList<BenchmarkResult> Results);

internal static class Program
{
    private const int DocumentLines = 100_000;
    private const int VisibleLines = 60;

    public static int Main(string[] args)
    {
        Console.WriteLine("[RendererBenchmarks] Main entered");
        try
        {
            var profile = GetArg(args, "--profile") ?? "quick";
            if (profile is not ("quick" or "full"))
                throw new ArgumentException("--profile must be quick or full");
            var output = GetArg(args, "--output") ?? Path.Combine("artifacts", "benchmarks", $"renderer-{profile}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

            Console.WriteLine("[RendererBenchmarks] Creating software CanvasDevice");
            using var device = new CanvasDevice(forceSoftwareRenderer: true);
            Console.WriteLine("[RendererBenchmarks] CanvasDevice created");
            using var format = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 14,
                WordWrapping = CanvasWordWrapping.NoWrap,
            };

            var results = new List<BenchmarkResult>
            {
                BenchmarkIsolatedViewPartDirty(device),
                BenchmarkDecorationViewPartDirty(device),
                BenchmarkBracketViewPartDirty(device),
                BenchmarkSyntheticScrollFrames(device, format, profile == "full" ? 400 : 120),
                BenchmarkViewportScrollLayouts(device, format, profile == "full" ? 800 : 200),
                BenchmarkResizeVisibleOnly(device, format, profile == "full" ? 240 : 80),
                BenchmarkMinimapSteadyScroll(profile == "full" ? 4_000 : 1_000),
                BenchmarkMinimapSingleLineUpdate(),
                BenchmarkSparseVisibleRangeQueries(profile == "full" ? 4_000 : 1_000),
            };

            var document = new BenchmarkDocument(
                1,
                "renderer-windows",
                profile,
                "0.57.0",
                "6a598d4a13031703d483d103c1d934a36ad27971",
                device.ForceSoftwareRenderer,
                results);
            File.WriteAllText(output, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            foreach (var result in results)
                Console.WriteLine($"{result.Name}: {string.Join(", ", result.Counts.Select(pair => $"{pair.Key}={pair.Value}"))}");
            Console.WriteLine($"Renderer benchmark result: {Path.GetFullPath(output)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Renderer benchmark FAIL: {ex}");
            return 1;
        }
    }

    private static BenchmarkResult BenchmarkIsolatedViewPartDirty(CanvasDevice device)
    {
        static void Draw(CanvasDrawingSession ds, EditorViewPartContext context) =>
            ds.FillRectangle(0, 0, 4, 4, Microsoft.UI.Colors.White);

        using var selection = new SelectionViewPart(Draw);
        using var decorations = new DecorationsViewPart(Draw);
        using var diagnostics = new DiagnosticsViewPart(Draw);
        using var brackets = new BracketViewPart(Draw);
        using var overview = new OverviewViewPart(Draw);
        using var caret = new CaretViewPart(Draw);
        var context = new EditorViewPartContext(10, 69, 198, 0, 1200, 900);

        selection.Prepare(device, context);
        decorations.Prepare(device, context);
        diagnostics.Prepare(device, context);
        brackets.Prepare(device, context);
        overview.Prepare(device, context);
        caret.Prepare(device, context);
        selection.ResetStatistics();
        decorations.ResetStatistics();
        diagnostics.ResetStatistics();
        brackets.ResetStatistics();
        overview.ResetStatistics();
        caret.ResetStatistics();

        var start = Stopwatch.GetTimestamp();
        diagnostics.Invalidate();
        selection.Prepare(device, context);
        decorations.Prepare(device, context);
        diagnostics.Prepare(device, context);
        brackets.Prepare(device, context);
        overview.Prepare(device, context);
        caret.Prepare(device, context);
        var elapsed = ElapsedMilliseconds(start);

        var s = selection.Statistics;
        var d = decorations.Statistics;
        var g = diagnostics.Statistics;
        var b = brackets.Statistics;
        var o = overview.Statistics;
        var c = caret.Statistics;
        return new BenchmarkResult(
            "viewparts.diagnostics_isolated_dirty",
            new() { ["prepare_elapsed_ms"] = elapsed },
            new()
            {
                ["selection_rebuilds"] = s.Rebuilds,
                ["decorations_rebuilds"] = d.Rebuilds,
                ["diagnostics_rebuilds"] = g.Rebuilds,
                ["bracket_rebuilds"] = b.Rebuilds,
                ["overview_rebuilds"] = o.Rebuilds,
                ["caret_rebuilds"] = c.Rebuilds,
                ["cache_hits"] = s.CacheHits + d.CacheHits + g.CacheHits + b.CacheHits + o.CacheHits + c.CacheHits,
            },
            (ulong)(s.CacheHits * 3 + d.CacheHits * 5 + g.Rebuilds * 7 + b.CacheHits * 11 + o.CacheHits * 13 + c.CacheHits * 17));
    }

    private static BenchmarkResult BenchmarkDecorationViewPartDirty(CanvasDevice device)
    {
        static void Draw(CanvasDrawingSession ds, EditorViewPartContext context) =>
            ds.DrawRectangle(1, 1, 5, 5, Microsoft.UI.Colors.White, 1);

        using var selection = new SelectionViewPart(Draw);
        using var decorations = new DecorationsViewPart(Draw);
        using var diagnostics = new DiagnosticsViewPart(Draw);
        using var brackets = new BracketViewPart(Draw);
        using var overview = new OverviewViewPart(Draw);
        using var caret = new CaretViewPart(Draw);
        var context = new EditorViewPartContext(20, 79, 418, 0, 1200, 900);
        EditorCommandListViewPart[] parts = { selection, decorations, diagnostics, brackets, overview, caret };
        foreach (var part in parts) part.Prepare(device, context);
        foreach (var part in parts) part.ResetStatistics();

        var start = Stopwatch.GetTimestamp();
        decorations.Invalidate();
        foreach (var part in parts) part.Prepare(device, context);
        var elapsed = ElapsedMilliseconds(start);
        return new BenchmarkResult(
            "viewparts.decorations_isolated_dirty",
            new() { ["prepare_elapsed_ms"] = elapsed },
            new()
            {
                ["selection_rebuilds"] = selection.Statistics.Rebuilds,
                ["decorations_rebuilds"] = decorations.Statistics.Rebuilds,
                ["diagnostics_rebuilds"] = diagnostics.Statistics.Rebuilds,
                ["bracket_rebuilds"] = brackets.Statistics.Rebuilds,
                ["overview_rebuilds"] = overview.Statistics.Rebuilds,
                ["caret_rebuilds"] = caret.Statistics.Rebuilds,
                ["cache_hits"] = parts.Sum(part => part.Statistics.CacheHits),
            },
            (ulong)(decorations.Statistics.Rebuilds * 17 + selection.Statistics.CacheHits * 19));
    }

    private static BenchmarkResult BenchmarkBracketViewPartDirty(CanvasDevice device)
    {
        static void Draw(CanvasDrawingSession ds, EditorViewPartContext context) =>
            ds.DrawRectangle(2, 2, 6, 6, Microsoft.UI.Colors.White, 1);

        using var selection = new SelectionViewPart(Draw);
        using var decorations = new DecorationsViewPart(Draw);
        using var diagnostics = new DiagnosticsViewPart(Draw);
        using var brackets = new BracketViewPart(Draw);
        using var overview = new OverviewViewPart(Draw);
        using var caret = new CaretViewPart(Draw);
        var context = new EditorViewPartContext(20, 79, 418, 0, 1200, 900);
        EditorCommandListViewPart[] parts = { selection, decorations, diagnostics, brackets, overview, caret };
        foreach (var part in parts) part.Prepare(device, context);
        foreach (var part in parts) part.ResetStatistics();

        var start = Stopwatch.GetTimestamp();
        brackets.Invalidate();
        foreach (var part in parts) part.Prepare(device, context);
        var elapsed = ElapsedMilliseconds(start);
        return new BenchmarkResult(
            "viewparts.bracket_isolated_dirty",
            new() { ["prepare_elapsed_ms"] = elapsed },
            new()
            {
                ["selection_rebuilds"] = selection.Statistics.Rebuilds,
                ["decorations_rebuilds"] = decorations.Statistics.Rebuilds,
                ["diagnostics_rebuilds"] = diagnostics.Statistics.Rebuilds,
                ["bracket_rebuilds"] = brackets.Statistics.Rebuilds,
                ["overview_rebuilds"] = overview.Statistics.Rebuilds,
                ["caret_rebuilds"] = caret.Statistics.Rebuilds,
                ["cache_hits"] = parts.Sum(part => part.Statistics.CacheHits),
            },
            (ulong)(brackets.Statistics.Rebuilds * 23 + overview.Statistics.CacheHits * 29));
    }

    private static BenchmarkResult BenchmarkSyntheticScrollFrames(CanvasDevice device, CanvasTextFormat format, int frames)
    {
        static void DrawChrome(CanvasDrawingSession ds, EditorViewPartContext context) =>
            ds.FillRectangle(0, 0, 3, 3, Microsoft.UI.Colors.Gray);

        using var selection = new SelectionViewPart(DrawChrome);
        using var decorations = new DecorationsViewPart(DrawChrome);
        using var diagnostics = new DiagnosticsViewPart(DrawChrome);
        using var brackets = new BracketViewPart(DrawChrome);
        using var overview = new OverviewViewPart(DrawChrome);
        using var caret = new CaretViewPart(DrawChrome);
        using var cache = new LineLayoutCache(device, format, capacity: 1024);
        using var target = new CanvasRenderTarget(device, 1200, 900, 96);
        ulong checksum = 0;

        var start = Stopwatch.GetTimestamp();
        for (var frame = 0; frame < frames; ++frame)
        {
            var first = 1 + frame * 3;
            var context = new EditorViewPartContext(first, first + VisibleLines - 1, frame * 66, 0, 1200, 900);
            selection.Prepare(device, context);
            decorations.Prepare(device, context);
            diagnostics.Prepare(device, context);
            brackets.Prepare(device, context);
            overview.Prepare(device, context);
            caret.Prepare(device, context);

            using var ds = target.CreateDrawingSession();
            ds.Clear(Microsoft.UI.Colors.Black);
            for (var line = first; line < first + VisibleLines; ++line)
            {
                var layout = cache.GetOrCreate(line, LineText(line), 22);
                ds.DrawTextLayout(layout, 64, (line - first) * 14, Microsoft.UI.Colors.White);
                checksum += (ulong)line;
            }
            selection.Composite(ds);
            decorations.Composite(ds);
            diagnostics.Composite(ds);
            brackets.Composite(ds);
            overview.Composite(ds);
            caret.Composite(ds);
        }
        var elapsed = ElapsedMilliseconds(start);
        var layoutStats = cache.Statistics;
        var viewPartRebuilds = selection.Statistics.Rebuilds + decorations.Statistics.Rebuilds +
            diagnostics.Statistics.Rebuilds + brackets.Statistics.Rebuilds +
            overview.Statistics.Rebuilds + caret.Statistics.Rebuilds;
        return new BenchmarkResult(
            "renderer.synthetic_scroll_frames",
            new()
            {
                ["elapsed_ms"] = elapsed,
                ["ms_per_frame"] = elapsed / frames,
            },
            new()
            {
                ["document_lines"] = DocumentLines,
                ["frames"] = frames,
                ["visible_lines_per_frame"] = VisibleLines,
                ["layout_requests"] = layoutStats.Requests,
                ["layout_misses"] = layoutStats.Misses,
                ["viewpart_rebuilds"] = viewPartRebuilds,
                ["viewpart_rebuilds_per_frame"] = viewPartRebuilds / frames,
            },
            checksum);
    }

    private static BenchmarkResult BenchmarkViewportScrollLayouts(CanvasDevice device, CanvasTextFormat format, int steps)
    {
        using var cache = new LineLayoutCache(device, format, capacity: 1024);
        var checksum = 0UL;
        var start = Stopwatch.GetTimestamp();
        for (var step = 0; step < steps; ++step)
        {
            var first = 1 + step * 3;
            for (var line = first; line < first + VisibleLines; ++line)
            {
                var layout = cache.GetOrCreate(line, LineText(line), 22);
                checksum += (ulong)Math.Round(layout.LayoutBoundsIncludingTrailingWhitespace.Width);
            }
        }
        var elapsed = ElapsedMilliseconds(start);
        var stats = cache.Statistics;
        return new BenchmarkResult(
            "layout.viewport_scroll_reuse",
            new()
            {
                ["elapsed_ms"] = elapsed,
                ["us_per_viewport"] = elapsed * 1000.0 / steps,
            },
            new()
            {
                ["document_lines"] = DocumentLines,
                ["viewport_lines"] = VisibleLines,
                ["viewports"] = steps,
                ["requests"] = stats.Requests,
                ["hits"] = stats.Hits,
                ["misses"] = stats.Misses,
                ["evictions"] = stats.Evictions,
            },
            checksum);
    }

    private static BenchmarkResult BenchmarkResizeVisibleOnly(CanvasDevice device, CanvasTextFormat format, int iterations)
    {
        using var cache = new LineLayoutCache(device, format, capacity: 512);
        var checksum = 0UL;
        var start = Stopwatch.GetTimestamp();
        for (var iteration = 0; iteration < iterations; ++iteration)
        {
            var lineHeight = (iteration & 1) == 0 ? 22f : 23f;
            for (var line = 50_000; line < 50_000 + VisibleLines; ++line)
            {
                var layout = cache.GetOrCreate(line, LineText(line), lineHeight);
                checksum += (ulong)Math.Round(layout.LayoutBounds.Height);
            }
        }
        var elapsed = ElapsedMilliseconds(start);
        var stats = cache.Statistics;
        return new BenchmarkResult(
            "layout.resize_visible_only",
            new() { ["elapsed_ms"] = elapsed },
            new()
            {
                ["document_lines"] = DocumentLines,
                ["viewport_lines"] = VisibleLines,
                ["resize_iterations"] = iterations,
                ["requests"] = stats.Requests,
                ["misses"] = stats.Misses,
                ["current_entries"] = stats.CurrentEntries,
            },
            checksum);
    }

    private static BenchmarkResult BenchmarkMinimapSteadyScroll(int iterations)
    {
        var lines = Enumerable.Range(1, DocumentLines)
            .Select(line => new MinimapLineSummary(20 + line % 80, line % 12))
            .ToArray();
        var rows = new MinimapRowCache();
        rows.Ensure(lines, 900);
        rows.ResetStatistics();

        var start = Stopwatch.GetTimestamp();
        ulong checksum = 0;
        for (var i = 0; i < iterations; ++i)
        {
            rows.Ensure(lines, 900);
            checksum += (ulong)rows.Rows[(i * 17) % rows.Rows.Count].MaxLength;
        }
        var elapsed = ElapsedMilliseconds(start);
        var stats = rows.Statistics;
        return new BenchmarkResult(
            "minimap.steady_scroll_cached_rows",
            new()
            {
                ["elapsed_ms"] = elapsed,
                ["us_per_scroll"] = elapsed * 1000.0 / iterations,
            },
            new()
            {
                ["document_lines"] = DocumentLines,
                ["minimap_rows"] = rows.Rows.Count,
                ["scroll_iterations"] = iterations,
                ["cache_hits"] = stats.CacheHits,
                ["rows_rebuilt"] = stats.RowsRebuilt,
                ["source_lines_scanned"] = stats.SourceLinesScanned,
            },
            checksum);
    }

    private static BenchmarkResult BenchmarkMinimapSingleLineUpdate()
    {
        var texts = Enumerable.Range(1, DocumentLines).Select(LineText).ToArray();
        var lines = new MinimapLineCache();
        lines.Ensure(texts.Length, line => texts[line - 1], 1);
        var rows = new MinimapRowCache();
        rows.Ensure(lines.Lines, 900);
        lines.ResetStatistics();
        rows.ResetStatistics();

        texts[49_999] = "        changed-middle-line-with-extra-content";
        lines.ApplyChange(50_000, 50_000, 50_000, DocumentLines, 2, fullInvalidation: false);
        rows.NotifyModelChanged(50_000, 50_000, 50_000, DocumentLines, fullInvalidation: false);
        var start = Stopwatch.GetTimestamp();
        lines.Ensure(DocumentLines, line => texts[line - 1], 2);
        rows.Ensure(lines.Lines, 900);
        var elapsed = ElapsedMilliseconds(start);
        var lineStats = lines.Statistics;
        var rowStats = rows.Statistics;
        return new BenchmarkResult(
            "minimap.single_line_incremental",
            new() { ["elapsed_ms"] = elapsed },
            new()
            {
                ["document_lines"] = DocumentLines,
                ["line_summaries_rebuilt"] = lineStats.LinesSummarized,
                ["rows_rebuilt"] = rowStats.RowsRebuilt,
                ["source_lines_scanned"] = rowStats.SourceLinesScanned,
            },
            (ulong)(lines.Lines[49_999].Length * 131 + rows.Rows[449].MaxLength));
    }

    private static BenchmarkResult BenchmarkSparseVisibleRangeQueries(int iterations)
    {
        var items = Enumerable.Range(0, 100_000)
            .Select(i => new TestInterval(i, i * 8 + 1, i * 8 + 2))
            .ToArray();
        var index = new LineRangeIndex<TestInterval>(items, static item => (item.Start, item.End));
        index.ResetStatistics();
        ulong checksum = 0;
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; ++i)
        {
            var first = 1 + (i * 97) % 700_000;
            var matches = index.Query(first, first + 79);
            foreach (var match in matches)
                checksum += (ulong)match.Id;
        }
        var elapsed = ElapsedMilliseconds(start);
        var stats = index.Statistics;
        return new BenchmarkResult(
            "ranges.visible_query_sparse",
            new()
            {
                ["elapsed_ms"] = elapsed,
                ["us_per_query"] = elapsed * 1000.0 / iterations,
            },
            new()
            {
                ["intervals"] = items.Length,
                ["queries"] = stats.QueryCount,
                ["nodes_visited"] = stats.NodesVisited,
                ["matches_returned"] = stats.MatchesReturned,
                ["last_nodes_visited"] = stats.LastNodesVisited,
            },
            checksum);
    }

    private static string LineText(int line) => $"    line {line:D6} alpha beta gamma delta epsilon";

    private static double ElapsedMilliseconds(long start) =>
        (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; ++i)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
