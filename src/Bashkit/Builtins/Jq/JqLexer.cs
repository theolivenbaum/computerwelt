using System.Globalization;
using System.Text;

namespace Bashkit.Builtins.Jq;

/// <summary>The lexical categories of a jq filter.</summary>
internal enum JqTokenKind
{
    /// <summary>End of input.</summary>
    End,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>A string literal, possibly with interpolations.</summary>
    String,

    /// <summary>An identifier or keyword.</summary>
    Identifier,

    /// <summary>A <c>$name</c> reference.</summary>
    Variable,

    /// <summary>An <c>@name</c> format.</summary>
    Format,

    /// <summary>An operator or punctuator.</summary>
    Operator,

    /// <summary>A <c>.name</c> field, lexed as one token so <c>.a.b</c> needs no lookahead.</summary>
    Field,
}

/// <summary>One jq token.</summary>
/// <param name="Kind">Its category.</param>
/// <param name="Text">Its text, unescaped for literals.</param>
/// <param name="Parts">
/// For an interpolated string, the alternating literal text and sub-token lists.
/// </param>
internal readonly record struct JqToken(JqTokenKind Kind, string Text, IReadOnlyList<object>? Parts = null)
{
    /// <summary>True when this is the operator <paramref name="text"/>.</summary>
    public bool Is(string text) =>
        Kind == JqTokenKind.Operator && string.Equals(Text, text, StringComparison.Ordinal);

    /// <summary>True when this is the keyword <paramref name="text"/>.</summary>
    public bool IsWord(string text) =>
        Kind == JqTokenKind.Identifier && string.Equals(Text, text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => $"{Kind}({Text})";
}

/// <summary>
/// Turns a jq filter into tokens.
/// </summary>
/// <remarks>
/// <para>
/// Two things are done here that a naive lexer defers to the parser, because doing them
/// later is much harder. <c>.name</c> is one token, so a chain like <c>.a.b?</c> parses
/// without backtracking over the dot. And a string's interpolations are lexed as they are
/// found, so <c>"a\(.x + "b")c"</c> nests correctly — the inner quote belongs to the inner
/// expression, not to the outer string.
/// </para>
/// </remarks>
internal sealed class JqLexer
{
    private static readonly string[] Operators =
    [
        // Longest first: `//=` must not lex as `//` then `=`.
        "?//", "//=", "|=", "+=", "-=", "*=", "/=", "%=", "==", "!=", "<=", ">=", "//", "..",
        ".", "|", ",", ":", ";", "(", ")", "[", "]", "{", "}", "+", "-", "*", "/", "%",
        "<", ">", "=", "?",
    ];

    private readonly string _source;
    private int _position;

    /// <summary>Creates a lexer over <paramref name="source"/>.</summary>
    public JqLexer(string source) => _source = source ?? string.Empty;

    /// <summary>Tokenizes the whole filter.</summary>
    public List<JqToken> Tokenize()
    {
        var tokens = new List<JqToken>();

        while (true)
        {
            var token = Next();
            tokens.Add(token);

            if (token.Kind == JqTokenKind.End)
            {
                return tokens;
            }
        }
    }

    private char Current => _position < _source.Length ? _source[_position] : '\0';

    private char Peek(int offset = 1) =>
        _position + offset < _source.Length ? _source[_position + offset] : '\0';

    private JqToken Next()
    {
        SkipTrivia();

        if (_position >= _source.Length)
        {
            return new JqToken(JqTokenKind.End, string.Empty);
        }

        var c = Current;

        if (c == '"')
        {
            var parts = ReadInterpolatedString();
            return new JqToken(JqTokenKind.String, string.Empty, parts);
        }

        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek())))
        {
            return new JqToken(JqTokenKind.Number, ReadNumber());
        }

        // `.name` is one token; a bare `.` or `..` falls through to the operator table.
        if (c == '.' && (char.IsAsciiLetter(Peek()) || Peek() == '_'))
        {
            _position++;
            return new JqToken(JqTokenKind.Field, ReadName());
        }

        if (c == '$')
        {
            _position++;
            return new JqToken(JqTokenKind.Variable, ReadName());
        }

        if (c == '@')
        {
            _position++;
            return new JqToken(JqTokenKind.Format, ReadName());
        }

        if (char.IsAsciiLetter(c) || c == '_')
        {
            return new JqToken(JqTokenKind.Identifier, ReadQualifiedName());
        }

        foreach (var op in Operators)
        {
            if (!Matches(op))
            {
                continue;
            }

            _position += op.Length;
            return new JqToken(JqTokenKind.Operator, op);
        }

        // `and`, `or` and `not` are words, so the only remaining symbols are these two.
        if (c is '|' or '&')
        {
            _position++;
            return new JqToken(JqTokenKind.Operator, c.ToString());
        }

        throw new JqException($"jq: syntax error: unexpected character '{c}'");
    }

    private bool Matches(string op) =>
        _position + op.Length <= _source.Length
        && string.CompareOrdinal(_source, _position, op, 0, op.Length) == 0;

    private void SkipTrivia()
    {
        while (_position < _source.Length)
        {
            var c = _source[_position];

            if (char.IsWhiteSpace(c))
            {
                _position++;
                continue;
            }

            if (c == '#')
            {
                while (_position < _source.Length && _source[_position] != '\n')
                {
                    _position++;
                }

                continue;
            }

            return;
        }
    }

    private string ReadName()
    {
        var start = _position;

        while (_position < _source.Length && (char.IsAsciiLetterOrDigit(_source[_position]) || _source[_position] == '_'))
        {
            _position++;
        }

        return _source[start.._position];
    }

    /// <summary>Reads a name that may carry a <c>::</c> module qualifier.</summary>
    private string ReadQualifiedName()
    {
        var name = ReadName();

        while (Current == ':' && Peek() == ':')
        {
            _position += 2;
            name += "::" + ReadName();
        }

        return name;
    }

    private string ReadNumber()
    {
        var start = _position;

        while (_position < _source.Length && (char.IsAsciiDigit(_source[_position]) || _source[_position] == '.'))
        {
            _position++;
        }

        if (_position < _source.Length && _source[_position] is 'e' or 'E')
        {
            var save = _position;
            _position++;

            if (_position < _source.Length && _source[_position] is '+' or '-')
            {
                _position++;
            }

            if (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
            {
                while (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
                {
                    _position++;
                }
            }
            else
            {
                _position = save;
            }
        }

        return _source[start.._position];
    }

    /// <summary>
    /// Reads a string literal, returning its parts: <see cref="string"/> for literal text
    /// and <see cref="List{T}"/> of tokens for each <c>\(...)</c> interpolation.
    /// </summary>
    private List<object> ReadInterpolatedString()
    {
        _position++;
        var parts = new List<object>();
        var builder = new StringBuilder();

        while (_position < _source.Length)
        {
            var c = _source[_position];

            if (c == '"')
            {
                _position++;

                if (builder.Length > 0 || parts.Count == 0)
                {
                    parts.Add(builder.ToString());
                }

                return parts;
            }

            if (c != '\\')
            {
                builder.Append(c);
                _position++;
                continue;
            }

            _position++;

            if (_position >= _source.Length)
            {
                break;
            }

            var escape = _source[_position++];

            if (escape == '(')
            {
                if (builder.Length > 0)
                {
                    parts.Add(builder.ToString());
                    builder.Clear();
                }

                parts.Add(ReadInterpolation());
                continue;
            }

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
                    if (_position + 4 > _source.Length)
                    {
                        throw new JqException("jq: syntax error: truncated \\u escape");
                    }

                    builder.Append((char)Convert.ToInt32(_source.Substring(_position, 4), 16));
                    _position += 4;
                    break;
                }

                default:
                    throw new JqException($"jq: syntax error: invalid escape '\\{escape}'");
            }
        }

        throw new JqException("jq: syntax error: unterminated string");
    }

    /// <summary>
    /// Lexes the filter inside <c>\(...)</c>, stopping at the parenthesis that closes it
    /// rather than at the first one — the interpolation may contain nested parentheses and
    /// strings of its own.
    /// </summary>
    private List<JqToken> ReadInterpolation()
    {
        var tokens = new List<JqToken>();
        var depth = 1;

        while (true)
        {
            SkipTrivia();

            if (_position >= _source.Length)
            {
                throw new JqException("jq: syntax error: unterminated interpolation");
            }

            if (Current == ')' && depth == 1)
            {
                _position++;
                tokens.Add(new JqToken(JqTokenKind.End, string.Empty));
                return tokens;
            }

            var token = Next();

            if (token.Is("("))
            {
                depth++;
            }
            else if (token.Is(")"))
            {
                depth--;
            }

            tokens.Add(token);
        }
    }

    /// <summary>Parses a jq numeric literal.</summary>
    public static double ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
