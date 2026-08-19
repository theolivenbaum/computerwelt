using System.Globalization;
using System.Text;

namespace Bashkit.Builtins.Awk;

/// <summary>
/// AWK's <c>printf</c>/<c>sprintf</c> conversions.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately separate from the shell's own <c>printf</c> builtin. The two look
/// alike but differ where it matters: AWK arguments are already typed, so <c>%d</c> takes
/// the operand's numeric value rather than re-parsing text, <c>%c</c> switches on whether
/// the operand is a number or a string, and a missing operand is an empty string or zero
/// instead of stopping the format.
/// </para>
/// </remarks>
internal static class AwkFormatter
{
    /// <summary>
    /// The largest width or precision a conversion may ask for.
    /// </summary>
    /// <remarks>
    /// A format is attacker-controlled input in an agent sandbox, and <c>%999999999d</c>
    /// would otherwise allocate a gigabyte before producing a single byte. Refusing it is
    /// the only difference from a real awk's formatting, and it is a deliberate one.
    /// </remarks>
    private const int MaxFieldSize = 10000;

    /// <summary>Formats <paramref name="arguments"/> according to <paramref name="format"/>.</summary>
    public static string Format(string format, IReadOnlyList<AwkValue> arguments, string convfmt)
    {
        var builder = new StringBuilder();
        var next = 0;

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];

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

