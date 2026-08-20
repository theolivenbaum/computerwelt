using System.Globalization;
using System.Numerics;
using System.Text;
using Monty.Runtime;

namespace Monty.Modules;

/// <summary>The <c>datetime</c> module.</summary>
/// <remarks>
/// The clock is supplied by the host, so <c>datetime.now()</c> is reproducible and the
/// sandbox cannot read the real wall clock as a fingerprint. There is no local time zone
/// either: a naive datetime is treated as UTC wherever an offset is unavoidable, because a
/// host's zone would be both non-deterministic and a fingerprint of its own.
/// </remarks>
public static class DatetimeModule
{
    /// <summary>Builds the module.</summary>
    /// <param name="timeProvider">The clock <c>now</c> and <c>today</c> read.</param>
    /// <returns>The module.</returns>
    public static PyModuleObject Create(TimeProvider timeProvider)
    {
        var module = new PyModuleObject("datetime");

        module.Add("datetime", new PyDateTimeType(timeProvider));
        module.Add("date", new PyDateType(timeProvider));
        module.Add("timedelta", new PyDeltaType());
        module.Add("timezone", new PyTimeZoneType());

        return module;
    }

    /// <summary>
    /// Constructs an instance when <paramref name="callable"/> is one of this module's class
    /// objects. Returns null otherwise, so the VM can fall through.
    /// </summary>
    /// <param name="callable">The object being called.</param>
    /// <param name="arguments">The positional arguments.</param>
    /// <param name="keywords">The keyword arguments, if any.</param>
    /// <returns>The new instance, or null when this is not one of ours.</returns>
    public static PyObject? TryConstruct(PyCallable callable, PyObject[] arguments, PyDict? keywords) =>
        callable is IPyClassLike type ? type.Construct(arguments, keywords) : null;

    /// <summary>
    /// Applies a binary operator over this module's types, or returns null when the operands
    /// are not ones it knows.
    /// </summary>
    /// <param name="op">The operator's spelling.</param>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The result, or null when the pair is not this module's business.</returns>
    public static PyObject? TryBinary(string op, PyObject left, PyObject right)
    {
        switch (op, left, right)
        {
            case ("+", PyDelta a, PyDelta b):
                return PyDelta.FromMicroseconds(a.Total + b.Total);

            case ("-", PyDelta a, PyDelta b):
                return PyDelta.FromMicroseconds(a.Total - b.Total);

            case ("*", PyDelta span, PyInt factor):
                return PyDelta.FromMicroseconds(span.Total * factor.Value);

            case ("*", PyInt factor, PyDelta span):
                return PyDelta.FromMicroseconds(span.Total * factor.Value);

            // True division rounds half to even, as every timedelta conversion does;
            // floor division truncates towards negative infinity.
            case ("/", PyDelta span, PyInt divisor):
                return PyDelta.FromMicroseconds(Halves(span.Total, divisor.Value));

            case ("//", PyDelta span, PyInt divisor):
                return PyDelta.FromMicroseconds(Floors(span.Total, divisor.Value));

            case ("/", PyDelta span, PyDelta divisor):
                return new PyFloat((double)span.Total / (double)divisor.Total);

            case ("//", PyDelta span, PyDelta divisor):
                return new PyInt(Floors(span.Total, divisor.Total));

            case ("+", PyDate date, PyDelta span):
                return PyDate.FromOrdinal(date.Ordinal + Whole(span.Total));

            case ("+", PyDelta span, PyDate date):
                return PyDate.FromOrdinal(date.Ordinal + Whole(span.Total));

            case ("-", PyDate date, PyDelta span):
                return PyDate.FromOrdinal(date.Ordinal - Whole(span.Total));

            case ("-", PyDate a, PyDate b):
                return PyDelta.FromMicroseconds((a.Ordinal - b.Ordinal) * PyDelta.MicrosecondsPerDay);

            case ("+", PyDateTime moment, PyDelta span):
                return moment.Shifted(span.Total);

            case ("+", PyDelta span, PyDateTime moment):
                return moment.Shifted(span.Total);

            case ("-", PyDateTime moment, PyDelta span):
                return moment.Shifted(-span.Total);

            case ("-", PyDateTime a, PyDateTime b):
                return PyDelta.FromMicroseconds(a.Instant - b.Instant);

            default:
                return null;
        }
    }

