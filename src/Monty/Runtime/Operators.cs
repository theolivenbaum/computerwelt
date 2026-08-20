using System.Numerics;
using System.Text;

namespace Monty.Runtime;

/// <summary>
/// The arithmetic, comparison and sequence operators.
/// </summary>
/// <remarks>
/// Numeric operators promote to <c>float</c> when either operand is one, integer division
/// floors toward negative infinity (unlike C#'s truncation), and <c>%</c> takes the sign of
/// the divisor. Each of those differs from the .NET default and is the reason this cannot
/// just call through to C# operators.
/// </remarks>
public static class Operators
{
    /// <summary>The dunder each binary operator dispatches to on a user class.</summary>
    private static readonly Dictionary<string, (string Forward, string Reflected)> ArithmeticDunders =
        new(StringComparer.Ordinal)
        {
            ["+"] = ("__add__", "__radd__"),
            ["-"] = ("__sub__", "__rsub__"),
            ["*"] = ("__mul__", "__rmul__"),
            ["/"] = ("__truediv__", "__rtruediv__"),
            ["//"] = ("__floordiv__", "__rfloordiv__"),
            ["%"] = ("__mod__", "__rmod__"),
            ["**"] = ("__pow__", "__rpow__"),
            ["&"] = ("__and__", "__rand__"),
            ["|"] = ("__or__", "__ror__"),
            ["^"] = ("__xor__", "__rxor__"),
            ["<<"] = ("__lshift__", "__rlshift__"),
            [">>"] = ("__rshift__", "__rrshift__"),
            ["@"] = ("__matmul__", "__rmatmul__"),
        };

    /// <summary>Applies a binary operator.</summary>
    public static PyObject Binary(string op, PyObject left, PyObject right)
    {
        // An augmented assignment arrives with its `=` still attached: `x += y` is `+=`,
        // which mutates a mutable container instead of building a new one.
        if (op.Length <= 1 || op[^1] != '=')
        {
            return Apply(op, left, right);
        }

        var plain = op[..^1];

        if (InPlace(plain, left, right) is { } mutated)
        {
            return mutated;
        }

        try
        {
            return Apply(plain, left, right);
        }
        catch (PyRaise raise) when (raise.Exception.Message
            == $"unsupported operand type(s) for {plain}: '{left.TypeName}' and '{right.TypeName}'")
        {
            // The fallback ran the plain operator, so its complaint names the plain one;
            // the script wrote the augmented form and expects to see it.
            throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for {op}: '{left.TypeName}' and '{right.TypeName}'"));
        }
    }

    private static PyObject Apply(string op, PyObject left, PyObject right)
    {
        if (left is PyDeque deque)
        {
            if (op == "+")
            {
                return PyDeque.Concat(deque, right);
            }

            if (op == "*")
            {
                return PyDeque.Repeat(deque, right);
            }
        }

        // `2 * d` repeats just as `d * 2` does.
        if (right is PyDeque repeated && op == "*")
        {
            return PyDeque.Repeat(repeated, left);
        }

        // A Counter's operators are multiset operations over the union of the keys, and
        // keep only the positive results.
        if (left is PyCounter counter && right is PyDict counts && op is "+" or "-" or "|" or "&")
        {
            return PyCounter.Combine(counter, counts, op);
        }


        // A user class defines an operator by its dunder; the reflected form is tried when
        // the left operand does not implement the forward one.
        if ((left is PyInstance || right is PyInstance) && ArithmeticDunders.TryGetValue(op, out var dunders))
        {
            if (left is PyInstance leftInstance && leftInstance.Dunder(dunders.Forward) is { } forward)
            {
                return leftInstance.Invoke(forward, [right]);
            }

            if (right is PyInstance rightInstance && rightInstance.Dunder(dunders.Reflected) is { } reflected)
            {
                return rightInstance.Invoke(reflected, [left]);
            }
        }

        // A dict view takes any iterable on the other side, which a set does not — so its
        // set operators are their own thing rather than a case of the set ones.
        if (op is "&" or "|" or "^" or "-" && ViewOperation(op, left, right) is { } viewResult)
        {
            return viewResult;
        }

        switch (op)
        {
            case "+": return Add(left, right);
            case "-": return Subtract(left, right);
            case "*": return Multiply(left, right);
            case "/": return TrueDivide(left, right);
            case "//": return FloorDivide(left, right);
            case "%": return Modulo(left, right);
            case "**": return Power(left, right);
            case "&": return BitwiseAnd(left, right);
            case "|": return BitwiseOr(left, right);
            case "^": return BitwiseXor(left, right);
            case "<<": return ShiftLeft(left, right);
            case ">>": return ShiftRight(left, right);
            case "@": break;
        }

        throw new PyRaise(PyErrors.TypeError(
            $"unsupported operand type(s) for {op}: '{left.TypeName}' and '{right.TypeName}'"));
    }

