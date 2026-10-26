// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// C# source port of microsoft/node-jsonc-parser/src/impl/scanner.ts.
// Source reviewed: main, 2026-10-23. This ports the scanner, NOT the schema language service.
using System.Text;

namespace MonacoNative.Language.Json;

public enum JsoncSyntaxKind
{
    OpenBraceToken = 1, CloseBraceToken, OpenBracketToken, CloseBracketToken,
    CommaToken, ColonToken, NullKeyword, TrueKeyword, FalseKeyword, StringLiteral,
    NumericLiteral, LineCommentTrivia, BlockCommentTrivia, LineBreakTrivia, Trivia,
    Unknown, EOF
}

public enum JsoncScanError
{
    None, UnexpectedEndOfComment, UnexpectedEndOfString, UnexpectedEndOfNumber,
    InvalidUnicode, InvalidEscapeCharacter, InvalidCharacter
}

/// <summary>
/// Stateful UTF-16 scanner. Token values, recovery, trivia and line tracking follow
/// the upstream scanner. Positions are offsets in the original text, not UTF-8 bytes.
/// The legacy upstream comment-value boundary is retained; use TokenOffset/TokenLength
/// to obtain the raw spelling of any token.
/// </summary>
public sealed class JsoncScanner
{
    private readonly string _text;
    private readonly bool _ignoreTrivia;
    private readonly CancellationToken _cancellationToken;
    private int _position, _lineNumber, _lineStartOffset, _tokenLineStartOffset, _previousTokenLineStartOffset;
    public JsoncScanner(string text, bool ignoreTrivia = false, CancellationToken cancellationToken = default)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _ignoreTrivia = ignoreTrivia;
        _cancellationToken = cancellationToken;
    }

    public int Position => _position;
    public JsoncSyntaxKind Token { get; private set; } = JsoncSyntaxKind.Unknown;
    public string TokenValue { get; private set; } = string.Empty;
    public int TokenOffset { get; private set; }
    public int TokenLength => _position - TokenOffset;
    public int TokenStartLine => _lineStartOffset;
    public int TokenStartCharacter => TokenOffset - _previousTokenLineStartOffset;
    public JsoncScanError TokenError { get; private set; }

    public void SetPosition(int position)
    {
        if (position < 0 || position > _text.Length) throw new ArgumentOutOfRangeException(nameof(position));
        _position = position;
        TokenValue = string.Empty;
        TokenOffset = 0;
        Token = JsoncSyntaxKind.Unknown;
        TokenError = JsoncScanError.None;
        // Upstream setPosition does not reset its line counters.
    }

    public JsoncSyntaxKind Scan()
    {
        JsoncSyntaxKind result;
        do { result = ScanNext(); }
        while (_ignoreTrivia && result >= JsoncSyntaxKind.LineCommentTrivia && result <= JsoncSyntaxKind.Trivia);
        return result;
    }

    private int CharAt(int offset) => offset >= 0 && offset < _text.Length ? _text[offset] : -1;
    private static bool IsDigit(int ch) => ch >= '0' && ch <= '9';
    private static bool IsWhiteSpace(int ch) => ch is ' ' or '\t';
    private static bool IsLineBreak(int ch) => ch is '\n' or '\r';
    private string Slice(int start, int end)
    {
        // JavaScript substring clamps negative/out-of-bounds indices and swaps its arguments.
        start = Math.Clamp(start, 0, _text.Length);
        end = Math.Clamp(end, 0, _text.Length);
        return start <= end ? _text[start..end] : _text[end..start];
    }
    private void CheckCancellation()
    {
        if ((_position & 4095) == 0) _cancellationToken.ThrowIfCancellationRequested();
    }

    private int ScanHexDigits(int count)
    {
        var digits = 0; var value = 0;
        while (digits < count)
        {
            var ch = CharAt(_position);
            if (IsDigit(ch)) value = value * 16 + ch - '0';
            else if (ch >= 'A' && ch <= 'F') value = value * 16 + ch - 'A' + 10;
            else if (ch >= 'a' && ch <= 'f') value = value * 16 + ch - 'a' + 10;
            else break;
            _position++; digits++;
        }
        return digits < count ? -1 : value;
    }

    private string ScanNumber()
    {
        var start = _position;
        if (CharAt(_position) == '0') _position++;
        else
        {
            _position++;
            while (IsDigit(CharAt(_position))) { _position++; CheckCancellation(); }
        }
        if (CharAt(_position) == '.')
        {
            _position++;
            if (IsDigit(CharAt(_position)))
                while (IsDigit(CharAt(_position))) { _position++; CheckCancellation(); }
            else
            {
                TokenError = JsoncScanError.UnexpectedEndOfNumber;
                return Slice(start, _position);
            }
        }
        var end = _position;
        if (CharAt(_position) is 'e' or 'E')
        {
            _position++;
            if (CharAt(_position) is '+' or '-') _position++;
            if (IsDigit(CharAt(_position)))
            {
                while (IsDigit(CharAt(_position))) { _position++; CheckCancellation(); }
                end = _position;
            }
            else TokenError = JsoncScanError.UnexpectedEndOfNumber;
        }
        return Slice(start, end);
    }

    private string ScanString()
    {
        var result = new StringBuilder();
        var start = _position;
        while (true)
        {
            CheckCancellation();
            if (_position >= _text.Length)
            {
                result.Append(Slice(start, _position));
                TokenError = JsoncScanError.UnexpectedEndOfString;
                break;
            }
            var ch = CharAt(_position);
            if (ch == '"')
            {
                result.Append(Slice(start, _position)); _position++; break;
            }
            if (ch == '\\')
            {
                result.Append(Slice(start, _position)); _position++;
                if (_position >= _text.Length)
                { TokenError = JsoncScanError.UnexpectedEndOfString; break; }
                switch (CharAt(_position++))
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        var value = ScanHexDigits(4);
                        if (value >= 0) result.Append((char)value);
                        else TokenError = JsoncScanError.InvalidUnicode;
                        break;
                    default: TokenError = JsoncScanError.InvalidEscapeCharacter; break;
                }
                start = _position;
                continue;
            }
            if (ch >= 0 && ch <= 0x1f)
            {
                if (IsLineBreak(ch))
                {
                    result.Append(Slice(start, _position));
                    TokenError = JsoncScanError.UnexpectedEndOfString;
                    break;
                }
                TokenError = JsoncScanError.InvalidCharacter;
            }
            _position++;
        }
        return result.ToString();
    }

    private JsoncSyntaxKind ScanNext()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        TokenValue = string.Empty;
        TokenError = JsoncScanError.None;
        TokenOffset = _position;
        _lineStartOffset = _lineNumber;
        _previousTokenLineStartOffset = _tokenLineStartOffset;
        if (_position >= _text.Length)
        { TokenOffset = _text.Length; return Token = JsoncSyntaxKind.EOF; }
        var code = CharAt(_position);
        if (IsWhiteSpace(code))
        {
            do { _position++; CheckCancellation(); } while (IsWhiteSpace(CharAt(_position)));
            TokenValue = Slice(TokenOffset, _position);
            return Token = JsoncSyntaxKind.Trivia;
        }
        if (IsLineBreak(code))
        {
            _position++;
            if (code == '\r' && CharAt(_position) == '\n') _position++;
            TokenValue = Slice(TokenOffset, _position);
            _lineNumber++; _tokenLineStartOffset = _position;
            return Token = JsoncSyntaxKind.LineBreakTrivia;
        }
        switch (code)
        {
            case '{': _position++; return Token = JsoncSyntaxKind.OpenBraceToken;
            case '}': _position++; return Token = JsoncSyntaxKind.CloseBraceToken;
            case '[': _position++; return Token = JsoncSyntaxKind.OpenBracketToken;
            case ']': _position++; return Token = JsoncSyntaxKind.CloseBracketToken;
            case ':': _position++; return Token = JsoncSyntaxKind.ColonToken;
            case ',': _position++; return Token = JsoncSyntaxKind.CommaToken;
            case '"': _position++; TokenValue = ScanString(); return Token = JsoncSyntaxKind.StringLiteral;
            case '/':
            {
                var start = _position - 1;
                if (CharAt(_position + 1) == '/')
                {
                    _position += 2;
                    while (_position < _text.Length && !IsLineBreak(CharAt(_position)))
                    { _position++; CheckCancellation(); }
                    TokenValue = Slice(start, _position);
                    return Token = JsoncSyntaxKind.LineCommentTrivia;
                }
                if (CharAt(_position + 1) == '*')
                {
                    _position += 2;
                    var closed = false;
                    while (_position < _text.Length - 1)
                    {
                        CheckCancellation();
                        var ch = CharAt(_position);
                        if (ch == '*' && CharAt(_position + 1) == '/')
                        { _position += 2; closed = true; break; }
                        _position++;
                        if (IsLineBreak(ch))
                        {
                            if (ch == '\r' && CharAt(_position) == '\n') _position++;
                            _lineNumber++; _tokenLineStartOffset = _position;
                        }
                    }
                    if (!closed) { _position++; TokenError = JsoncScanError.UnexpectedEndOfComment; }
                    TokenValue = Slice(start, _position);
                    return Token = JsoncSyntaxKind.BlockCommentTrivia;
                }
                _position++; TokenValue = "/"; return Token = JsoncSyntaxKind.Unknown;
            }
            case '-':
                _position++; TokenValue = "-";
                if (_position == _text.Length || !IsDigit(CharAt(_position)))
                    return Token = JsoncSyntaxKind.Unknown;
                TokenValue += ScanNumber(); return Token = JsoncSyntaxKind.NumericLiteral;
            default:
                if (IsDigit(code))
                { TokenValue = ScanNumber(); return Token = JsoncSyntaxKind.NumericLiteral; }
                while (_position < _text.Length && IsUnknownContentCharacter(CharAt(_position)))
                { _position++; CheckCancellation(); }
                if (TokenOffset != _position)
                {
                    TokenValue = Slice(TokenOffset, _position);
                    return Token = TokenValue switch
                    {
                        "true" => JsoncSyntaxKind.TrueKeyword, "false" => JsoncSyntaxKind.FalseKeyword,
                        "null" => JsoncSyntaxKind.NullKeyword, _ => JsoncSyntaxKind.Unknown
                    };
                }
                TokenValue = ((char)code).ToString(); _position++;
                return Token = JsoncSyntaxKind.Unknown;
        }
    }

    private static bool IsUnknownContentCharacter(int code) =>
        !IsWhiteSpace(code) && !IsLineBreak(code) && code is not ('}' or ']' or '{' or '[' or '"' or ':' or ',' or '/');
}
