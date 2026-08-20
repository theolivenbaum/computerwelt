using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// The seven JSON types, ordered as jq orders them.
/// </summary>
/// <remarks>
/// The order matters: <c>sort</c>, <c>min</c>, <c>max</c>, <c>unique</c> and every
/// comparison operator are defined in terms of it, and <c>false</c> ranks below
/// <c>true</c> rather than sharing a "boolean" rank.
/// </remarks>
internal enum JsonKind
{
    /// <summary><c>null</c>.</summary>
    Null,

    /// <summary><c>false</c>.</summary>
    False,

    /// <summary><c>true</c>.</summary>
    True,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A string.</summary>
    String,

    /// <summary>An array.</summary>
    Array,

    /// <summary>An object.</summary>
    Object,
}

/// <summary>A JSON value.</summary>
/// <remarks>
/// Values are treated as immutable: every jq operation builds a new value rather than
/// mutating one in place, which is what lets the same input flow down several branches of a
/// filter without them interfering.
/// </remarks>
internal abstract class JsonValue
{
    /// <summary>Which of the seven types this is.</summary>
    public abstract JsonKind Kind { get; }

    /// <summary>The type name jq's <c>type</c> builtin reports.</summary>
    public string TypeName => Kind switch
    {
        JsonKind.Null => "null",
        JsonKind.False or JsonKind.True => "boolean",
        JsonKind.Number => "number",
        JsonKind.String => "string",
        JsonKind.Array => "array",
        _ => "object",
    };

    /// <summary>Whether this value is true in a condition: everything but null and false.</summary>
    public bool IsTruthy => Kind is not (JsonKind.Null or JsonKind.False);

    /// <summary>Orders two values by jq's total order.</summary>
    public static int Compare(JsonValue left, JsonValue right)
    {
        if (left.Kind != right.Kind)
        {
            return left.Kind.CompareTo(right.Kind);
        }

        switch (left.Kind)
        {
            case JsonKind.Number:
                return ((JsonNumber)left).Value.CompareTo(((JsonNumber)right).Value);

            case JsonKind.String:
                return string.CompareOrdinal(((JsonString)left).Value, ((JsonString)right).Value);

            case JsonKind.Array:
            {
                var a = (JsonArray)left;
                var b = (JsonArray)right;

                for (var i = 0; i < Math.Min(a.Items.Count, b.Items.Count); i++)
                {
                    var order = Compare(a.Items[i], b.Items[i]);

                    if (order != 0)
                    {
                        return order;
                    }
                }

                return a.Items.Count.CompareTo(b.Items.Count);
            }

            case JsonKind.Object:
            {
                var a = (JsonObject)left;
                var b = (JsonObject)right;

                // Objects compare by their sorted key sets first, then by the values in
                // that order — jq's rule, not insertion order.
                var aKeys = a.Keys.Order(StringComparer.Ordinal).ToList();
                var bKeys = b.Keys.Order(StringComparer.Ordinal).ToList();

                for (var i = 0; i < Math.Min(aKeys.Count, bKeys.Count); i++)
                {
                    var order = string.CompareOrdinal(aKeys[i], bKeys[i]);

                    if (order != 0)
                    {
                        return order;
                    }
                }

                if (aKeys.Count != bKeys.Count)
                {
                    return aKeys.Count.CompareTo(bKeys.Count);
                }

                foreach (var key in aKeys)
                {
                    var order = Compare(a[key]!, b[key]!);

                    if (order != 0)
                    {
                        return order;
                    }
                }

                return 0;
            }

            default:
                return 0;
        }
    }

    /// <summary>Deep equality, which is <see cref="Compare"/> returning zero.</summary>
    public static bool DeepEquals(JsonValue left, JsonValue right) => Compare(left, right) == 0;

    /// <summary>Wraps a boolean.</summary>
    public static JsonValue Of(bool value) => value ? JsonBool.True : JsonBool.False;

