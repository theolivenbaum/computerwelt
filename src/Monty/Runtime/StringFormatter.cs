using System.Globalization;
using System.Numerics;
using System.Text;

namespace Monty.Runtime;

/// <summary>
/// Implements Python's two formatting languages: <c>%</c> interpolation and the
/// <c>format</c> mini-language used by <c>str.format</c> and f-strings.
/// </summary>
/// <remarks>
/// Neither maps onto .NET format strings closely enough to delegate: <c>{:&gt;10}</c> means
/// right-align in Python and nothing in .NET, <c>{:,}</c> differs in grouping rules, and
/// <c>%r</c> has no counterpart at all.
/// </remarks>
public static class StringFormatter
{
    /// <summary>Applies <c>%</c> interpolation.</summary>
    public static string PercentFormat(string format, PyObject value)
    {
        // A tuple supplies several values; a mapping supplies named ones; anything else is
        // a single value.
        var values = value is PyTuple tuple ? tuple.Items : [value];
        var mapping = value as PyDict;

        var builder = new StringBuilder(format.Length);
        var next = 0;

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
            {
                builder.Append(format[i]);
                continue;
            }

            if (i + 1 < format.Length && format[i + 1] == '%')
            {
                builder.Append('%');
                i++;
                continue;
            }

            i++;

            string? key = null;
            if (i < format.Length && format[i] == '(')
            {
                var close = format.IndexOf(')', i);

                if (close < 0)
                {
                    throw new PyRaise(PyErrors.ValueError("incomplete format key"));
                }

                key = format[(i + 1)..close];
                i = close + 1;
            }

            var flags = new StringBuilder();
            while (i < format.Length && format[i] is '-' or '+' or ' ' or '#' or '0')
            {
                flags.Append(format[i++]);
            }

            var width = 0;
            while (i < format.Length && char.IsAsciiDigit(format[i]))
            {
                width = (width * 10) + (format[i++] - '0');
            }

            int? precision = null;
            if (i < format.Length && format[i] == '.')
            {
                i++;
                precision = 0;
                while (i < format.Length && char.IsAsciiDigit(format[i]))
                {
                    precision = (precision * 10) + (format[i++] - '0');
                }
            }

            while (i < format.Length && format[i] is 'l' or 'h' or 'L')
            {
                i++;
            }

            if (i >= format.Length)
            {
                throw new PyRaise(PyErrors.ValueError("incomplete format"));
            }

            var conversion = format[i];

            PyObject operand;

            if (key is not null)
            {
                if (mapping is null || !mapping.TryGetValue(new PyStr(key), out operand))
                {
                    throw new PyRaise(PyErrors.KeyError(new PyStr(key)));
                }
            }
            else
            {
                if (next >= values.Count)
                {
                    throw new PyRaise(PyErrors.TypeError("not enough arguments for format string"));
                }

                operand = values[next++];
            }

            builder.Append(Pad(ConvertPercent(conversion, operand, precision), width, flags.ToString(), conversion));
        }

        if (key0Unused(mapping, next, values.Count))
        {
            throw new PyRaise(PyErrors.TypeError("not all arguments converted during string formatting"));
        }

        return builder.ToString();

