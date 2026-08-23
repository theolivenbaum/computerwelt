using System.Globalization;
using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>A <c>datetime.timedelta</c>.</summary>
/// <remarks>
/// Normalised the way CPython's is — days, then seconds in 0..86399, then microseconds in
/// 0..999999 — because that normalisation is visible: `timedelta(seconds=-1).days` is -1.
/// </remarks>
public sealed class PyDelta : PyObject
{
    /// <summary>Microseconds in one day.</summary>
    internal const long MicrosecondsPerDay = 86_400_000_000L;

    private PyDelta(int days, int seconds, int microseconds)
    {
        Days = days;
        Seconds = seconds;
        Microseconds = microseconds;
    }

    /// <summary>Whole days, which carry the sign.</summary>
    public int Days { get; }

    /// <summary>Seconds within the day, always 0..86399.</summary>
    public int Seconds { get; }

    /// <summary>Microseconds within the second, always 0..999999.</summary>
    public int Microseconds { get; }

    /// <summary>The whole span as a microsecond count.</summary>
    internal BigInteger Total =>
        ((BigInteger)Days * MicrosecondsPerDay) + ((long)Seconds * 1_000_000L) + Microseconds;

    /// <inheritdoc />
    public override string TypeName => "datetime.timedelta";

    /// <summary>Builds a span from a microsecond count.</summary>
    /// <param name="microseconds">The count.</param>
    /// <returns>The span.</returns>
    internal static PyDelta FromMicroseconds(BigInteger microseconds)
    {
        var days = BigInteger.Divide(microseconds, MicrosecondsPerDay);
        var rest = microseconds - (days * MicrosecondsPerDay);

        // The remainder must be non-negative, which means borrowing a day when it is not.
        if (rest.Sign < 0)
        {
            days -= 1;
            rest += MicrosecondsPerDay;
        }

        if (BigInteger.Abs(days) > 999_999_999)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, $"days={days}; must have magnitude <= 999999999"));
        }

        return new PyDelta((int)days, (int)(rest / 1_000_000), (int)(rest % 1_000_000));
    }

    /// <inheritdoc />
    public override string Repr()
    {
        var parts = new List<string>(3);

        if (Days != 0)
        {
            parts.Add($"days={Days}");
        }

        if (Seconds != 0)
        {
            parts.Add($"seconds={Seconds}");
        }

        if (Microseconds != 0)
        {
            parts.Add($"microseconds={Microseconds}");
        }

        return parts.Count == 0
            ? "datetime.timedelta(0)"
            : "datetime.timedelta(" + string.Join(", ", parts) + ")";
    }

    /// <inheritdoc />
    public override string Display()
    {
        var text = new StringBuilder();

        if (Days != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"{Days} day{(Math.Abs(Days) == 1 ? string.Empty : "s")}, ");
        }

        text.Append(CultureInfo.InvariantCulture, $"{Seconds / 3600}:{Seconds / 60 % 60:D2}:{Seconds % 60:D2}");

        if (Microseconds != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $".{Microseconds:D6}");
        }

        return text.ToString();
    }

    /// <inheritdoc />
    public override bool IsTruthy() => !Total.IsZero;

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other is PyDelta span && Total == span.Total;

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) => other is PyDelta span ? Total.CompareTo(span.Total) : null;

    /// <inheritdoc />
    public override BigInteger PyHash() => Numbers.Hash(Total);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "days" => PyInt.From(Days),
        "seconds" => PyInt.From(Seconds),
        "microseconds" => PyInt.From(Microseconds),
        "total_seconds" => new PyBuiltinFunction("total_seconds", _ =>
            new PyFloat((double)Total / 1_000_000.0)),
        _ => null,
    };
}

/// <summary>A <c>datetime.timezone</c>: a fixed offset from UTC, optionally named.</summary>
public sealed class PyTimeZone : PyObject
{
    private PyTimeZone(PyDelta offset, string? name)
    {
        Offset = offset;
        Name = name;
    }

    /// <summary>The UTC singleton, which <c>timezone(timedelta(0))</c> also returns.</summary>
    public static PyTimeZone Utc { get; } = new(PyDelta.FromMicroseconds(BigInteger.Zero), null);

    /// <summary>The offset from UTC.</summary>
    public PyDelta Offset { get; }

    /// <summary>The name given at construction, if any.</summary>
    public string? Name { get; }

    /// <inheritdoc />
    public override string TypeName => "datetime.timezone";

