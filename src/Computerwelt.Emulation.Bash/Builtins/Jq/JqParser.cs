namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// A recursive-descent parser for the jq language.
/// </summary>
/// <remarks>
/// <para>
/// The precedence ladder mirrors jq's own grammar exactly, because the surprises live
/// there: <c>|</c> is the loosest operator, <c>,</c> binds tighter than it, and <c>//</c>
/// binds <i>looser</i> than assignment, so <c>a // b = c</c> means <c>a // (b = c)</c>.
/// </para>
/// <para>
/// <c>as</c>, <c>reduce</c> and <c>foreach</c> take a <i>term</i> on the left but a full
/// expression on the right, so they are handled where terms are parsed and then swallow the
/// rest of the pipeline.
/// </para>
/// </remarks>
internal sealed class JqParser
{
    private static readonly HashSet<string> AssignOperators = new(StringComparer.Ordinal)
    {
        "=", "|=", "+=", "-=", "*=", "/=", "%=", "//=",
    };

    private readonly List<JqToken> _tokens;
    private int _index;

    private JqParser(List<JqToken> tokens) => _tokens = tokens;

    /// <summary>Parses a complete filter.</summary>
    public static JqNode Parse(string source)
    {
        var parser = new JqParser(new JqLexer(source).Tokenize());
        var node = parser.ParseExpression();

        if (!parser.AtEnd)
        {
            throw parser.Error("unexpected token");
        }

        return node;
    }

    private JqToken Current => _tokens[Math.Min(_index, _tokens.Count - 1)];

    private bool AtEnd => Current.Kind == JqTokenKind.End;

    private JqToken Advance() => _tokens[_index++];

    private bool Match(string text)
    {
        if (!Current.Is(text))
        {
            return false;
        }

        _index++;
        return true;
    }

    private bool MatchWord(string text)
    {
        if (!Current.IsWord(text))
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

    private void ExpectWord(string text)
    {
        if (!MatchWord(text))
        {
            throw Error($"expected '{text}'");
        }
    }

    private JqException Error(string message) =>
        new($"jq: error: syntax error, {message} (got '{Current.Text}')");

    // ---- precedence ladder ----

    private JqNode ParseExpression()
    {
        if (Current.IsWord("def"))
        {
            return ParseFuncDef();
        }

        if (Current.IsWord("label"))
        {
            _index++;
            var name = Advance().Text;
            Expect("|");
            return new JqLabel(name, ParseExpression());
        }

        var left = ParseComma();

        if (!Match("|"))
        {
            return left;
        }

        return new JqPipe(left, ParseExpression());
    }

    private JqNode ParseFuncDef()
    {
        ExpectWord("def");
        var name = Advance().Text;
        var parameters = new List<string>();

        if (Match("("))
        {
            while (true)
            {
                var token = Advance();
                parameters.Add(token.Kind == JqTokenKind.Variable ? "$" + token.Text : token.Text);

                if (Match(";"))
                {
                    continue;
                }

                Expect(")");
                break;
            }
        }

        Expect(":");
        var body = ParseExpression();
        Expect(";");

        // Everything after the semicolon is the scope the definition covers.
        return new JqFuncDef(name, parameters, body, ParseExpression());
    }

    private JqNode ParseComma()
    {
        var left = ParseAlternative();

        while (Match(","))
        {
            left = new JqComma(left, ParseAlternative());
        }

        return left;
    }

    private JqNode ParseAlternative()
    {
        var left = ParseAssignment();

        // `//` is right-associative and looser than assignment.
        return Match("//") ? new JqBinary("//", left, ParseAlternative()) : left;
    }

    private JqNode ParseAssignment()
    {
        var left = ParseOr();

        if (Current.Kind != JqTokenKind.Operator || !AssignOperators.Contains(Current.Text))
        {
            return left;
        }

        var op = Advance().Text;
        return new JqAssign(op, left, ParseAlternative());
    }

    private JqNode ParseOr()
    {
        var left = ParseAnd();

        while (MatchWord("or"))
        {
            left = new JqBinary("or", left, ParseAnd());
        }

        return left;
    }

    private JqNode ParseAnd()
    {
        var left = ParseComparison();

        while (MatchWord("and"))
        {
            left = new JqBinary("and", left, ParseComparison());
        }

        return left;
    }

    private JqNode ParseComparison()
    {
        var left = ParseAdditive();

        // Comparison does not chain in jq, so this runs at most once.
        if (Current.Kind != JqTokenKind.Operator || Current.Text is not ("==" or "!=" or "<" or "<=" or ">" or ">="))
        {
            return left;
        }

        var op = Advance().Text;
        return new JqBinary(op, left, ParseAdditive());
    }

    private JqNode ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (Current.Is("+") || Current.Is("-"))
        {
            var op = Advance().Text;
            left = new JqBinary(op, left, ParseMultiplicative());
        }

        return left;
    }

