using System.Globalization;
using System.Numerics;

namespace Monty.Runtime;

/// <summary>
/// <c>collections.deque</c>: a double-ended queue, optionally bounded.
/// </summary>
/// <remarks>
/// A bounded deque evicts from the far end when it overflows, which is what makes it a
/// sliding window. It is not a list subclass — <c>deque([1]) != [1]</c> — so it is its own
/// type rather than a wrapper over one.
/// </remarks>
public sealed class PyDeque : PyObject
{
    /// <summary>Creates a deque over <paramref name="items"/>.</summary>
    public PyDeque(IEnumerable<PyObject>? items = null, int? maxlen = null)
    {
        MaxLength = maxlen;

        foreach (var item in items ?? [])
        {
            Items.Add(item);
        }

        TrimLeft();
    }

    /// <summary>
    /// The <c>deque</c> type object.
    /// </summary>
    /// <remarks>
    /// One shared object, so <c>type(d) is deque</c> holds however the name was reached.
    /// </remarks>
    public static PyType Type { get; } = new(
        "collections.deque",
        static value => value is PyDeque,
        static (arguments, keywords) => Create(arguments, keywords));

    /// <summary>The elements, left to right.</summary>
    public List<PyObject> Items { get; } = [];

    /// <summary>
    /// Counts structural changes, so a live iterator can tell it has been invalidated.
    /// </summary>
    /// <remarks>
    /// A length check is not enough: CPython invalidates on any structural change, so an
    /// append paired with a popleft — which leaves the length alone — still stops the loop.
    /// Rearranging in place (reverse, item assignment) does not count.
    /// </remarks>
    private int _version;

    /// <summary>The bound, or null when unbounded.</summary>
    public int? MaxLength { get; }

    /// <inheritdoc />
    public override string TypeName => "collections.deque";

    /// <inheritdoc />
    public override bool IsTruthy() => Items.Count > 0;

    /// <inheritdoc />
    public override int? Length() => Items.Count;

    /// <inheritdoc />
    /// <remarks>
    /// Iterating follows the live deque, and CPython refuses to continue once its size
    /// changes — a mutation mid-loop is a bug, not a longer loop.
    /// </remarks>
    public override IEnumerable<PyObject>? Iterate()
    {
        var expected = _version;

        for (var i = 0; i < Items.Count; i++)
        {
            yield return Items[i];

            if (_version != expected)
            {
                throw new PyRaise(PyErrors.RuntimeError("deque mutated during iteration"));
            }
        }
    }

    /// <inheritdoc />
    public override string Repr()
    {
        if (!RecursionGuard.TryEnter(this))
        {
            return "[...]";
        }

        try
        {
            // A snapshot: a member whose `__repr__` mutates the deque changes nothing here.
            var body = "deque([" + string.Join(", ", Items.ToList().Select(static i => i.Repr())) + "]";
            return MaxLength is { } bound
                ? body + ", maxlen=" + bound.ToString(CultureInfo.InvariantCulture) + ")"
                : body + ")";
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    /// <remarks>The bound takes no part: two deques are equal when their items are.</remarks>
    public override bool PyEquals(PyObject other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is not PyDeque deque || deque.Items.Count != Items.Count)
        {
            return false;
        }

        RecursionGuard.EnterComparison(this);

        try
        {
            return PyList.SequenceEquals(Items, deque.Items);
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override int? PyCompare(PyObject other)
    {
        if (other is not PyDeque deque)
        {
            return null;
        }

        RecursionGuard.EnterComparison(this);

        try
        {
            return PyList.CompareSequences(Items, deque.Items);
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override bool Contains(PyObject item) =>
        Items.Any(candidate => SameOrEqual(candidate, item));

    /// <inheritdoc />
    /// <remarks>A deque has no slicing: it is indexed one element at a time.</remarks>
    public override PyObject GetItem(PyObject index) => Items[Position(index)];

    /// <inheritdoc />
    public override void SetItem(PyObject index, PyObject value) => Items[Position(index)] = value;

    /// <inheritdoc />
    public override void DeleteItem(PyObject index) => Items.RemoveAt(Position(index));

    /// <summary>
    /// Resolves a subscript to a position, reporting an index too large for the host as an
    /// IndexError rather than an overflow — which is how CPython words this one.
    /// </summary>
    private int Position(PyObject index)
    {
        if (index is not PyInt integer)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"sequence index must be integer, not '{index.TypeName}'"));
        }

        if (BigInteger.Abs(integer.Value) > int.MaxValue)
        {
            throw new PyRaise(PyErrors.IndexError("cannot fit 'int' into an index-sized integer"));
        }

        return PyStr.Normalize((int)integer.Value, Items.Count, "deque index out of range");
    }

    /// <summary>Reads a whole-number argument, refusing one the host cannot index with.</summary>
    private static BigInteger Whole(PyObject value)
    {
        if (value is not PyInt count)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"'{value.TypeName}' object cannot be interpreted as an integer"));
        }

