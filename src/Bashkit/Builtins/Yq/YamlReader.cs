using System.Globalization;
using System.Text;
using Bashkit.Builtins.Jq;

namespace Bashkit.Builtins.Yq;

/// <summary>A YAML document that could not be parsed.</summary>
/// <param name="message">The diagnostic, phrased as libyaml phrases it.</param>
internal sealed class YamlException(string message) : Exception(message);

/// <summary>
/// Parses YAML into the same value model jq uses.
/// </summary>
/// <remarks>
/// <para>
/// Sharing <see cref="JsonValue"/> is the whole point: YAML's data model is JSON's plus
/// syntax, so once a document is read every jq filter applies to it unchanged and
/// <c>yq</c> is a front end rather than a second implementation.
/// </para>
/// <para>
/// The parser covers what configuration files actually use — block mappings and sequences,
/// flow collections, the three scalar styles, literal and folded blocks, comments,
/// multi-document streams. Anchors, aliases, tags and merge keys are not supported; a
/// document using them is reported rather than silently mis-read.
/// </para>
/// </remarks>
internal sealed class YamlReader
{
    private readonly List<string> _lines;
    private int _index;

    private YamlReader(string text) =>
        _lines = [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    /// <summary>Parses a stream of YAML documents.</summary>
    public static List<JsonValue> ParseAll(string text)
    {
        var reader = new YamlReader(text);
        return reader.ReadDocuments();
    }

    private string? Current => _index < _lines.Count ? _lines[_index] : null;

    private List<JsonValue> ReadDocuments()
    {
        var documents = new List<JsonValue>();

        while (true)
        {
            SkipIgnorable();

            if (_index >= _lines.Count)
            {
                return documents;
            }

            if (Current!.TrimEnd() == "---")
            {
                _index++;
                continue;
            }

            if (Current!.TrimEnd() == "...")
            {
                _index++;
                continue;
            }

            documents.Add(ReadBlock(0) ?? JsonNull.Instance);
        }
    }

    /// <summary>Skips blank lines and whole-line comments.</summary>
    private void SkipIgnorable()
    {
        while (_index < _lines.Count)
        {
            var trimmed = _lines[_index].Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                _index++;
                continue;
            }

            return;
        }
    }

    private static int IndentOf(string line)
    {
        var indent = 0;

        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        return indent;
    }

    private bool AtDocumentBoundary() =>
        Current is { } line && (line.TrimEnd() == "---" || line.TrimEnd() == "...");

    /// <summary>Reads whatever node begins at or below <paramref name="indent"/>.</summary>
    private JsonValue? ReadBlock(int indent)
    {
        SkipIgnorable();

        if (_index >= _lines.Count || AtDocumentBoundary())
        {
            return null;
        }

        var line = Current!;
        var actual = IndentOf(line);

        if (actual < indent)
        {
            return null;
        }

        var rest = line[actual..];

        if (rest == "-" || rest.StartsWith("- ", StringComparison.Ordinal))
        {
            return ReadSequence(actual);
        }

        if (FindKeyEnd(rest) >= 0)
        {
            return ReadMapping(actual);
        }

        // A bare scalar as the whole node.
        _index++;
        return ReadScalar(rest, actual);
    }

    private JsonValue ReadSequence(int indent)
    {
        var items = new List<JsonValue>();

        while (true)
        {
            SkipIgnorable();

            if (_index >= _lines.Count || AtDocumentBoundary())
            {
                break;
            }

            var line = Current!;

            if (IndentOf(line) != indent)
            {
                break;
            }

            var rest = line[indent..];

            if (rest != "-" && !rest.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            if (rest == "-")
            {
                _index++;
                items.Add(ReadBlock(indent + 1) ?? JsonNull.Instance);
                continue;
            }

            // `- key: value` starts a mapping whose indentation is the column after the
            // dash. Rewriting the line as spaces lets the ordinary block reader handle it.
            var content = rest[2..];
            var column = indent + 2;
            _lines[_index] = new string(' ', column) + content;
            items.Add(ReadBlock(column) ?? JsonNull.Instance);
        }

        return new JsonArray(items);
    }

    private JsonValue ReadMapping(int indent)
    {
        var mapping = new JsonObject();

        while (true)
        {
            SkipIgnorable();

            if (_index >= _lines.Count || AtDocumentBoundary())
            {
                break;
            }

            var line = Current!;

            if (IndentOf(line) != indent)
            {
                break;
            }

            var rest = line[indent..];
            var colon = FindKeyEnd(rest);

            if (colon < 0)
            {
                break;
            }

            var key = ScalarText(rest[..colon].TrimEnd());
            var value = rest[(colon + 1)..].Trim();
            _index++;

            if (value.Length == 0 || value.StartsWith('#'))
            {
                mapping.Set(key, ReadBlock(indent + 1) ?? JsonNull.Instance);
                continue;
            }

            if (value is "|" or "|-" or "|+" or ">" or ">-" or ">+")
            {
                mapping.Set(key, ReadBlockScalar(value, indent));
                continue;
            }

            mapping.Set(key, ReadScalar(value, indent));
        }

        return mapping;
    }

    /// <summary>
    /// Finds the <c>:</c> that ends a mapping key, or -1 when the line is not a key.
    /// </summary>
    /// <remarks>
    /// A colon only separates a key when it is followed by a space or ends the line, and
    /// only outside quotes and flow collections — otherwise <c>url: http://x</c> and
    /// <c>{a: 1}</c> would both be misread.
    /// </remarks>
    private static int FindKeyEnd(string text)
    {
        var depth = 0;
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    continue;

                case '[' or '{':
                    depth++;
                    continue;

                case ']' or '}':
                    depth--;
                    continue;

                case '#' when i > 0 && text[i - 1] == ' ':
                    return -1;

                case ':' when depth == 0 && (i + 1 >= text.Length || text[i + 1] == ' '):
                    return i;
            }
        }