    /// <summary>Wraps a number.</summary>
    public static JsonValue Of(double value) => new JsonNumber(value);

    /// <summary>Wraps a string.</summary>
    public static JsonValue Of(string value) => new JsonString(value);

    /// <inheritdoc />
    public override string ToString() => JsonWriter.Write(this, JsonFormat.Compact);
}

/// <summary><c>null</c>.</summary>
internal sealed class JsonNull : JsonValue
{
    private JsonNull()
    {
    }

    /// <summary>The single null instance.</summary>
    public static JsonNull Instance { get; } = new();

    /// <inheritdoc />
    public override JsonKind Kind => JsonKind.Null;
}

/// <summary><c>true</c> or <c>false</c>.</summary>
internal sealed class JsonBool : JsonValue
{
    private JsonBool(bool value) => Value = value;

    /// <summary>The single true instance.</summary>
    public static JsonBool True { get; } = new(true);

    /// <summary>The single false instance.</summary>
    public static JsonBool False { get; } = new(false);

    /// <summary>Which of the two this is.</summary>
    public bool Value { get; }

    /// <inheritdoc />
    public override JsonKind Kind => Value ? JsonKind.True : JsonKind.False;
}

/// <summary>A JSON number, always held as a double as jq does.</summary>
/// <param name="value">The value.</param>
internal sealed class JsonNumber(double value) : JsonValue
{
    /// <summary>The value.</summary>
    public double Value { get; } = value;

    /// <inheritdoc />
    public override JsonKind Kind => JsonKind.Number;
}

/// <summary>A JSON string.</summary>
/// <param name="value">The text.</param>
internal sealed class JsonString(string value) : JsonValue
{
    /// <summary>The text.</summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override JsonKind Kind => JsonKind.String;
}

/// <summary>A JSON array.</summary>
internal sealed class JsonArray : JsonValue
{
    /// <summary>Creates an empty array.</summary>
    public JsonArray() => Items = [];

    /// <summary>Creates an array over <paramref name="items"/>, which it takes ownership of.</summary>
    public JsonArray(List<JsonValue> items) => Items = items;

    /// <summary>Creates an array copying <paramref name="items"/>.</summary>
    public JsonArray(IEnumerable<JsonValue> items) => Items = [.. items];

    /// <summary>The elements.</summary>
    public List<JsonValue> Items { get; }

    /// <inheritdoc />
    public override JsonKind Kind => JsonKind.Array;
}

