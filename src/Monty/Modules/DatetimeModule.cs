using System.Globalization;
using Monty.Runtime;

namespace Monty.Modules;

/// <summary>The <c>datetime</c> module.</summary>
/// <remarks>
/// The clock is supplied by the host, so <c>datetime.now()</c> is reproducible and the
/// sandbox cannot read the real wall clock as a fingerprint.
/// </remarks>
public static class DatetimeModule
{
    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(TimeProvider timeProvider)
    {
        var module = new PyModuleObject("datetime");

        module.Add("datetime", new PyDateTimeType(timeProvider));
        module.Add("date", new PyDateType(timeProvider));
        module.Add("timedelta", new PyBuiltinFunction("timedelta", static (arguments, keywords) =>
        {
            double Read(string name, int position) =>
                position < arguments.Length ? ToDouble(arguments[position])
                : keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? ToDouble(value)
                : 0;

            var span = TimeSpan.FromDays(Read("days", 0))
                + TimeSpan.FromSeconds(Read("seconds", 1))
                + TimeSpan.FromMicroseconds(Read("microseconds", 2))
                + TimeSpan.FromMilliseconds(Read("milliseconds", 3))
                + TimeSpan.FromMinutes(Read("minutes", 4))
                + TimeSpan.FromHours(Read("hours", 5))
                + TimeSpan.FromDays(Read("weeks", 6) * 7);

            return new PyTimeDelta(span);
        }));

        return module;
    }

    /// <summary>
    /// Constructs a datetime or date when <paramref name="callable"/> is one of this
    /// module's class objects. Returns null otherwise, so the VM can fall through.
    /// </summary>
    public static PyObject? TryConstruct(PyCallable callable, PyObject[] arguments) => callable switch
    {
        PyDateTimeType type => type.Construct(arguments),
        PyDateType type => type.Construct(arguments),
        _ => null,
    };

