using MonacoNative.Accessibility;
using MonacoNative.I18n;

var tests = new (string Name, Action Body)[]
{
    ("character units are grapheme-safe", CharacterUnits),
    ("CRLF is one character navigation unit", CrLfUnit),
    ("line and word expansion", LineAndWordExpansion),
    ("find text and endpoint comparison", FindAndCompare),
    ("range movement stays normalized under randomized operations", RandomizedRanges),
    ("live range clamps after document shrink", LiveDocumentClamp),
    ("pseudo locale preserves placeholders and expands labels", PseudoLocale),
};

var failures = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex}");
    }
}

Console.WriteLine($"AccessibilityTests: {tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void CharacterUnits()
{
    const string text = "A😀e\u0301👩‍💻中";
    var token = new object();
    var emoji = new AccessibleTextRange(() => text, token, 2, 2); // inside 😀 surrogate pair
    emoji.ExpandToEnclosingUnit(AccessibleTextUnit.Character);
    Check.Equal("😀", emoji.GetText(), "surrogate pair must expand as one character");

    var combining = new AccessibleTextRange(() => text, token, 4, 4);
    combining.ExpandToEnclosingUnit(AccessibleTextUnit.Character);
    Check.Equal("e\u0301", combining.GetText(), "combining sequence must remain intact");

    var zwjStart = text.IndexOf("👩", StringComparison.Ordinal);
    var zwj = new AccessibleTextRange(() => text, token, zwjStart + 1, zwjStart + 1);
    zwj.ExpandToEnclosingUnit(AccessibleTextUnit.Character);
    Check.Equal("👩‍💻", zwj.GetText(), "ZWJ sequence must remain intact");
}

static void CrLfUnit()
{
    const string text = "ab\r\ncd";
    var token = new object();
    var caret = new AccessibleTextRange(() => text, token, 2, 2);
    caret.ExpandToEnclosingUnit(AccessibleTextUnit.Character);
    Check.Equal("\r\n", caret.GetText(), "CRLF character expansion");
    caret.Set(2, 2);
    var moved = caret.MoveEndpointByUnit(AccessibleTextEndpoint.End, AccessibleTextUnit.Character, 1);
    Check.Equal(1, moved, "one CRLF navigation step");
    Check.Equal(4, caret.End, "CRLF must advance two UTF-16 units in one step");
}

static void LineAndWordExpansion()
{
    const string text = "alpha_beta 42\r\n第二行!\nlast";
    var token = new object();
    var word = new AccessibleTextRange(() => text, token, 3, 3);
    word.ExpandToEnclosingUnit(AccessibleTextUnit.Word);
    Check.Equal("alpha_beta", word.GetText(), "identifier word expansion");

    var second = text.IndexOf("第二", StringComparison.Ordinal);
    var line = new AccessibleTextRange(() => text, token, second, second + 1);
    line.ExpandToEnclosingUnit(AccessibleTextUnit.Line);
    Check.Equal("第二行!\n", line.GetText(), "line unit includes its EOL");
}

static void FindAndCompare()
{
    const string text = "one two ONE two";
    var token = new object();
    var all = new AccessibleTextRange(() => text, token, 0, text.Length);
    var forward = all.FindText("ONE", false, true)!;
    Check.Equal("one", forward.GetText(), "ignore-case forward find picks first match");
    var backward = all.FindText("two", true, false)!;
    Check.Equal(text.LastIndexOf("two", StringComparison.Ordinal), backward.Start, "backward find");
    Check.True(!forward.Compare(backward), "different ranges compare false");
    Check.True(forward.CompareEndpoints(AccessibleTextEndpoint.Start, backward, AccessibleTextEndpoint.Start) < 0,
        "endpoint ordering");
}

static void RandomizedRanges()
{
    var random = new Random(0x51A11);
    var samples = new[]
    {
        "abc def\r\nghi",
        "中文 输入😀 test",
        "e\u0301 café\n👩‍💻 code",
        "a\r\nb\nc\rd",
        "旗🇨🇳标点,word_2",
    };
    foreach (var text in samples)
    {
        var token = new object();
        for (var round = 0; round < 1000; ++round)
        {
            var a = random.Next(0, text.Length + 1);
            var b = random.Next(0, text.Length + 1);
            var range = new AccessibleTextRange(() => text, token, a, b);
            var unit = (AccessibleTextUnit)random.Next(0, 7);
            if (random.Next(2) == 0)
                range.ExpandToEnclosingUnit(unit);
            else
                range.MoveEndpointByUnit(random.Next(2) == 0 ? AccessibleTextEndpoint.Start : AccessibleTextEndpoint.End,
                    unit, random.Next(-4, 5));
            Check.True(range.Start >= 0 && range.Start <= range.End && range.End <= text.Length,
                "randomized range must remain ordered and bounded");
            _ = range.GetText(random.Next(-1, 8));
        }
    }
}

static void LiveDocumentClamp()
{
    var text = "abcdef";
    var token = new object();
    var range = new AccessibleTextRange(() => text, token, 2, 6);
    text = "abc";
    Check.Equal(3, range.End, "live range clamps to current document length");
    Check.Equal("c", range.GetText(), "clamped text remains valid");
}

static void PseudoLocale()
{
    var i18n = new MonacoLocalizationService("qps-ploc");
    Check.Equal("qps-ploc", i18n.CultureName, "pseudo locale must remain selectable");
    var source = new MonacoLocalizationService("en-US").GetString("monacoNative.sample.status");
    var pseudo = i18n.GetString("monacoNative.sample.status");
    Check.True(pseudo.Length > source.Length, "pseudo label should expand layout width");
    foreach (var placeholder in new[] { "{0}", "{1}", "{2}", "{3}", "{4}" })
        Check.True(pseudo.Contains(placeholder, StringComparison.Ordinal), $"pseudo locale preserves {placeholder}");
    Check.True(pseudo.StartsWith("[!! ", StringComparison.Ordinal) && pseudo.EndsWith(" !!]", StringComparison.Ordinal),
        "pseudo locale is visually obvious");
}


static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
    }
}
