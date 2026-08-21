using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>bc</c> — an arbitrary-precision calculator language.
/// </summary>
/// <remarks>
/// <para>
/// Decimal rather than binary arithmetic, because that is what <c>bc</c> is for: a shell
/// script reaches for it precisely when <c>$(( ))</c>'s integers are not enough, and
/// <c>scale=2; 100.50 * 1.0825</c> has to give <c>108.79</c> and not a float's near miss.
/// </para>
/// <para>
/// The supported language is a calculator: numbers, variables, the arithmetic and
/// comparison operators, parentheses, and the <c>sqrt</c>, <c>length</c> and <c>scale</c>
/// functions. Statements, loops and user functions are not part of it.
/// </para>
/// </remarks>
public sealed class BcBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "bc";

    /// <inheritdoc />
    public string? LlmHint =>
        "bc: Decimal calculator. Reads expressions from stdin; supports + - * / % ^, "
        + "parentheses, variables, comparisons, scale= and sqrt().";

    /// <inheritdoc />
    public string? Help => "Usage: bc [OPTION]...\nAn arbitrary-precision calculator language.\n";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case "--help":
                    return ValueTask.FromResult(ExecResult.Ok(Help!));

                case "--version" or "-v":
                    return ValueTask.FromResult(ExecResult.Ok("bc (bashkit) 0.1\n"));
            }
        }

        var session = new BcSession();
        var output = new StringBuilder();

        foreach (var line in context.StdinText.Split('\n'))
        {
            context.Budget.ChargeWork(1);

            // A `;` separates statements exactly as a newline does.
            foreach (var statement in line.Split(';'))
            {
                var trimmed = Strip(statement);

                if (trimmed.Length == 0)
                {
                    continue;
                }

                try
                {
                    if (session.Evaluate(trimmed) is { } value)
                    {
                        output.Append(session.Render(value)).Append('\n');
                    }
                }
                catch (BcException exception)
                {
                    return ValueTask.FromResult(new ExecResult
                    {
                        Stdout = StreamData.FromText(output.ToString()),
                        Stderr = StreamData.FromText($"bc: {exception.Message}\n"),
                        ExitCode = ExitCodes.Failure,
                    });
                }
            }
        }

        return ValueTask.FromResult(ExecResult.Ok(output.ToString()));
    }

    /// <summary>Removes a trailing comment and surrounding blanks.</summary>
    private static string Strip(string statement)
    {
        var comment = statement.IndexOf('#', StringComparison.Ordinal);
        return (comment >= 0 ? statement[..comment] : statement).Trim();
    }
}

/// <summary>A <c>bc</c> error, which ends the calculation with a diagnostic.</summary>
/// <param name="message">What went wrong.</param>
internal sealed class BcException(string message) : Exception(message);

/// <summary>
/// One <c>bc</c> session: the variables and the current <c>scale</c>.
/// </summary>
/// <remarks>
/// Values are <see cref="decimal"/>, which is exact for the decimal fractions bc is asked
/// about. That trades bc's unbounded precision for a 28-digit ceiling, which no shell
/// script's arithmetic reaches.
/// </remarks>
internal sealed class BcSession
{
    private readonly Dictionary<string, decimal> _variables = new(StringComparer.Ordinal);
    private string _source = string.Empty;
    private int _position;

    /// <summary>The number of digits kept after the point.</summary>
    public int Scale { get; private set; }

    /// <summary>
    /// Evaluates one statement, returning its value or null when it produced none — an
    /// assignment prints nothing, which is why the return is nullable.
    /// </summary>
    public decimal? Evaluate(string statement)
    {
        _source = statement;
        _position = 0;

        if (TryReadAssignment())
        {
            // An assignment is a complete statement and prints nothing.
            return null;
        }

        var value = ParseExpression();
        SkipSpace();

        if (_position < _source.Length)
        {
            throw new BcException($"syntax error near '{_source[_position..]}'");
        }

        return value;
    }

    /// <summary>Renders a value with the session's scale, as bc prints one.</summary>
    public string Render(decimal value)
    {
        var rounded = Round(value);
        var text = rounded.ToString("0.############################", CultureInfo.InvariantCulture);

        // bc prints a bare `.5` for values below one, without the leading zero.
        return text.StartsWith("0.", StringComparison.Ordinal) ? text[1..]
            : text.StartsWith("-0.", StringComparison.Ordinal) ? "-" + text[2..]
            : text;
    }

    /// <summary>
    /// Handles <c>name = expression</c> and <c>scale = n</c>, returning whether the
    /// statement was one.
    /// </summary>
    private bool TryReadAssignment()
    {
        var save = _position;
        SkipSpace();
        var start = _position;

        while (_position < _source.Length && (char.IsAsciiLetterOrDigit(_source[_position]) || _source[_position] == '_'))
        {
            _position++;
        }

        var name = _source[start.._position];
        SkipSpace();

        // A `==` is a comparison, not an assignment.
        if (name.Length == 0
            || _position >= _source.Length
            || _source[_position] != '='
            || (_position + 1 < _source.Length && _source[_position + 1] == '='))
        {
            _position = save;
            return false;
        }

        _position++;
        var value = ParseExpression();

        if (string.Equals(name, "scale", StringComparison.Ordinal))
        {
            Scale = Math.Clamp((int)value, 0, 28);
            return true;
        }

        _variables[name] = value;
        return true;
    }