        static bool key0Unused(PyDict? mapping, int consumed, int available) =>
            mapping is null && consumed < available;
    }

    private static string ConvertPercent(char conversion, PyObject value, int? precision) => conversion switch
    {
        's' => Truncate(value.Display(), precision),
        'r' => Truncate(value.Repr(), precision),
        'a' => Truncate(value.Repr(), precision),
        'd' or 'i' or 'u' => RequireInt(value, conversion).ToString(CultureInfo.InvariantCulture),
        'x' => FormatRadix(RequireInt(value, conversion), 16, upper: false),
        'X' => FormatRadix(RequireInt(value, conversion), 16, upper: true),
        'o' => FormatRadix(RequireInt(value, conversion), 8, upper: false),
        'f' or 'F' => RequireFloat(value, conversion).ToString(
            "F" + (precision ?? 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        'e' or 'E' => FormatExponential(RequireFloat(value, conversion), precision ?? 6, conversion == 'E'),
        'g' or 'G' => RequireFloat(value, conversion).ToString(
            (conversion == 'g' ? "G" : "G") + (precision ?? 6).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture),
        'c' => value is PyInt code ? ((char)(int)code.Value).ToString() : value.Display(),
        _ => throw new PyRaise(PyErrors.ValueError(
            $"unsupported format character '{conversion}' (0x{(int)conversion:x}) at index 0")),
    };

    private static string Truncate(string text, int? precision) =>
        precision is { } limit && limit < text.Length ? text[..limit] : text;

    private static BigInteger RequireInt(PyObject value, char conversion) => value switch
    {
        PyInt integer => integer.Value,
        PyFloat number => new BigInteger(Math.Truncate(number.Value)),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"%{conversion} format: a real number is required, not {value.TypeName}")),
    };

    private static double RequireFloat(PyObject value, char conversion) => value switch
    {
        PyInt integer => (double)integer.Value,
        PyFloat number => number.Value,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"%{conversion} format: a real number is required, not {value.TypeName}")),
    };

    internal static string FormatRadix(BigInteger value, int radix, bool upper)
    {
        if (value.IsZero)
        {
            return "0";
        }

        var negative = value.Sign < 0;
        var magnitude = BigInteger.Abs(value);
        var digits = new StringBuilder();
        var alphabet = upper ? "0123456789ABCDEF" : "0123456789abcdef";

        while (!magnitude.IsZero)
        {
            digits.Insert(0, alphabet[(int)(magnitude % radix)]);
            magnitude /= radix;
        }

        return negative ? "-" + digits : digits.ToString();
    }

    private static string FormatExponential(double value, int precision, bool upper)
    {
        var text = value.ToString((upper ? "E" : "e") + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        // .NET writes three exponent digits; Python writes at least two.
        var marker = text.IndexOfAny(['e', 'E']);

        if (marker < 0)
        {
            return text;
        }

        var mantissa = text[..(marker + 2)];
        var exponent = text[(marker + 2)..].TrimStart('0');

        return mantissa + (exponent.Length < 2 ? exponent.PadLeft(2, '0') : exponent);
    }

    private static string Pad(string text, int width, string flags, char conversion)
    {
        if (text.Length >= width)
        {
            return text;
        }

        if (flags.Contains('-', StringComparison.Ordinal))
        {
            return text.PadRight(width);
        }

        if (flags.Contains('0', StringComparison.Ordinal) && conversion is not ('s' or 'r' or 'a' or 'c'))
        {
            return text.StartsWith('-')
                ? "-" + text[1..].PadLeft(width - 1, '0')
                : text.PadLeft(width, '0');
        }

        return text.PadLeft(width);
    }

    /// <summary>
    /// Applies the format mini-language: <c>[[fill]align][sign][#][0][width][,][.precision][type]</c>.
    /// </summary>
    public static string Format(PyObject value, string spec)
    {
        if (spec.Length == 0)
        {
            return value.Display();
        }

        var parsed = FormatSpec.Parse(spec);
        Validate(value, parsed, spec);

        var text = parsed.Type switch
        {
            'd' or 'n' => FormatInteger(value, parsed, 10, upper: false),
            'b' => FormatInteger(value, parsed, 2, upper: false),
            'o' => FormatInteger(value, parsed, 8, upper: false),
            'x' => FormatInteger(value, parsed, 16, upper: false),
            'X' => FormatInteger(value, parsed, 16, upper: true),
            'f' or 'F' => FormatFixed(value, parsed),
            'e' or 'E' => FormatExponential(ToDouble(value), parsed.Precision ?? 6, parsed.Type == 'E'),
            'g' or 'G' => ToDouble(value).ToString(
                "G" + (parsed.Precision ?? 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            '%' => FormatPercentage(value, parsed),
            's' => value.Display(),
            'r' => value.Repr(),
            _ => DefaultText(value, parsed),
        };

        return Align(text, parsed, value);
    }

    /// <summary>
    /// Rejects a spec the value cannot be formatted with.
    /// </summary>
    /// <remarks>
    /// The order matters: CPython reports a grouping conflict before an unknown code, so
    /// <c>{1:,k}</c> complains about the comma rather than about <c>k</c>.
    /// </remarks>
    private static void Validate(PyObject value, FormatSpec spec, string source)
    {
        var isString = value is PyStr;
        var presentation = spec.Type == '\0' ? (isString ? 's' : '\0') : spec.Type;

        if (spec.BothSeparators)
        {
            throw new PyRaise(PyErrors.ValueError("Cannot specify both ',' and '_'."));
        }

        // Grouping is only meaningful for the decimal presentations.
        if (spec.Grouping != '\0' && presentation is not ('\0' or 'd' or 'n' or 'e' or 'E' or 'f' or 'F' or 'g' or 'G' or '%'))
        {
            throw new PyRaise(PyErrors.ValueError($"Cannot specify '{spec.Grouping}' with '{presentation}'."));
        }

        if (spec.Alternate)
        {
            if (isString || presentation == 's')
            {
                throw new PyRaise(PyErrors.ValueError("Alternate form (#) not allowed in string format specifier"));
            }

            if (presentation == 'c')
            {
                throw new PyRaise(PyErrors.ValueError(
                    "Alternate form (#) not allowed with integer format specifier 'c'"));
            }
        }

        if (isString && spec.Alignment == '=')
        {
            throw new PyRaise(PyErrors.ValueError("'=' alignment not allowed in string format specifier"));
        }

        if (spec.MissingPrecision)
        {
            throw new PyRaise(PyErrors.ValueError("Format specifier missing precision"));
        }

        if (spec.Tail.Length == 1)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"Unknown format code '{spec.Tail}' for object of type '{value.TypeName}'"));
        }

        if (spec.Tail.Length > 1)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"Invalid format specifier '{source}' for object of type '{value.TypeName}'"));
        }

        // A presentation the value's type does not offer is an unknown code for that type.
        var known = value switch
        {
            PyStr => spec.Type is '\0' or 's' or 'r' or 'a',
            PyBool or PyInt => spec.Type is '\0' or 'd' or 'n' or 'b' or 'o' or 'x' or 'X' or 'c'
                or 'e' or 'E' or 'f' or 'F' or 'g' or 'G' or '%' or 'r' or 's' or 'a',
            PyFloat => spec.Type is '\0' or 'e' or 'E' or 'f' or 'F' or 'g' or 'G' or 'n' or '%' or 'r' or 'a',
            _ => true,
        };

        // `int` accepts `s` in `str.format` but not in an f-string spec; both spell the
        // rejection the same way when the code does not apply.
        if (!known || (value is PyInt and not PyBool && spec.Type == 's') || (value is PyStr && spec.Type == 'd'))
        {
            throw new PyRaise(PyErrors.ValueError(
                $"Unknown format code '{spec.Type}' for object of type '{value.TypeName}'"));
        }
    }

    private static string DefaultText(PyObject value, FormatSpec spec)
    {
        // A numeric value with a width but no type still honours grouping and sign.
        if (value is PyInt integer && (spec.Grouping != '\0' || spec.Sign != '\0'))
        {
            return ApplySign(Group(integer.Value.ToString(CultureInfo.InvariantCulture), spec.Grouping), integer.Value.Sign >= 0, spec);
        }

        if (value is PyFloat number && spec.Grouping != '\0')
        {
            return ApplySign(Group(PyFloat.Format(number.Value), spec.Grouping), number.Value >= 0, spec);
        }

        return value.Display();
    }

    private static string FormatInteger(PyObject value, FormatSpec spec, int radix, bool upper)
    {
        var integer = value switch
        {
            PyInt number => number.Value,
            PyFloat number when radix == 10 => new BigInteger(Math.Truncate(number.Value)),
            _ => throw new PyRaise(PyErrors.ValueError(
                $"Unknown format code '{spec.Type}' for object of type '{value.TypeName}'")),
        };

        var text = FormatRadix(BigInteger.Abs(integer), radix, upper);

        if (spec.Grouping != '\0' && radix == 10)
        {
            text = Group(text, spec.Grouping);
        }

        if (spec.Alternate)
        {
            text = radix switch
            {
                2 => "0b" + text,
                8 => "0o" + text,
                16 => (upper ? "0X" : "0x") + text,
                _ => text,
            };
        }

        return ApplySign(text, integer.Sign >= 0, spec);
    }

    private static string FormatFixed(PyObject value, FormatSpec spec)
    {
        var number = ToDouble(value);
        var text = Math.Abs(number).ToString(
            "F" + (spec.Precision ?? 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        if (spec.Grouping != '\0')
        {
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            var whole = dot < 0 ? text : text[..dot];
            var fraction = dot < 0 ? string.Empty : text[dot..];
            text = Group(whole, spec.Grouping) + fraction;
        }

        return ApplySign(text, number >= 0, spec);
    }

    private static string FormatPercentage(PyObject value, FormatSpec spec)
    {
        var number = ToDouble(value) * 100;
        var text = Math.Abs(number).ToString(
            "F" + (spec.Precision ?? 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + "%";

        return ApplySign(text, number >= 0, spec);
    }

    private static double ToDouble(PyObject value) => value switch
    {
        PyInt integer => (double)integer.Value,
        PyFloat number => number.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"unsupported format string for {value.TypeName}")),
    };

    private static string Group(string digits, char separator)
    {
        if (separator == '\0' || digits.Length <= 3)
        {
            return digits;
        }

        var negative = digits.StartsWith('-');
        var body = negative ? digits[1..] : digits;
        var builder = new StringBuilder();

        for (var i = 0; i < body.Length; i++)
        {
            if (i > 0 && (body.Length - i) % 3 == 0)
            {
                builder.Append(separator == 'n' ? ',' : separator);
            }

            builder.Append(body[i]);
        }

        return (negative ? "-" : string.Empty) + builder;
    }

    private static string ApplySign(string magnitude, bool nonNegative, FormatSpec spec)
    {
        if (!nonNegative)
        {
            return magnitude.StartsWith('-') ? magnitude : "-" + magnitude;
        }

        return spec.Sign switch
        {
            '+' => "+" + magnitude,
            ' ' => " " + magnitude,
            _ => magnitude,
        };
    }

    private static string Align(string text, FormatSpec spec, PyObject value)
    {
        if (text.Length >= spec.Width)
        {
            return text;
        }

        // Numbers default to right alignment; everything else to left.
        var alignment = spec.Alignment != '\0'
            ? spec.Alignment
            : spec.ZeroPad ? '=' : value is PyInt or PyFloat ? '>' : '<';

        var padding = spec.Width - text.Length;
        var fill = spec.ZeroPad && spec.Alignment == '\0' ? '0' : spec.Fill;

        switch (alignment)
        {
            case '<':
                return text + new string(fill, padding);

            case '>':
                return new string(fill, padding) + text;

            case '^':
            {
                var left = padding / 2;
                return new string(fill, left) + text + new string(fill, padding - left);
            }

            case '=':
            {
                // Zero padding goes after the sign, so `-1` at width 4 is `-001`.
                var signLength = text.Length > 0 && text[0] is '-' or '+' or ' ' ? 1 : 0;
                return text[..signLength] + new string(fill, padding) + text[signLength..];
            }

            default:
                return text;
        }
    }

    /// <summary>A parsed format spec.</summary>
    private readonly record struct FormatSpec(
        char Fill,
        char Alignment,
        char Sign,
        bool Alternate,
        bool ZeroPad,
        int Width,
        char Grouping,
        int? Precision,
        char Type)
    {
        /// <summary>The grouping applied to the fractional digits, from <c>._f</c>.</summary>
        public char FractionGrouping { get; init; }

        /// <summary>Whatever the spec had left over after the presentation type.</summary>
        public string Tail { get; init; } = string.Empty;

        /// <summary>True when a <c>.</c> was given with no precision after it.</summary>
        public bool MissingPrecision { get; init; }

        /// <summary>True when both separators appear, which is never allowed.</summary>
        public bool BothSeparators { get; init; }

        public static FormatSpec Parse(string spec)
        {
            var i = 0;
            var fill = ' ';
            var alignment = '\0';

            // A fill character is only present when followed by an alignment.
            if (spec.Length >= 2 && spec[1] is '<' or '>' or '^' or '=')
            {
                fill = spec[0];
                alignment = spec[1];
                i = 2;
            }
            else if (spec.Length >= 1 && spec[0] is '<' or '>' or '^' or '=')
            {
                alignment = spec[0];
                i = 1;
            }

            var sign = '\0';
            if (i < spec.Length && spec[i] is '+' or '-' or ' ')
            {
                sign = spec[i++];
            }

            var alternate = false;
            if (i < spec.Length && spec[i] == '#')
            {
                alternate = true;
                i++;
            }

            var zeroPad = false;
            if (i < spec.Length && spec[i] == '0')
            {
                zeroPad = true;
                i++;
            }

            var width = 0;
            while (i < spec.Length && char.IsAsciiDigit(spec[i]))
            {
                width = (width * 10) + (spec[i++] - '0');
            }

            var grouping = '\0';
            var both = false;

            if (i < spec.Length && spec[i] is ',' or '_')
            {
                grouping = spec[i++];

                // A second, different separator is its own complaint; the same one twice
                // falls through as an inapplicable presentation type.
                if (i < spec.Length && spec[i] is ',' or '_' && spec[i] != grouping)
                {
                    both = true;
                    i++;
                }
            }

            int? precision = null;
            var fractionGrouping = '\0';
            var missingPrecision = false;

            if (i < spec.Length && spec[i] == '.')
            {
                i++;

                // `.{_,}` groups the fractional digits and leaves the precision default.
                if (i < spec.Length && spec[i] is ',' or '_')
                {
                    fractionGrouping = spec[i++];
                }

                var digits = i;

                while (i < spec.Length && char.IsAsciiDigit(spec[i]))
                {
                    precision = ((precision ?? 0) * 10) + (spec[i++] - '0');
                }

                missingPrecision = i == digits && fractionGrouping == '\0';
            }

            var type = i < spec.Length ? spec[i++] : '\0';

            return new FormatSpec(fill, alignment, sign, alternate, zeroPad, width, grouping, precision, type)
            {
                FractionGrouping = fractionGrouping,
                Tail = spec[i..],
                MissingPrecision = missingPrecision,
                BothSeparators = both,
            };
        }
    }
}
