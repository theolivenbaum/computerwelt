using System.Numerics;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// <c>collections.Counter</c>: a dict whose values are counts.
/// </summary>
/// <remarks>
/// The counts are ordinary Python values, never coerced — a float, a bool or a big integer
/// stored as a count comes back out unchanged, which is what makes a Counter usable as a
/// tally of anything additive rather than only of small integers.
/// </remarks>
public sealed class PyCounter : PyDict
{
    /// <summary>
    /// The <c>Counter</c> type object.
    /// </summary>
    /// <remarks>
    /// One shared object, so <c>type(c) is Counter</c> and <c>isinstance(c, Counter)</c>
    /// hold however the name was reached. It is deliberately not in the builtin type
    /// registry: <c>Counter</c> is a name from <c>collections</c>, not a builtin.
    /// </remarks>
    public static PyType Type { get; } = new(
        "Counter",
        static value => value is PyCounter,
        static (arguments, keywords) => Create(arguments, keywords));

    /// <inheritdoc />
    public override string TypeName => "Counter";

    /// <summary>The zero every missing key counts as.</summary>
    private static PyInt Zero => new(0);

    /// <inheritdoc />
    /// <remarks>A missing key counts as zero, and reading one does not insert it.</remarks>
    public override PyObject GetItem(PyObject index) =>
        TryGetValue(index, out var value) ? value : Zero;

    /// <inheritdoc />
    /// <remarks>
    /// Between two Counters a zero count is indistinguishable from an absent key, because
    /// the comparison runs over the union of the keys. Against anything else this is plain
    /// dict equality, where a zero count is a real entry.
    /// </remarks>
    public override bool PyEquals(PyObject other)
    {
        if (other is not PyCounter counter)
        {
            return base.PyEquals(other);
        }

        foreach (var key in Entries.Select(static e => e.Key)
            .Concat(counter.Entries.Select(static e => e.Key).Where(key => !Contains(key))))
        {
            if (!GetItem(key).PyEquals(counter.GetItem(key)))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override string Repr()
    {
        if (Count == 0)
        {
            return "Counter()";
        }

        if (!RecursionGuard.TryEnter(this))
        {
            return "Counter({...})";
        }

        try
        {
            return "Counter({"
                // A snapshot: the ordering pass has already read every entry, so an item
                // whose `__repr__` mutates the counter changes nothing here.
                + string.Join(", ", MostCommon(null).ToList().Select(e => e.Key.Repr() + ": " + e.Value.Repr()))
                + "})";
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "most_common" => new PyBuiltinFunction("most_common", arguments => new PyList(
            [.. MostCommon(Limit(arguments))
                .Select(static e => (PyObject)new PyTuple([e.Key, e.Value]))])),

        "elements" => new PyBuiltinFunction("elements", _ => new PyIterator(Elements())),
        "total" => new PyBuiltinFunction("total", _ => Total()),
        "copy" => new PyBuiltinFunction("copy", _ => CopyCounter()),

        "update" => new PyBuiltinFunction("update", (arguments, keywords) =>
        {
            Combine("update", arguments, keywords, subtract: false);
            return PyNone.Instance;
        }),

        "subtract" => new PyBuiltinFunction("subtract", (arguments, keywords) =>
        {
            Combine("subtract", arguments, keywords, subtract: true);
            return PyNone.Instance;
        }),

        // CPython disables the inherited classmethod rather than letting it build a
        // Counter whose counts are all the same value.
        "fromkeys" => new PyBuiltinFunction("fromkeys", static _ =>
            throw new PyRaise(new PyException(
                PyExceptionType.NotImplementedError,
                "Counter.fromkeys() is undefined.  Use Counter(iterable) instead."))),

        _ => base.GetAttribute(name),
    };

    /// <summary>
    /// Reads <c>most_common</c>'s optional count, which clamps rather than overflowing: a
    /// huge positive returns everything and a negative returns nothing.
    /// </summary>
    private static int? Limit(PyObject[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] is PyNone)
        {
            return null;
        }

        if (arguments[0] is not PyInt count)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"'{arguments[0].TypeName}' object cannot be interpreted as an integer"));
        }

