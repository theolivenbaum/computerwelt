using System.Globalization;
using System.Numerics;

namespace Computerwelt.Emulation.Python.Parsing;

/// <summary>
/// A recursive-descent parser for Python.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is layered strictly by precedence, one method per level, which is what
/// keeps Python's long operator table honest. The places that need care are the ones where
/// the grammar is genuinely ambiguous until more input is seen: a parenthesized expression
/// versus a tuple versus a generator, a dict versus a set, and a lambda's parameter list
/// versus a conditional expression.
/// </para>
/// <para>
/// The tokenizer has already turned indentation into <see cref="TokenKind.Indent"/> and
/// <see cref="TokenKind.Dedent"/>, so blocks parse like any bracketed construct.
/// </para>
/// </remarks>
public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _index;

    private Parser(List<Token> tokens) => _tokens = tokens;

    /// <summary>Parses <paramref name="source"/> into a module.</summary>
    public static PyModule Parse(string source)
    {
        var tokens = new Tokenizer(source).Tokenize();
        return new Parser(tokens).ParseModule();
    }

    private Token Current => _tokens[Math.Min(_index, _tokens.Count - 1)];

    private Token Peek(int offset = 1) => _tokens[Math.Min(_index + offset, _tokens.Count - 1)];

    private bool AtEnd => Current.Kind == TokenKind.EndOfInput;

    private Token Advance() => _tokens[_index++];

    private bool Match(string text)
    {
        if (!Current.Is(text))
        {
            return false;
        }

        _index++;
        return true;
    }

    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind)
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

    private void Expect(TokenKind kind)
    {
        if (!Match(kind))
        {
            throw Error($"expected {kind}");
        }
    }

    private PythonSyntaxError Error(string message) =>
        new($"invalid syntax: {message}, got '{Current.Text}'", Current.Line, Current.Column);

    private T At<T>(T node, Token token) where T : PyNode =>
        node with { Line = token.Line, Column = token.Column };

    private PyModule ParseModule()
    {
        var body = new List<Statement>();

        while (!AtEnd)
        {
            if (Match(TokenKind.Newline) || Match(TokenKind.Indent) || Match(TokenKind.Dedent))
            {
                continue;
            }

            body.AddRange(ParseStatement());
        }

        return new PyModule(body);
    }

    /// <summary>
    /// Parses one statement. Returns a list because a simple-statement line may hold
    /// several separated by <c>;</c>.
    /// </summary>
    private List<Statement> ParseStatement()
    {
        var token = Current;

        if (token.Kind == TokenKind.Keyword)
        {
            switch (token.Text)
            {
                case "if": return [ParseIf()];
                case "while": return [ParseWhile()];
                case "for": return [ParseFor(isAsync: false)];
                case "try": return [ParseTry()];
                case "with": return [ParseWith(isAsync: false)];
                case "def": return [ParseFunctionDef([], isAsync: false)];
                case "class": return [ParseClassDef([])];
                case "async": return [ParseAsync()];
            }
        }

        if (token.Is("@"))
        {
            return [ParseDecorated()];
        }

        return ParseSimpleStatementLine();
    }

    private Statement ParseAsync()
    {
        Expect("async");

        return Current.Text switch
        {
            "def" => ParseFunctionDef([], isAsync: true),
            "for" => ParseFor(isAsync: true),
            "with" => ParseWith(isAsync: true),
            _ => throw Error("expected 'def', 'for' or 'with' after 'async'"),
        };
    }

    private Statement ParseDecorated()
    {
        var decorators = new List<Expression>();

        while (Match("@"))
        {
            decorators.Add(ParseExpression());
            Expect(TokenKind.Newline);
        }

        if (Match("async"))
        {
            return ParseFunctionDef(decorators, isAsync: true);
        }

        return Current.Text switch
        {
            "def" => ParseFunctionDef(decorators, isAsync: false),
            "class" => ParseClassDef(decorators),
            _ => throw Error("expected a definition after decorators"),
        };
    }

    private List<Statement> ParseSimpleStatementLine()
    {
        var statements = new List<Statement> { ParseSimpleStatement() };

        while (Match(";"))
        {
            if (Current.Kind is TokenKind.Newline or TokenKind.EndOfInput)
            {
                break;
            }

            statements.Add(ParseSimpleStatement());
        }

        if (!Match(TokenKind.Newline) && !AtEnd && Current.Kind != TokenKind.Dedent)
        {
            throw Error("expected end of statement");
        }

        return statements;
    }

    private Statement ParseSimpleStatement()
    {
        var token = Current;

        if (token.Kind == TokenKind.Keyword)
        {
            switch (token.Text)
            {
                case "pass": _index++; return At(new Pass(), token);
                case "break": _index++; return At(new Break(), token);
                case "continue": _index++; return At(new Continue(), token);

                case "return":
                {
                    _index++;
                    var value = IsStatementEnd() ? null : ParseExpressionList();
                    return At(new Return(value), token);
                }

                case "raise":
                {
                    _index++;
                    if (IsStatementEnd())
                    {
                        return At(new Raise(null, null), token);
                    }

                    var exception = ParseExpression();
                    var cause = Match("from") ? ParseExpression() : null;
                    return At(new Raise(exception, cause), token);
                }

                case "assert":
                {
                    _index++;
                    var test = ParseExpression();
                    var message = Match(",") ? ParseExpression() : null;
                    return At(new Assert(test, message), token);
                }

                case "del":
                {
                    _index++;
                    var targets = new List<Expression> { ParseExpression() };
                    while (Match(","))
                    {
                        targets.Add(ParseExpression());
                    }

                    return At(new Delete(targets), token);
                }

                case "global" or "nonlocal":
                {
                    _index++;
                    var names = new List<string> { Advance().Text };
                    while (Match(","))
                    {
                        names.Add(Advance().Text);
                    }

                    return At<Statement>(
                        token.Text == "global" ? new Global(names) : new Nonlocal(names),
                        token);
                }

                case "import": return ParseImport();
                case "from": return ParseImportFrom();
            }
        }

        return ParseExpressionOrAssignment();
    }

    private bool IsStatementEnd() =>
        AtEnd || Current.Kind is TokenKind.Newline or TokenKind.Dedent || Current.Is(";");

    private Statement ParseImport()
    {
        var token = Current;
        Expect("import");

        var names = new List<ImportAlias>();

        do
        {
            var name = ParseDottedName();
            var alias = Match("as") ? Advance().Text : null;
            names.Add(new ImportAlias(name, alias));
        }
        while (Match(","));

        return At(new Import(names), token);
    }

    private Statement ParseImportFrom()
    {
        var token = Current;
        Expect("from");

        var module = ParseDottedName();
        Expect("import");

        var names = new List<ImportAlias>();

        if (Match("*"))
        {
            names.Add(new ImportAlias("*", null));
            return At(new ImportFrom(module, names), token);
        }

        var parenthesized = Match("(");

        do
        {
            if (parenthesized && Current.Is(")"))
            {
                break;
            }

            var name = Advance().Text;
            var alias = Match("as") ? Advance().Text : null;
            names.Add(new ImportAlias(name, alias));
        }
        while (Match(","));

        if (parenthesized)
        {
            Expect(")");
        }

        return At(new ImportFrom(module, names), token);
    }

    private string ParseDottedName()
    {
        var parts = new List<string> { Advance().Text };

        while (Current.Is(".") && Peek().Kind == TokenKind.Name)
        {
            _index++;
            parts.Add(Advance().Text);
        }

        return string.Join('.', parts);
    }

    /// <summary>
    /// Parses an expression statement, which may turn out to be an assignment. The two are
    /// only distinguishable after the left-hand side has been read.
    /// </summary>
    private Statement ParseExpressionOrAssignment()
    {
        var token = Current;
        var first = ParseExpressionList();

        // `x: int = 1` — an annotated assignment.
        if (Match(":"))
        {
            var annotation = ParseExpression();
            var annotatedValue = Match("=") ? ParseExpressionList() : null;
            return At(new Assign([first], annotatedValue, annotation), token);
        }

        if (Current.Kind == TokenKind.Operator && Current.Text.Length > 1 && Current.Text.EndsWith('=')
            && Current.Text is not ("==" or "!=" or "<=" or ">="))
        {
            var op = Advance().Text[..^1];
            var value = ParseExpressionList();
            return At(new AugmentedAssign(first, op, value), token);
        }

        if (!Current.Is("="))
        {
            return At(new ExpressionStatement(first), token);
        }

        // Chained assignment: every `=` but the last introduces another target.
        var targets = new List<Expression> { first };
        Expression assigned = first;

        while (Match("="))
        {
            assigned = ParseExpressionList();
            targets.Add(assigned);
        }

        targets.RemoveAt(targets.Count - 1);
        return At(new Assign(targets, assigned), token);
    }

    /// <summary>Parses an expression, folding a bare comma-separated list into a tuple.</summary>
    private Expression ParseExpressionList()
    {
        var token = Current;
        var first = ParseExpressionOrStar();

        if (!Current.Is(","))
        {
            return first;
        }

        var elements = new List<Expression> { first };

        while (Match(","))
        {
            if (IsExpressionEnd())
            {
                break;
            }

            elements.Add(ParseExpressionOrStar());
        }

        return At(new TupleExpr(elements), token);
    }

    private Expression ParseExpressionOrStar()
    {
        if (Current.Is("*"))
        {
            var token = Advance();
            return At(new Starred(ParseExpression()), token);
        }

        return ParseExpression();
    }

    private bool IsExpressionEnd() =>
        AtEnd
        || Current.Kind is TokenKind.Newline or TokenKind.Dedent
        || Current.Text is ")" or "]" or "}" or ":" or "=" or ";";

    private List<Statement> ParseBlock()
    {
        Expect(":");

        // A suite may be on the same line: `if x: return 1`.
        if (!Match(TokenKind.Newline))
        {
            return ParseSimpleStatementLine();
        }

        Expect(TokenKind.Indent);

        var body = new List<Statement>();

        while (!AtEnd && Current.Kind != TokenKind.Dedent)
        {
            if (Match(TokenKind.Newline))
            {
                continue;
            }

            body.AddRange(ParseStatement());
        }

        Match(TokenKind.Dedent);
        return body;
    }

    private Statement ParseIf()
    {
        var token = Current;
        Expect("if");

        var test = ParseNamedExpression();
        var body = ParseBlock();
        var orElse = ParseElseTail();

        return At(new If(test, body, orElse), token);
    }

    private List<Statement> ParseElseTail()
    {
        SkipBlankLines();

        if (Current.Is("elif"))
        {
            var token = Current;
            _index++;
            var test = ParseNamedExpression();
            var body = ParseBlock();
            var orElse = ParseElseTail();
            return [At(new If(test, body, orElse), token)];
        }

        if (Current.Is("else"))
        {
            _index++;
            return ParseBlock();
        }

        return [];
    }

    private void SkipBlankLines()
    {
        while (Current.Kind == TokenKind.Newline)
        {
            _index++;
        }
    }

    private Statement ParseWhile()
    {
        var token = Current;
        Expect("while");

        var test = ParseNamedExpression();
        var body = ParseBlock();
        var orElse = ParseOptionalElse();

        return At(new While(test, body, orElse), token);
    }

    private List<Statement> ParseOptionalElse()
    {
        SkipBlankLines();
        return Match("else") ? ParseBlock() : [];
    }

    private Statement ParseFor(bool isAsync)
    {
        var token = Current;
        Expect("for");

        var target = ParseTargetList();
        Expect("in");
        var iterable = ParseExpressionList();
        var body = ParseBlock();
        var orElse = ParseOptionalElse();

        _ = isAsync;
        return At(new For(target, iterable, body, orElse), token);
    }

    /// <summary>
    /// Parses an assignment target, which may be a bare tuple without parentheses.
    /// </summary>
    /// <remarks>
    /// Targets are parsed with the postfix grammar rather than the full expression
    /// grammar, because <c>in</c> is a comparison operator everywhere else: parsing
    /// <c>for i in xs</c> as an expression would swallow the <c>in</c> and leave the loop
    /// header with no iterable.
    /// </remarks>
    private Expression ParseTargetList()
    {
        var token = Current;
        var first = ParseTarget();

        if (!Current.Is(","))
        {
            return first;
        }

        var elements = new List<Expression> { first };

        while (Match(","))
        {
            if (Current.Is("in") || IsExpressionEnd())
            {
                break;
            }

            elements.Add(ParseTarget());
        }

        return At(new TupleExpr(elements), token);
    }

    private Expression ParseTarget()
    {
        if (Current.Is("*"))
        {
            var token = Advance();
            return At(new Starred(ParseTarget()), token);
        }

        if (Current.Is("(") || Current.Is("["))
        {
            var closing = Current.Is("(") ? ")" : "]";
            var token = Advance();
            var elements = new List<Expression>();

            while (!Current.Is(closing) && !AtEnd)
            {
                elements.Add(ParseTarget());

                if (!Match(","))
                {
                    break;
                }
            }

            Expect(closing);
            return At(new TupleExpr(elements), token);
        }

        return ParsePostfix();
    }

    private Statement ParseTry()
    {
        var token = Current;
        Expect("try");

        var body = ParseBlock();
        var handlers = new List<ExceptHandler>();

        SkipBlankLines();

        while (Current.Is("except"))
        {
            var handlerToken = Current;
            _index++;

            // `except*` is exception-group syntax; parsed and then treated as `except`.
            Match("*");

            Expression? type = null;
            string? name = null;

            if (!Current.Is(":"))
            {
                type = ParseExpression();
                if (Match("as"))
                {
                    name = Advance().Text;
                }
            }

            var handlerBody = ParseBlock();
            handlers.Add(At(new ExceptHandler(type, name, handlerBody), handlerToken));
            SkipBlankLines();
        }

        var orElse = Match("else") ? ParseBlock() : [];
        SkipBlankLines();
        var finallyBody = Match("finally") ? ParseBlock() : [];

        return At(new Try(body, handlers, orElse, finallyBody), token);
    }

    private Statement ParseWith(bool isAsync)
    {
        var token = Current;
        Expect("with");

        var items = new List<WithItem>();
        var parenthesized = Current.Is("(") && LooksLikeParenthesizedWithItems();

        if (parenthesized)
        {
            _index++;
        }

        do
        {
            if (parenthesized && Current.Is(")"))
            {
                break;
            }

            var manager = ParseExpression();
            var target = Match("as") ? ParseExpression() : null;
            items.Add(new WithItem(manager, target));
        }
        while (Match(","));

        if (parenthesized)
        {
            Expect(")");
        }

        _ = isAsync;
        return At(new With(items, ParseBlock()), token);
    }

    /// <summary>
    /// Distinguishes <c>with (a, b):</c> — a parenthesized item list — from
    /// <c>with (a, b) as t:</c>, where the parentheses build a tuple. Only an <c>as</c> or a
    /// comma directly before the closing paren settles it.
    /// </summary>
    private bool LooksLikeParenthesizedWithItems()
    {
        var depth = 0;

        for (var i = _index; i < _tokens.Count; i++)
        {
            var token = _tokens[i];

            if (token.Kind != TokenKind.Operator && token.Kind != TokenKind.Keyword)
            {
                continue;
            }

            switch (token.Text)
            {
                case "(" or "[" or "{":
                    depth++;
                    break;

                case ")" or "]" or "}":
                    depth--;
                    if (depth == 0)
                    {
                        // Only a `:` straight after the closing paren makes it an item
                        // list; anything else — `.open()`, `as`, an operator — means the
                        // parentheses were part of an expression.
                        return i + 1 < _tokens.Count && _tokens[i + 1].Is(":");
                    }

                    break;

                case "as" when depth == 1:
                    return true;
            }
        }

        return false;
    }

    private Statement ParseFunctionDef(List<Expression> decorators, bool isAsync)
    {
        var token = Current;
        Expect("def");

        var name = Advance().Text;
        Expect("(");
        var parameters = ParseParameterList(")");
        Expect(")");

        var returnAnnotation = Match("->") ? ParseExpression() : null;
        var body = ParseBlock();

        return At(new FunctionDef(name, parameters, body, decorators, isAsync, returnAnnotation), token);
    }

    private Statement ParseClassDef(List<Expression> decorators)
    {
        var token = Current;
        Expect("class");

        var name = Advance().Text;
        var bases = new List<Expression>();

        if (Match("("))
        {
            while (!Current.Is(")"))
            {
                bases.Add(ParseExpression());
                if (!Match(","))
                {
                    break;
                }
            }

            Expect(")");
        }

        return At(new ClassDef(name, bases, ParseBlock(), decorators), token);
    }

    /// <summary>
    /// Consumes the annotation on a <c>*args</c> or <c>**kwargs</c> parameter.
    /// </summary>
    /// <remarks>
    /// It is parsed and dropped, like every other annotation: the runtime erases them, and
    /// only their presence has to be accepted.
    /// </remarks>
    private void SkipAnnotation(bool allowed)
    {
        if (allowed && Match(":"))
        {
            ParseExpression();
        }
    }

    /// <summary>
    /// Parses a formal parameter list up to <paramref name="terminator"/>.
    /// </summary>
    /// <remarks>
    /// A lambda's list ends at <c>:</c>, and a lambda parameter cannot be annotated — so
    /// treating <c>:</c> as an annotation there would swallow the body.
    /// </remarks>
    private ParameterList ParseParameterList(string terminator)
    {
        var allowAnnotations = terminator != ":";

        var positional = new List<Parameter>();
        var keywordOnly = new List<Parameter>();
        string? varArgs = null;
        string? keywordArgs = null;
        var positionalOnlyCount = 0;
        var afterStar = false;

        while (!Current.Is(terminator) && !AtEnd)
        {
            if (Match("/"))
            {
                positionalOnlyCount = positional.Count;
                if (!Match(","))
                {
                    break;
                }

                continue;
            }

            if (Current.Is("*"))
            {
                _index++;
                afterStar = true;

                // A bare `*` only marks the start of keyword-only parameters.
                if (!Current.Is(",") && !Current.Is(terminator))
                {
                    varArgs = Advance().Text;
                    SkipAnnotation(allowAnnotations);
                }

                if (!Match(","))
                {
                    break;
                }

                continue;
            }

            if (Match("**"))
            {
                keywordArgs = Advance().Text;
                SkipAnnotation(allowAnnotations);
                Match(",");
                break;
            }

            var name = Advance().Text;
            var annotation = allowAnnotations && Match(":") ? ParseExpression() : null;
            var defaultValue = Match("=") ? ParseExpression() : null;

            (afterStar ? keywordOnly : positional).Add(new Parameter(name, defaultValue, annotation));

            if (!Match(","))
            {
                break;
            }
        }

        return new ParameterList(positional, varArgs, keywordOnly, keywordArgs, positionalOnlyCount);
    }

    // ---- expressions, by precedence ----

    private Expression ParseNamedExpression()
    {
        // `x := value` binds while yielding the value.
        if (Current.Kind == TokenKind.Name && Peek().Is(":="))
        {
            var token = Current;
            var target = new Name(Advance().Text) { Line = token.Line, Column = token.Column };
            _index++;
            return At(new NamedExpr(target, ParseExpression()), token);
        }

        return ParseExpression();
    }

    private Expression ParseExpression() => ParseConditional();

    private Expression ParseConditional()
    {
        var token = Current;

        if (Current.Is("lambda"))
        {
            return ParseLambda();
        }

        var value = ParseOr();

        if (!Current.Is("if"))
        {
            return value;
        }

        _index++;
        var test = ParseOr();
        Expect("else");
        var orElse = ParseExpression();

        return At(new Conditional(test, value, orElse), token);
    }

    private Expression ParseLambda()
    {
        var token = Current;
        Expect("lambda");

        var parameters = ParseParameterList(":");
        Expect(":");

        return At(new Lambda(parameters, ParseExpression()), token);
    }

    private Expression ParseOr()
    {
        var token = Current;
        var left = ParseAnd();

        if (!Current.Is("or"))
        {
            return left;
        }

        var values = new List<Expression> { left };

        while (Match("or"))
        {
            values.Add(ParseAnd());
        }

        return At(new BoolOp("or", values), token);
    }

    private Expression ParseAnd()
    {
        var token = Current;
        var left = ParseNot();

        if (!Current.Is("and"))
        {
            return left;
        }

        var values = new List<Expression> { left };

        while (Match("and"))
        {
            values.Add(ParseNot());
        }

        return At(new BoolOp("and", values), token);
    }

    private Expression ParseNot()
    {
        if (!Current.Is("not"))
        {
            return ParseComparison();
        }

        var token = Advance();
        return At(new UnaryOp("not", ParseNot()), token);
    }

    private Expression ParseComparison()
    {
        var token = Current;
        var left = ParseBitwiseOr();

        var operators = new List<string>();
        var comparators = new List<Expression>();

        while (true)
        {
            string op;

            if (Current.Kind == TokenKind.Operator && Current.Text is "<" or ">" or "==" or "!=" or "<=" or ">=")
            {
                op = Advance().Text;
            }
            else if (Current.Is("in"))
            {
                _index++;
                op = "in";
            }
            else if (Current.Is("not") && Peek().Is("in"))
            {
                _index += 2;
                op = "not in";
            }
            else if (Current.Is("is"))
            {
                _index++;
                op = Match("not") ? "is not" : "is";
            }
            else
            {
                break;
            }

            operators.Add(op);
            comparators.Add(ParseBitwiseOr());
        }

        return operators.Count == 0 ? left : At(new Compare(left, operators, comparators), token);
    }

    private Expression ParseBitwiseOr() => ParseBinary(ParseBitwiseXor, "|");

    private Expression ParseBitwiseXor() => ParseBinary(ParseBitwiseAnd, "^");

    private Expression ParseBitwiseAnd() => ParseBinary(ParseShift, "&");

    private Expression ParseShift() => ParseBinary(ParseAdditive, "<<", ">>");

    private Expression ParseAdditive() => ParseBinary(ParseMultiplicative, "+", "-");

    private Expression ParseMultiplicative() => ParseBinary(ParseUnary, "*", "/", "//", "%", "@");

    private Expression ParseBinary(Func<Expression> next, params string[] operators)
    {
        var token = Current;
        var left = next();

        while (Current.Kind == TokenKind.Operator && operators.Contains(Current.Text))
        {
            var op = Advance().Text;
            left = At(new BinaryOp(op, left, next()), token);
        }

        return left;
    }

    private Expression ParseUnary()
    {
        if (Current.Kind == TokenKind.Operator && Current.Text is "-" or "+" or "~")
        {
            var token = Advance();
            return At(new UnaryOp(token.Text, ParseUnary()), token);
        }

        if (Current.Is("await"))
        {
            var token = Advance();
            return At(new Await(ParseUnary()), token);
        }

        return ParsePower();
    }

    private Expression ParsePower()
    {
        var token = Current;
        var left = ParsePostfix();

        // `**` binds tighter than a unary on its left but looser on its right, so the
        // exponent is parsed as a full unary expression: `2 ** -1` is valid.
        if (!Match("**"))
        {
            return left;
        }

        return At(new BinaryOp("**", left, ParseUnary()), token);
    }

    private Expression ParsePostfix()
    {
        var token = Current;
        var value = ParsePrimary();

        while (true)
        {
            if (Match("."))
            {
                value = At(new Attribute(value, Advance().Text), token);
                continue;
            }

            if (Current.Is("("))
            {
                _index++;
                var (arguments, keywords) = ParseCallArguments();
                Expect(")");
                value = At(new Call(value, arguments, keywords), token);
                continue;
            }

            if (Current.Is("["))
            {
                _index++;
                var index = ParseSubscriptIndex();
                Expect("]");
                value = At(new Subscript(value, index), token);
                continue;
            }

            return value;
        }
    }

    private (List<Expression> Arguments, List<KeywordArgument> Keywords) ParseCallArguments()
    {
        var arguments = new List<Expression>();
        var keywords = new List<KeywordArgument>();

        while (!Current.Is(")") && !AtEnd)
        {
            if (Match("**"))
            {
                keywords.Add(new KeywordArgument(null, ParseExpression()));
            }
            else if (Current.Is("*"))
            {
                var token = Advance();
                arguments.Add(At(new Starred(ParseExpression()), token));
            }
            else if (Current.Kind == TokenKind.Name && Peek().Is("=") )
            {
                var name = Advance().Text;
                _index++;
                keywords.Add(new KeywordArgument(name, ParseExpression()));
            }
            else
            {
                var argument = ParseNamedExpression();

                // A bare comprehension as the sole argument: `sum(x for x in y)`.
                if (Current.Is("for"))
                {
                    argument = ParseComprehensionTail(argument, ComprehensionKind.Generator, null, Current);
                }

                arguments.Add(argument);
            }

            if (!Match(","))
            {
                break;
            }
        }

        return (arguments, keywords);
    }

    private Expression ParseSubscriptIndex()
    {
        var token = Current;
        var elements = new List<Expression> { ParseSliceOrExpression() };

        if (!Current.Is(","))
        {
            return elements[0];
        }

        while (Match(","))
        {
            if (Current.Is("]"))
            {
                break;
            }

            elements.Add(ParseSliceOrExpression());
        }

        return At(new TupleExpr(elements), token);
    }

    private Expression ParseSliceOrExpression()
    {
        var token = Current;
        Expression? lower = null;

        if (!Current.Is(":"))
        {
            lower = ParseExpression();

            if (!Current.Is(":"))
            {
                return lower;
            }
        }

        Expect(":");

        Expression? upper = null;
        if (!Current.Is(":") && !Current.Is("]") && !Current.Is(","))
        {
            upper = ParseExpression();
        }

        Expression? step = null;
        if (Match(":") && !Current.Is("]") && !Current.Is(","))
        {
            step = ParseExpression();
        }

        return At(new Slice(lower, upper, step), token);
    }

    private Expression ParsePrimary()
    {
        var token = Current;

        switch (token.Kind)
        {
            case TokenKind.Integer:
                _index++;
                return At(new Literal(BigInteger.Parse(token.Text, CultureInfo.InvariantCulture)), token);

            case TokenKind.Float:
                _index++;
                return At(new Literal(double.Parse(token.Text, CultureInfo.InvariantCulture)), token);

            case TokenKind.String or TokenKind.FString:
                return At(ConcatenateStrings(), token);

            case TokenKind.Bytes:
            {
                // Adjacent bytes literals concatenate, exactly as strings do.
                var bytes = new System.Text.StringBuilder();

                while (Current.Kind == TokenKind.Bytes)
                {
                    bytes.Append(Advance().Text);
                }

                return At(new Literal(System.Text.Encoding.Latin1.GetBytes(bytes.ToString())), token);
            }

            case TokenKind.Name:
                _index++;
                return At(new Name(token.Text), token);

            case TokenKind.Keyword:
                switch (token.Text)
                {
                    case "True": _index++; return At(new Literal(true), token);
                    case "False": _index++; return At(new Literal(false), token);
                    case "None": _index++; return At(new Literal(null), token);
                    case "lambda": return ParseLambda();
                    case "not": return ParseNot();

                    case "yield":
                    {
                        _index++;
                        var delegating = Match("from");
                        var value = IsExpressionEnd() ? null : ParseExpression();
                        return At(new Yield(value, delegating), token);
                    }
                }

                break;
        }

        if (token.Is("("))
        {
            return ParseParenthesized();
        }

        if (token.Is("["))
        {
            return ParseListDisplay();
        }

        if (token.Is("{"))
        {
            return ParseBraceDisplay();
        }

        if (token.Is("..."))
        {
            _index++;
            return At(new Literal(Ellipsis.Value), token);
        }

        throw Error("expected an expression");
    }

    /// <summary>
    /// Concatenates adjacent string literals, which Python treats as one literal:
    /// <c>'a' 'b'</c> is <c>'ab'</c>.
    /// </summary>
    /// <remarks>
    /// An f-string may join the run — <c>f'x' 'y'</c> is one f-string — so the result is a
    /// formatted string whenever any piece was one, and a plain literal otherwise.
    /// </remarks>
    private Expression ConcatenateStrings()
    {
        var parts = new List<FormatPart>();
        var text = new System.Text.StringBuilder();
        var formatted = false;
        var unicodePrefix = false;

        while (Current.Kind is TokenKind.String or TokenKind.FString)
        {
            var piece = Advance();
            unicodePrefix |= piece.Prefix.Contains('u', StringComparison.Ordinal);

            if (piece.Kind == TokenKind.String)
            {
                text.Append(piece.Text);

                // The tokenizer has already decoded a plain string, so it goes in as-is.
                parts.Add(new FormatPart(piece.Text, null, '\0', null));
                continue;
            }

            formatted = true;
            parts.AddRange(FStringParser.Parse(piece.Text).Parts);
        }

        return formatted
            ? new FormattedString(parts)
            : new Literal(text.ToString()) { HasUnicodePrefix = unicodePrefix };
    }

    private Expression ParseParenthesized()
    {
        var token = Current;
        Expect("(");

        if (Match(")"))
        {
            return At(new TupleExpr([]), token);
        }

        var first = Current.Is("*")
            ? ParseExpressionOrStar()
            : Current.Is("yield") ? ParsePrimary() : ParseNamedExpression();

        if (Current.Is("for"))
        {
            var comprehension = ParseComprehensionTail(first, ComprehensionKind.Generator, null, token);
            Expect(")");
            return comprehension;
        }

        if (!Current.Is(","))
        {
            Expect(")");
            return first;
        }

        var elements = new List<Expression> { first };

        while (Match(","))
        {
            if (Current.Is(")"))
            {
                break;
            }

            elements.Add(ParseExpressionOrStar());
        }

        Expect(")");
        return At(new TupleExpr(elements), token);
    }

    private Expression ParseListDisplay()
    {
        var token = Current;
        Expect("[");

        if (Match("]"))
        {
            return At(new ListExpr([]), token);
        }

        var first = ParseExpressionOrStar();

        if (Current.Is("for"))
        {
            var comprehension = ParseComprehensionTail(first, ComprehensionKind.List, null, token);
            Expect("]");
            return comprehension;
        }

        var elements = new List<Expression> { first };

        while (Match(","))
        {
            if (Current.Is("]"))
            {
                break;
            }

            elements.Add(ParseExpressionOrStar());
        }

        Expect("]");
        return At(new ListExpr(elements), token);
    }

    /// <summary>Parses a set or dict display — they share the same opening brace.</summary>
    private Expression ParseBraceDisplay()
    {
        var token = Current;
        Expect("{");

        if (Match("}"))
        {
            return At(new DictExpr([], []), token);
        }

        // `{**mapping}` can only be a dict.
        if (Match("**"))
        {
            var keys = new List<Expression?> { null };
            var values = new List<Expression> { ParseExpression() };
            ParseDictTail(keys, values);
            return At(new DictExpr(keys, values), token);
        }

        var first = ParseExpressionOrStar();

        if (Match(":"))
        {
            var firstValue = ParseExpression();

            if (Current.Is("for"))
            {
                var comprehension = ParseComprehensionTail(firstValue, ComprehensionKind.Dictionary, first, token);
                Expect("}");
                return comprehension;
            }

            var keys = new List<Expression?> { first };
            var values = new List<Expression> { firstValue };
            ParseDictTail(keys, values);
            return At(new DictExpr(keys, values), token);
        }

        if (Current.Is("for"))
        {
            var comprehension = ParseComprehensionTail(first, ComprehensionKind.Set, null, token);
            Expect("}");
            return comprehension;
        }

        var elements = new List<Expression> { first };

        while (Match(","))
        {
            if (Current.Is("}"))
            {
                break;
            }

            elements.Add(ParseExpressionOrStar());
        }

        Expect("}");
        return At(new SetExpr(elements), token);
    }

    private void ParseDictTail(List<Expression?> keys, List<Expression> values)
    {
        while (Match(","))
        {
            if (Current.Is("}"))
            {
                break;
            }

            if (Match("**"))
            {
                keys.Add(null);
                values.Add(ParseExpression());
                continue;
            }

            keys.Add(ParseExpression());
            Expect(":");
            values.Add(ParseExpression());
        }

        Expect("}");
    }

    private Expression ParseComprehensionTail(Expression element, ComprehensionKind kind, Expression? key, Token token)
    {
        var clauses = new List<ComprehensionClause>();

        while (Current.Is("for") || Current.Is("async"))
        {
            var isAsync = Match("async");
            Expect("for");

            var target = ParseTargetList();
            Expect("in");

            // The iterable stops before any `if`, which belongs to this clause.
            var iterable = ParseOr();
            var conditions = new List<Expression>();

            while (Match("if"))
            {
                conditions.Add(ParseOr());
            }

            clauses.Add(new ComprehensionClause(target, iterable, conditions, isAsync));
        }

        return At(new Comprehension(kind, element, key, clauses), token);
    }
}

/// <summary>The singleton value of Python's <c>...</c>.</summary>
public sealed class Ellipsis
{
    private Ellipsis()
    {
    }

    /// <summary>The one instance.</summary>
    public static Ellipsis Value { get; } = new();

    /// <inheritdoc />
    public override string ToString() => "Ellipsis";
}
