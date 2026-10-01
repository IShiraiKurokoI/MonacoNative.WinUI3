namespace MonacoNative.Language.BracketPairs;

internal static class Concat23Trees
{
    public static AstNode? Concat(IList<AstNode> items)
    {
        if (items.Count == 0) return null;
        if (items.Count == 1) return items[0];

        var i = 0;
        AstNode? ReadNode()
        {
            if (i >= items.Count) return null;
            var start = i;
            var height = items[start].ListHeight;
            i++;
            while (i < items.Count && items[i].ListHeight == height) i++;
            if (i - start >= 2)
            {
                var group = start == 0 && i == items.Count
                    ? items.ToList()
                    : items.Skip(start).Take(i - start).ToList();
                return ConcatSameHeight(group, false);
            }
            return items[start];
        }

        var first = ReadNode()!;
        var second = ReadNode();
        if (second is null) return first;
        for (var item = ReadNode(); item is not null; item = ReadNode())
        {
            if (HeightDiff(first, second) <= HeightDiff(second, item))
            {
                first = ConcatTwo(first, second);
                second = item;
            }
            else
            {
                second = ConcatTwo(second, item);
            }
        }
        return ConcatTwo(first, second);
    }

    public static AstNode? ConcatSameHeight(IList<AstNode> items, bool createImmutableLists = false)
    {
        if (items.Count == 0) return null;
        if (items.Count == 1) return items[0];

        var work = items.ToList();
        var length = work.Count;
        while (length > 3)
        {
            var newLength = length >> 1;
            for (var i = 0; i < newLength; i++)
            {
                var j = i << 1;
                work[i] = ListAstNode.Create23(work[j], work[j + 1], j + 3 == length ? work[j + 2] : null, createImmutableLists);
            }
            length = newLength;
        }
        return ListAstNode.Create23(work[0], work[1], length >= 3 ? work[2] : null, createImmutableLists);
    }

    private static int HeightDiff(AstNode a, AstNode b) => Math.Abs(a.ListHeight - b.ListHeight);

    private static AstNode ConcatTwo(AstNode node1, AstNode node2)
    {
        if (node1.ListHeight == node2.ListHeight)
            return ListAstNode.Create23(node1, node2, null);
        return node1.ListHeight > node2.ListHeight
            ? Append((ListAstNode)node1, node2)
            : Prepend((ListAstNode)node2, node1);
    }

    private static AstNode Append(ListAstNode list, AstNode nodeToAppend)
    {
        list = list.ToMutable();
        AstNode curNode = list;
        var parents = new List<ListAstNode>();
        AstNode? nodeOfCorrectHeight = null;
        while (true)
        {
            if (nodeToAppend.ListHeight == curNode.ListHeight)
            {
                nodeOfCorrectHeight = nodeToAppend;
                break;
            }
            if (curNode is not ListAstNode curList)
                throw new InvalidOperationException("Unexpected non-list while descending (2,3) tree.");
            parents.Add(curList);
            curNode = curList.MakeLastElementMutable()!;
        }

        for (var i = parents.Count - 1; i >= 0; i--)
        {
            var parent = parents[i];
            if (nodeOfCorrectHeight is not null)
            {
                if (parent.ChildrenLength >= 3)
                    nodeOfCorrectHeight = ListAstNode.Create23(parent.UnappendChild()!, nodeOfCorrectHeight, null);
                else
                {
                    parent.AppendChildOfSameHeight(nodeOfCorrectHeight);
                    nodeOfCorrectHeight = null;
                }
            }
            else
            {
                parent.HandleChildrenChanged();
            }
        }

        return nodeOfCorrectHeight is not null
            ? ListAstNode.Create23(list, nodeOfCorrectHeight, null)
            : list;
    }

    private static AstNode Prepend(ListAstNode list, AstNode nodeToPrepend)
    {
        list = list.ToMutable();
        AstNode curNode = list;
        var parents = new List<ListAstNode>();
        while (nodeToPrepend.ListHeight != curNode.ListHeight)
        {
            if (curNode is not ListAstNode curList)
                throw new InvalidOperationException("Unexpected non-list while descending (2,3) tree.");
            parents.Add(curList);
            curNode = curList.MakeFirstElementMutable()!;
        }

        AstNode? nodeOfCorrectHeight = nodeToPrepend;
        for (var i = parents.Count - 1; i >= 0; i--)
        {
            var parent = parents[i];
            if (nodeOfCorrectHeight is not null)
            {
                if (parent.ChildrenLength >= 3)
                    nodeOfCorrectHeight = ListAstNode.Create23(nodeOfCorrectHeight, parent.UnprependChild()!, null);
                else
                {
                    parent.PrependChildOfSameHeight(nodeOfCorrectHeight);
                    nodeOfCorrectHeight = null;
                }
            }
            else
            {
                parent.HandleChildrenChanged();
            }
        }

        return nodeOfCorrectHeight is not null
            ? ListAstNode.Create23(nodeOfCorrectHeight, list, null)
            : list;
    }
}