    private JqNode ParseMultiplicative()
    {
        var left = ParseUnary();

        while (Current.Is("*") || Current.Is("/") || Current.Is("%"))
        {
            var op = Advance().Text;
            left = new JqBinary(op, left, ParseUnary());
        }

        return left;
    }

    private JqNode ParseUnary()
    {
        if (Match("-"))
        {
            return new JqNegate(ParseUnary());
        }

        return ParseTerm();
    }

    /// <summary>
    /// Parses a term and everything that may follow it: index and iterate suffixes, an
    /// optional <c>?</c>, and the <c>as</c> binding that turns the term into a generator for
    /// the rest of the pipeline.
    /// </summary>
    private JqNode ParseTerm()
    {
        var node = ParsePostfix(ParsePrimary());

        if (!Current.IsWord("as"))
        {
            return node;
        }

        _index++;
        var patterns = new List<JqPattern> { ParsePattern() };

        while (Match("?//"))
        {
            patterns.Add(ParsePattern());
        }

        Expect("|");
        return new JqBind(node, patterns, ParseExpression());
    }

    private JqNode ParsePostfix(JqNode node)
    {
        while (true)
        {
            if (Current.Kind == JqTokenKind.Field)
            {
                node = new JqIndex(node, new JqLiteral(new JsonString(Advance().Text)), Optional: false);
                continue;
            }

            // `.` before a bracket only separates it from the previous suffix: `.a.[0]`.
            if (Current.Is(".") && _tokens[Math.Min(_index + 1, _tokens.Count - 1)].Is("["))
            {
                _index++;
                continue;
            }

            if (Current.Is("[") )
            {
                node = ParseBracketSuffix(node);
                continue;
            }

            // A `.` immediately followed by a string is a quoted field name.
            if (Current.Is(".") && _tokens[Math.Min(_index + 1, _tokens.Count - 1)].Kind == JqTokenKind.String)
            {
                _index++;
                node = new JqIndex(node, ParseStringToken(Advance()), Optional: false);
                continue;
            }

            if (Match("?"))
            {
                // On an index or an iteration `?` is the suffix's own flag, which keeps the
                // whole thing usable as a path expression; anywhere else it is `try`.
                node = node switch
                {
                    JqIndex index => index with { Optional = true },
                    JqIterate iterate => iterate with { Optional = true },
                    JqSlice slice => slice with { Optional = true },
                    _ => new JqTry(node, null),
                };

                continue;
            }

            return node;
        }
    }

    private JqNode ParseBracketSuffix(JqNode target)
    {
        Expect("[");

        if (Match("]"))
        {
            return new JqIterate(target, Optional: false);
        }

        if (Match(":"))
        {
            var upper = ParseExpression();
            Expect("]");
            return new JqSlice(target, null, upper, Optional: false);
        }

        var index = ParseExpression();

        if (Match(":"))
        {
            if (Match("]"))
            {
                return new JqSlice(target, index, null, Optional: false);
            }

            var upper = ParseExpression();
            Expect("]");
            return new JqSlice(target, index, upper, Optional: false);
        }

        Expect("]");
        return new JqIndex(target, index, Optional: false);
    }

