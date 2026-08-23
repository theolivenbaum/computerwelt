using System.Globalization;
using System.Numerics;
using System.Text;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary><c>str</c>.</summary>
public sealed class PyStr : PyObject
{
    /// <summary>Creates a string.</summary>
    public PyStr(string value) => Value = value;

    /// <summary>The empty string.</summary>
    public static PyStr Empty { get; } = new(string.Empty);

    /// <summary>The underlying text.</summary>
    public string Value { get; }

    /// <summary>
    /// The offset of each code point, or null when every code point is one UTF-16 unit.
    /// </summary>
    /// <remarks>
    /// Python indexes a string by code point, .NET by UTF-16 unit; the two differ only for
    /// a string holding an astral character, so the map is built only for those and every
    /// other string keeps direct indexing.
    /// </remarks>
    private int[]? Offsets
    {
        get
        {
            if (_offsets is null && !_scanned)
            {
                _scanned = true;

                for (var i = 0; i < Value.Length; i++)
                {
                    if (!char.IsHighSurrogate(Value[i]))
                    {
                        continue;
                    }

                    var offsets = new List<int>(Value.Length);

                    for (var j = 0; j < Value.Length; j++)
                    {
                        offsets.Add(j);

                        if (char.IsHighSurrogate(Value[j]) && j + 1 < Value.Length && char.IsLowSurrogate(Value[j + 1]))
                        {
                            j++;
                        }
                    }

                    _offsets = [.. offsets];
                    break;
                }
            }

            return _offsets;
        }
    }

    private int[]? _offsets;
    private bool _scanned;

    /// <summary>The number of code points, which is what Python calls the length.</summary>
    private int Count => Offsets?.Length ?? Value.Length;

    /// <summary>The code point at <paramref name="index"/>, as a one-character string.</summary>
    private string At(int index)
    {
        if (Offsets is not { } offsets)
        {
            return Value[index].ToString();
        }

        var start = offsets[index];
        var end = index + 1 < offsets.Length ? offsets[index + 1] : Value.Length;
        return Value[start..end];
    }

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

    // A string is immutable, and the same one is hashed on every dictionary lookup it
    // takes part in — a global's name is hashed once per read of that global.
    private int _hash;
    private bool _hashed;

