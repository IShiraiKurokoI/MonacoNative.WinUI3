namespace MonacoNative.Language.BracketPairs;

internal sealed record BracketTreeInfo(LanguageRange Range, int NestingLevel, int NestingLevelOfEqualBracketType, bool IsInvalid);
internal sealed record BracketTreeMatch(LanguageRange Bracket, LanguageRange Match);

/// <summary>
/// Monaco 0.57 bracket-pair tree lifecycle: an initial no-token tree is available immediately,
/// a token-aware tree takes over when tokenization completes, text edits are lazily composed,
/// and queries flush only the pending incremental edits.
/// </summary>
internal sealed class BracketPairsTree
{
    private readonly ILanguageTextModel _model;
    private readonly IncrementalTokenizationCache _tokenization;
    private readonly LanguageRegistry _languages;
    private LanguageAgnosticBracketTokens _brackets;
    private AstNode? _initialAstWithoutTokens;
    private AstNode? _astWithTokens;
    private IReadOnlyList<TextEditInfo> _queuedInitialEdits = Array.Empty<TextEditInfo>();
    private IReadOnlyList<TextEditInfo> _queuedTokenEdits = Array.Empty<TextEditInfo>();
    private bool _tokenAwareReady;

    public BracketPairsTree(ILanguageTextModel model, IncrementalTokenizationCache tokenization, LanguageRegistry languages)
    {
        _model = model;
        _tokenization = tokenization;
        _languages = languages;
        _brackets = NewBracketTokens();
        BuildInitialTree();
    }

    public bool DidLanguageChange(string languageId) => _brackets.DidLanguageChange(languageId);

    public void HandleContentChanged(TextEditInfo edit, bool fullInvalidation)
    {
        if (fullInvalidation)
        {
            BuildInitialTree();
            return;
        }

        if (_initialAstWithoutTokens is not null)
            _queuedInitialEdits = CombineTextEditInfos.Combine(_queuedInitialEdits, new[] { edit });
        if (_tokenAwareReady)
            _queuedTokenEdits = CombineTextEditInfos.Combine(_queuedTokenEdits, new[] { edit });
    }

    public void HandleLanguageConfigurationChanged(string languageId)
    {
        if (!DidLanguageChange(languageId)) return;
        _brackets = NewBracketTokens();
        BuildInitialTree();
    }

    public void HandleTokenizationChanged(string languageId)
    {
        if (!string.Equals(languageId, _model.LanguageId, StringComparison.OrdinalIgnoreCase)) return;
        BuildInitialTree();
    }

    /// <summary>
    /// Corresponds to Monaco's backgroundTokenizationState === Completed transition.
    /// The first call builds the semantic/token-aware tree and drops the fast initial tree.
    /// </summary>
    public void CompleteTokenization()
    {
        if (_tokenAwareReady) return;
        if (!_languages.TryGetMonarchTokenizer(_model.LanguageId, out _, out _)) return;
        _ = _tokenization.ConsumePendingChangedRange();
        _astWithTokens = ParseTokenAware(Array.Empty<TextEditInfo>(), null, createImmutableLists: true);
        _queuedTokenEdits = Array.Empty<TextEditInfo>();
        _initialAstWithoutTokens = null;
        _queuedInitialEdits = Array.Empty<TextEditInfo>();
        _tokenAwareReady = true;
    }

