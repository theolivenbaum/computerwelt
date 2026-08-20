using System.Globalization;
using System.Numerics;

namespace Monty.Runtime;

/// <summary>
/// Base of every value the interpreter manipulates.
/// </summary>
/// <remarks>
/// <para>
/// Python semantics are defined by protocols — truthiness, equality, hashing, iteration,
/// arithmetic — so those are virtual methods here rather than dispatched through a
/// dictionary of dunder names. A user-defined class overrides the same methods by looking
/// its dunders up, so both routes end in one place.
/// </para>
/// <para>
/// Integers are <see cref="BigInteger"/> because Python's are unbounded, and a fixture
/// that computes a factorial is not exotic.
/// </para>
/// </remarks>
public abstract class PyObject
{
    /// <summary>The type name reported by <c>type(x).__name__</c> and in error messages.</summary>
    public abstract string TypeName { get; }

    /// <summary>The value's truthiness, as <c>bool(x)</c> and <c>if x</c> see it.</summary>
    public virtual bool IsTruthy() => true;

    /// <summary>The result of <c>str(x)</c>.</summary>
    public virtual string Display() => Repr();

    /// <summary>The result of <c>repr(x)</c>.</summary>
    public abstract string Repr();

    /// <summary>Value equality, as <c>==</c> defines it.</summary>
    public virtual bool PyEquals(PyObject other) => ReferenceEquals(this, other);

    /// <summary>
    /// The hash used by <c>dict</c> and <c>set</c>. Unhashable types throw, because
    /// silently hashing a list would break the dict invariant rather than report the error
    /// Python promises.
    /// </summary>
    public virtual BigInteger PyHash() =>
        throw new PyRaise(PyErrors.TypeError($"unhashable type: '{TypeName}'"));

    /// <summary>Ordering, for <c>&lt;</c> and friends. Returns null when the types do not compare.</summary>
    public virtual int? PyCompare(PyObject other) => null;

    /// <summary>
    /// Formats the value for a format spec, or null to fall back to the format
    /// mini-language. This is the <c>__format__</c> hook: a type whose specs are its own
    /// language — <c>datetime</c>'s strftime patterns — implements it.
    /// </summary>
    public virtual string? PyFormat(string spec) => null;

    /// <summary>Attribute lookup.</summary>
    public virtual PyObject? GetAttribute(string name) => null;

    /// <summary>Attribute assignment. Returns false when the type does not permit it.</summary>
    public virtual bool SetAttribute(string name, PyObject value) => false;

    /// <summary>Element count, for <c>len()</c>. Returns null when the type has no length.</summary>
    public virtual int? Length() => null;

    /// <summary>Iteration, for <c>for</c> and the iterable protocol. Returns null when not iterable.</summary>
    public virtual IEnumerable<PyObject>? Iterate() => null;

    /// <summary>Subscript read.</summary>
    public virtual PyObject GetItem(PyObject index) =>
        throw new PyRaise(PyErrors.TypeError($"'{TypeName}' object is not subscriptable"));

    /// <summary>Subscript write.</summary>
    public virtual void SetItem(PyObject index, PyObject value) =>
        throw new PyRaise(PyErrors.TypeError($"'{TypeName}' object does not support item assignment"));

    /// <summary>Subscript delete.</summary>
    public virtual void DeleteItem(PyObject index) =>
        throw new PyRaise(PyErrors.TypeError($"'{TypeName}' object doesn't support item deletion"));

    /// <summary>
    /// Whether two values count as equal for a container operation.
    /// </summary>
    /// <remarks>
    /// Membership, <c>count</c>, <c>index</c> and sequence comparison treat an object as
    /// equal to itself without asking <c>__eq__</c>, which is what keeps a value with a
    /// deliberately odd equality — a NaN, or a class that never compares equal — findable
    /// in the list it was put into.
    /// </remarks>
    public static bool SameOrEqual(PyObject left, PyObject right) =>
        ReferenceEquals(left, right)
        || (Operators.RichEquals(left, right) is { } result ? result.IsTruthy() : left.PyEquals(right));