        return -1;
    }

    private JsonValue ReadBlockScalar(string header, int indent)
    {
        var folded = header.StartsWith('>');
        var chomp = header.Length > 1 ? header[1] : '\0';
        var collected = new List<string>();
        var contentIndent = -1;

        while (_index < _lines.Count)
        {
            var line = _lines[_index];

            if (line.Trim().Length == 0)
            {
                collected.Add(string.Empty);
                _index++;
                continue;
            }

            var actual = IndentOf(line);

            if (actual <= indent)
            {
                break;
            }

            contentIndent = contentIndent < 0 ? actual : Math.Min(contentIndent, actual);
            collected.Add(line);
            _index++;
        }

        if (contentIndent < 0)
        {
            return new JsonString(string.Empty);
        }

        var text = collected
            .Select(line => line.Length > contentIndent ? line[contentIndent..] : string.Empty)
            .ToList();

        // Trailing blank lines are dropped unless `+` asked for them to be kept.
        while (chomp != '+' && text.Count > 0 && text[^1].Length == 0)
        {
            text.RemoveAt(text.Count - 1);
        }

        var joined = folded ? string.Join(' ', text) : string.Join('\n', text);
        return new JsonString(chomp == '-' ? joined : joined + "\n");
    }

    /// <summary>Reads a scalar or a flow collection, joining continuation lines as needed.</summary>
    private JsonValue ReadScalar(string text, int indent)
    {
        if (text.Length == 0)
        {
            return JsonNull.Instance;
        }

        if (text[0] is '[' or '{')
        {
            var flow = new StringBuilder(text);

            while (!IsBalanced(flow.ToString()))
            {
                if (_index >= _lines.Count)
                {
                    throw new YamlException(
                        $"did not find expected node content at line {_lines.Count} column 1, while parsing a flow node");
                }

                flow.Append(' ').Append(_lines[_index++].Trim());
            }

            return ParseFlow(flow.ToString(), out _);
        }

        _ = indent;
        return ScalarValue(text);
    }

    private static bool IsBalanced(string text)
    {
        var depth = 0;
        var quote = '\0';

        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'': quote = c; break;
                case '[' or '{': depth++; break;
                case ']' or '}': depth--; break;
            }
        }

        return depth == 0;
    }

    private static JsonValue ParseFlow(string text, out int consumed)
    {
        var position = 0;
        var value = ParseFlowNode(text, ref position);
        consumed = position;
        return value;
    }

    private static JsonValue ParseFlowNode(string text, ref int position)
    {
        SkipFlowSpace(text, ref position);

        if (position >= text.Length)
        {
            throw new YamlException(
                "did not find expected node content at line 1 column 1, while parsing a flow node");
        }

        switch (text[position])
        {
            case '[':
            {
                position++;
                var items = new List<JsonValue>();
                SkipFlowSpace(text, ref position);

                if (position < text.Length && text[position] == ']')
                {
                    position++;
                    return new JsonArray(items);
                }

                while (true)
                {
                    items.Add(ParseFlowNode(text, ref position));
                    SkipFlowSpace(text, ref position);

                    if (position < text.Length && text[position] == ',')
                    {
                        position++;
                        continue;
                    }

                    if (position < text.Length && text[position] == ']')
                    {
                        position++;
                        return new JsonArray(items);
                    }

                    throw new YamlException(
                        "did not find expected ',' or ']' while parsing a flow sequence");
                }
            }

            case '{':
            {
                position++;
                var mapping = new JsonObject();
                SkipFlowSpace(text, ref position);

                if (position < text.Length && text[position] == '}')
                {
                    position++;
                    return mapping;
                }

                while (true)
                {
                    var key = ParseFlowScalarText(text, ref position);
                    SkipFlowSpace(text, ref position);

                    if (position < text.Length && text[position] == ':')
                    {
                        position++;
                        mapping.Set(key, ParseFlowNode(text, ref position));
                    }
                    else
                    {
                        mapping.Set(key, JsonNull.Instance);
                    }

                    SkipFlowSpace(text, ref position);

                    if (position < text.Length && text[position] == ',')
                    {
                        position++;
                        continue;
                    }

                    if (position < text.Length && text[position] == '}')
                    {
                        position++;
                        return mapping;
                    }

                    throw new YamlException(
                        "did not find expected ',' or '}' while parsing a flow mapping");
                }
            }

            default:
                return ScalarValue(ParseFlowScalarText(text, ref position));
        }
    }

    private static void SkipFlowSpace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private static string ParseFlowScalarText(string text, ref int position)
    {
        SkipFlowSpace(text, ref position);

        if (position >= text.Length)
        {
            return string.Empty;
        }

        if (text[position] is '"' or '\'')
        {
            var quote = text[position++];
            var builder = new StringBuilder();

            while (position < text.Length && text[position] != quote)
            {
                if (quote == '"' && text[position] == '\\' && position + 1 < text.Length)
                {
                    builder.Append(Unescape(text[++position]));
                    position++;
                    continue;
                }

                builder.Append(text[position++]);
            }

            position++;
            return quote == '"' ? builder.ToString() : builder.ToString();
        }

        var start = position;

        while (position < text.Length && text[position] is not (',' or ']' or '}' or ':'))
        {
            position++;
        }

        return text[start..position].Trim();
    }

    private static char Unescape(char escape) => escape switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        '0' => '\0',
        _ => escape,
    };

    /// <summary>Strips quotes from a scalar without interpreting its type — used for keys.</summary>
    private static string ScalarText(string text)
    {
        if (text.Length >= 2 && text[0] == text[^1] && text[0] is '"' or '\'')
        {
            var inner = text[1..^1];
            return text[0] == '"' ? UnescapeDouble(inner) : inner.Replace("''", "'", StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// Resolves a plain scalar's type, which in YAML is decided by its spelling.
    /// </summary>
    /// <remarks>
    /// Quoting is what makes a scalar a string: <c>yes</c> is a string here (YAML 1.2
    /// dropped the boolean spellings), but <c>"1"</c> stays a string while <c>1</c> is a
    /// number, and that distinction survives the round-trip through jq's value model.
    /// </remarks>
    private static JsonValue ScalarValue(string text)
    {
        if (text.Length >= 2 && text[0] == text[^1] && text[0] is '"' or '\'')
        {
            return new JsonString(ScalarText(text));
        }

        // An unquoted trailing comment is not part of the value.
        var comment = text.IndexOf(" #", StringComparison.Ordinal);

        if (comment >= 0)
        {
            text = text[..comment].TrimEnd();
        }

        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL": return JsonNull.Instance;
            case "true" or "True" or "TRUE": return JsonBool.True;
            case "false" or "False" or "FALSE": return JsonBool.False;
            case ".inf" or ".Inf": return new JsonNumber(double.PositiveInfinity);
            case "-.inf" or "-.Inf": return new JsonNumber(double.NegativeInfinity);
            case ".nan" or ".NaN": return new JsonNumber(double.NaN);
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && text.Length > 2
            && text[2..].All(Uri.IsHexDigit))
        {
            return new JsonNumber((double)Convert.ToInt64(text[2..], 16));
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && text.All(static c => char.IsAsciiDigit(c) || c is '.' or '-' or '+' or 'e' or 'E'))
        {
            return new JsonNumber(number);
        }

        return new JsonString(text);
    }

    private static string UnescapeDouble(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                builder.Append(text[i]);
                continue;
            }

            var escape = text[++i];

            if (escape == 'u' && i + 4 < text.Length)
            {
                builder.Append((char)Convert.ToInt32(text.Substring(i + 1, 4), 16));
                i += 4;
                continue;
            }

            builder.Append(Unescape(escape));
        }

        return builder.ToString();
    }
}