    private static double ToDouble(PyObject value) => value switch
    {
        PyInt integer => (double)integer.Value,
        PyFloat number => number.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"unsupported type for timedelta: {value.TypeName}")),
    };

    /// <summary>The <c>datetime.datetime</c> class.</summary>
    private sealed class PyDateTimeType(TimeProvider timeProvider) : PyCallable
    {
        /// <inheritdoc />
        public override string Name => "datetime";

        /// <inheritdoc />
        public override string TypeName => "type";

        /// <inheritdoc />
        public override string Repr() => "<class 'datetime.datetime'>";

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "now" or "utcnow" => new PyBuiltinFunction(name, _ => new PyDateTime(timeProvider.GetUtcNow())),

            "fromtimestamp" or "utcfromtimestamp" => new PyBuiltinFunction(name, static arguments =>
                new PyDateTime(DateTimeOffset.FromUnixTimeMilliseconds((long)(ToDouble(arguments[0]) * 1000)))),

            "fromisoformat" => new PyBuiltinFunction(name, static arguments =>
                DateTimeOffset.TryParse(
                    arguments[0].Display(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                    ? new PyDateTime(parsed)
                    : throw new PyRaise(PyErrors.ValueError($"Invalid isoformat string: {arguments[0].Repr()}"))),

            "min" => new PyDateTime(DateTimeOffset.MinValue),
            "max" => new PyDateTime(DateTimeOffset.MaxValue),
            "__name__" => new PyStr("datetime"),
            _ => null,
        };

        /// <summary>Constructs from year, month, day and optional time parts.</summary>
        public PyObject Construct(PyObject[] arguments)
        {
            int Part(int index, int fallback) =>
                index < arguments.Length && arguments[index] is PyInt value ? (int)value.Value : fallback;

            return new PyDateTime(new DateTimeOffset(
                Part(0, 1), Part(1, 1), Part(2, 1),
                Part(3, 0), Part(4, 0), Part(5, 0),
                TimeSpan.Zero));
        }
    }

    /// <summary>The <c>datetime.date</c> class.</summary>
    private sealed class PyDateType(TimeProvider timeProvider) : PyCallable
    {
        /// <inheritdoc />
        public override string Name => "date";

        /// <inheritdoc />
        public override string TypeName => "type";

        /// <inheritdoc />
        public override string Repr() => "<class 'datetime.date'>";

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "today" => new PyBuiltinFunction("today", _ => new PyDateTime(timeProvider.GetUtcNow().Date)),
            "__name__" => new PyStr("date"),
            _ => null,
        };

        /// <summary>Constructs from year, month and day.</summary>
        public PyObject Construct(PyObject[] arguments)
        {
            int Part(int index) => arguments[index] is PyInt value ? (int)value.Value : 1;

            return new PyDateTime(new DateTimeOffset(Part(0), Part(1), Part(2), 0, 0, 0, TimeSpan.Zero));
        }
    }

    /// <summary>A <c>datetime</c> instance.</summary>
    internal sealed class PyDateTime(DateTimeOffset value) : PyObject
    {
        /// <summary>The wrapped instant.</summary>
        public DateTimeOffset Value => value;

        /// <inheritdoc />
        public override string TypeName => "datetime";

        /// <inheritdoc />
        public override string Display() => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public override string Repr() =>
            $"datetime.datetime({value.Year}, {value.Month}, {value.Day}, {value.Hour}, {value.Minute}, {value.Second})";

        /// <inheritdoc />
        public override bool PyEquals(PyObject other) => other is PyDateTime instant && value == instant.Value;

        /// <inheritdoc />
        public override int? PyCompare(PyObject other) =>
            other is PyDateTime instant ? value.CompareTo(instant.Value) : null;

        /// <inheritdoc />
        public override System.Numerics.BigInteger PyHash() => value.UtcTicks;

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "year" => new PyInt(value.Year),
            "month" => new PyInt(value.Month),
            "day" => new PyInt(value.Day),
            "hour" => new PyInt(value.Hour),
            "minute" => new PyInt(value.Minute),
            "second" => new PyInt(value.Second),
            "microsecond" => new PyInt(value.Microsecond),

            "isoformat" => new PyBuiltinFunction("isoformat", _ =>
                new PyStr(value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))),

            "strftime" => new PyBuiltinFunction("strftime", arguments =>
                new PyStr(Strftime(value, arguments[0].Display()))),

            "timestamp" => new PyBuiltinFunction("timestamp", _ =>
                new PyFloat(value.ToUnixTimeMilliseconds() / 1000.0)),

            "date" => new PyBuiltinFunction("date", _ => new PyDateTime(value.Date)),
            "weekday" => new PyBuiltinFunction("weekday", _ => new PyInt(((int)value.DayOfWeek + 6) % 7)),
            "isoweekday" => new PyBuiltinFunction("isoweekday", _ =>
                new PyInt((int)value.DayOfWeek == 0 ? 7 : (int)value.DayOfWeek)),

            _ => null,
        };

        /// <summary>The strftime specifiers scripts actually use.</summary>
        private static string Strftime(DateTimeOffset moment, string format)
        {
            var builder = new System.Text.StringBuilder(format.Length);
            var time = moment.UtcDateTime;

            for (var i = 0; i < format.Length; i++)
            {
                if (format[i] != '%' || i + 1 >= format.Length)
                {
                    builder.Append(format[i]);
                    continue;
                }

                builder.Append(format[++i] switch
                {
                    'Y' => time.Year.ToString("D4", CultureInfo.InvariantCulture),
                    'y' => (time.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
                    'm' => time.Month.ToString("D2", CultureInfo.InvariantCulture),
                    'd' => time.Day.ToString("D2", CultureInfo.InvariantCulture),
                    'H' => time.Hour.ToString("D2", CultureInfo.InvariantCulture),
                    'M' => time.Minute.ToString("D2", CultureInfo.InvariantCulture),
                    'S' => time.Second.ToString("D2", CultureInfo.InvariantCulture),
                    'j' => time.DayOfYear.ToString("D3", CultureInfo.InvariantCulture),
                    'A' => time.DayOfWeek.ToString(),
                    'a' => time.DayOfWeek.ToString()[..3],
                    'B' => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(time.Month),
                    'b' => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(time.Month),
                    '%' => "%",
                    var other => "%" + other,
                });
            }

            return builder.ToString();
        }
    }

    /// <summary>A <c>timedelta</c>.</summary>
    internal sealed class PyTimeDelta(TimeSpan value) : PyObject
    {
        /// <summary>The wrapped span.</summary>
        public TimeSpan Value => value;

        /// <inheritdoc />
        public override string TypeName => "timedelta";

        /// <inheritdoc />
        public override string Repr() => $"datetime.timedelta(seconds={(long)value.TotalSeconds})";

        /// <inheritdoc />
        public override bool IsTruthy() => value != TimeSpan.Zero;

        /// <inheritdoc />
        public override bool PyEquals(PyObject other) => other is PyTimeDelta span && value == span.Value;

        /// <inheritdoc />
        public override int? PyCompare(PyObject other) =>
            other is PyTimeDelta span ? value.CompareTo(span.Value) : null;

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "days" => new PyInt(value.Days),
            "seconds" => new PyInt((int)value.TotalSeconds % 86400),
            "microseconds" => new PyInt(value.Microseconds),
            "total_seconds" => new PyBuiltinFunction("total_seconds", _ => new PyFloat(value.TotalSeconds)),
            _ => null,
        };
    }
}