    /// <summary>Applies a unary operator.</summary>
    public static PyObject Unary(string op, PyObject operand) => op switch
    {
        "not" => PyBool.Of(!operand.IsTruthy()),

        "-" => operand switch
        {
            PyCounter counter => PyCounter.Negate(counter, negate: true),
            PyInstance instance when instance.Dunder("__neg__") is { } negate =>
                instance.Invoke(negate, []),
            PyBool flag => new PyInt(flag.Value ? -1 : 0),
            PyInt integer => new PyInt(-integer.Value),
            PyFloat number => new PyFloat(-number.Value),
            Modules.DatetimeModule.PyTimeDelta span => new Modules.DatetimeModule.PyTimeDelta(-span.Value),
            _ => throw new PyRaise(PyErrors.TypeError($"bad operand type for unary -: '{operand.TypeName}'")),
        },

        "+" => operand switch
        {
            PyCounter counter => PyCounter.Negate(counter, negate: false),
            PyInstance instance when instance.Dunder("__pos__") is { } plus =>
                instance.Invoke(plus, []),
            PyBool flag => new PyInt(flag.Value ? 1 : 0),
            PyInt or PyFloat or Modules.DatetimeModule.PyTimeDelta => operand,
            _ => throw new PyRaise(PyErrors.TypeError($"bad operand type for unary +: '{operand.TypeName}'")),
        },

        "~" => operand switch
        {
            PyInstance instance when instance.Dunder("__invert__") is { } invert =>
                instance.Invoke(invert, []),
            PyInt integer => new PyInt(-integer.Value - 1),
            _ => throw new PyRaise(PyErrors.TypeError($"bad operand type for unary ~: '{operand.TypeName}'")),
        },

        _ => throw new PyRaise(PyErrors.TypeError($"unsupported unary operator {op}")),
    };

    /// <summary>Applies a comparison.</summary>
    public static PyObject Compare(string op, PyObject left, PyObject right) => op switch
    {
        "==" => Equality(left, right, negate: false),
        "!=" => Equality(left, right, negate: true),
        "is" => PyBool.Of(Identical(left, right)),
        "is not" => PyBool.Of(!Identical(left, right)),
        "in" => PyBool.Of(right.Contains(left)),
        "not in" => PyBool.Of(!right.Contains(left)),
        // A Counter compares as a multiset, element-wise over the union of the keys,
        // rather than as the mapping it otherwise is — and only against another Counter.
        "<" or "<=" or ">" or ">=" when left is PyCounter counter && right is PyCounter counts =>
            PyBool.Of(PyCounter.Compare(counter, counts, op)),

        "<" or "<=" or ">" or ">=" => Ordering(op, left, right),
        _ => throw new PyRaise(PyErrors.TypeError($"unsupported comparison {op}")),
    };