    private JqNode ParsePrimary()
    {
        var token = Current;

        switch (token.Kind)
        {
            case JqTokenKind.Number:
                _index++;
                return new JqLiteral(new JsonNumber(JqLexer.ParseNumber(token.Text)));

            case JqTokenKind.String:
                _index++;
                return ParseStringToken(token);

            case JqTokenKind.Field:
                _index++;
                return new JqIndex(new JqIdentity(), new JqLiteral(new JsonString(token.Text)), Optional: false);

            case JqTokenKind.Variable:
                _index++;
                return new JqVariable(token.Text);

            case JqTokenKind.Format:
            {
                _index++;

                // `@base64 "..."` applies the format to the string's interpolations.
                if (Current.Kind == JqTokenKind.String)
                {
                    var literal = ParseStringToken(Advance(), token.Text);
                    return literal;
                }

                return new JqFormat(token.Text);
            }

            case JqTokenKind.Identifier:
                return ParseIdentifier();
        }

        if (Match(".."))
        {
            return new JqRecurseDefault();
        }

        if (Match("."))
        {
            return new JqIdentity();
        }

        if (Match("("))
        {
            var inner = ParseExpression();
            Expect(")");
            return inner;
        }

        if (Match("["))
        {
            if (Match("]"))
            {
                return new JqArrayCons(null);
            }

            var body = ParseExpression();
            Expect("]");
            return new JqArrayCons(body);
        }

        if (Current.Is("{"))
        {
            return ParseObjectConstruction();
        }

        throw Error("expected a filter");
    }

    private JqNode ParseIdentifier()
    {
        var name = Current.Text;

        switch (name)
        {
            case "true":
                _index++;
                return new JqLiteral(JsonBool.True);

            case "false":
                _index++;
                return new JqLiteral(JsonBool.False);

            case "null":
                _index++;
                return new JqLiteral(JsonNull.Instance);

            case "if":
                return ParseIf();

            case "try":
            {
                _index++;
                var body = ParsePostfix(ParsePrimary());
                return MatchWord("catch") ? new JqTry(body, ParsePostfix(ParsePrimary())) : new JqTry(body, null);
            }

            case "reduce":
            {
                _index++;
                var source = ParsePostfix(ParsePrimary());
                ExpectWord("as");
                var pattern = ParsePattern();
                Expect("(");
                var init = ParseExpression();
                Expect(";");
                var update = ParseExpression();
                Expect(")");
                return new JqReduce(source, pattern, init, update);
            }

            case "foreach":
            {
                _index++;
                var source = ParsePostfix(ParsePrimary());
                ExpectWord("as");
                var pattern = ParsePattern();
                Expect("(");
                var init = ParseExpression();
                Expect(";");
                var update = ParseExpression();
                JqNode? extract = null;

                if (Match(";"))
                {
                    extract = ParseExpression();
                }

                Expect(")");
                return new JqForeach(source, pattern, init, update, extract);
            }

            case "def":
                return ParseFuncDef();

            case "break":
            {
                _index++;
                return new JqBreak(Advance().Text);
            }
        }

        _index++;
        var arguments = new List<JqNode>();

        if (Match("("))
        {
            while (true)
            {
                arguments.Add(ParseExpression());

                if (Match(";"))
                {
                    continue;
                }

                Expect(")");
                break;
            }
        }

        return new JqCall(name, arguments);
    }

    private JqNode ParseIf()
    {
        ExpectWord("if");
        var condition = ParseExpression();
        ExpectWord("then");
        var then = ParseExpression();

        if (MatchWord("elif"))
        {
            // An `elif` chain is just a nested `if` that shares the closing `end`.
            _index--;
            _tokens[_index] = new JqToken(JqTokenKind.Identifier, "if");
            return new JqIf(condition, then, ParseIf());
        }

        if (MatchWord("else"))
        {
            var otherwise = ParseExpression();
            ExpectWord("end");
            return new JqIf(condition, then, otherwise);
        }

        ExpectWord("end");
        return new JqIf(condition, then, null);
    }

    private JqNode ParseObjectConstruction()
    {
        Expect("{");
        var entries = new List<JqObjectEntry>();

        if (Match("}"))
        {
            return new JqObjectCons(entries);
        }

        while (true)
        {
            entries.Add(ParseObjectEntry());

            if (Match(","))
            {
                // A trailing comma before `}` is accepted, as jq accepts it.
                if (Match("}"))
                {
                    return new JqObjectCons(entries);
                }

                continue;
            }

            Expect("}");
            return new JqObjectCons(entries);
        }
    }

