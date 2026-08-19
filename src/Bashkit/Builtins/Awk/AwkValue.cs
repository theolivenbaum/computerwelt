using System.Globalization;

namespace Bashkit.Builtins.Awk;

/// <summary>What a value knows about itself, which is what decides how it compares.</summary>
internal enum AwkValueKind
{
    /// <summary>Never assigned: equal to both <c>""</c> and <c>0</c>.</summary>
    Uninitialized,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A string, from a literal or a string-producing operation.</summary>
    Text,

    /// <summary>
    /// A "strnum" — text that came from input, so it compares numerically when it looks
    /// like a number and as text otherwise.
    /// </summary>
    StrNum,
}

/// <summary>
/// An AWK scalar.
/// </summary>
/// <remarks>
/// <para>
/// AWK's one genuinely surprising rule is that a value remembers <i>where it came from</i>.
/// <c>$1 == 0</c> is true for the input <c>0.0</c> because a field is a "strnum" and
/// compares numerically, while the identical literal <c>"0.0" == 0</c> is false because a
/// string literal never does. Carrying <see cref="AwkValueKind"/> on every value is the
/// only way to reproduce that, so it is modelled explicitly rather than inferred.
/// </para>
/// </remarks>
internal readonly struct AwkValue : IEquatable<AwkValue>
{
    private readonly string? _text;
    private readonly double _number;

    private AwkValue(AwkValueKind kind, double number, string? text)
    {
        Kind = kind;
        _number = number;
        _text = text;
    }

    /// <summary>What this value is.</summary>
    public AwkValueKind Kind { get; }

    /// <summary>The uninitialized value.</summary>
    public static AwkValue Uninitialized => default;

    /// <summary>Wraps a number.</summary>
    public static AwkValue Number(double value) => new(AwkValueKind.Number, value, null);

    /// <summary>Wraps a string literal or a computed string.</summary>
    public static AwkValue Text(string value) => new(AwkValueKind.Text, 0, value);

    /// <summary>Wraps a boolean as AWK's 1 or 0.</summary>
    public static AwkValue Bool(bool value) => Number(value ? 1 : 0);

    /// <summary>
    /// Wraps input-derived text, which compares numerically when it looks like a number.
    /// </summary>
    public static AwkValue Input(string value) => new(AwkValueKind.StrNum, 0, value);

    /// <summary>True when this value participates in numeric comparison.</summary>
    public bool IsNumeric => Kind switch
    {
        AwkValueKind.Number or AwkValueKind.Uninitialized => true,
        AwkValueKind.StrNum => LooksNumeric(_text),
        _ => false,
    };

    /// <summary>This value as a number, using AWK's leading-prefix rule.</summary>
    public double ToNumber() => Kind switch
    {
        AwkValueKind.Number => _number,
        AwkValueKind.Uninitialized => 0,
        _ => ParsePrefix(_text),
    };

    /// <summary>This value as text, formatting numbers with <paramref name="convfmt"/>.</summary>
    public string ToText(string convfmt = "%.6g") => Kind switch
    {
        AwkValueKind.Number => FormatNumber(_number, convfmt),
        AwkValueKind.Uninitialized => string.Empty,
        _ => _text ?? string.Empty,
    };

    /// <summary>Whether this value is true in a condition.</summary>
    /// <remarks>
    /// A number is true when non-zero; a string is true when non-empty. A strnum follows
    /// its numeric reading when it has one, which is why <c>$1</c> holding <c>"0"</c> is
    /// false while the literal <c>"0"</c> is true.
    /// </remarks>
    public bool ToBoolean() => Kind switch
    {
        AwkValueKind.Number => _number != 0,
        AwkValueKind.Uninitialized => false,
        AwkValueKind.StrNum when LooksNumeric(_text) => ParsePrefix(_text) != 0,
        _ => !string.IsNullOrEmpty(_text),
    };

    /// <summary>Compares two values under AWK's mixed numeric/string rules.</summary>
    public static int Compare(AwkValue left, AwkValue right, string convfmt)
    {
        if (left.IsNumeric && right.IsNumeric)
        {
            return left.ToNumber().CompareTo(right.ToNumber());
        }

        return string.CompareOrdinal(left.ToText(convfmt), right.ToText(convfmt));
    }

    /// <inheritdoc />
    public bool Equals(AwkValue other) => Compare(this, other, "%.6g") == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AwkValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => ToText().GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => ToText();

    /// <summary>
    /// True when the whole string is a number, ignoring surrounding blanks. Only then does
    /// a strnum compare numerically — <c>"3x"</c> converts to 3 but still compares as text.
    /// </summary>
    private static bool LooksNumeric(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim(" \t\n\r");

        if (span.IsEmpty)
        {
            return false;
        }

        // `nan` and `inf` are numbers to .NET but plain text to awk.
        foreach (var c in span)
        {
            if (char.IsAsciiLetter(c) && c is not ('e' or 'E'))
            {
                return false;
            }
        }

        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Reads the longest numeric prefix, as AWK's string-to-number conversion does:
    /// <c>"12abc"</c> is 12 and <c>"abc"</c> is 0.
    /// </summary>
    internal static double ParsePrefix(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var i = 0;

        while (i < text.Length && text[i] is ' ' or '\t' or '\n' or '\r')
        {
            i++;
        }

        var start = i;

        if (i < text.Length && text[i] is '+' or '-')
        {
            i++;
        }

        // Hexadecimal is a common extension and the shell's own arithmetic accepts it.
        if (i + 1 < text.Length && text[i] == '0' && (text[i + 1] is 'x' or 'X'))
        {
            var hexStart = i + 2;
            var hex = hexStart;

            while (hex < text.Length && Uri.IsHexDigit(text[hex]))
            {
                hex++;
            }

            if (hex > hexStart)
            {
                var magnitude = (double)Convert.ToInt64(text[hexStart..hex], 16);
                return text[start] == '-' ? -magnitude : magnitude;
            }
        }

        var digits = i;

        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i < text.Length && text[i] == '.')
        {
            i++;

            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }
        }

        if (i == digits || (i == digits + 1 && text[digits] == '.'))
        {
            return 0;
        }

        if (i < text.Length && text[i] is 'e' or 'E')
        {
            var save = i;
            i++;

            if (i < text.Length && text[i] is '+' or '-')
            {
                i++;
            }

            if (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
            }
            else
            {
                i = save;
            }
        }

        return double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    /// <summary>
    /// Renders a number the way AWK prints one: integers without a decimal point, and
    /// everything else through <c>CONVFMT</c>/<c>OFMT</c>, which default to <c>%.6g</c>.
    /// </summary>
    internal static string FormatNumber(double value, string convfmt)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        // An integral value prints exactly, however large the format's precision.
        if (Math.Abs(value) < 1e16 && value == Math.Floor(value))
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return AwkFormatter.Format(convfmt, [AwkValue.Number(value)], convfmt);
    }
}
