namespace MonacoNative.Language.BracketPairs;

internal sealed class NodeReader
{
    private readonly List<AstNode> _nextNodes;
    private readonly List<BracketLength> _offsets;
    private readonly List<int> _indices;
    private BracketLength _lastOffset = BracketLength.Zero;

    public NodeReader(AstNode node)
    {
        _nextNodes = new List<AstNode> { node };
        _offsets = new List<BracketLength> { BracketLength.Zero };
        _indices = new List<int>();
    }

    public AstNode? ReadLongestNodeAt(BracketLength offset, Func<AstNode, bool> predicate)
    {
        if (offset < _lastOffset)
            throw new InvalidOperationException("NodeReader offsets must be monotonically increasing.");
        _lastOffset = offset;

        while (true)
        {
            if (_nextNodes.Count == 0) return null;
            var curNode = _nextNodes[^1];
            var curOffset = _offsets[^1];

            if (offset < curOffset)
                return null;

            if (curOffset < offset)
            {
                if (curOffset + curNode.Length <= offset)
                {
                    NextNodeAfterCurrent();
                }
                else
                {
                    var childIndex = GetNextChildIndex(curNode);
                    if (childIndex != -1)
                    {
                        _nextNodes.Add(curNode.GetChild(childIndex)!);
                        _offsets.Add(curOffset);
                        _indices.Add(childIndex);
                    }
                    else
                    {
                        NextNodeAfterCurrent();
                    }
                }
            }
            else
            {
                if (predicate(curNode))
                {
                    NextNodeAfterCurrent();
                    return curNode;
                }

                var childIndex = GetNextChildIndex(curNode);
                if (childIndex == -1)
                {
                    NextNodeAfterCurrent();
                    return null;
                }
                _nextNodes.Add(curNode.GetChild(childIndex)!);
                _offsets.Add(curOffset);
                _indices.Add(childIndex);
            }
        }
    }

    private void NextNodeAfterCurrent()
    {
        while (true)
        {
            var currentOffset = _offsets.Count == 0 ? BracketLength.Zero : _offsets[^1];
            var currentNode = _nextNodes.Count == 0 ? null : _nextNodes[^1];
            if (_nextNodes.Count > 0) _nextNodes.RemoveAt(_nextNodes.Count - 1);
            if (_offsets.Count > 0) _offsets.RemoveAt(_offsets.Count - 1);

            if (_indices.Count == 0)
                break;

            var parent = _nextNodes[^1];
            var nextChildIndex = GetNextChildIndex(parent, _indices[^1]);
            if (nextChildIndex != -1)
            {
                _nextNodes.Add(parent.GetChild(nextChildIndex)!);
                _offsets.Add(currentOffset + currentNode!.Length);
                _indices[^1] = nextChildIndex;
                break;
            }
            _indices.RemoveAt(_indices.Count - 1);
        }
    }

    private static int GetNextChildIndex(AstNode node, int currentIndex = -1)
    {
        while (true)
        {
            currentIndex++;
            if (currentIndex >= node.ChildrenLength) return -1;
            if (node.GetChild(currentIndex) is not null) return currentIndex;
        }
    }
}
