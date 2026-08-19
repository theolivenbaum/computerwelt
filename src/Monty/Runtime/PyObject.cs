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

    /// <summary>Membership, for <c>in</c>.</summary>
    public virtual bool Contains(PyObject item)
    {
        var items = Iterate() ?? throw new PyRaise(
            PyErrors.TypeError($"argument of type '{TypeName}' is not iterable"));

        foreach (var candidate in items)
        {
            if (candidate.PyEquals(item))
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

    /// <inheritdoc />
    public override string Repr() => Value.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other switch
    {
        PyInt integer => Value == integer.Value,
        PyFloat number => (double)Value == number.Value,
        _ => false,
    };

    /// <inheritdoc />
    public override BigInteger PyHash() => Value;

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) => other switch
    {
        PyInt integer => Value.CompareTo(integer.Value),
        PyFloat number => ((double)Value).CompareTo(number.Value),
        _ => null,
    };

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
        PyFloat number => Value.Equals(number.Value),
        PyInt integer => Value == (double)integer.Value,
        _ => false,
    };

    /// <inheritdoc />
    public override BigInteger PyHash() =>
        double.IsFinite(Value) && double.IsInteger(Value) && Math.Abs(Value) < 1e18
            ? new BigInteger(Value)
            : new BigInteger(Value.GetHashCode());

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) => other switch
    {
        PyFloat number => Value.CompareTo(number.Value),
        PyInt integer => Value.CompareTo((double)integer.Value),
        _ => null,
    };

    /// <summary>
    /// Formats a float the way Python's <c>repr</c> does: the shortest text that round
    /// trips, always with a decimal point or exponent so it cannot be mistaken for an int.
    /// </summary>
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

        if (text.Contains('E', StringComparison.Ordinal))
        {
            // Python writes `1e+20`, not `1E+20`.
            var mantissaEnd = text.IndexOf('E', StringComparison.Ordinal);
            var mantissa = text[..mantissaEnd];
            var exponent = int.Parse(text[(mantissaEnd + 1)..], CultureInfo.InvariantCulture);

            if (!mantissa.Contains('.', StringComparison.Ordinal))
            {
                mantissa += ".0";
            }

            return $"{mantissa}e{(exponent < 0 ? "-" : "+")}{Math.Abs(exponent):00}";
        }

        return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
    }
}
