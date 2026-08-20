namespace Computerwelt.Emulation.Bash.Builtins.Awk;

/// <summary>
/// A recursive-descent parser for AWK.
/// </summary>
/// <remarks>
/// <para>
/// Two things make AWK's grammar awkward and both are handled explicitly here.
/// Concatenation has <b>no operator</b> — two adjacent expressions concatenate — so the
/// parser has to decide from the next token alone whether an expression continues, and it
/// binds tighter than comparison but looser than addition.
/// </para>
/// <para>
/// And <c>&gt;</c> is ambiguous inside <c>print</c>: <c>print a &gt; b</c> redirects rather
/// than compares. The parser therefore tracks whether it is inside a print argument list
/// and stops treating <c>&gt;</c> as an operator there.
/// </para>
/// </remarks>
internal sealed class AwkParser
{
    private readonly List<AwkToken> _tokens;
    private readonly Dictionary<string, AwkFunction> _functions = new(StringComparer.Ordinal);
    private int _index;
    private bool _inPrint;

    private AwkParser(List<AwkToken> tokens) => _tokens = tokens;

    /// <summary>Parses a complete AWK program.</summary>
    public static AwkProgram Parse(string source)
    {
        var parser = new AwkParser(new AwkLexer(source).Tokenize());
        return parser.ParseProgram();
    }

    private AwkToken Current => _tokens[Math.Min(_index, _tokens.Count - 1)];

    private AwkToken Peek(int offset = 1) => _tokens[Math.Min(_index + offset, _tokens.Count - 1)];

    private bool AtEnd => Current.Kind == AwkTokenKind.End;

    private AwkToken Advance() => _tokens[_index++];

    private bool Match(string text)
    {
        if (!Current.Is(text))
        {
            return false;
        }

        _index++;
        return true;
    }

    private void Expect(string text)
    {
        if (!Match(text))
        {
            throw Error($"expected '{text}'");
        }
    }

    private BashkitException Error(string message) =>
        new(BashkitErrorKind.Parse, $"awk: syntax error: {message}, got '{Current.Text}'");

    private void SkipNewlines()
    {
        while (Current.Kind == AwkTokenKind.Newline || Current.Is(";"))
        {
            _index++;
        }
    }

    /// <summary>Skips newlines that the grammar allows inside a construct.</summary>
    private void SkipOptionalNewlines()
    {
        while (Current.Kind == AwkTokenKind.Newline)
        {
            _index++;
        }
    }

    private AwkProgram ParseProgram()
    {
        var rules = new List<AwkRule>();
        SkipNewlines();

        while (!AtEnd)
        {
            if (Current.Is("function") || Current.Is("func"))
            {
                ParseFunction();
                SkipNewlines();
                continue;
            }

            rules.Add(ParseRule());
            SkipNewlines();
        }

        return new AwkProgram(rules, _functions);
    }

    private void ParseFunction()
    {
        _index++;
        var name = Advance().Text;

        Expect("(");
        var parameters = new List<string>();

        while (!Current.Is(")") && !AtEnd)
        {
            parameters.Add(Advance().Text);

            if (!Match(","))
            {
                break;
            }

            SkipOptionalNewlines();
        }

        Expect(")");
        SkipOptionalNewlines();

        _functions[name] = new AwkFunction(name, parameters, ParseBlock());
    }

    private AwkRule ParseRule()
    {
        if (Current.Is("BEGIN"))
        {
            _index++;
            SkipOptionalNewlines();
            return new AwkRule { Kind = AwkPatternKind.Begin, Action = ParseBlock() };
        }

        if (Current.Is("END"))
        {
            _index++;
            SkipOptionalNewlines();
            return new AwkRule { Kind = AwkPatternKind.End, Action = ParseBlock() };
        }

        // A bare block with no pattern runs for every record.
        if (Current.Is("{"))
        {
            return new AwkRule { Kind = AwkPatternKind.Always, Action = ParseBlock() };
        }

        var pattern = ParseExpression();

        if (Match(","))
        {
            SkipOptionalNewlines();
            var end = ParseExpression();
            var rangeAction = Current.Is("{") ? ParseBlock() : null;

            return new AwkRule
            {
                Kind = AwkPatternKind.Range,
                Pattern = pattern,
                RangeEnd = end,
                Action = rangeAction,
            };
        }

        // A pattern with no action means `{ print }`.
        var action = Current.Is("{") ? ParseBlock() : null;

        return new AwkRule { Kind = AwkPatternKind.Expression, Pattern = pattern, Action = action };
    }

