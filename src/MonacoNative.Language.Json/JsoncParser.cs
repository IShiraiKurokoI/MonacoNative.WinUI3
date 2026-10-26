// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// Port of node-jsonc-parser/src/impl/parser.ts: visit, parseTree, location and path APIs.
// The depth guard and cancellation checks are deliberate native-host safeguards.
using System.Globalization;
using System.Text;

namespace MonacoNative.Language.Json;

public enum JsoncParseErrorCode
{
    InvalidSymbol = 1, InvalidNumberFormat, PropertyNameExpected, ValueExpected, ColonExpected,
    CommaExpected, CloseBraceExpected, CloseBracketExpected, EndOfFileExpected, InvalidCommentToken,
    UnexpectedEndOfComment, UnexpectedEndOfString, UnexpectedEndOfNumber, InvalidUnicode,
    InvalidEscapeCharacter, InvalidCharacter, MaximumDepthExceeded
}
public enum JsoncNodeType { Object, Array, Property, String, Number, Boolean, Null }
public sealed record JsoncParseOptions(bool DisallowComments = false, bool AllowTrailingComma = false,
    bool AllowEmptyContent = false, int MaximumDepth = 256);
public readonly record struct JsoncTokenLocation(int Offset, int Length, int StartLine, int StartCharacter);
public sealed record JsoncParseError(JsoncParseErrorCode Code, int Offset, int Length, int StartLine, int StartCharacter);
public sealed class JsoncNode
{
    public JsoncNodeType Type { get; internal set; }
    public object? Value { get; internal set; }
    public int Offset { get; internal set; }
    public int Length { get; internal set; }
    public int End => Offset + Length;
    public int? ColonOffset { get; internal set; }
    public JsoncNode? Parent { get; internal set; }
    public List<JsoncNode> Children { get; } = new();
    internal JsoncNode(JsoncNodeType type, int offset, int length = 0, JsoncNode? parent = null, object? value = null)
    { Type = type; Offset = offset; Length = length; Parent = parent; Value = value; }
}
public sealed record JsoncParseResult(JsoncNode? Root, IReadOnlyList<JsoncParseError> Errors);
public sealed record JsoncLocation(IReadOnlyList<object> Path, JsoncNode? PreviousNode, bool IsAtPropertyKey)
{
    public bool Matches(IReadOnlyList<object> pattern)
    {
        var k = 0;
        for (var i = 0; k < pattern.Count && i < Path.Count; i++)
            if (Equals(pattern[k], Path[i]) || Equals(pattern[k], "*")) k++;
            else if (!Equals(pattern[k], "**")) return false;
        return k == pattern.Count;
    }
}
public sealed class JsoncVisitor
{
    public Func<JsoncTokenLocation, IReadOnlyList<object>, bool>? OnObjectBegin { get; init; }
    public Action<string, JsoncTokenLocation, IReadOnlyList<object>>? OnObjectProperty { get; init; }
    public Action<JsoncTokenLocation>? OnObjectEnd { get; init; }
    public Func<JsoncTokenLocation, IReadOnlyList<object>, bool>? OnArrayBegin { get; init; }
    public Action<JsoncTokenLocation>? OnArrayEnd { get; init; }
    public Action<object?, JsoncTokenLocation, IReadOnlyList<object>>? OnLiteralValue { get; init; }
    public Action<char, JsoncTokenLocation>? OnSeparator { get; init; }
    public Action<JsoncTokenLocation>? OnComment { get; init; }
    public Action<JsoncParseError>? OnError { get; init; }
}