            i = AppendConversion(builder, format, i, arguments, ref next, convfmt);
        }

        return builder.ToString();
    }

    /// <summary>Formats one conversion and returns the index of its final character.</summary>
    private static int AppendConversion(
        StringBuilder builder,
        string format,
        int start,
        IReadOnlyList<AwkValue> arguments,
        ref int next,
        string convfmt)
    {
        var i = start + 1;
        var flags = string.Empty;

        while (i < format.Length && format[i] is '-' or '+' or ' ' or '0' or '#' or '\'')
        {
            flags += format[i++];
        }

        var width = ReadNumberOrStar(format, ref i, arguments, ref next);
        int? precision = null;

        if (i < format.Length && format[i] == '.')
        {
            i++;
            precision = ReadNumberOrStar(format, ref i, arguments, ref next) ?? 0;
        }

        Check(width, "width");
        Check(precision, "precision");

        // Length modifiers are accepted and ignored: awk has one numeric type.
        while (i < format.Length && format[i] is 'h' or 'l' or 'L' or 'q' or 'j' or 'z' or 't')
        {
            i++;
        }

        if (i >= format.Length)
        {
            builder.Append(format, start, format.Length - start);
            return format.Length;
        }

        var conversion = format[i];
        var argument = next < arguments.Count ? arguments[next++] : AwkValue.Uninitialized;

        // A negative width from `*` means left-justify, as C does.
        if (width is < 0)
        {
            flags += '-';
            width = -width;
        }

        builder.Append(Pad(Convert(conversion, argument, precision, flags, convfmt), width, flags, conversion));
        return i;
    }

    private static void Check(int? value, string what)
    {
        if (value is { } size && Math.Abs((long)size) > MaxFieldSize)
        {
            throw new AwkRuntimeException(
                $"awk: format {what} {Math.Abs((long)size)} exceeds maximum ({MaxFieldSize})");
        }
    }

    private static int? ReadNumberOrStar(string format, ref int i, IReadOnlyList<AwkValue> arguments, ref int next)
    {
        if (i < format.Length && format[i] == '*')
        {
            i++;
            var value = next < arguments.Count ? arguments[next++] : AwkValue.Uninitialized;
            return (int)value.ToNumber();
        }

        var start = i;

        while (i < format.Length && char.IsAsciiDigit(format[i]))
        {
            i++;
        }

        if (i == start)
        {
            return null;
        }

        // A width longer than an int is certainly over the cap; saturating keeps the
        // diagnostic honest about the number the format actually asked for.
        var digits = format[start..i];
        return long.TryParse(digits, CultureInfo.InvariantCulture, out var parsed)
            ? (int)Math.Min(parsed, int.MaxValue)
            : int.MaxValue;
    }

    private static string Convert(char conversion, AwkValue argument, int? precision, string flags, string convfmt)
    {
        switch (conversion)
        {
            case 'd' or 'i':
            {
                var number = Truncate(argument.ToNumber());
                var text = Math.Abs(number).ToString(CultureInfo.InvariantCulture);

                if (precision is { } digits)
                {
                    text = text.PadLeft(digits, '0');
                }

                return Sign(number < 0, flags) + text;
            }

            case 'u':
            {
                var number = Truncate(argument.ToNumber());
                var magnitude = unchecked((ulong)number);
                return magnitude.ToString(CultureInfo.InvariantCulture);
            }

            case 'o':
            {
                var text = System.Convert.ToString(Truncate(argument.ToNumber()), 8);
                return flags.Contains('#', StringComparison.Ordinal) && !text.StartsWith('0') ? "0" + text : text;
            }

            case 'x' or 'X':
            {
                var text = System.Convert.ToString(Truncate(argument.ToNumber()), 16);
                text = conversion == 'X' ? text.ToUpperInvariant() : text;
                return flags.Contains('#', StringComparison.Ordinal) && text != "0"
                    ? (conversion == 'X' ? "0X" : "0x") + text
                    : text;
            }

            case 'c':
            {
                // A number — including a field that reads as one — is a character code;
                // a string contributes its first character instead.
                if (argument.Kind is AwkValueKind.Number
                    || (argument.Kind is AwkValueKind.StrNum && argument.IsNumeric))
                {
                    var code = (int)argument.ToNumber();
                    return code is > 0 and <= 0x10FFFF ? char.ConvertFromUtf32(code) : string.Empty;
                }

                var text = argument.ToText(convfmt);
                return text.Length == 0 ? string.Empty : text[..1];
            }

            case 's':
            {
                var text = argument.ToText(convfmt);
                return precision is { } limit && limit < text.Length ? text[..limit] : text;
            }

            case 'e' or 'E':
            {
                var value = argument.ToNumber();
                var text = Math.Abs(value).ToString("e" + (precision ?? 6), CultureInfo.InvariantCulture);
                text = NormalizeExponent(conversion == 'E' ? text.ToUpperInvariant() : text);
                return Sign(value < 0, flags) + text;
            }

            case 'f' or 'F':
            {
                var value = argument.ToNumber();
                var text = Math.Abs(value).ToString("F" + (precision ?? 6), CultureInfo.InvariantCulture);
                return Sign(value < 0, flags) + text;
            }

            case 'g' or 'G':
            {
                var value = argument.ToNumber();
                var text = General(Math.Abs(value), precision ?? 6, flags.Contains('#', StringComparison.Ordinal));
                return Sign(value < 0, flags) + (conversion == 'G' ? text.ToUpperInvariant() : text);
            }

            default:
                return conversion.ToString();
        }
    }

    /// <summary>Renders <c>%g</c>: significant digits, with the exponent form chosen by size.</summary>
    private static string General(double value, int precision, bool keepZeros)
    {
        if (precision == 0)
        {
            precision = 1;
        }

        if (value == 0)
        {
            return keepZeros ? "0." + new string('0', precision - 1) : "0";
        }

        var exponent = (int)Math.Floor(Math.Log10(value));

        // Rounding to `precision` digits can carry into the next power of ten.
        var rounded = double.Parse(
            value.ToString("e" + Math.Max(precision - 1, 0), CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);

        if (rounded != 0)
        {
            exponent = (int)Math.Floor(Math.Log10(rounded));
        }

        if (exponent < -4 || exponent >= precision)
        {
            var text = NormalizeExponent(value.ToString("e" + Math.Max(precision - 1, 0), CultureInfo.InvariantCulture));
            return keepZeros ? text : TrimMantissa(text);
        }

        var fixedText = value.ToString("F" + Math.Max(precision - 1 - exponent, 0), CultureInfo.InvariantCulture);
        return keepZeros ? fixedText : TrimZeros(fixedText);
    }

    private static string TrimMantissa(string text)
    {
        var e = text.IndexOfAny(['e', 'E']);
        return e < 0 ? TrimZeros(text) : TrimZeros(text[..e]) + text[e..];
    }

    private static string TrimZeros(string text) =>
        text.Contains('.', StringComparison.Ordinal) ? text.TrimEnd('0').TrimEnd('.') : text;

    /// <summary>Rewrites .NET's <c>e+006</c> as C's <c>e+06</c>.</summary>
    private static string NormalizeExponent(string text)
    {
        var index = text.IndexOfAny(['e', 'E']);

        if (index < 0 || index + 2 >= text.Length)
        {
            return text;
        }

        var sign = text[index + 1];
        var digits = text[(index + 2)..].TrimStart('0');

        if (digits.Length < 2)
        {
            digits = digits.PadLeft(2, '0');
        }

        return text[..(index + 1)] + sign + digits;
    }

    private static long Truncate(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        var truncated = Math.Truncate(value);
        return truncated switch
        {
            >= 9.2233720368547758e18 => long.MaxValue,
            <= -9.2233720368547758e18 => long.MinValue,
            _ => (long)truncated,
        };
    }

    private static string Sign(bool negative, string flags)
    {
        if (negative)
        {
            return "-";
        }

        if (flags.Contains('+', StringComparison.Ordinal))
        {
            return "+";
        }

        return flags.Contains(' ', StringComparison.Ordinal) ? " " : string.Empty;
    }

    private static string Pad(string text, int? width, string flags, char conversion)
    {
        if (width is not { } target || text.Length >= target)
        {
            return text;
        }

        if (flags.Contains('-', StringComparison.Ordinal))
        {
            return text.PadRight(target);
        }

        // Zero-padding goes after the sign, and never applies to strings.
        if (flags.Contains('0', StringComparison.Ordinal) && conversion is not ('s' or 'c'))
        {
            var prefix = text.Length > 0 && text[0] is '-' or '+' or ' ' ? text[..1] : string.Empty;
            return prefix + text[prefix.Length..].PadLeft(target - prefix.Length, '0');
        }

        return text.PadLeft(target);
    }
}
