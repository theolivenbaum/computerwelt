namespace Bashkit.Parsing;

/// <summary>
/// A recursive-descent parser over the token stream.
/// </summary>
/// <remarks>
/// The grammar is bash's, which is ambiguous in places that only context resolves: a word
/// is a keyword only in command position, <c>(</c> starts a subshell in command position
/// but a function body after a name, and <c>in</c> is a keyword only inside <c>for</c> and
/// <c>case</c>. The parser therefore tracks command position explicitly rather than
/// relying on the token kind alone.
/// </remarks>
public sealed class Parser
{
    private static readonly HashSet<string> BlockTerminators =
    [
        "then", "elif", "else", "fi", "do", "done", "esac", "}",
    ];

    private readonly string _source;
    private readonly List<Token> _tokens;
    private readonly IReadOnlyList<string> _hereDocumentBodies;
    private readonly ExecutionBudget? _budget;
    private int _index;
    private int _hereDocumentCursor;

    private Parser(string source, List<Token> tokens, IReadOnlyList<string> hereDocumentBodies, ExecutionBudget? budget)
    {
        _source = source;
        _tokens = tokens;
        _hereDocumentBodies = hereDocumentBodies;
        _budget = budget;
    }

    /// <summary>Parses <paramref name="script"/> into a syntax tree.</summary>
    public static Script Parse(string script, ExecutionBudget? budget = null)
    {
        script ??= string.Empty;
        var lexer = new Lexer(script, budget);
        var tokens = lexer.Tokenize();
        return new Parser(script, tokens, lexer.HereDocumentBodies, budget).ParseScript();
    }

    private Token Current => _tokens[_index];

    private Token Peek(int offset = 1) =>
        _index + offset < _tokens.Count ? _tokens[_index + offset] : _tokens[^1];

    private bool AtEnd => Current.Kind == TokenKind.EndOfInput;

