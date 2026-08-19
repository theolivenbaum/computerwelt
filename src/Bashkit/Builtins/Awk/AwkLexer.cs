using System.Globalization;
using System.Text;

namespace Bashkit.Builtins.Awk;

/// <summary>The lexical categories of an AWK program.</summary>
internal enum AwkTokenKind
{
    /// <summary>End of input.</summary>
    End,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>A string literal, already unescaped.</summary>
    String,

    /// <summary>A regular-expression literal.</summary>
    Regex,

    /// <summary>An identifier.</summary>
    Name,

    /// <summary>A function name immediately followed by <c>(</c>.</summary>
    FunctionName,

    /// <summary>A reserved word.</summary>
    Keyword,

    /// <summary>An operator or punctuator.</summary>
    Operator,

    /// <summary>A newline, which terminates a statement in AWK.</summary>
    Newline,
}

/// <summary>One AWK token.</summary>
/// <param name="Kind">Its category.</param>
/// <param name="Text">Its text, unescaped for literals.</param>
internal readonly record struct AwkToken(AwkTokenKind Kind, string Text)
{
    /// <summary>True when this is the operator or keyword <paramref name="text"/>.</summary>
    public bool Is(string text) =>
        Kind is AwkTokenKind.Operator or AwkTokenKind.Keyword && string.Equals(Text, text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => $"{Kind}({Text})";
}

/// <summary>
/// Turns AWK source into tokens.
/// </summary>
/// <remarks>
/// <para>
/// The one genuinely hard part is <c>/</c>: it is division in one position and the start of
/// a regex literal in another, and nothing local distinguishes them. The lexer resolves it
/// the way every AWK does — by remembering whether the previous token could end an
/// expression. After a value, <c>/</c> divides; anywhere else it opens a regex.
/// </para>
/// <para>
/// Newlines are significant, so they are emitted rather than skipped, except where the
/// grammar says a line may continue: after a comma, an operator, or an opening brace.
/// </para>
/// </remarks>
internal sealed class AwkLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "BEGIN", "END", "function", "func", "if", "else", "while", "for", "do",
        "break", "continue", "next", "nextfile", "exit", "return", "delete",
        "in", "getline", "print", "printf",
    };

    private static readonly string[] Operators =
    [
        // Longest first: `**=` must not lex as `**` then `=`.
        "**=", ">>", "<=", ">=", "==", "!=", "&&", "||", "++", "--",
        "+=", "-=", "*=", "/=", "%=", "^=", "**", "!~",
        "{", "}", "(", ")", "[", "]", ";", ",", "+", "-", "*", "/", "%", "^",
        "<", ">", "=", "!", "?", ":", "~", "$", "|", "&",
    ];

    private readonly string _source;
    private int _position;
    private bool _afterValue;

    /// <summary>Creates a lexer over <paramref name="source"/>.</summary>
    public AwkLexer(string source) => _source = source ?? string.Empty;

    /// <summary>Tokenizes the whole program.</summary>
    public List<AwkToken> Tokenize()
    {
        var tokens = new List<AwkToken>();

        while (true)
        {
            var token = Next();
            tokens.Add(token);

            if (token.Kind == AwkTokenKind.End)
            {
                return tokens;
            }
        }
    }

    private char Current => _position < _source.Length ? _source[_position] : '\0';

    private char Peek(int offset = 1) =>
        _position + offset < _source.Length ? _source[_position + offset] : '\0';

    private AwkToken Next()
    {
        SkipBlanks();

        if (_position >= _source.Length)
        {
            return new AwkToken(AwkTokenKind.End, string.Empty);
        }

        var c = Current;

        if (c == '\n')
        {
            _position++;
            _afterValue = false;
            return new AwkToken(AwkTokenKind.Newline, "\n");
        }

        if (c == '"')
        {
            _afterValue = true;
            return new AwkToken(AwkTokenKind.String, ReadString());
        }

        // A `/` after a value divides; otherwise it opens a regex literal.
        if (c == '/' && !_afterValue)
        {
            _afterValue = true;
            return new AwkToken(AwkTokenKind.Regex, ReadRegex());
        }

        if (Chars.IsDigit(c) || (c == '.' && Chars.IsDigit(Peek())))
        {
            _afterValue = true;
            return new AwkToken(AwkTokenKind.Number, ReadNumber());
        }

        if (Chars.IsNameStart(c))
        {
            var name = ReadName();

            if (Keywords.Contains(name))
            {
                // `getline` yields a value; the other keywords do not.
                _afterValue = name == "getline";
                return new AwkToken(AwkTokenKind.Keyword, name);
            }

            _afterValue = true;

            // A name touching `(` is a call, which the parser must not read as
            // concatenation with a parenthesized group.
            return new AwkToken(Current == '(' ? AwkTokenKind.FunctionName : AwkTokenKind.Name, name);
        }

        foreach (var op in Operators)
        {
            if (!Matches(op))
            {
                continue;
            }

            _position += op.Length;

            // Only these can end an expression, so only they make a following `/` division.
            _afterValue = op is ")" or "]" or "++" or "--" or "$";
            return new AwkToken(AwkTokenKind.Operator, op);
        }

        throw new BashkitException(BashkitErrorKind.Parse, $"awk: syntax error at '{c}'");
    }

    private bool Matches(string op) =>
        _position + op.Length <= _source.Length
        && string.CompareOrdinal(_source, _position, op, 0, op.Length) == 0;

    private void SkipBlanks()
    {
        while (_position < _source.Length)
        {
            var c = _source[_position];

            if (c is ' ' or '\t' or '\r')
            {
                _position++;
                continue;
            }

            // A backslash at end of line joins the next line.
            if (c == '\\' && Peek() == '\n')
            {
                _position += 2;
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

        while (_position < _source.Length && Chars.IsNamePart(_source[_position]))
        {
            _position++;
        }

        return _source[start.._position];
    }

    private string ReadNumber()
    {
        var start = _position;

        // Hexadecimal literals are a common extension and cost nothing to accept.
        if (Current == '0' && (Peek() is 'x' or 'X'))
        {
            _position += 2;

            while (_position < _source.Length && Uri.IsHexDigit(_source[_position]))
            {
                _position++;
            }

            return _source[start.._position];
        }

        while (_position < _source.Length && Chars.IsDigit(_source[_position]))
        {
            _position++;
        }

        if (Current == '.')
        {
            _position++;

            while (_position < _source.Length && Chars.IsDigit(_source[_position]))
            {
                _position++;
            }
        }

        if (Current is 'e' or 'E')
        {
            var save = _position;
            _position++;

            if (Current is '+' or '-')
            {
                _position++;
            }

            if (Chars.IsDigit(Current))
            {
                while (_position < _source.Length && Chars.IsDigit(_source[_position]))
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

    private string ReadString()
    {
        _position++;
        var builder = new StringBuilder();

        while (_position < _source.Length && _source[_position] != '"')
        {
            var c = _source[_position];

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

            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '\\': builder.Append('\\'); break;
                case '"': builder.Append('"'); break;
                case '/': builder.Append('/'); break;
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'v': builder.Append('\v'); break;

                case >= '0' and <= '7':
                {
                    var value = escape - '0';
                    var digits = 1;

                    while (digits < 3 && _position < _source.Length && _source[_position] is >= '0' and <= '7')
                    {
                        value = (value * 8) + (_source[_position++] - '0');
                        digits++;
                    }

                    builder.Append((char)value);
                    break;
                }

                // `\uXXXX` and `\xXX` are gawk extensions, and scripts written for an LLM
                // sandbox reach for them constantly.
                case 'u' or 'U' or 'x':
                {
                    var limit = escape switch { 'x' => 2, 'u' => 6, _ => 8 };
                    var start = _position;

                    while (_position - start < limit
                           && _position < _source.Length
                           && Uri.IsHexDigit(_source[_position]))
                    {
                        _position++;
                    }

                    if (_position == start)
                    {
                        builder.Append('\\').Append(escape);
                        break;
                    }

                    var code = Convert.ToInt32(_source[start.._position], 16);
                    builder.Append(code <= 0x10FFFF ? char.ConvertFromUtf32(code) : string.Empty);
                    break;
                }

                default:
                    // An unknown escape keeps the backslash, as POSIX awk does.
                    builder.Append('\\').Append(escape);
                    break;
            }
        }

        if (_position < _source.Length)
        {
            _position++;
        }

        return builder.ToString();
    }

    private string ReadRegex()
    {
        _position++;
        var builder = new StringBuilder();
        var inBracket = false;

        while (_position < _source.Length)
        {
            var c = _source[_position];

            if (c == '\\' && _position + 1 < _source.Length)
            {
                builder.Append(c).Append(_source[_position + 1]);
                _position += 2;
                continue;
            }

            // A `/` inside a bracket expression is literal, not the terminator.
            if (c == '[')
            {
                inBracket = true;
            }
            else if (c == ']')
            {
                inBracket = false;
            }
            else if (c == '/' && !inBracket)
            {
                _position++;
                return builder.ToString();
            }
            else if (c == '\n')
            {
                break;
            }

            builder.Append(c);
            _position++;
        }

        throw new BashkitException(BashkitErrorKind.Parse, "awk: unterminated regex");
    }

    /// <summary>Character predicates, spelled out so the grammar's intent is explicit.</summary>
    private static class Chars
    {
        public static bool IsDigit(char c) => c is >= '0' and <= '9';

        public static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

        public static bool IsNamePart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    }

    /// <summary>Parses an AWK numeric literal, which may be decimal or hexadecimal.</summary>
    public static double ParseNumber(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
        {
            return (double)Convert.ToInt64(text[2..], 16);
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
