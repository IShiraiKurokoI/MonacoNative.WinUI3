using System.Text;
using System.Text.RegularExpressions;

namespace MonacoNative.I18n;

/// <summary>Deterministic layout-stress pseudo localization. Format placeholders remain intact.</summary>
public static partial class PseudoLocalizer
{
    private static readonly IReadOnlyDictionary<char, char> AccentMap = new Dictionary<char, char>
    {
        ['A']='Å', ['B']='Ɓ', ['C']='Ç', ['D']='Ð', ['E']='Ë', ['F']='Ƒ', ['G']='Ğ', ['H']='Ħ', ['I']='Ï',
        ['J']='Ĵ', ['K']='Ķ', ['L']='Ŀ', ['M']='Ḿ', ['N']='Ñ', ['O']='Ø', ['P']='Þ', ['Q']='Ǫ', ['R']='Ŕ',
        ['S']='Š', ['T']='Ŧ', ['U']='Û', ['V']='Ṽ', ['W']='Ŵ', ['X']='Ẋ', ['Y']='Ÿ', ['Z']='Ž',
        ['a']='å', ['b']='ƀ', ['c']='ç', ['d']='ð', ['e']='ë', ['f']='ƒ', ['g']='ğ', ['h']='ħ', ['i']='ï',
        ['j']='ĵ', ['k']='ķ', ['l']='ŀ', ['m']='ḿ', ['n']='ñ', ['o']='ø', ['p']='þ', ['q']='ǫ', ['r']='ŕ',
        ['s']='š', ['t']='ŧ', ['u']='û', ['v']='ṽ', ['w']='ŵ', ['x']='ẋ', ['y']='ÿ', ['z']='ž',
    };

    public static string Transform(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new StringBuilder(source.Length * 2 + 8);
        result.Append("[!! ");
        var cursor = 0;
        foreach (Match match in PlaceholderRegex().Matches(source))
        {
            AppendPseudo(result, source.AsSpan(cursor, match.Index - cursor));
            result.Append(match.Value);
            cursor = match.Index + match.Length;
        }
        AppendPseudo(result, source.AsSpan(cursor));
        result.Append(" !!]");
        return result.ToString();
    }

    private static void AppendPseudo(StringBuilder target, ReadOnlySpan<char> text)
    {
        foreach (var ch in text)
        {
            target.Append(AccentMap.TryGetValue(ch, out var mapped) ? mapped : ch);
            if (ch is 'a' or 'e' or 'i' or 'o' or 'u' or 'A' or 'E' or 'I' or 'O' or 'U')
                target.Append('~');
        }
    }

    [GeneratedRegex(@"\{\d+(?:[^{}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}
