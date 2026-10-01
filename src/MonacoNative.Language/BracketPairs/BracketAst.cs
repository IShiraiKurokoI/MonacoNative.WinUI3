namespace MonacoNative.Language.BracketPairs;

internal enum AstNodeKind
{
    Text = 0,
    Bracket = 1,
    Pair = 2,
    UnexpectedClosingBracket = 3,
    List = 4,
}

internal abstract class AstNode
{
    protected AstNode(BracketLength length) => Length = length;

    public abstract AstNodeKind Kind { get; }
    public abstract int ChildrenLength { get; }
    public abstract AstNode? GetChild(int index);
    public abstract IReadOnlyList<AstNode> Children { get; }
    public abstract SmallImmutableSet<int> MissingOpeningBracketIds { get; }
    public abstract int ListHeight { get; }
    public BracketLength Length { get; protected set; }

    public abstract bool CanBeReused(SmallImmutableSet<int> openBracketIds);
    public abstract AstNode FlattenLists();
    public abstract AstNode DeepClone();
}

internal sealed class PairAstNode : AstNode
{
    private PairAstNode(
        BracketLength length,
        BracketAstNode openingBracket,
        AstNode? child,
        BracketAstNode? closingBracket,
        SmallImmutableSet<int> missingOpeningBracketIds)
        : base(length)
    {
        OpeningBracket = openingBracket;
        Child = child;
        ClosingBracket = closingBracket;
        MissingOpeningBracketIds = missingOpeningBracketIds;
    }

    public static PairAstNode Create(BracketAstNode openingBracket, AstNode? child, BracketAstNode? closingBracket)
    {
        var length = openingBracket.Length;
        if (child is not null) length += child.Length;
        if (closingBracket is not null) length += closingBracket.Length;
        return new PairAstNode(
            length,
            openingBracket,
            child,
            closingBracket,
            child?.MissingOpeningBracketIds ?? SmallImmutableSet<int>.GetEmpty());
    }

    public override AstNodeKind Kind => AstNodeKind.Pair;
    public override int ListHeight => 0;
    public override int ChildrenLength => 3;
    public BracketAstNode OpeningBracket { get; }
    public AstNode? Child { get; }
    public BracketAstNode? ClosingBracket { get; }
    public override SmallImmutableSet<int> MissingOpeningBracketIds { get; }

    public override AstNode? GetChild(int index) => index switch
    {
        0 => OpeningBracket,
        1 => Child,
        2 => ClosingBracket,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public override IReadOnlyList<AstNode> Children
    {
        get
        {
            var result = new List<AstNode>(3) { OpeningBracket };
            if (Child is not null) result.Add(Child);
            if (ClosingBracket is not null) result.Add(ClosingBracket);
            return result;
        }
    }

    public override bool CanBeReused(SmallImmutableSet<int> openBracketIds)
    {
        if (ClosingBracket is null)
            return false;
        return !openBracketIds.Intersects(MissingOpeningBracketIds);
    }

    public override AstNode FlattenLists() => Create(
        (BracketAstNode)OpeningBracket.FlattenLists(),
        Child?.FlattenLists(),
        ClosingBracket is null ? null : (BracketAstNode)ClosingBracket.FlattenLists());

    public override AstNode DeepClone() => new PairAstNode(
        Length,
        (BracketAstNode)OpeningBracket.DeepClone(),
        Child?.DeepClone(),
        ClosingBracket is null ? null : (BracketAstNode)ClosingBracket.DeepClone(),
        MissingOpeningBracketIds);
}

internal abstract class ListAstNode : AstNode
{
    private SmallImmutableSet<int> _missingOpeningBracketIds;

    protected ListAstNode(BracketLength length, int listHeight, SmallImmutableSet<int> missingOpeningBracketIds)
        : base(length)
    {
        ListHeight = listHeight;
        _missingOpeningBracketIds = missingOpeningBracketIds;
    }

