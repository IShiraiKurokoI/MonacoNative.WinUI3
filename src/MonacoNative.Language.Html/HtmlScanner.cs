// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// C# source port of microsoft/vscode-html-languageservice/src/parser/htmlScanner.ts.
// Source reviewed: main, 2026-10-23. Scanner only, NOT the complete HTML language service.
using System.Text.RegularExpressions;

namespace MonacoNative.Language.Html;

public enum HtmlTokenType
{
    StartCommentTag, Comment, EndCommentTag, StartTagOpen, StartTagClose,
    StartTagSelfClose, StartTag, EndTagOpen, EndTagClose, EndTag,
    DelimiterAssign, AttributeName, AttributeValue, StartDoctypeTag, Doctype,
    EndDoctypeTag, Content, Whitespace, Unknown, Script, Styles, EOS
}
public enum HtmlScannerState
{
    WithinContent, AfterOpeningStartTag, AfterOpeningEndTag, WithinDoctype,
    WithinTag, WithinEndTag, WithinComment, WithinScriptContent, WithinStyleContent,
    AfterAttributeName, BeforeAttributeValue
}

public sealed class HtmlScanner
{
    // Explicit JS whitespace class: .NET Regex \s and JS \s are not interchangeable.
    private const string JsSpace = @"\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
    private static readonly Regex ElementName = new(@"\G[_:A-Za-z0-9][_:A-Za-z0-9.\-]*", RegexOptions.CultureInvariant);
    private static readonly Regex AttributeName = new("\\G[^" + JsSpace + "\"'></=\\x00-\\x0F\\x7F\\x80-\\x9F]*", RegexOptions.CultureInvariant);
    private static readonly Regex AttributeValue = new("\\G[^" + JsSpace + "\"'`=<>]+", RegexOptions.CultureInvariant);
    private static readonly Regex Doctype = new(@"\G![dD][oO][cC][tT][yY][pP][eE]", RegexOptions.CultureInvariant);
    private static readonly Regex ScriptBoundary = new("<!--|-->|</?[sS][cC][rR][iI][pP][tT][" + JsSpace + "]*/?>?", RegexOptions.CultureInvariant);
    private static readonly Regex StyleBoundary = new(@"</[sS][tT][yY][lL][eE]", RegexOptions.CultureInvariant);
    private static readonly Regex ScriptComment = new(@"<!--|-->", RegexOptions.CultureInvariant);
    private readonly string _text;
    private readonly bool _emitPseudoCloseTags;
    private readonly CancellationToken _cancellationToken;
    private int _position;
    private bool _hasSpaceAfterTag;
    private string _lastTag = string.Empty;
    private string? _lastAttributeName, _lastTypeValue;

