using System.Numerics;
using System.Text;

namespace Monty.Runtime;

/// <summary>
/// Guards against infinite recursion on self-referential containers.
/// </summary>
/// <remarks>
/// <c>a = []; a.append(a)</c> is legal Python, and both <c>repr</c> and <c>==</c> have to
/// terminate on it. CPython prints <c>[...]</c> and treats a container as equal to itself
/// on re-entry; this reproduces both.
/// </remarks>
internal static class RecursionGuard
{
    [ThreadStatic]
    private static HashSet<object>? active;

    /// <summary>Enters <paramref name="value"/>, or returns false when already inside it.</summary>
    public static bool TryEnter(object value)
    {
        active ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        return active.Add(value);
    }

    /// <summary>Leaves a container entered with <see cref="TryEnter"/>.</summary>
    public static void Exit(object value) => active?.Remove(value);
}

/// <summary><c>list</c>.</summary>
public sealed class PyList : PyObject
{
    /// <summary>Creates a list wrapping <paramref name="items"/>, which it then owns.</summary>
    public PyList(List<PyObject>? items = null) => Items = items ?? [];

    /// <summary>The elements.</summary>
    public List<PyObject> Items { get; }

    /// <inheritdoc />
    public override string TypeName => "list";

    /// <inheritdoc />
    public override bool IsTruthy() => Items.Count > 0;

    /// <inheritdoc />
    public override int? Length() => Items.Count;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => Items.ToList();

    /// <inheritdoc />
    public override string Repr()
    {
        if (!RecursionGuard.TryEnter(this))
        {
            return "[...]";
        }

        try
        {
            return "[" + string.Join(", ", Items.Select(static i => i.Repr())) + "]";
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is not PyList list || !RecursionGuard.TryEnter(this))
        {
            return other is PyList;
        }

        try
        {
            return SequenceEquals(Items, list.Items);
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) =>
        other is PyList list ? CompareSequences(Items, list.Items) : null;

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) => SequenceOps.GetItem(Items, index, "list", static items => new PyList(items));

    /// <inheritdoc />
    public override void SetItem(PyObject index, PyObject value)
    {
        if (index is PySlice slice)
        {
            SequenceOps.SetSlice(Items, slice, value);
            return;
        }

        if (index is not PyInt integer)
        {
            throw new PyRaise(PyErrors.TypeError($"list indices must be integers or slices, not {index.TypeName}"));
        }

        Items[PyStr.Normalize(integer.ToIndex(), Items.Count, "list assignment index out of range")] = value;
    }

    /// <inheritdoc />
    public override void DeleteItem(PyObject index)
    {
        if (index is PySlice slice)
        {
            var (start, _, step, count) = slice.Resolve(Items.Count);
            var positions = Enumerable.Range(0, count).Select(i => start + (i * step)).OrderDescending();

            foreach (var position in positions)
            {
                Items.RemoveAt(position);
            }

            return;
        }

        if (index is not PyInt integer)
        {
            throw new PyRaise(PyErrors.TypeError($"list indices must be integers or slices, not {index.TypeName}"));
        }

        Items.RemoveAt(PyStr.Normalize(integer.ToIndex(), Items.Count, "list assignment index out of range"));
    }

    internal static bool SequenceEquals(IReadOnlyList<PyObject> left, IReadOnlyList<PyObject> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].PyEquals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static int CompareSequences(IReadOnlyList<PyObject> left, IReadOnlyList<PyObject> right)
    {
        var shared = Math.Min(left.Count, right.Count);

        for (var i = 0; i < shared; i++)
        {
            if (left[i].PyEquals(right[i]))
            {
                continue;
            }

            return left[i].PyCompare(right[i])
                ?? throw new PyRaise(PyErrors.TypeError(
                    $"'<' not supported between instances of '{left[i].TypeName}' and '{right[i].TypeName}'"));
        }

        return left.Count.CompareTo(right.Count);
    }
}

/// <summary><c>tuple</c>.</summary>
public sealed class PyTuple : PyObject
{
    /// <summary>Creates a tuple.</summary>
    public PyTuple(IReadOnlyList<PyObject> items) => Items = items;

    /// <summary>The empty tuple.</summary>
    public static PyTuple Empty { get; } = new([]);

    /// <summary>The elements.</summary>
    public IReadOnlyList<PyObject> Items { get; }

    /// <inheritdoc />
    public override string TypeName => "tuple";

    /// <inheritdoc />
    public override bool IsTruthy() => Items.Count > 0;

