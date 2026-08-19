using System.Text;

namespace Bashkit.Parsing;

/// <summary>
/// Turns script text into a token stream.
/// </summary>
/// <remarks>
/// <para>
/// The lexer keeps words as <b>raw source text with quotes intact</b> and leaves their
/// internal structure to <see cref="WordParser"/>. Shell lexing is context-sensitive —
/// <c>a|b</c> is three tokens but <c>"a|b"</c> and <c>$(a|b)</c> are one — so the lexer's
/// real job is deciding where a word ends, which means tracking quote state and nesting
/// depth for <c>$(...)</c>, <c>${...}</c>, <c>$((...))</c>, backticks and process
/// substitution.
/// </para>
/// <para>
/// Here-documents are collected here too: the body starts on the line after the operator,
/// not where the operator appears, so only the lexer is in a position to gather it.
/// </para>
/// </remarks>
public sealed class Lexer
{
    private readonly string _input;
    private readonly ExecutionBudget? _budget;
    private readonly List<PendingHereDocument> _pendingHereDocuments = [];
    private int _position;

    /// <summary>Creates a lexer over <paramref name="input"/>.</summary>
    public Lexer(string input, ExecutionBudget? budget = null)
    {
        _input = input ?? string.Empty;
        _budget = budget;
    }

    /// <summary>Collected here-document bodies, keyed by the order the operators appeared.</summary>
    public IReadOnlyList<string> HereDocumentBodies => _hereDocumentBodies;

    private readonly List<string> _hereDocumentBodies = [];