    public HtmlScanner(string text, int initialOffset = 0, HtmlScannerState initialState = HtmlScannerState.WithinContent,
        bool emitPseudoCloseTags = false, CancellationToken cancellationToken = default)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        if (initialOffset < 0 || initialOffset > text.Length) throw new ArgumentOutOfRangeException(nameof(initialOffset));
        _position = initialOffset; State = initialState;
        _emitPseudoCloseTags = emitPseudoCloseTags;
        _cancellationToken = cancellationToken;
    }
    public HtmlScannerState State { get; private set; }
    public HtmlTokenType TokenType { get; private set; } = HtmlTokenType.Unknown;
    public int TokenOffset { get; private set; }
    public int TokenLength => _position - TokenOffset;
    public int TokenEnd => _position;
    public string TokenText => _text[TokenOffset..Math.Min(_position, _text.Length)];
    public string? TokenError { get; private set; }
    private bool Eos => _position >= _text.Length;
    private int Peek(int delta = 0) => _position + delta >= 0 && _position + delta < _text.Length ? _text[_position + delta] : 0;
    private bool AdvanceIf(char ch)
    {
        if (Peek() != ch || Eos) return false;
        _position++; return true;
    }
    private bool AdvanceIf(string text)
    {
        if (!_text.AsSpan(_position).StartsWith(text.AsSpan(), StringComparison.Ordinal)) return false;
        _position += text.Length; return true;
    }
    private bool AdvanceUntil(char ch)
    {
        var index = _text.IndexOf(ch, _position);
        _position = index < 0 ? _text.Length : index;
        return index >= 0;
    }
    private void AdvanceUntil(string text)
    {
        var index = _text.IndexOf(text, _position, StringComparison.Ordinal);
        _position = index < 0 ? _text.Length : index;
    }
    private string AdvanceIfRegex(Regex regex)
    {
        var match = regex.Match(_text, _position);
        if (!match.Success) return string.Empty;
        _position = match.Index + match.Length;
        return match.Value;
    }
    private bool SkipWhitespace()
    {
        var start = _position;
        while (!Eos && (Peek() is ' ' or '\t' or '\n' or '\f' or '\r')) _position++;
        return _position > start;
    }
    private HtmlTokenType Finish(int offset, HtmlTokenType type, string? error = null)
    { TokenOffset = offset; TokenType = type; TokenError = error; return type; }
    public HtmlTokenType Scan()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var offset = _position;
        var token = ScanInternal();
        if (token != HtmlTokenType.EOS && offset == _position &&
            !(_emitPseudoCloseTags && (token is HtmlTokenType.StartTagClose or HtmlTokenType.EndTagClose)))
        { _position++; return Finish(offset, HtmlTokenType.Unknown); }
        return token;
    }
    private HtmlTokenType ScanInternal()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var offset = _position;
        if (Eos) return Finish(offset, HtmlTokenType.EOS);
        string? error = null;
        switch (State)
        {
            case HtmlScannerState.WithinComment:
                if (AdvanceIf("-->"))
                { State = HtmlScannerState.WithinContent; return Finish(offset, HtmlTokenType.EndCommentTag); }
                AdvanceUntil("-->"); return Finish(offset, HtmlTokenType.Comment);
            case HtmlScannerState.WithinDoctype:
                if (AdvanceIf('>'))
                { State = HtmlScannerState.WithinContent; return Finish(offset, HtmlTokenType.EndDoctypeTag); }
                AdvanceUntil('>'); return Finish(offset, HtmlTokenType.Doctype);
            case HtmlScannerState.WithinContent:
                if (AdvanceIf('<'))
                {
                    if (!Eos && Peek() == '!')
                    {
                        if (AdvanceIf("!--"))
                        { State = HtmlScannerState.WithinComment; return Finish(offset, HtmlTokenType.StartCommentTag); }
                        if (AdvanceIfRegex(Doctype).Length > 0)
                        { State = HtmlScannerState.WithinDoctype; return Finish(offset, HtmlTokenType.StartDoctypeTag); }
                    }
                    if (AdvanceIf('/'))
                    { State = HtmlScannerState.AfterOpeningEndTag; return Finish(offset, HtmlTokenType.EndTagOpen); }
                    State = HtmlScannerState.AfterOpeningStartTag;
                    return Finish(offset, HtmlTokenType.StartTagOpen);
                }
                AdvanceUntil('<'); return Finish(offset, HtmlTokenType.Content);
            case HtmlScannerState.AfterOpeningEndTag:
                if (AdvanceIfRegex(ElementName).Length > 0)
                { State = HtmlScannerState.WithinEndTag; return Finish(offset, HtmlTokenType.EndTag); }
                if (SkipWhitespace()) return Finish(offset, HtmlTokenType.Whitespace, "Tag name must directly follow the open bracket.");
                State = HtmlScannerState.WithinEndTag;
                AdvanceUntil('>');
                if (offset < _position) return Finish(offset, HtmlTokenType.Unknown, "End tag name expected.");
                return ScanInternal();
            case HtmlScannerState.WithinEndTag:
                if (SkipWhitespace()) return Finish(offset, HtmlTokenType.Whitespace);
                if (AdvanceIf('>'))
                { State = HtmlScannerState.WithinContent; return Finish(offset, HtmlTokenType.EndTagClose); }
                if (_emitPseudoCloseTags && Peek() == '<')
                { State = HtmlScannerState.WithinContent; return Finish(offset, HtmlTokenType.EndTagClose, "Closing bracket missing."); }
                error = "Closing bracket expected."; break;
            case HtmlScannerState.AfterOpeningStartTag:
                _lastTag = AdvanceIfRegex(ElementName).ToLowerInvariant();
                _lastTypeValue = null; _lastAttributeName = null;
                if (_lastTag.Length > 0)
                {
                    _hasSpaceAfterTag = false; State = HtmlScannerState.WithinTag;
                    return Finish(offset, HtmlTokenType.StartTag);
                }
                if (SkipWhitespace()) return Finish(offset, HtmlTokenType.Whitespace, "Tag name must directly follow the open bracket.");
                State = HtmlScannerState.WithinTag;
                AdvanceUntil('>');
                if (offset < _position) return Finish(offset, HtmlTokenType.Unknown, "Start tag name expected.");
                return ScanInternal();
            case HtmlScannerState.WithinTag:
                if (SkipWhitespace())
                { _hasSpaceAfterTag = true; return Finish(offset, HtmlTokenType.Whitespace); }
                if (_hasSpaceAfterTag)
                {
                    _lastAttributeName = AdvanceIfRegex(AttributeName).ToLowerInvariant();
                    if (_lastAttributeName.Length > 0)
                    {
                        State = HtmlScannerState.AfterAttributeName; _hasSpaceAfterTag = false;
                        return Finish(offset, HtmlTokenType.AttributeName);
                    }
                }
                if (AdvanceIf("/>"))
                { State = HtmlScannerState.WithinContent; return Finish(offset, HtmlTokenType.StartTagSelfClose); }
                if (AdvanceIf('>'))
                {
                    State = _lastTag == "script"
                        ? _lastTypeValue is "text/x-handlebars-template" or "text/html"
                            ? HtmlScannerState.WithinContent : HtmlScannerState.WithinScriptContent
                        : _lastTag == "style" ? HtmlScannerState.WithinStyleContent : HtmlScannerState.WithinContent;
                    return Finish(offset, HtmlTokenType.StartTagClose);
                }
                if (_emitPseudoCloseTags && Peek() == '<')
                { State = HtmlScannerState.WithinContent; return Finish(offset, HtmlTokenType.StartTagClose, "Closing bracket missing."); }
                _position++; return Finish(offset, HtmlTokenType.Unknown, "Unexpected character in tag.");
            case HtmlScannerState.AfterAttributeName:
                if (SkipWhitespace())
                { _hasSpaceAfterTag = true; return Finish(offset, HtmlTokenType.Whitespace); }
                if (AdvanceIf('='))
                { State = HtmlScannerState.BeforeAttributeValue; return Finish(offset, HtmlTokenType.DelimiterAssign); }
                State = HtmlScannerState.WithinTag; return ScanInternal();
            case HtmlScannerState.BeforeAttributeValue:
                if (SkipWhitespace()) return Finish(offset, HtmlTokenType.Whitespace);
                var value = AdvanceIfRegex(AttributeValue);
                if (value.Length > 0)
                {
                    if (Peek() == '>' && Peek(-1) == '/') { _position--; value = value[..^1]; }
                    if (_lastAttributeName == "type") _lastTypeValue = value;
                    if (value.Length > 0)
                    {
                        State = HtmlScannerState.WithinTag; _hasSpaceAfterTag = false;
                        return Finish(offset, HtmlTokenType.AttributeValue);
                    }
                }
                var quote = Peek();
                if (quote is '\'' or '"')
                {
                    _position++;
                    if (AdvanceUntil((char)quote)) _position++;
                    if (_lastAttributeName == "type")
                        _lastTypeValue = _text[Math.Min(offset + 1, _position - 1)..Math.Max(offset + 1, _position - 1)];
                    State = HtmlScannerState.WithinTag; _hasSpaceAfterTag = false;
                    return Finish(offset, HtmlTokenType.AttributeValue);
                }
                State = HtmlScannerState.WithinTag; _hasSpaceAfterTag = false;
                return ScanInternal();
            case HtmlScannerState.WithinScriptContent:
                var scriptState = 1;
                while (!Eos)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var match = AdvanceIfRegex(ScriptBoundary);
                    if (match.Length == 0) { _position = _text.Length; return Finish(offset, HtmlTokenType.Script); }
                    if (match == "<!--") { if (scriptState == 1) scriptState = 2; }
                    else if (match == "-->") scriptState = 1;
                    else if (match[1] != '/') { if (scriptState == 2) scriptState = 3; }
                    else if (scriptState == 3) scriptState = 2;
                    else { _position -= match.Length; break; }
                }
                State = HtmlScannerState.WithinContent;
                if (offset < _position) return Finish(offset, HtmlTokenType.Script);
                return ScanInternal();
            case HtmlScannerState.WithinStyleContent:
                var boundary = StyleBoundary.Match(_text, _position);
                _position = boundary.Success ? boundary.Index : _text.Length;
                State = HtmlScannerState.WithinContent;
                if (offset < _position) return Finish(offset, HtmlTokenType.Styles);
                return ScanInternal();
        }
        _position++; State = HtmlScannerState.WithinContent;
        return Finish(offset, HtmlTokenType.Unknown, error);
    }

    public static string ConvertScriptContentToJavaScript(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var depth = 0;
        return ScriptComment.Replace(content, match =>
        {
            if (match.Value == "<!--") { depth++; return "/* "; }
            if (depth > 0) { depth--; return " */"; }
            return match.Value;
        });
    }
}
