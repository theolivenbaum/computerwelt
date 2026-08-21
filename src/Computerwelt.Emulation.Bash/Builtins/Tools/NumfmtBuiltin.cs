using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>numfmt</c> — convert numbers to and from human-readable suffixes.
/// </summary>
/// <remarks>
/// The two suffix families are genuinely different and confusing them is the usual bug:
/// <c>si</c> steps by 1000 and <c>iec</c> by 1024, so a megabyte is 1000000 in one and
/// 1048576 in the other. Both are supported, along with <c>iec-i</c>, which spells its
/// units <c>Ki</c>, <c>Mi</c>, <c>Gi</c>.
/// </remarks>
public sealed class NumfmtBuiltin : IBuiltin
{
    private const string Units = "KMGTPEZY";

    /// <inheritdoc />
    public string Name => "numfmt";

    /// <inheritdoc />
    public string? LlmHint =>
        "numfmt: Convert numbers to/from human-readable form. --to=si|iec|iec-i, "
        + "--from=si|iec, --suffix, --padding.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: numfmt [OPTION]... [NUMBER]...
        Reformat NUMBER(s) from and to human-readable strings.

          --from=UNIT     parse the input with UNIT suffixes (si, iec, auto)
          --to=UNIT       print with UNIT suffixes (si, iec, iec-i)
          --suffix=SUF    append SUF to the result
          --padding=N     pad to N characters, right-aligned when N is positive
        """;

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        string? to = null;
        string? from = null;
        var suffix = string.Empty;
        var padding = 0;
        var operands = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            switch (argument)
            {
                case "--help":
                    return ValueTask.FromResult(ExecResult.Ok(Help + "\n"));

                case "--version":
                    return ValueTask.FromResult(ExecResult.Ok("numfmt (bashkit) 0.1\n"));
            }

            if (Value(argument, "--to") is { } toValue)
            {
                to = toValue;
                continue;
            }

            if (Value(argument, "--from") is { } fromValue)
            {
                from = fromValue;
                continue;
            }

            if (Value(argument, "--suffix") is { } suffixValue)
            {
                suffix = suffixValue;
                continue;
            }

            if (Value(argument, "--padding") is { } paddingValue)
            {
                padding = int.TryParse(paddingValue, CultureInfo.InvariantCulture, out var width) ? width : 0;
                continue;
            }

            if (argument is "--to" or "--from" or "--suffix" or "--padding" && i + 1 < context.Arguments.Count)
            {
                var value = context.Arguments[++i];

                switch (argument)
                {
                    case "--to": to = value; break;
                    case "--from": from = value; break;
                    case "--suffix": suffix = value; break;
                    default:
                        padding = int.TryParse(value, CultureInfo.InvariantCulture, out var width) ? width : 0;
                        break;
                }

                continue;
            }

            if (argument.StartsWith('-') && argument.Length > 1)
            {
                return ValueTask.FromResult(ExecResult.Usage(Name, $"invalid option -- '{argument}'"));
            }

            operands.Add(argument);
        }

        var inputs = operands.Count > 0
            ? operands
            : [.. context.StdinText.Split('\n', StringSplitOptions.RemoveEmptyEntries)];

        var output = new StringBuilder();

        foreach (var input in inputs)
        {
            context.Budget.ChargeWork(1);

            if (Parse(input.Trim(), from) is not { } number)
            {
                return ValueTask.FromResult(new ExecResult
                {
                    Stdout = StreamData.FromText(output.ToString()),
                    Stderr = StreamData.FromText($"numfmt: invalid number: '{input.Trim()}'\n"),
                    ExitCode = ExitCodes.Usage,
                });
            }

            var text = Render(number, to) + suffix;
            output.Append(padding > 0 ? text.PadLeft(padding) : padding < 0 ? text.PadRight(-padding) : text)
                .Append('\n');
        }

        return ValueTask.FromResult(ExecResult.Ok(output.ToString()));
    }

    private static string? Value(string argument, string name) =>
        argument.StartsWith(name + "=", StringComparison.Ordinal) ? argument[(name.Length + 1)..] : null;

    /// <summary>Reads a number, honouring the suffix family <paramref name="from"/> names.</summary>
    private static double? Parse(string text, string? from)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var suffixIndex = text.Length;

        while (suffixIndex > 0 && !char.IsAsciiDigit(text[suffixIndex - 1]) && text[suffixIndex - 1] != '.')
        {
            suffixIndex--;
        }

        var digits = text[..suffixIndex];
        var suffix = text[suffixIndex..].TrimEnd('i', 'B');

        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        if (suffix.Length == 0)
        {
            return value;
        }

        // A suffix on the input is only meaningful once `--from` says which family it is in.
        var power = Units.IndexOf(char.ToUpperInvariant(suffix[0]), StringComparison.Ordinal);

        if (power < 0 || from is null)
        {
            return from is null ? null : value;
        }

        var factor = from.StartsWith("iec", StringComparison.Ordinal) ? 1024d : 1000d;
        return value * Math.Pow(factor, power + 1);
    }

    private static string Render(double value, string? to)
    {
        if (to is null)
        {
            return value.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        var factor = to.StartsWith("iec", StringComparison.Ordinal) ? 1024d : 1000d;
        var tail = to == "iec-i" ? "i" : string.Empty;
        var magnitude = Math.Abs(value);
        var power = 0;

        while (magnitude >= factor && power < Units.Length)
        {
            magnitude /= factor;
            value /= factor;
            power++;
        }

        if (power == 0)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        // GNU rounds away from zero to one decimal, so 1.04 MiB still prints as 1.1M.
        var scaled = Math.Ceiling(Math.Abs(value) * 10) / 10 * Math.Sign(value);
        return scaled.ToString("0.0", CultureInfo.InvariantCulture) + Units[power - 1] + tail;
    }
}
