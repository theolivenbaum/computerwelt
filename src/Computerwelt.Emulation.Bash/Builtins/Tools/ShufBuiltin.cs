using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>shuf</c> — write a random permutation of its input.
/// </summary>
/// <remarks>
/// One deliberate departure from GNU: <c>-r</c> without <c>-n</c> is rejected. GNU's
/// <c>shuf -r</c> streams forever, which in an embedded shell means a command that never
/// returns and an output buffer that grows until a limit kills the whole script. Requiring
/// a count turns that into an error the caller can see.
/// </remarks>
public sealed class ShufBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "shuf";

    /// <inheritdoc />
    public string? LlmHint =>
        "shuf: Randomly permute input lines. Supports -e (arguments as input), -i LO-HI, "
        + "-n COUNT, -r (with replacement, requires -n), -z.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: shuf [OPTION]... [FILE]
        Write a random permutation of the input lines.

          -e         treat each argument as an input line
          -i LO-HI   use the range LO..HI as input
          -n COUNT   output at most COUNT lines
          -r         sample with replacement; requires -n
          -z         separate output with NUL rather than newline
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var echo = false;
        var repeat = false;
        var zero = false;
        int? count = null;
        string? range = null;
        var operands = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            switch (argument)
            {
                case "--help":
                    return ExecResult.Ok(Help + "\n");

                case "--echo":
                    echo = true;
                    continue;

                case "--repeat":
                    repeat = true;
                    continue;

                case "--zero-terminated":
                    zero = true;
                    continue;
            }

            if (argument.StartsWith("--input-range=", StringComparison.Ordinal))
            {
                range = argument["--input-range=".Length..];
                continue;
            }

            if (argument.StartsWith("--head-count=", StringComparison.Ordinal))
            {
                count = int.TryParse(argument["--head-count=".Length..], CultureInfo.InvariantCulture, out var n) ? n : null;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                return ExecResult.Usage(Name, $"unrecognized option '{argument}'");
            }

            if (argument.Length < 2 || argument[0] != '-')
            {
                operands.Add(argument);
                continue;
            }

            for (var c = 1; c < argument.Length; c++)
            {
                switch (argument[c])
                {
                    case 'e': echo = true; break;
                    case 'r': repeat = true; break;
                    case 'z': zero = true; break;

                    case 'i':
                        range = c + 1 < argument.Length ? argument[(c + 1)..]
                            : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;
                        c = argument.Length;
                        break;

                    case 'n':
                    {
                        var text = c + 1 < argument.Length ? argument[(c + 1)..]
                            : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;

                        count = int.TryParse(text, CultureInfo.InvariantCulture, out var n) ? n : null;
                        c = argument.Length;
                        break;
                    }

                    default:
                        return ExecResult.Usage(Name, $"invalid option -- '{argument[c]}'");
                }
            }
        }

        if (repeat && count is null)
        {
            return ExecResult.Error(
                $"{Name}: -r requires -n in this shell, because an endless stream cannot be bounded\n",
                ExitCodes.Failure);
        }

        List<string> lines;

        if (range is not null)
        {
            var bounds = range.Split('-');

            if (bounds.Length != 2
                || !long.TryParse(bounds[0], CultureInfo.InvariantCulture, out var low)
                || !long.TryParse(bounds[1], CultureInfo.InvariantCulture, out var high))
            {
                return ExecResult.Usage(Name, $"invalid input range '{range}'");
            }

            lines = [];

            for (var value = low; value <= high; value++)
            {
                context.Budget.ChargeWork(1);
                lines.Add(value.ToString(CultureInfo.InvariantCulture));
            }
        }
        else if (echo)
        {
            lines = operands;
        }
        else
        {
            var content = string.Concat((await context.ReadOperandsAsync(operands, cancellationToken))
                .Select(static file => file.Content));

            lines = content.Length == 0
                ? []
                : [.. (content.EndsWith('\n') ? content[..^1] : content).Split('\n')];
        }

        var random = Random.Shared;
        var output = new StringBuilder();
        var separator = zero ? '\0' : '\n';

        if (repeat)
        {
            if (lines.Count == 0)
            {
                return ExecResult.Success;
            }

            for (var i = 0; i < count; i++)
            {
                context.Budget.ChargeWork(1);
                output.Append(lines[random.Next(lines.Count)]).Append(separator);
            }

            return ExecResult.Ok(output.ToString());
        }

        // Fisher-Yates over a copy: the input list may be the caller's own operands.
        var shuffled = lines.ToList();

        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        foreach (var line in shuffled.Take(count ?? shuffled.Count))
        {
            output.Append(line).Append(separator);
        }

        return ExecResult.Ok(output.ToString());
    }
}