    private JqObjectEntry ParseObjectEntry()
    {
        var token = Current;

        switch (token.Kind)
        {
            case JqTokenKind.Variable:
            {
                _index++;

                // `{$x}` is shorthand for `{x: $x}`.
                return new JqObjectEntry(
                    new JqLiteral(new JsonString(token.Text)),
                    new JqVariable(token.Text));
            }

            case JqTokenKind.Identifier:
            {
                _index++;
                var key = new JqLiteral(new JsonString(token.Text));

                if (!Match(":"))
                {
                    // `{a}` is shorthand for `{a: .a}`.
                    return new JqObjectEntry(key, new JqIndex(new JqIdentity(), key, Optional: false));
                }

                return new JqObjectEntry(key, ParseObjectValue());
            }

            case JqTokenKind.String:
            {
                _index++;
                var key = ParseStringToken(token);

                if (!Match(":"))
                {
                    return new JqObjectEntry(key, new JqIndex(new JqIdentity(), key, Optional: false));
                }

                return new JqObjectEntry(key, ParseObjectValue());
            }

            case JqTokenKind.Format:
            {
                _index++;

                if (Current.Kind == JqTokenKind.String)
                {
                    var key = ParseStringToken(Advance(), token.Text);
                    Expect(":");
                    return new JqObjectEntry(key, ParseObjectValue());
                }

                throw Error("expected a string after a format in an object key");
            }
        }

        if (Match("("))
        {
            var key = ParseExpression();
            Expect(")");
            Expect(":");
            return new JqObjectEntry(key, ParseObjectValue());
        }

        throw Error("expected an object key");
    }

    /// <summary>
    /// Parses an object entry's value, which stops at <c>,</c> — inside <c>{}</c> a comma
    /// separates entries rather than concatenating streams.
    /// </summary>
    private JqNode ParseObjectValue()
    {
        var left = ParseAlternative();

        while (Match("|"))
        {
            left = new JqPipe(left, ParseAlternative());
        }

        return left;
    }

    private JqPattern ParsePattern()
    {
        var token = Current;

        if (token.Kind == JqTokenKind.Variable)
        {
            _index++;
            return new JqVarPattern(token.Text);
        }

        if (Match("["))
        {
            var elements = new List<JqPattern>();

            while (!Current.Is("]"))
            {
                elements.Add(ParsePattern());

                if (!Match(","))
                {
                    break;
                }
            }

            Expect("]");
            return new JqArrayPattern(elements);
        }

        if (Match("{"))
        {
            var entries = new List<(JqNode, JqPattern)>();

            while (!Current.Is("}"))
            {
                entries.Add(ParseObjectPatternEntry());

                if (!Match(","))
                {
                    break;
                }
            }

            Expect("}");
            return new JqObjectPattern(entries);
        }

        throw Error("expected a destructuring pattern");
    }

    private (JqNode Key, JqPattern Value) ParseObjectPatternEntry()
    {
        var token = Current;

        if (token.Kind == JqTokenKind.Variable && !_tokens[Math.Min(_index + 1, _tokens.Count - 1)].Is(":"))
        {
            // `{$x}` binds `$x` to `.x`.
            _index++;
            return (new JqLiteral(new JsonString(token.Text)), new JqVarPattern(token.Text));
        }

        JqNode key;

        switch (token.Kind)
        {
            case JqTokenKind.Identifier or JqTokenKind.Variable:
                _index++;
                key = new JqLiteral(new JsonString(token.Text));
                break;

            case JqTokenKind.String:
                _index++;
                key = ParseStringToken(token);
                break;

            default:
                Expect("(");
                key = ParseExpression();
                Expect(")");
                break;
        }

        Expect(":");
        return (key, ParsePattern());
    }

    /// <summary>
    /// Turns a string token into a node: a plain literal when it has no interpolations, and
    /// an interpolation node otherwise.
    /// </summary>
    private static JqNode ParseStringToken(JqToken token, string? format = null)
    {
        var parts = token.Parts ?? [string.Empty];

        if (format is null && parts.Count == 1 && parts[0] is string only)
        {
            return new JqLiteral(new JsonString(only));
        }

        var compiled = new List<object>(parts.Count);

        foreach (var part in parts)
        {
            compiled.Add(part switch
            {
                string text => text,
                List<JqToken> tokens => new JqParser(tokens).ParseInterpolation(),
                _ => string.Empty,
            });
        }

        return new JqStringInterp(compiled, format);
    }

    private JqNode ParseInterpolation()
    {
        var node = ParseExpression();

        if (!AtEnd)
        {
            throw Error("unexpected token in string interpolation");
        }

        return node;
    }
}