    private Token Advance() => _tokens[_index++];

    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        _index++;
        return true;
    }

    private bool MatchKeyword(string keyword)
    {
        if (!IsKeyword(Current, keyword))
        {
            return false;
        }

        _index++;
        return true;
    }

    private void ExpectKeyword(string keyword)
    {
        if (!MatchKeyword(keyword))
        {
            throw Unexpected($"expected `{keyword}'");
        }
    }

    private static bool IsKeyword(Token token, string keyword) =>
        token.Kind == TokenKind.Word && token.Text == keyword;

    private ParseException Unexpected(string expectation) =>
        new($"syntax error near unexpected token `{Current.Text}': {expectation}", Current.Start);

    private Script ParseScript()
    {
        var commands = new List<Node>();
        SkipTerminators();

        while (!AtEnd)
        {
            _budget?.ChargeParserFuel();
            commands.Add(ParseList());
            SkipTerminators();
        }

        return new Script(commands);
    }

    private void SkipTerminators()
    {
        while (Current.Kind is TokenKind.Newline or TokenKind.Semicolon)
        {
            _index++;
        }
    }

    /// <summary>Parses a list of pipelines joined by <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and <c>&amp;</c>.</summary>
    private Node ParseList()
    {
        var left = ParsePipeline();

        while (true)
        {
            _budget?.ChargeParserFuel();

            ListOperator op;
            switch (Current.Kind)
            {
                case TokenKind.AndIf:
                    op = ListOperator.And;
                    break;
                case TokenKind.OrIf:
                    op = ListOperator.Or;
                    break;
                case TokenKind.Ampersand:
                    _index++;
                    left = new CommandList(left, ListOperator.Background, null) { Span = left.Span };
                    SkipNewlinesAfterOperator(consumeSeparators: false);
                    if (IsListEnd())
                    {
                        return left;
                    }

                    left = new CommandList(left, ListOperator.Sequence, ParseList()) { Span = left.Span };
                    return left;
                case TokenKind.Semicolon:
                    _index++;
                    if (IsListEnd())
                    {
                        return left;
                    }

                    return new CommandList(left, ListOperator.Sequence, ParseList()) { Span = left.Span };
                default:
                    return left;
            }

            _index++;
            SkipNewlinesAfterOperator(consumeSeparators: false);
            var right = ParsePipeline();
            left = new CommandList(left, op, right) { Span = left.Span.Union(right.Span) };
        }
    }

    /// <summary>True when the next token cannot begin another command in this list.</summary>
    private bool IsListEnd()
    {
        SkipNewlinesOnly();
        return AtEnd
            || Current.Kind is TokenKind.RightParen or TokenKind.DoubleSemicolon
                or TokenKind.SemiAmpersand or TokenKind.DoubleSemiAmpersand
            || (Current.Kind == TokenKind.Word && BlockTerminators.Contains(Current.Text));
    }

    private void SkipNewlinesOnly()
    {
        while (Current.Kind == TokenKind.Newline)
        {
            _index++;
        }
    }

    private void SkipNewlinesAfterOperator(bool consumeSeparators)
    {
        while (Current.Kind == TokenKind.Newline || (consumeSeparators && Current.Kind == TokenKind.Semicolon))
        {
            _index++;
        }
    }

    private Node ParsePipeline()
    {
        var negated = false;
        while (IsKeyword(Current, "!"))
        {
            negated = !negated;
            _index++;
        }

        var stages = new List<Node> { ParseCommand() };
        var pipeStderr = new List<bool>();

        while (Current.Kind is TokenKind.Pipe or TokenKind.PipeAmpersand)
        {
            pipeStderr.Add(Current.Kind == TokenKind.PipeAmpersand);
            _index++;
            SkipNewlinesOnly();
            stages.Add(ParseCommand());
        }

        if (stages.Count == 1 && !negated)
        {
            return stages[0];
        }

        var span = stages[0].Span.Union(stages[^1].Span);
        return new Pipeline(stages, negated, pipeStderr) { Span = span };
    }

    private Node ParseCommand()
    {
        _budget?.ChargeParserFuel();

        if (Current.Kind == TokenKind.Word)
        {
            switch (Current.Text)
            {
                case "if": return WithRedirects(ParseIf());
                case "while": return WithRedirects(ParseWhile());
                case "until": return WithRedirects(ParseUntil());
                case "for": return WithRedirects(ParseFor());
                case "case": return WithRedirects(ParseCase());
                case "function": return ParseFunctionKeyword();
                case "{": return WithRedirects(ParseBraceGroup());
                case "[[": return WithRedirects(ParseConditional());
            }

            // `name()` introduces a function definition.
            if (Peek().Kind == TokenKind.LeftParen && Peek(2).Kind == TokenKind.RightParen)
            {
                return ParseFunctionShorthand();
            }
        }

        if (Current.Kind == TokenKind.DoubleLeftParen)
        {
            return WithRedirects(ParseArithmeticCommand());
        }

        if (Current.Kind == TokenKind.LeftParen)
        {
            return WithRedirects(ParseSubshell());
        }

        return ParseSimpleCommand();
    }

    /// <summary>Attaches any redirections that follow a compound command to it.</summary>
    private Node WithRedirects(Node body)
    {
        var redirects = new List<Redirect>();
        while (Current.IsRedirectOperator || (Current.Kind == TokenKind.IoNumber && Peek().IsRedirectOperator))
        {
            redirects.Add(ParseRedirect());
        }

        return redirects.Count == 0 ? body : new CompoundCommand(body, redirects) { Span = body.Span };
    }

    private Node ParseIf()
    {
        var start = Current.Start;
        ExpectKeyword("if");
        var condition = ParseBlockUntil("then");
        ExpectKeyword("then");
        var then = ParseBlockUntil("elif", "else", "fi");

        Node? elseBranch = null;
        if (IsKeyword(Current, "elif"))
        {
            // `elif` is exactly a nested `if` in the else position.
            _index++;
            var elifCondition = ParseBlockUntil("then");
            ExpectKeyword("then");
            var elifThen = ParseBlockUntil("elif", "else", "fi");
            var nested = ParseElseTail(elifCondition, elifThen, start);
            return nested;
        }

        if (MatchKeyword("else"))
        {
            elseBranch = ParseBlockUntil("fi");
        }

        ExpectKeyword("fi");
        return new IfCommand(condition, then, elseBranch) { Span = new Span(start, Current.Start - start) };
    }

    private Node ParseElseTail(Node condition, Node then, int start)
    {
        Node? elseBranch = null;

        if (IsKeyword(Current, "elif"))
        {
            _index++;
            var nestedCondition = ParseBlockUntil("then");
            ExpectKeyword("then");
            var nestedThen = ParseBlockUntil("elif", "else", "fi");
            elseBranch = ParseElseTail(nestedCondition, nestedThen, start);
            return new IfCommand(condition, then, elseBranch) { Span = new Span(start, Current.Start - start) };
        }

        if (MatchKeyword("else"))
        {
            elseBranch = ParseBlockUntil("fi");
        }

        ExpectKeyword("fi");
        return new IfCommand(condition, then, elseBranch) { Span = new Span(start, Current.Start - start) };
    }

    private Node ParseWhile()
    {
        var start = Current.Start;
        ExpectKeyword("while");
        var condition = ParseBlockUntil("do");
        ExpectKeyword("do");
        var body = ParseBlockUntil("done");
        ExpectKeyword("done");
        return new WhileCommand(condition, body) { Span = new Span(start, Current.Start - start) };
    }

    private Node ParseUntil()
    {
        var start = Current.Start;
        ExpectKeyword("until");
        var condition = ParseBlockUntil("do");
        ExpectKeyword("do");
        var body = ParseBlockUntil("done");
        ExpectKeyword("done");
        return new UntilCommand(condition, body) { Span = new Span(start, Current.Start - start) };
    }

    private Node ParseFor()
    {
        var start = Current.Start;
        ExpectKeyword("for");

        if (Current.Kind == TokenKind.DoubleLeftParen)
        {
            return ParseArithmeticFor(start);
        }

        if (Current.Kind != TokenKind.Word)
        {
            throw Unexpected("expected a loop variable name");
        }

        var variable = Advance().Text;
        List<Word>? items = null;

        if (MatchKeyword("in"))
        {
            items = [];
            while (Current.Kind == TokenKind.Word && !BlockTerminators.Contains(Current.Text))
            {
                items.Add(WordParser.Parse(Advance().Text));
            }
        }

        SkipTerminators();
        ExpectKeyword("do");
        var body = ParseBlockUntil("done");
        ExpectKeyword("done");

        return new ForCommand(variable, items, body) { Span = new Span(start, Current.Start - start) };
    }

    private Node ParseArithmeticFor(int start)
    {
        // The lexer hands back `((init; cond; update` as a `((` token plus words, so the
        // clauses are recovered from the raw source between the parentheses.
        var open = Current.Start;
        var text = ReadUntilDoubleRightParen(open + 2);

        var clauses = SplitTopLevel(text, ';');
        var init = clauses.Count > 0 ? clauses[0].Trim() : string.Empty;
        var condition = clauses.Count > 1 ? clauses[1].Trim() : string.Empty;
        var update = clauses.Count > 2 ? clauses[2].Trim() : string.Empty;

        SkipTerminators();
        ExpectKeyword("do");
        var body = ParseBlockUntil("done");
        ExpectKeyword("done");

        return new ArithmeticForCommand(init, condition, update, body)
        {
            Span = new Span(start, Current.Start - start),
        };
    }

    private Node ParseArithmeticCommand()
    {
        var start = Current.Start;
        var text = ReadUntilDoubleRightParen(start + 2);
        return new ArithmeticCommand(text.Trim()) { Span = new Span(start, Current.Start - start) };
    }

    /// <summary>
    /// Recovers the raw text between <c>((</c> and its matching <c>))</c> by walking the
    /// token stream, which is how arithmetic clauses avoid being re-lexed as shell words.
    /// </summary>
    private string ReadUntilDoubleRightParen(int textStart)
    {
        _index++; // consume `((`
        var depth = 1;

        while (!AtEnd)
        {
            switch (Current.Kind)
            {
                case TokenKind.LeftParen or TokenKind.DoubleLeftParen:
                    depth += Current.Kind == TokenKind.DoubleLeftParen ? 2 : 1;
                    break;

                case TokenKind.RightParen:
                    depth--;
                    if (depth == 0)
                    {
                        var end = Current.Start;
                        _index++;
                        if (Current.Kind == TokenKind.RightParen)
                        {
                            _index++;
                        }

                        return SourceBetween(textStart, end);
                    }

                    if (depth == 1 && Peek().Kind == TokenKind.RightParen)
                    {
                        var end = Current.Start;
                        _index += 2;
                        return SourceBetween(textStart, end);
                    }

                    break;
            }

            _index++;
        }

        throw new ParseException("unexpected EOF while looking for matching `))'", textStart);
    }

    /// <summary>
    /// The raw source between two offsets. Arithmetic clauses are recovered this way
    /// rather than by re-joining tokens, because the token stream has already discarded
    /// the spacing and quoting the arithmetic grammar cares about.
    /// </summary>
    private string SourceBetween(int start, int end) =>
        start >= 0 && end <= _source.Length && start <= end ? _source[start..end] : string.Empty;

    private Node ParseCase()
    {
        var start = Current.Start;
        ExpectKeyword("case");

        if (Current.Kind != TokenKind.Word)
        {
            throw Unexpected("expected a word after `case'");
        }

        var subject = WordParser.Parse(Advance().Text);
        SkipNewlinesOnly();
        ExpectKeyword("in");
        SkipTerminators();

        var items = new List<CaseItem>();
        while (!AtEnd && !IsKeyword(Current, "esac"))
        {
            _budget?.ChargeParserFuel();
            items.Add(ParseCaseItem());
            SkipTerminators();
        }

        ExpectKeyword("esac");
        return new CaseCommand(subject, items) { Span = new Span(start, Current.Start - start) };
    }

    private CaseItem ParseCaseItem()
    {
        // An optional leading `(` is allowed before the first pattern.
        Match(TokenKind.LeftParen);

        var patterns = new List<Word>();
        while (true)
        {
            if (Current.Kind != TokenKind.Word)
            {
                throw Unexpected("expected a case pattern");
            }

            patterns.Add(WordParser.Parse(Advance().Text));

            if (Current.Kind == TokenKind.Pipe)
            {
                _index++;
                continue;
            }

            break;
        }

        if (!Match(TokenKind.RightParen))
        {
            throw Unexpected("expected `)' after case pattern");
        }

        SkipTerminators();

        Node? body = null;
        if (!IsCaseItemEnd())
        {
            body = ParseList();
        }

        SkipNewlinesOnly();

        var terminator = Current.Kind switch
        {
            TokenKind.DoubleSemicolon => CaseTerminator.Break,
            TokenKind.SemiAmpersand => CaseTerminator.FallThrough,
            TokenKind.DoubleSemiAmpersand => CaseTerminator.ContinueMatching,
            _ => CaseTerminator.Break,
        };

        if (Current.Kind is TokenKind.DoubleSemicolon or TokenKind.SemiAmpersand or TokenKind.DoubleSemiAmpersand)
        {
            _index++;
        }

        return new CaseItem(patterns, body, terminator);
    }

    private bool IsCaseItemEnd()
    {
        SkipNewlinesOnly();
        return AtEnd
            || Current.Kind is TokenKind.DoubleSemicolon or TokenKind.SemiAmpersand or TokenKind.DoubleSemiAmpersand
            || IsKeyword(Current, "esac");
    }

    private Node ParseSubshell()
    {
        var start = Current.Start;
        _index++; // `(`
        var body = ParseCompoundBody(TokenKind.RightParen);
        if (!Match(TokenKind.RightParen))
        {
            throw Unexpected("expected `)'");
        }

        return new Subshell(body) { Span = new Span(start, Current.Start - start) };
    }

    private Node ParseBraceGroup()
    {
        var start = Current.Start;
        ExpectKeyword("{");
        var body = ParseBlockUntil("}");
        ExpectKeyword("}");
        return new BraceGroup(body) { Span = new Span(start, Current.Start - start) };
    }

    private Node ParseConditional()
    {
        var start = Current.Start;
        ExpectKeyword("[[");
        var expression = ParseConditionalOr();

        if (!MatchKeyword("]]"))
        {
            throw Unexpected("expected `]]'");
        }

        return new ConditionalCommand(expression) { Span = new Span(start, Current.Start - start) };
    }

    private ConditionalExpression ParseConditionalOr()
    {
        var left = ParseConditionalAnd();
        while (Current.Kind == TokenKind.OrIf)
        {
            _index++;
            SkipNewlinesOnly();
            left = new ConditionalExpression.Or(left, ParseConditionalAnd());
        }

        return left;
    }

    private ConditionalExpression ParseConditionalAnd()
    {
        var left = ParseConditionalPrimary();
        while (Current.Kind == TokenKind.AndIf)
        {
            _index++;
            SkipNewlinesOnly();
            left = new ConditionalExpression.And(left, ParseConditionalPrimary());
        }

        return left;
    }

    private ConditionalExpression ParseConditionalPrimary()
    {
        if (IsKeyword(Current, "!"))
        {
            _index++;
            return new ConditionalExpression.Not(ParseConditionalPrimary());
        }

        if (Current.Kind == TokenKind.LeftParen)
        {
            _index++;
            var inner = ParseConditionalOr();
            if (!Match(TokenKind.RightParen))
            {
                throw Unexpected("expected `)' in conditional expression");
            }

            return inner;
        }

        if (Current.Kind != TokenKind.Word)
        {
            throw Unexpected("expected an operand in conditional expression");
        }

        var first = Advance();

        // Unary file and string tests.
        if (first.Text.Length == 2 && first.Text[0] == '-' && char.IsAsciiLetter(first.Text[1])
            && Current.Kind == TokenKind.Word && Current.Text != "]]")
        {
            return new ConditionalExpression.Unary(first.Text, WordParser.Parse(Advance().Text));
        }

        var left = WordParser.Parse(first.Text);

        if (Current.Kind == TokenKind.Word && IsConditionalBinaryOperator(Current.Text))
        {
            var op = Advance().Text;

            // `=~` takes a regular expression, whose `(`, `)` and `|` the lexer split into
            // separate tokens. The operand is recovered from the raw source instead: it
            // runs to the next whitespace, which is exactly where bash ends it too.
            if (op == "=~")
            {
                return new ConditionalExpression.Binary(op, left, ReadRegexOperand());
            }

            if (Current.Kind != TokenKind.Word)
            {
                throw Unexpected($"expected an operand after `{op}'");
            }

            return new ConditionalExpression.Binary(op, left, WordParser.Parse(Advance().Text));
        }

        // `<` and `>` lex as redirections but mean string comparison inside `[[ ]]`.
        if (Current.Kind is TokenKind.Less or TokenKind.Great)
        {
            var op = Current.Kind == TokenKind.Less ? "<" : ">";
            _index++;
            if (Current.Kind != TokenKind.Word)
            {
                throw Unexpected($"expected an operand after `{op}'");
            }

            return new ConditionalExpression.Binary(op, left, WordParser.Parse(Advance().Text));
        }

        return new ConditionalExpression.Value(left);
    }

    /// <summary>
    /// Reads the operand of <c>=~</c> as the run of source characters up to the next
    /// whitespace, rejoining tokens the lexer separated on regex metacharacters.
    /// </summary>
    private Word ReadRegexOperand()
    {
        if (AtEnd)
        {
            throw Unexpected("expected a regular expression after `=~'");
        }

        var first = Advance();
        var end = first.Start + first.Length;

        // Tokens that abut with no gap were one whitespace-delimited word in the source.
        while (!AtEnd
            && Current.Kind is not (TokenKind.Newline or TokenKind.Semicolon)
            && Current.Start == end)
        {
            end = Current.Start + Current.Length;
            _index++;
        }

        return WordParser.Parse(SourceBetween(first.Start, end));
    }

    private static bool IsConditionalBinaryOperator(string text) =>
        text is "=" or "==" or "!=" or "=~" or "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge"
            or "-nt" or "-ot" or "-ef";

    private Node ParseFunctionKeyword()
    {
        var start = Current.Start;
        ExpectKeyword("function");

        if (Current.Kind != TokenKind.Word)
        {
            throw Unexpected("expected a function name");
        }

        var name = Advance().Text;

        // `function name ()` allows the parentheses but does not require them.
        if (Current.Kind == TokenKind.LeftParen)
        {
            _index++;
            if (!Match(TokenKind.RightParen))
            {
                throw Unexpected("expected `)' in function definition");
            }
        }

        SkipNewlinesOnly();
        var body = ParseFunctionBody();
        return new FunctionDef(name, body) { Span = new Span(start, Current.Start - start) };
    }

    private Node ParseFunctionShorthand()
    {
        var start = Current.Start;
        var name = Advance().Text;
        _index += 2; // `(` `)`
        SkipNewlinesOnly();
        var body = ParseFunctionBody();
        return new FunctionDef(name, body) { Span = new Span(start, Current.Start - start) };
    }

    private Node ParseFunctionBody()
    {
        if (IsKeyword(Current, "{"))
        {
            return ParseBraceGroup();
        }

        if (Current.Kind == TokenKind.LeftParen)
        {
            return ParseSubshell();
        }

        return ParseCommand();
    }

    /// <summary>
    /// Parses the commands up to one of <paramref name="terminators"/>.
    /// </summary>
    /// <remarks>
    /// An empty body is a syntax error in bash — <c>while true; do done</c> does not run an
    /// empty loop, it fails to parse — so callers that require a body say so. Only
    /// <c>case ... esac</c> may legitimately be empty.
    /// </remarks>
    private Node ParseBlockUntil(params string[] terminators) => ParseBlockUntil(true, terminators);

    private Node ParseBlockUntil(bool required, params string[] terminators)
    {
        var terminatorSet = new HashSet<string>(terminators, StringComparer.Ordinal);
        var commands = new List<Node>();
        SkipTerminators();

        while (!AtEnd && !(Current.Kind == TokenKind.Word && terminatorSet.Contains(Current.Text)))
        {
            _budget?.ChargeParserFuel();
            commands.Add(ParseList());
            SkipTerminators();
        }

        if (required && commands.Count == 0)
        {
            throw Unexpected($"syntax error near unexpected token `{Current.Text}'");
        }

        return Sequence(commands);
    }

    private Node ParseCompoundBody(TokenKind terminator)
    {
        var commands = new List<Node>();
        SkipTerminators();

        while (!AtEnd && Current.Kind != terminator)
        {
            _budget?.ChargeParserFuel();
            commands.Add(ParseList());
            SkipTerminators();
        }

        return Sequence(commands);
    }

    private static Node Sequence(List<Node> commands)
    {
        if (commands.Count == 0)
        {
            return new SimpleCommand([], [], []);
        }

        var result = commands[0];
        for (var i = 1; i < commands.Count; i++)
        {
            result = new CommandList(result, ListOperator.Sequence, commands[i])
            {
                Span = result.Span.Union(commands[i].Span),
            };
        }

        return result;
    }

    private Node ParseSimpleCommand()
    {
        var start = Current.Start;
        var assignments = new List<Assignment>();
        var words = new List<Word>();
        var redirects = new List<Redirect>();
        var seenCommandName = false;

        while (!AtEnd)
        {
            _budget?.ChargeParserFuel();

            if (Current.IsRedirectOperator || (Current.Kind == TokenKind.IoNumber && Peek().IsRedirectOperator))
            {
                redirects.Add(ParseRedirect());
                continue;
            }

            if (Current.Kind == TokenKind.IoNumber)
            {
                words.Add(WordParser.Parse(Advance().Text));
                seenCommandName = true;
                continue;
            }

            if (Current.Kind != TokenKind.Word)
            {
                break;
            }

            var text = Current.Text;

            if (!seenCommandName && BlockTerminators.Contains(text) && words.Count == 0)
            {
                break;
            }

            // Assignments only count before the command name; after it they are arguments.
            if (!seenCommandName && TryParseAssignment(text, out var assignment))
            {
                assignments.Add(assignment);
                _index++;
                continue;
            }

            words.Add(WordParser.Parse(text));
            seenCommandName = true;
            _index++;
        }

        if (assignments.Count == 0 && words.Count == 0 && redirects.Count == 0)
        {
            throw Unexpected("expected a command");
        }

        return new SimpleCommand(assignments, words, redirects)
        {
            Span = new Span(start, Math.Max(0, Current.Start - start)),
        };
    }

    /// <summary>
    /// Recognizes <c>NAME=value</c>, <c>NAME+=value</c> and <c>NAME[idx]=value</c>. The name
    /// must be a valid identifier, which is what keeps <c>a=b</c> an assignment but
    /// <c>1=b</c> and <c>a-b=c</c> ordinary words.
    /// </summary>
    private static bool TryParseAssignment(string raw, out Assignment assignment)
    {
        assignment = null!;

        var i = 0;
        if (raw.Length == 0 || (!char.IsAsciiLetter(raw[0]) && raw[0] != '_'))
        {
            return false;
        }

        while (i < raw.Length && (char.IsAsciiLetterOrDigit(raw[i]) || raw[i] == '_'))
        {
            i++;
        }

        var name = raw[..i];
        string? index = null;

        if (i < raw.Length && raw[i] == '[')
        {
            var close = WordParser.FindBalanced(raw, i, '[', ']');
            if (close < 0)
            {
                return false;
            }

            index = raw[(i + 1)..close];
            i = close + 1;
        }

        var append = false;
        if (i < raw.Length && raw[i] == '+')
        {
            append = true;
            i++;
        }

        if (i >= raw.Length || raw[i] != '=')
        {
            return false;
        }

        var valueText = raw[(i + 1)..];

        AssignmentValue value = valueText.StartsWith('(') && valueText.EndsWith(')')
            ? new AssignmentValue.Array(ParseArrayElements(valueText[1..^1]))
            : new AssignmentValue.Scalar(WordParser.Parse(valueText));

        assignment = new Assignment(name, value, append, index);
        return true;
    }

    private static List<ArrayElement> ParseArrayElements(string body)
    {
        var elements = new List<ArrayElement>();

        foreach (var piece in SplitWords(body))
        {
            if (piece.StartsWith('[') )
            {
                var close = WordParser.FindBalanced(piece, 0, '[', ']');
                if (close > 0 && close + 1 < piece.Length && piece[close + 1] == '=')
                {
                    elements.Add(new ArrayElement(piece[1..close], WordParser.Parse(piece[(close + 2)..])));
                    continue;
                }
            }

            elements.Add(new ArrayElement(null, WordParser.Parse(piece)));
        }

        return elements;
    }

    /// <summary>
    /// Splits on unquoted whitespace, keeping quoted regions and expansions intact.
    /// </summary>
    /// <remarks>
    /// Expansions must survive whole: <c>a=($(echo x y))</c> has one element at parse
    /// time — the substitution — which only splits into two fields once it has run.
    /// </remarks>
    private static List<string> SplitWords(string text)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(c).Append(text[++i]);
                continue;
            }

            if (c is '\'' or '"')
            {
                var end = text.IndexOf(c, i + 1);
                if (end < 0)
                {
                    end = text.Length - 1;
                }

                current.Append(text[i..(end + 1)]);
                i = end;
                continue;
            }

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end < 0)
                {
                    end = text.Length - 1;
                }

                current.Append(text[i..(end + 1)]);
                i = end;
                continue;
            }

            if (c == '$' && i + 1 < text.Length && text[i + 1] is '(' or '{')
            {
                var open = text[i + 1];
                var close = open == '(' ? ')' : '}';
                var end = WordParser.FindBalanced(text, i + 1, open, close);

                if (end > 0)
                {
                    current.Append(text[i..(end + 1)]);
                    i = end;
                    continue;
                }
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private Redirect ParseRedirect()
    {
        int? explicitFd = null;
        if (Current.Kind == TokenKind.IoNumber)
        {
            explicitFd = int.Parse(Current.Text, System.Globalization.CultureInfo.InvariantCulture);
            _index++;
        }

        var op = Advance();

        var kind = op.Kind switch
        {
            TokenKind.Great => RedirectKind.Output,
            TokenKind.DoubleGreat => RedirectKind.Append,
            TokenKind.Less => RedirectKind.Input,
            TokenKind.DoubleLess or TokenKind.DoubleLessDash => RedirectKind.HereDocument,
            TokenKind.TripleLess => RedirectKind.HereString,
            TokenKind.LessGreat => RedirectKind.ReadWrite,
            TokenKind.GreatAmpersand => RedirectKind.DuplicateOutput,
            TokenKind.LessAmpersand => RedirectKind.DuplicateInput,
            TokenKind.Clobber => RedirectKind.Clobber,
            TokenKind.AmpersandGreat => RedirectKind.OutputBoth,
            TokenKind.AmpersandDoubleGreat => RedirectKind.AppendBoth,
            _ => throw new ParseException($"unsupported redirection `{op.Text}'", op.Start),
        };

        var fd = explicitFd ?? DefaultFd(kind);

        if (Current.Kind != TokenKind.Word)
        {
            throw Unexpected("expected a redirection target");
        }

        var targetToken = Advance();

        if (kind != RedirectKind.HereDocument)
        {
            return new Redirect(kind, fd, WordParser.Parse(targetToken.Text));
        }

        var body = _hereDocumentCursor < _hereDocumentBodies.Count
            ? _hereDocumentBodies[_hereDocumentCursor++]
            : string.Empty;

        return new Redirect(
            kind,
            fd,
            Word.Literal(targetToken.Text),
            body,
            HereDocumentExpands: !Lexer.IsQuotedDelimiter(targetToken.Text));
    }

    private static int DefaultFd(RedirectKind kind) => kind switch
    {
        RedirectKind.Input or RedirectKind.HereDocument or RedirectKind.HereString
            or RedirectKind.DuplicateInput or RedirectKind.ReadWrite => 0,
        _ => 1,
    };

    /// <summary>Splits on a delimiter at nesting depth zero, respecting quotes.</summary>
    private static List<string> SplitTopLevel(string text, char delimiter)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            switch (c)
            {
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case '\'' or '"':
                {
                    var end = text.IndexOf(c, i + 1);
                    i = end < 0 ? text.Length - 1 : end;
                    break;
                }

                default:
                    if (c == delimiter && depth == 0)
                    {
                        result.Add(text[start..i]);
                        start = i + 1;
                    }

                    break;
            }
        }

        result.Add(text[start..]);
        return result;
    }
}