    private void SkipSpace()
    {
        while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
        {
            _position++;
        }
    }

    private bool Match(string token)
    {
        SkipSpace();

        if (_position + token.Length > _source.Length
            || string.CompareOrdinal(_source, _position, token, 0, token.Length) != 0)
        {
            return false;
        }

        _position += token.Length;
        return true;
    }

    private decimal ParseExpression() => ParseComparison();

    private decimal ParseComparison()
    {
        var left = ParseAdditive();

        foreach (var op in (string[])["==", "!=", "<=", ">=", "<", ">"])
        {
            if (!Match(op))
            {
                continue;
            }

            var right = ParseAdditive();

            // bc's comparisons yield 1 or 0, which is what makes `if (a < b)` work.
            return (op switch
            {
                "==" => left == right,
                "!=" => left != right,
                "<=" => left <= right,
                ">=" => left >= right,
                "<" => left < right,
                _ => left > right,
            })
                ? 1
                : 0;
        }

        return left;
    }

    private decimal ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (true)
        {
            if (Match("+"))
            {
                left += ParseMultiplicative();
                continue;
            }

            if (Match("-"))
            {
                left -= ParseMultiplicative();
                continue;
            }

            return left;
        }
    }

    private decimal ParseMultiplicative()
    {
        var left = ParsePower();

        while (true)
        {
            if (Match("*"))
            {
                left = Round(left * ParsePower());
                continue;
            }

            if (Match("/"))
            {
                var divisor = ParsePower();

                if (divisor == 0)
                {
                    throw new BcException("divide by zero");
                }

                left = Round(left / divisor);
                continue;
            }

            if (Match("%"))
            {
                var divisor = ParsePower();

                if (divisor == 0)
                {
                    throw new BcException("divide by zero");
                }

                left = Round(left - (divisor * decimal.Truncate(left / divisor)));
                continue;
            }

            return left;
        }
    }

    private decimal ParsePower()
    {
        var left = ParseUnary();

        if (!Match("^"))
        {
            return left;
        }

        // Exponentiation is right-associative, and bc's exponent must be a whole number.
        var exponent = (int)ParsePower();
        var result = 1m;

        for (var i = 0; i < Math.Abs(exponent); i++)
        {
            result *= left;
        }

        return exponent < 0 ? Round(1 / result) : result;
    }

    private decimal ParseUnary()
    {
        if (Match("-"))
        {
            return -ParseUnary();
        }

        if (Match("+"))
        {
            return ParseUnary();
        }

        return ParsePrimary();
    }

    private decimal ParsePrimary()
    {
        SkipSpace();

        if (_position >= _source.Length)
        {
            throw new BcException("syntax error: expression expected");
        }

        if (Match("("))
        {
            var inner = ParseExpression();

            if (!Match(")"))
            {
                throw new BcException("syntax error: expected ')'");
            }

            return inner;
        }

        var c = _source[_position];

        if (char.IsAsciiDigit(c) || c == '.')
        {
            return ParseNumber();
        }

        if (char.IsAsciiLetter(c) || c == '_')
        {
            return ParseNameOrCall();
        }

        throw new BcException($"syntax error near '{_source[_position..]}'");
    }

    private decimal ParseNumber()
    {
        var start = _position;

        while (_position < _source.Length && (char.IsAsciiDigit(_source[_position]) || _source[_position] == '.'))
        {
            _position++;
        }

        return decimal.TryParse(_source[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new BcException($"invalid number '{_source[start.._position]}'");
    }

    private decimal ParseNameOrCall()
    {
        var start = _position;

        while (_position < _source.Length && (char.IsAsciiLetterOrDigit(_source[_position]) || _source[_position] == '_'))
        {
            _position++;
        }

        var name = _source[start.._position];

        if (!Match("("))
        {
            return string.Equals(name, "scale", StringComparison.Ordinal)
                ? Scale
                : _variables.GetValueOrDefault(name);
        }

        var argument = ParseExpression();

        if (!Match(")"))
        {
            throw new BcException("syntax error: expected ')'");
        }

        return name switch
        {
            "sqrt" => argument < 0
                ? throw new BcException("square root of a negative number")
                : Round((decimal)Math.Sqrt((double)argument)),
            "length" => Render(argument).Count(char.IsAsciiDigit),
            "scale" => CountDecimals(argument),
            "abs" => Math.Abs(argument),
            _ => throw new BcException($"function {name} is not defined"),
        };
    }

    private static int CountDecimals(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        var point = text.IndexOf('.', StringComparison.Ordinal);
        return point < 0 ? 0 : text.Length - point - 1;
    }

    /// <summary>
    /// Truncates towards zero at the current scale, which is what bc does — it never
    /// rounds half away.
    /// </summary>
    private decimal Round(decimal value)
    {
        var factor = 1m;

        for (var i = 0; i < Scale; i++)
        {
            factor *= 10;
        }

        return decimal.Truncate(value * factor) / factor;
    }
}