    /// <summary>Builds a timezone, returning the UTC singleton for a zero unnamed offset.</summary>
    /// <param name="offset">The offset from UTC.</param>
    /// <param name="name">The name, or null for none.</param>
    /// <returns>The timezone.</returns>
    public static PyTimeZone Of(PyDelta offset, string? name)
    {
        if (BigInteger.Abs(offset.Total) >= 24L * 3600L * 1_000_000L)
        {
            throw new PyRaise(PyErrors.ValueError(
                "offset must be a timedelta strictly between -timedelta(hours=24) and "
                + $"timedelta(hours=24), not {offset.Repr()}"));
        }

        // `timezone(timedelta(0)) is timezone.utc` holds, which scripts check.
        return name is null && offset.Total.IsZero ? Utc : new PyTimeZone(offset, name);
    }

    /// <inheritdoc />
    public override string Repr()
    {
        if (ReferenceEquals(this, Utc))
        {
            return "datetime.timezone.utc";
        }

        return Name is null
            ? $"datetime.timezone({Offset.Repr()})"
            : $"datetime.timezone({Offset.Repr()}, {PyStr.Quote(Name)})";
    }

    /// <inheritdoc />
    public override string Display()
    {
        if (Name is not null)
        {
            return Name;
        }

        if (Offset.Total.IsZero)
        {
            return "UTC";
        }

        var total = (long)BigInteger.Abs(Offset.Total) / 1_000_000L;
        var seconds = total % 60;
        var text = $"UTC{(Offset.Total.Sign < 0 ? "-" : "+")}{total / 3600:D2}:{total / 60 % 60:D2}";

        return seconds == 0 ? text : text + $":{seconds:D2}";
    }

    /// <inheritdoc />
    /// <remarks>Two zones are equal by offset alone; the name plays no part, as CPython's does not.</remarks>
    public override bool PyEquals(PyObject other) => other is PyTimeZone zone && Offset.Total == zone.Offset.Total;

    /// <inheritdoc />
    public override BigInteger PyHash() => Numbers.Hash(Offset.Total);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "utcoffset" => new PyBuiltinFunction("utcoffset", _ => Offset),
        "tzname" => new PyBuiltinFunction("tzname", _ => new PyStr(Display())),
        _ => null,
    };
}

/// <summary>A <c>datetime.date</c>.</summary>
public sealed class PyDate : PyObject
{
    private static readonly int[] MonthLengths = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary>Creates a date from validated fields.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    internal PyDate(int year, int month, int day)
    {
        Year = year;
        Month = month;
        Day = day;
    }

    /// <summary>The year.</summary>
    public int Year { get; }

    /// <summary>The month.</summary>
    public int Month { get; }

    /// <summary>The day.</summary>
    public int Day { get; }

    /// <summary>The proleptic Gregorian ordinal, with 0001-01-01 as day 1.</summary>
    internal int Ordinal => OrdinalOf(Year, Month, Day);

    /// <inheritdoc />
    public override string TypeName => "datetime.date";

    /// <summary>Whether a year has a leap day.</summary>
    /// <param name="year">The year.</param>
    /// <returns>True when February has 29 days.</returns>
    public static bool IsLeap(int year) => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;

    /// <summary>The number of days in a month.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <returns>The day count.</returns>
    public static int DaysInMonth(int year, int month) =>
        month == 2 && IsLeap(year) ? 29 : MonthLengths[month - 1];

    /// <summary>The day of the year, counting from 1.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <returns>The ordinal within the year.</returns>
    public static int DayOfYear(int year, int month, int day)
    {
        var total = day;

        for (var i = 1; i < month; i++)
        {
            total += DaysInMonth(year, i);
        }

        return total;
    }

    /// <summary>The proleptic Gregorian ordinal of a date, with 0001-01-01 as day 1.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <returns>The ordinal.</returns>
    public static int OrdinalOf(int year, int month, int day)
    {
        var years = year - 1;
        return (years * 365) + (years / 4) - (years / 100) + (years / 400) + DayOfYear(year, month, day);
    }

    /// <summary>The weekday of an ordinal, Monday being 0.</summary>
    /// <param name="ordinal">The proleptic Gregorian ordinal.</param>
    /// <returns>The weekday.</returns>
    public static int WeekdayOf(int ordinal) => (ordinal + 6) % 7;