        return BigInteger.Abs(count.Value) > long.MaxValue
            ? throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "Python int too large to convert to C ssize_t"))
            : count.Value;
    }

    /// <inheritdoc />
    /// <remarks>A deque is mutable, so it has no hash.</remarks>
    public override BigInteger PyHash() =>
        throw new PyRaise(PyErrors.TypeError($"unhashable type: '{TypeName}'"));

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value) =>
        name == "maxlen"
            ? throw new PyRaise(new PyException(
                PyExceptionType.AttributeError,
                $"attribute 'maxlen' of '{TypeName}' objects is not writable"))
            : base.SetAttribute(name, value);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "maxlen" => MaxLength is { } bound ? new PyInt(bound) : PyNone.Instance,

        "append" => Method(name, 1, arguments =>
        {
            Items.Add(arguments[0]);
            TrimLeft();
            _version++;
        }),

        "appendleft" => Method(name, 1, arguments =>
        {
            Items.Insert(0, arguments[0]);
            TrimRight();
            _version++;
        }),

        "extend" => Method(name, 1, arguments =>
        {
            foreach (var item in Source(arguments[0]))
            {
                Items.Add(item);
                TrimLeft();
                _version++;
            }
        }),

        "extendleft" => Method(name, 1, arguments =>
        {
            // Each item goes to the front, so the result is the source reversed.
            foreach (var item in Source(arguments[0]))
            {
                Items.Insert(0, item);
                TrimRight();
                _version++;
            }
        }),

        "pop" => new PyBoundMethod(name, this, (_, arguments, _) =>
        {
            Check("pop", arguments, 0, 0);
            return Take(Items.Count - 1, "pop from an empty deque");
        }),

        "popleft" => new PyBoundMethod(name, this, (_, arguments, _) =>
        {
            Check("popleft", arguments, 0, 0);
            return Take(0, "pop from an empty deque");
        }),

        "clear" => Method(name, 0, _ =>
        {
            Items.Clear();
            _version++;
        }),

        "copy" => new PyBoundMethod(name, this, (_, arguments, _) =>
        {
            Check("copy", arguments, 0, 0);
            return new PyDeque(Items, MaxLength);
        }),

        "count" => new PyBoundMethod(name, this, (_, arguments, _) =>
        {
            Check("count", arguments, 1, 1);
            return new PyInt(Items.Count(item => SameOrEqual(item, arguments[0])));
        }),

        "index" => new PyBoundMethod(name, this, (_, arguments, _) => Index(arguments)),

        "insert" => Method(name, 2, 2, arguments =>
        {
            if (MaxLength is { } bound && Items.Count >= bound)
            {
                throw new PyRaise(PyErrors.IndexError("deque already at its maximum size"));
            }

            Items.Insert(Resolve(Whole(arguments[0])), arguments[1]);
            _version++;
        }),

        "remove" => Method(name, 1, arguments =>
        {
            var found = Items.FindIndex(item => SameOrEqual(item, arguments[0]));

            if (found < 0)
            {
                throw new PyRaise(PyErrors.ValueError("deque.remove(x): x not in deque"));
            }

            Items.RemoveAt(found);
            _version++;
        }),

        "reverse" => Method(name, 0, _ => Items.Reverse()),
        "rotate" => Method(name, 0, 1, Rotate),

        _ => null,
    };

    /// <summary>Builds a deque from the constructor's arguments.</summary>
    public static PyDeque Create(PyObject[] arguments, PyDict? keywords)
    {
        if (arguments.Length > 2)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"deque() takes at most 2 arguments ({arguments.Length} given)"));
        }

        var source = arguments.Length > 0 ? arguments[0]
            : keywords?.TryGetValue(new PyStr("iterable"), out var named) == true ? named
            : null;

        var bound = arguments.Length > 1 ? arguments[1]
            : keywords?.TryGetValue(new PyStr("maxlen"), out var limit) == true ? limit
            : null;

        foreach (var (key, _) in keywords?.Entries ?? [])
        {
            if (key.Display() is not ("iterable" or "maxlen"))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"deque() got an unexpected keyword argument '{key.Display()}'"));
            }
        }

        // An omitted iterable builds an empty deque, but an explicit None is a real
        // argument and is refused like any other non-iterable.
        return new PyDeque(
            source is null ? [] : VirtualMachine.RequireIterable(source),
            Bound(bound));
    }

    /// <summary>Concatenation and repetition, which keep the left operand's bound.</summary>
    internal static PyDeque Concat(PyDeque left, PyObject right) =>
        right is PyDeque other
            ? new PyDeque([.. left.Items, .. other.Items], left.MaxLength)
            : throw new PyRaise(PyErrors.TypeError(
                $"can only concatenate deque (not \"{right.TypeName}\") to deque"));

    /// <inheritdoc cref="Concat" />
    internal static PyDeque Repeat(PyDeque deque, PyObject count)
    {
        if (count is not PyInt times)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"can't multiply sequence by non-int of type '{count.TypeName}'"));
        }

        if (times.Value > long.MaxValue || times.Value < long.MinValue)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "cannot fit 'int' into an index-sized integer"));
        }

        var items = new List<PyObject>();

        // The result is bounded, so a huge count must not build the whole product first:
        // only the last `maxlen` items can survive.
        var rounds = deque.MaxLength is { } bound && deque.Items.Count > 0
            ? BigInteger.Min(times.Value, (bound / deque.Items.Count) + 2)
            : times.Value;

        for (var i = BigInteger.Zero; i < rounds; i++)
        {
            items.AddRange(deque.Items);
        }

        return new PyDeque(items, deque.MaxLength);
    }

    /// <summary>Extends in place, which is what <c>+=</c> means for a deque.</summary>
    internal static PyDeque Extend(PyDeque deque, PyObject items)
    {
        foreach (var item in deque.Source(items))
        {
            deque.Items.Add(item);
            deque.TrimLeft();
            deque._version++;
        }

        return deque;
    }

    /// <summary>Repeats in place, truncating to the bound as it goes.</summary>
    internal static PyDeque RepeatInPlace(PyDeque deque, PyObject count)
    {
        var result = Repeat(deque, count);
        deque.Items.Clear();
        deque.Items.AddRange(result.Items);
        deque._version++;
        return deque;
    }

    /// <summary>
    /// The items to extend by.
    /// </summary>
    /// <remarks>
    /// The source is consumed as it yields, so one that raises part-way leaves the items
    /// that arrived — except when it is this deque itself, which is copied first so the
    /// extension terminates.
    /// </remarks>
    private IEnumerable<PyObject> Source(PyObject items) =>
        ReferenceEquals(items, this)
            ? Items.ToList()
            : VirtualMachine.RequireIterable(items);

    private static int? Bound(PyObject? value)
    {
        if (value is null or PyNone)
        {
            return null;
        }

        if (value is not PyInt limit)
        {
            throw new PyRaise(PyErrors.TypeError("an integer is required"));
        }

        // The conversion happens before the sign check, so a huge negative overflows
        // rather than being rejected as negative.
        if (BigInteger.Abs(limit.Value) > long.MaxValue)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "Python int too large to convert to C ssize_t"));
        }

        return limit.Value < 0
            ? throw new PyRaise(PyErrors.ValueError("maxlen must be non-negative"))
            : (int)limit.Value;
    }

    private PyBoundMethod Method(string name, int arity, Action<PyObject[]> mutate) =>
        new(name, this, (_, arguments, _) =>
        {
            Check(name, arguments, arity, arity);
            mutate(arguments);
            return PyNone.Instance;
        });

    private PyBoundMethod Method(string name, int minimum, int maximum, Action<PyObject[]> mutate) =>
        new(name, this, (_, arguments, _) =>
        {
            Check(name, arguments, minimum, maximum);
            mutate(arguments);
            return PyNone.Instance;
        });

    /// <summary>
    /// Enforces a method's arity.
    /// </summary>
    /// <remarks>
    /// CPython words this two ways, and the difference is visible: the one-argument and
    /// no-argument methods are declared METH_O and METH_NOARGS and name the type, while
    /// the rest go through PyArg_UnpackTuple, which does not.
    /// </remarks>
    private static void Check(string name, PyObject[] arguments, int minimum, int maximum)
    {
        if (arguments.Length >= minimum && arguments.Length <= maximum)
        {
            return;
        }

        var given = arguments.Length.ToString(CultureInfo.InvariantCulture);

        if (name is "append" or "appendleft" or "extend" or "extendleft" or "count" or "remove")
        {
            throw new PyRaise(PyErrors.TypeError(
                $"deque.{name}() takes exactly one argument ({given} given)"));
        }

        if (name is "copy" or "pop" or "popleft" or "clear" or "reverse")
        {
            throw new PyRaise(PyErrors.TypeError(
                $"deque.{name}() takes no arguments ({given} given)"));
        }

        throw new PyRaise(PyErrors.TypeError(minimum == maximum
            ? $"{name} expected {Count(minimum)}, got {given}"
            : arguments.Length < minimum
                ? $"{name} expected at least {Count(minimum)}, got {given}"
                : $"{name} expected at most {Count(maximum)}, got {given}"));
    }

    private static string Count(int arity) =>
        arity == 1 ? "1 argument" : $"{arity.ToString(CultureInfo.InvariantCulture)} arguments";

    private PyObject Take(int index, string message)
    {
        if (Items.Count == 0)
        {
            throw new PyRaise(PyErrors.IndexError(message));
        }

        var item = Items[index];
        Items.RemoveAt(index);
        _version++;
        return item;
    }

    private PyObject Index(PyObject[] arguments)
    {
        Check("index", arguments, 1, 3);

        // A bound is a slice index, so it reports itself that way — and unlike slicing,
        // an explicit None is a real argument rather than the "omitted" sentinel.
        var start = arguments.Length > 1 ? Bounds(arguments[1]) : 0;
        var end = arguments.Length > 2 ? Bounds(arguments[2]) : Items.Count;

        for (var i = start; i < Math.Min(end, Items.Count); i++)
        {
            if (SameOrEqual(Items[i], arguments[0]))
            {
                return new PyInt(i);
            }
        }

        throw new PyRaise(PyErrors.ValueError("deque.index(x): x not in deque"));
    }

    private void Rotate(PyObject[] arguments)
    {
        if (Items.Count == 0)
        {
            return;
        }

        // Even a rotation by zero counts as a mutation once there is more than one item,
        // which is what CPython's state counter records.
        if (Items.Count > 1)
        {
            _version++;
        }

        var steps = arguments.Length > 0 ? Whole(arguments[0]) : BigInteger.One;

        // Rotating by more than the length repeats, so only the remainder matters.
        var shift = (int)(((steps % Items.Count) + Items.Count) % Items.Count);

        if (shift == 0)
        {
            return;
        }

        var moved = Items.GetRange(Items.Count - shift, shift);
        Items.RemoveRange(Items.Count - shift, shift);
        Items.InsertRange(0, moved);
    }

    private int Bounds(PyObject value) => Resolve(value is PyInt index
        ? index.Value
        : throw new PyRaise(PyErrors.TypeError(
            "slice indices must be integers or have an __index__ method")));

    /// <summary>Resolves an index against the deque, clamping rather than overflowing.</summary>
    private int Resolve(BigInteger index)
    {
        var resolved = index < 0 ? Items.Count + index : index;
        return (int)BigInteger.Max(0, BigInteger.Min(resolved, Items.Count));
    }

    private void TrimLeft()
    {
        while (MaxLength is { } bound && Items.Count > bound)
        {
            Items.RemoveAt(0);
        }
    }

    private void TrimRight()
    {
        while (MaxLength is { } bound && Items.Count > bound)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }
}
