using System.Globalization;

namespace Bashkit.Builtins;

/// <summary>
/// <c>mapfile</c> and <c>readarray</c> — read lines into an array.
/// </summary>
/// <remarks>
/// The idiom this replaces is <c>while read</c>, and the difference that matters is
/// <c>-t</c>: without it every element keeps its trailing newline, which is almost never
/// what the caller wants and is a common source of comparisons that silently fail.
/// </remarks>
public sealed class MapfileBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under <paramref name="name"/>, either name.</summary>
    public MapfileBuiltin(string name = "mapfile") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint => $"{Name}: Read lines of input into an array. Supports -t, -n, -s, -O, -d.";

    /// <inheritdoc />
    public string? Help =>
        $"""
        Usage: {Name} [-d DELIM] [-n COUNT] [-O ORIGIN] [-s SKIP] [-t] [ARRAY]
        Read lines from standard input into ARRAY, or MAPFILE by default.

          -d DELIM   use DELIM instead of newline
          -n COUNT   read at most COUNT lines
          -O ORIGIN  start assigning at index ORIGIN
          -s SKIP    discard the first SKIP lines
          -t         strip the delimiter from each line
        """;

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var strip = false;
        var delimiter = '\n';
        var count = 0;
        var skip = 0;
        var origin = 0;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "--help":
                    return ValueTask.FromResult(ExecResult.Ok(Help + "\n"));

                case "-t": strip = true; break;

                case "-d":
                    delimiter = cursor.TakeValue() is { Length: > 0 } value ? value[0] : '\n';
                    break;

                case "-n": count = Number(cursor.TakeValue()); break;
                case "-s": skip = Number(cursor.TakeValue()); break;
                case "-O": origin = Number(cursor.TakeValue()); break;

                // The callback options need a real descriptor to interleave against.
                case "-u" or "-C" or "-c": cursor.TakeValue(); break;

                default:
                    return ValueTask.FromResult(ExecResult.Usage(Name, $"invalid option -- '{option.TrimStart('-')}'"));
            }
        }

        var name = cursor.Operands.Count > 0 ? cursor.Operands[0] : "MAPFILE";
        var text = context.Input?.ReadToEnd() ?? context.StdinText;
        var lines = new List<string>();

        for (var start = 0; start < text.Length;)
        {
            context.Budget.ChargeWork(1);
            var end = text.IndexOf(delimiter, start);

            if (end < 0)
            {
                lines.Add(text[start..]);
                break;
            }

            lines.Add(strip ? text[start..end] : text[start..(end + 1)]);
            start = end + 1;
        }

        var selected = lines.Skip(skip);

        if (count > 0)
        {
            selected = selected.Take(count);
        }

        var variable = context.State.GetOrCreate(name);

        if (origin == 0)
        {
            variable.SetArray(selected);
            return ValueTask.FromResult(ExecResult.Success);
        }

        // A non-zero origin keeps whatever is already in the array before it.
        var index = (long)origin;

        foreach (var line in selected)
        {
            variable.SetIndexed(index++, line);
        }

        return ValueTask.FromResult(ExecResult.Success);
    }

    private static int Number(string? text) =>
        int.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
