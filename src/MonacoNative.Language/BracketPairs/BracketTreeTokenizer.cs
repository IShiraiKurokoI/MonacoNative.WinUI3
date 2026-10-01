using System.Text.RegularExpressions;

namespace MonacoNative.Language.BracketPairs;

/// <summary>
/// Monaco 0.57 style fast tokenizer used for the initial no-token bracket tree.
/// It scans bracket delimiters and line feeds only; semantic token information is intentionally ignored.
/// </summary>
internal sealed class FastBracketTreeTokenizer : IBracketTreeTokenizer
{
    private readonly IReadOnlyList<BracketParserToken> _tokens;
    private int _index;

    public FastBracketTreeTokenizer(string text, BracketTokens brackets)
    {
        text ??= string.Empty;
        var tokens = new List<BracketParserToken>();
        var pattern = brackets.GetRegexPattern();
        var regex = pattern is null ? null : new Regex($"(?:{pattern})|\\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var lastOffset = 0;
        var lastLine = 0;
        var lastLineStart = 0;
        var line = 0;

        if (regex is not null)
        {
            foreach (Match match in regex.Matches(text))
            {
                if (match.Value == "\n")
                {
                    line++;
                    lastLineStart = match.Index + 1;
                    continue;
                }

                if (lastOffset != match.Index)
                {
                    var length = lastLine == line
                        ? BracketLength.From(0, match.Index - lastOffset)
                        : BracketLength.From(line - lastLine, match.Index - lastLineStart);
                    tokens.Add(Text(length));
                }

                var bracket = brackets.GetToken(match.Value)!;
                tokens.Add(ToParserToken(bracket, languageId: string.Empty));
                lastOffset = match.Index + match.Length;
                lastLine = line;
            }
        }

        if (lastOffset != text.Length)
        {
            var length = lastLine == line
                ? BracketLength.From(0, text.Length - lastOffset)
                : BracketLength.From(line - lastLine, text.Length - lastLineStart);
            tokens.Add(Text(length));
        }

        _tokens = tokens;
    }

    public BracketLength Offset { get; private set; } = BracketLength.Zero;
    public BracketParserToken? Peek() => _index < _tokens.Count ? _tokens[_index] : null;
    public BracketParserToken? Read()
    {
        var token = Peek();
        if (token is null) return null;
        _index++;
        Offset += token.Length;
        return token;
    }

    public void Skip(BracketLength length)
    {
        var target = Offset + length;
        while (Offset < target)
        {
            _ = Read() ?? throw new InvalidOperationException("Cannot skip beyond tokenizer end.");
            if (target < Offset)
                throw new InvalidOperationException("Cached AST node does not align with tokenizer token boundaries.");
        }
    }

    internal static BracketParserToken Text(BracketLength length) =>
        new(length, BracketTreeTokenKind.Text, -1, SmallImmutableSet<int>.GetEmpty(), new TextAstNode(length));

    internal static BracketParserToken ToParserToken(BracketTreeToken token, string languageId) =>
        new(token.Length, token.Kind, token.BracketId, token.BracketIds,
            BracketAstNode.Create(token.Length, token.Text, languageId, token.BracketIds, token.ColorizedBracketIds));
}

/// <summary>
/// Token-aware tokenizer corresponding to Monaco's TextBufferTokenizer. Brackets are only
/// recognized inside normal language tokens; strings/comments/regexp tokens are skipped.
/// Long text nodes are deliberately capped with Monaco's 1000/1500 heuristic so local edits
/// cannot force large text-node recomputation.
/// </summary>
internal sealed class TextBufferBracketTreeTokenizer : IBracketTreeTokenizer
{
    private readonly Reader _reader;
    private BracketParserToken? _peeked;
    private bool _didPeek;

    public TextBufferBracketTreeTokenizer(
        ILanguageTextModel model,
        IncrementalTokenizationCache tokenization,
        LanguageAgnosticBracketTokens bracketTokens)
    {
        _reader = new Reader(model, tokenization, bracketTokens);
    }

    public BracketLength Offset { get; private set; } = BracketLength.Zero;

    public BracketParserToken? Peek()
    {
        if (!_didPeek)
        {
            _peeked = _reader.Read();
            _didPeek = true;
        }
        return _peeked;
    }

    public BracketParserToken? Read()
    {
        BracketParserToken? token;
        if (_didPeek)
        {
            token = _peeked;
            _didPeek = false;
            _peeked = null;
        }
        else
        {
            token = _reader.Read();
        }
        if (token is not null) Offset += token.Length;
        return token;
    }