    /// <summary>
    /// Applies <c>==</c> or <c>!=</c>.
    /// </summary>
    /// <remarks>
    /// A user <c>__eq__</c> may return anything, and the operator hands that object back
    /// unchanged — only <c>NotImplemented</c> is special, meaning "ask the other side", and
    /// falling back to identity when neither side answers. Truth-testing the result is the
    /// caller's business, which is why containers use <see cref="PyObject.SameOrEqual"/>
    /// instead of this.
    /// </remarks>
    private static PyObject Equality(PyObject left, PyObject right, bool negate)
    {
        if (RichEquals(left, right) is { } result)
        {
            return negate ? PyBool.Of(!result.IsTruthy()) : result;
        }

        var identical = ReferenceEquals(left, right);
        return PyBool.Of(negate ? !identical : identical);
    }

    /// <summary>Runs <c>__eq__</c> on either side, or null when neither answered.</summary>
    internal static PyObject? RichEquals(PyObject left, PyObject right)
    {
        if (left is PyInstance instance && instance.Dunder("__eq__") is { } equals)
        {
            var answer = instance.Invoke(equals, [right]);

            if (answer is not Builtins.NotImplementedSingleton)
            {
                return answer;
            }
        }

        if (right is PyInstance reflected && reflected.Dunder("__eq__") is { } reflectedEquals)
        {
            var answer = reflected.Invoke(reflectedEquals, [left]);

            if (answer is not Builtins.NotImplementedSingleton)
            {
                return answer;
            }
        }

        return left is PyInstance || right is PyInstance ? null : PyBool.Of(left.PyEquals(right));
    }

    /// <summary>
    /// Identity, as <c>is</c> defines it. Small integers, booleans and <c>None</c> are
    /// interned in CPython, so <c>x is 1</c> holds for equal small ints; the port matches
    /// that for the values fixtures actually test.
    /// </summary>
    private static bool Identical(PyObject left, PyObject right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return (left, right) switch
        {
            (PyNone, PyNone) => true,
            (PyBool a, PyBool b) => a.Value == b.Value,
            (PyBool, _) or (_, PyBool) => false,
            (PyInt a, PyInt b) => a.Value == b.Value && BigInteger.Abs(a.Value) < 257,
            (PyStr a, PyStr b) => string.Equals(a.Value, b.Value, StringComparison.Ordinal),

            // The empty tuple is a singleton in CPython, so `() is ()` holds. A named
            // tuple with no fields is still a distinct object, hence the exclusion.
            (PyTuple { Items.Count: 0 } a, PyTuple { Items.Count: 0 } b) =>
                a is not PyNamedTuple && b is not PyNamedTuple,
            _ => false,
        };
    }

    /// <summary>
    /// Applies an ordering comparison.
    /// </summary>
    /// <remarks>
    /// A pair with no ordering is usually a type error, but a NaN is not: IEEE says every
    /// comparison against it is false, including inside a sequence, where the first
    /// differing element decides.
    /// </remarks>
    private static PyObject Ordering(string op, PyObject left, PyObject right)
    {
        int? order;
        var (a, b) = (left, right);

        try
        {
            order = left.PyCompare(right);
        }
        catch (PyList.UnorderedPair pair)
        {
            (order, a, b) = (null, pair.Left, pair.Right);
        }

        if (order is not { } decided)
        {
            // A NaN answers False to every comparison against another number; against a
            // non-number it is still a type mismatch, and says so.
            return (IsNaN(a) && b is PyInt or PyFloat) || (IsNaN(b) && a is PyInt or PyFloat)
                ? PyBool.False
                : throw new PyRaise(PyErrors.TypeError(
                    $"'{op}' not supported between instances of '{a.TypeName}' and '{b.TypeName}'"));
        }

        return PyBool.Of(op switch
        {
            "<" => decided < 0,
            "<=" => decided <= 0,
            ">" => decided > 0,
            _ => decided >= 0,
        });
    }

    private static bool IsNaN(PyObject value) => value is PyFloat number && double.IsNaN(number.Value);