    public static ListAstNode Create23(AstNode item1, AstNode item2, AstNode? item3, bool immutable = false)
    {
        if (item1.ListHeight != item2.ListHeight || (item3 is not null && item1.ListHeight != item3.ListHeight))
            throw new InvalidOperationException("Invalid list heights");

        var length = item1.Length + item2.Length;
        var missing = item1.MissingOpeningBracketIds.Merge(item2.MissingOpeningBracketIds);
        if (item3 is not null)
        {
            length += item3.Length;
            missing = missing.Merge(item3.MissingOpeningBracketIds);
        }

        return immutable
            ? new Immutable23ListAstNode(length, item1.ListHeight + 1, item1, item2, item3, missing)
            : new TwoThreeListAstNode(length, item1.ListHeight + 1, item1, item2, item3, missing);
    }

    public static ListAstNode Create(IReadOnlyList<AstNode> items, bool immutable = false)
    {
        if (items.Count == 0)
            return GetEmpty();

        var length = items[0].Length;
        var missing = items[0].MissingOpeningBracketIds;
        for (var i = 1; i < items.Count; i++)
        {
            length += items[i].Length;
            missing = missing.Merge(items[i].MissingOpeningBracketIds);
        }

        var copy = items.ToArray();
        return immutable
            ? new ImmutableArrayListAstNode(length, items[0].ListHeight + 1, copy, missing)
            : new ArrayListAstNode(length, items[0].ListHeight + 1, copy, missing);
    }

    public static ListAstNode GetEmpty() =>
        new ImmutableArrayListAstNode(BracketLength.Zero, 0, Array.Empty<AstNode>(), SmallImmutableSet<int>.GetEmpty());

    public override AstNodeKind Kind => AstNodeKind.List;
    public override int ListHeight { get; }
    public override SmallImmutableSet<int> MissingOpeningBracketIds => _missingOpeningBracketIds;

    protected virtual void ThrowIfImmutable() { }
    protected abstract void SetChild(int index, AstNode child);

    public AstNode? MakeLastElementMutable()
    {
        ThrowIfImmutable();
        if (ChildrenLength == 0) return null;
        var index = ChildrenLength - 1;
        var child = GetChild(index)!;
        var mutable = child is ListAstNode list ? list.ToMutable() : child;
        if (!ReferenceEquals(child, mutable)) SetChild(index, mutable);
        return mutable;
    }

    public AstNode? MakeFirstElementMutable()
    {
        ThrowIfImmutable();
        if (ChildrenLength == 0) return null;
        var child = GetChild(0)!;
        var mutable = child is ListAstNode list ? list.ToMutable() : child;
        if (!ReferenceEquals(child, mutable)) SetChild(0, mutable);
        return mutable;
    }

    public override bool CanBeReused(SmallImmutableSet<int> openBracketIds)
    {
        if (openBracketIds.Intersects(MissingOpeningBracketIds) || ChildrenLength == 0)
            return false;

        AstNode lastChild = this;
        while (lastChild is ListAstNode list)
        {
            if (list.ChildrenLength == 0)
                throw new InvalidOperationException("Empty lists must not be nested in lists.");
            lastChild = list.GetChild(list.ChildrenLength - 1)!;
        }
        return lastChild.CanBeReused(openBracketIds);
    }

    public void HandleChildrenChanged()
    {
        ThrowIfImmutable();
        if (ChildrenLength == 0)
        {
            Length = BracketLength.Zero;
            _missingOpeningBracketIds = SmallImmutableSet<int>.GetEmpty();
            return;
        }

        var length = GetChild(0)!.Length;
        var missing = GetChild(0)!.MissingOpeningBracketIds;
        for (var i = 1; i < ChildrenLength; i++)
        {
            var child = GetChild(i)!;
            length += child.Length;
            missing = missing.Merge(child.MissingOpeningBracketIds);
        }
        Length = length;
        _missingOpeningBracketIds = missing;
    }

    public override AstNode FlattenLists()
    {
        var items = new List<AstNode>();
        foreach (var child in Children)
        {
            var normalized = child.FlattenLists();
            if (normalized is ListAstNode list)
                items.AddRange(list.Children);
            else
                items.Add(normalized);
        }
        return Create(items);
    }

    public abstract ListAstNode ToMutable();
    public abstract void AppendChildOfSameHeight(AstNode node);
    public abstract AstNode? UnappendChild();
    public abstract void PrependChildOfSameHeight(AstNode node);
    public abstract AstNode? UnprependChild();
}

internal class TwoThreeListAstNode : ListAstNode
{
    private AstNode _item1;
    private AstNode _item2;
    private AstNode? _item3;