    /// <inheritdoc />
    public override int? Length() => Items.Count;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => Items;

    /// <inheritdoc />
    public override string Repr()
    {
        if (!RecursionGuard.TryEnter(this))
        {
            return "(...)";
        }

        try
        {
            // A one-element tuple needs its trailing comma, or it reads as a
            // parenthesized value.
            return Items.Count == 1
                ? "(" + Items[0].Repr() + ",)"
                : "(" + string.Join(", ", Items.Select(static i => i.Repr())) + ")";
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) =>
        other is PyTuple tuple && PyList.SequenceEquals(Items, tuple.Items);

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) =>
        other is PyTuple tuple ? PyList.CompareSequences(Items, tuple.Items) : null;

    /// <inheritdoc />
    public override BigInteger PyHash()
    {
        var hash = new BigInteger(0x345678);

        foreach (var item in Items)
        {
            hash = ((hash * 1000003) ^ item.PyHash()) & 0xFFFFFFFFFFFFFFF;
        }

        return hash;
    }

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) =>
        SequenceOps.GetItem(Items, index, "tuple", static items => new PyTuple(items));
}

/// <summary>Shared sequence indexing and slicing.</summary>
internal static class SequenceOps
{
    public static PyObject GetItem<T>(IReadOnlyList<PyObject> items, PyObject index, string typeName, Func<List<PyObject>, T> build)
        where T : PyObject
    {
        if (index is PySlice slice)
        {
            var (start, _, step, count) = slice.Resolve(items.Count);
            var result = new List<PyObject>(count);

            for (var i = 0; i < count; i++)
            {
                result.Add(items[start + (i * step)]);
            }

            return build(result);
        }

        if (index is not PyInt integer)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{typeName} indices must be integers or slices, not {index.TypeName}"));
        }

        return items[PyStr.Normalize(integer.ToIndex(), items.Count, $"{typeName} index out of range")];
    }

    public static void SetSlice(List<PyObject> items, PySlice slice, PyObject value)
    {
        var replacement = value.Iterate()?.ToList()
            ?? throw new PyRaise(PyErrors.TypeError("can only assign an iterable"));

        var (start, _, step, count) = slice.Resolve(items.Count);

        if (step == 1)
        {
            items.RemoveRange(start, count);
            items.InsertRange(start, replacement);
            return;
        }

        // An extended slice must be replaced element for element.
        if (replacement.Count != count)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"attempt to assign sequence of size {replacement.Count} to extended slice of size {count}"));
        }

        for (var i = 0; i < count; i++)
        {
            items[start + (i * step)] = replacement[i];
        }
    }
}

/// <summary>
/// <c>dict</c>, preserving insertion order as Python guarantees.
/// </summary>
/// <remarks>
/// Keys are wrapped in <see cref="PyKey"/> so that <see cref="Dictionary{TKey,TValue}"/>
/// uses Python's hash and equality rather than .NET's — <c>1</c>, <c>1.0</c> and
/// <c>True</c> must be the same key, which reference or structural .NET equality would get
/// wrong in both directions.
/// </remarks>
public sealed class PyDict : PyObject
{
    private readonly Dictionary<PyKey, int> _index = [];
    private readonly List<KeyValuePair<PyObject, PyObject>> _entries = [];

    /// <summary>A shared empty dictionary, for bootstrapping.</summary>
    public static PyDict Empty { get; } = new();

    /// <inheritdoc />
    public override string TypeName => "dict";

    /// <summary>The entries, in insertion order.</summary>
    public IReadOnlyList<KeyValuePair<PyObject, PyObject>> Entries => _entries;

    /// <summary>The number of entries.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public override bool IsTruthy() => _entries.Count > 0;

    /// <inheritdoc />
    public override int? Length() => _entries.Count;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => _entries.Select(static e => e.Key).ToList();

