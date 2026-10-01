namespace MonacoNative.Language.BracketPairs;

/// <summary>
/// Monaco 0.57 bracket-tree edit composition. Combines edits from s0->s1 and s1->s2
/// into a single s0->s2 edit list so queued lazy reparses can still reuse the s0 AST.
/// Ported from VS Code 6a598d4 bracketPairsTree/combineTextEditInfos.ts.
/// </summary>
internal static class CombineTextEditInfos
{
    public static IReadOnlyList<TextEditInfo> Combine(IReadOnlyList<TextEditInfo> first, IReadOnlyList<TextEditInfo> second)
    {
        if (first.Count == 0) return second.ToArray();
        if (second.Count == 0) return first.ToArray();

        var q = new Queue<LengthMapping>(ToLengthMapping(first));
        var secondMap = ToLengthMapping(second).Cast<LengthMapping?>().ToList();
        secondMap.Add(null); // copy the remainder unchanged
        var current = q.Count > 0 ? q.Dequeue() : null;

        List<LengthMapping> TakeS0ToS1(BracketLength? s1Length)
        {
            if (s1Length is null)
            {
                var all = new List<LengthMapping>();
                if (current is not null) all.Add(current);
                while (q.Count > 0) all.Add(q.Dequeue());
                current = null;
                return all;
            }

            var remaining = s1Length.Value;
            var result = new List<LengthMapping>();
            while (current is not null && !remaining.IsZero)
            {
                var (head, tail) = current.SplitAt(remaining);
                result.Add(head);
                remaining = BracketLength.DiffNonNegative(head.LengthAfter, remaining);
                current = tail ?? (q.Count > 0 ? q.Dequeue() : null);
            }
            if (!remaining.IsZero)
                result.Add(new LengthMapping(false, remaining, remaining));
            return result;
        }

        var edits = new List<TextEditInfo>();
        void PushEdit(BracketLength start, BracketLength end, BracketLength newLength)
        {
            if (edits.Count > 0 && edits[^1].EndOffset.Value == start.Value)
            {
                var last = edits[^1];
                edits[^1] = new TextEditInfo(last.StartOffset, end, last.NewLength + newLength);
            }
            else
            {
                edits.Add(new TextEditInfo(start, end, newLength));
            }
        }

        var s0Offset = BracketLength.Zero;
        foreach (var s1ToS2 in secondMap)
        {
            var mappings = TakeS0ToS1(s1ToS2?.LengthBefore);
            if (s1ToS2 is { Modified: true })
            {
                var s0Length = Sum(mappings, m => m.LengthBefore);
                var s0End = s0Offset + s0Length;
                PushEdit(s0Offset, s0End, s1ToS2.LengthAfter);
                s0Offset = s0End;
            }
            else
            {
                foreach (var m in mappings)
                {
                    var start = s0Offset;
                    s0Offset += m.LengthBefore;
                    if (m.Modified)
                        PushEdit(start, s0Offset, m.LengthAfter);
                }
            }
        }
        return edits;
    }

    private static BracketLength Sum(IEnumerable<LengthMapping> items, Func<LengthMapping, BracketLength> selector)
    {
        var result = BracketLength.Zero;
        foreach (var item in items) result += selector(item);
        return result;
    }

    private static List<LengthMapping> ToLengthMapping(IReadOnlyList<TextEditInfo> edits)
    {
        var result = new List<LengthMapping>();
        var lastOffset = BracketLength.Zero;
        foreach (var edit in edits)
        {
            var space = BracketLength.DiffNonNegative(lastOffset, edit.StartOffset);
            if (!space.IsZero) result.Add(new LengthMapping(false, space, space));
            var before = BracketLength.DiffNonNegative(edit.StartOffset, edit.EndOffset);
            result.Add(new LengthMapping(true, before, edit.NewLength));
            lastOffset = edit.EndOffset;
        }
        return result;
    }

    private sealed record LengthMapping(bool Modified, BracketLength LengthBefore, BracketLength LengthAfter)
    {
        public (LengthMapping Head, LengthMapping? Tail) SplitAt(BracketLength lengthAfter)
        {
            var remaining = BracketLength.DiffNonNegative(lengthAfter, LengthAfter);
            if (remaining.IsZero) return (this, null);
            if (Modified)
                return (new LengthMapping(true, LengthBefore, lengthAfter), new LengthMapping(true, BracketLength.Zero, remaining));
            return (new LengthMapping(false, lengthAfter, lengthAfter), new LengthMapping(false, remaining, remaining));
        }
    }
}
