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
    /// <summary>Applies a binary operator.</summary>
    public static PyObject Binary(string op, PyObject left, PyObject right)
    {
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
            PyBool flag => new PyInt(flag.Value ? -1 : 0),
            PyInt integer => new PyInt(-integer.Value),
            PyFloat number => new PyFloat(-number.Value),
            _ => throw new PyRaise(PyErrors.TypeError($"bad operand type for unary -: '{operand.TypeName}'")),
        },

        "+" => operand switch
        {
            PyBool flag => new PyInt(flag.Value ? 1 : 0),
            PyInt or PyFloat => operand,
            _ => throw new PyRaise(PyErrors.TypeError($"bad operand type for unary +: '{operand.TypeName}'")),
        },

        "~" => operand switch
        {
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
            return new PyFloat(result);
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
        if (left is PySet x && right is PySet y)
        {
            return new PySet(x.Items.Where(y.Contains));
        }

        return new PyInt(RequireInt(left, "&") & RequireInt(right, "&"));
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