    private AwkStatement ParseBlock()
    {
        Expect("{");
        var statements = new List<AwkStatement>();
        SkipNewlines();

        while (!Current.Is("}") && !AtEnd)
        {
            statements.Add(ParseStatement());
            SkipNewlines();
        }

        Expect("}");
        return new AwkBlock(statements);
    }

    private AwkStatement ParseStatement()
    {
        if (Current.Is("{"))
        {
            return ParseBlock();
        }

        if (Current.Kind == AwkTokenKind.Keyword)
        {
            switch (Current.Text)
            {
                case "if": return ParseIf();
                case "while": return ParseWhile();
                case "do": return ParseDoWhile();
                case "for": return ParseFor();
                case "print": return ParsePrint(formatted: false);
                case "printf": return ParsePrint(formatted: true);
                case "delete": return ParseDelete();
                case "getline": return ParseGetlineStatement();

                case "next":
                    _index++;
                    return new AwkNext();

                case "nextfile":
                    _index++;
                    return new AwkNextFile();

                case "break":
                    _index++;
                    return new AwkBreak();

                case "continue":
                    _index++;
                    return new AwkContinue();

                case "exit":
                {
                    _index++;
                    var code = IsStatementEnd() ? null : ParseExpression();
                    return new AwkExit(code);
                }

                case "return":
                {
                    _index++;
                    var value = IsStatementEnd() ? null : ParseExpression();
                    return new AwkReturn(value);
                }
            }
        }

        if (Current.Is(";"))
        {
            _index++;
            return new AwkEmpty();
        }

        return new AwkExpressionStatement(ParseExpression());
    }

    private bool IsStatementEnd() =>
        AtEnd || Current.Kind == AwkTokenKind.Newline || Current.Is(";") || Current.Is("}");

    /// <summary>Consumes the terminator after a simple statement, if one is present.</summary>
    private void EndStatement()
    {
        if (Current.Is(";") || Current.Kind == AwkTokenKind.Newline)
        {
            _index++;
        }
    }

    private AwkStatement ParseIf()
    {
        _index++;
        Expect("(");
        var condition = ParseExpression();
        Expect(")");
        SkipOptionalNewlines();

        var then = ParseStatement();

        // The `else` may be separated by newlines and semicolons from the consequent.
        var save = _index;
        SkipNewlines();

        if (!Match("else"))
        {
            _index = save;
            return new AwkIf(condition, then, null);
        }

        SkipOptionalNewlines();
        return new AwkIf(condition, then, ParseStatement());
    }

    private AwkStatement ParseWhile()
    {
        _index++;
        Expect("(");
        var condition = ParseExpression();
        Expect(")");
        SkipOptionalNewlines();

        return new AwkWhile(condition, ParseStatement());
    }

    private AwkStatement ParseDoWhile()
    {
        _index++;
        SkipOptionalNewlines();
        var body = ParseStatement();
        SkipNewlines();

        Expect("while");
        Expect("(");
        var condition = ParseExpression();
        Expect(")");

        return new AwkDoWhile(body, condition);
    }

    private AwkStatement ParseFor()
    {
        _index++;
        Expect("(");

        // `for (k in a)` is distinguishable only by looking two tokens ahead.
        if (Current.Kind == AwkTokenKind.Name && Peek().Is("in"))
        {
            var variable = Advance().Text;
            _index++;
            var arrayName = Advance().Text;
            Expect(")");
            SkipOptionalNewlines();

            return new AwkForIn(variable, arrayName, ParseStatement());
        }

        // A parenthesized `for ((k, j) in a)` is the multi-subscript form.
        AwkStatement? init = Current.Is(";") ? null : new AwkExpressionStatement(ParseExpression());
        Expect(";");
        SkipOptionalNewlines();

        var condition = Current.Is(";") ? null : ParseExpression();
        Expect(";");
        SkipOptionalNewlines();

        AwkStatement? update = Current.Is(")") ? null : new AwkExpressionStatement(ParseExpression());
        Expect(")");
        SkipOptionalNewlines();

        return new AwkFor(init, condition, update, ParseStatement());
    }

