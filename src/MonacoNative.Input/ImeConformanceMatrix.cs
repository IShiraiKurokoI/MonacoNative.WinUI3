using System.Globalization;

namespace MonacoNative.Input;

public sealed record ImeConformanceCase(
    string Id,
    string InputLanguage,
    string Description,
    string Text,
    int Utf16Length,
    int TextElementCount,
    bool RequiresComposition);

/// <summary>
/// Deterministic corpus used by CI in addition to the manual Windows IME matrix.
/// Expected lengths are pinned rather than calculated so a regression in UTF-16/text-
/// element handling cannot make the test oracle change together with the implementation.
/// </summary>
public static class ImeConformanceMatrix
{
    public static IReadOnlyList<ImeConformanceCase> Cases { get; } = new[]
    {
        new ImeConformanceCase("zh-pinyin", "zh-CN", "Microsoft Pinyin committed Han text", "中文输入", 4, 4, true),
        new ImeConformanceCase("zh-wubi", "zh-CN", "Wubi committed Han text", "五笔输入", 4, 4, true),
        new ImeConformanceCase("ja-hiragana-kanji", "ja-JP", "Japanese conversion", "にほん日本", 5, 5, true),
        new ImeConformanceCase("ko-hangul", "ko-KR", "Korean Hangul composition", "한글입력", 4, 4, true),
        new ImeConformanceCase("emoji-supplementary", "und", "Supplementary-plane emoji", "A😀B", 4, 3, false),
        new ImeConformanceCase("emoji-zwj", "und", "ZWJ emoji", "👩‍💻", 5, 1, false),
        new ImeConformanceCase("emoji-flag", "und", "Regional-indicator flag", "🇨🇳", 4, 1, false),
        new ImeConformanceCase("combining", "und", "Combining mark cluster", "e\u0301", 2, 1, false),
        new ImeConformanceCase("variation-selector", "und", "Variation selector sequence", "✈️", 2, 1, false),
        new ImeConformanceCase("mixed-cjk-emoji", "zh-CN", "Mixed CJK and emoji", "你好，世界😀", 7, 6, true),
    };

    public static void Validate()
    {
        var duplicate = Cases.GroupBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() != 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate IME conformance case '{duplicate.Key}'.");
        foreach (var item in Cases)
        {
            if (item.Utf16Length != item.Text.Length)
                throw new InvalidOperationException($"UTF-16 length drift in '{item.Id}'.");
            if (item.TextElementCount != StringInfo.ParseCombiningCharacters(item.Text).Length)
                throw new InvalidOperationException($"Text-element count drift in '{item.Id}'.");
        }
    }
}
