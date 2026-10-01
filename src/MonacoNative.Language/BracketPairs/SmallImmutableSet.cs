namespace MonacoNative.Language.BracketPairs;

internal interface IDenseKeyProvider<in T>
{
    int GetKey(T value);
}

/// <summary>
/// Dense-key provider used by Monaco's bracket parser to encode small sets as bitsets.
/// </summary>
internal sealed class DenseKeyProvider<T> : IDenseKeyProvider<T> where T : notnull
{
    private readonly Dictionary<T, int> _items;

    public DenseKeyProvider(IEqualityComparer<T>? comparer = null) =>
        _items = new Dictionary<T, int>(comparer ?? EqualityComparer<T>.Default);

    public int GetKey(T value)
    {
        if (_items.TryGetValue(value, out var key))
            return key;
        key = _items.Count;
        _items.Add(value, key);
        return key;
    }

    public bool TryReverseLookup(int value, out T? item)
    {
        foreach (var pair in _items)
        {
            if (pair.Value == value)
            {
                item = pair.Key;
                return true;
            }
        }
        item = default;
        return false;
    }

    public IEnumerable<T> Keys => _items.Keys;
}

internal sealed class IdentityDenseKeyProvider : IDenseKeyProvider<int>
{
    public static readonly IdentityDenseKeyProvider Instance = new();
    private IdentityDenseKeyProvider() { }
    public int GetKey(int value) => value;
}

/// <summary>
/// Immutable bitset optimized for the small bracket-id sets used by Monaco. The first
/// 32 dense keys are stored inline and additional 32-bit words are allocated only when needed.
/// </summary>
internal sealed class SmallImmutableSet<T>
{
    private static readonly SmallImmutableSet<T>?[] Cache = new SmallImmutableSet<T>[129];
    private static readonly uint[] EmptyAdditional = Array.Empty<uint>();
    private static readonly SmallImmutableSet<T> Empty = Create(0, EmptyAdditional);

    private readonly uint _items;
    private readonly uint[] _additionalItems;

    private SmallImmutableSet(uint items, uint[] additionalItems)
    {
        _items = items;
        _additionalItems = additionalItems;
    }

    public static SmallImmutableSet<T> GetEmpty() => Empty;

    public SmallImmutableSet<T> Add(T value, IDenseKeyProvider<T> keyProvider)
    {
        var key = keyProvider.GetKey(value);
        if (key < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        var idx = key >> 5;
        if (idx == 0)
        {
            var newItems = _items | (1u << (key & 31));
            return newItems == _items ? this : Create(newItems, _additionalItems);
        }

        idx--;
        var length = Math.Max(_additionalItems.Length, idx + 1);
        var additional = new uint[length];
        Array.Copy(_additionalItems, additional, _additionalItems.Length);
        var bit = 1u << (key & 31);
        if ((additional[idx] & bit) != 0)
            return this;
        additional[idx] |= bit;
        return Create(_items, additional);
    }

    public bool Has(T value, IDenseKeyProvider<T> keyProvider)
    {
        var key = keyProvider.GetKey(value);
        if (key < 0)
            return false;
        var idx = key >> 5;
        if (idx == 0)
            return (_items & (1u << (key & 31))) != 0;
        idx--;
        return idx < _additionalItems.Length && (_additionalItems[idx] & (1u << (key & 31))) != 0;
    }

    public SmallImmutableSet<T> Merge(SmallImmutableSet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var merged = _items | other._items;
        if (_additionalItems.Length == 0 && other._additionalItems.Length == 0)
        {
            if (merged == _items) return this;
            if (merged == other._items) return other;
            return Create(merged, EmptyAdditional);
        }

        var length = Math.Max(_additionalItems.Length, other._additionalItems.Length);
        var additional = new uint[length];
        for (var i = 0; i < length; i++)
        {
            var a = i < _additionalItems.Length ? _additionalItems[i] : 0u;
            var b = i < other._additionalItems.Length ? other._additionalItems[i] : 0u;
            additional[i] = a | b;
        }
        return Create(merged, additional);
    }

    public bool Intersects(SmallImmutableSet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if ((_items & other._items) != 0)
            return true;
        var length = Math.Min(_additionalItems.Length, other._additionalItems.Length);
        for (var i = 0; i < length; i++)
        {
            if ((_additionalItems[i] & other._additionalItems[i]) != 0)
                return true;
        }
        return false;
    }

    public bool SetEquals(SmallImmutableSet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_items != other._items || _additionalItems.Length != other._additionalItems.Length)
            return false;
        for (var i = 0; i < _additionalItems.Length; i++)
        {
            if (_additionalItems[i] != other._additionalItems[i])
                return false;
        }
        return true;
    }

    private static SmallImmutableSet<T> Create(uint items, uint[] additionalItems)
    {
        if (items <= 128 && additionalItems.Length == 0)
        {
            var index = checked((int)items);
            return Cache[index] ??= new SmallImmutableSet<T>(items, EmptyAdditional);
        }
        return new SmallImmutableSet<T>(items, additionalItems);
    }
}