    /// <inheritdoc />
    public override string Repr()
    {
        if (!RecursionGuard.TryEnter(this))
        {
            return "{...}";
        }

        try
        {
            return "{" + string.Join(", ", _entries.Select(static e => e.Key.Repr() + ": " + e.Value.Repr())) + "}";
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is not PyDict dict || dict.Count != Count)
        {
            return false;
        }

        if (!RecursionGuard.TryEnter(this))
        {
            return true;
        }

        try
        {
            foreach (var (key, value) in _entries)
            {
                if (!dict.TryGetValue(key, out var otherValue) || !value.PyEquals(otherValue))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override bool Contains(PyObject item) => TryGetValue(item, out _);

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) =>
        TryGetValue(index, out var value) ? value : throw new PyRaise(PyErrors.KeyError(index));

    /// <inheritdoc />
    public override void SetItem(PyObject index, PyObject value) => Set(index, value);

    /// <inheritdoc />
    public override void DeleteItem(PyObject index)
    {
        if (!Remove(index))
        {
            throw new PyRaise(PyErrors.KeyError(index));
        }
    }

    /// <summary>Reads a value by key.</summary>
    public bool TryGetValue(PyObject key, out PyObject value)
    {
        if (_index.TryGetValue(new PyKey(key), out var position))
        {
            value = _entries[position].Value;
            return true;
        }

        value = PyNone.Instance;
        return false;
    }

    /// <summary>Inserts or replaces a value, keeping the original insertion position.</summary>
    public void Set(PyObject key, PyObject value)
    {
        var wrapped = new PyKey(key);

        if (_index.TryGetValue(wrapped, out var position))
        {
            _entries[position] = new KeyValuePair<PyObject, PyObject>(_entries[position].Key, value);
            return;
        }

        _index[wrapped] = _entries.Count;
        _entries.Add(new KeyValuePair<PyObject, PyObject>(key, value));
    }

    /// <summary>Removes a key. Returns false when it was absent.</summary>
    public bool Remove(PyObject key)
    {
        var wrapped = new PyKey(key);

        if (!_index.Remove(wrapped, out var position))
        {
            return false;
        }

        _entries.RemoveAt(position);

        // Every later entry shifted down by one.
        foreach (var entry in _index.Where(pair => pair.Value > position).ToList())
        {
            _index[entry.Key] = entry.Value - 1;
        }

        return true;
    }

    /// <summary>Removes every entry.</summary>
    public void Clear()
    {
        _index.Clear();
        _entries.Clear();
    }

    /// <summary>A shallow copy.</summary>
    public PyDict Copy()
    {
        var copy = new PyDict();

        foreach (var (key, value) in _entries)
        {
            copy.Set(key, value);
        }

        return copy;
    }
}

/// <summary>Wraps a key so a .NET dictionary uses Python's hash and equality.</summary>
internal readonly struct PyKey(PyObject value) : IEquatable<PyKey>
{
    public PyObject Value { get; } = value;

    public bool Equals(PyKey other) => Value.PyEquals(other.Value);

    public override bool Equals(object? obj) => obj is PyKey other && Equals(other);

    public override int GetHashCode() => Value.PyHash().GetHashCode();
}

/// <summary><c>set</c>.</summary>
public sealed class PySet : PyObject
{
    private readonly Dictionary<PyKey, PyObject> _items = [];

    /// <summary>Creates a set, optionally seeded from <paramref name="items"/>.</summary>
    public PySet(IEnumerable<PyObject>? items = null)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            Add(item);
        }
    }

    /// <summary>
    /// True for a <c>frozenset</c>, which differs from a set only in being immutable and
    /// therefore hashable.
    /// </summary>
    public bool IsFrozen { get; init; }

    /// <inheritdoc />
    public override string TypeName => IsFrozen ? "frozenset" : "set";

    /// <summary>The members, in insertion order.</summary>
    public IReadOnlyCollection<PyObject> Items => _items.Values;

    /// <summary>The number of members.</summary>
    public int Count => _items.Count;

    /// <inheritdoc />
    public override bool IsTruthy() => _items.Count > 0;

    /// <inheritdoc />
    public override int? Length() => _items.Count;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => _items.Values.ToList();

    /// <inheritdoc />
    public override bool Contains(PyObject item) => _items.ContainsKey(new PyKey(item));

    /// <inheritdoc />
    public override string Repr()
    {
        // There is no empty-set literal; `set()` is the only way to write one. A frozenset
        // always names its type, since `{1, 2}` would read back as a set.
        if (_items.Count == 0)
        {
            return IsFrozen ? "frozenset()" : "set()";
        }

        var members = "{" + string.Join(", ", _items.Values.Select(static i => i.Repr())) + "}";
        return IsFrozen ? "frozenset(" + members + ")" : members;
    }

    /// <inheritdoc />
    public override BigInteger PyHash()
    {
        if (!IsFrozen)
        {
            return base.PyHash();
        }

        // Order must not matter, so the members' hashes are combined commutatively.
        var hash = BigInteger.Zero;

        foreach (var item in _items.Values)
        {
            hash ^= item.PyHash();
        }

        return hash;
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) =>
        other is PySet set && set.Count == Count && _items.Keys.All(set._items.ContainsKey);

