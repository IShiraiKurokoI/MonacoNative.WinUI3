namespace MonacoNative.Language.BracketPairs;

internal sealed record BracketParserToken(
    BracketLength Length,
    BracketTreeTokenKind Kind,
    int BracketId,
    SmallImmutableSet<int> BracketIds,
    AstNode AstNode);

internal interface IBracketTreeTokenizer
{
    BracketLength Offset { get; }
    BracketParserToken? Peek();
    BracketParserToken? Read();
    void Skip(BracketLength length);
}

internal sealed class ArrayBracketTreeTokenizer : IBracketTreeTokenizer
{
    private readonly IReadOnlyList<BracketParserToken> _tokens;
    private int _index;
    public ArrayBracketTreeTokenizer(IReadOnlyList<BracketParserToken> tokens) => _tokens = tokens;
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
            var token = Read() ?? throw new InvalidOperationException("Cannot skip beyond tokenizer end.");
            if (target < Offset)
                throw new InvalidOperationException("Cached AST node does not align with tokenizer token boundaries.");
        }
        if (Offset.Value != target.Value)
            throw new InvalidOperationException("Cached AST node does not align with tokenizer token boundaries.");
    }
}

internal sealed record BracketParseResult(AstNode Root, int NodesConstructed, int NodesReused);

internal static class BracketParser
{
    public static BracketParseResult ParseDocument(
        IBracketTreeTokenizer tokenizer,
        IReadOnlyList<TextEditInfo> edits,
        AstNode? oldNode,
        bool createImmutableLists)
    {
        var parser = new Parser(tokenizer, edits, oldNode, createImmutableLists);
        var root = parser.ParseDocument();
        return new BracketParseResult(root, parser.NodesConstructed, parser.NodesReused);
    }

    private sealed class Parser
    {
        private readonly IBracketTreeTokenizer _tokenizer;
        private readonly NodeReader? _oldNodeReader;
        private readonly BeforeEditPositionMapper _positionMapper;
        private readonly bool _createImmutableLists;
        public int NodesConstructed { get; private set; }
        public int NodesReused { get; private set; }

        public Parser(IBracketTreeTokenizer tokenizer, IReadOnlyList<TextEditInfo> edits, AstNode? oldNode, bool createImmutableLists)
        {
            if (oldNode is not null && createImmutableLists)
                throw new InvalidOperationException("Immutable list creation is only supported for non-incremental parses.");
            _tokenizer = tokenizer;
            _oldNodeReader = oldNode is null ? null : new NodeReader(oldNode);
            _positionMapper = new BeforeEditPositionMapper(edits);
            _createImmutableLists = createImmutableLists;
        }

        public AstNode ParseDocument()
        {
            NodesConstructed = 0;
            NodesReused = 0;
            return ParseList(SmallImmutableSet<int>.GetEmpty(), 0) ?? ListAstNode.GetEmpty();
        }

        private AstNode? ParseList(SmallImmutableSet<int> openedBracketIds, int level)
        {
            var items = new List<AstNode>();
            while (true)
            {
                var child = TryReadChildFromCache(openedBracketIds);
                if (child is null)
                {
                    var token = _tokenizer.Peek();
                    if (token is null || (token.Kind == BracketTreeTokenKind.ClosingBracket && token.BracketIds.Intersects(openedBracketIds)))
                        break;
                    child = ParseChild(openedBracketIds, level + 1);
                }

                if (child is ListAstNode list && list.ChildrenLength == 0)
                    continue;
                items.Add(child);
            }

            return _oldNodeReader is not null
                ? Concat23Trees.Concat(items)
                : Concat23Trees.ConcatSameHeight(items, _createImmutableLists);
        }

        private AstNode? TryReadChildFromCache(SmallImmutableSet<int> openedBracketIds)
        {
            if (_oldNodeReader is null) return null;
            var maxCacheableLength = _positionMapper.GetDistanceToNextChange(_tokenizer.Offset);
            if (maxCacheableLength is not null && maxCacheableLength.Value.IsZero)
                return null;

            var oldOffset = _positionMapper.GetOffsetBeforeChange(_tokenizer.Offset);
            var cached = _oldNodeReader.ReadLongestNodeAt(oldOffset, node =>
            {
                if (maxCacheableLength is not null && !(node.Length < maxCacheableLength.Value))
                    return false;
                return node.CanBeReused(openedBracketIds);
            });
            if (cached is null) return null;

            NodesReused++;
            _tokenizer.Skip(cached.Length);
            return cached;
        }

        private AstNode ParseChild(SmallImmutableSet<int> openedBracketIds, int level)
        {
            NodesConstructed++;
            var token = _tokenizer.Read() ?? throw new InvalidOperationException("Unexpected end of bracket token stream.");
            switch (token.Kind)
            {
                case BracketTreeTokenKind.ClosingBracket:
                    return new InvalidBracketAstNode(token.BracketIds, token.Length);
                case BracketTreeTokenKind.Text:
                    return token.AstNode;
                case BracketTreeTokenKind.OpeningBracket:
                    if (level > 300)
                        return new TextAstNode(token.Length);
                    var set = openedBracketIds.Merge(token.BracketIds);
                    var child = ParseList(set, level + 1);
                    var nextToken = _tokenizer.Peek();
                    if (nextToken is not null &&
                        nextToken.Kind == BracketTreeTokenKind.ClosingBracket &&
                        (nextToken.BracketId == token.BracketId || nextToken.BracketIds.Intersects(token.BracketIds)))
                    {
                        _tokenizer.Read();
                        return PairAstNode.Create((BracketAstNode)token.AstNode, child, (BracketAstNode)nextToken.AstNode);
                    }
                    return PairAstNode.Create((BracketAstNode)token.AstNode, child, null);
                default:
                    throw new InvalidOperationException("Unexpected bracket token kind.");
            }
        }
    }
}