/// <summary>
/// A JSON object that remembers the order its keys were inserted in.
/// </summary>
/// <remarks>
/// jq preserves key order on output unless <c>-S</c> is given, so the order is part of the
/// value rather than an artefact of the container.
/// </remarks>
internal sealed class JsonObject : JsonValue
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, JsonValue> _values = new(StringComparer.Ordinal);

    /// <summary>Creates an empty object.</summary>
    public JsonObject()
    {
    }

    /// <summary>Creates a copy of <paramref name="source"/>, keys in the same order.</summary>
    public JsonObject(JsonObject source)
    {
        _order = [.. source._order];
        _values = new Dictionary<string, JsonValue>(source._values, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override JsonKind Kind => JsonKind.Object;

    /// <summary>The keys, in insertion order.</summary>
    public IReadOnlyList<string> Keys => _order;

    /// <summary>How many entries there are.</summary>
    public int Count => _order.Count;

    /// <summary>The value for <paramref name="key"/>, or null when absent.</summary>
    public JsonValue? this[string key] => _values.GetValueOrDefault(key);

    /// <summary>True when <paramref name="key"/> is present.</summary>
    public bool ContainsKey(string key) => _values.ContainsKey(key);

    /// <summary>Sets an entry, appending it when the key is new.</summary>
    public void Set(string key, JsonValue value)
    {
        if (!_values.ContainsKey(key))
        {
            _order.Add(key);
        }

        _values[key] = value;
    }

    /// <summary>Removes an entry.</summary>
    public void Remove(string key)
    {
        if (_values.Remove(key))
        {
            _order.Remove(key);
        }
    }

    /// <summary>The entries, in insertion order.</summary>
    public IEnumerable<(string Key, JsonValue Value)> Entries()
    {
        foreach (var key in _order)
        {
            yield return (key, _values[key]);
        }
    }
}

/// <summary>How a value is rendered.</summary>
/// <param name="Indent">The indent string, or null for compact output.</param>
/// <param name="SortKeys">Whether object keys are sorted rather than kept in order.</param>
internal readonly record struct JsonFormat(string? Indent, bool SortKeys)
{
    /// <summary>Everything on one line, as <c>-c</c> produces.</summary>
    public static JsonFormat Compact { get; } = new(null, false);

    /// <summary>Two-space indentation, jq's default.</summary>
    public static JsonFormat Pretty { get; } = new("  ", false);
}

/// <summary>Renders values as JSON text.</summary>
internal static class JsonWriter
{
    /// <summary>Renders <paramref name="value"/> under <paramref name="format"/>.</summary>
    public static string Write(JsonValue value, JsonFormat format)
    {
        var builder = new StringBuilder();
        Write(builder, value, format, 0);
        return builder.ToString();
    }

    /// <summary>
    /// Formats a number the way jq does: integral values print without a decimal point, and
    /// everything else uses the shortest representation that round-trips.
    /// </summary>
    public static string Number(double value)
    {
        if (double.IsNaN(value))
        {
            return "null";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "1.7976931348623157e+308";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-1.7976931348623157e+308";
        }

        if (value == Math.Floor(value) && Math.Abs(value) < 1e17)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);

        // .NET writes `1E+100`; JSON and jq write `1e+100`.
        return text.Replace("E", "e", StringComparison.Ordinal);
    }

    /// <summary>Renders a JSON string literal, escapes and all.</summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        Quote(builder, value);
        return builder.ToString();
    }

    private static void Quote(StringBuilder builder, string value)
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

                case < ' ':
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    break;

                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
    }

    private static void Write(StringBuilder builder, JsonValue value, JsonFormat format, int depth)
    {
        switch (value)
        {
            case JsonNull:
                builder.Append("null");
                return;

            case JsonBool boolean:
                builder.Append(boolean.Value ? "true" : "false");
                return;

            case JsonNumber number:
                builder.Append(Number(number.Value));
                return;

            case JsonString text:
                Quote(builder, text.Value);
                return;

            case JsonArray array:
                WriteArray(builder, array, format, depth);
                return;

            case JsonObject json:
                WriteObject(builder, json, format, depth);
                return;
        }
    }

    private static void WriteArray(StringBuilder builder, JsonArray array, JsonFormat format, int depth)
    {
        if (array.Items.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append('[');

        for (var i = 0; i < array.Items.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            NewLine(builder, format, depth + 1);
            Write(builder, array.Items[i], format, depth + 1);
        }

        NewLine(builder, format, depth);
        builder.Append(']');
    }

    private static void WriteObject(StringBuilder builder, JsonObject json, JsonFormat format, int depth)
    {
        if (json.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        var keys = format.SortKeys ? json.Keys.Order(StringComparer.Ordinal).ToList() : json.Keys.ToList();
        builder.Append('{');

        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            NewLine(builder, format, depth + 1);
            Quote(builder, keys[i]);
            builder.Append(':');

            if (format.Indent is not null)
            {
                builder.Append(' ');
            }

            Write(builder, json[keys[i]]!, format, depth + 1);
        }

        NewLine(builder, format, depth);
        builder.Append('}');
    }

    private static void NewLine(StringBuilder builder, JsonFormat format, int depth)
    {
        if (format.Indent is null)
        {
            return;
        }

        builder.Append('\n');

        for (var i = 0; i < depth; i++)
        {
            builder.Append(format.Indent);
        }
    }
}