    public void Skip(BracketLength length)
    {
        _didPeek = false;
        _peeked = null;
        Offset += length;
        _reader.SetPosition(Offset.LineCount, Offset.ColumnCount);
    }

    private sealed class Reader
    {
        private readonly ILanguageTextModel _model;
        private readonly IncrementalTokenizationCache _tokenization;
        private readonly LanguageAgnosticBracketTokens _bracketTokens;
        private readonly int _lineCount;
        private readonly int _lastLineLength;

        private int _lineIndex;
        private int _column;
        private string? _line;
        private MonarchLineTokens? _tokens;
        private BracketParserToken? _peekedBracket;

        public Reader(ILanguageTextModel model, IncrementalTokenizationCache tokenization, LanguageAgnosticBracketTokens bracketTokens)
        {
            _model = model;
            _tokenization = tokenization;
            _bracketTokens = bracketTokens;
            _lineCount = model.LineCount;
            _lastLineLength = _lineCount == 0 ? 0 : model.GetLineContent(_lineCount).Length;
        }

        public void SetPosition(int lineIndex, int column)
        {
            _lineIndex = lineIndex;
            _column = column;
            _line = null;
            _tokens = null;
            _peekedBracket = null;
        }

        public BracketParserToken? Read()
        {
            if (_peekedBracket is not null)
            {
                var result = _peekedBracket;
                _peekedBracket = null;
                _column += result.Length.ColumnCount;
                return result;
            }

            if (_lineIndex >= _lineCount || (_lineIndex == _lineCount - 1 && _column >= _lastLineLength))
                return null;

            EnsureLine();
            var startLine = _lineIndex;
            var startColumn = _column;
            var heuristic = 0;

            while (true)
            {
                EnsureLine();
                var line = _line!;
                var (segmentEnd, type) = SegmentAt(_tokens!, line.Length, _column);
                BracketParserToken? bracket = null;

                if (segmentEnd > _column && IsOtherToken(type))
                {
                    var languageBrackets = _bracketTokens.GetSingleLanguageBracketTokens(_model.LanguageId);
                    var regex = languageBrackets.GlobalRegex;
                    if (regex is not null)
                    {
                        var text = line.Substring(_column, segmentEnd - _column);
                        var match = regex.Match(text);
                        if (match.Success)
                        {
                            var token = languageBrackets.GetToken(match.Value)!;
                            bracket = FastBracketTreeTokenizer.ToParserToken(token, _model.LanguageId);
                            _column += match.Index;
                        }
                    }
                }

                heuristic += Math.Max(0, segmentEnd - _column);
                if (bracket is not null)
                {
                    if (startLine != _lineIndex || startColumn != _column)
                    {
                        _peekedBracket = bracket;
                        break;
                    }
                    _column += bracket.Length.ColumnCount;
                    return bracket;
                }

                if (_column < segmentEnd)
                    _column = segmentEnd;
                else if (_column < line.Length)
                    _column++;

                if (_column >= line.Length)
                {
                    if (_lineIndex == _lineCount - 1)
                        break;
                    _lineIndex++;
                    _column = 0;
                    _line = null;
                    _tokens = null;
                    heuristic += 33;
                    if (heuristic > 1000)
                        break;
                }

                if (heuristic > 1500)
                    break;
            }

            var length = BracketLength.Diff(startLine, startColumn, _lineIndex, _column);
            return FastBracketTreeTokenizer.Text(length);
        }

        private void EnsureLine()
        {
            if (_line is not null) return;
            if (_lineIndex >= _lineCount) return;
            _line = _model.GetLineContent(_lineIndex + 1);
            _tokens = _tokenization.GetLineTokens(_lineIndex + 1);
            _column = Math.Clamp(_column, 0, _line.Length);
        }

        private static (int End, string Type) SegmentAt(MonarchLineTokens tokens, int lineLength, int offset)
        {
            foreach (var token in tokens.Tokens)
            {
                if (offset < token.StartIndex)
                    return (token.StartIndex, string.Empty);
                if (offset >= token.StartIndex && offset < token.EndIndex)
                    return (token.EndIndex, token.Type);
            }
            return (lineLength, string.Empty);
        }

        private static bool IsOtherToken(string tokenType)
        {
            if (string.IsNullOrEmpty(tokenType)) return true;
            return !(tokenType.StartsWith("comment", StringComparison.OrdinalIgnoreCase) ||
                     tokenType.StartsWith("string", StringComparison.OrdinalIgnoreCase) ||
                     tokenType.StartsWith("regexp", StringComparison.OrdinalIgnoreCase));
        }
    }
}
