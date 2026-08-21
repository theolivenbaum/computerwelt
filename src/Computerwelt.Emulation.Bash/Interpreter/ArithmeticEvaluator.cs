using System.Globalization;

namespace Computerwelt.Emulation.Bash.Interpreter;

/// <summary>
/// Evaluates shell arithmetic: <c>$(( ))</c>, <c>(( ))</c>, array subscripts and
/// <c>declare -i</c> assignments.
/// </summary>
/// <remarks>
/// <para>
/// Shell arithmetic is C's integer arithmetic with C's precedence, on 64-bit signed
/// values, plus one shell-specific rule: a bare identifier is read as a variable, and an
/// empty or unset variable is 0. Recursion is bounded because a variable's value is itself
/// evaluated as an expression, so <c>a=b; b=a</c> would otherwise loop forever.
/// </para>
/// <para>
/// Division by zero is a shell error, not a .NET exception, so it surfaces as a
/// <see cref="ShellArithmeticException"/> carrying bash's own message.
/// </para>
/// </remarks>
public sealed class ArithmeticEvaluator
{
    private const int MaxVariableRecursion = 32;

    private readonly ShellState _state;
    private readonly string _text;
    private int _position;
    private int _variableDepth;

    private ArithmeticEvaluator(ShellState state, string text, int variableDepth)
    {
        _state = state;
        _text = text;
        _variableDepth = variableDepth;
    }

    /// <summary>Evaluates <paramref name="expression"/> against <paramref name="state"/>.</summary>
    public static long Evaluate(ShellState state, string expression) =>
        EvaluateInternal(state, expression, 0);