    /// <summary>Applies a unary operator over this module's types, or returns null.</summary>
    /// <param name="op">The operator's spelling.</param>
    /// <param name="operand">The operand.</param>
    /// <returns>The result, or null when the operand is not this module's business.</returns>
    public static PyObject? TryUnary(string op, PyObject operand) => (op, operand) switch
    {
        ("-", PyDelta span) => PyDelta.FromMicroseconds(-span.Total),
        ("+", PyDelta span) => span,
        ("abs", PyDelta span) => PyDelta.FromMicroseconds(BigInteger.Abs(span.Total)),
        _ => null,
    };

    /// <summary>Divides, rounding a half to the even quotient.</summary>
    private static BigInteger Halves(BigInteger value, BigInteger divisor)
    {
        if (divisor.IsZero)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError("division by zero"));
        }

        var quotient = BigInteger.DivRem(value, divisor, out var remainder);

        if (remainder.IsZero)
        {
            return quotient;
        }

        // Compare twice the remainder against the divisor, in absolute terms, so the sign
        // of either operand does not change which way a half goes.
        var twice = BigInteger.Abs(remainder) * 2;
        var size = BigInteger.Abs(divisor);
        var negative = (value.Sign < 0) ^ (divisor.Sign < 0);
        var away = twice > size || (twice == size && !quotient.IsEven);

        return away ? quotient + (negative ? -1 : 1) : quotient;
    }

    /// <summary>Divides, rounding towards negative infinity.</summary>
    private static BigInteger Floors(BigInteger value, BigInteger divisor)
    {
        if (divisor.IsZero)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError("division by zero"));
        }

        var quotient = BigInteger.DivRem(value, divisor, out var remainder);
        return remainder.IsZero || (value.Sign < 0) == (divisor.Sign < 0) ? quotient : quotient - 1;
    }

    /// <summary>Whole days in a microsecond count, rounded towards negative infinity.</summary>
    private static int Whole(BigInteger microseconds) => (int)Floors(microseconds, PyDelta.MicrosecondsPerDay);

    /// <summary>A class object of this module, which knows how to build its instances.</summary>
    internal interface IPyClassLike
    {
        /// <summary>Builds an instance.</summary>
        PyObject Construct(PyObject[] arguments, PyDict? keywords);

        /// <summary>Whether a value is an instance of this class.</summary>
        bool Matches(PyObject value);
    }

    // ---- argument binding -------------------------------------------------

    /// <summary>
    /// Binds arguments the way CPython's datetime constructors do.
    /// </summary>
    /// <remarks>
    /// The order of the complaints is the argument parser's, and it is not the obvious one:
    /// the total count is checked first, a missing required argument beats a duplicate, and
    /// a duplicate beats an unknown keyword.
    /// </remarks>
    private static PyObject?[] Bind(
        string function,
        string[] names,
        int required,
        PyObject[] arguments,
        PyDict? keywords,
        int? maxPositional = null)
    {
        var positionalLimit = maxPositional ?? names.Length;
        var total = arguments.Length + (keywords?.Count ?? 0);

        if (total > names.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{function} takes at most {names.Length} argument{(names.Length == 1 ? string.Empty : "s")} ({total} given)"));
        }

        if (arguments.Length > positionalLimit)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{function} takes at most {positionalLimit} positional arguments ({arguments.Length} given)"));
        }

        var given = new PyObject?[names.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            given[i] = arguments[i];
        }

        string? unknown = null;
        int? duplicate = null;

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var position = Array.IndexOf(names, key.Display());

            if (position < 0)
            {
                unknown ??= key.Display();
                continue;
            }

            if (given[position] is not null)
            {
                duplicate ??= position;
                continue;
            }

            given[position] = value;
        }

        for (var i = 0; i < required; i++)
        {
            if (given[i] is null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{function} missing required argument '{names[i]}' (pos {i + 1})"));
            }
        }

        if (duplicate is { } clash)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"argument for {function} given by name ('{names[clash]}') and position ({clash + 1})"));
        }

        // An anonymous parser calls itself "function" everywhere but here, where CPython
        // spells it "this function".
        return unknown is null
            ? given
            : throw new PyRaise(PyErrors.TypeError(
                $"{(function == "function" ? "this function" : function)} "
                + $"got an unexpected keyword argument '{unknown}'"));
    }

    /// <summary>
    /// Reads a date or time component, which goes through a C long and then a C int.
    /// </summary>
    /// <remarks>
    /// The two conversions have different complaints, and a value can fail either — which is
    /// why 2**40 and 2**100 are reported differently for the same parameter.
    /// </remarks>
    private static int Component(PyObject? value, int fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value is not PyInt number)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"'{value.TypeName}' object cannot be interpreted as an integer"));
        }

        if (BigInteger.Abs(number.Value) > long.MaxValue)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "Python int too large to convert to C long"));
        }

        if (number.Value > int.MaxValue)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "signed integer is greater than maximum"));
        }

        return number.Value < int.MinValue
            ? throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "signed integer is less than minimum"))
            : (int)number.Value;
    }

    /// <summary>Reads a tzinfo argument, which must be a timezone or None.</summary>
    private static PyTimeZone? Zone(PyObject? value) => value switch
    {
        null or PyNone => null,
        PyTimeZone zone => zone,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"tzinfo argument must be None or of a tzinfo subclass, not type '{value.TypeName}'")),
    };

    /// <summary>Reads a <c>strftime</c> format argument and applies it.</summary>
    internal static PyObject Formatted(PyObject[] arguments, PyDict? keywords, Fields parts)
    {
        var given = Bind("strftime()", ["format"], 1, arguments, keywords);

        return given[0] is PyStr text
            ? new PyStr(Expand(parts, text.Value))
            : throw new PyRaise(PyErrors.TypeError(
                $"strftime() argument 1 must be str, not {(given[0] is PyNone ? "None" : given[0]!.TypeName)}"));
    }

    /// <summary>The date and time fields a date or datetime carries.</summary>
    internal readonly record struct Fields(
        int Year,
        int Month,
        int Day,
        int Hour,
        int Minute,
        int Second,
        int Microsecond);

    /// <summary>Expands a <c>strftime</c> format string.</summary>
    /// <remarks>
    /// An unrecognised directive is passed through verbatim, which is what glibc does and
    /// therefore what CPython does on Linux.
    /// </remarks>
    internal static string Expand(Fields parts, string format)
    {
        var builder = new StringBuilder(format.Length);
        var weekday = PyDate.WeekdayOf(PyDate.OrdinalOf(parts.Year, parts.Month, parts.Day));

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length)
            {
                builder.Append(format[i]);
                continue;
            }

            builder.Append(format[++i] switch
            {
                'Y' => parts.Year.ToString("D4", CultureInfo.InvariantCulture),
                'y' => (parts.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
                'm' => parts.Month.ToString("D2", CultureInfo.InvariantCulture),
                'd' => parts.Day.ToString("D2", CultureInfo.InvariantCulture),
                'H' => parts.Hour.ToString("D2", CultureInfo.InvariantCulture),
                'I' => (parts.Hour % 12 == 0 ? 12 : parts.Hour % 12).ToString("D2", CultureInfo.InvariantCulture),
                'p' => parts.Hour < 12 ? "AM" : "PM",
                'M' => parts.Minute.ToString("D2", CultureInfo.InvariantCulture),
                'S' => parts.Second.ToString("D2", CultureInfo.InvariantCulture),
                'f' => parts.Microsecond.ToString("D6", CultureInfo.InvariantCulture),
                'j' => PyDate.DayOfYear(parts.Year, parts.Month, parts.Day).ToString("D3", CultureInfo.InvariantCulture),
                'A' => CultureInfo.InvariantCulture.DateTimeFormat.DayNames[(weekday + 1) % 7],
                'a' => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(weekday + 1) % 7],
                'B' => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(parts.Month),
                'b' => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(parts.Month),
                '%' => "%",
                var other => "%" + other,
            });
        }

        return builder.ToString();
    }

    /// <summary>Reads a datetime out of text, following a <c>strptime</c> format.</summary>
    private static PyObject Strptime(string text, string format)
    {
        int year = 1900, month = 1, day = 1, hour = 0, minute = 0, second = 0, microsecond = 0;
        var at = 0;

        bool Digits(int most, out int value)
        {
            var start = at;

            while (at < text.Length && at - start < most && char.IsAsciiDigit(text[at]))
            {
                at++;
            }

            value = 0;

            return at > start
                && int.TryParse(text.AsSpan(start, at - start), CultureInfo.InvariantCulture, out value);
        }

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length)
            {
                // Literal text has to match, one character for one character.
                if (at >= text.Length || text[at] != format[i])
                {
                    throw Mismatch(text, format);
                }

                at++;
                continue;
            }

            var directive = format[++i];
            var ok = directive switch
            {
                'Y' => Digits(4, out year),
                'm' => Digits(2, out month),
                'd' => Digits(2, out day),
                'H' => Digits(2, out hour),
                'M' => Digits(2, out minute),
                'S' => Digits(2, out second),
                'y' => Digits(2, out year) && Adjust(ref year),
                'f' => Fraction(out microsecond),
                '%' => at < text.Length && text[at] == '%' && ++at > 0,
                _ => throw new PyRaise(PyErrors.ValueError($"'{directive}' is a bad directive in format '{format}'")),
            };

            if (!ok)
            {
                throw Mismatch(text, format);
            }
        }

        if (at != text.Length)
        {
            throw Mismatch(text, format);
        }

        return new PyDateTime(Checked(year, month, day, hour, minute, second, microsecond), null);

        // A two-digit year is 1969..2068, as the stdlib's table says.
        static bool Adjust(ref int value)
        {
            value += value < 69 ? 2000 : 1900;
            return true;
        }

        // `%f` takes one to six digits and pads them out to microseconds.
        bool Fraction(out int value)
        {
            var start = at;

            while (at < text.Length && at - start < 6 && char.IsAsciiDigit(text[at]))
            {
                at++;
            }

            value = 0;

            if (at == start)
            {
                return false;
            }

            var digits = text[start..at];
            value = int.Parse(digits.PadRight(6, '0'), CultureInfo.InvariantCulture);
            return true;
        }
    }

    private static PyRaise Mismatch(string text, string format) =>
        new(PyErrors.ValueError(
            $"time data {PyStr.Quote(text)} does not match format {PyStr.Quote(format)}"));

    /// <summary>Validates a full set of date and time fields, as the constructors do.</summary>
    private static Fields Checked(int year, int month, int day, int hour, int minute, int second, int microsecond)
    {
        if (year is < 1 or > 9999)
        {
            throw new PyRaise(PyErrors.ValueError($"year must be in 1..9999, not {year}"));
        }

        if (month is < 1 or > 12)
        {
            throw new PyRaise(PyErrors.ValueError($"month must be in 1..12, not {month}"));
        }

        var length = PyDate.DaysInMonth(year, month);

        if (day < 1 || day > length)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"day {day} must be in range 1..{length} for month {month} in year {year}"));
        }

        if (hour is < 0 or > 23)
        {
            throw new PyRaise(PyErrors.ValueError($"hour must be in 0..23, not {hour}"));
        }

        if (minute is < 0 or > 59)
        {
            throw new PyRaise(PyErrors.ValueError($"minute must be in 0..59, not {minute}"));
        }

        if (second is < 0 or > 59)
        {
            throw new PyRaise(PyErrors.ValueError($"second must be in 0..59, not {second}"));
        }

        return microsecond is < 0 or > 999999
            ? throw new PyRaise(PyErrors.ValueError($"microsecond must be in 0..999999, not {microsecond}"))
            : new Fields(year, month, day, hour, minute, second, microsecond);
    }

    /// <summary>
    /// The microseconds within the second of a tick count.
    /// </summary>
    /// <remarks>
    /// .NET's `Microsecond` property is the part within the millisecond, 0..999; Python's is
    /// the part within the second, 0..999999.
    /// </remarks>
    /// <param name="ticks">The tick count.</param>
    /// <returns>The microseconds.</returns>
    private static int Microseconds(long ticks) => (int)(ticks % TimeSpan.TicksPerSecond / 10);

    /// <summary>The Unix epoch, as a microsecond count on the same scale as an instant.</summary>
    internal static BigInteger Epoch { get; } = (BigInteger)PyDate.OrdinalOf(1970, 1, 1) * PyDelta.MicrosecondsPerDay;

    /// <summary>Splits a microsecond count into date and time fields.</summary>
    /// <param name="microseconds">The count, on the same scale as an instant.</param>
    /// <returns>The fields.</returns>
    internal static Fields Split(BigInteger microseconds)
    {
        var days = Floors(microseconds, PyDelta.MicrosecondsPerDay);
        var rest = (long)(microseconds - (days * PyDelta.MicrosecondsPerDay));

        if (days < 1 || days > 3652059)
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "date value out of range"));
        }

        var date = PyDate.FromOrdinal((int)days);

        return new Fields(
            date.Year,
            date.Month,
            date.Day,
            (int)(rest / 3_600_000_000L),
            (int)(rest / 60_000_000L % 60),
            (int)(rest / 1_000_000L % 60),
            (int)(rest % 1_000_000L));
    }

    /// <summary>Implements <c>replace</c> for a date or a datetime.</summary>
    /// <param name="value">The value being replaced.</param>
    /// <param name="arguments">The positional arguments, of which there may be none.</param>
    /// <param name="keywords">The fields to change.</param>
    /// <returns>The new value.</returns>
    internal static PyObject Replaced(PyObject value, PyObject[] arguments, PyDict? keywords)
    {
        if (arguments.Length > 0)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"replace() takes 1 positional argument but {arguments.Length + 1} were given"));
        }

        var parts = value switch
        {
            PyDate date => date.Parts,
            _ => ((PyDateTime)value).Parts,
        };

        var zone = value is PyDateTime aware ? aware.TzInfo : null;
        var names = value is PyDate
            ? new[] { "year", "month", "day" }
            : ["year", "month", "day", "hour", "minute", "second", "microsecond", "tzinfo"];

        var given = new PyObject?[names.Length];

        foreach (var (key, item) in keywords?.Entries ?? [])
        {
            var position = Array.IndexOf(names, key.Display());

            if (position < 0)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"replace() got an unexpected keyword argument '{key.Display()}'"));
            }

            given[position] = item;
        }

        var replaced = Checked(
            Component(given[0], parts.Year),
            Component(given[1], parts.Month),
            Component(given[2], parts.Day),
            names.Length > 3 ? Component(given[3], parts.Hour) : 0,
            names.Length > 4 ? Component(given[4], parts.Minute) : 0,
            names.Length > 5 ? Component(given[5], parts.Second) : 0,
            names.Length > 6 ? Component(given[6], parts.Microsecond) : 0);

        if (value is PyDate)
        {
            return new PyDate(replaced.Year, replaced.Month, replaced.Day);
        }

        return new PyDateTime(replaced, given[7] is null ? zone : Zone(given[7]));
    }

    /// <summary>Reads a timedelta component, which is converted through a C int.</summary>
    private static BigInteger Span(PyObject? value, long scale, ref double fraction)
    {
        switch (value)
        {
            case null:
                return BigInteger.Zero;

            case PyInt number when BigInteger.Abs(number.Value) <= int.MaxValue:
                return number.Value * scale;

            case PyInt:
                throw new PyRaise(new PyException(
                    PyExceptionType.OverflowError, "Python int too large to convert to C int"));

            case PyFloat number:
                fraction += number.Value * scale;
                return BigInteger.Zero;

            default:
                throw new PyRaise(PyErrors.TypeError(
                    $"'{value.TypeName}' object cannot be interpreted as an integer"));
        }
    }

    /// <summary>The <c>datetime.timedelta</c> class.</summary>
    private sealed class PyDeltaType : PyCallable, IPyClassLike
    {
        /// <inheritdoc />
        public override string Name => "timedelta";

        /// <inheritdoc />
        public override string TypeName => "type";

        /// <inheritdoc />
        public override string Repr() => "<class 'datetime.timedelta'>";

        /// <inheritdoc />
        public bool Matches(PyObject value) => value is PyDelta;

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "__name__" => new PyStr("timedelta"),
            "min" => PyDelta.FromMicroseconds(new BigInteger(-999_999_999) * PyDelta.MicrosecondsPerDay),
            "max" => PyDelta.FromMicroseconds(
                (new BigInteger(999_999_999) * PyDelta.MicrosecondsPerDay) + (PyDelta.MicrosecondsPerDay - 1)),
            "resolution" => PyDelta.FromMicroseconds(BigInteger.One),
            _ => null,
        };

        /// <inheritdoc />
        public PyObject Construct(PyObject[] arguments, PyDict? keywords)
        {
            string[] names = ["days", "seconds", "microseconds", "milliseconds", "minutes", "hours", "weeks"];
            var given = Bind("timedelta()", names, 0, arguments, keywords);

            // The whole components are summed exactly; only the fractional parts of float
            // components go through a double, and they are rounded once at the end.
            var fraction = 0.0;

            var total = Span(given[0], 86_400_000_000L, ref fraction)
                + Span(given[1], 1_000_000L, ref fraction)
                + Span(given[2], 1L, ref fraction)
                + Span(given[3], 1_000L, ref fraction)
                + Span(given[4], 60_000_000L, ref fraction)
                + Span(given[5], 3_600_000_000L, ref fraction)
                + Span(given[6], 7L * 86_400_000_000L, ref fraction);

            return PyDelta.FromMicroseconds(total + new BigInteger(Math.Round(fraction, MidpointRounding.ToEven)));
        }
    }

    /// <summary>The <c>datetime.timezone</c> class.</summary>
    private sealed class PyTimeZoneType : PyCallable, IPyClassLike
    {
        /// <inheritdoc />
        public override string Name => "timezone";

        /// <inheritdoc />
        public override string TypeName => "type";

        /// <inheritdoc />
        public override string Repr() => "<class 'datetime.timezone'>";

        /// <inheritdoc />
        public bool Matches(PyObject value) => value is PyTimeZone;

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "utc" => PyTimeZone.Utc,
            "__name__" => new PyStr("timezone"),
            _ => null,
        };

        /// <inheritdoc />
        public PyObject Construct(PyObject[] arguments, PyDict? keywords)
        {
            var given = Bind("timezone()", ["offset", "name"], 1, arguments, keywords);

            if (given[0] is not PyDelta offset)
            {
                throw new PyRaise(PyErrors.TypeError(
                    "timezone() argument 1 must be datetime.timedelta, not "
                    + (given[0] is PyNone ? "None" : given[0]!.TypeName)));
            }

            // An explicit None name is not the same as no name at all: the first is a type
            // error, the second the ordinary unnamed zone.
            return given[1] switch
            {
                null => PyTimeZone.Of(offset, null),
                PyStr text => PyTimeZone.Of(offset, text.Value),
                var bad => throw new PyRaise(PyErrors.TypeError(
                    $"timezone() argument 2 must be str, not {(bad is PyNone ? "None" : bad.TypeName)}")),
            };
        }
    }

    /// <summary>The <c>datetime.date</c> class.</summary>
    private sealed class PyDateType(TimeProvider timeProvider) : PyCallable, IPyClassLike
    {
        /// <inheritdoc />
        public override string Name => "date";

        /// <inheritdoc />
        public override string TypeName => "type";

        /// <inheritdoc />
        public override string Repr() => "<class 'datetime.date'>";

        /// <inheritdoc />
        /// <remarks>A datetime is a date, as it is in Python, so it satisfies this too.</remarks>
        public bool Matches(PyObject value) => value is PyDate or PyDateTime;

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "today" => new PyBuiltinFunction("today", _ =>
            {
                var now = timeProvider.GetUtcNow();
                return new PyDate(now.Year, now.Month, now.Day);
            }),

            "fromisoformat" => new PyBuiltinFunction("fromisoformat", static arguments =>
            {
                var text = arguments[0].Display();

                return DateTime.TryParseExact(
                    text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? new PyDate(parsed.Year, parsed.Month, parsed.Day)
                    : throw new PyRaise(PyErrors.ValueError($"Invalid isoformat string: {arguments[0].Repr()}"));
            }),

            "fromordinal" => new PyBuiltinFunction("fromordinal", static arguments =>
                PyDate.FromOrdinal((int)((PyInt)arguments[0]).Value)),

            "min" => new PyDate(1, 1, 1),
            "max" => new PyDate(9999, 12, 31),
            "resolution" => PyDelta.FromMicroseconds(PyDelta.MicrosecondsPerDay),
            "__name__" => new PyStr("date"),
            _ => null,
        };

        /// <inheritdoc />
        public PyObject Construct(PyObject[] arguments, PyDict? keywords)
        {
            var given = Bind("function", ["year", "month", "day"], 3, arguments, keywords);

            var fields = Checked(
                Component(given[0], 1), Component(given[1], 1), Component(given[2], 1), 0, 0, 0, 0);

            return new PyDate(fields.Year, fields.Month, fields.Day);
        }
    }

    /// <summary>The <c>datetime.datetime</c> class.</summary>
    private sealed class PyDateTimeType(TimeProvider timeProvider) : PyCallable, IPyClassLike
    {
        /// <inheritdoc />
        public override string Name => "datetime";

        /// <inheritdoc />
        public override string TypeName => "type";

        /// <inheritdoc />
        public override string Repr() => "<class 'datetime.datetime'>";

        /// <inheritdoc />
        public bool Matches(PyObject value) => value is PyDateTime;

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "now" => new PyBuiltinFunction("now", (arguments, keywords) =>
            {
                var given = Bind("now()", ["tz"], 0, arguments, keywords);
                return Now(Zone(given[0]));
            }),

            "utcnow" => new PyBuiltinFunction("utcnow", _ => Now(null)),

            "fromtimestamp" => new PyBuiltinFunction("fromtimestamp", static arguments =>
            {
                var seconds = arguments[0] switch
                {
                    PyInt number => (double)number.Value,
                    PyFloat number => number.Value,
                    var bad => throw new PyRaise(PyErrors.TypeError(
                        $"an integer is required (got type {bad.TypeName})")),
                };

                var zone = arguments.Length > 1 ? Zone(arguments[1]) : null;
                var instant = Epoch + new BigInteger(Math.Round(seconds * 1_000_000.0, MidpointRounding.ToEven));

                return new PyDateTime(Split(instant + (zone?.Offset.Total ?? BigInteger.Zero)), zone);
            }),

            "utcfromtimestamp" => new PyBuiltinFunction("utcfromtimestamp", static arguments =>
                new PyDateTime(
                    Split(Epoch + new BigInteger(Math.Round(
                        arguments[0] is PyFloat number ? number.Value * 1_000_000.0
                            : (double)((PyInt)arguments[0]).Value * 1_000_000.0,
                        MidpointRounding.ToEven))),
                    null)),

            "fromisoformat" => new PyBuiltinFunction("fromisoformat", static arguments =>
                FromIsoformat(arguments[0])),

            "strptime" => new PyBuiltinFunction("strptime", static arguments =>
                Strptime(arguments[0].Display(), arguments[1].Display())),

            "combine" => new PyBuiltinFunction("combine", static arguments =>
            {
                var date = (PyDate)arguments[0];
                return new PyDateTime(new Fields(date.Year, date.Month, date.Day, 0, 0, 0, 0), null);
            }),

            "min" => new PyDateTime(new Fields(1, 1, 1, 0, 0, 0, 0), null),
            "max" => new PyDateTime(new Fields(9999, 12, 31, 23, 59, 59, 999999), null),
            "resolution" => PyDelta.FromMicroseconds(BigInteger.One),
            "__name__" => new PyStr("datetime"),
            _ => null,
        };

        /// <inheritdoc />
        public PyObject Construct(PyObject[] arguments, PyDict? keywords)
        {
            // `fold` is keyword-only and unsupported here, but it counts towards the arity:
            // the parser accepts nine parameters, only eight of them positionally.
            string[] names =
                ["year", "month", "day", "hour", "minute", "second", "microsecond", "tzinfo", "fold"];

            var given = Bind("function", names, 3, arguments, keywords, maxPositional: 8);

            var fields = Checked(
                Component(given[0], 1),
                Component(given[1], 1),
                Component(given[2], 1),
                Component(given[3], 0),
                Component(given[4], 0),
                Component(given[5], 0),
                Component(given[6], 0));

            return new PyDateTime(fields, Zone(given[7]));
        }

        private PyObject Now(PyTimeZone? zone)
        {
            var utc = timeProvider.GetUtcNow();

            var instant = ((BigInteger)PyDate.OrdinalOf(utc.Year, utc.Month, utc.Day) * PyDelta.MicrosecondsPerDay)
                + (((((long)utc.Hour * 60) + utc.Minute) * 60L + utc.Second) * 1_000_000L)
                + Microseconds(utc.Ticks);

            return new PyDateTime(Split(instant + (zone?.Offset.Total ?? BigInteger.Zero)), zone);
        }
    }

    /// <summary>Parses the ISO 8601 forms <c>datetime.fromisoformat</c> accepts.</summary>
    private static PyObject FromIsoformat(PyObject argument)
    {
        var text = argument.Display();
        PyTimeZone? zone = null;

        // A trailing offset is peeled off first, so what remains is a plain local time.
        var mark = text.LastIndexOfAny(['+', '-']);

        if (mark > 9)
        {
            var offset = text[mark..];
            text = text[..mark];

            var sign = offset[0] == '-' ? -1 : 1;
            var pieces = offset[1..].Split(':');
            var seconds = (long.Parse(pieces[0], CultureInfo.InvariantCulture) * 3600)
                + (pieces.Length > 1 ? long.Parse(pieces[1], CultureInfo.InvariantCulture) * 60 : 0)
                + (pieces.Length > 2 ? long.Parse(pieces[2], CultureInfo.InvariantCulture) : 0);

            zone = PyTimeZone.Of(PyDelta.FromMicroseconds(new BigInteger(sign * seconds) * 1_000_000), null);
        }

        string[] layouts =
        [
            "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm",
            "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss.ffffff",
        ];

        return DateTime.TryParseExact(
            text, layouts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? new PyDateTime(
                new Fields(
                    parsed.Year,
                    parsed.Month,
                    parsed.Day,
                    parsed.Hour,
                    parsed.Minute,
                    parsed.Second,
                    Microseconds(parsed.Ticks)),
                zone)
            : throw new PyRaise(PyErrors.ValueError($"Invalid isoformat string: {argument.Repr()}"));
    }
}