    private static PyObject Add(PyObject left, PyObject right)
    {
        if (TryNumbers(left, right, out var a, out var b, out var useFloat))
        {
            return useFloat ? new PyFloat(a + b) : new PyInt(AsInt(left) + AsInt(right));
        }

        switch (left, right)
        {
            case (PyStr x, PyStr y):
                return new PyStr(x.Value + y.Value);

            case (PyList x, PyList y):
                return new PyList([.. x.Items, .. y.Items]);

            case (PyTuple x, PyTuple y):
                return new PyTuple([.. x.Items, .. y.Items]);

            case (PyBytes x, PyBytes y):
                return new PyBytes([.. x.Value, .. y.Value]);
        }

        // Concatenating a sequence with something that is not one is the most common
        // Python type error, so its message is worth matching exactly.
        if (left is PyStr or PyList or PyTuple)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"can only concatenate {left.TypeName} (not \"{right.TypeName}\") to {left.TypeName}"));
        }

        throw new PyRaise(PyErrors.TypeError(
            $"unsupported operand type(s) for +: '{left.TypeName}' and '{right.TypeName}'"));
    }

    private static PyObject Subtract(PyObject left, PyObject right)
    {
        if (TryNumbers(left, right, out var a, out var b, out var useFloat))
        {
            return useFloat ? new PyFloat(a - b) : new PyInt(AsInt(left) - AsInt(right));
        }

        if (AsSet(left) is { } x && AsSet(right) is { } y)
        {
            return MakeSet(left, right, x.Items.Where(item => !y.Contains(item)));
        }

        throw new PyRaise(PyErrors.TypeError(
            $"unsupported operand type(s) for -: '{left.TypeName}' and '{right.TypeName}'"));
    }

    private static PyObject Multiply(PyObject left, PyObject right)
    {
        if (TryNumbers(left, right, out var a, out var b, out var useFloat))
        {
            return useFloat ? new PyFloat(a * b) : new PyInt(AsInt(left) * AsInt(right));
        }

        // Sequence repetition: `'ab' * 3`, `[0] * 4`. A negative count yields empty.
        var (sequence, count) = left is PyInt
            ? (right, BigInteger.Max(0, AsInt(left)))
            : (left, right is PyInt ? BigInteger.Max(0, AsInt(right)) : BigInteger.MinusOne);

        if (count >= 0)
        {
            // An empty sequence repeats to an empty one however large the count is: nothing
            // is materialised, so the count never has to fit an index.
            var length = sequence switch
            {
                PyStr text => text.Value.Length,
                PyList list => list.Items.Count,
                PyTuple tuple => tuple.Items.Count,
                PyBytes bytes => bytes.Value.Length,
                _ => -1,
            };

            if (length == 0)
            {
                count = 0;
            }

            if (count > 100_000_000)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.OverflowError, "cannot fit 'int' into an index-sized integer"));
            }

            var repeats = (int)BigInteger.Max(0, count);

            switch (sequence)
            {
                case PyStr text:
                    return new PyStr(string.Concat(Enumerable.Repeat(text.Value, repeats)));

                case PyList list:
                {
                    var items = new List<PyObject>(list.Items.Count * repeats);
                    for (var i = 0; i < repeats; i++)
                    {
                        items.AddRange(list.Items);
                    }

                    return new PyList(items);
                }

                case PyTuple tuple:
                {
                    var items = new List<PyObject>(tuple.Items.Count * repeats);
                    for (var i = 0; i < repeats; i++)
                    {
                        items.AddRange(tuple.Items);
                    }

                    return new PyTuple(items);
                }

                case PyBytes bytes:
                {
                    var result = new byte[bytes.Value.Length * repeats];
                    for (var i = 0; i < repeats; i++)
                    {
                        bytes.Value.CopyTo(result, i * bytes.Value.Length);
                    }

                    return new PyBytes(result);
                }
            }
        }

        // A repeatable sequence with a count that is not an int names the count's type;
        // a pair that is not a sequence-and-count at all names both operands.
        if (sequence is PyStr or PyList or PyTuple or PyBytes)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"can't multiply sequence by non-int of type '{(left is PyInt ? right : left).TypeName}'"));
        }

        throw new PyRaise(PyErrors.TypeError(
            $"unsupported operand type(s) for *: '{left.TypeName}' and '{right.TypeName}'"));
    }

    /// <summary>Widens an integer to a float, refusing one that has no float to widen to.</summary>
    private static double ToDouble(BigInteger value) =>
        BigInteger.Abs(value) <= new BigInteger(double.MaxValue)
            ? (double)value
            : throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "int too large to convert to float"));

    private static PyObject TrueDivide(PyObject left, PyObject right)
    {
        // `Path('/usr') / 'local'` is the idiomatic way to build a path, and `str / Path`
        // works too because Path defines the reflected operator.
        if (left is Modules.PyPath || right is Modules.PyPath)
        {
            return JoinPaths(left, right);
        }

        if (!TryNumbers(left, right, out var a, out var b, out _))
        {
            throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for /: '{left.TypeName}' and '{right.TypeName}'"));
        }

        if (b == 0)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError("division by zero"));
        }

        return new PyFloat(a / b);
    }

    private static PyObject JoinPaths(PyObject left, PyObject right)
    {
        var names = $"'{left.TypeName}' and '{right.TypeName}'";
        var head = Segment(left, names);
        var tail = Segment(right, names);

        var fileSystem = (left as Modules.PyPath)?.FileSystem ?? (right as Modules.PyPath)?.FileSystem;
        return new Modules.PyPath(Modules.PyPath.Join(head, tail), fileSystem);
    }

    private static string Segment(PyObject value, string names) => value switch
    {
        Modules.PyPath path => path.Value,
        PyStr text => text.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"unsupported operand type(s) for /: {names}")),
    };

    private static PyObject FloorDivide(PyObject left, PyObject right)
    {
        if (!TryNumbers(left, right, out var a, out var b, out var useFloat))
        {
            throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for //: '{left.TypeName}' and '{right.TypeName}'"));
        }

        if (b == 0)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError(
                "division by zero"));
        }

        if (useFloat)
        {
            return new PyFloat(Math.Floor(a / b));
        }

        // Python floors toward negative infinity; C# truncates toward zero.
        var x = AsInt(left);
        var y = AsInt(right);
        var quotient = BigInteger.Divide(x, y);

        if ((x % y) != 0 && ((x < 0) != (y < 0)))
        {
            quotient -= 1;
        }

        return new PyInt(quotient);
    }

    private static PyObject Modulo(PyObject left, PyObject right)
    {
        // `'%s' % value` is string formatting, not arithmetic.
        if (left is PyStr format)
        {
            return new PyStr(StringFormatter.PercentFormat(format.Value, right));
        }

        if (!TryNumbers(left, right, out var a, out var b, out var useFloat))
        {
            throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for %: '{left.TypeName}' and '{right.TypeName}'"));
        }

        if (b == 0)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError(
                "division by zero"));
        }

        if (useFloat)
        {
            // An infinite divisor leaves the dividend as the remainder when the two agree
            // in sign, and the divisor itself when they do not.
            if (double.IsInfinity(b) && double.IsFinite(a))
            {
                return new PyFloat(a == 0 || double.IsNegative(a) == double.IsNegative(b) ? a : b);
            }

            var result = a - (Math.Floor(a / b) * b);

            // A zero result takes the divisor's sign, so `6.0 % -3.0` is `-0.0`.
            return new PyFloat(result == 0 ? Math.CopySign(0, b) : result);
        }

        // The result takes the sign of the divisor, as Python specifies.
        var x = AsInt(left);
        var y = AsInt(right);
        var remainder = BigInteger.Remainder(x, y);

        if (!remainder.IsZero && ((remainder < 0) != (y < 0)))
        {
            remainder += y;
        }

        return new PyInt(remainder);
    }

    private static PyObject Power(PyObject left, PyObject right)
    {
        if (!TryNumbers(left, right, out var a, out var b, out var useFloat))
        {
            throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for ** or pow(): '{left.TypeName}' and '{right.TypeName}'"));
        }

        if (!useFloat)
        {
            var exponent = AsInt(right);

            if (exponent >= 0)
            {
                var baseValue = AsInt(left);

                // 0, 1 and -1 have an answer at every exponent, however large, so they meet
                // no size guard: `(-1) ** 2**63` is 1, not a refusal.
                if (BigInteger.Abs(baseValue) <= BigInteger.One)
                {
                    return new PyInt(baseValue.Sign switch
                    {
                        0 => exponent.IsZero ? BigInteger.One : BigInteger.Zero,
                        1 => BigInteger.One,
                        _ => exponent.IsEven ? BigInteger.One : BigInteger.MinusOne,
                    });
                }

                // The result is exact, so the only limit is what can be built: an exponent
                // in the millions would produce a number no sandbox should try to hold.
                if (exponent > 1_000_000)
                {
                    throw new PyRaise(new PyException(PyExceptionType.OverflowError, "exponent too large"));
                }

                return new PyInt(BigInteger.Pow(AsInt(left), (int)exponent));
            }

            // A negative integer exponent produces a float, as `2 ** -1 == 0.5` — so both
            // operands have to be representable as one before anything else is decided.
            a = ToDouble(AsInt(left));
            b = ToDouble(exponent);
        }

        if (b < 0 && a == 0)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError("zero to a negative power"));
        }

        return new PyFloat(Math.Pow(a, b));
    }

    private static PyObject BitwiseAnd(PyObject left, PyObject right)
    {
        if (AsSet(left) is { } x && AsSet(right) is { } y)
        {
            return MakeSet(left, right, x.Items.Where(y.Contains));
        }

        // A set on one side and a non-set on the other is a set operation with a bad
        // operand, not a bitwise one.
        if (AsSet(left) is not null || AsSet(right) is not null)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for &: '{left.TypeName}' and '{right.TypeName}'"));
        }

        // Two bools give a bool: `True & True` is `True`, not `1`.
        if (left is PyBool && right is PyBool)
        {
            return PyBool.Of(left.IsTruthy() && right.IsTruthy());
        }

        return new PyInt(RequireInt(left, "&", left, right) & RequireInt(right, "&", left, right));
    }

    /// <summary>
    /// Applies an augmented assignment in place, or returns null when the type has no
    /// in-place form and the ordinary operator should run instead.
    /// </summary>
    /// <remarks>
    /// The identity matters: <c>a = b; a += [1]</c> must leave <c>b</c> holding the longer
    /// list, which rebinding a fresh one would not.
    /// </remarks>
    private static PyObject? InPlace(string op, PyObject left, PyObject right)
    {
        if (left is PyInstance instance
            && InPlaceDunders.TryGetValue(op, out var dunder)
            && instance.Dunder(dunder) is { } method)
        {
            return instance.Invoke(method, [right]);
        }

        // `+=` on a deque is extend, so any iterable works; `*=` repeats in place.
        if (left is PyDeque target)
        {
            if (op == "+")
            {
                return PyDeque.Extend(target, right);
            }

            if (op == "*")
            {
                return PyDeque.RepeatInPlace(target, right);
            }
        }

        // A Counter's in-place operators mutate it, unlike the binary forms, and accept
        // any mapping — or, for `&`, anything subscriptable.
        if (left is PyCounter counter && op is "+" or "-" or "|" or "&")
        {
            return PyCounter.Update(counter, right, op);
        }

        switch (op, left)
        {
            case ("+", PyList list):
            {
                // `lst += lst` must append the items the list had, not the ones it grows;
                // the source is read out before the target is touched.
                var extra = VirtualMachine.RequireIterable(right).ToList();
                list.Items.AddRange(extra);
                return list;
            }

            case ("*", PyList list) when right is PyInt count:
            {
                var original = list.Items.ToList();
                list.Items.Clear();

                for (var i = 0; i < count.Value; i++)
                {
                    list.Items.AddRange(original);
                }

                return list;
            }

            case ("|" or "&" or "-" or "^", PySet { IsFrozen: false } set) when AsSet(right) is { } other:
            {
                var items = op switch
                {
                    "|" => set.Items.Concat(other.Items),
                    "&" => set.Items.Where(other.Contains),
                    "-" => set.Items.Where(item => !other.Contains(item)),
                    _ => set.Items.Where(item => !other.Contains(item))
                        .Concat(other.Items.Where(item => !set.Contains(item))),
                };

                var replacement = items.ToList();
                set.Clear();

                foreach (var item in replacement)
                {
                    set.Add(item);
                }

                return set;
            }

            case ("|", PyDict dict) when right is PyDict source:
                foreach (var (key, value) in source.Entries)
                {
                    dict.Set(key, value);
                }

                return dict;

            default:
                return null;
        }
    }

    private static readonly Dictionary<string, string> InPlaceDunders = new(StringComparer.Ordinal)
    {
        ["+"] = "__iadd__", ["-"] = "__isub__", ["*"] = "__imul__", ["/"] = "__itruediv__",
        ["//"] = "__ifloordiv__", ["%"] = "__imod__", ["**"] = "__ipow__", ["|"] = "__ior__",
        ["&"] = "__iand__", ["^"] = "__ixor__", ["<<"] = "__ilshift__", [">>"] = "__irshift__",
        ["@"] = "__imatmul__",
    };

    /// <summary>
    /// Views a value as a set for the set operators.
    /// </summary>
    /// <remarks>
    /// <c>frozenset</c> and the dict views all take part in <c>&amp;</c>, <c>|</c>,
    /// <c>-</c> and <c>^</c>, so the operators are defined over anything set-like rather
    /// than over one class.
    /// </remarks>
    /// <summary>
    /// Applies a set operator with a dict view on one side, or returns null when neither
    /// side is a set-like view and the ordinary handling should run.
    /// </summary>
    /// <remarks>
    /// Views differ from sets in what they accept and in when they hash. Intersection walks
    /// the other operand and probes the view, so a view over unhashable values still
    /// intersects; the other three build a set from each side, left first, which is why a
    /// non-iterable left operand is reported before the view's values are ever hashed.
    /// </remarks>
    private static PyObject? ViewOperation(string op, PyObject left, PyObject right)
    {
        if ((left as PyView)?.IsSetLike != true && (right as PyView)?.IsSetLike != true)
        {
            return null;
        }

        if (op == "&")
        {
            var (view, other) = left is PyView { IsSetLike: true } candidate
                ? (candidate, right)
                : ((PyView)right, left);

            // The other side's items are hashed as they are read — an unhashable one is an
            // error even against an empty view — while the view's own values never are.
            var probes = new PySet(VirtualMachine.RequireIterable(other));
            return new PySet(probes.Items.Where(view.Contains));
        }

        var x = new PySet(VirtualMachine.RequireIterable(left));
        var y = new PySet(VirtualMachine.RequireIterable(right));

        return op switch
        {
            "|" => new PySet(x.Items.Concat(y.Items)),
            "^" => new PySet(x.Items.Where(item => !y.Contains(item))
                .Concat(y.Items.Where(item => !x.Contains(item)))),
            _ => new PySet(x.Items.Where(item => !y.Contains(item))),
        };
    }

    private static PySet? AsSet(PyObject value) => value switch
    {
        PySet set => set,
        PyView view when view.IsSetLike => new PySet(view.Iterate() ?? []),
        _ => null,
    };

    /// <summary>
    /// Builds the result of a set operation, which is a frozenset only when the left
    /// operand was one.
    /// </summary>
    private static PyObject MakeSet(PyObject left, PyObject right, IEnumerable<PyObject> items)
    {
        _ = right;
        return new PySet(items) { IsFrozen = left is PySet { IsFrozen: true } };
    }

    private static PyObject BitwiseOr(PyObject left, PyObject right)
    {
        if (AsSet(left) is { } x && AsSet(right) is { } y)
        {
            return MakeSet(left, right, x.Items.Concat(y.Items));
        }

        // A set on one side and a non-set on the other is a set operation with a bad
        // operand, not a bitwise one.
        if (AsSet(left) is not null || AsSet(right) is not null)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for |: '{left.TypeName}' and '{right.TypeName}'"));
        }

        if (left is PyDict a && right is PyDict b)
        {
            var merged = a.Copy();
            foreach (var (key, value) in b.Entries)
            {
                merged.Set(key, value);
            }

            return merged;
        }

        // Two bools give a bool: `True | False` is `True`, not `1`.
        if (left is PyBool && right is PyBool)
        {
            return PyBool.Of(left.IsTruthy() || right.IsTruthy());
        }

        return new PyInt(RequireInt(left, "|", left, right) | RequireInt(right, "|", left, right));
    }

    private static PyObject BitwiseXor(PyObject left, PyObject right)
    {
        if (AsSet(left) is { } x && AsSet(right) is { } y)
        {
            return MakeSet(left, right, x.Items.Where(item => !y.Contains(item))
                .Concat(y.Items.Where(item => !x.Contains(item))));
        }

        // A set on one side and a non-set on the other is a set operation with a bad
        // operand, not a bitwise one.
        if (AsSet(left) is not null || AsSet(right) is not null)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for ^: '{left.TypeName}' and '{right.TypeName}'"));
        }

        if (left is PyBool && right is PyBool)
        {
            return PyBool.Of(left.IsTruthy() ^ right.IsTruthy());
        }

        return new PyInt(RequireInt(left, "^", left, right) ^ RequireInt(right, "^", left, right));
    }

    private static PyObject ShiftLeft(PyObject left, PyObject right)
    {
        var shift = RequireInt(right, "<<", left, right);

        if (shift < 0)
        {
            throw new PyRaise(PyErrors.ValueError("negative shift count"));
        }

        var value = RequireInt(left, "<<", left, right);

        // Nothing shifted stays nothing however far it moves, so a count too large to apply
        // is only a problem when there is something to move.
        if (value.IsZero)
        {
            return new PyInt(BigInteger.Zero);
        }

        return shift > 1_000_000
            ? throw new PyRaise(new PyException(PyExceptionType.OverflowError, "shift count too large"))
            : new PyInt(value << (int)shift);
    }

    private static PyObject ShiftRight(PyObject left, PyObject right)
    {
        var shift = RequireInt(right, ">>", left, right);

        if (shift < 0)
        {
            throw new PyRaise(PyErrors.ValueError("negative shift count"));
        }

        var value = RequireInt(left, ">>", left, right);

        // Shifting past the last bit leaves the sign behind: 0 for a positive value, -1 for
        // a negative one, whatever the count.
        return shift > 1_000_000
            ? new PyInt(value.Sign < 0 ? BigInteger.MinusOne : BigInteger.Zero)
            : new PyInt(value >> (int)shift);
    }

    /// <summary>
    /// Reads a bitwise operand, which must be an integer.
    /// </summary>
    /// <remarks>
    /// The complaint names both operands even though only one of them is at fault — that is
    /// how CPython words every binary-operator mismatch, and scripts match on it.
    /// </remarks>
    private static BigInteger RequireInt(PyObject value, string op, PyObject left, PyObject right) =>
        value is PyInt integer
            ? integer.Value
            : throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for {op}: '{left.TypeName}' and '{right.TypeName}'"));

    private static BigInteger AsInt(PyObject value) => ((PyInt)value).Value;

    /// <summary>
    /// Extracts both operands as doubles when both are numeric, reporting whether the
    /// result should be a float.
    /// </summary>
    private static bool TryNumbers(PyObject left, PyObject right, out double a, out double b, out bool useFloat)
    {
        a = 0;
        b = 0;
        useFloat = false;

        switch (left)
        {
            case PyInt integer: a = (double)integer.Value; break;
            case PyFloat number: a = number.Value; useFloat = true; break;
            default: return false;
        }

        switch (right)
        {
            case PyInt integer: b = (double)integer.Value; break;
            case PyFloat number: b = number.Value; useFloat = true; break;
            default: return false;
        }

        return true;
    }
}