    /// <summary>Builds a date from a proleptic Gregorian ordinal.</summary>
    /// <param name="ordinal">The ordinal, with 0001-01-01 as day 1.</param>
    /// <returns>The date.</returns>
    public static PyDate FromOrdinal(int ordinal)
    {
        if (ordinal is < 1 or > 3652059)
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "date value out of range"));
        }

        // Walk the 400-year cycle down to a year, then the months within it. The arithmetic
        // is exact and small, so the loop costs nothing worth optimising away.
        var year = 1;
        var left = ordinal;

        while (true)
        {
            var length = IsLeap(year) ? 366 : 365;

            if (left <= length)
            {
                break;
            }

            left -= length;
            year++;
        }

        var month = 1;

        while (left > DaysInMonth(year, month))
        {
            left -= DaysInMonth(year, month);
            month++;
        }

        return new PyDate(year, month, left);
    }

    /// <inheritdoc />
    public override string Repr() => $"datetime.date({Year}, {Month}, {Day})";

    /// <inheritdoc />
    public override string Display() => $"{Year:D4}-{Month:D2}-{Day:D2}";

    /// <inheritdoc />
    public override bool IsTruthy() => true;

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other is PyDate date && Ordinal == date.Ordinal;

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) => other is PyDate date ? Ordinal.CompareTo(date.Ordinal) : null;

    /// <inheritdoc />
    public override BigInteger PyHash() => Numbers.Hash(new BigInteger(Ordinal));

    /// <inheritdoc />
    /// <remarks>A date's format spec is a strftime pattern, not the mini-language.</remarks>
    public override string? PyFormat(string spec) =>
        spec.Length == 0 ? Display() : DatetimeModule.Expand(Parts, spec);

    /// <summary>This date's fields, with the time at midnight.</summary>
    internal DatetimeModule.Fields Parts => new(Year, Month, Day, 0, 0, 0, 0);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "year" => PyInt.From(Year),
        "month" => PyInt.From(Month),
        "day" => PyInt.From(Day),
        "isoformat" => new PyBuiltinFunction("isoformat", _ => new PyStr(Display())),
        "strftime" => new PyBuiltinFunction("strftime", (arguments, keywords) =>
            DatetimeModule.Formatted(arguments, keywords, Parts)),
        "weekday" => new PyBuiltinFunction("weekday", _ => PyInt.From(WeekdayOf(Ordinal))),
        "isoweekday" => new PyBuiltinFunction("isoweekday", _ => PyInt.From(WeekdayOf(Ordinal) + 1)),
        "toordinal" => new PyBuiltinFunction("toordinal", _ => PyInt.From(Ordinal)),
        "replace" => new PyBuiltinFunction("replace", (arguments, keywords) =>
            DatetimeModule.Replaced(this, arguments, keywords)),
        _ => null,
    };
}

/// <summary>A <c>datetime.datetime</c>.</summary>
/// <remarks>
/// A subclass of <c>date</c>, as it is in Python, so `isinstance(dt, date)` holds — while
/// the two never compare equal, because a date and a datetime are different values.
/// </remarks>
public sealed class PyDateTime : PyObject
{
    internal PyDateTime(DatetimeModule.Fields fields, PyTimeZone? zone)
    {
        Parts = fields;
        TzInfo = zone;
    }

    /// <summary>The date and time fields.</summary>
    internal DatetimeModule.Fields Parts { get; }

    /// <summary>The timezone, or null when the value is naive.</summary>
    public PyTimeZone? TzInfo { get; }

    /// <inheritdoc />
    public override string TypeName => "datetime.datetime";

    /// <summary>The instant as a microsecond count, offset applied when the value is aware.</summary>
    internal BigInteger Instant
    {
        get
        {
            var local = ((BigInteger)PyDate.OrdinalOf(Parts.Year, Parts.Month, Parts.Day) * PyDelta.MicrosecondsPerDay)
                + (((((long)Parts.Hour * 60) + Parts.Minute) * 60L + Parts.Second) * 1_000_000L)
                + Parts.Microsecond;

            return TzInfo is null ? local : local - TzInfo.Offset.Total;
        }
    }

    /// <summary>This datetime moved by a microsecond count, keeping its timezone.</summary>
    /// <param name="microseconds">How far to move.</param>
    /// <returns>The new datetime.</returns>
    internal PyDateTime Shifted(BigInteger microseconds)
    {
        var local = ((BigInteger)PyDate.OrdinalOf(Parts.Year, Parts.Month, Parts.Day) * PyDelta.MicrosecondsPerDay)
            + (((((long)Parts.Hour * 60) + Parts.Minute) * 60L + Parts.Second) * 1_000_000L)
            + Parts.Microsecond
            + microseconds;

        return new PyDateTime(DatetimeModule.Split(local), TzInfo);
    }

