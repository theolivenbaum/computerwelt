using System.Globalization;
using System.Numerics;
using System.Text;
using Monty.Runtime;

namespace Monty.Modules;

/// <summary>The <c>json</c> module.</summary>
/// <remarks>
/// Written directly rather than over <c>System.Text.Json</c>: Python's encoder emits
/// <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> as bare tokens, coerces non-string keys,
/// sorts keys on request, and has its own separator and indent rules — and its decoder
/// reports positions as line/column pairs. None of that is what the .NET reader and writer
/// do, and scripts match on the exact messages.
/// </remarks>
public static class JsonModule
{
    /// <summary>The digit limit CPython applies to decimal integer conversion.</summary>
    private const int MaxIntegerDigits = 4300;

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("json");

        module.Add("JSONDecodeError", PyExceptionType.JsonDecodeError);

        module.Add("dumps", (arguments, keywords) =>
        {
            if (arguments.Length != 1)
            {
                throw new PyRaise(PyErrors.TypeError(DumpsArity(arguments.Length, keywords?.Count ?? 0)));
            }

            var options = Options.Read(keywords);
            var builder = new StringBuilder();

            // Nesting is bounded by the same limit function calls are, so a structure too
            // deep to serialize reports the same error a recursive function would.
            new Encoder(builder, options, machine.RecursionLimit).Write(arguments[0], 0, []);
            return new PyStr(builder.ToString());
        });

        module.Add("loads", static arguments =>
        {
            Arity.AtLeast("loads", arguments, 1);

            var text = arguments[0] switch
            {
                PyStr source => source.Value,
                PyBytes bytes => Encoding.UTF8.GetString(bytes.Value),
                var other => throw new PyRaise(PyErrors.TypeError(
                    $"the JSON object must be str, bytes or bytearray, not {other.TypeName}")),
            };

            var reader = new Reader(text);
            var value = reader.ReadValue();
            reader.SkipWhitespace();

            if (!reader.AtEnd)
            {
                throw reader.Fail("Extra data", reader.Position);
            }

            return value;
        });

