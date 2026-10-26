// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// C# source port of microsoft/vscode-css-languageservice/src/parser/cssScanner.ts.
// Source reviewed: main, 2026-10-23. CSS scanner only: LESS/SCSS parsers and the
// CSS language-service AST/completion engine are NOT supplied by this class.
using System.Text;

namespace MonacoNative.Language.Css;

public enum CssTokenType
{
    Ident, AtKeyword, String, BadString, UnquotedString, Hash, Num, Percentage,
    Dimension, UnicodeRange, CDO, CDC, Colon, SemiColon, CurlyL, CurlyR,
    ParenthesisL, ParenthesisR, BracketL, BracketR, Whitespace, Includes,
    Dashmatch, SubstringOperator, PrefixOperator, SuffixOperator, Delim,
    EMS, EXS, Length, Angle, Time, Freq, Exclamation, Resolution, Comma,
    Charset, EscapedJavaScript, BadEscapedJavaScript, Comment, SingleLineComment,
    EOF, ContainerQueryLength, CustomToken
}
public sealed record CssToken(CssTokenType Type, string Text, int Offset, int Length);

public class CssScanner
{
    private string _text;
    private int _position;
    private readonly CancellationToken _cancellationToken;
    public bool IgnoreComment { get; set; } = true;
    public bool IgnoreWhitespace { get; set; } = true;
    public bool InUrl { get; set; }
    public int Position => _position;
    protected bool Eos => _position >= _text.Length;
    private static readonly IReadOnlyDictionary<string, CssTokenType> Units = new Dictionary<string, CssTokenType>(StringComparer.Ordinal)
    {
        ["em"] = CssTokenType.EMS, ["ex"] = CssTokenType.EXS,
        ["px"] = CssTokenType.Length, ["cm"] = CssTokenType.Length, ["mm"] = CssTokenType.Length,
        ["in"] = CssTokenType.Length, ["pt"] = CssTokenType.Length, ["pc"] = CssTokenType.Length,
        ["deg"] = CssTokenType.Angle, ["rad"] = CssTokenType.Angle, ["grad"] = CssTokenType.Angle,
        ["ms"] = CssTokenType.Time, ["s"] = CssTokenType.Time, ["hz"] = CssTokenType.Freq, ["khz"] = CssTokenType.Freq,
        ["%"] = CssTokenType.Percentage, ["fr"] = CssTokenType.Percentage,
        ["dpi"] = CssTokenType.Resolution, ["dpcm"] = CssTokenType.Resolution,
        ["cqw"] = CssTokenType.ContainerQueryLength, ["cqh"] = CssTokenType.ContainerQueryLength,
        ["cqi"] = CssTokenType.ContainerQueryLength, ["cqb"] = CssTokenType.ContainerQueryLength,
        ["cqmin"] = CssTokenType.ContainerQueryLength, ["cqmax"] = CssTokenType.ContainerQueryLength
    };
    public CssScanner(string text = "", CancellationToken cancellationToken = default)
    { _text = text ?? throw new ArgumentNullException(nameof(text)); _cancellationToken = cancellationToken; }
    public void SetSource(string text) { _text = text ?? throw new ArgumentNullException(nameof(text)); _position = 0; }
    public void GoBackTo(int position)
    {
        if (position < 0 || position > _text.Length) throw new ArgumentOutOfRangeException(nameof(position));
        _position = position;
    }
    protected int Peek(int delta = 0) => _position + delta >= 0 && _position + delta < _text.Length ? _text[_position + delta] : 0;
    protected int Next() => _position < _text.Length ? _text[_position++] : NextPastEnd();
    private int NextPastEnd() { _position++; return 0; }
    private string Slice(int start, int end)
    {
        start = Math.Clamp(start, 0, _text.Length); end = Math.Clamp(end, 0, _text.Length);
        return start <= end ? _text[start..end] : _text[end..start];
    }
    protected bool AdvanceIf(char ch)
    { if (Eos || Peek() != ch) return false; _position++; return true; }
    protected bool AdvanceIf(string text)
    {
        if (_position > _text.Length || !_text.AsSpan(_position).StartsWith(text.AsSpan(), StringComparison.Ordinal)) return false;
        _position += text.Length; return true;
    }
    protected int AdvanceWhile(Func<int, bool> predicate)
    {
        var start = _position;
        while (!Eos && predicate(Peek()))
        { _position++; if ((_position & 4095) == 0) _cancellationToken.ThrowIfCancellationRequested(); }
        return _position - start;
    }
    protected CssToken Finish(int start, CssTokenType type, string? text = null) =>
        new(type, string.IsNullOrEmpty(text) ? Slice(start, _position) : text, start, _position - start);
    public string Substring(int offset, int length) => _text.Substring(offset, length);
    private static bool IsDigit(int ch) => ch >= '0' && ch <= '9';
    private static bool IsHex(int ch) => IsDigit(ch) || ch >= 'a' && ch <= 'f' || ch >= 'A' && ch <= 'F';
    private static bool IsWhitespace(int ch) => ch is ' ' or '\t' or '\n' or '\r' or '\f';
    private static bool IsFirst(int ch) => ch == '_' || ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z' || ch >= 0x80 && ch <= 0xffff;
    private bool AddIf(StringBuilder result, Func<int, bool> predicate)
    {
        if (Eos || !predicate(Peek())) return false;
        result.Append((char)Next()); return true;
    }
    private bool Newline(StringBuilder result)
    {
        var ch = Peek();
        if (ch is not ('\r' or '\f' or '\n')) return false;
        _position++; result.Append((char)ch);
        if (ch == '\r' && AdvanceIf('\n')) result.Append('\n');
        return true;
    }
    private bool Escape(StringBuilder result, bool includeNewLines = false)
    {
        if (Peek() != '\\') return false;
        _position++;
        var count = 0; var value = 0;
        while (count < 6 && IsHex(Peek()))
        {
            var ch = Next();
            value = value * 16 + (IsDigit(ch) ? ch - '0' : ch >= 'a' ? ch - 'a' + 10 : ch - 'A' + 10);
            count++;
        }
        if (count > 0)
        {
            // Upstream uses String.fromCharCode, deliberately retaining its UTF-16 truncation.
            if (value != 0) result.Append(unchecked((char)value));
            if (Peek() is ' ' or '\t') _position++;
            else Newline(new StringBuilder());
            return true;
        }
        if (Peek() is not ('\r' or '\f' or '\n')) { result.Append((char)Next()); return true; }
        return includeNewLines && Newline(result);
    }
    private bool Name(StringBuilder result)
    {
        var matched = false;
        while (AddIf(result, ch => IsFirst(ch) || IsDigit(ch) || ch == '-') || Escape(result))
        { matched = true; _cancellationToken.ThrowIfCancellationRequested(); }
        return matched;
    }
    protected bool Ident(StringBuilder result)
    {
        var start = _position;
        var minus = AddIf(result, ch => ch == '-');
        if (minus ? AddIf(result, ch => ch == '-') || AddIf(result, IsFirst) || Escape(result)
                  : AddIf(result, IsFirst) || Escape(result))
        {
            Name(result); return true;
        }
        _position = start; return false;
    }
    private bool Number()
    {
        var lookahead = 0; var hasDot = false;
        if (Peek() is '+' or '-') lookahead++;
        if (Peek(lookahead) == '.') { lookahead++; hasDot = true; }
        if (!IsDigit(Peek(lookahead))) return false;
        _position += lookahead + 1;
        // This matches the source's predicate, including its recovery on malformed decimals.
        AdvanceWhile(ch => IsDigit(ch) || !hasDot && ch == '.');
        return true;
    }
    private CssTokenType? String(StringBuilder result)
    {
        if (Peek() is not ('\'' or '"')) return null;
        var quote = Next(); result.Append((char)quote);
        while (AddIf(result, ch => ch != 0 && ch != quote && ch is not ('\\' or '\r' or '\f' or '\n')) || Escape(result, true))
            _cancellationToken.ThrowIfCancellationRequested();
        if (Peek() == quote) { result.Append((char)Next()); return CssTokenType.String; }
        return CssTokenType.BadString;
    }
    public CssToken? ScanUnquotedString()
    {
        var start = _position; var result = new StringBuilder(); var matched = false;
        while (AddIf(result, ch => ch != 0 && ch is not ('\\' or '\'' or '"' or '(' or ')' or ' ' or '\t' or '\r' or '\n' or '\f')) || Escape(result))
        { matched = true; _cancellationToken.ThrowIfCancellationRequested(); }
        return matched ? Finish(start, CssTokenType.UnquotedString, result.ToString()) : null;
    }
    public CssToken? TryScanUnicode()
    {
        var start = _position;
        if (!Eos && AdvanceIf('+'))
        {
            var count = AdvanceWhile(IsHex) + AdvanceWhile(ch => ch == '?');
            if (count is >= 1 and <= 6)
            {
                if (!AdvanceIf('-')) return Finish(start, CssTokenType.UnicodeRange);
                count = AdvanceWhile(IsHex);
                if (count is >= 1 and <= 6) return Finish(start, CssTokenType.UnicodeRange);
            }
        }
        _position = start; return null;
    }
    protected virtual bool Comment()
    {
        if (!AdvanceIf("/*")) return false;
        var success = false; var hot = false;
        AdvanceWhile(ch =>
        {
            if (hot && ch == '/') { success = true; return false; }
            hot = ch == '*'; return true;
        });
        if (success) _position++;
        return true;
    }
    public CssToken Scan()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            var start = _position;
            if (AdvanceWhile(IsWhitespace) > 0)
            { if (!IgnoreWhitespace) return Finish(start, CssTokenType.Whitespace); }
            else if (Comment())
            { if (!IgnoreComment) return Finish(start, CssTokenType.Comment); }
            else break;
        }
        var offset = _position;
        if (Eos) return Finish(offset, CssTokenType.EOF);
        return ScanNext(offset);
    }
    protected virtual CssToken ScanNext(int offset)
    {
        if (AdvanceIf("<!--")) return Finish(offset, CssTokenType.CDO);
        if (AdvanceIf("-->")) return Finish(offset, CssTokenType.CDC);
        var content = new StringBuilder();
        if (Ident(content)) return Finish(offset, CssTokenType.Ident, content.ToString());
        if (AdvanceIf('@'))
        {
            content = new StringBuilder("@");
            if (!Name(content)) return Finish(offset, CssTokenType.Delim);
            var keyword = content.ToString();
            return Finish(offset, keyword == "@charset" ? CssTokenType.Charset : CssTokenType.AtKeyword, keyword);
        }
        if (AdvanceIf('#'))
        {
            content = new StringBuilder("#");
            return Name(content) ? Finish(offset, CssTokenType.Hash, content.ToString()) : Finish(offset, CssTokenType.Delim);
        }
        if (AdvanceIf('!')) return Finish(offset, CssTokenType.Exclamation);
        if (Number())
        {
            var end = _position; content = new StringBuilder(Slice(offset, end));
            if (AdvanceIf('%')) return Finish(offset, CssTokenType.Percentage);
            if (Ident(content))
                return Finish(offset, Units.TryGetValue(Slice(end, _position).ToLowerInvariant(), out var unit) ? unit : CssTokenType.Dimension, content.ToString());
            return Finish(offset, CssTokenType.Num);
        }
        content = new StringBuilder();
        var stringType = String(content);
        if (stringType is { } kind) return Finish(offset, kind, content.ToString());
        CssTokenType? punctuation = Peek() switch
        {
            ';' => CssTokenType.SemiColon, ':' => CssTokenType.Colon, '{' => CssTokenType.CurlyL,
            '}' => CssTokenType.CurlyR, '[' => CssTokenType.BracketL, ']' => CssTokenType.BracketR,
            '(' => CssTokenType.ParenthesisL, ')' => CssTokenType.ParenthesisR, ',' => CssTokenType.Comma, _ => null
        };
        if (punctuation is { } single) { _position++; return Finish(offset, single); }
        if (Peek(1) == '=')
        {
            CssTokenType? pair = Peek() switch
            {
                '~' => CssTokenType.Includes, '|' => CssTokenType.Dashmatch, '*' => CssTokenType.SubstringOperator,
                '^' => CssTokenType.PrefixOperator, '$' => CssTokenType.SuffixOperator, _ => null
            };
            if (pair is { } match) { _position += 2; return Finish(offset, match); }
        }
        Next(); return Finish(offset, CssTokenType.Delim);
    }
}