    /// <inheritdoc />
    public override string Repr()
    {
        var text = new StringBuilder(
            $"datetime.datetime({Parts.Year}, {Parts.Month}, {Parts.Day}, {Parts.Hour}, {Parts.Minute}");

        // Seconds and microseconds are shown only when they carry information, and the
        // microseconds cannot appear without the seconds before them.
        if (Parts.Second != 0 || Parts.Microsecond != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $", {Parts.Second}");
        }

        if (Parts.Microsecond != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $", {Parts.Microsecond}");
        }

        if (TzInfo is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $", tzinfo={TzInfo.Repr()}");
        }

        return text.Append(')').ToString();
    }

    /// <inheritdoc />
    public override string Display() => Isoformat(' ');

    /// <summary>Formats as an ISO 8601 string.</summary>
    /// <param name="separator">What stands between the date and the time.</param>
    /// <returns>The formatted text.</returns>
    public string Isoformat(char separator)
    {
        var text = new StringBuilder(
            $"{Parts.Year:D4}-{Parts.Month:D2}-{Parts.Day:D2}{separator}{Parts.Hour:D2}:{Parts.Minute:D2}:{Parts.Second:D2}");

        if (Parts.Microsecond != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $".{Parts.Microsecond:D6}");
        }

        if (TzInfo is not null)
        {
            var total = (long)BigInteger.Abs(TzInfo.Offset.Total) / 1_000_000L;
            text.Append(CultureInfo.InvariantCulture,
                $"{(TzInfo.Offset.Total.Sign < 0 ? '-' : '+')}{total / 3600:D2}:{total / 60 % 60:D2}");

            if (total % 60 != 0)
            {
                text.Append(CultureInfo.InvariantCulture, $":{total % 60:D2}");
            }
        }

        return text.ToString();
    }

    /// <inheritdoc />
    public override bool IsTruthy() => true;

    /// <inheritdoc />
    /// <remarks>
    /// An aware and a naive datetime are never equal: there is no instant to compare, since
    /// the naive one names no point on the timeline.
    /// </remarks>
    public override bool PyEquals(PyObject other) =>
        other is PyDateTime moment && (TzInfo is null) == (moment.TzInfo is null) && Instant == moment.Instant;

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) =>
        other is PyDateTime moment && (TzInfo is null) == (moment.TzInfo is null)
            ? Instant.CompareTo(moment.Instant)
            : null;

    /// <inheritdoc />
    public override BigInteger PyHash() => Numbers.Hash(Instant);

    /// <inheritdoc />
    /// <remarks>A datetime's format spec is a strftime pattern, not the mini-language.</remarks>
    public override string? PyFormat(string spec) =>
        spec.Length == 0 ? Display() : DatetimeModule.Expand(Parts, spec);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "year" => PyInt.From(Parts.Year),
        "month" => PyInt.From(Parts.Month),
        "day" => PyInt.From(Parts.Day),
        "hour" => PyInt.From(Parts.Hour),
        "minute" => PyInt.From(Parts.Minute),
        "second" => PyInt.From(Parts.Second),
        "microsecond" => PyInt.From(Parts.Microsecond),
        "tzinfo" => TzInfo ?? (PyObject)PyNone.Instance,

        "isoformat" => new PyBuiltinFunction("isoformat", _ => new PyStr(Isoformat('T'))),
        "strftime" => new PyBuiltinFunction("strftime", (arguments, keywords) =>
            DatetimeModule.Formatted(arguments, keywords, Parts)),

        "date" => new PyBuiltinFunction("date", _ => new PyDate(Parts.Year, Parts.Month, Parts.Day)),
        "weekday" => new PyBuiltinFunction("weekday", _ =>
            PyInt.From(PyDate.WeekdayOf(PyDate.OrdinalOf(Parts.Year, Parts.Month, Parts.Day)))),
        "isoweekday" => new PyBuiltinFunction("isoweekday", _ =>
            PyInt.From(PyDate.WeekdayOf(PyDate.OrdinalOf(Parts.Year, Parts.Month, Parts.Day)) + 1)),

        // A naive datetime has no offset to apply, so it is read as UTC: the sandbox has no
        // local zone, and inventing one from the host would be neither reproducible nor safe.
        "timestamp" => new PyBuiltinFunction("timestamp", _ =>
            new PyFloat((double)(Instant - DatetimeModule.Epoch) / 1_000_000.0)),

        "replace" => new PyBuiltinFunction("replace", (arguments, keywords) =>
            DatetimeModule.Replaced(this, arguments, keywords)),

        _ => null,
    };
}
