using System.Globalization;
using System.Text;

namespace Bashkit.Builtins;

/// <summary>
/// <c>printf</c> — formats and prints its arguments.
/// </summary>
/// <remarks>
/// The format string is reused until every argument is consumed, which is what makes
/// <c>printf '%s\n' a b c</c> print three lines. Conversions are implemented directly
/// rather than delegated to <see cref="string.Format(string, object?[])"/> because the C
/// specifiers, the shell's <c>%b</c> and <c>%q</c>, and the "missing argument means zero or
/// empty" rule have no .NET equivalent.
/// </remarks>
public sealed class PrintfBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "printf";

    /// <inheritdoc />
    public string? LlmHint => "printf: Formats output. Supports %s %d %i %f %x %o %c %b %q, width/precision flags.";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage("printf", "usage: printf format [arguments]"));
        }

        var index = 0;
        string? variableName = null;

        if (context.Arguments[0] == "-v" && context.Arguments.Count > 2)
        {
            variableName = context.Arguments[1];
            index = 2;
        }

        var format = context.Arguments[index++];
        var arguments = context.Arguments.Skip(index).ToList();
        var builder = new StringBuilder();

        var consumed = 0;
        do
        {
            var before = consumed;
            FormatOnce(builder, format, arguments, ref consumed);

            // Stop when a pass consumes nothing, or the format would repeat forever.
            if (consumed == before)
            {
                break;
            }
        }
        while (consumed < arguments.Count);

        var text = builder.ToString();

        if (variableName is not null)
        {
            context.State.Set(variableName, text);
            return ValueTask.FromResult(ExecResult.Success);
        }

        return ValueTask.FromResult(ExecResult.Ok(text));
    }

    private static void FormatOnce(StringBuilder builder, string format, List<string> arguments, ref int consumed)
    {
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];

            if (c == '\\' && i + 1 < format.Length)
            {
                i += AppendEscape(builder, format, i);
                continue;
            }

            if (c != '%')
            {
                builder.Append(c);
                continue;
            }

            if (i + 1 < format.Length && format[i + 1] == '%')
            {
                builder.Append('%');
                i++;
                continue;
            }

            i += AppendConversion(builder, format, i, arguments, ref consumed);
        }
    }

    /// <summary>Appends one escape sequence and returns how many extra characters it consumed.</summary>
    private static int AppendEscape(StringBuilder builder, string format, int index)
    {
        var c = format[index + 1];

        switch (c)
        {
            case 'a': builder.Append('\a'); return 1;
            case 'b': builder.Append('\b'); return 1;
            case 'e': builder.Append('\x1b'); return 1;
            case 'f': builder.Append('\f'); return 1;
            case 'n': builder.Append('\n'); return 1;
            case 'r': builder.Append('\r'); return 1;
            case 't': builder.Append('\t'); return 1;
            case 'v': builder.Append('\v'); return 1;
            case '\\': builder.Append('\\'); return 1;
            case '"': builder.Append('"'); return 1;

            case >= '0' and <= '7':
            {
                var digits = 0;
                var value = 0;
                var offset = index + 1;
                while (digits < 3 && offset < format.Length && format[offset] is >= '0' and <= '7')
                {
                    value = (value * 8) + (format[offset++] - '0');
                    digits++;
                }

                builder.Append((char)value);
                return digits;
            }

            // `\uXXXX` and `\UXXXXXXXX` name a codepoint, which is how a script emits a
            // character it cannot type.
            case 'u' or 'U':
            {
                var limit = c == 'u' ? 4 : 8;
                var digits = 0;
                var value = 0;
                var offset = index + 2;

                while (digits < limit && offset < format.Length && Uri.IsHexDigit(format[offset]))
                {
                    value = (value * 16) + Convert.ToInt32(format[offset++].ToString(), 16);
                    digits++;
                }

                if (digits == 0)
                {
                    builder.Append('\\').Append(c);
                    return 1;
                }

                builder.Append(value <= 0x10FFFF ? char.ConvertFromUtf32(value) : string.Empty);
                return digits + 1;
            }

            case 'x':
            {
                var digits = 0;
                var value = 0;
                var offset = index + 2;
                while (digits < 2 && offset < format.Length && Uri.IsHexDigit(format[offset]))
                {
                    value = (value * 16) + System.Convert.ToInt32(format[offset++].ToString(), 16);
                    digits++;
                }

                if (digits == 0)
                {
                    builder.Append("\\x");
                    return 1;
                }

                builder.Append((char)value);
                return digits + 1;
            }

            default:
                builder.Append('\\').Append(c);
                return 1;
        }
    }

    /// <summary>Appends one conversion and returns how many extra characters it consumed.</summary>
    private static int AppendConversion(StringBuilder builder, string format, int start, List<string> arguments, ref int consumed)
    {
        var i = start + 1;
        var flags = new StringBuilder();

        while (i < format.Length && format[i] is '-' or '+' or ' ' or '#' or '0')
        {
            flags.Append(format[i++]);
        }

        var width = ReadNumberOrStar(format, ref i, arguments, ref consumed);
        int? precision = null;

        if (i < format.Length && format[i] == '.')
        {
            i++;
            precision = ReadNumberOrStar(format, ref i, arguments, ref consumed) ?? 0;
        }

        // Length modifiers carry no meaning here; every integer is 64-bit.
        while (i < format.Length && format[i] is 'l' or 'h' or 'q' or 'j' or 'z' or 't' or 'L')
        {
            i++;
        }

        if (i >= format.Length)
        {
            builder.Append(format[start..]);
            return format.Length - start - 1;
        }

        var conversion = format[i];
        var argument = consumed < arguments.Count ? arguments[consumed++] : string.Empty;
        var text = ApplyConversion(conversion, argument, precision, flags.ToString());

        builder.Append(Pad(text, width, flags.ToString(), conversion));
        return i - start;
    }

    private static int? ReadNumberOrStar(string format, ref int i, List<string> arguments, ref int consumed)
    {
        if (i < format.Length && format[i] == '*')
        {
            i++;
            var value = consumed < arguments.Count ? arguments[consumed++] : "0";
            return int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        var start = i;
        while (i < format.Length && char.IsAsciiDigit(format[i]))
        {
            i++;
        }

        return i > start ? int.Parse(format[start..i], CultureInfo.InvariantCulture) : null;
    }

    private static string ApplyConversion(char conversion, string argument, int? precision, string flags)
    {
        switch (conversion)
        {
            case 's':
                return precision is { } p && p < argument.Length ? argument[..p] : argument;

            case 'b':
            {
                var builder = new StringBuilder();
                for (var i = 0; i < argument.Length; i++)
                {
                    if (argument[i] == '\\' && i + 1 < argument.Length)
                    {
                        i += AppendEscape(builder, argument, i);
                        continue;
                    }

                    builder.Append(argument[i]);
                }

                return builder.ToString();
            }

            case 'q':
                return Interpreter.Expander.Quote(argument);

            case 'd' or 'i':
            {
                var value = ParseInteger(argument);
                var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
                if (precision is { } digits)
                {
                    text = text.PadLeft(digits, '0');
                }

                if (value < 0)
                {
                    return "-" + text;
                }

                return flags.Contains('+', StringComparison.Ordinal) ? "+" + text
                    : flags.Contains(' ', StringComparison.Ordinal) ? " " + text
                    : text;
            }

            case 'u':
                return ((ulong)ParseInteger(argument)).ToString(CultureInfo.InvariantCulture);

            case 'x':
                return ParseInteger(argument).ToString("x", CultureInfo.InvariantCulture);

            case 'X':
                return ParseInteger(argument).ToString("X", CultureInfo.InvariantCulture);

            case 'o':
                return System.Convert.ToString(ParseInteger(argument), 8);

            case 'c':
                return argument.Length > 0 ? argument[0].ToString() : string.Empty;

            case 'f' or 'F':
                return ParseDouble(argument).ToString("F" + (precision ?? 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

            case 'e' or 'E':
            {
                var text = ParseDouble(argument).ToString(
                    (conversion == 'e' ? "e" : "E") + (precision ?? 6).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture);
                // .NET writes a three-digit exponent; C writes two.
                return NormalizeExponent(text);
            }

            case 'g' or 'G':
                return ParseDouble(argument).ToString(
                    (conversion == 'g' ? "g" : "G") + (precision ?? 6).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture);

            default:
                return argument;
        }
    }

    private static string NormalizeExponent(string text)
    {
        var index = text.IndexOfAny(['e', 'E']);
        if (index < 0 || index + 2 >= text.Length)
        {
            return text;
        }

        var mantissa = text[..(index + 2)];
        var digits = text[(index + 2)..].TrimStart('0');
        return mantissa + (digits.Length < 2 ? digits.PadLeft(2, '0') : digits);
    }

    private static string Pad(string text, int? width, string flags, char conversion)
    {
        if (width is not { } size || text.Length >= size)
        {
            return text;
        }

        if (flags.Contains('-', StringComparison.Ordinal))
        {
            return text.PadRight(size);
        }

        // Zero padding applies to numbers only, and must go after any sign.
        if (flags.Contains('0', StringComparison.Ordinal) && conversion is 'd' or 'i' or 'f' or 'F' or 'x' or 'X' or 'o' or 'u' or 'e' or 'E' or 'g' or 'G')
        {
            if (text.StartsWith('-') || text.StartsWith('+'))
            {
                return text[0] + text[1..].PadLeft(size - 1, '0');
            }

            return text.PadLeft(size, '0');
        }

        return text.PadLeft(size);
    }

    /// <summary>
    /// Parses an integer operand. A leading <c>'</c> or <c>"</c> means "the code point of the
    /// next character", which scripts use for character arithmetic.
    /// </summary>
    private static long ParseInteger(string text)
    {
        if (text.Length > 1 && text[0] is '\'' or '"')
        {
            return text[1];
        }

        var trimmed = text.Trim();

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return System.Convert.ToInt64(trimmed[2..], 16);
        }

        return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