    private AwkStatement ParsePrint(bool formatted)
    {
        _index++;
        var arguments = new List<AwkExpression>();

        var wasInPrint = _inPrint;
        _inPrint = true;

        try
        {
            if (!IsStatementEnd() && !IsRedirectStart())
            {
                arguments.Add(ParseExpression());

                while (Match(","))
                {
                    SkipOptionalNewlines();
                    arguments.Add(ParseExpression());
                }
            }
        }
        finally
        {
            _inPrint = wasInPrint;
        }

        // `print (a, b) > "f"` parses the parenthesized list as the arguments.
        if (arguments.Count == 1 && arguments[0] is AwkGroup group && group.Items.Count > 1)
        {
            arguments.Clear();
            arguments.AddRange(group.Items);
        }

        var redirect = ParseRedirect();
        EndStatement();

        return formatted ? new AwkPrintf(arguments, redirect) : new AwkPrint(arguments, redirect);
    }

    private bool IsRedirectStart() => Current.Is(">") || Current.Is(">>") || Current.Is("|");

    private AwkRedirect? ParseRedirect()
    {
        if (Match(">>"))
        {
            return new AwkRedirect(AwkRedirectKind.Append, ParseConcatenation());
        }

        if (Match(">"))
        {
            return new AwkRedirect(AwkRedirectKind.Truncate, ParseConcatenation());
        }

        if (Match("|"))
        {
            return new AwkRedirect(AwkRedirectKind.Pipe, ParseConcatenation());
        }

        return null;
    }

    private AwkStatement ParseDelete()
    {
        _index++;
        var name = Advance().Text;

        if (!Match("["))
        {
            // `delete a` clears the whole array.
            return new AwkDelete(name, null);
        }

        var subscript = new List<AwkExpression> { ParseExpression() };

        while (Match(","))
        {
            subscript.Add(ParseExpression());
        }

        Expect("]");
        return new AwkDelete(name, subscript);
    }

    private AwkStatement ParseGetlineStatement()
    {
        var expression = ParseGetline(null);
        return new AwkExpressionStatement(expression);
    }

    // ---- expressions, by precedence ----

    private AwkExpression ParseExpression() => ParseAssignment();

    private AwkExpression ParseAssignment()
    {
        var left = ParseConditional();

        if (Current.Kind != AwkTokenKind.Operator)
        {
            return left;
        }

        var op = Current.Text;

        if (op is not ("=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=" or "**="))
        {
            return left;
        }

        if (left is not (AwkVariable or AwkField or AwkIndex))
        {
            return left;
        }

        _index++;
        SkipOptionalNewlines();

        return new AwkAssign(left, op == "**=" ? "^=" : op, ParseAssignment());
    }

    private AwkExpression ParseConditional()
    {
        var condition = ParseOr();

        if (!Match("?"))
        {
            return condition;
        }

        SkipOptionalNewlines();
        var then = ParseAssignment();
        Expect(":");
        SkipOptionalNewlines();

        return new AwkConditional(condition, then, ParseAssignment());
    }

    private AwkExpression ParseOr()
    {
        var left = ParseAnd();

        while (Match("||"))
        {
            SkipOptionalNewlines();
            left = new AwkBinary("||", left, ParseAnd());
        }

        return left;
    }

    private AwkExpression ParseAnd()
    {
        var left = ParseIn();

        while (Match("&&"))
        {
            SkipOptionalNewlines();
            left = new AwkBinary("&&", left, ParseIn());
        }

        return left;
    }

    private AwkExpression ParseIn()
    {
        var left = ParseMatch();

        while (Current.Is("in"))
        {
            _index++;
            var arrayName = Advance().Text;

            // `(i, j) in a` tests a multi-dimensional subscript.
            var subscript = left is AwkGroup group ? group.Items : [left];
            left = new AwkIn(subscript, arrayName);
        }

        return left;
    }

    private AwkExpression ParseMatch()
    {
        var left = ParseComparison();

        while (Current.Is("~") || Current.Is("!~"))
        {
            var negated = Advance().Text == "!~";
            left = new AwkMatch(left, ParseComparison(), negated);
        }

        return left;
    }

