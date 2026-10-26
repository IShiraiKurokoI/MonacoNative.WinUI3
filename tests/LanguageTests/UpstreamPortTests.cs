// Copyright (c) Microsoft Corporation. All rights reserved.
// MIT; see THIRD_PARTY_NOTICES.md. Tests retain upstream inputs and expected results.
// Each file documents its exact source coverage; this is not the entire upstream suite.
using MonacoNative.Language;

static partial class UpstreamPortTests
{
    private static int _cases;
    private static readonly List<Exception> Failures = new();
    public static void Run()
    {
        _cases = 0;
        Failures.Clear();
        JsonScanner();
        JsonEdits();
        CssScanners();
        HtmlTags();
        Folding();
        Console.WriteLine($"UpstreamPortTests: {_cases - Failures.Count}/{_cases} cases passed.");
        if (Failures.Count != 0) throw new AggregateException("Source-ported upstream regression failures", Failures);
    }
    private static void Case(string name, Action action)
    {
        _cases++;
        try { action(); }
        catch (Exception error) { Failures.Add(new Exception(name, error)); Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
    }
    private static (string Text, int Offset) Cursor(string marked)
    {
        var offset = marked.IndexOf('|');
        if (offset < 0) throw new ArgumentException("Missing cursor marker.", nameof(marked));
        return (marked.Remove(offset, 1), offset);
    }
    private static int Start(LanguageTextSnapshot snapshot, LanguageRange range) =>
        snapshot.OffsetAt(new(range.StartLineNumber, range.StartColumn));
    private static int End(LanguageTextSnapshot snapshot, LanguageRange range) =>
        snapshot.OffsetAt(new(range.EndLineNumber, range.EndColumn));
}
