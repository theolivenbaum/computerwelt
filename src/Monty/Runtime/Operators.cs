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
            PyInstance instance when instance.Dunder("__neg__") is { } negate =>
                instance.Invoke(negate, []),
            PyBool flag => new PyInt(flag.Value ? -1 : 0),
            PyInt integer => new PyInt(-integer.Value),
            PyFloat number => new PyFloat(-number.Value),
            _ => throw new PyRaise(PyErrors.TypeError($"bad operand type for unary -: '{operand.TypeName}'")),
        },

        "+" => operand switch
        {
            PyInstance instance when instance.Dunder("__pos__") is { } plus =>
                instance.Invoke(plus, []),
            PyBool flag => new PyInt(flag.Value ? 1 : 0),
            PyInt or PyFloat => operand,
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
        "==" => PyBool.Of(left.PyEquals(right)),
        "!=" => PyBool.Of(!left.PyEquals(right)),
        "is" => PyBool.Of(Identical(left, right)),
        "is not" => PyBool.Of(!Identical(left, right)),
        "in" => PyBool.Of(right.Contains(left)),
        "not in" => PyBool.Of(!right.Contains(left)),
        "<" => PyBool.Of(Order(op, left, right) < 0),
        "<=" => PyBool.Of(Order(op, left, right) <= 0),
        ">" => PyBool.Of(Order(op, left, right) > 0),
        ">=" => PyBool.Of(Order(op, left, right) >= 0),
        _ => throw new PyRaise(PyErrors.TypeError($"unsupported comparison {op}")),
    };

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
            _ => false,
        };
    }

    private static int Order(string op, PyObject left, PyObject right) =>
        left.PyCompare(right)
        ?? throw new PyRaise(PyErrors.TypeError(
            $"'{op}' not supported between instances of '{left.TypeName}' and '{right.TypeName}'"));

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

        // Concatenating a string with anything else is the most common Python type error,
        // so its message is worth matching exactly.
        if (left is PyStr)
        {
            throw new PyRaise(PyErrors.TypeError($"can only concatenate str (not \"{right.TypeName}\") to str"));
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

        if (left is PySet x && right is PySet y)
        {
            return new PySet(x.Items.Where(item => !y.Contains(item)));
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

        throw new PyRaise(PyErrors.TypeError(
            $"can't multiply sequence by non-int of type '{(left is PyInt ? right : left).TypeName}'"));
    }

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
                useFloat ? "float floor division by zero" : "integer division or modulo by zero"));
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
                useFloat ? "float modulo" : "integer division or modulo by zero"));
        }

        if (useFloat)
        {
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

        if (b < 0 && a == 0)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError(
                useFloat
                    ? "0.0 cannot be raised to a negative power"
                    : "0 cannot be raised to a negative power"));
        }

        if (!useFloat)
        {
            var exponent = AsInt(right);

            // A negative integer exponent produces a float, as `2 ** -1 == 0.5`.
            if (exponent >= 0)
            {
                if (exponent > 1_000_000)
                {
                    throw new PyRaise(new PyException(PyExceptionType.OverflowError, "exponent too large"));
                }

                return new PyInt(BigInteger.Pow(AsInt(left), (int)exponent));
            }
        }

        return new PyFloat(Math.Pow(a, b));
    }

    private static PyObject BitwiseAnd(PyObject left, PyObject right)
    {
        if (AsSet(left) is { } x && AsSet(right) is { } y)
        {
            return MakeSet(left, right, x.Items.Where(y.Contains));
        }

        // Two bools give a bool: `True & True` is `True`, not `1`.
        if (left is PyBool && right is PyBool)
        {
            return PyBool.Of(left.IsTruthy() && right.IsTruthy());
        }

        return new PyInt(RequireInt(left, "&") & RequireInt(right, "&"));
    }

    /// <summary>
    /// Views a value as a set for the set operators.
    /// </summary>
    /// <remarks>
    /// <c>frozenset</c> and the dict views all take part in <c>&amp;</c>, <c>|</c>,
    /// <c>-</c> and <c>^</c>, so the operators are defined over anything set-like rather
    /// than over one class.
    /// </remarks>
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
        if (left is PySet x && right is PySet y)
        {
            return new PySet(x.Items.Concat(y.Items));
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

        return new PyInt(RequireInt(left, "|") | RequireInt(right, "|"));
    }

    private static PyObject BitwiseXor(PyObject left, PyObject right)
    {
        if (left is PySet x && right is PySet y)
        {
            return new PySet(x.Items.Where(item => !y.Contains(item))
                .Concat(y.Items.Where(item => !x.Contains(item))));
        }

        return new PyInt(RequireInt(left, "^") ^ RequireInt(right, "^"));
    }

    private static PyObject ShiftLeft(PyObject left, PyObject right)
    {
        var shift = RequireInt(right, "<<");

        if (shift < 0)
        {
            throw new PyRaise(PyErrors.ValueError("negative shift count"));
        }

        return new PyInt(RequireInt(left, "<<") << (int)shift);
    }

    private static PyObject ShiftRight(PyObject left, PyObject right)
    {
        var shift = RequireInt(right, ">>");

        if (shift < 0)
        {
            throw new PyRaise(PyErrors.ValueError("negative shift count"));
        }

        return new PyInt(RequireInt(left, ">>") >> (int)shift);
    }

    private static BigInteger RequireInt(PyObject value, string op) =>
        value is PyInt integer
            ? integer.Value
            : throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for {op}: '{value.TypeName}'"));

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