        return module;
    }

    /// <summary>Words the arity error <c>dumps</c> reports, which names its keyword-only tail.</summary>
    private static string DumpsArity(int positional, int keywords) =>
        keywords == 0
            ? $"dumps() takes 1 positional argument but {positional} were given"
            : $"dumps() takes 1 positional argument but {positional} positional arguments "
              + $"(and {keywords} keyword-only argument{(keywords == 1 ? string.Empty : "s")}) were given";

    // ---- writing ----

    /// <summary>The encoder settings <c>dumps</c> was called with.</summary>
    private sealed record Options
    {
        private static readonly string[] Known =
            ["skipkeys", "ensure_ascii", "check_circular", "allow_nan", "cls", "indent",
             "separators", "default", "sort_keys"];

        public string? Indent { get; private init; }

        public string ItemSeparator { get; private init; } = ", ";

        public string KeySeparator { get; private init; } = ": ";

        public bool SortKeys { get; private init; }

        public bool SkipKeys { get; private init; }

        public bool EnsureAscii { get; private init; } = true;

        public bool AllowNan { get; private init; } = true;

        public static Options Read(PyDict? keywords)
        {
            foreach (var (key, _) in keywords?.Entries ?? [])
            {
                if (!Known.Contains(key.Display(), StringComparer.Ordinal))
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"JSONEncoder.__init__() got an unexpected keyword argument '{key.Display()}'"));
                }
            }

            var indent = ReadIndent(Keyword(keywords, "indent"));

            // Indenting changes the default item separator: a trailing space before a
            // newline would be invisible trailing whitespace.
            var item = indent is null ? ", " : ",";
            var keySeparator = ": ";

            if (Keyword(keywords, "separators") is { } separators)
            {
                (item, keySeparator) = ReadSeparators(separators);
            }

            return new Options
            {
                Indent = indent,
                ItemSeparator = item,
                KeySeparator = keySeparator,
                SortKeys = Keyword(keywords, "sort_keys")?.IsTruthy() ?? false,
                SkipKeys = Keyword(keywords, "skipkeys")?.IsTruthy() ?? false,
                EnsureAscii = Keyword(keywords, "ensure_ascii")?.IsTruthy() ?? true,
                AllowNan = Keyword(keywords, "allow_nan")?.IsTruthy() ?? true,
            };
        }

        /// <summary>
        /// Reads the <c>indent</c> argument, which may be a count, a string, or a boolean.
        /// </summary>
        /// <remarks>
        /// A negative count means no indentation but still one item per line, which is why
        /// this returns an empty string rather than null for it.
        /// </remarks>
        private static string? ReadIndent(PyObject? value) => value switch
        {
            null => null,
            PyStr text => text.Value,
            PyBool flag => flag.Value ? " " : string.Empty,
            PyInt count => new string(' ', Math.Max(0, count.ToIndex())),
            _ => null,
        };

        private static (string Item, string Key) ReadSeparators(PyObject value)
        {
            var items = value switch
            {
                PyTuple tuple => tuple.Items,
                PyList list => list.Items,
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"cannot unpack non-iterable {value.TypeName} object")),
            };

            if (items.Count > 2)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"too many values to unpack (expected 2, got {items.Count})"));
            }

            if (items.Count < 2)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"not enough values to unpack (expected 2, got {items.Count})"));
            }

            // The encoder takes them the other way round, and names them by position.
            if (items[0] is not PyStr item)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"make_encoder() argument 6 must be str, not {items[0].TypeName}"));
            }

            if (items[1] is not PyStr key)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"make_encoder() argument 5 must be str, not {items[1].TypeName}"));
            }

            return (item.Value, key.Value);
        }
    }

    /// <summary>Serializes one value tree.</summary>
    private sealed class Encoder(StringBuilder builder, Options options, int maxDepth)
    {
        public void Write(PyObject value, int depth, List<PyObject> stack)
        {
            if (depth > maxDepth)
            {
                throw new PyRaise(new PyException(PyExceptionType.RecursionError,
                    "maximum recursion depth exceeded while encoding a JSON object"));
            }

            switch (value)
            {
                case PyNone:
                    builder.Append("null");
                    return;

                case PyBool flag:
                    builder.Append(flag.Value ? "true" : "false");
                    return;

                case PyInt integer:
                    builder.Append(Digits(integer.Value));
                    return;

                case PyFloat number:
                    builder.Append(Number(number.Value));
                    return;

                case PyStr text:
                    WriteString(text.Value);
                    return;

                case PyList or PyTuple:
                {
                    var items = value is PyList list ? list.Items : ((PyTuple)value).Items;
                    WriteSequence(value, items, depth, stack);
                    return;
                }

                case PyDict dict:
                    WriteMapping(dict, depth, stack);
                    return;

                default:
                    throw new PyRaise(PyErrors.TypeError(
                        $"Object of type {value.TypeName} is not JSON serializable"));
            }
        }

        private void WriteSequence(PyObject owner, IReadOnlyList<PyObject> items, int depth, List<PyObject> stack)
        {
            if (items.Count == 0)
            {
                builder.Append("[]");
                return;
            }

            Enter(owner, stack);

            try
            {
                builder.Append('[');

                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(options.ItemSeparator);
                    }

                    Break(depth + 1);
                    Write(items[i], depth + 1, stack);
                }

                Break(depth);
                builder.Append(']');
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }

        private void WriteMapping(PyDict dict, int depth, List<PyObject> stack)
        {
            var entries = new List<KeyValuePair<PyObject, PyObject>>(dict.Count);
            var sources = new List<PyObject>(dict.Count);

            foreach (var entry in dict.Entries)
            {
                if (KeyText(entry.Key) is { } text)
                {
                    entries.Add(new KeyValuePair<PyObject, PyObject>(new PyStr(text), entry.Value));
                    sources.Add(entry.Key);
                }
            }

            if (entries.Count == 0)
            {
                builder.Append("{}");
                return;
            }

            if (options.SortKeys)
            {
                Sort(entries, sources);
            }

            Enter(dict, stack);

            try
            {
                builder.Append('{');

                for (var i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(options.ItemSeparator);
                    }

                    Break(depth + 1);
                    WriteString(((PyStr)entries[i].Key).Value);
                    builder.Append(options.KeySeparator);
                    Write(entries[i].Value, depth + 1, stack);
                }

                Break(depth);
                builder.Append('}');
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }

        /// <summary>
        /// Sorts the entries by their original keys.
        /// </summary>
        /// <remarks>
        /// Keys are ordered as the Python values they were, not as the strings they become,
        /// so sorting <c>{1: ..., 'b': ...}</c> fails exactly as <c>1 &lt; 'b'</c> does. The
        /// sort is written out rather than handed to <c>List.Sort</c>, which would wrap the
        /// raised <c>TypeError</c> in a host exception.
        /// </remarks>
        private static void Sort(List<KeyValuePair<PyObject, PyObject>> entries, List<PyObject> keys)
        {
            for (var i = 1; i < entries.Count; i++)
            {
                var entry = entries[i];
                var key = keys[i];
                var j = i - 1;

                // Compared this way round, an incomparable pair is named in the order
                // CPython's sort names it.
                while (j >= 0 && Compare(key, keys[j]) < 0)
                {
                    entries[j + 1] = entries[j];
                    keys[j + 1] = keys[j];
                    j--;
                }

                entries[j + 1] = entry;
                keys[j + 1] = key;
            }
        }

        private static int Compare(PyObject left, PyObject right) =>
            left.PyCompare(right) ?? throw new PyRaise(PyErrors.TypeError(
                $"'<' not supported between instances of '{left.TypeName}' and '{right.TypeName}'"));

        /// <summary>Pushes a container, refusing one that already encloses itself.</summary>
        private static void Enter(PyObject container, List<PyObject> stack)
        {
            if (stack.Any(entry => ReferenceEquals(entry, container)))
            {
                throw new PyRaise(PyErrors.ValueError("Circular reference detected"));
            }

            stack.Add(container);
        }

        /// <summary>
        /// The JSON text of a dictionary key, or null when the key is skipped.
        /// </summary>
        /// <remarks>
        /// JSON keys are strings, so Python coerces the few key types that have an obvious
        /// spelling and rejects the rest — unless <c>skipkeys</c> asks for them to be
        /// dropped instead.
        /// </remarks>
        private string? KeyText(PyObject key)
        {
            switch (key)
            {
                case PyStr text: return text.Value;
                case PyBool flag: return flag.Value ? "true" : "false";
                case PyInt integer: return Digits(integer.Value);
                case PyFloat number: return Number(number.Value);
                case PyNone: return "null";
            }

            return options.SkipKeys
                ? null
                : throw new PyRaise(PyErrors.TypeError(
                    $"keys must be str, int, float, bool or None, not {key.TypeName}"));
        }

        private string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                if (!options.AllowNan)
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"Out of range float values are not JSON compliant: {PyFloat.Format(value)}"));
                }

                return double.IsNaN(value) ? "NaN" : double.IsPositiveInfinity(value) ? "Infinity" : "-Infinity";
            }

            return PyFloat.Format(value);
        }

        /// <summary>Writes the break between items, when indenting.</summary>
        private void Break(int depth)
        {
            if (options.Indent is { } indent)
            {
                builder.Append('\n');

                for (var i = 0; i < depth; i++)
                {
                    builder.Append(indent);
                }
            }
        }

        private void WriteString(string value)
        {
            builder.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        // Surrogates are escaped one half at a time, which is what produces
                        // the `😀` pair CPython emits for an astral character.
                        if (c < 0x20 || (options.EnsureAscii && c > 0x7e))
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }

    /// <summary>Renders an integer, refusing one wider than the decimal conversion limit.</summary>
    private static string Digits(BigInteger value)
    {
        // The bound is checked before formatting, so a multi-million-digit value costs
        // nothing to reject.
        if (BigInteger.Abs(value) >= BigInteger.Pow(10, MaxIntegerDigits))
        {
            throw new PyRaise(PyErrors.ValueError(
                $"Exceeds the limit ({MaxIntegerDigits} digits) for integer string conversion"));
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static PyObject? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) && value is not PyNone
            ? value
            : null;

    // ---- reading ----

    /// <summary>A decoder that reports positions the way CPython's does.</summary>
    private sealed class Reader(string text)
    {
        public int Position { get; private set; }

        public bool AtEnd => Position >= text.Length;

        private char Current => text[Position];

        public PyObject ReadValue()
        {
            SkipWhitespace();

            if (AtEnd)
            {
                throw Fail("Expecting value", Position);
            }

            switch (Current)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return new PyStr(ReadString());
            }

            foreach (var (literal, value) in Literals)
            {
                if (Position + literal.Length <= text.Length
                    && text.AsSpan(Position, literal.Length).SequenceEqual(literal))
                {
                    Position += literal.Length;
                    return value();
                }
            }

            return ReadNumber();
        }

        private static readonly (string Literal, Func<PyObject> Value)[] Literals =
        [
            ("true", static () => PyBool.True),
            ("false", static () => PyBool.False),
            ("null", static () => PyNone.Instance),
            ("NaN", static () => new PyFloat(double.NaN)),
            ("Infinity", static () => new PyFloat(double.PositiveInfinity)),
            ("-Infinity", static () => new PyFloat(double.NegativeInfinity)),
        ];

        private PyObject ReadObject()
        {
            var dict = new PyDict();
            var open = Position;
            Position++;
            SkipWhitespace();

            if (!AtEnd && Current == '}')
            {
                Position++;
                return dict;
            }

            while (true)
            {
                SkipWhitespace();

                if (!AtEnd && Current == '}')
                {
                    // The comma that led here had nothing after it.
                    throw Fail("Illegal trailing comma before end of object", LastComma(open));
                }

                if (AtEnd || Current != '"')
                {
                    throw Fail("Expecting property name enclosed in double quotes", Position);
                }

                var key = ReadString();
                SkipWhitespace();

                if (AtEnd || Current != ':')
                {
                    throw Fail("Expecting ':' delimiter", Position);
                }

                Position++;
                dict.Set(new PyStr(key), ReadValue());
                SkipWhitespace();

                if (!AtEnd && Current == ',')
                {
                    Position++;
                    continue;
                }

                if (!AtEnd && Current == '}')
                {
                    Position++;
                    return dict;
                }

                throw Fail("Expecting ',' delimiter", Position);
            }
        }

        private PyObject ReadArray()
        {
            var items = new List<PyObject>();
            var open = Position;
            Position++;
            SkipWhitespace();

            if (!AtEnd && Current == ']')
            {
                Position++;
                return new PyList(items);
            }

            while (true)
            {
                SkipWhitespace();

                if (!AtEnd && Current == ']' && items.Count > 0)
                {
                    throw Fail("Illegal trailing comma before end of array", LastComma(open));
                }

                items.Add(ReadValue());
                SkipWhitespace();

                if (!AtEnd && Current == ',')
                {
                    Position++;
                    continue;
                }

                if (!AtEnd && Current == ']')
                {
                    Position++;
                    return new PyList(items);
                }

                throw Fail("Expecting ',' delimiter", Position);
            }
        }

        /// <summary>The position of the comma just before where reading stopped.</summary>
        private int LastComma(int from)
        {
            for (var i = Position - 1; i > from; i--)
            {
                if (text[i] == ',')
                {
                    return i;
                }
            }

            return Position;
        }

        private string ReadString()
        {
            var start = Position;
            Position++;
            var builder = new StringBuilder();

            while (!AtEnd && Current != '"')
            {
                if (Current != '\\')
                {
                    builder.Append(Current);
                    Position++;
                    continue;
                }

                var escape = Position;
                Position++;

                if (AtEnd)
                {
                    break;
                }

                switch (Current)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;

                    case 'u':
                    {
                        if (Position + 4 >= text.Length
                            || !int.TryParse(text.AsSpan(Position + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw Fail("Invalid \\uXXXX escape", escape + 1);
                        }

                        builder.Append((char)code);
                        Position += 4;
                        break;
                    }

                    default:
                        throw Fail("Invalid \\escape", escape);
                }

                Position++;
            }

            if (AtEnd)
            {
                throw Fail("Unterminated string starting at", start);
            }

            Position++;
            return builder.ToString();
        }

        private PyObject ReadNumber()
        {
            var start = Position;

            if (!AtEnd && Current is '-' or '+')
            {
                Position++;
            }

            var isFloat = false;

            while (!AtEnd)
            {
                var c = Current;

                if (char.IsAsciiDigit(c))
                {
                    Position++;
                    continue;
                }

                if (c is '.' or 'e' or 'E' or '+' or '-')
                {
                    isFloat = c is '.' or 'e' or 'E' || isFloat;
                    Position++;
                    continue;
                }

                break;
            }

            var token = text[start..Position];

            if (token.Length == 0 || token is "-" or "+")
            {
                Position = start;
                throw Fail("Expecting value", start);
            }

            if (!isFloat)
            {
                // The digit count is checked before parsing, so a huge literal is rejected
                // without ever allocating the number it names.
                var digits = token.Count(char.IsAsciiDigit);

                if (digits > MaxIntegerDigits)
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"Exceeds the limit ({MaxIntegerDigits} digits) for integer string conversion: "
                        + $"value has {digits} digits"));
                }

                if (BigInteger.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                {
                    return new PyInt(integer);
                }
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return new PyFloat(number);
            }

            Position = start;
            throw Fail("Expecting value", start);
        }

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
            {
                Position++;
            }
        }

        /// <summary>Builds the decode error, locating <paramref name="at"/> by line and column.</summary>
        public PyRaise Fail(string message, int at)
        {
            var line = 1;
            var column = 1;

            for (var i = 0; i < at && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return new PyRaise(new PyException(
                PyExceptionType.JsonDecodeError,
                $"{message}: line {line} column {column} (char {at})"));
        }
    }
}