    /// <summary>Tokenizes the whole input.</summary>
    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfInput)
            {
                return tokens;
            }
        }
    }

    private char Current => _position < _input.Length ? _input[_position] : '\0';

    private char Peek(int offset = 1) =>
        _position + offset < _input.Length ? _input[_position + offset] : '\0';

    private bool AtEnd => _position >= _input.Length;

    private Token NextToken()
    {
        _budget?.ChargeParserFuel();
        SkipBlanksAndComments();

        if (AtEnd)
        {
            return new Token(TokenKind.EndOfInput, string.Empty, _position, 0);
        }

        var start = _position;
        var c = Current;

        if (c == '\n')
        {
            _position++;
            CollectPendingHereDocuments();
            return new Token(TokenKind.Newline, "\n", start, 1);
        }

        if (TryLexOperator(out var op))
        {
            return op;
        }

        return LexWord();
    }

    private void SkipBlanksAndComments()
    {
        while (!AtEnd)
        {
            var c = Current;

            if (c is ' ' or '\t' or '\r')
            {
                _position++;
                continue;
            }

            // A backslash-newline is a line continuation and vanishes entirely.
            if (c == '\\' && Peek() == '\n')
            {
                _position += 2;
                continue;
            }

            if (c == '#' && IsCommentStart())
            {
                while (!AtEnd && Current != '\n')
                {
                    _position++;
                }

                continue;
            }

            return;
        }
    }

    // `#` only starts a comment at the beginning of a word. `echo a#b` keeps the hash.
    private bool IsCommentStart()
    {
        if (_position == 0)
        {
            return true;
        }

        var prev = _input[_position - 1];
        return prev is ' ' or '\t' or '\n' or ';' or '(' or ')' or '&' or '|' or '\r';
    }

    private bool TryLexOperator(out Token token)
    {
        var start = _position;
        var c = Current;
        var next = Peek();
        var third = Peek(2);

        switch (c)
        {
            case ';' when next == ';' && third == '&':
                return Emit(TokenKind.DoubleSemiAmpersand, 3, out token);
            case ';' when next == ';':
                return Emit(TokenKind.DoubleSemicolon, 2, out token);
            case ';' when next == '&':
                return Emit(TokenKind.SemiAmpersand, 2, out token);
            case ';':
                return Emit(TokenKind.Semicolon, 1, out token);

            case '&' when next == '&':
                return Emit(TokenKind.AndIf, 2, out token);
            case '&' when next == '>' && third == '>':
                return Emit(TokenKind.AmpersandDoubleGreat, 3, out token);
            case '&' when next == '>':
                return Emit(TokenKind.AmpersandGreat, 2, out token);
            case '&':
                return Emit(TokenKind.Ampersand, 1, out token);

            case '|' when next == '|':
                return Emit(TokenKind.OrIf, 2, out token);
            case '|' when next == '&':
                return Emit(TokenKind.PipeAmpersand, 2, out token);
            case '|':
                return Emit(TokenKind.Pipe, 1, out token);

            case '>' when next == '>':
                return Emit(TokenKind.DoubleGreat, 2, out token);
            case '>' when next == '&':
                return Emit(TokenKind.GreatAmpersand, 2, out token);
            case '>' when next == '|':
                return Emit(TokenKind.Clobber, 2, out token);
            case '>':
                return Emit(TokenKind.Great, 1, out token);

            case '<' when next == '<' && third == '<':
                return Emit(TokenKind.TripleLess, 3, out token);
            case '<' when next == '<' && third == '-':
                return EmitHereDocumentOperator(TokenKind.DoubleLessDash, 3, out token);
            case '<' when next == '<':
                return EmitHereDocumentOperator(TokenKind.DoubleLess, 2, out token);
            case '<' when next == '&':
                return Emit(TokenKind.LessAmpersand, 2, out token);
            case '<' when next == '>':
                return Emit(TokenKind.LessGreat, 2, out token);
            case '<' when next == '(':
                token = default;
                return false; // process substitution is part of a word
            case '<':
                return Emit(TokenKind.Less, 1, out token);

            case '(' when next == '(':
                return Emit(TokenKind.DoubleLeftParen, 2, out token);
            case '(':
                return Emit(TokenKind.LeftParen, 1, out token);
            case ')':
                return Emit(TokenKind.RightParen, 1, out token);
        }

        token = default;
        _ = start;
        return false;
    }

    private bool Emit(TokenKind kind, int length, out Token token)
    {
        token = new Token(kind, _input.Substring(_position, length), _position, length);
        _position += length;
        return true;
    }

    private bool EmitHereDocumentOperator(TokenKind kind, int length, out Token token)
    {
        Emit(kind, length, out token);
        _pendingHereDocuments.Add(new PendingHereDocument(
            Index: _hereDocumentBodies.Count,
            StripTabs: kind == TokenKind.DoubleLessDash));
        _hereDocumentBodies.Add(string.Empty);
        return true;
    }

    /// <summary>
    /// Reads one word, stopping at the first unquoted metacharacter. Nesting constructs are
    /// consumed wholesale so their contents never terminate the word.
    /// </summary>
    private Token LexWord()
    {
        var start = _position;
        var builder = new StringBuilder();

        while (!AtEnd)
        {
            _budget?.ChargeParserFuel();
            var c = Current;

            if (c is ' ' or '\t' or '\n' or '\r' or ';' or '&' or '|' or '<' or '>' or '(' or ')')
            {
                // `<(` and `>(` are word content, not redirections.
                if ((c is '<' or '>') && Peek() == '(')
                {
                    builder.Append(c);
                    builder.Append('(');
                    _position += 2;
                    ConsumeBalanced(builder, '(', ')');
                    continue;
                }

                // An array literal belongs to the assignment word: `a=(1 2 3)` is one
                // token, even though a bare `(` would open a subshell.
                if (c == '(' && EndsWithAssignmentPrefix(builder))
                {
                    builder.Append('(');
                    _position++;
                    ConsumeBalanced(builder, '(', ')');
                    continue;
                }

                break;
            }

            switch (c)
            {
                case '\\':
                    if (Peek() == '\n')
                    {
                        _position += 2;
                        continue;
                    }

                    builder.Append(c);
                    _position++;
                    if (!AtEnd)
                    {
                        builder.Append(Current);
                        _position++;
                    }

                    continue;

                case '\'':
                    ConsumeSingleQuoted(builder);
                    continue;

                case '"':
                    ConsumeDoubleQuoted(builder);
                    continue;

                case '`':
                    ConsumeBackticks(builder);
                    continue;

                case '$':
                    ConsumeDollar(builder);
                    continue;

                default:
                    builder.Append(c);
                    _position++;
                    continue;
            }
        }

        var text = builder.ToString();
        var length = _position - start;

        // A run of digits immediately before a redirection operator names a descriptor.
        if (text.Length > 0 && text.All(char.IsAsciiDigit) && Current is '<' or '>')
        {
            return new Token(TokenKind.IoNumber, text, start, length);
        }

        // A here-document delimiter must be captured before the body is collected.
        if (_pendingHereDocuments.Count > 0)
        {
            for (var i = 0; i < _pendingHereDocuments.Count; i++)
            {
                if (_pendingHereDocuments[i].Delimiter is null)
                {
                    _pendingHereDocuments[i] = _pendingHereDocuments[i] with { Delimiter = text };
                    break;
                }
            }
        }

        return new Token(TokenKind.Word, text, start, length);
    }

    /// <summary>
    /// True when the word so far looks like <c>NAME=</c>, <c>NAME+=</c> or
    /// <c>NAME[idx]=</c> — the only positions where a following <c>(</c> opens an array
    /// literal rather than a subshell.
    /// </summary>
    private static bool EndsWithAssignmentPrefix(StringBuilder builder)
    {
        if (builder.Length < 2 || builder[^1] != '=')
        {
            return false;
        }

        var text = builder.ToString();
        var end = text.Length - 1;

        if (end > 0 && text[end - 1] == '+')
        {
            end--;
        }

        if (end > 0 && text[end - 1] == ']')
        {
            var open = text.LastIndexOf('[', end - 1);
            if (open < 0)
            {
                return false;
            }

            end = open;
        }

        if (end == 0)
        {
            return false;
        }

        if (!char.IsAsciiLetter(text[0]) && text[0] != '_')
        {
            return false;
        }

        for (var i = 1; i < end; i++)
        {
            if (!char.IsAsciiLetterOrDigit(text[i]) && text[i] != '_')
            {
                return false;
            }
        }

        return true;
    }

    private void ConsumeSingleQuoted(StringBuilder builder)
    {
        builder.Append('\'');
        _position++;

        while (!AtEnd && Current != '\'')
        {
            builder.Append(Current);
            _position++;
        }

        if (AtEnd)
        {
            throw new ParseException("unexpected EOF while looking for matching `''", _position);
        }

        builder.Append('\'');
        _position++;
    }

    private void ConsumeDoubleQuoted(StringBuilder builder)
    {
        builder.Append('"');
        _position++;

        while (!AtEnd && Current != '"')
        {
            _budget?.ChargeParserFuel();
            var c = Current;

            if (c == '\\')
            {
                builder.Append(c);
                _position++;
                if (!AtEnd)
                {
                    builder.Append(Current);
                    _position++;
                }

                continue;
            }

            if (c == '$')
            {
                ConsumeDollar(builder);
                continue;
            }

            if (c == '`')
            {
                ConsumeBackticks(builder);
                continue;
            }

            builder.Append(c);
            _position++;
        }

        if (AtEnd)
        {
            throw new ParseException("unexpected EOF while looking for matching `\"'", _position);
        }

        builder.Append('"');
        _position++;
    }

    private void ConsumeBackticks(StringBuilder builder)
    {
        builder.Append('`');
        _position++;

        while (!AtEnd && Current != '`')
        {
            if (Current == '\\' && _position + 1 < _input.Length)
            {
                builder.Append(Current);
                _position++;
            }

            builder.Append(Current);
            _position++;
        }

        if (AtEnd)
        {
            throw new ParseException("unexpected EOF while looking for matching '`'", _position);
        }

        builder.Append('`');
        _position++;
    }

    private void ConsumeDollar(StringBuilder builder)
    {
        builder.Append('$');
        _position++;

        if (AtEnd)
        {
            return;
        }

        switch (Current)
        {
            case '(' when Peek() == '(':
                builder.Append("((");
                _position += 2;
                ConsumeArithmetic(builder);
                return;

            case '(':
                builder.Append('(');
                _position++;
                ConsumeBalanced(builder, '(', ')');
                return;

            case '{':
                builder.Append('{');
                _position++;
                ConsumeBraceExpansion(builder);
                return;

            case '\'':
                // $'...' — ANSI-C quoting. Escapes are decoded by the word parser.
                ConsumeSingleQuotedAnsiC(builder);
                return;

            case '"':
                // $"..." — locale translation, which for a sandbox is the identity.
                ConsumeDoubleQuoted(builder);
                return;

            default:
                return;
        }
    }

    private void ConsumeSingleQuotedAnsiC(StringBuilder builder)
    {
        builder.Append('\'');
        _position++;

        while (!AtEnd && Current != '\'')
        {
            if (Current == '\\' && _position + 1 < _input.Length)
            {
                builder.Append(Current);
                _position++;
            }

            builder.Append(Current);
            _position++;
        }

        if (AtEnd)
        {
            throw new ParseException("unexpected EOF while looking for matching `''", _position);
        }

        builder.Append('\'');
        _position++;
    }

    private void ConsumeArithmetic(StringBuilder builder)
    {
        var depth = 1;
        while (!AtEnd)
        {
            _budget?.ChargeParserFuel();
            var c = Current;

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    // The closing `))` counts as one level; the outer one is consumed next.
                    if (Peek() == ')')
                    {
                        builder.Append("))");
                        _position += 2;
                        return;
                    }

                    builder.Append(')');
                    _position++;
                    return;
                }
            }

            builder.Append(c);
            _position++;
        }

        throw new ParseException("unexpected EOF in arithmetic expansion", _position);
    }

    private void ConsumeBraceExpansion(StringBuilder builder)
    {
        var depth = 1;
        while (!AtEnd)
        {
            _budget?.ChargeParserFuel();
            var c = Current;

            switch (c)
            {
                case '\'':
                    ConsumeSingleQuoted(builder);
                    continue;

                case '"':
                    ConsumeDoubleQuoted(builder);
                    continue;

                case '\\':
                    builder.Append(c);
                    _position++;
                    if (!AtEnd)
                    {
                        builder.Append(Current);
                        _position++;
                    }

                    continue;

                case '{':
                    depth++;
                    break;

                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        builder.Append('}');
                        _position++;
                        return;
                    }

                    break;
            }

            builder.Append(c);
            _position++;
        }

        throw new ParseException("unexpected EOF while looking for matching `}'", _position);
    }

    /// <summary>Consumes a nested construct, respecting quotes, up to its matching close.</summary>
    private void ConsumeBalanced(StringBuilder builder, char open, char close)
    {
        var depth = 1;
        while (!AtEnd)
        {
            _budget?.ChargeParserFuel();
            var c = Current;

            switch (c)
            {
                case '\'':
                    ConsumeSingleQuoted(builder);
                    continue;

                case '"':
                    ConsumeDoubleQuoted(builder);
                    continue;

                case '\\':
                    builder.Append(c);
                    _position++;
                    if (!AtEnd)
                    {
                        builder.Append(Current);
                        _position++;
                    }

                    continue;

                case '#':
                    // A comment inside a substitution runs to end of line.
                    while (!AtEnd && Current != '\n')
                    {
                        builder.Append(Current);
                        _position++;
                    }

                    continue;
            }

            if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    builder.Append(close);
                    _position++;
                    return;
                }
            }

            builder.Append(c);
            _position++;
        }

        throw new ParseException($"unexpected EOF while looking for matching `{close}'", _position);
    }

    /// <summary>
    /// Reads the bodies of any here-documents whose operators appeared on the line just
    /// ended, consuming input up to and including each terminator line.
    /// </summary>
    private void CollectPendingHereDocuments()
    {
        if (_pendingHereDocuments.Count == 0)
        {
            return;
        }

        foreach (var pending in _pendingHereDocuments)
        {
            var rawDelimiter = pending.Delimiter ?? string.Empty;
            var delimiter = StripQuotes(rawDelimiter);
            var body = new StringBuilder();

            while (!AtEnd)
            {
                var lineEnd = _input.IndexOf('\n', _position);
                var line = lineEnd < 0 ? _input[_position..] : _input[_position..lineEnd];
                _position = lineEnd < 0 ? _input.Length : lineEnd + 1;

                var compared = pending.StripTabs ? line.TrimStart('\t') : line;
                if (compared == delimiter)
                {
                    break;
                }

                body.Append(compared);
                body.Append('\n');
            }

            _hereDocumentBodies[pending.Index] = body.ToString();
        }

        _pendingHereDocuments.Clear();
    }

    /// <summary>
    /// True when a here-document delimiter was quoted, which suppresses expansion of the
    /// body — <c>&lt;&lt;'EOF'</c> is literal, <c>&lt;&lt;EOF</c> is not.
    /// </summary>
    public static bool IsQuotedDelimiter(string delimiter) =>
        delimiter.Contains('\'', StringComparison.Ordinal)
        || delimiter.Contains('"', StringComparison.Ordinal)
        || delimiter.Contains('\\', StringComparison.Ordinal);

    private static string StripQuotes(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\'' or '"')
            {
                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                builder.Append(text[++i]);
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private readonly record struct PendingHereDocument(int Index, bool StripTabs, string? Delimiter = null);
}
