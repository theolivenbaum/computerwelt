using System.Globalization;
using System.Text.RegularExpressions;

namespace Bashkit.Builtins;

/// <summary>
/// <c>expr</c> — evaluates an expression given as separate arguments.
/// </summary>
/// <remarks>
/// <c>expr</c> is not the shell's arithmetic: its operands arrive already split into
/// arguments, it mixes string and integer operators, and its exit status encodes the
/// result (0 for a non-empty, non-zero value; 1 otherwise; 2 for a usage error). It
/// therefore needs its own evaluator rather than a call into
/// <see cref="Interpreter.ArithmeticEvaluator"/>.
/// </remarks>
public sealed class ExprBuiltin : IBuiltin
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public string Name => "expr";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage("expr", "missing operand"));
        }

        try
        {
            var parser = new ExprParser(context.Arguments);
            var value = parser.ParseOr();

            if (!parser.AtEnd)
            {
                return ValueTask.FromResult(ExecResult.Usage("expr", "syntax error"));
            }

            var text = value ?? string.Empty;
            var falsy = text.Length == 0 || text == "0";

            return ValueTask.FromResult(new ExecResult
            {
                Stdout = StreamData.FromText(text + "\n"),
                ExitCode = falsy ? 1 : 0,
            });
        }
        catch (DivideByZeroException)
        {
            return ValueTask.FromResult(ExecResult.Usage("expr", "division by zero", ExitCodes.Usage));
        }
        catch (FormatException e)
        {
            return ValueTask.FromResult(ExecResult.Usage("expr", e.Message));
        }
    }

    /// <summary>Recursive-descent evaluator over pre-split argument tokens.</summary>
    private sealed class ExprParser(IReadOnlyList<string> tokens)
    {
        private int _index;

        public bool AtEnd => _index >= tokens.Count;

        private string? Current => _index < tokens.Count ? tokens[_index] : null;

        private bool Match(string token)
        {
            if (Current != token)
            {
                return false;
            }

            _index++;
            return true;
        }

        public string ParseOr()
        {
            var left = ParseAnd();
            while (Match("|"))
            {
                var right = ParseAnd();
                // `|` yields its left operand when that is neither empty nor zero.
                left = IsTruthy(left) ? left : right;
            }

            return left;
        }

        private string ParseAnd()
        {
            var left = ParseComparison();
            while (Match("&"))
            {
                var right = ParseComparison();
                left = IsTruthy(left) && IsTruthy(right) ? left : "0";
            }

            return left;
        }

        private string ParseComparison()
        {
            var left = ParseAdditive();

            while (Current is "=" or "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                var op = tokens[_index++];
                var right = ParseAdditive();
                left = Compare(left, op, right) ? "1" : "0";
            }

            return left;
        }

        private string ParseAdditive()
        {
            var left = ParseMultiplicative();

            while (Current is "+" or "-")
            {
                var op = tokens[_index++];
                var right = ParseMultiplicative();
                var value = op == "+" ? ToNumber(left) + ToNumber(right) : ToNumber(left) - ToNumber(right);
                left = value.ToString(CultureInfo.InvariantCulture);
            }

            return left;
        }

        private string ParseMultiplicative()
        {
            var left = ParseMatch();

            while (Current is "*" or "/" or "%")
            {
                var op = tokens[_index++];
                var right = ParseMatch();
                var a = ToNumber(left);
                var b = ToNumber(right);

                var value = op switch
                {
                    "*" => a * b,
                    "/" => b == 0 ? throw new DivideByZeroException() : a / b,
                    _ => b == 0 ? throw new DivideByZeroException() : a % b,
                };

                left = value.ToString(CultureInfo.InvariantCulture);
            }

            return left;
        }

        private string ParseMatch()
        {
            var left = ParsePrimary();

            while (Match(":"))
            {
                var pattern = ParsePrimary();
                left = MatchAnchored(left, pattern);
            }

            return left;
        }

        private string ParsePrimary()
        {
            if (Match("("))
            {
                var value = ParseOr();
                if (!Match(")"))
                {
                    throw new FormatException("syntax error: expecting ')'");
                }

                return value;
            }

            // The keyword forms, which exist so that a value like `+` can be used literally.
            if (Match("length"))
            {
                return ParsePrimary().Length.ToString(CultureInfo.InvariantCulture);
            }

            if (Match("substr"))
            {
                var text = ParsePrimary();
                var offset = (int)ToNumber(ParsePrimary());
                var length = (int)ToNumber(ParsePrimary());

                if (offset < 1 || offset > text.Length || length < 1)
                {
                    return string.Empty;
                }

                return text.Substring(offset - 1, Math.Min(length, text.Length - offset + 1));
            }

            if (Match("index"))
            {
                var text = ParsePrimary();
                var characters = ParsePrimary();
                var index = text.IndexOfAny(characters.ToCharArray());
                return (index + 1).ToString(CultureInfo.InvariantCulture);
            }

            if (Match("match"))
            {
                var text = ParsePrimary();
                var pattern = ParsePrimary();
                return MatchAnchored(text, pattern);
            }

            if (Match("+"))
            {
                // `expr + length` treats the next token as a plain string.
                return Current is null ? throw new FormatException("syntax error") : tokens[_index++];
            }

            if (Current is null)
            {
                throw new FormatException("syntax error: unexpected end of expression");
            }

            return tokens[_index++];
        }

        /// <summary>
        /// Applies a BRE anchored at the start. With a capture group the match returns the
        /// group; without one it returns the match length, and 0 on failure.
        /// </summary>
        private static string MatchAnchored(string text, string pattern)
        {
            try
            {
                var translated = BasicRegex.Translate(pattern);
                var match = Regex.Match(text, "^(?:" + translated + ")", RegexOptions.None, RegexTimeout);

                if (!match.Success)
                {
                    return pattern.Contains("\\(", StringComparison.Ordinal) ? string.Empty : "0";
                }

                return match.Groups.Count > 1
                    ? match.Groups[1].Value
                    : match.Length.ToString(CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                throw new FormatException($"invalid regular expression: {pattern}");
            }
            catch (RegexMatchTimeoutException)
            {
                return "0";
            }
        }

        private static bool Compare(string left, string op, string right)
        {
            // Two numeric operands compare numerically; anything else compares as strings.
            if (TryNumber(left, out var a) && TryNumber(right, out var b))
            {
                return op switch
                {
                    "=" or "==" => a == b,
                    "!=" => a != b,
                    "<" => a < b,
                    "<=" => a <= b,
                    ">" => a > b,
                    ">=" => a >= b,
                    _ => false,
                };
            }

            var comparison = string.CompareOrdinal(left, right);

            return op switch
            {
                "=" or "==" => comparison == 0,
                "!=" => comparison != 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                _ => false,
            };
        }

        private static bool IsTruthy(string value) => value.Length > 0 && value != "0";

        private static long ToNumber(string value) =>
            TryNumber(value, out var number) ? number : throw new FormatException("non-integer argument");

        private static bool TryNumber(string value, out long number) =>
            long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
    }
}

/// <summary>
/// Translates POSIX basic regular expressions into .NET syntax.
/// </summary>
internal static class BasicRegex
{
    /// <summary>Rewrites a BRE for .NET.</summary>
    public static string Translate(string pattern) => PosixRegex.TranslateBasic(pattern);
}