    private static long EvaluateInternal(ShellState state, string expression, int depth)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return 0;
        }

        var evaluator = new ArithmeticEvaluator(state, expression, depth);
        var value = evaluator.ParseCommaExpression();
        evaluator.SkipWhitespace();

        if (evaluator._position < evaluator._text.Length)
        {
            throw new ShellArithmeticException(
                $"{expression}: syntax error in expression (error token is \"{expression[evaluator._position..]}\")");
        }

        return value;
    }

    private char Current => _position < _text.Length ? _text[_position] : '\0';

    private char Peek(int offset = 1) => _position + offset < _text.Length ? _text[_position + offset] : '\0';

    private void SkipWhitespace()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }
    }

    private bool TryConsume(string op)
    {
        SkipWhitespace();
        if (_position + op.Length > _text.Length || !_text.AsSpan(_position, op.Length).SequenceEqual(op))
        {
            return false;
        }

        // `+` must not swallow the `+` of `++`, and `=` must not swallow `==`.
        if (op is "+" or "-" && Peek(op.Length) == op[0])
        {
            return false;
        }

        if (op == "=" && Peek(1) == '=')
        {
            return false;
        }

        _position += op.Length;
        return true;
    }

    private long ParseCommaExpression()
    {
        var value = ParseAssignment();
        while (TryConsume(","))
        {
            value = ParseAssignment();
        }

        return value;
    }

    private long ParseAssignment()
    {
        var save = _position;
        SkipWhitespace();

        var name = TryReadIdentifier();
        if (name is not null)
        {
            SkipWhitespace();

            foreach (var op in AssignmentOperators)
            {
                if (!TryConsume(op))
                {
                    continue;
                }

                var operand = ParseAssignment();
                var current = op == "=" ? 0 : ReadVariable(name);
                var result = op switch
                {
                    "=" => operand,
                    "+=" => current + operand,
                    "-=" => current - operand,
                    "*=" => current * operand,
                    "/=" => Divide(current, operand),
                    "%=" => Modulo(current, operand),
                    "<<=" => current << (int)(operand & 63),
                    ">>=" => current >> (int)(operand & 63),
                    "&=" => current & operand,
                    "|=" => current | operand,
                    "^=" => current ^ operand,
                    _ => operand,
                };

                _state.Set(name, result.ToString(CultureInfo.InvariantCulture));
                return result;
            }
        }

        _position = save;
        return ParseTernary();
    }

    // Longest first, so `<<=` is not read as `<` then `<=`.
    private static readonly string[] AssignmentOperators =
        ["<<=", ">>=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "="];

    private long ParseTernary()
    {
        var condition = ParseLogicalOr();
        if (!TryConsume("?"))
        {
            return condition;
        }

        var whenTrue = ParseAssignment();
        if (!TryConsume(":"))
        {
            throw new ShellArithmeticException("expected `:' in conditional expression");
        }

        var whenFalse = ParseAssignment();
        return condition != 0 ? whenTrue : whenFalse;
    }

    private long ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (TryConsume("||"))
        {
            var right = ParseLogicalAnd();
            left = (left != 0 || right != 0) ? 1 : 0;
        }

        return left;
    }

    private long ParseLogicalAnd()
    {
        var left = ParseBitwiseOr();
        while (TryConsume("&&"))
        {
            var right = ParseBitwiseOr();
            left = (left != 0 && right != 0) ? 1 : 0;
        }

        return left;
    }

    private long ParseBitwiseOr()
    {
        var left = ParseBitwiseXor();
        while (true)
        {
            SkipWhitespace();
            if (Current != '|' || Peek() == '|')
            {
                return left;
            }

            _position++;
            left |= ParseBitwiseXor();
        }
    }

    private long ParseBitwiseXor()
    {
        var left = ParseBitwiseAnd();
        while (TryConsume("^"))
        {
            left ^= ParseBitwiseAnd();
        }

        return left;
    }

    private long ParseBitwiseAnd()
    {
        var left = ParseEquality();
        while (true)
        {
            SkipWhitespace();
            if (Current != '&' || Peek() == '&')
            {
                return left;
            }

            _position++;
            left &= ParseEquality();
        }
    }

    private long ParseEquality()
    {
        var left = ParseRelational();
        while (true)
        {
            if (TryConsume("=="))
            {
                left = left == ParseRelational() ? 1 : 0;
            }
            else if (TryConsume("!="))
            {
                left = left != ParseRelational() ? 1 : 0;
            }
            else
            {
                return left;
            }
        }
    }

    private long ParseRelational()
    {
        var left = ParseShift();
        while (true)
        {
            if (TryConsume("<="))
            {
                left = left <= ParseShift() ? 1 : 0;
            }
            else if (TryConsume(">="))
            {
                left = left >= ParseShift() ? 1 : 0;
            }
            else
            {
                SkipWhitespace();
                if (Current == '<' && Peek() != '<')
                {
                    _position++;
                    left = left < ParseShift() ? 1 : 0;
                }
                else if (Current == '>' && Peek() != '>')
                {
                    _position++;
                    left = left > ParseShift() ? 1 : 0;
                }
                else
                {
                    return left;
                }
            }
        }
    }

    private long ParseShift()
    {
        var left = ParseAdditive();
        while (true)
        {
            SkipWhitespace();
            if (Current == '<' && Peek() == '<' && Peek(2) != '=')
            {
                _position += 2;
                left <<= (int)(ParseAdditive() & 63);
            }
            else if (Current == '>' && Peek() == '>' && Peek(2) != '=')
            {
                _position += 2;
                left >>= (int)(ParseAdditive() & 63);
            }
            else
            {
                return left;
            }
        }
    }

    private long ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            SkipWhitespace();
            if (Current == '+' && Peek() != '+' && Peek() != '=')
            {
                _position++;
                left += ParseMultiplicative();
            }
            else if (Current == '-' && Peek() != '-' && Peek() != '=')
            {
                _position++;
                left -= ParseMultiplicative();
            }
            else
            {
                return left;
            }
        }
    }

    private long ParseMultiplicative()
    {
        var left = ParsePower();
        while (true)
        {
            SkipWhitespace();
            if (Current == '*' && Peek() != '*' && Peek() != '=')
            {
                _position++;
                left *= ParsePower();
            }
            else if (Current == '/' && Peek() != '=')
            {
                _position++;
                left = Divide(left, ParsePower());
            }
            else if (Current == '%' && Peek() != '=')
            {
                _position++;
                left = Modulo(left, ParsePower());
            }
            else
            {
                return left;
            }
        }
    }

    private long ParsePower()
    {
        var left = ParseUnary();
        SkipWhitespace();

        if (Current == '*' && Peek() == '*')
        {
            _position += 2;
            // Exponentiation is right-associative: 2**3**2 is 2**9.
            var exponent = ParsePower();
            return Power(left, exponent);
        }

        return left;
    }

    private long ParseUnary()
    {
        SkipWhitespace();

        switch (Current)
        {
            case '+' when Peek() == '+':
                _position += 2;
                return PreIncrement(1);

            case '-' when Peek() == '-':
                _position += 2;
                return PreIncrement(-1);

            case '+':
                _position++;
                return ParseUnary();

            case '-':
                _position++;
                return -ParseUnary();

            case '!':
                _position++;
                return ParseUnary() == 0 ? 1 : 0;

            case '~':
                _position++;
                return ~ParseUnary();

            default:
                return ParsePostfix();
        }
    }

    private long PreIncrement(int delta)
    {
        SkipWhitespace();
        var name = TryReadIdentifier() ?? throw new ShellArithmeticException("expected a variable after increment operator");
        var value = ReadVariable(name) + delta;
        _state.Set(name, value.ToString(CultureInfo.InvariantCulture));
        return value;
    }

    private long ParsePostfix()
    {
        SkipWhitespace();

        if (Current == '(')
        {
            _position++;
            var value = ParseCommaExpression();
            SkipWhitespace();
            if (Current != ')')
            {
                throw new ShellArithmeticException("expected `)'");
            }

            _position++;
            return value;
        }

        if (char.IsAsciiDigit(Current))
        {
            return ParseNumber();
        }

        var save = _position;
        var name = TryReadIdentifier();
        if (name is null)
        {
            throw new ShellArithmeticException(
                $"{_text}: syntax error in expression (error token is \"{_text[Math.Min(save, _text.Length)..]}\")");
        }

        string? subscript = null;
        if (Current == '[')
        {
            var close = Parsing.WordParser.FindBalanced(_text, _position, '[', ']');
            if (close > 0)
            {
                subscript = _text[(_position + 1)..close];
                _position = close + 1;
            }
        }

        SkipWhitespace();

        if (Current == '+' && Peek() == '+')
        {
            _position += 2;
            var before = ReadVariable(name, subscript);
            WriteVariable(name, subscript, before + 1);
            return before;
        }

        if (Current == '-' && Peek() == '-')
        {
            _position += 2;
            var before = ReadVariable(name, subscript);
            WriteVariable(name, subscript, before - 1);
            return before;
        }

        return ReadVariable(name, subscript);
    }

    /// <summary>
    /// Parses a literal in any of the bases the shell accepts: <c>0x</c> hex, a leading
    /// <c>0</c> for octal, and <c>base#digits</c> for bases 2..64.
    /// </summary>
    private long ParseNumber()
    {
        var start = _position;

        while (_position < _text.Length && char.IsAsciiLetterOrDigit(_text[_position]))
        {
            _position++;
        }

        // `base#digits`
        if (Current == '#')
        {
            var baseText = _text[start.._position];
            _position++;
            var digitStart = _position;
            while (_position < _text.Length && (char.IsAsciiLetterOrDigit(_text[_position]) || _text[_position] is '@' or '_'))
            {
                _position++;
            }

            return ParseInBase(baseText, _text[digitStart.._position]);
        }

        var token = _text[start.._position];

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToInt64(token[2..], 16);
        }

        if (token.Length > 1 && token[0] == '0' && token.All(static c => c is >= '0' and <= '7'))
        {
            return Convert.ToInt64(token[1..], 8);
        }

        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new ShellArithmeticException($"{token}: value too great for base");
    }

    private static long ParseInBase(string baseText, string digits)
    {
        if (!int.TryParse(baseText, CultureInfo.InvariantCulture, out var radix) || radix is < 2 or > 64)
        {
            throw new ShellArithmeticException($"{baseText}: invalid arithmetic base");
        }

        long result = 0;
        foreach (var c in digits)
        {
            var digit = DigitValue(c);
            if (digit < 0 || digit >= radix)
            {
                throw new ShellArithmeticException($"{digits}: value too great for base");
            }

            result = (result * radix) + digit;
        }

        return result;
    }

    // Bases above 36 use bash's ordering: digits, lower case, upper case, then @ and _.
    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'z' => c - 'a' + 10,
        >= 'A' and <= 'Z' => c - 'A' + 36,
        '@' => 62,
        '_' => 63,
        _ => -1,
    };

    private string? TryReadIdentifier()
    {
        SkipWhitespace();
        if (!char.IsAsciiLetter(Current) && Current != '_')
        {
            return null;
        }

        var start = _position;
        while (_position < _text.Length && (char.IsAsciiLetterOrDigit(_text[_position]) || _text[_position] == '_'))
        {
            _position++;
        }

        return _text[start.._position];
    }

    /// <summary>
    /// Reads a variable as a number. Its textual value is itself an arithmetic expression,
    /// so <c>a=1+1; echo $((a))</c> gives 2 — bounded to stop mutually referential values.
    /// </summary>
    private long ReadVariable(string name, string? subscript = null)
    {
        if (_variableDepth >= MaxVariableRecursion)
        {
            throw new ShellArithmeticException($"{name}: expression recursion level exceeded");
        }

        string? text;
        if (subscript is null)
        {
            text = _state.Lookup(name)?.Value;
        }
        else
        {
            var variable = _state.Lookup(name);
            var key = variable is { IsAssociative: true }
                ? subscript
                : EvaluateInternal(_state, subscript, _variableDepth + 1).ToString(CultureInfo.InvariantCulture);
            text = variable?.GetElement(key);
        }

        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct))
        {
            return direct;
        }

        return EvaluateInternal(_state, text, _variableDepth + 1);
    }

    private void WriteVariable(string name, string? subscript, long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);

        if (subscript is null)
        {
            _state.Set(name, text);
            return;
        }

        var variable = _state.GetOrCreate(name);
        if (variable.IsAssociative)
        {
            variable.SetAssociative(subscript, text);
            return;
        }

        variable.SetIndexed(EvaluateInternal(_state, subscript, _variableDepth + 1), text);
    }

    private static long Divide(long left, long right) =>
        right == 0 ? throw new ShellArithmeticException("division by 0") : left / right;

    private static long Modulo(long left, long right) =>
        right == 0 ? throw new ShellArithmeticException("division by 0") : left % right;

    private static long Power(long baseValue, long exponent)
    {
        if (exponent < 0)
        {
            // Integer arithmetic only: bash yields 0 for a negative exponent unless the
            // base is 1 or -1.
            return baseValue switch
            {
                1 => 1,
                -1 => exponent % 2 == 0 ? 1 : -1,
                _ => 0,
            };
        }

        long result = 1;
        while (exponent > 0)
        {
            if ((exponent & 1) == 1)
            {
                result *= baseValue;
            }

            baseValue *= baseValue;
            exponent >>= 1;
        }

        return result;
    }
}

/// <summary>An error in an arithmetic expression, reported the way bash reports it.</summary>
public sealed class ShellArithmeticException : ShellException
{
    /// <summary>Creates an arithmetic error.</summary>
    public ShellArithmeticException(string message) : base(ShellErrorKind.Internal, message)
    {
    }

    /// <inheritdoc />
    public override int ExitCode => ExitCodes.Failure;
}
