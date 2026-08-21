using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// jq's <c>@</c> formats, which convert a value to a string for a particular destination.
/// </summary>
/// <remarks>
/// Each exists because the naive conversion is wrong somewhere: <c>@sh</c> quotes for a
/// shell, <c>@uri</c> percent-encodes, <c>@csv</c> and <c>@tsv</c> escape their separators.
/// Using the right one is the difference between a filter that composes and one that
/// produces an injection.
/// </remarks>
internal static class JqFormats
{
    /// <summary>Applies the named format to <paramref name="value"/>.</summary>
    public static string Apply(string name, JsonValue value) => name switch
    {
        "text" => JqValues.ToText(value),
        "json" => JsonWriter.Write(value, JsonFormat.Compact),
        "base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(JqValues.ToText(value))),
        "base64d" => Decode(JqValues.ToText(value)),
        "uri" => Uri(JqValues.ToText(value)),
        "csv" => Separated(value, ",", Csv),
        "tsv" => Separated(value, "\t", Tsv),
        "html" => Html(JqValues.ToText(value)),
        "sh" => Shell(value),
        _ => throw new JqException($"{name} is not a valid format"),
    };

    private static string Decode(string text)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
        catch (FormatException)
        {
            throw new JqException($"{text} is not valid base64 data");
        }
    }

    private static string Uri(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')
            {
                builder.Append(c);
                continue;
            }

            builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string Html(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '<' => "&lt;",
                '>' => "&gt;",
                '&' => "&amp;",
                '\'' => "&#39;",
                '"' => "&quot;",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    private static string Separated(JsonValue value, string separator, Func<JsonValue, string> cell)
    {
        if (value is not JsonArray array)
        {
            throw new JqException($"{JqEvaluator.Describe(value)} cannot be csv-formatted, only an array can be");
        }

        return string.Join(separator, array.Items.Select(cell));
    }

    private static string Csv(JsonValue value) => value switch
    {
        JsonNull => string.Empty,
        JsonNumber number => JsonWriter.Number(number.Value),
        JsonBool boolean => boolean.Value ? "true" : "false",
        JsonString text => "\"" + text.Value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        _ => throw new JqException($"{JqEvaluator.Describe(value)} is not valid in a csv row"),
    };

    private static string Tsv(JsonValue value) => value switch
    {
        JsonNull => string.Empty,
        JsonNumber number => JsonWriter.Number(number.Value),
        JsonBool boolean => boolean.Value ? "true" : "false",
        JsonString text => text.Value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal),
        _ => throw new JqException($"{JqEvaluator.Describe(value)} is not valid in a tsv row"),
    };

    private static string Shell(JsonValue value)
    {
        if (value is JsonArray array)
        {
            return string.Join(" ", array.Items.Select(Quote));
        }

        return Quote(value);
    }

    private static string Quote(JsonValue value) => value switch
    {
        JsonString text => "'" + text.Value.Replace("'", "'\\''", StringComparison.Ordinal) + "'",
        JsonNumber or JsonBool or JsonNull => JsonWriter.Write(value, JsonFormat.Compact),
        _ => throw new JqException($"{JqEvaluator.Describe(value)} can not be escaped for shell"),
    };
}