    public IReadOnlyList<BracketTreeInfo> GetBracketsInRange(LanguageRange range, bool onlyColorizedBrackets = true)
    {
        FlushQueue();
        var root = ActiveRoot;
        if (root is null) return Array.Empty<BracketTreeInfo>();
        var normalized = range.Normalize();
        var start = ToLength(normalized.StartLineNumber, normalized.StartColumn);
        var end = ToLength(normalized.EndLineNumber, normalized.EndColumn);
        var result = new List<BracketTreeInfo>();
        CollectBrackets(root, BracketLength.Zero, root.Length, start, end, result, 0, 0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), onlyColorizedBrackets, false);
        return result;
    }

    public BracketTreeMatch? FindMatchingBracket(LanguagePosition position)
    {
        FlushQueue();
        var root = ActiveRoot;
        if (root is null) return null;
        var target = ToLength(position.LineNumber, position.Column);
        return FindMatch(root, BracketLength.Zero, target);
    }

    private AstNode? ActiveRoot => _initialAstWithoutTokens ?? _astWithTokens;

    private void BuildInitialTree()
    {
        _queuedInitialEdits = Array.Empty<TextEditInfo>();
        _queuedTokenEdits = Array.Empty<TextEditInfo>();
        _tokenAwareReady = false;
        var tokens = _brackets.GetSingleLanguageBracketTokens(_model.LanguageId);
        var result = BracketParser.ParseDocument(new FastBracketTreeTokenizer(_model.GetValue(), tokens), Array.Empty<TextEditInfo>(), null, createImmutableLists: true);
        _initialAstWithoutTokens = result.Root;
        _astWithTokens = result.Root;
    }

    private void FlushQueue()
    {
        if (_tokenAwareReady)
        {
            var tokenChange = _tokenization.ConsumePendingChangedRange();
            if (tokenChange is { } changed)
            {
                var tokenEdit = new TextEditInfo(
                    BracketLength.From(changed.FromLine - 1, 0),
                    BracketLength.From(changed.ToLine, 0),
                    BracketLength.From(changed.ToLine - changed.FromLine + 1, 0));
                _queuedTokenEdits = CombineTextEditInfos.Combine(_queuedTokenEdits, new[] { tokenEdit });
            }
        }

        if (_tokenAwareReady && _queuedTokenEdits.Count > 0)
        {
            _astWithTokens = ParseTokenAware(_queuedTokenEdits, _astWithTokens, createImmutableLists: false);
            _queuedTokenEdits = Array.Empty<TextEditInfo>();
        }

        if (_initialAstWithoutTokens is not null && _queuedInitialEdits.Count > 0)
        {
            // Monaco updates both trees while background tokenization is in progress. Once line
            // token information is available, the same TextBufferTokenizer is used for this delta.
            _initialAstWithoutTokens = ParseTokenAware(_queuedInitialEdits, _initialAstWithoutTokens, createImmutableLists: false);
            _queuedInitialEdits = Array.Empty<TextEditInfo>();
        }
    }

    private AstNode ParseTokenAware(IReadOnlyList<TextEditInfo> edits, AstNode? oldRoot, bool createImmutableLists)
    {
        var tokenizer = new TextBufferBracketTreeTokenizer(_model, _tokenization, _brackets);
        return BracketParser.ParseDocument(tokenizer, edits, oldRoot, createImmutableLists).Root;
    }

    private LanguageAgnosticBracketTokens NewBracketTokens() =>
        new(id => _languages.GetLanguageConfiguration(id));

    private static BracketLength ToLength(int lineNumber, int column) =>
        BracketLength.From(Math.Max(0, lineNumber - 1), Math.Max(0, column - 1));

    private static LanguageRange ToRange(BracketLength start, BracketLength end) =>
        new(start.LineCount + 1, start.ColumnCount + 1, end.LineCount + 1, end.ColumnCount + 1);

    private static bool Overlaps(BracketLength start, BracketLength end, BracketLength queryStart, BracketLength queryEnd) =>
        start <= queryEnd && end >= queryStart;

    // Port of Monaco's collectBrackets traversal. It prunes the balanced tree by source length,
    // so a viewport query is proportional to tree height + brackets in/near that viewport.
    private static bool CollectBrackets(
        AstNode node,
        BracketLength nodeStart,
        BracketLength nodeEnd,
        BracketLength rangeStart,
        BracketLength rangeEnd,
        List<BracketTreeInfo> output,
        int level,
        int equalTypeLevel,
        Dictionary<string, int> levelPerType,
        bool onlyColorizedBrackets,
        bool parentIncomplete)
    {
        if (level > 200) return true;

        if (node is ListAstNode)
        {
            var childStart = nodeStart;
            for (var i = 0; i < node.ChildrenLength; i++)
            {
                var child = node.GetChild(i);
                if (child is null) continue;
                var childEnd = childStart + child.Length;
                if (Overlaps(childStart, childEnd, rangeStart, rangeEnd))
                    CollectBrackets(child, childStart, childEnd, rangeStart, rangeEnd, output, level, 0, levelPerType, onlyColorizedBrackets, parentIncomplete);
                childStart = childEnd;
                if (childStart > rangeEnd) break;
            }
            return true;
        }

        if (node is PairAstNode pair)
        {
            var colorize = !onlyColorizedBrackets || pair.ClosingBracket is null ||
                pair.ClosingBracket.ColorizedBracketIds.Intersects(pair.OpeningBracket.BracketIds);
            var existing = levelPerType.TryGetValue(pair.OpeningBracket.Text, out var value) ? value : 0;
            if (colorize) levelPerType[pair.OpeningBracket.Text] = existing + 1;
            var childStart = nodeStart;
            for (var i = 0; i < pair.ChildrenLength; i++)
            {
                var child = pair.GetChild(i);
                if (child is null) continue;
                var childEnd = childStart + child.Length;
                if (Overlaps(childStart, childEnd, rangeStart, rangeEnd) &&
                    (colorize || child.Kind != AstNodeKind.Bracket || pair.ClosingBracket is null))
                {
                    CollectBrackets(child, childStart, childEnd, rangeStart, rangeEnd, output,
                        colorize ? level + 1 : level, colorize ? existing + 1 : existing, levelPerType, onlyColorizedBrackets, pair.ClosingBracket is null);
                }
                childStart = childEnd;
                if (childStart > rangeEnd) break;
            }
            levelPerType[pair.OpeningBracket.Text] = existing;
            return true;
        }

        if (node.Kind == AstNodeKind.UnexpectedClosingBracket)
        {
            output.Add(new BracketTreeInfo(ToRange(nodeStart, nodeEnd), level - 1, 0, true));
        }
        else if (node.Kind == AstNodeKind.Bracket)
        {
            output.Add(new BracketTreeInfo(ToRange(nodeStart, nodeEnd), level - 1, equalTypeLevel - 1, parentIncomplete));
        }
        return true;
    }

    private static BracketTreeMatch? FindMatch(AstNode node, BracketLength nodeStart, BracketLength target)
    {
        if (node is PairAstNode pair)
        {
            var openingStart = nodeStart;
            var openingEnd = openingStart + pair.OpeningBracket.Length;
            var childStart = openingEnd;
            var childEnd = pair.Child is null ? childStart : childStart + pair.Child.Length;
            var closingStart = childEnd;
            var closingEnd = pair.ClosingBracket is null ? closingStart : closingStart + pair.ClosingBracket.Length;

            if (Contains(openingStart, openingEnd, target) && pair.ClosingBracket is not null)
                return new BracketTreeMatch(ToRange(openingStart, openingEnd), ToRange(closingStart, closingEnd));
            if (pair.ClosingBracket is not null && Contains(closingStart, closingEnd, target))
                return new BracketTreeMatch(ToRange(closingStart, closingEnd), ToRange(openingStart, openingEnd));
            if (pair.Child is not null && target >= childStart && target < childEnd)
                return FindMatch(pair.Child, childStart, target);
            return null;
        }

        if (node is ListAstNode)
        {
            var childStart = nodeStart;
            for (var i = 0; i < node.ChildrenLength; i++)
            {
                var child = node.GetChild(i);
                if (child is null) continue;
                var childEnd = childStart + child.Length;
                if (target >= childStart && target < childEnd)
                    return FindMatch(child, childStart, target);
                childStart = childEnd;
            }
        }
        return null;
    }

    private static bool Contains(BracketLength start, BracketLength end, BracketLength target) => target >= start && target < end;
}