    /// <summary>Adds a member. Returns false when it was already present.</summary>
    public bool Add(PyObject item) => _items.TryAdd(new PyKey(item), item);

    /// <summary>Removes a member. Returns false when it was absent.</summary>
    public bool Remove(PyObject item) => _items.Remove(new PyKey(item));

    /// <summary>Removes every member.</summary>
    public void Clear() => _items.Clear();
}

/// <summary><c>range</c>.</summary>
public sealed class PyRange : PyObject
{
    /// <summary>Creates a range.</summary>
    public PyRange(BigInteger start, BigInteger stop, BigInteger step)
    {
        if (step.IsZero)
        {
            throw new PyRaise(PyErrors.ValueError("range() arg 3 must not be zero"));
        }

        Start = start;
        Stop = stop;
        Step = step;
    }

    /// <summary>The first value.</summary>
    public BigInteger Start { get; }

    /// <summary>The exclusive bound.</summary>
    public BigInteger Stop { get; }

    /// <summary>The increment.</summary>
    public BigInteger Step { get; }

    /// <inheritdoc />
    public override string TypeName => "range";

    /// <summary>The number of values the range yields.</summary>
    public BigInteger Count
    {
        get
        {
            var span = Step > 0 ? Stop - Start : Start - Stop;
            if (span <= 0)
            {
                return BigInteger.Zero;
            }

            return ((span - 1) / BigInteger.Abs(Step)) + 1;
        }
    }

    /// <inheritdoc />
    public override bool IsTruthy() => Count > 0;

    /// <inheritdoc />
    public override int? Length()
    {
        var count = Count;

        // A range may be longer than an int can hold; `len()` on one is an OverflowError
        // in CPython too, not a host crash.
        if (count > int.MaxValue)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "cannot fit 'int' into an index-sized integer"));
        }

        return (int)count;
    }

    /// <inheritdoc />
    public override string Repr() =>
        Step.IsOne ? $"range({Start}, {Stop})" : $"range({Start}, {Stop}, {Step})";

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) =>
        other is PyRange range && Start == range.Start && Stop == range.Stop && Step == range.Step;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate()
    {
        var count = Count;

        for (BigInteger i = 0; i < count; i++)
        {
            yield return new PyInt(Start + (i * Step));
        }
    }

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index)
    {
        if (index is not PyInt integer)
        {
            throw new PyRaise(PyErrors.TypeError($"range indices must be integers, not {index.TypeName}"));
        }

        var count = Count;
        var position = integer.Value < 0 ? count + integer.Value : integer.Value;

        if (position < 0 || position >= count)
        {
            throw new PyRaise(PyErrors.IndexError("range object index out of range"));
        }

        return new PyInt(Start + (position * Step));
    }
}

/// <summary>
/// A <c>dict_keys</c>, <c>dict_values</c> or <c>dict_items</c> view.
/// </summary>
/// <remarks>
/// A view is not a list, and the difference is observable: its type name appears in error
/// messages and <c>repr</c>, and the key and item views take part in the set operators
/// while the value view does not.
/// </remarks>
public sealed class PyView : PyObject
{
    private readonly List<PyObject> _items;

    /// <summary>Creates a view.</summary>
    /// <param name="typeName">The Python type name, such as <c>dict_keys</c>.</param>
    /// <param name="items">The members.</param>
    /// <param name="isSetLike">Whether the view supports the set operators.</param>
    public PyView(string typeName, IEnumerable<PyObject> items, bool isSetLike)
    {
        TypeName = typeName;
        _items = [.. items];
        IsSetLike = isSetLike;
    }

    /// <inheritdoc />
    public override string TypeName { get; }

    /// <summary>Whether this view participates in the set operators.</summary>
    public bool IsSetLike { get; }

    /// <inheritdoc />
    public override int? Length() => _items.Count;

    /// <inheritdoc />
    public override bool IsTruthy() => _items.Count > 0;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => _items;

    /// <inheritdoc />
    public override bool Contains(PyObject item) => _items.Any(candidate => candidate.PyEquals(item));

    /// <inheritdoc />
    public override string Repr() =>
        TypeName + "([" + string.Join(", ", _items.Select(static i => i.Repr())) + "])";

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other switch
    {
        PyView view => _items.Count == view._items.Count
            && _items.All(item => view._items.Any(item.PyEquals)),

        PySet set => _items.Count == set.Count && _items.All(set.Contains),
        _ => false,
    };
}
