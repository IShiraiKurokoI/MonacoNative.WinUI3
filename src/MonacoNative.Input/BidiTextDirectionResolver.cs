using System.Text;

namespace MonacoNative.Input;

public enum BidiBaseDirection
{
    LeftToRight = 0,
    RightToLeft = 1,
}

/// <summary>
/// Portable first-strong base-direction policy used by the renderer. This controls the
/// document paragraph direction only; it is intentionally independent from XAML FlowDirection.
/// </summary>
public static class BidiTextDirectionResolver
{
    public static BidiBaseDirection Resolve(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return BidiBaseDirection.LeftToRight;

        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;

            // Directional marks are strong even though they are not letters.
            if (value is 0x061C or 0x200F) // Arabic Letter Mark / Right-to-Left Mark
                return BidiBaseDirection.RightToLeft;
            if (value == 0x200E) // Left-to-Right Mark
                return BidiBaseDirection.LeftToRight;

            // European and Arabic-Indic digits, punctuation and combining marks are
            // not strong paragraph-direction characters. Only classify a script
            // range after confirming that the rune is a Unicode letter.
            if (!Rune.IsLetter(rune))
                continue;

            return IsRightToLeftScript(value)
                ? BidiBaseDirection.RightToLeft
                : BidiBaseDirection.LeftToRight;
        }
        return BidiBaseDirection.LeftToRight;
    }

    private static bool IsRightToLeftScript(int value) =>
        // Hebrew, Arabic, Syriac, Thaana, N'Ko, Samaritan, Mandaic and Arabic extensions.
        value is >= 0x0590 and <= 0x08FF ||
        // Hebrew/Arabic presentation forms.
        value is >= 0xFB1D and <= 0xFDFF ||
        value is >= 0xFE70 and <= 0xFEFF ||
        // Historic/modern RTL scripts in supplementary planes commonly encountered in text.
        value is >= 0x10840 and <= 0x1093F || // Aramaic, Palmyrene, Nabataean, Hatran, Phoenician, Lydian
        value is >= 0x10A00 and <= 0x10A9F || // Kharoshthi, Old South/North Arabian
        value is >= 0x10AC0 and <= 0x10BFF || // Manichaean, Avestan, Pahlavi, Parthian
        value is >= 0x10D00 and <= 0x10D8F || // Hanifi Rohingya and related RTL additions
        value is >= 0x10E80 and <= 0x10FFF || // Yezidi, Old Sogdian/Uyghur and neighbors
        value is >= 0x1E800 and <= 0x1E95F || // Mende Kikakui, Adlam
        value is >= 0x1EC70 and <= 0x1EEFF;   // Indic Siyaq and Arabic Mathematical Alphabetic Symbols
}
