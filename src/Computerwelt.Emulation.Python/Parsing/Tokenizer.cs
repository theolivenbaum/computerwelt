using System.Globalization;
using System.Numerics;
using System.Text;

namespace Computerwelt.Emulation.Python.Parsing;

/// <summary>
/// Turns Python source into a token stream.
/// </summary>
/// <remarks>
/// <para>
/// Two things make Python tokenizing unlike most languages, and both are handled here
/// rather than in the parser. First, <b>indentation is syntax</b>: the tokenizer keeps an
/// indent stack and emits <see cref="TokenKind.Indent"/> and
/// <see cref="TokenKind.Dedent"/> so the parser sees ordinary block delimiters. Second,
/// <b>newlines are only sometimes significant</b>: inside brackets they are whitespace,
/// which means the tokenizer must track bracket depth.
/// </para>
/// <para>
/// String literals are decoded here too, because the prefix (<c>r</c>, <c>b</c>, <c>f</c>)
/// changes what the body means, and the prefix is a lexical property.
/// </para>
/// </remarks>
public sealed class Tokenizer
{
    private static readonly string[] Operators =
    [
        // Longest first: maximal munch matters for `**=` against `**` and `*`.
        "**=", "//=", ">>=", "<<=", "...", "!=",
        "**", "//", ">>", "<<", "<=", ">=", "==", "->", ":=",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "@=",
        "+", "-", "*", "/", "%", "@", "&", "|", "^", "~", "<", ">",
        "(", ")", "[", "]", "{", "}", ",", ":", ".", ";", "=",
    ];

    private readonly string _source;
    private readonly List<int> _indents = [0];
    private readonly List<Token> _tokens = [];
    private int _position;
    private int _line = 1;
    private int _lineStart;
    private int _bracketDepth;
    private bool _atLineStart = true;

    /// <summary>Creates a tokenizer over <paramref name="source"/>.</summary>
    public Tokenizer(string source) => _source = source ?? string.Empty;

    /// <summary>Tokenizes the whole source.</summary>
    public List<Token> Tokenize()
    {
        while (true)
        {
            if (_atLineStart && _bracketDepth == 0)
            {
                if (!HandleLineStart())
                {
                    break;
                }

                continue;
            }

            SkipInlineWhitespace();

            if (_position >= _source.Length)
            {
                break;
            }

            var c = _source[_position];

            if (c == '\n')
            {
                _position++;
                if (_bracketDepth == 0)
                {
                    Emit(TokenKind.Newline, "\n", _position - 1, 1);
                    _atLineStart = true;
                }

                _line++;
                _lineStart = _position;
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

            // A backslash at end of line joins the next line explicitly.
            if (c == '\\' && _position + 1 < _source.Length && _source[_position + 1] == '\n')
            {
                _position += 2;
                _line++;
                _lineStart = _position;
                continue;
            }

            if (TryReadStringPrefix(out var prefix))
            {
                ReadString(prefix);
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                ReadName();
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && _position + 1 < _source.Length && char.IsAsciiDigit(_source[_position + 1])))
            {
                ReadNumber();
                continue;
            }

            ReadOperator();
        }

        // Close every open block, then terminate.
        if (_tokens.Count > 0 && _tokens[^1].Kind != TokenKind.Newline)
        {
            Emit(TokenKind.Newline, "\n", _position, 0);
        }

        while (_indents.Count > 1)
        {
            _indents.RemoveAt(_indents.Count - 1);
            Emit(TokenKind.Dedent, string.Empty, _position, 0);
        }

        Emit(TokenKind.EndOfInput, string.Empty, _position, 0);
        return _tokens;
    }

    /// <summary>
    /// Measures a line's indentation and emits the matching INDENT/DEDENT tokens.
    /// Returns false at end of input.
    /// </summary>
    private bool HandleLineStart()
    {
        var indent = 0;

        while (_position < _source.Length)
        {
            var c = _source[_position];

            if (c == ' ')
            {
                indent++;
            }
            else if (c == '\t')
            {
                // A tab advances to the next multiple of eight, as CPython does.
                indent = (indent / 8 + 1) * 8;
            }
            else if (c == '\f')
            {
                indent = 0;
            }
            else
            {
                break;
            }

            _position++;
        }

        if (_position >= _source.Length)
        {
            return false;
        }

        // A blank or comment-only line has no indentation significance at all.
        if (_source[_position] == '\n' || _source[_position] == '#' || _source[_position] == '\r')
        {
            while (_position < _source.Length && _source[_position] != '\n')
            {
                _position++;
            }

            if (_position < _source.Length)
            {
                _position++;
                _line++;
                _lineStart = _position;
            }

            return _position < _source.Length;
        }

        _atLineStart = false;

        if (indent > _indents[^1])
        {
            _indents.Add(indent);
            Emit(TokenKind.Indent, string.Empty, _position, 0);
            return true;
        }

        while (indent < _indents[^1])
        {
            _indents.RemoveAt(_indents.Count - 1);
            Emit(TokenKind.Dedent, string.Empty, _position, 0);
        }

        if (indent != _indents[^1])
        {
            throw new PythonSyntaxError("unindent does not match any outer indentation level", _line, _position - _lineStart);
        }

        return true;
    }

