using System.Globalization;
using System.Numerics;
using System.Text;

namespace Monty.Runtime;

/// <summary><c>str</c>.</summary>
public sealed class PyStr : PyObject
{
    /// <summary>Creates a string.</summary>
    public PyStr(string value) => Value = value;

    /// <summary>The empty string.</summary>
    public static PyStr Empty { get; } = new(string.Empty);

    /// <summary>The underlying text.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string TypeName => "str";

    /// <inheritdoc />
    public override bool IsTruthy() => Value.Length > 0;

    /// <inheritdoc />
    public override string Display() => Value;

    /// <inheritdoc />
    public override string Repr() => Quote(Value);

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) =>
        other is PyStr text && string.Equals(Value, text.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override BigInteger PyHash() => new(StringComparer.Ordinal.GetHashCode(Value));

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) =>
        other is PyStr text ? string.CompareOrdinal(Value, text.Value) : null;

    /// <inheritdoc />
    public override int? Length() => Value.Length;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => Value.Select(static c => new PyStr(c.ToString()));

    /// <inheritdoc />
    public override bool Contains(PyObject item) =>
        item is PyStr text
            ? Value.Contains(text.Value, StringComparison.Ordinal)
            : throw new PyRaise(PyErrors.TypeError(
                $"'in <string>' requires string as left operand, not {item.TypeName}"));

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index)
    {
        if (index is PySlice slice)
        {
            var (start, _, step, count) = slice.Resolve(Value.Length);
            var builder = new StringBuilder(count);

            for (var i = 0; i < count; i++)
            {
                builder.Append(Value[start + (i * step)]);
            }

            return new PyStr(builder.ToString());
        }

        if (index is not PyInt integer)
        {
            throw new PyRaise(PyErrors.TypeError($"string indices must be integers, not '{index.TypeName}'"));
        }

        var position = Normalize(integer.ToIndex(), Value.Length, "string index out of range");
        return new PyStr(Value[position].ToString());
    }

    /// <summary>Resolves a possibly negative index, raising when it is out of range.</summary>
    internal static int Normalize(int index, int length, string message)
    {
        var resolved = index < 0 ? length + index : index;

        if (resolved < 0 || resolved >= length)
        {
            throw new PyRaise(PyErrors.IndexError(message));
        }

        return resolved;
    }

    /// <summary>
    /// Quotes a string the way Python's <c>repr</c> does: single quotes by default,
    /// double quotes when the value contains a single quote but no double one.
    /// </summary>
    internal static string Quote(string value)
    {
        var quote = value.Contains('\'', StringComparison.Ordinal)
            && !value.Contains('"', StringComparison.Ordinal)
            ? '"'
            : '\'';

        var builder = new StringBuilder(value.Length + 2);
        builder.Append(quote);

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c == quote)
                    {
                        builder.Append('\\').Append(c);
                    }
                    else if (char.IsControl(c))
                    {
                        builder.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append(quote);
        return builder.ToString();
    }
}

/// <summary><c>bytes</c>.</summary>
public sealed class PyBytes : PyObject
{
    /// <summary>Creates a bytes object.</summary>
    public PyBytes(byte[] value) => Value = value;

    /// <summary>The underlying bytes.</summary>
    public byte[] Value { get; }

    /// <inheritdoc />
    public override string TypeName => "bytes";

    /// <inheritdoc />
    public override bool IsTruthy() => Value.Length > 0;

