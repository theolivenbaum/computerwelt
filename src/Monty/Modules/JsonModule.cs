using System.Globalization;
using System.Numerics;
using System.Text;
using Monty.Runtime;

namespace Monty.Modules;

/// <summary>The <c>json</c> module.</summary>
/// <remarks>
/// Written directly rather than over <c>System.Text.Json</c>: Python's encoder emits
/// <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> as bare tokens, sorts keys on request,
/// and has its own separator and indent rules, none of which the .NET writer produces.
/// </remarks>
public static class JsonModule
{
    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("json");

        module.Add("dumps", static (arguments, keywords) =>
        {
            Arity.AtLeast("dumps", arguments, 1);

            var indent = Keyword(keywords, "indent");
            var sortKeys = Keyword(keywords, "sort_keys")?.IsTruthy() ?? false;

            var builder = new StringBuilder();
            Write(builder, arguments[0], indent is PyInt spaces ? spaces.ToIndex() : null, 0, sortKeys);
            return new PyStr(builder.ToString());
        });

        module.Add("loads", static arguments =>
        {
            Arity.AtLeast("loads", arguments, 1);

            var text = arguments[0].Display();
            var position = 0;
            var value = Read(text, ref position);

            SkipWhitespace(text, ref position);

            if (position < text.Length)
            {
                throw Error($"Extra data: line 1 column {position + 1} (char {position})");
            }

            return value;
        });