    private void SkipInlineWhitespace()
    {
        while (_position < _source.Length && _source[_position] is ' ' or '\t' or '\r' or '\f')
        {
            _position++;
        }
    }

    private void ReadName()
    {
        var start = _position;

        while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
        {
            _position++;
        }

        var text = _source[start.._position];
        Emit(Keywords.IsReserved(text) ? TokenKind.Keyword : TokenKind.Name, text, start, _position - start);
    }

    private void ReadNumber()
    {
        var start = _position;

        // Radix-prefixed integers.
        if (_source[_position] == '0' && _position + 1 < _source.Length && char.ToLowerInvariant(_source[_position + 1]) is 'x' or 'o' or 'b')
        {
            var radix = char.ToLowerInvariant(_source[_position + 1]) switch
            {
                'x' => 16,
                'o' => 8,
                _ => 2,
            };

            _position += 2;
            var digitsStart = _position;

            while (_position < _source.Length && (Uri.IsHexDigit(_source[_position]) || _source[_position] == '_'))
            {
                _position++;
            }

            var digits = _source[digitsStart.._position].Replace("_", string.Empty, StringComparison.Ordinal);

            if (digits.Length == 0)
            {
                throw new PythonSyntaxError("invalid literal", _line, start - _lineStart);
            }

            // Python integers are unbounded, so a radix literal is accumulated in
            // BigInteger rather than parsed into a machine word.
            var value = BigInteger.Zero;

            foreach (var digit in digits)
            {
                var d = char.IsAsciiDigit(digit) ? digit - '0' : char.ToLowerInvariant(digit) - 'a' + 10;

                if (d < 0 || d >= radix)
                {
                    throw new PythonSyntaxError("invalid literal", _line, start - _lineStart);
                }

                value = (value * radix) + d;
            }

            Emit(TokenKind.Integer, value.ToString(CultureInfo.InvariantCulture), start, _position - start);
            return;
        }

        var isFloat = false;

        while (_position < _source.Length && (char.IsAsciiDigit(_source[_position]) || _source[_position] == '_'))
        {
            _position++;
        }

        if (_position < _source.Length && _source[_position] == '.'
            && !(_position + 1 < _source.Length && _source[_position + 1] == '.'))
        {
            isFloat = true;
            _position++;

            while (_position < _source.Length && (char.IsAsciiDigit(_source[_position]) || _source[_position] == '_'))
            {
                _position++;
            }
        }

        if (_position < _source.Length && char.ToLowerInvariant(_source[_position]) == 'e')
        {
            var save = _position;
            _position++;

            if (_position < _source.Length && _source[_position] is '+' or '-')
            {
                _position++;
            }

            if (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
            {
                isFloat = true;
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

        var text = _source[start.._position].Replace("_", string.Empty, StringComparison.Ordinal);
        Emit(isFloat ? TokenKind.Float : TokenKind.Integer, text, start, _position - start);
    }

    /// <summary>Recognizes a string prefix such as <c>r</c>, <c>b</c>, <c>f</c> or <c>rb</c>.</summary>
    private bool TryReadStringPrefix(out string prefix)
    {
        prefix = string.Empty;
        var probe = _position;
        var letters = new StringBuilder(3);

        while (probe < _source.Length && letters.Length < 3 && char.IsAsciiLetter(_source[probe]))
        {
            letters.Append(char.ToLowerInvariant(_source[probe]));
            probe++;
        }

        if (probe >= _source.Length || _source[probe] is not ('"' or '\''))
        {
            return letters.Length == 0 && _position < _source.Length && _source[_position] is '"' or '\'';
        }

        var text = letters.ToString();

        if (text.Length > 0 && !IsValidPrefix(text))
        {
            return false;
        }

        prefix = text;
        _position = probe;
        return true;
    }

    private static bool IsValidPrefix(string prefix) => prefix switch
    {
        "" or "r" or "b" or "f" or "u" or "rb" or "br" or "rf" or "fr" => true,
        _ => false,
    };

    private void ReadString(string prefix)
    {
        var start = _position - prefix.Length;
        var quote = _source[_position];

        // Triple quotes span lines and permit unescaped quotes inside.
        var triple = _position + 2 < _source.Length
            && _source[_position + 1] == quote
            && _source[_position + 2] == quote;

        _position += triple ? 3 : 1;
        var bodyStart = _position;

        while (true)
        {
            if (_position >= _source.Length)
            {
                throw new PythonSyntaxError(
                    triple ? "unterminated triple-quoted string literal" : "unterminated string literal",
                    _line,
                    start - _lineStart);
            }

            var c = _source[_position];

            if (c == '\\' && _position + 1 < _source.Length)
            {
                if (_source[_position + 1] == '\n')
                {
                    _line++;
                }

                _position += 2;
                continue;
            }

            if (c == '\n')
            {
                if (!triple)
                {
                    throw new PythonSyntaxError("unterminated string literal", _line, start - _lineStart);
                }

                _line++;
                _lineStart = _position + 1;
                _position++;
                continue;
            }

            if (c == quote)
            {
                if (!triple)
                {
                    break;
                }

                if (_position + 2 < _source.Length && _source[_position + 1] == quote && _source[_position + 2] == quote)
                {
                    break;
                }
            }

            _position++;
        }

        var body = _source[bodyStart.._position];
        _position += triple ? 3 : 1;

        var isRaw = prefix.Contains('r', StringComparison.Ordinal);
        var isBytes = prefix.Contains('b', StringComparison.Ordinal);
        var isFormat = prefix.Contains('f', StringComparison.Ordinal);

        // An f-string keeps its raw body: the parser has to find the `{}` fields, and
        // decoding escapes first would corrupt braces that came from escapes.
        var kind = isFormat ? TokenKind.FString : isBytes ? TokenKind.Bytes : TokenKind.String;
        var text = isFormat || isRaw ? body : DecodeEscapes(body, isBytes);

        _tokens.Add(new Token(kind, text, start, _position - start, _line, start - _lineStart) { Prefix = prefix });
    }

    /// <summary>Decodes the escape sequences a non-raw literal may contain.</summary>
    internal static string DecodeEscapes(string body, bool isBytes)
    {
        if (!body.Contains('\\', StringComparison.Ordinal))
        {
            return body;
        }

        var builder = new StringBuilder(body.Length);

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i + 1 >= body.Length)
            {
                builder.Append(body[i]);
                continue;
            }

            var c = body[++i];

            switch (c)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '0': builder.Append('\0'); break;
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'v': builder.Append('\v'); break;
                case '\\': builder.Append('\\'); break;
                case '\'': builder.Append('\''); break;
                case '"': builder.Append('"'); break;
                case '\n': break; // a line continuation inside the literal

                case 'x':
                {
                    var digits = Take(body, i + 1, 2, Uri.IsHexDigit);
                    if (digits.Length == 2)
                    {
                        builder.Append((char)Convert.ToInt32(digits, 16));
                        i += 2;
                    }
                    else
                    {
                        builder.Append("\\x");
                    }

                    break;
                }

                case 'u' when !isBytes:
                {
                    var digits = Take(body, i + 1, 4, Uri.IsHexDigit);
                    if (digits.Length == 4)
                    {
                        builder.Append((char)Convert.ToInt32(digits, 16));
                        i += 4;
                    }
                    else
                    {
                        builder.Append("\\u");
                    }

                    break;
                }

                case 'U' when !isBytes:
                {
                    var digits = Take(body, i + 1, 8, Uri.IsHexDigit);
                    if (digits.Length == 8)
                    {
                        builder.Append(char.ConvertFromUtf32(Convert.ToInt32(digits, 16)));
                        i += 8;
                    }
                    else
                    {
                        builder.Append("\\U");
                    }

                    break;
                }

                case >= '1' and <= '7':
                {
                    var digits = Take(body, i, 3, static ch => ch is >= '0' and <= '7');
                    builder.Append((char)Convert.ToInt32(digits, 8));
                    i += digits.Length - 1;
                    break;
                }

                default:
                    // An unknown escape is left intact, which is what Python does.
                    builder.Append('\\').Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string Take(string text, int start, int max, Func<char, bool> predicate)
    {
        var end = start;
        while (end < text.Length && end - start < max && predicate(text[end]))
        {
            end++;
        }

        return text[start..end];
    }

    private void ReadOperator()
    {
        foreach (var op in Operators)
        {
            if (_position + op.Length > _source.Length
                || !_source.AsSpan(_position, op.Length).SequenceEqual(op))
            {
                continue;
            }

            switch (op)
            {
                case "(" or "[" or "{":
                    _bracketDepth++;
                    break;
                case ")" or "]" or "}":
                    _bracketDepth = Math.Max(0, _bracketDepth - 1);
                    break;
            }

            Emit(TokenKind.Operator, op, _position, op.Length);
            _position += op.Length;
            return;
        }

        throw new PythonSyntaxError($"invalid character '{_source[_position]}'", _line, _position - _lineStart);
    }

    private void Emit(TokenKind kind, string text, int start, int length) =>
        _tokens.Add(new Token(kind, text, start, length, _line, start - _lineStart));
}

/// <summary>A Python <c>SyntaxError</c> raised while tokenizing or parsing.</summary>
public sealed class PythonSyntaxError : Exception
{
    /// <summary>Creates a syntax error at a source position.</summary>
    public PythonSyntaxError(string message, int line, int column) : base(message)
    {
        Line = line;
        Column = column;
    }

    /// <summary>1-based line number.</summary>
    public int Line { get; }

    /// <summary>0-based column.</summary>
    public int Column { get; }
}