    private AwkExpression ParseComparison()
    {
        var left = ParseConcatenation();

        // Comparison does not chain in AWK, so this runs at most once.
        if (Current.Kind != AwkTokenKind.Operator)
        {
            return left;
        }

        var op = Current.Text;

        // Inside a `print` list, `>` starts a redirection rather than comparing.
        if (op is ">" or ">>" && _inPrint)
        {
            return left;
        }

        if (op is not ("<" or "<=" or ">" or ">=" or "==" or "!="))
        {
            return left;
        }

        _index++;
        return new AwkBinary(op, left, ParseConcatenation());
    }

    /// <summary>
    /// Parses string concatenation, which AWK writes with no operator at all.
    /// </summary>
    private AwkExpression ParseConcatenation()
    {
        var left = ParseAdditive();

        while (StartsConcatenationOperand())
        {
            left = new AwkConcat(left, ParseAdditive());
        }

        return left;
    }

    /// <summary>
    /// True when the next token could begin another operand, and therefore concatenates.
    /// </summary>
    /// <remarks>
    /// This is the whole difficulty of AWK's grammar: <c>a " " b</c> is concatenation while
    /// <c>a &lt; b</c> is comparison, and only the token kind separates them. A leading
    /// <c>+</c> or <c>-</c> is deliberately excluded — <c>a - b</c> subtracts.
    /// </remarks>
    private bool StartsConcatenationOperand()
    {
        switch (Current.Kind)
        {
            case AwkTokenKind.Number or AwkTokenKind.String or AwkTokenKind.Regex
                or AwkTokenKind.Name or AwkTokenKind.FunctionName:
                return true;

            case AwkTokenKind.Keyword:
                // Only the value-producing keywords can appear inside an expression.
                return Current.Text is "getline";

            case AwkTokenKind.Operator:
                return Current.Text is "$" or "(" or "!" or "++" or "--";

            default:
                return false;
        }
    }

    private AwkExpression ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (Current.Is("+") || Current.Is("-"))
        {
            var op = Advance().Text;
            left = new AwkBinary(op, left, ParseMultiplicative());
        }