    /// <summary>Membership, for <c>in</c>.</summary>
    public virtual bool Contains(PyObject item)
    {
        var items = Iterate() ?? throw new PyRaise(
            PyErrors.TypeError($"argument of type '{TypeName}' is not iterable"));

        foreach (var candidate in items)
        {
            if (SameOrEqual(candidate, item))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Repr();
}

/// <summary><c>None</c>.</summary>
public sealed class PyNone : PyObject
{
    private PyNone()
    {
    }

    /// <summary>The one instance.</summary>
    public static PyNone Instance { get; } = new();

    /// <inheritdoc />
    public override string TypeName => "NoneType";

    /// <inheritdoc />
    public override bool IsTruthy() => false;

    /// <inheritdoc />
    public override string Repr() => "None";

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other is PyNone;

    /// <inheritdoc />
    public override BigInteger PyHash() => 0;
}

/// <summary><c>...</c>.</summary>
public sealed class PyEllipsis : PyObject
{
    private PyEllipsis()
    {
    }

    /// <summary>The one instance.</summary>
    public static PyEllipsis Instance { get; } = new();

    /// <inheritdoc />
    public override string TypeName => "ellipsis";

    /// <inheritdoc />
    public override string Repr() => "Ellipsis";

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other is PyEllipsis;

    /// <inheritdoc />
    public override BigInteger PyHash() => 269;
}

/// <summary><c>bool</c>. A subtype of <c>int</c> in Python, and modelled as one here.</summary>
public sealed class PyBool : PyInt
{
    private PyBool(bool value) : base(value ? BigInteger.One : BigInteger.Zero) => Value = value;

    /// <summary><c>True</c>.</summary>
    public static PyBool True { get; } = new(true);

    /// <summary><c>False</c>.</summary>
    public static PyBool False { get; } = new(false);

    /// <summary>Returns the singleton for <paramref name="value"/>.</summary>
    public static PyBool Of(bool value) => value ? True : False;

    /// <summary>The underlying boolean.</summary>
    public new bool Value { get; }

    /// <inheritdoc />
    public override string TypeName => "bool";

    /// <inheritdoc />
    public override string Repr() => Value ? "True" : "False";
}

/// <summary><c>int</c>, with Python's unbounded precision.</summary>
public class PyInt : PyObject
{
    /// <summary>Creates an integer.</summary>
    public PyInt(BigInteger value) => Value = value;

    /// <summary>Creates an integer from a 64-bit value.</summary>
    public PyInt(long value) => Value = value;

    /// <summary>The integer value.</summary>
    public BigInteger Value { get; }

    /// <inheritdoc />
    public override string TypeName => "int";

    /// <inheritdoc />
    public override bool IsTruthy() => !Value.IsZero;

    /// <summary>
    /// How many decimal digits an integer may be converted to or from.
    /// </summary>
    /// <remarks>
    /// Decimal conversion of a huge integer is quadratic, so CPython caps it — and so does
    /// this, at the same 4300 digits, because a script that can ask for a billion-digit
    /// conversion can burn arbitrary CPU inside the sandbox. Binary, octal and hexadecimal
    /// are linear and stay unbounded.
    /// </remarks>
    public const int MaxStringDigits = 4300;

    /// <inheritdoc />
    public override string Repr() => Decimal(Value);

    /// <summary>Renders an integer in decimal, refusing one past the digit limit.</summary>
    public static string Decimal(BigInteger value)
    {
        // The bound is checked before formatting, so a huge value costs nothing to reject.
        if (BigInteger.Abs(value) >= BigInteger.Pow(10, MaxStringDigits))
        {
            throw new PyRaise(PyErrors.ValueError(
                $"Exceeds the limit ({MaxStringDigits} digits) for integer string conversion: "
                + "use sys.set_int_max_str_digits() to increase the limit"));
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other switch
    {
        PyInt integer => Value == integer.Value,
        PyFloat number => Mixed(Value, number.Value) == 0,
        _ => false,
    };

    /// <inheritdoc />
    public override BigInteger PyHash() => Numbers.Hash(Value);

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) => other switch
    {
        PyInt integer => Value.CompareTo(integer.Value),
        PyFloat number => Mixed(Value, number.Value),
        _ => null,
    };

    /// <summary>
    /// Compares an integer against a float exactly, without rounding either to the other.
    /// </summary>
    /// <remarks>
    /// Widening the integer to a double would make `10**30 == 1e30` — the two round to the
    /// same double although they are different numbers. Splitting the float into its whole
    /// and fractional parts keeps the comparison exact at every magnitude.
    /// </remarks>
    /// <param name="left">The integer.</param>
    /// <param name="right">The float.</param>
    /// <returns>The sign of left minus right, or null when the float is a NaN.</returns>
    public static int? Mixed(BigInteger left, double right)
    {
        if (double.IsNaN(right))
        {
            return null;
        }

        if (double.IsInfinity(right))
        {
            return right > 0 ? -1 : 1;
        }

        // The floor of a finite double is itself a whole number, so this conversion is
        // exact however large the value is.
        var floor = Math.Floor(right);
        var order = left.CompareTo(new BigInteger(floor));

        // Equal whole parts leave the fraction to decide: any fraction at all puts the
        // float above the integer.
        return order != 0 ? order : right > floor ? -1 : 0;
    }

    /// <summary>Converts to a 32-bit index, raising when it does not fit.</summary>
    public int ToIndex()
    {
        if (Value < int.MinValue || Value > int.MaxValue)
        {
            throw new PyRaise(PyErrors.IndexError("cannot fit 'int' into an index-sized integer"));
        }

        return (int)Value;
    }
}

/// <summary><c>float</c>.</summary>
public sealed class PyFloat : PyObject
{
    /// <summary>Creates a float.</summary>
    public PyFloat(double value) => Value = value;

    /// <summary>The value.</summary>
    public double Value { get; }

    /// <inheritdoc />
    public override string TypeName => "float";

    /// <inheritdoc />
    public override bool IsTruthy() => Value != 0;

    /// <inheritdoc />
    public override string Repr() => Format(Value);

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other switch
    {
        // `==` rather than `Equals`: NaN is unequal to itself, and -0.0 equals 0.0.
        PyFloat number => Value == number.Value,
        PyInt integer => PyInt.Mixed(integer.Value, Value) == 0,
        _ => false,
    };

    /// <inheritdoc />
    public override BigInteger PyHash() => Numbers.Hash(Value);

    /// <inheritdoc />
    public override int? PyCompare(PyObject other)
    {
        // NaN orders against nothing, so every comparison with it is false.
        if (double.IsNaN(Value))
        {
            return null;
        }

        return other switch
        {
            PyFloat number => double.IsNaN(number.Value) ? null : Value.CompareTo(number.Value),
            PyInt integer => -PyInt.Mixed(integer.Value, Value),
            _ => null,
        };
    }

    /// <summary>
    /// Formats a float the way Python's <c>repr</c> does: the shortest text that round
    /// trips, always with a decimal point or exponent so it cannot be mistaken for an int.
    /// </summary>
    /// <remarks>
    /// .NET and Python disagree on when to switch to exponent notation — .NET writes
    /// <c>10000000000000000</c> where Python writes <c>1e+16</c> — so the shortest digits
    /// are taken from .NET and then laid out by Python's rule: exponent notation when the
    /// leading digit's decimal exponent is below -4 or at least 16.
    /// </remarks>
    internal static string Format(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);

        // .NET prints negative zero as "-0", and Python keeps the sign too.
        var sign = text.StartsWith('-') ? "-" : string.Empty;

        if (sign.Length > 0)
        {
            text = text[1..];
        }

        var exponent = 0;
        var e = text.IndexOf('E', StringComparison.Ordinal);

        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var point = text.IndexOf('.', StringComparison.Ordinal);
        var digits = point < 0 ? text : text.Remove(point, 1);
        var integerLength = point < 0 ? text.Length : point;

        // The decimal exponent of the leading significant digit.
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');

        if (digits.Length == 0)
        {
            return sign + "0.0";
        }

        var decimalExponent = exponent + integerLength - 1 - leadingZeros;

        if (decimalExponent is >= -4 and < 16)
        {
            if (decimalExponent < 0)
            {
                return sign + "0." + new string('0', -decimalExponent - 1) + digits;
            }

            var whole = digits.PadRight(decimalExponent + 1, '0');
            var fraction = whole[(decimalExponent + 1)..];
            return sign + whole[..(decimalExponent + 1)] + "." + (fraction.Length > 0 ? fraction : "0");
        }

        var mantissa = digits.Length > 1 ? digits[..1] + "." + digits[1..] : digits;
        return $"{sign}{mantissa}e{(decimalExponent < 0 ? "-" : "+")}{Math.Abs(decimalExponent):00}";
    }
}
