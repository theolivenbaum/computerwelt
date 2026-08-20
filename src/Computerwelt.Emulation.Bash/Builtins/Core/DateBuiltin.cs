using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>date</c> — prints or formats a date.
/// </summary>
/// <remarks>
/// <para>
/// The clock comes from a <see cref="TimeProvider"/> so that a host can pin it. A sandbox
/// that a script can use to read the real wall clock is a fingerprinting channel, and a
/// pinned clock is what makes snapshot/restore and reproducible tests possible.
/// </para>
/// <para>
/// Format specifiers are implemented directly rather than mapped onto .NET custom format
/// strings: strftime's set is larger, its escapes differ, and a translation layer would
/// have to be exactly right in both directions.
/// </para>
/// </remarks>
public sealed class DateBuiltin : IBuiltin
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the builtin reading from <paramref name="timeProvider"/>.</summary>
    public DateBuiltin(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public string Name => "date";

    /// <inheritdoc />
    public string? LlmHint => "date: Prints the date. Supports +FORMAT strftime specifiers, -u, -d, -r, --iso-8601.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: date [+FORMAT] [-u] [-R] [-I[TIMESPEC]] [-d STRING] [-r FILE]
        Display the current time in the given FORMAT.

          -u            use UTC
          -R            output in RFC 5322 format
          -I[SPEC]      output in ISO 8601 format
          -d STRING     display the time described by STRING
          -r FILE       display FILE's modification time
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (CommandHelp.Handle(context.Arguments, Help!, "date 0.1.0") is { } help)
        {
            return help;
        }

        var cursor = new ArgCursor(context.Arguments);
        var utc = false;
        string? dateSpec = null;
        string? referenceFile = null;
        string? format = null;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-u" or "--utc" or "--universal": utc = true; break;
                case "-d" or "--date": dateSpec = cursor.TakeValue(); break;
                case "-r" or "--reference": referenceFile = cursor.TakeValue(); break;
                case "-R" or "--rfc-2822": format = "%a, %d %b %Y %H:%M:%S %z"; break;
                case "-I" or "--iso-8601": format = "%Y-%m-%d"; break;
                case "-s" or "--set":
                    return ExecResult.Error("date: cannot set date: Operation not permitted\n", ExitCodes.Failure);

                default:
                    return ExecResult.Usage("date", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        foreach (var operand in cursor.Operands)
        {
            if (operand.StartsWith('+'))
            {
                format = operand[1..];
                continue;
            }

            return ExecResult.Usage("date", $"invalid date '{operand}'");
        }

        var moment = _timeProvider.GetUtcNow();

        // A fixed epoch pins the clock for reproducible runs.
        if (long.TryParse(context.State.Get("BASHKIT_EPOCH"), CultureInfo.InvariantCulture, out var fixedEpoch))
        {
            moment = DateTimeOffset.FromUnixTimeSeconds(fixedEpoch);
        }

        if (referenceFile is not null)
        {
            try
            {
                moment = (await context.FileSystem.StatAsync(context.ResolvePath(referenceFile), cancellationToken)).ModifiedAt;
            }
            catch (FileSystemException)
            {
                return ExecResult.Error($"date: cannot stat '{referenceFile}': No such file or directory\n", ExitCodes.Failure);
            }
        }

        if (dateSpec is not null)
        {
            if (!TryResolveDateSpec(dateSpec, moment, out moment))
            {
                return ExecResult.Usage("date", $"invalid date '{dateSpec}'");
            }
        }

        _ = utc; // The sandbox clock is UTC; there is no local timezone to differ from.

        return ExecResult.Ok(Format(format ?? "%a %b %e %H:%M:%S %Z %Y", moment) + "\n");
    }

    /// <summary>
    /// Resolves the subset of <c>-d</c> expressions that scripts actually rely on:
    /// <c>@epoch</c>, an absolute timestamp, <c>now</c>, and relative offsets such as
    /// <c>"2 days ago"</c> or <c>"+1 hour"</c>.
    /// </summary>
    private static bool TryResolveDateSpec(string spec, DateTimeOffset now, out DateTimeOffset resolved)
    {
        resolved = now;
        var text = spec.Trim();

        if (text.Length == 0 || text.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A compound spec is a base plus or minus an offset: `2024-01-15 + 30 days`.
        var compound = FindCompoundOperator(text);

        if (compound > 0)
        {
            var basis = text[..compound].Trim();
            var negative = text[compound] == '-';
            var offset = text[(compound + 1)..].Trim();

            return TryResolveDateSpec(basis, now, out var start)
                && TryResolveRelative(negative ? offset + " ago" : offset, start, out resolved);
        }

        if (text.StartsWith('@'))
        {
            if (!long.TryParse(text[1..], CultureInfo.InvariantCulture, out var epoch))
            {
                return false;
            }

            resolved = DateTimeOffset.FromUnixTimeSeconds(epoch);
            return true;
        }

        if (text.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            resolved = new DateTimeOffset(now.Date, TimeSpan.Zero);
            return true;
        }

        if (text.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
        {
            resolved = new DateTimeOffset(now.Date, TimeSpan.Zero).AddDays(-1);
            return true;
        }

        if (text.Equals("tomorrow", StringComparison.OrdinalIgnoreCase))
        {
            resolved = new DateTimeOffset(now.Date, TimeSpan.Zero).AddDays(1);
            return true;
        }

        if (TryResolveRelative(text, now, out resolved))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out resolved))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the <c>+</c> or <c>-</c> joining a base date to a relative offset.
    /// </summary>
    /// <remarks>
    /// Only a sign surrounded by blanks counts, so the hyphens inside <c>2024-01-15</c> are
    /// not mistaken for one.
    /// </remarks>
    private static int FindCompoundOperator(string text)
    {
        for (var i = 1; i < text.Length - 1; i++)
        {
            if (text[i] is '+' or '-' && char.IsWhiteSpace(text[i - 1]) && char.IsWhiteSpace(text[i + 1]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryResolveRelative(string text, DateTimeOffset now, out DateTimeOffset resolved)
    {
        resolved = now;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ago = words.Length > 0 && words[^1].Equals("ago", StringComparison.OrdinalIgnoreCase);

        if (ago)
        {
            words = words[..^1];
        }

        if (words.Length == 0)
        {
            return false;
        }

        var applied = false;

        for (var i = 0; i + 1 < words.Length; i += 2)
        {
            if (!double.TryParse(words[i], NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
            {
                return false;
            }

            if (ago)
            {
                amount = -amount;
            }

            var unit = words[i + 1].TrimEnd('s').ToLowerInvariant();

            resolved = unit switch
            {
                "second" or "sec" => resolved.AddSeconds(amount),
                "minute" or "min" => resolved.AddMinutes(amount),
                "hour" => resolved.AddHours(amount),
                "day" => resolved.AddDays(amount),
                "week" => resolved.AddDays(amount * 7),
                "month" => resolved.AddMonths((int)amount),
                "year" => resolved.AddYears((int)amount),
                _ => resolved,
            };

            applied = true;
        }

        return applied;
    }

    /// <summary>Applies a strftime format string.</summary>
    internal static string Format(string format, DateTimeOffset moment)
    {
        var builder = new StringBuilder(format.Length);
        var time = moment.UtcDateTime;

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '\\' && i + 1 < format.Length)
            {
                builder.Append(format[++i] switch
                {
                    'n' => "\n",
                    't' => "\t",
                    var other => other.ToString(),
                });

                continue;
            }

            if (format[i] != '%' || i + 1 >= format.Length)
            {
                builder.Append(format[i]);
                continue;
            }

            var specifier = format[++i];

            // `%-x` and `%0x` suppress or force padding.
            var noPad = false;
            if (specifier is '-' or '_' or '0' && i + 1 < format.Length)
            {
                noPad = specifier == '-';
                specifier = format[++i];
            }

            var text = Expand(specifier, time, moment);
            builder.Append(noPad ? text.TrimStart('0', ' ') is { Length: > 0 } trimmed ? trimmed : "0" : text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The week number, counting from the first <paramref name="start"/> of the year.
    /// </summary>
    /// <remarks>
    /// Days before that first week day are week 0, which is what makes <c>%U</c> and
    /// <c>%W</c> differ from the ISO week number.
    /// </remarks>
    private static int WeekOfYear(DateTime time, DayOfWeek start)
    {
        var january1 = new DateTime(time.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var offset = ((int)january1.DayOfWeek - (int)start + 7) % 7;
        return (time.DayOfYear + offset - 1) / 7;
    }

    private static string Expand(char specifier, DateTime time, DateTimeOffset moment) => specifier switch
    {
        'Y' => time.Year.ToString("D4", CultureInfo.InvariantCulture),
        'y' => (time.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
        'C' => (time.Year / 100).ToString("D2", CultureInfo.InvariantCulture),
        'm' => time.Month.ToString("D2", CultureInfo.InvariantCulture),
        'd' => time.Day.ToString("D2", CultureInfo.InvariantCulture),
        'e' => time.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2),
        'H' => time.Hour.ToString("D2", CultureInfo.InvariantCulture),
        'I' => (time.Hour % 12 == 0 ? 12 : time.Hour % 12).ToString("D2", CultureInfo.InvariantCulture),
        'M' => time.Minute.ToString("D2", CultureInfo.InvariantCulture),
        'S' => time.Second.ToString("D2", CultureInfo.InvariantCulture),
        'N' => (time.Ticks % TimeSpan.TicksPerSecond * 100).ToString("D9", CultureInfo.InvariantCulture),
        'j' => time.DayOfYear.ToString("D3", CultureInfo.InvariantCulture),
        's' => moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        'p' => time.Hour < 12 ? "AM" : "PM",
        'P' => time.Hour < 12 ? "am" : "pm",
        'a' => time.DayOfWeek.ToString()[..3],
        'A' => time.DayOfWeek.ToString(),
        'b' or 'h' => MonthName(time.Month)[..3],
        'B' => MonthName(time.Month),
        'u' => ((int)time.DayOfWeek == 0 ? 7 : (int)time.DayOfWeek).ToString(CultureInfo.InvariantCulture),
        'w' => ((int)time.DayOfWeek).ToString(CultureInfo.InvariantCulture),
        'U' => WeekOfYear(time, DayOfWeek.Sunday).ToString("D2", CultureInfo.InvariantCulture),
        'W' => WeekOfYear(time, DayOfWeek.Monday).ToString("D2", CultureInfo.InvariantCulture),
        'Z' => "UTC",
        'z' => "+0000",
        'F' => time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        'T' => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        'R' => time.ToString("HH:mm", CultureInfo.InvariantCulture),
        'D' => time.ToString("MM/dd/yy", CultureInfo.InvariantCulture),
        'c' => time.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture),
        'x' => time.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        'X' => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        'V' => ISOWeek.GetWeekOfYear(time).ToString("D2", CultureInfo.InvariantCulture),
        'G' => ISOWeek.GetYear(time).ToString("D4", CultureInfo.InvariantCulture),
        'n' => "\n",
        't' => "\t",
        '%' => "%",
        _ => "%" + specifier,
    };

    private static string MonthName(int month) => month switch
    {
        1 => "January",
        2 => "February",
        3 => "March",
        4 => "April",
        5 => "May",
        6 => "June",
        7 => "July",
        8 => "August",
        9 => "September",
        10 => "October",
        11 => "November",
        _ => "December",
    };
}

/// <summary><c>timeout</c> — runs a command with a time limit.</summary>
/// <remarks>
/// The timed command is a builtin running in-process, so there is nothing to kill: the
/// limit is applied by racing the command against a delay and reporting 124 if the delay
/// wins. The command's own work still ends when the execution budget does.
/// </remarks>
public sealed class TimeoutBuiltin : IBuiltin
{
    /// <summary>The status <c>timeout</c> reports when the limit was reached.</summary>
    public const int TimedOut = 124;

    /// <summary>The status coreutils uses for a <c>timeout</c> that could not run at all.</summary>
    private const int TimeoutFailure = 125;

    /// <inheritdoc />
    public string Name => "timeout";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var index = 0;
        var preserveStatus = false;

        while (index < context.Arguments.Count && context.Arguments[index].StartsWith('-'))
        {
            switch (context.Arguments[index])
            {
                case "-s" or "--signal" or "-k" or "--kill-after":
                    index += 2;
                    continue;

                case "--preserve-status": preserveStatus = true; break;
                case "--foreground" or "-v" or "--verbose": break;
                case "--": index++; goto parsed;
                default: goto parsed;
            }

            index++;
        }

    parsed:
        if (index >= context.Arguments.Count)
        {
            return ExecResult.Usage("timeout", "missing duration operand", TimeoutFailure);
        }

        if (!SleepBuiltin.TryParseDuration(context.Arguments[index++], out var limit))
        {
            return ExecResult.Usage("timeout", $"invalid time interval '{context.Arguments[index - 1]}'", TimeoutFailure);
        }

        var words = context.Arguments.Skip(index).ToList();

        if (words.Count == 0)
        {
            return ExecResult.Usage("timeout", "missing command operand", TimeoutFailure);
        }

        // A zero limit has already elapsed, so the command never starts.
        if (limit <= TimeSpan.Zero)
        {
            return ExecResult.FromExitCode(TimedOut);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var command = context.Hooks.RunCommand(words, context.Stdin, linked.Token).AsTask();
        var expiry = Task.Delay(limit, linked.Token);

        var winner = await Task.WhenAny(command, expiry);

        if (winner == command)
        {
            await linked.CancelAsync();
            return await command;
        }

        await linked.CancelAsync();

        try
        {
            await command;
        }
        catch (OperationCanceledException)
        {
            // Expected: the command was abandoned when the limit expired.
        }

        return ExecResult.FromExitCode(preserveStatus ? ExitCodes.Terminated : TimedOut);
    }
}
