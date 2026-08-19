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
        'g' or 'G' => FormatGeneral(RequireFloat(value, conversion), precision ?? 6, conversion == 'G', alternate: false),
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

    /// <summary>
    /// Formats with the <c>g</c> rules: scientific notation only outside
    /// <c>[10**-4, 10**precision)</c>, and trailing zeros trimmed unless the alternate
    /// form asked to keep them.
    /// </summary>
    private static string FormatGeneral(double value, int precision, bool upper, bool alternate, bool typeless = false)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            var special = double.IsNaN(value) ? "nan" : value > 0 ? "inf" : "-inf";
            return upper ? special.ToUpperInvariant() : special;
        }

        // A precision of zero behaves as one significant digit.
        var digits = Math.Max(precision, 1);

        // The decimal exponent decides the notation; taking it from the rounded scientific
        // form is what keeps 9.99e-5 at precision 1 from being called 1e-4's neighbour.
        var scientific = FormatExponential(value, digits - 1, upper: false);
        var marker = scientific.IndexOf('e', StringComparison.Ordinal);
        var exponent = int.Parse(scientific[(marker + 1)..], CultureInfo.InvariantCulture);

        string text;

        if (exponent < -4 || exponent >= digits - (typeless ? 1 : 0))
        {
            var mantissa = Trim(scientific[..marker], alternate);
            text = mantissa + (upper ? "E" : "e") + scientific[(marker + 1)..];
        }
        else
        {
            text = Trim(
                value.ToString("F" + (digits - 1 - exponent).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                alternate);
        }

        return text;

        static string Trim(string text, bool keep)
        {
            if (keep)
            {
                return ForcePoint(text, alternate: true);
            }

            if (!text.Contains('.', StringComparison.Ordinal))
            {
                return text;
            }

            var trimmed = text.TrimEnd('0');
            return trimmed.EndsWith('.') ? trimmed[..^1] : trimmed;
        }
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

        if (value.PyFormat(spec) is { } custom)
        {
            return custom;
        }

        var parsed = FormatSpec.Parse(spec);
        Validate(value, parsed, spec);

        // `inf` and `nan` have no digits to group, and grouping their padding would be
        // nonsense, so the separator is dropped before anything looks at it.
        if (value is PyFloat { Value: var raw } && !double.IsFinite(raw))
        {
            parsed = parsed with { Grouping = '\0' };
        }

        var text = parsed.Type switch
        {
            'd' => FormatInteger(value, parsed, 10, upper: false),
            'n' => value is PyInt ? FormatInteger(value, parsed, 10, upper: false) : FormatSignificant(value, parsed),
            'b' => FormatInteger(value, parsed, 2, upper: false),
            'o' => FormatInteger(value, parsed, 8, upper: false),
            'x' => FormatInteger(value, parsed, 16, upper: false),
            'X' => FormatInteger(value, parsed, 16, upper: true),
            'f' or 'F' => FormatFixed(value, parsed),
            'e' or 'E' => FormatScientific(value, parsed),
            'g' or 'G' => FormatSignificant(value, parsed),
            '%' => FormatPercentage(value, parsed),
            'c' => FormatChar(value),
            's' => value.Display(),
            'r' => value.Repr(),
            _ => DefaultText(value, parsed),
        };

        // Precision truncates a string rather than rounding it.
        if (value is PyStr && parsed.Precision is { } limit && text.Length > limit)
        {
            text = text[..limit];
        }

        if (parsed.FractionGrouping != '\0')
        {
            text = GroupFraction(text, parsed.FractionGrouping);
        }

        // `z` coerces a negative zero *after* rounding, so -0.001 at one decimal is 0.0
        // while at three decimals it stays -0.001.
        if (parsed.CoerceNegativeZero && text.StartsWith('-') && !text.Any(static c => c is >= '1' and <= '9'))
        {
            text = parsed.Sign is '+' or ' ' ? parsed.Sign + text[1..] : text[1..];
        }

        return Align(text, parsed, value);
    }

    /// <summary>
    /// Separates the fractional digits into groups of three, as <c>.{_,}</c> asks for.
    /// </summary>
    private static string GroupFraction(string text, char separator)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);

        if (dot < 0)
        {
            return text;
        }

        var digits = dot + 1;

        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        var builder = new StringBuilder(text[..(dot + 1)]);

        for (var i = dot + 1; i < digits; i++)
        {
            if (i > dot + 1 && (i - dot - 1) % 3 == 0)
            {
                builder.Append(separator);
            }

            builder.Append(text[i]);
        }

        return builder.Append(text[digits..]).ToString();
    }

    /// <summary>
    /// Rejects a spec the value cannot be formatted with.
    /// </summary>
    /// <remarks>
    /// The order matters — CPython's is grouping, then type, then sign, then the alternate
    /// form, then <c>=</c> alignment — so <c>{1:,k}</c> complains about the comma rather
    /// than about <c>k</c>, and <c>{'x':+#}</c> about the sign rather than the <c>#</c>.
    /// </remarks>
    private static void Validate(PyObject value, FormatSpec spec, string source)
    {
        var isString = value is PyStr;
        var presentation = spec.Type == '\0' ? (isString ? 's' : '\0') : spec.Type;

        if (spec.BothSeparators)
        {
            throw new PyRaise(PyErrors.ValueError("Cannot specify both ',' and '_'."));
        }

        // A comma groups the decimal presentations only; an underscore also groups the
        // other integer bases, in fours rather than threes.
        var groupable = presentation is '\0' or 'd' or 'e' or 'E' or 'f' or 'F' or 'g' or 'G' or '%'
            || (spec.Grouping == '_' && presentation is 'b' or 'o' or 'x' or 'X');

        if (spec.Grouping != '\0' && !groupable)
        {
            throw new PyRaise(PyErrors.ValueError($"Cannot specify '{spec.Grouping}' with '{presentation}'."));
        }

        // Precision is meaningless for the integer presentations, and CPython reports that
        // before it reports anything about `z`.
        var integral = value is PyInt && presentation is '\0' or 'd' or 'n' or 'b' or 'o' or 'x' or 'X' or 'c';

        if (integral && spec.Precision.HasValue)
        {
            throw new PyRaise(PyErrors.ValueError("Precision not allowed in integer format specifier"));
        }

        if (spec.CoerceNegativeZero && (isString || integral))
        {
            throw new PyRaise(PyErrors.ValueError(isString
                ? "Negative zero coercion (z) not allowed in string format specifier"
                : "Negative zero coercion (z) not allowed in integer format specifier"));
        }

        // Anything left over after the presentation char means the spec never parsed; an
        // unrecognised char on its own is the narrower "unknown code" complaint below.
        if (spec.Tail.Length > 0)
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

        if (presentation == 'c' && spec.Sign != '\0')
        {
            throw new PyRaise(PyErrors.ValueError("Sign not allowed with integer format specifier 'c'"));
        }

        if (isString && spec.Sign != '\0')
        {
            throw new PyRaise(PyErrors.ValueError(spec.Sign == ' '
                ? "Space not allowed in string format specifier"
                : "Sign not allowed in string format specifier"));
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
    }

    private static string FormatScientific(PyObject value, FormatSpec spec)
    {
        var number = ToDouble(value);
        var text = Special(number, spec.Type == 'E')
            ?? FormatExponential(Math.Abs(number), spec.Precision ?? 6, spec.Type == 'E');

        if (spec.Alternate && !text.Contains('.', StringComparison.Ordinal))
        {
            var marker = text.IndexOfAny(['e', 'E']);
            text = marker < 0 ? text + "." : text[..marker] + "." + text[marker..];
        }

        return ApplySign(text, IsNonNegative(number), spec);
    }

    private static string FormatSignificant(PyObject value, FormatSpec spec)
    {
        var number = ToDouble(value);
        var text = Special(number, spec.Type == 'G')
            ?? FormatGeneral(
                Math.Abs(number),
                spec.Precision ?? 6,
                spec.Type == 'G',
                spec.Alternate,
                typeless: spec.Type == '\0');

        return ApplySign(GroupWhole(text, spec.Grouping), IsNonNegative(number), spec);
    }

    /// <summary>Formats an integer as the character with that code point.</summary>
    private static string FormatChar(PyObject value)
    {
        if (value is not PyInt code || code.Value < 0 || code.Value > 0x10FFFF)
        {
            throw new PyRaise(PyErrors.ValueError("Cannot convert value to a character"));
        }

        return char.ConvertFromUtf32((int)code.Value);
    }

    private static string DefaultText(PyObject value, FormatSpec spec)
    {
        // A precision with no presentation type formats a float as `g` does.
        if (value is PyFloat precise && spec.Precision.HasValue)
        {
            return FormatSignificant(precise, spec);
        }

        // Under a non-empty spec an integer is always a number — which is how `bool` stops
        // printing as `True` and starts printing as `1`.
        if (value is PyInt integer)
        {
            return ApplySign(
                GroupWhole(integer.Value.ToString(CultureInfo.InvariantCulture), spec.Grouping),
                integer.Value.Sign >= 0,
                spec);
        }

        if (value is PyFloat number && spec.Grouping != '\0')
        {
            return ApplySign(GroupWhole(PyFloat.Format(number.Value), spec.Grouping), IsNonNegative(number.Value), spec);
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

        if (spec.Grouping != '\0')
        {
            text = Group(text, spec.Grouping, radix == 10 ? 3 : 4);
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

    /// <summary>Appends the decimal point the alternate form insists on.</summary>
    private static string ForcePoint(string text, bool alternate) =>
        alternate && !text.Contains('.', StringComparison.Ordinal) ? text + "." : text;

    /// <summary>
    /// Whether the sign to print is a positive one. A NaN carries .NET's sign bit but
    /// prints unsigned, so it is not simply the absence of <c>IsNegative</c>.
    /// </summary>
    private static bool IsNonNegative(double value) => double.IsNaN(value) || !double.IsNegative(value);

    /// <summary>The text of a non-finite value, or null when the value is an ordinary one.</summary>
    private static string? Special(double value, bool upper)
    {
        var text = double.IsNaN(value) ? "nan" : double.IsInfinity(value) ? "inf" : null;
        return text is null ? null : upper ? text.ToUpperInvariant() : text;
    }

    private static string FormatFixed(PyObject value, FormatSpec spec)
    {
        var number = ToDouble(value);

        if (Special(number, spec.Type is 'F' or 'E' or 'G') is { } special)
        {
            return ApplySign(special, IsNonNegative(number), spec);
        }
        var text = Math.Abs(number).ToString(
            "F" + (spec.Precision ?? 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        if (spec.Grouping != '\0')
        {
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            var whole = dot < 0 ? text : text[..dot];
            var fraction = dot < 0 ? string.Empty : text[dot..];
            text = Group(whole, spec.Grouping) + fraction;
        }

        return ApplySign(ForcePoint(text, spec.Alternate), IsNonNegative(number), spec);
    }

    private static string FormatPercentage(PyObject value, FormatSpec spec)
    {
        var number = ToDouble(value) * 100;

        // A non-finite percentage still ends in the sign: `nan%`, not `nan`.
        if (Special(number, upper: false) is { } special)
        {
            return ApplySign(special + "%", IsNonNegative(number), spec);
        }
        var text = Math.Abs(number).ToString(
            "F" + (spec.Precision ?? 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + "%";

        if (spec.Grouping != '\0')
        {
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            text = Group(text[..dot], spec.Grouping) + text[dot..];
        }

        if (spec.Alternate && !text.Contains('.', StringComparison.Ordinal))
        {
            text = text[..^1] + ".%";
        }

        return ApplySign(text, IsNonNegative(number), spec);
    }

    private static double ToDouble(PyObject value) => value switch
    {
        PyInt integer => (double)integer.Value,
        PyFloat number => number.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"unsupported format string for {value.TypeName}")),
    };

    /// <summary>
    /// Groups only the integral digits of a number, leaving the fraction and any exponent
    /// alone.
    /// </summary>
    private static string GroupWhole(string text, char separator)
    {
        if (separator == '\0')
        {
            return text;
        }

        var end = text.IndexOfAny(['.', 'e', 'E']);
        return end < 0 ? Group(text, separator) : Group(text[..end], separator) + text[end..];
    }

    private static string Group(string digits, char separator, int size = 3)
    {
        if (separator == '\0' || digits.Length <= size)
        {
            return digits;
        }

        var negative = digits.StartsWith('-');
        var body = negative ? digits[1..] : digits;
        var builder = new StringBuilder();

        for (var i = 0; i < body.Length; i++)
        {
            if (i > 0 && (body.Length - i) % size == 0)
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

        // Numbers default to right alignment; everything else to left. A leading `0` pads
        // after the sign for a number, but a string has no sign, so it keeps left alignment
        // and gains a run of zeros on the right.
        var alignment = spec.Alignment != '\0'
            ? spec.Alignment
            : value is PyInt or PyFloat ? (spec.ZeroPad ? '=' : '>') : '<';

        var padding = spec.Width - text.Length;
        // A leading `0` is a `0` fill; it only implies sign-aware alignment when the spec
        // did not name an alignment of its own.
        var fill = spec.ZeroPad && !spec.ExplicitFill ? "0" : spec.Fill;

        switch (alignment)
        {
            case '<':
                return text + Repeat(fill, padding);

            case '>':
                return Repeat(fill, padding) + text;

            case '^':
            {
                var left = padding / 2;
                return Repeat(fill, left) + text + Repeat(fill, padding - left);
            }

            case '=' when spec.Grouping != '\0' && fill == "0":
                // The pad zeros are themselves grouped, so they cannot simply be inserted:
                // the number is re-grouped with more leading zeros until it fills the width.
                return PadGrouped(text, spec);

            case '=':
            {
                // Padding goes after the sign and after any `0x`-style base prefix, so `-1`
                // at width 4 is `-001` and `#010x` of 255 is `0x000000ff`.
                var head = HeadLength(text);
                return text[..head] + Repeat(fill, padding) + text[head..];
            }

            default:
                return text;
        }

        // The fill is a whole code point, which may be a surrogate pair.
        static string Repeat(string fill, int count) =>
            fill.Length == 1 ? new string(fill[0], count) : string.Concat(Enumerable.Repeat(fill, count));
    }

    /// <summary>
    /// Zero-pads a grouped number to the spec's width, grouping the padding as well.
    /// </summary>
    /// <remarks>
    /// A group separator is only ever added between digits, so the width cannot always be
    /// hit exactly — CPython overshoots by one rather than leaving a leading separator,
    /// which is why this grows the zeros until the result is wide enough.
    /// </remarks>
    private static string PadGrouped(string text, FormatSpec spec)
    {
        // `0b`/`0o`/`0x` occupy width but are not part of the grouped digits.
        var head = HeadLength(text);
        var body = text[head..];
        // `e` is a hex digit, not an exponent marker, so the base presentations have no
        // trailing part to keep out of the grouping.
        var split = spec.Type is 'b' or 'o' or 'x' or 'X' ? -1 : body.IndexOfAny(['.', 'e', 'E', '%']);
        var whole = split < 0 ? body : body[..split];
        var tail = split < 0 ? string.Empty : body[split..];
        var digits = whole.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        var size = spec.Type is 'b' or 'o' or 'x' or 'X' ? 4 : 3;
        var fixedWidth = head + tail.Length;
        var grouped = Group(digits, spec.Grouping, size);

        for (var zeros = 1; fixedWidth + grouped.Length < spec.Width; zeros++)
        {
            grouped = Group(new string('0', zeros) + digits, spec.Grouping, size);
        }

        return text[..head] + grouped + tail;
    }

    /// <summary>
    /// The length of the sign and base prefix a number carries — the part padding is
    /// inserted after.
    /// </summary>
    private static int HeadLength(string text)
    {
        var head = text.Length > 0 && text[0] is '-' or '+' or ' ' ? 1 : 0;

        return text.Length >= head + 2
            && text[head] == '0'
            && text[head + 1] is 'b' or 'o' or 'x' or 'B' or 'O' or 'X'
                ? head + 2
                : head;
    }

    /// <summary>A parsed format spec.</summary>
    private readonly record struct FormatSpec(
        string Fill,
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

        /// <summary>Whether the spec named a fill character of its own.</summary>
        public bool ExplicitFill { get; init; }

        /// <summary>The <c>z</c> flag: render a negative zero as a positive one.</summary>
        public bool CoerceNegativeZero { get; init; }

        public static FormatSpec Parse(string spec)
        {
            var i = 0;
            var fill = " ";
            var alignment = '\0';

            // A fill character is only present when followed by an alignment — and it is a
            // code point, so an astral fill occupies two UTF-16 units.
            var fillWidth = spec.Length >= 1 && char.IsHighSurrogate(spec[0]) ? 2 : 1;

            if (spec.Length >= fillWidth + 1 && spec[fillWidth] is '<' or '>' or '^' or '=')
            {
                fill = spec[..fillWidth];
                alignment = spec[fillWidth];
                i = fillWidth + 1;
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

            // `z` sits between the sign and the alternate form; in the fill slot it was
            // already consumed above as a fill character.
            var coerce = false;
            if (i < spec.Length && spec[i] == 'z')
            {
                coerce = true;
                i++;
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

                // The fractional grouping may sit on either side of the digits: `._f` and
                // `.6_f` are both spelled that way.
                if (fractionGrouping == '\0' && i < spec.Length && spec[i] is ',' or '_')
                {
                    fractionGrouping = spec[i++];
                }

                missingPrecision = i == digits && fractionGrouping == '\0';
            }

            var type = i < spec.Length ? spec[i++] : '\0';

            return new FormatSpec(fill, alignment, sign, alternate, zeroPad, width, grouping, precision, type)
            {
                FractionGrouping = fractionGrouping,
                Tail = spec[i..],
                MissingPrecision = missingPrecision,
                CoerceNegativeZero = coerce,
                ExplicitFill = fill != " ",
                BothSeparators = both,
            };
        }
    }
}