    /// <inheritdoc />
    public override int? Length() => Value.Length;

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) =>
        other is PyBytes bytes && Value.AsSpan().SequenceEqual(bytes.Value);

    /// <inheritdoc />
    public override BigInteger PyHash()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return new BigInteger(hash.ToHashCode());
    }

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) =>
        other is PyBytes bytes ? Value.AsSpan().SequenceCompareTo(bytes.Value) : null;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => Value.Select(static b => new PyInt(b));

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index)
    {
        if (index is PySlice slice)
        {
            var (start, _, step, count) = slice.Resolve(Value.Length);
            var result = new byte[count];

            for (var i = 0; i < count; i++)
            {
                result[i] = Value[start + (i * step)];
            }

            return new PyBytes(result);
        }

        if (index is not PyInt integer)
        {
            throw new PyRaise(PyErrors.TypeError($"byte indices must be integers, not '{index.TypeName}'"));
        }

        // Indexing bytes yields an int, not a one-byte bytes — a classic Python surprise.
        return new PyInt(Value[PyStr.Normalize(integer.ToIndex(), Value.Length, "index out of range")]);
    }

    /// <inheritdoc />
    public override string Repr()
    {
        // A value containing `'` but not `"` is quoted with `"`, so the apostrophe needs no
        // escape — the same rule CPython applies to str and bytes alike.
        var quote = Value.Contains((byte)'\'') && !Value.Contains((byte)'"') ? '"' : '\'';
        var builder = new StringBuilder("b").Append(quote);

        foreach (var b in Value)
        {
            switch (b)
            {
                case (byte)'\\': builder.Append("\\\\"); break;
                case (byte)'\n': builder.Append("\\n"); break;
                case (byte)'\r': builder.Append("\\r"); break;
                case (byte)'\t': builder.Append("\\t"); break;
                case (byte)'\'' when quote == '\'': builder.Append("\\'"); break;
                case (byte)'"' when quote == '"': builder.Append("\\\""); break;
                default:
                    if (b is >= 0x20 and < 0x7f)
                    {
                        builder.Append((char)b);
                    }
                    else
                    {
                        builder.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    }

                    break;
            }
        }

        return builder.Append(quote).ToString();
    }
}

/// <summary>A <c>slice</c> object.</summary>
public sealed class PySlice : PyObject
{
    /// <summary>Creates a slice.</summary>
    public PySlice(PyObject? start, PyObject? stop, PyObject? step)
    {
        Start = start;
        Stop = stop;
        Step = step;
    }

    /// <summary>The start bound, or null when omitted.</summary>
    public PyObject? Start { get; }

    /// <summary>The stop bound, or null when omitted.</summary>
    public PyObject? Stop { get; }

    /// <summary>The step, or null when omitted.</summary>
    public PyObject? Step { get; }

    /// <inheritdoc />
    public override string TypeName => "slice";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "start" => Start ?? PyNone.Instance,
        "stop" => Stop ?? PyNone.Instance,
        "step" => Step ?? PyNone.Instance,
        _ => null,
    };

    /// <inheritdoc />
    public override string Repr() =>
        $"slice({Start?.Repr() ?? "None"}, {Stop?.Repr() ?? "None"}, {Step?.Repr() ?? "None"})";

    /// <summary>
    /// Resolves the slice against a sequence of <paramref name="length"/> elements,
    /// returning the first index, the exclusive end, the step and the element count.
    /// </summary>
    /// <remarks>
    /// Slicing never raises for out-of-range bounds — it clamps — which is why this cannot
    /// reuse the indexing path.
    /// </remarks>
    public (int Start, int Stop, int Step, int Count) Resolve(int length)
    {
        var step = Step is null or PyNone ? 1 : ToInt(Step, "slice step");

        if (step == 0)
        {
            throw new PyRaise(PyErrors.ValueError("slice step cannot be zero"));
        }

        int start, stop;

        if (step > 0)
        {
            start = Start is null or PyNone ? 0 : Clamp(ToInt(Start, "slice index"), length, 0, length);
            stop = Stop is null or PyNone ? length : Clamp(ToInt(Stop, "slice index"), length, 0, length);
        }
        else
        {
            start = Start is null or PyNone ? length - 1 : Clamp(ToInt(Start, "slice index"), length, -1, length - 1);
            stop = Stop is null or PyNone ? -1 : Clamp(ToInt(Stop, "slice index"), length, -1, length - 1);
        }

        var span = step > 0 ? stop - start : start - stop;
        var count = span <= 0 ? 0 : ((span - 1) / Math.Abs(step)) + 1;

        return (start, stop, step, count);
    }

    private static int ToInt(PyObject value, string what) =>
        value is PyInt integer
            ? integer.ToIndex()
            : throw new PyRaise(PyErrors.TypeError($"{what} must be an integer or None"));

    private static int Clamp(int index, int length, int low, int high)
    {
        var resolved = index < 0 ? length + index : index;
        return Math.Clamp(resolved, low, high);
    }
}