    /// <inheritdoc />
    public override BigInteger PyHash()
    {
        if (!_hashed)
        {
            _hash = StringComparer.Ordinal.GetHashCode(Value);
            _hashed = true;
        }

        return new BigInteger(_hash);
    }

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) =>
        other is PyStr text ? string.CompareOrdinal(Value, text.Value) : null;

    /// <inheritdoc />
    public override int? Length() => Count;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return new PyStr(At(i));
        }
    }

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
            var (start, _, step, count) = slice.Resolve(Count);
            var builder = new StringBuilder(count);

            for (var i = 0; i < count; i++)
            {
                builder.Append(At(start + (i * step)));
            }

            return new PyStr(builder.ToString());
        }

        if (index is not PyInt integer)
        {
            throw new PyRaise(PyErrors.TypeError($"string indices must be integers, not '{index.TypeName}'"));
        }

        return new PyStr(At(Normalize(integer.ToIndex(), Count, "string index out of range")));
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

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

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
                        break;
                    }

                    // A surrogate pair is one code point, and it is printable or not as a
                    // whole; testing the halves separately would call every pair unprintable.
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        var codePoint = char.ConvertToUtf32(c, value[i + 1]);
                        i++;

                        if (IsPrintable(char.GetUnicodeCategory(value, i - 1)))
                        {
                            builder.Append(c).Append(value[i]);
                        }
                        else
                        {
                            builder.Append("\\U").Append(codePoint.ToString("x8", CultureInfo.InvariantCulture));
                        }

                        break;
                    }

                    if (c == ' ' || IsPrintable(CharUnicodeInfo.GetUnicodeCategory(c)))
                    {
                        builder.Append(c);
                    }
                    else if (c <= 0xFF)
                    {
                        builder.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }

                    break;
            }
        }

        builder.Append(quote);
        return builder.ToString();
    }

    /// <summary>
    /// Escapes every non-ASCII character of an existing <c>repr</c>, the way <c>ascii()</c>
    /// and the <c>!a</c> conversion do.
    /// </summary>
    internal static string Ascii(string repr)
    {
        if (repr.All(char.IsAscii))
        {
            return repr;
        }

        var builder = new StringBuilder(repr.Length);

        for (var i = 0; i < repr.Length; i++)
        {
            var c = repr[i];

            if (char.IsAscii(c))
            {
                builder.Append(c);
            }
            else if (char.IsHighSurrogate(c) && i + 1 < repr.Length && char.IsLowSurrogate(repr[i + 1]))
            {
                builder.Append("\\U")
                    .Append(char.ConvertToUtf32(c, repr[++i]).ToString("x8", CultureInfo.InvariantCulture));
            }
            else if (c <= 0xFF)
            {
                builder.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether a character survives <c>repr</c> as itself.
    /// </summary>
    /// <remarks>
    /// This is <c>str.isprintable</c>'s rule: everything except the separators (other than
    /// a plain space) and the "other" categories — control, format, surrogate, private use
    /// and unassigned. Testing for a control character alone would leave a no-break space
    /// or a line separator in the output looking exactly like an ordinary one.
    /// </remarks>
    private static bool IsPrintable(UnicodeCategory category) => category is not (
        UnicodeCategory.Control
        or UnicodeCategory.Format
        or UnicodeCategory.Surrogate
        or UnicodeCategory.PrivateUse
        or UnicodeCategory.OtherNotAssigned
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.SpaceSeparator);
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
    public override IEnumerable<PyObject>? Iterate() => Value.Select(static b => PyInt.From(b));

    /// <inheritdoc />
    /// <remarks>
    /// Iterating bytes yields ints, but <c>in</c> also accepts a bytes operand and looks
    /// for it as a subsequence — the one place the two views meet.
    /// </remarks>
    public override bool Contains(PyObject item) => item switch
    {
        PyBytes needle => Value.AsSpan().IndexOf(needle.Value) >= 0,
        PyInt b when b.Value >= 0 && b.Value <= 255 => Value.Contains((byte)b.Value),
        PyInt => throw new PyRaise(PyErrors.ValueError("byte must be in range(0, 256)")),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"a bytes-like object is required, not '{item.TypeName}'")),
    };

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
        return PyInt.From(Value[PyStr.Normalize(integer.ToIndex(), Value.Length, "index out of range")]);
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

    /// <inheritdoc />
    /// <remarks>Slices compare as the (start, stop, step) triple they carry.</remarks>
    public override bool PyEquals(PyObject other) =>
        other is PySlice slice && Same(Start, slice.Start) && Same(Stop, slice.Stop) && Same(Step, slice.Step);

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
        var step = Step is null or PyNone ? 1 : ToInt(Step, "slice step", length);

        if (step == 0)
        {
            throw new PyRaise(PyErrors.ValueError("slice step cannot be zero"));
        }

        int start, stop;

        if (step > 0)
        {
            start = Start is null or PyNone ? 0 : Clamp(ToInt(Start, "slice index", length), length, 0, length);
            stop = Stop is null or PyNone ? length : Clamp(ToInt(Stop, "slice index", length), length, 0, length);
        }
        else
        {
            start = Start is null or PyNone ? length - 1 : Clamp(ToInt(Start, "slice index", length), length, -1, length - 1);
            stop = Stop is null or PyNone ? -1 : Clamp(ToInt(Stop, "slice index", length), length, -1, length - 1);
        }

        var span = step > 0 ? stop - start : start - stop;
        var count = span <= 0 ? 0 : ((span - 1) / Math.Abs(step)) + 1;

        return (start, stop, step, count);
    }

    private static bool Same(PyObject? left, PyObject? right) =>
        (left ?? PyNone.Instance).PyEquals(right ?? PyNone.Instance);

    /// <summary>Reads a slice bound or step, clipping it to the sequence it applies to.</summary>
    /// <remarks>
    /// Slice components clip rather than overflow: `'hello'[::-(2**63)]` is a legal slice,
    /// and any magnitude past the length behaves exactly as the length itself does.
    /// </remarks>
    private static int ToInt(PyObject value, string what, int length)
    {
        if (value is not PyInt integer)
        {
            throw new PyRaise(PyErrors.TypeError($"{what} must be an integer or None"));
        }

        var bound = BigInteger.Min((BigInteger)length + 1, int.MaxValue);
        return (int)BigInteger.Clamp(integer.Value, -bound, bound);
    }

    private static int Clamp(int index, int length, int low, int high)
    {
        var resolved = index < 0 ? length + index : index;
        return Math.Clamp(resolved, low, high);
    }
}