        return left;
    }

    private AwkExpression ParseMultiplicative()
    {
        var left = ParseUnary();

        while (Current.Is("*") || Current.Is("/") || Current.Is("%"))
        {
            var op = Advance().Text;
            left = new AwkBinary(op, left, ParseUnary());
        }

        return left;
    }

    private AwkExpression ParseUnary()
    {
        if (Current.Is("!") || Current.Is("-") || Current.Is("+"))
        {
            var op = Advance().Text;
            return new AwkUnary(op, ParseUnary());
        }

        return ParsePower();
    }

    private AwkExpression ParsePower()
    {
        var left = ParsePostfix();

        if (!Current.Is("^") && !Current.Is("**"))
        {
            return left;
        }

        _index++;

        // Exponentiation is right-associative and binds tighter than unary minus on its
        // right, so `2 ^ -1` is valid.
        return new AwkBinary("^", left, ParseUnary());
    }

    private AwkExpression ParsePostfix()
    {
        var value = ParsePrimary();

        while (Current.Is("++") || Current.Is("--"))
        {
            if (value is not (AwkVariable or AwkField or AwkIndex))
            {
                break;
            }

            var delta = Advance().Text == "++" ? 1 : -1;
            value = new AwkIncrement(value, delta, Prefix: false);
        }

        // `cmd | getline` reads from a pipeline.
        while (Current.Is("|") && Peek().Is("getline"))
        {
            _index++;
            value = ParseGetline(value);
        }

        return value;
    }

    private AwkExpression ParsePrimary()
    {
        var token = Current;

        switch (token.Kind)
        {
            case AwkTokenKind.Number:
                _index++;
                return new AwkNumber(AwkLexer.ParseNumber(token.Text));

            case AwkTokenKind.String:
                _index++;
                return new AwkString(token.Text);

            case AwkTokenKind.Regex:
                _index++;
                return new AwkRegex(token.Text);

            case AwkTokenKind.FunctionName:
            {
                _index++;
                Expect("(");
                var arguments = new List<AwkExpression>();

                while (!Current.Is(")") && !AtEnd)
                {
                    arguments.Add(ParseExpression());

                    if (!Match(","))
                    {
                        break;
                    }

                    SkipOptionalNewlines();
                }

                Expect(")");
                return new AwkCall(token.Text, arguments);
            }

            case AwkTokenKind.Name:
            {
                _index++;

                if (!Match("["))
                {
                    return new AwkVariable(token.Text);
                }

                var subscript = new List<AwkExpression> { ParseExpression() };

                while (Match(","))
                {
                    subscript.Add(ParseExpression());
                }

                Expect("]");
                return new AwkIndex(token.Text, subscript);
            }

            case AwkTokenKind.Keyword when token.Text == "getline":
                return ParseGetline(null);
        }

        if (Match("$"))
        {
            return new AwkField(ParsePostfixTarget());
        }

        if (Current.Is("++") || Current.Is("--"))
        {
            var delta = Advance().Text == "++" ? 1 : -1;
            return new AwkIncrement(ParsePostfix(), delta, Prefix: true);
        }

        if (Match("("))
        {
            // Inside parentheses `>` compares again: only a bare `print` argument list
            // reads it as a redirection.
            var wasInPrint = _inPrint;
            _inPrint = false;
            var items = new List<AwkExpression>();

            try
            {
                items.Add(ParseExpression());

                while (Match(","))
                {
                    SkipOptionalNewlines();
                    items.Add(ParseExpression());
                }
            }
            finally
            {
                _inPrint = wasInPrint;
            }

            Expect(")");

            // A single parenthesized expression is just that expression; a list is a group
            // that only `in` and `print` can consume.
            return items.Count == 1 ? items[0] : new AwkGroup(items);
        }

        throw Error("expected an expression");
    }

    /// <summary>
    /// Parses the operand of <c>$</c>, which binds tighter than any binary operator:
    /// <c>$NF-1</c> is <c>($NF) - 1</c>.
    /// </summary>
    private AwkExpression ParsePostfixTarget()
    {
        if (Match("("))
        {
            var inner = ParseExpression();
            Expect(")");
            return inner;
        }

        if (Current.Is("$"))
        {
            _index++;
            return new AwkField(ParsePostfixTarget());
        }

        if (Current.Is("++") || Current.Is("--"))
        {
            var delta = Advance().Text == "++" ? 1 : -1;
            return new AwkIncrement(ParsePostfixTarget(), delta, Prefix: true);
        }

        if (Current.Is("-"))
        {
            _index++;
            return new AwkUnary("-", ParsePostfixTarget());
        }

        var token = Advance();

        return token.Kind switch
        {
            AwkTokenKind.Number => new AwkNumber(AwkLexer.ParseNumber(token.Text)),
            AwkTokenKind.String => new AwkString(token.Text),
            AwkTokenKind.Name when Current.Is("[") => ParseIndexTail(token.Text),
            AwkTokenKind.Name => new AwkVariable(token.Text),
            AwkTokenKind.FunctionName => ParseCallTail(token.Text),
            _ => throw Error("expected a field index"),
        };
    }

    private AwkExpression ParseIndexTail(string name)
    {
        Expect("[");
        var subscript = new List<AwkExpression> { ParseExpression() };

        while (Match(","))
        {
            subscript.Add(ParseExpression());
        }

        Expect("]");
        return new AwkIndex(name, subscript);
    }

    private AwkExpression ParseCallTail(string name)
    {
        Expect("(");
        var arguments = new List<AwkExpression>();

        while (!Current.Is(")") && !AtEnd)
        {
            arguments.Add(ParseExpression());

            if (!Match(","))
            {
                break;
            }
        }

        Expect(")");
        return new AwkCall(name, arguments);
    }

    /// <summary>
    /// Parses <c>getline</c> in all four of its shapes.
    /// </summary>
    /// <param name="pipeSource">The command on the left of <c>|</c>, when there was one.</param>
    private AwkExpression ParseGetline(AwkExpression? pipeSource)
    {
        Expect("getline");

        AwkExpression? target = null;

        // `getline var` and `getline $1` name a destination; anything else reads into $0.
        if (Current.Kind == AwkTokenKind.Name || Current.Is("$"))
        {
            target = Current.Is("$") ? ParsePrimary() : new AwkVariable(Advance().Text);

            if (target is AwkVariable variable && Current.Is("["))
            {
                target = ParseIndexTail(variable.Name);
            }
        }

        if (pipeSource is not null)
        {
            return new AwkGetline(target, pipeSource, FromPipe: true);
        }

        // `getline < "file"` reads from a named file.
        if (Match("<"))
        {
            return new AwkGetline(target, ParseConcatenation(), FromPipe: false);
        }

        return new AwkGetline(target, null, FromPipe: false);
    }
}