public static class JsoncParser
{
    public static bool Visit(string text, JsoncVisitor visitor, JsoncParseOptions? options = null, CancellationToken cancellationToken = default) =>
        new Reader(text, visitor, options ?? new(), cancellationToken).Read();
    public static JsoncParseResult ParseTree(string text, JsoncParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<JsoncParseError>();
        var artificial = new JsoncNode(JsoncNodeType.Array, -1, -1);
        var current = artificial;
        void CompleteProperty(int end)
        { if (current.Type == JsoncNodeType.Property) { current.Length = end - current.Offset; current = current.Parent!; } }
        void Begin(JsoncNodeType type, JsoncTokenLocation l)
        { var child = new JsoncNode(type, l.Offset, -1, current); current.Children.Add(child); current = child; }
        void End(JsoncTokenLocation l)
        {
            CompleteProperty(l.Offset + l.Length);
            current.Length = l.Offset + l.Length - current.Offset; current = current.Parent!;
            CompleteProperty(l.Offset + l.Length);
        }
        Visit(text, new JsoncVisitor
        {
            OnObjectBegin = (l, _) => { Begin(JsoncNodeType.Object, l); return true; },
            OnArrayBegin = (l, _) => { Begin(JsoncNodeType.Array, l); return true; },
            OnObjectProperty = (name, l, _) =>
            { Begin(JsoncNodeType.Property, l); current.Children.Add(new(JsoncNodeType.String, l.Offset, l.Length, current, name)); },
            OnObjectEnd = End, OnArrayEnd = End,
            OnLiteralValue = (value, l, _) =>
            { current.Children.Add(new(NodeType(value), l.Offset, l.Length, current, value)); CompleteProperty(l.Offset + l.Length); },
            OnSeparator = (separator, l) =>
            {
                if (current.Type != JsoncNodeType.Property) return;
                if (separator == ':') current.ColonOffset = l.Offset;
                else if (separator == ',') CompleteProperty(l.Offset);
            }, OnError = errors.Add
        }, options, cancellationToken);
        var root = artificial.Children.FirstOrDefault();
        if (root is not null) root.Parent = null;
        return new(root, errors);
    }
    public static object? Parse(string text, out IReadOnlyList<JsoncParseError> errors, JsoncParseOptions? options = null, CancellationToken cancellationToken = default)
    { var result = ParseTree(text, options, cancellationToken); errors = result.Errors; return result.Root is null ? null : GetNodeValue(result.Root); }
    public static object? GetNodeValue(JsoncNode node)
    {
        if (node.Type == JsoncNodeType.Array) return node.Children.Select(GetNodeValue).ToArray();
        if (node.Type != JsoncNodeType.Object) return node.Value;
        var value = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in node.Children)
            if (property.Children.Count == 2 && property.Children[0].Value is string name) value[name] = GetNodeValue(property.Children[1]);
        return value;
    }
    public static JsoncNode? FindNodeAtLocation(JsoncNode? root, IEnumerable<object> path)
    {
        var node = root;
        foreach (var segment in path)
        {
            if (node is null) return null;
            if (segment is string name) node = node.Type == JsoncNodeType.Object
                ? node.Children.FirstOrDefault(p => p.Children.Count == 2 && Equals(p.Children[0].Value, name))?.Children[1] : null;
            else if (segment is int index) node = node.Type == JsoncNodeType.Array && index >= 0 && index < node.Children.Count ? node.Children[index] : null;
            else return null;
        }
        return node;
    }
    public static IReadOnlyList<object> GetNodePath(JsoncNode node)
    {
        var path = new List<object>();
        while (node.Parent is { } parent)
        {
            if (parent.Type == JsoncNodeType.Property) path.Add(parent.Children[0].Value!);
            else if (parent.Type == JsoncNodeType.Array) path.Add(parent.Children.IndexOf(node));
            node = parent;
        }
        path.Reverse(); return path;
    }
    public static JsoncNode? FindNodeAtOffset(JsoncNode? node, int offset, bool includeRightBound = false)
    {
        if (node is null || offset < node.Offset || offset > node.End || (!includeRightBound && offset == node.End)) return null;
        foreach (var child in node.Children)
        { if (child.Offset > offset) break; if (FindNodeAtOffset(child, offset, includeRightBound) is { } found) return found; }
        return node;
    }
    private sealed class StopLocation : Exception { }
    public static JsoncLocation GetLocation(string text, int position, CancellationToken cancellationToken = default)
    {
        var path = new List<object>(); JsoncNode? previous = null; var atKey = false;
        void Stop() => throw new StopLocation();
        try
        {
            Visit(text, new JsoncVisitor
            {
                OnObjectBegin = (l, _) => { if (position <= l.Offset) Stop(); previous = null; atKey = position > l.Offset; path.Add(""); return true; },
                OnArrayBegin = (l, _) => { if (position <= l.Offset) Stop(); previous = null; path.Add(0); return true; },
                OnObjectProperty = (name, l, _) =>
                { if (position < l.Offset) Stop(); previous = new(JsoncNodeType.Property, l.Offset, l.Length, value: name); path[^1] = name; if (position <= l.Offset + l.Length) Stop(); },
                OnObjectEnd = l => { if (position <= l.Offset) Stop(); previous = null; if (path.Count > 0) path.RemoveAt(path.Count - 1); },
                OnArrayEnd = l => { if (position <= l.Offset) Stop(); previous = null; if (path.Count > 0) path.RemoveAt(path.Count - 1); },
                OnLiteralValue = (value, l, _) =>
                { if (position < l.Offset) Stop(); previous = new(NodeType(value), l.Offset, l.Length, value: value); if (position <= l.Offset + l.Length) Stop(); },
                OnSeparator = (separator, l) =>
                {
                    if (position <= l.Offset) Stop();
                    if (separator == ':' && previous?.Type == JsoncNodeType.Property) { previous.ColonOffset = l.Offset; atKey = false; previous = null; }
                    else if (separator == ',' && path.Count > 0)
                    { if (path[^1] is int index) path[^1] = index + 1; else { atKey = true; path[^1] = ""; } previous = null; }
                }
            }, cancellationToken: cancellationToken);
        }
        catch (StopLocation) { }
        return new(path.ToArray(), previous, atKey);
    }
    public static string StripComments(string text, char? replaceCharacter = null)
    {
        var scanner = new JsoncScanner(text); var result = new StringBuilder(text.Length); var start = 0;
        while (scanner.Scan() != JsoncSyntaxKind.EOF)
        {
            if (scanner.Token is not (JsoncSyntaxKind.LineCommentTrivia or JsoncSyntaxKind.BlockCommentTrivia)) continue;
            result.Append(text, start, scanner.TokenOffset - start);
            if (replaceCharacter is { } replacement)
                for (var i = scanner.TokenOffset; i < scanner.Position; i++) result.Append(text[i] is '\r' or '\n' ? text[i] : replacement);
            start = scanner.Position;
        }
        return result.Append(text, start, text.Length - start).ToString();
    }
    private static JsoncNodeType NodeType(object? value) => value switch
    { null => JsoncNodeType.Null, string => JsoncNodeType.String, bool => JsoncNodeType.Boolean, _ => JsoncNodeType.Number };
    private sealed class Reader
    {
        private readonly JsoncScanner _scanner; private readonly JsoncVisitor _visitor;
        private readonly JsoncParseOptions _options; private readonly CancellationToken _ct;
        private readonly List<object> _path = new(); private int _suppressed, _depth;
        private JsoncTokenLocation Location => new(_scanner.TokenOffset, _scanner.TokenLength, _scanner.TokenStartLine, _scanner.TokenStartCharacter);
        private JsoncSyntaxKind Token => _scanner.Token;
        public Reader(string text, JsoncVisitor visitor, JsoncParseOptions options, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(visitor);
            if (options.MaximumDepth < 1 || options.MaximumDepth > 512) throw new ArgumentOutOfRangeException(nameof(options));
            _scanner = new(text, cancellationToken: ct); _visitor = visitor; _options = options; _ct = ct;
        }
        private void Begin(Func<JsoncTokenLocation, IReadOnlyList<object>, bool>? action)
        { if (action is null) return; if (_suppressed > 0) _suppressed++; else if (action(Location, _path.ToArray()) == false) _suppressed = 1; }
        private void End(Action<JsoncTokenLocation>? action)
        { if (action is null) return; if (_suppressed > 0) _suppressed--; if (_suppressed == 0) action(Location); }
        private void Separator(char value) { if (_suppressed == 0) _visitor.OnSeparator?.Invoke(value, Location); }
        private void Literal(object? value) { if (_suppressed == 0) _visitor.OnLiteralValue?.Invoke(value, Location, _path.ToArray()); }
        private void Error(JsoncParseErrorCode code, JsoncSyntaxKind[]? after = null, JsoncSyntaxKind[]? until = null)
        {
            var l = Location;
            if (_suppressed == 0) _visitor.OnError?.Invoke(new(code, l.Offset, l.Length, l.StartLine, l.StartCharacter));
            if ((after?.Length ?? 0) + (until?.Length ?? 0) == 0) return;
            while (Token != JsoncSyntaxKind.EOF)
            { if (after?.Contains(Token) == true) { Next(); break; } if (until?.Contains(Token) == true) break; Next(); }
        }
        private void Next()
        {
            while (true)
            {
                _ct.ThrowIfCancellationRequested(); var token = _scanner.Scan();
                var error = _scanner.TokenError switch
                {
                    JsoncScanError.InvalidUnicode => JsoncParseErrorCode.InvalidUnicode,
                    JsoncScanError.InvalidEscapeCharacter => JsoncParseErrorCode.InvalidEscapeCharacter,
                    JsoncScanError.UnexpectedEndOfNumber => JsoncParseErrorCode.UnexpectedEndOfNumber,
                    JsoncScanError.UnexpectedEndOfComment when !_options.DisallowComments => JsoncParseErrorCode.UnexpectedEndOfComment,
                    JsoncScanError.UnexpectedEndOfString => JsoncParseErrorCode.UnexpectedEndOfString,
                    JsoncScanError.InvalidCharacter => JsoncParseErrorCode.InvalidCharacter, _ => (JsoncParseErrorCode)0
                };
                if (error != 0) Error(error);
                switch (token)
                {
                    case JsoncSyntaxKind.LineCommentTrivia: case JsoncSyntaxKind.BlockCommentTrivia:
                        if (_options.DisallowComments) Error(JsoncParseErrorCode.InvalidCommentToken);
                        else if (_suppressed == 0) _visitor.OnComment?.Invoke(Location); break;
                    case JsoncSyntaxKind.Unknown: Error(JsoncParseErrorCode.InvalidSymbol); break;
                    case JsoncSyntaxKind.Trivia: case JsoncSyntaxKind.LineBreakTrivia: break;
                    default: return;
                }
            }
        }
        private bool String(bool isValue)
        {
            var value = _scanner.TokenValue;
            if (isValue) Literal(value);
            else { if (_suppressed == 0) _visitor.OnObjectProperty?.Invoke(value, Location, _path.ToArray()); _path.Add(value); }
            Next(); return true;
        }
        private bool Property()
        {
            if (Token != JsoncSyntaxKind.StringLiteral)
            { Error(JsoncParseErrorCode.PropertyNameExpected, until: new[] { JsoncSyntaxKind.CloseBraceToken, JsoncSyntaxKind.CommaToken }); return false; }
            String(false);
            if (Token == JsoncSyntaxKind.ColonToken)
            { Separator(':'); Next(); if (!Value()) Error(JsoncParseErrorCode.ValueExpected, until: new[] { JsoncSyntaxKind.CloseBraceToken, JsoncSyntaxKind.CommaToken }); }
            else Error(JsoncParseErrorCode.ColonExpected, until: new[] { JsoncSyntaxKind.CloseBraceToken, JsoncSyntaxKind.CommaToken });
            _path.RemoveAt(_path.Count - 1); return true;
        }
        private bool Container(bool array)
        {
            var close = array ? JsoncSyntaxKind.CloseBracketToken : JsoncSyntaxKind.CloseBraceToken;
            Begin(array ? _visitor.OnArrayBegin : _visitor.OnObjectBegin); Next();
            var comma = false; var first = true;
            while (Token != close && Token != JsoncSyntaxKind.EOF)
            {
                if (Token == JsoncSyntaxKind.CommaToken)
                { if (!comma) Error(JsoncParseErrorCode.ValueExpected); Separator(','); Next(); if (Token == close && _options.AllowTrailingComma) break; }
                else if (comma) Error(JsoncParseErrorCode.CommaExpected);
                if (array) { if (first) { _path.Add(0); first = false; } else _path[^1] = (int)_path[^1] + 1; }
                if (!(array ? Value() : Property())) Error(JsoncParseErrorCode.ValueExpected, until: new[] { close, JsoncSyntaxKind.CommaToken });
                comma = true;
            }
            End(array ? _visitor.OnArrayEnd : _visitor.OnObjectEnd);
            if (array && !first) _path.RemoveAt(_path.Count - 1);
            if (Token != close) Error(array ? JsoncParseErrorCode.CloseBracketExpected : JsoncParseErrorCode.CloseBraceExpected, after: new[] { close });
            else Next();
            return true;
        }
        private bool Value()
        {
            if (Token is JsoncSyntaxKind.OpenBraceToken or JsoncSyntaxKind.OpenBracketToken)
            {
                if (++_depth > _options.MaximumDepth)
                {
                    Error(JsoncParseErrorCode.MaximumDepthExceeded);
                    var balance = 1; Next();
                    while (balance > 0 && Token != JsoncSyntaxKind.EOF)
                    { if (Token is JsoncSyntaxKind.OpenBraceToken or JsoncSyntaxKind.OpenBracketToken) balance++; if (Token is JsoncSyntaxKind.CloseBraceToken or JsoncSyntaxKind.CloseBracketToken) balance--; Next(); }
                    _depth--; return true;
                }
                var result = Container(Token == JsoncSyntaxKind.OpenBracketToken); _depth--; return result;
            }
            switch (Token)
            {
                case JsoncSyntaxKind.StringLiteral: return String(true);
                case JsoncSyntaxKind.NumericLiteral:
                    if (!double.TryParse(_scanner.TokenValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { Error(JsoncParseErrorCode.InvalidNumberFormat); number = 0; }
                    Literal(number); break;
                case JsoncSyntaxKind.NullKeyword: Literal(null); break;
                case JsoncSyntaxKind.TrueKeyword: Literal(true); break;
                case JsoncSyntaxKind.FalseKeyword: Literal(false); break;
                default: return false;
            }
            Next(); return true;
        }
        public bool Read()
        {
            Next();
            if (Token == JsoncSyntaxKind.EOF) { if (_options.AllowEmptyContent) return true; Error(JsoncParseErrorCode.ValueExpected); return false; }
            if (!Value()) { Error(JsoncParseErrorCode.ValueExpected); return false; }
            if (Token != JsoncSyntaxKind.EOF) Error(JsoncParseErrorCode.EndOfFileExpected);
            return true;
        }
    }
}