    public TwoThreeListAstNode(
        BracketLength length,
        int listHeight,
        AstNode item1,
        AstNode item2,
        AstNode? item3,
        SmallImmutableSet<int> missingOpeningBracketIds)
        : base(length, listHeight, missingOpeningBracketIds)
    {
        _item1 = item1;
        _item2 = item2;
        _item3 = item3;
    }

    public AstNode Item1 => _item1;
    public AstNode Item2 => _item2;
    public AstNode? Item3 => _item3;
    public override int ChildrenLength => _item3 is null ? 2 : 3;
    public override AstNode? GetChild(int index) => index switch
    {
        0 => _item1,
        1 => _item2,
        2 => _item3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override void SetChild(int index, AstNode child)
    {
        switch (index)
        {
            case 0: _item1 = child; break;
            case 1: _item2 = child; break;
            case 2: _item3 = child; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public override IReadOnlyList<AstNode> Children =>
        _item3 is null ? new AstNode[] { _item1, _item2 } : new AstNode[] { _item1, _item2, _item3! };

    public override AstNode DeepClone() => new TwoThreeListAstNode(
        Length,
        ListHeight,
        _item1.DeepClone(),
        _item2.DeepClone(),
        _item3?.DeepClone(),
        MissingOpeningBracketIds);

    public override void AppendChildOfSameHeight(AstNode node)
    {
        if (_item3 is not null) throw new InvalidOperationException("Cannot append to a full (2,3) tree node");
        ThrowIfImmutable();
        _item3 = node;
        HandleChildrenChanged();
    }

    public override AstNode? UnappendChild()
    {
        if (_item3 is null) throw new InvalidOperationException("Cannot remove from a non-full (2,3) tree node");
        ThrowIfImmutable();
        var result = _item3;
        _item3 = null;
        HandleChildrenChanged();
        return result;
    }

    public override void PrependChildOfSameHeight(AstNode node)
    {
        if (_item3 is not null) throw new InvalidOperationException("Cannot prepend to a full (2,3) tree node");
        ThrowIfImmutable();
        _item3 = _item2;
        _item2 = _item1;
        _item1 = node;
        HandleChildrenChanged();
    }

    public override AstNode? UnprependChild()
    {
        if (_item3 is null) throw new InvalidOperationException("Cannot remove from a non-full (2,3) tree node");
        ThrowIfImmutable();
        var result = _item1;
        _item1 = _item2;
        _item2 = _item3;
        _item3 = null;
        HandleChildrenChanged();
        return result;
    }

    public override ListAstNode ToMutable() => this;
}

internal sealed class Immutable23ListAstNode : TwoThreeListAstNode
{
    public Immutable23ListAstNode(
        BracketLength length,
        int listHeight,
        AstNode item1,
        AstNode item2,
        AstNode? item3,
        SmallImmutableSet<int> missingOpeningBracketIds)
        : base(length, listHeight, item1, item2, item3, missingOpeningBracketIds) { }

    public override ListAstNode ToMutable() =>
        new TwoThreeListAstNode(Length, ListHeight, Item1, Item2, Item3, MissingOpeningBracketIds);

    protected override void ThrowIfImmutable() => throw new InvalidOperationException("This AST node is immutable.");
}

internal class ArrayListAstNode : ListAstNode
{
    private readonly List<AstNode> _children;

    public ArrayListAstNode(
        BracketLength length,
        int listHeight,
        IReadOnlyList<AstNode> children,
        SmallImmutableSet<int> missingOpeningBracketIds)
        : base(length, listHeight, missingOpeningBracketIds) => _children = children.ToList();

    public override int ChildrenLength => _children.Count;
    public override AstNode? GetChild(int index) => _children[index];
    protected override void SetChild(int index, AstNode child) => _children[index] = child;
    public override IReadOnlyList<AstNode> Children => _children;

    public override AstNode DeepClone() => new ArrayListAstNode(
        Length,
        ListHeight,
        _children.Select(c => c.DeepClone()).ToArray(),
        MissingOpeningBracketIds);

    public override void AppendChildOfSameHeight(AstNode node)
    {
        ThrowIfImmutable();
        _children.Add(node);
        HandleChildrenChanged();
    }

    public override AstNode? UnappendChild()
    {
        ThrowIfImmutable();
        if (_children.Count == 0) return null;
        var result = _children[^1];
        _children.RemoveAt(_children.Count - 1);
        HandleChildrenChanged();
        return result;
    }

    public override void PrependChildOfSameHeight(AstNode node)
    {
        ThrowIfImmutable();
        _children.Insert(0, node);
        HandleChildrenChanged();
    }

    public override AstNode? UnprependChild()
    {
        ThrowIfImmutable();
        if (_children.Count == 0) return null;
        var result = _children[0];
        _children.RemoveAt(0);
        HandleChildrenChanged();
        return result;
    }

    public override ListAstNode ToMutable() => this;
}

internal sealed class ImmutableArrayListAstNode : ArrayListAstNode
{
    public ImmutableArrayListAstNode(
        BracketLength length,
        int listHeight,
        IReadOnlyList<AstNode> children,
        SmallImmutableSet<int> missingOpeningBracketIds)
        : base(length, listHeight, children, missingOpeningBracketIds) { }

    public override ListAstNode ToMutable() =>
        new ArrayListAstNode(Length, ListHeight, Children.ToArray(), MissingOpeningBracketIds);

    protected override void ThrowIfImmutable() => throw new InvalidOperationException("This AST node is immutable.");
}

internal abstract class ImmutableLeafAstNode : AstNode
{
    protected ImmutableLeafAstNode(BracketLength length) : base(length) { }
    public override int ListHeight => 0;
    public override int ChildrenLength => 0;
    public override AstNode? GetChild(int index) => null;
    public override IReadOnlyList<AstNode> Children => Array.Empty<AstNode>();
    public override AstNode FlattenLists() => this;
    public override AstNode DeepClone() => this;
}

internal sealed class TextAstNode : ImmutableLeafAstNode
{
    public TextAstNode(BracketLength length) : base(length) { }
    public override AstNodeKind Kind => AstNodeKind.Text;
    public override SmallImmutableSet<int> MissingOpeningBracketIds => SmallImmutableSet<int>.GetEmpty();
    public override bool CanBeReused(SmallImmutableSet<int> openBracketIds) => true;
}

internal sealed class BracketAstNode : ImmutableLeafAstNode
{
    private BracketAstNode(BracketLength length, string text, string languageId, SmallImmutableSet<int> bracketIds, SmallImmutableSet<int> colorizedBracketIds)
        : base(length)
    {
        Text = text;
        LanguageId = languageId;
        BracketIds = bracketIds;
        ColorizedBracketIds = colorizedBracketIds;
    }

    public static BracketAstNode Create(BracketLength length, string text, string languageId, SmallImmutableSet<int> bracketIds, SmallImmutableSet<int>? colorizedBracketIds = null) =>
        new(length, text, languageId, bracketIds, colorizedBracketIds ?? bracketIds);

    public override AstNodeKind Kind => AstNodeKind.Bracket;
    public override SmallImmutableSet<int> MissingOpeningBracketIds => SmallImmutableSet<int>.GetEmpty();
    public SmallImmutableSet<int> BracketIds { get; }
    public SmallImmutableSet<int> ColorizedBracketIds { get; }
    public string Text { get; }
    public string LanguageId { get; }
    public override bool CanBeReused(SmallImmutableSet<int> openBracketIds) => false;
}

internal sealed class InvalidBracketAstNode : ImmutableLeafAstNode
{
    public InvalidBracketAstNode(SmallImmutableSet<int> closingBrackets, BracketLength length) : base(length) =>
        MissingOpeningBracketIds = closingBrackets;

    public override AstNodeKind Kind => AstNodeKind.UnexpectedClosingBracket;
    public override SmallImmutableSet<int> MissingOpeningBracketIds { get; }
    public override bool CanBeReused(SmallImmutableSet<int> openedBracketIds) =>
        !openedBracketIds.Intersects(MissingOpeningBracketIds);
}