        return module;
    }

    private static PyObject? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) && value is not PyNone
            ? value
            : null;

    private static PyException JsonDecodeError(string message) =>
        new(PyExceptionType.Registry.TryGetValue("ValueError", out var type) ? type : PyExceptionType.ValueError, message);

    private static PyRaise Error(string message) => new(JsonDecodeError(message));

    // ---- writing ----

    private static void Write(StringBuilder builder, PyObject value, int? indent, int depth, bool sortKeys)
    {
        switch (value)
        {
            case PyNone:
                builder.Append("null");
                return;

            case PyBool flag:
                builder.Append(flag.Value ? "true" : "false");
                return;

            case PyInt integer:
                builder.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
                return;

            case PyFloat number:
                builder.Append(double.IsNaN(number.Value) ? "NaN"
                    : double.IsPositiveInfinity(number.Value) ? "Infinity"
                    : double.IsNegativeInfinity(number.Value) ? "-Infinity"
                    : PyFloat.Format(number.Value));
                return;

            case PyStr text:
                WriteString(builder, text.Value);
                return;

            case PyList or PyTuple:
            {
                var items = (value as PyList)?.Items.AsEnumerable() ?? ((PyTuple)value).Items;
                WriteSequence(builder, items.ToList(), indent, depth, sortKeys);
                return;
            }

            case PyDict dict:
                WriteMapping(builder, dict, indent, depth, sortKeys);
                return;

            default:
                throw new PyRaise(PyErrors.TypeError(
                    $"Object of type {value.TypeName} is not JSON serializable"));
        }
    }

    private static void WriteSequence(StringBuilder builder, List<PyObject> items, int? indent, int depth, bool sortKeys)
    {
        if (items.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append('[');

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            NewLine(builder, indent, depth + 1);
            Write(builder, items[i], indent, depth + 1, sortKeys);
        }

        NewLine(builder, indent, depth);
        builder.Append(']');
    }

    private static void WriteMapping(StringBuilder builder, PyDict dict, int? indent, int depth, bool sortKeys)
    {
        if (dict.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        var entries = dict.Entries.ToList();

        if (sortKeys)
        {
            entries.Sort(static (a, b) => string.CompareOrdinal(a.Key.Display(), b.Key.Display()));
        }

        builder.Append('{');

        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            NewLine(builder, indent, depth + 1);
            WriteString(builder, KeyText(entries[i].Key));
            builder.Append(indent is null ? ": " : ": ");
            Write(builder, entries[i].Value, indent, depth + 1, sortKeys);
        }

        NewLine(builder, indent, depth);
        builder.Append('}');
    }

    /// <summary>JSON keys are strings, so non-string Python keys are coerced as Python does.</summary>
    private static string KeyText(PyObject key) => key switch
    {
        PyStr text => text.Value,
        PyBool flag => flag.Value ? "true" : "false",
        PyInt or PyFloat => key.Display(),
        PyNone => "null",
        _ => throw new PyRaise(PyErrors.TypeError($"keys must be str, int, float, bool or None, not {key.TypeName}")),
    };

    private static void NewLine(StringBuilder builder, int? indent, int depth)
    {
        if (indent is not { } spaces)
        {
            return;
        }

        builder.Append('\n').Append(new string(' ', spaces * depth));
    }

    private static void WriteString(StringBuilder builder, string value)
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
                    if (c < 0x20)
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

    // ---- reading ----

    private static PyObject Read(string text, ref int position)
    {
        SkipWhitespace(text, ref position);

        if (position >= text.Length)
        {
            throw Error("Expecting value: line 1 column 1 (char 0)");
        }

        var c = text[position];

        switch (c)
        {
            case '{': return ReadObject(text, ref position);
            case '[': return ReadArray(text, ref position);
            case '"': return new PyStr(ReadString(text, ref position));
        }

        foreach (var (literal, value) in Literals)
        {
            if (position + literal.Length <= text.Length
                && text.AsSpan(position, literal.Length).SequenceEqual(literal))
            {
                position += literal.Length;
                return value();
            }
        }

        return ReadNumber(text, ref position);
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

    private static PyObject ReadObject(string text, ref int position)
    {
        var dict = new PyDict();
        position++;
        SkipWhitespace(text, ref position);

        if (position < text.Length && text[position] == '}')
        {
            position++;
            return dict;
        }

        while (true)
        {
            SkipWhitespace(text, ref position);

            if (position >= text.Length || text[position] != '"')
            {
                throw Error($"Expecting property name enclosed in double quotes: char {position}");
            }

            var key = ReadString(text, ref position);
            SkipWhitespace(text, ref position);

            if (position >= text.Length || text[position] != ':')
            {
                throw Error($"Expecting ':' delimiter: char {position}");
            }

            position++;
            dict.Set(new PyStr(key), Read(text, ref position));
            SkipWhitespace(text, ref position);

            if (position < text.Length && text[position] == ',')
            {
                position++;
                continue;
            }

            if (position < text.Length && text[position] == '}')
            {
                position++;
                return dict;
            }

            throw Error($"Expecting ',' delimiter: char {position}");
        }
    }

    private static PyObject ReadArray(string text, ref int position)
    {
        var items = new List<PyObject>();
        position++;
        SkipWhitespace(text, ref position);

        if (position < text.Length && text[position] == ']')
        {
            position++;
            return new PyList(items);
        }

        while (true)
        {
            items.Add(Read(text, ref position));
            SkipWhitespace(text, ref position);

            if (position < text.Length && text[position] == ',')
            {
                position++;
                continue;
            }

            if (position < text.Length && text[position] == ']')
            {
                position++;
                return new PyList(items);
            }

            throw Error($"Expecting ',' delimiter: char {position}");
        }
    }

    private static string ReadString(string text, ref int position)
    {
        position++;
        var builder = new StringBuilder();

        while (position < text.Length && text[position] != '"')
        {
            var c = text[position];

            if (c != '\\')
            {
                builder.Append(c);
                position++;
                continue;
            }

            position++;

            if (position >= text.Length)
            {
                break;
            }

            switch (text[position])
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
                    if (position + 4 >= text.Length)
                    {
                        throw Error("Invalid \\uXXXX escape");
                    }

                    builder.Append((char)Convert.ToInt32(text.Substring(position + 1, 4), 16));
                    position += 4;
                    break;
                }

                default:
                    throw Error($"Invalid \\escape: char {position}");
            }

            position++;
        }

        if (position >= text.Length)
        {
            throw Error("Unterminated string starting at");
        }

        position++;
        return builder.ToString();
    }

    private static PyObject ReadNumber(string text, ref int position)
    {
        var start = position;

        if (position < text.Length && text[position] is '-' or '+')
        {
            position++;
        }

        var isFloat = false;

        while (position < text.Length)
        {
            var c = text[position];

            if (char.IsAsciiDigit(c))
            {
                position++;
                continue;
            }

            if (c is '.' or 'e' or 'E' or '+' or '-')
            {
                isFloat = c is '.' or 'e' or 'E' || isFloat;
                position++;
                continue;
            }

            break;
        }

        var token = text[start..position];

        if (token.Length == 0)
        {
            throw Error($"Expecting value: char {start}");
        }

        if (!isFloat && BigInteger.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return new PyInt(integer);
        }

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return new PyFloat(number);
        }

        throw Error($"Expecting value: char {start}");
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }
}
