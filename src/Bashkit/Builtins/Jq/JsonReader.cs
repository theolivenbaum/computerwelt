using System.Globalization;
using System.Text;

namespace Bashkit.Builtins.Jq;

/// <summary>
/// Parses JSON text into <see cref="JsonValue"/>s.
/// </summary>
/// <remarks>
/// Hand-written rather than delegating to <c>System.Text.Json</c> because jq needs things a
/// general-purpose reader does not offer: object key order preserved as data, a stream of
/// several whitespace-separated values from one document (NDJSON), and a bounded nesting
/// depth so a hostile input cannot exhaust the stack.
/// </remarks>
internal sealed class JsonReader
{
    private const int MaxDepth = 256;

    private readonly string _text;
    private int _position;

    private JsonReader(string text) => _text = text;

    /// <summary>Parses exactly one value, rejecting trailing content.</summary>
    public static JsonValue Parse(string text)
    {
        var reader = new JsonReader(text);
        var value = reader.ReadValue(0);
        reader.SkipWhitespace();

        if (reader._position < reader._text.Length)
        {
            throw new JqException("Unexpected extra JSON input");
        }

        return value;
    }

    /// <summary>
    /// Parses every value in <paramref name="text"/>, which may hold several separated only
    /// by whitespace — the NDJSON that <c>jq</c> reads by default.
    /// </summary>
    public static List<JsonValue> ParseAll(string text)
    {
        var reader = new JsonReader(text);
        var values = new List<JsonValue>();

        while (true)
        {
            reader.SkipWhitespace();

            if (reader._position >= reader._text.Length)
            {
                return values;
            }

            values.Add(reader.ReadValue(0));
        }
    }

    private char Current => _position < _text.Length ? _text[_position] : '\0';

    private void SkipWhitespace()
    {
        while (_position < _text.Length && _text[_position] is ' ' or '\t' or '\n' or '\r')
        {
            _position++;
        }
    }

    private JsonValue ReadValue(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new JqException("JSON nesting is too deep");
        }

        SkipWhitespace();

        switch (Current)
        {
            case '{': return ReadObject(depth);
            case '[': return ReadArray(depth);
            case '"': return new JsonString(ReadString());

            case 't':
                Expect("true");
                return JsonBool.True;

            case 'f':
                Expect("false");
                return JsonBool.False;

            case 'n':
                Expect("null");
                return JsonNull.Instance;

            case '\0':
                throw new JqException("Unexpected end of JSON input");

            default:
                return new JsonNumber(ReadNumber());
        }
    }

    private void Expect(string literal)
    {
        if (_position + literal.Length > _text.Length
            || string.CompareOrdinal(_text, _position, literal, 0, literal.Length) != 0)
        {
            throw new JqException($"Invalid JSON text at offset {_position}");
        }

        _position += literal.Length;
    }

    private JsonValue ReadObject(int depth)
    {
        _position++;
        var json = new JsonObject();
        SkipWhitespace();

        if (Current == '}')
        {
            _position++;
            return json;
        }

        while (true)
        {
            SkipWhitespace();

            if (Current != '"')
            {
                throw new JqException($"Expected a JSON object key at offset {_position}");
            }

            var key = ReadString();
            SkipWhitespace();

            if (Current != ':')
            {
                throw new JqException($"Expected ':' at offset {_position}");
            }

            _position++;
            json.Set(key, ReadValue(depth + 1));
            SkipWhitespace();

            if (Current == ',')
            {
                _position++;
                continue;
            }

            if (Current == '}')
            {
                _position++;
                return json;
            }

            throw new JqException($"Expected ',' or '}}' at offset {_position}");
        }
    }

    private JsonValue ReadArray(int depth)
    {
        _position++;
        var items = new List<JsonValue>();
        SkipWhitespace();

        if (Current == ']')
        {
            _position++;
            return new JsonArray(items);
        }

        while (true)
        {
            items.Add(ReadValue(depth + 1));
            SkipWhitespace();

            if (Current == ',')
            {
                _position++;
                continue;
            }

            if (Current == ']')
            {
                _position++;
                return new JsonArray(items);
            }

            throw new JqException($"Expected ',' or ']' at offset {_position}");
        }
    }

    private string ReadString()
    {
        _position++;
        var builder = new StringBuilder();

        while (_position < _text.Length)
        {
            var c = _text[_position++];

            if (c == '"')
            {
                return builder.ToString();
            }

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (_position >= _text.Length)
            {
                break;
            }

            var escape = _text[_position++];

            switch (escape)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;

                case 'u':
                {
                    if (_position + 4 > _text.Length)
                    {
                        throw new JqException("Truncated \\u escape in JSON string");
                    }

                    builder.Append((char)Convert.ToInt32(_text.Substring(_position, 4), 16));
                    _position += 4;
                    break;
                }

                default:
                    throw new JqException($"Invalid escape '\\{escape}' in JSON string");
            }
        }

        throw new JqException("Unterminated JSON string");
    }

    private double ReadNumber()
    {
        var start = _position;

        if (Current is '-' or '+')
        {
            _position++;
        }

        while (_position < _text.Length && (char.IsAsciiDigit(_text[_position]) || _text[_position] == '.'))
        {
            _position++;
        }

        if (_position < _text.Length && _text[_position] is 'e' or 'E')
        {
            _position++;

            if (_position < _text.Length && _text[_position] is '+' or '-')
            {
                _position++;
            }

            while (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
            {
                _position++;
            }
        }

        var span = _text.AsSpan(start, _position - start);

        if (span.IsEmpty
            || !double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new JqException($"Invalid JSON number at offset {start}");
        }

        return value;
    }
}