        return count.Value > int.MaxValue ? int.MaxValue
            : count.Value < 0 ? 0
            : (int)count.Value;
    }

    /// <summary>Builds a Counter from a mapping or iterable and keyword counts.</summary>
    public static PyCounter Create(PyObject[] arguments, PyDict? keywords)
    {
        var counter = new PyCounter();
        counter.Combine("Counter", arguments, keywords, subtract: false);
        return counter;
    }

    /// <summary>The entries in count-descending order, ties in insertion order.</summary>
    private List<KeyValuePair<PyObject, PyObject>> MostCommon(int? limit)
    {
        // A stable sort is what keeps ties in insertion order, which the repr shows.
        var ordered = Entries
            .Select(static (entry, position) => (entry, position))
            .OrderByDescending(static pair => pair.entry.Value, CountComparer.Instance)
            .ThenBy(static pair => pair.position)
            .Select(static pair => pair.entry)
            .ToList();

        return limit is { } take ? ordered.Take(Math.Max(0, take)).ToList() : ordered;
    }

    private List<PyObject> Elements()
    {
        var items = new List<PyObject>();

        foreach (var (key, value) in Entries)
        {
            if (value is not PyInt count)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"'{value.TypeName}' object cannot be interpreted as an integer"));
            }

            // CPython converts the count before it looks at the sign, so a huge negative
            // overflows rather than being skipped the way a small negative one is.
            if (count.Value > long.MaxValue || count.Value < long.MinValue)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.OverflowError, "Python int too large to convert to C ssize_t"));
            }

            for (var i = BigInteger.Zero; i < count.Value; i++)
            {
                items.Add(key);
            }
        }

        return items;
    }

    private PyObject Total()
    {
        PyObject total = Zero;

        foreach (var (_, value) in Entries)
        {
            total = Operators.Binary("+", total, value);
        }

        return total;
    }

    private PyCounter CopyCounter()
    {
        var copy = new PyCounter();

        foreach (var (key, value) in Entries)
        {
            copy.Set(key, value);
        }

        return copy;
    }

    /// <summary>Adds or subtracts the counts of a mapping, an iterable and keywords.</summary>
    private void Combine(string method, PyObject[] arguments, PyDict? keywords, bool subtract)
    {
        if (arguments.Length > 1)
        {
            // The arity counts the implicit `self`, which is how CPython words it.
            throw new PyRaise(PyErrors.TypeError(
                $"Counter.{method}() takes from 1 to 2 positional arguments "
                + $"but {arguments.Length + 1} were given"));
        }

        var op = subtract ? "-" : "+";

        if (arguments.Length == 1 && arguments[0] is not PyNone)
        {
            if (arguments[0] is PyDict source)
            {
                Merge(source, op, subtract);
            }
            else
            {
                foreach (var item in VirtualMachine.RequireIterable(arguments[0]))
                {
                    Bump(item, new PyInt(1), op);
                }
            }
        }

        if (keywords is { Count: > 0 })
        {
            Merge(keywords, op, subtract);
        }
    }

    /// <summary>
    /// Folds a mapping's counts in.
    /// </summary>
    /// <remarks>
    /// Updating an empty Counter copies rather than adds, which is what lets a count be any
    /// value at all — <c>Counter(a='x')</c> is a Counter holding a string.
    /// </remarks>
    private void Merge(PyDict source, string op, bool subtract)
    {
        var copy = !subtract && Count == 0;

        foreach (var (key, value) in source.Entries.ToList())
        {
            if (copy)
            {
                Set(key, value);
                continue;
            }

            Bump(key, value, op);
        }
    }

    private void Bump(PyObject key, PyObject amount, string op) =>
        Set(key, Operators.Binary(op, TryGetValue(key, out var current) ? current : Zero, amount));

    /// <summary>Applies a Counter's binary operator, keeping only positive results.</summary>
    /// <remarks>
    /// Which comparison runs is observable: an unorderable count reports the operator the
    /// operation actually used, so <c>|</c> complains about <c>&lt;</c> and <c>&amp;</c>
    /// about <c>&lt;</c> too, but with the operands the other way round.
    /// </remarks>
    internal static PyCounter Combine(PyCounter left, PyDict right, string op)
    {
        var result = new PyCounter();

        foreach (var (key, count) in left.Entries)
        {
            var other = right is PyCounter counter ? counter.GetItem(key)
                : right.TryGetValue(key, out var value) ? value
                : Zero;

            result.Keep(key, op switch
            {
                "+" => Operators.Binary("+", count, other),
                "-" => Operators.Binary("-", count, other),
                "|" => Operators.Compare("<", count, other).IsTruthy() ? other : count,
                _ => Operators.Compare("<", count, other).IsTruthy() ? count : other,
            });
        }

        foreach (var (key, other) in right.Entries)
        {
            if (left.Contains(key))
            {
                continue;
            }

            result.Keep(key, op switch
            {
                "+" or "|" => other,
                "-" => Operators.Binary("-", Zero, other),
                _ => Zero,
            });
        }

        return result;
    }

    /// <summary>
    /// Applies a Counter's unary operator.
    /// </summary>
    /// <remarks>
    /// Unary plus keeps the positive counts; unary minus keeps the negative ones with
    /// their signs flipped. Which comparison each runs is observable, because an
    /// unorderable count reports it.
    /// </remarks>
    internal static PyCounter Negate(PyCounter counter, bool negate)
    {
        var result = new PyCounter();

        foreach (var (key, count) in counter.Entries)
        {
            if (!Operators.Compare(negate ? "<" : ">", count, Zero).IsTruthy())
            {
                continue;
            }

            result.Set(key, negate ? Operators.Binary("-", Zero, count) : count);
        }

        return result;
    }

    /// <summary>
    /// Applies a Counter's in-place operator, mutating and returning the left one.
    /// </summary>
    /// <remarks>
    /// Unlike the binary forms these mutate, so an alias sees the result. Each walks a
    /// different side and runs a different comparison, which an unorderable count reports.
    /// </remarks>
    internal static PyCounter Update(PyCounter left, PyObject right, string op)
    {
        // `&` subscripts the operand; the rest read its `items()`, and say so when it has
        // none — which is how a list or an int is refused.
        if (op != "&" && right is not PyDict)
        {
            throw new PyRaise(PyErrors.AttributeError(right.TypeName, "items"));
        }

        switch (op)
        {
            case "+" or "-":
                foreach (var (key, count) in ((PyDict)right).Entries.ToList())
                {
                    left.Bump(key, count, op);
                }

                break;

            case "|":
                // `other_count > count`, so the right operand is on the left of the compare.
                foreach (var (key, count) in ((PyDict)right).Entries.ToList())
                {
                    if (Operators.Compare(">", count, left.GetItem(key)).IsTruthy())
                    {
                        left.Set(key, count);
                    }
                }

                break;

            default:
                // `&` walks the left's own keys and subscripts the right, so a Counter
                // reads zero for a missing key while a plain dict raises.
                foreach (var (key, count) in left.Entries.ToList())
                {
                    var other = right.GetItem(key);

                    if (Operators.Compare("<", other, count).IsTruthy())
                    {
                        left.Set(key, other);
                    }
                }

                break;
        }

        left.KeepPositive();
        return left;
    }

    /// <summary>Drops every count that is not positive.</summary>
    private void KeepPositive()
    {
        foreach (var (key, count) in Entries.ToList())
        {
            if (!Operators.Compare(">", count, Zero).IsTruthy())
            {
                Remove(key);
            }
        }
    }

    /// <summary>Compares two Counters element-wise over the union of their keys.</summary>
    internal static bool Compare(PyCounter left, PyCounter right, string op)
    {
        // The comparison runs over every key either side has; a key present on only one
        // side counts as zero on the other.
        var keys = left.Entries.Select(static e => e.Key)
            .Concat(right.Entries.Select(static e => e.Key).Where(key => !left.Contains(key)));

        var strict = op is "<" or ">";
        var relation = op is "<" or "<=" ? "<=" : ">=";
        var equal = true;

        foreach (var key in keys)
        {
            var a = left.GetItem(key);
            var b = right.GetItem(key);

            if (!Operators.Compare(relation, a, b).IsTruthy())
            {
                return false;
            }

            equal = equal && a.PyEquals(b);
        }

        return !strict || !equal;
    }

    /// <summary>Records a count, dropping it when it is not positive.</summary>
    private void Keep(PyObject key, PyObject count)
    {
        if (Operators.Compare(">", count, Zero).IsTruthy())
        {
            Set(key, count);
        }
    }

    /// <summary>Orders counts by value, leaving a count that will not compare last.</summary>
    private sealed class CountComparer : IComparer<PyObject>
    {
        public static CountComparer Instance { get; } = new();

        public int Compare(PyObject? x, PyObject? y) =>
            x is null || y is null ? 0 : x.PyCompare(y) ?? 0;
    }
}
