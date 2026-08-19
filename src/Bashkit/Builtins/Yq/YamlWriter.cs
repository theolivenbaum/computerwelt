using System.Globalization;
using System.Text;
using Bashkit.Builtins.Jq;

namespace Bashkit.Builtins.Yq;

/// <summary>
/// Renders a value as YAML.
/// </summary>
/// <remarks>
/// Object keys are emitted in sorted order. That is not YAML's requirement but it is what
/// makes <c>yq</c> output stable: two runs over the same data produce byte-identical
/// documents, which is what lets the output be diffed or committed.
/// </remarks>
internal static class YamlWriter
{
    /// <summary>Renders <paramref name="value"/> as a YAML document, newline-terminated.</summary>
    public static string Write(JsonValue value)
    {
        var builder = new StringBuilder();
        WriteNode(builder, value, 0, inline: false);

        if (builder.Length == 0 || builder[^1] != '\n')
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes a node.
    /// </summary>
    /// <param name="builder">Where the text goes.</param>
    /// <param name="value">The node.</param>
    /// <param name="indent">The column its lines start at.</param>
    /// <param name="inline">
    /// True when the caller has already positioned the cursor for the node's first line —
    /// which is what puts a mapping's first key on the same line as the <c>-</c> that
    /// introduces it.
    /// </param>
    private static void WriteNode(StringBuilder builder, JsonValue value, int indent, bool inline)
    {
        switch (value)
        {
            case JsonArray array:
                WriteSequence(builder, array, indent, inline);
                return;

            case JsonObject json:
                WriteMapping(builder, json, indent, inline);
                return;

            default:
                if (!inline)
                {
                    builder.Append(' ', indent);
                }

                builder.Append(Scalar(value)).Append('\n');
                return;
        }
    }

    private static void WriteSequence(StringBuilder builder, JsonArray array, int indent, bool inline)
    {
        if (array.Items.Count == 0)
        {
            if (!inline)
            {
                builder.Append(' ', indent);
            }

            builder.Append("[]\n");
            return;
        }

        var first = true;

        foreach (var item in array.Items)
        {
            if (!first || !inline)
            {
                builder.Append(' ', indent);
            }

            first = false;
            builder.Append("- ");

            if (item is JsonArray or JsonObject)
            {
                WriteNode(builder, item, indent + 2, inline: true);
                continue;
            }

            builder.Append(Scalar(item)).Append('\n');
        }
    }

    private static void WriteMapping(StringBuilder builder, JsonObject json, int indent, bool inline)
    {
        if (json.Count == 0)
        {
            if (!inline)
            {
                builder.Append(' ', indent);
            }

            builder.Append("{}\n");
            return;
        }

        var first = true;

        foreach (var key in json.Keys.Order(StringComparer.Ordinal))
        {
            if (!first || !inline)
            {
                builder.Append(' ', indent);
            }

            first = false;
            builder.Append(Key(key)).Append(':');

            switch (json[key]!)
            {
                // A sequence under a key sits at the key's own indent, which is the style
                // every widely used YAML emitter produces.
                case JsonArray { Items.Count: > 0 } sequence:
                    builder.Append('\n');
                    WriteSequence(builder, sequence, indent, inline: false);
                    break;

                case JsonObject { Count: > 0 } nested:
                    builder.Append('\n');
                    WriteMapping(builder, nested, indent + 2, inline: false);
                    break;

                case { } scalar:
                    builder.Append(' ').Append(Scalar(scalar)).Append('\n');
                    break;
            }
        }
    }

    private static string Key(string key) => NeedsQuoting(key) ? JsonWriter.Quote(key) : key;

    private static string Scalar(JsonValue value) => value switch
    {
        JsonNull => "null",
        JsonBool boolean => boolean.Value ? "true" : "false",
        JsonNumber number => JsonWriter.Number(number.Value),
        JsonString text => NeedsQuoting(text.Value) ? JsonWriter.Quote(text.Value) : text.Value,
        JsonArray => "[]",
        _ => "{}",
    };

    /// <summary>
    /// Whether a string has to be quoted to survive a round-trip.
    /// </summary>
    /// <remarks>
    /// A plain scalar that reads back as a number, a boolean or null would change type, so
    /// those spellings are quoted; so are strings carrying YAML's structural characters or
    /// edge whitespace.
    /// </remarks>
    private static bool NeedsQuoting(string text)
    {
        if (text.Length == 0 || text != text.Trim())
        {
            return true;
        }

        switch (text)
        {
            // YAML 1.2 dropped `yes`/`no`/`on`/`off` as booleans, so they need no quoting.
            case "true" or "false" or "null" or "~":
                return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        if (text[0] is '-' or '?' or ':' or ',' or '[' or ']' or '{' or '}' or '#' or '&' or '*'
            or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`')
        {
            return true;
        }

        return text.Contains(": ", StringComparison.Ordinal)
            || text.Contains(" #", StringComparison.Ordinal)
            || text.EndsWith(':')
            || text.Contains('\n', StringComparison.Ordinal);
    }
}
