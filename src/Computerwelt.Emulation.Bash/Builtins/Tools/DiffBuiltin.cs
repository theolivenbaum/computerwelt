using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>diff</c> — compare two files line by line.
/// </summary>
/// <remarks>
/// <para>
/// The edit script comes from a longest-common-subsequence diff, so the output is minimal
/// rather than a line-by-line comparison that reports everything after an inserted line as
/// changed.
/// </para>
/// <para>
/// The default output is the normal (ed-style) format, as POSIX specifies, with <c>-u</c>
/// for unified and <c>-q</c> for a one-line verdict. The exit status is the part scripts
/// depend on: 0 when the files match, 1 when they differ, 2 on an error.
/// </para>
/// </remarks>
public sealed class DiffBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "diff";

    /// <inheritdoc />
    public string? LlmHint =>
        "diff: Compare two files. Default output is normal format; -u for unified, "
        + "-q for brief, -i to ignore case, -w to ignore whitespace. Exit 1 when they differ.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: diff [OPTION]... FILE1 FILE2
        Compare FILE1 and FILE2 line by line.

          -u, -U N    unified format, with N lines of context (default 3)
          -q          report only whether the files differ
          -i          ignore case
          -w          ignore all whitespace
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var unified = false;
        var brief = false;
        var ignoreCase = false;
        var ignoreSpace = false;
        var contextLines = 3;
        var operands = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            switch (argument)
            {
                case "--help":
                    return ExecResult.Ok(Help + "\n");

                case "--unified":
                    unified = true;
                    continue;

                case "--brief":
                    brief = true;
                    continue;
            }

            if (argument.StartsWith("-U", StringComparison.Ordinal))
            {
                unified = true;
                var text = argument.Length > 2 ? argument[2..]
                    : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;

                contextLines = int.TryParse(text, CultureInfo.InvariantCulture, out var n) ? n : 3;
                continue;
            }

            if (argument.Length > 1 && argument[0] == '-' && argument != "-")
            {
                foreach (var flag in argument[1..])
                {
                    switch (flag)
                    {
                        case 'u': unified = true; break;
                        case 'q': brief = true; break;
                        case 'i': ignoreCase = true; break;
                        case 'w' or 'b': ignoreSpace = true; break;
                        default: break;
                    }
                }

                continue;
            }

            operands.Add(argument);
        }

        if (operands.Count != 2)
        {
            return ExecResult.Usage(Name, "missing operand");
        }

        List<(string Name, string Content)> files;

        try
        {
            files = await context.ReadOperandsAsync(operands, cancellationToken);
        }
        catch (BashkitException exception)
        {
            return ExecResult.Error($"diff: {exception.Message}\n", ExitCodes.Usage);
        }

        var left = Lines(files[0].Content);
        var right = Lines(files[1].Content);
        context.Budget.ChargeWork(left.Count + right.Count);

        string Key(string line)
        {
            var value = ignoreSpace ? new string(line.Where(static c => !char.IsWhiteSpace(c)).ToArray()) : line;
            return ignoreCase ? value.ToUpperInvariant() : value;
        }

        var edits = Edits(left, right, Key);

        if (edits.Count == 0)
        {
            return ExecResult.Success;
        }

        if (brief)
        {
            return ExecResult.Ok($"Files {operands[0]} and {operands[1]} differ\n") with { ExitCode = ExitCodes.Failure };
        }

        var output = unified
            ? Unified(operands[0], operands[1], left, right, edits, contextLines)
            : Normal(left, right, edits);

        return ExecResult.Ok(output) with { ExitCode = ExitCodes.Failure };
    }

    private static List<string> Lines(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var trimmed = content.EndsWith('\n') ? content[..^1] : content;
        return [.. trimmed.Split('\n')];
    }

    /// <summary>One contiguous change: lines removed from the left, lines added from the right.</summary>
    /// <param name="LeftStart">The first left-hand line index affected.</param>
    /// <param name="LeftCount">How many left-hand lines were removed.</param>
    /// <param name="RightStart">The first right-hand line index affected.</param>
    /// <param name="RightCount">How many right-hand lines were added.</param>
    private readonly record struct Edit(int LeftStart, int LeftCount, int RightStart, int RightCount);

    /// <summary>
    /// Computes the changes between two files by way of their longest common subsequence.
    /// </summary>
    private static List<Edit> Edits(List<string> left, List<string> right, Func<string, string> key)
    {
        var table = new int[left.Count + 1, right.Count + 1];

        for (var i = left.Count - 1; i >= 0; i--)
        {
            for (var j = right.Count - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(key(left[i]), key(right[j]), StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var edits = new List<Edit>();
        int x = 0, y = 0;

        while (x < left.Count || y < right.Count)
        {
            if (x < left.Count && y < right.Count
                && string.Equals(key(left[x]), key(right[y]), StringComparison.Ordinal))
            {
                x++;
                y++;
                continue;
            }

            var startX = x;
            var startY = y;

            // Collect the whole run of non-matching lines into one hunk.
            while (x < left.Count || y < right.Count)
            {
                if (x < left.Count && y < right.Count
                    && string.Equals(key(left[x]), key(right[y]), StringComparison.Ordinal))
                {
                    break;
                }

                if (y < right.Count && (x >= left.Count || table[x, y + 1] >= table[x + 1, y]))
                {
                    y++;
                    continue;
                }

                x++;
            }

            edits.Add(new Edit(startX, x - startX, startY, y - startY));
        }

        return edits;
    }

    private static string Normal(List<string> left, List<string> right, List<Edit> edits)
    {
        var output = new StringBuilder();

        foreach (var edit in edits)
        {
            var command = edit.LeftCount == 0 ? 'a' : edit.RightCount == 0 ? 'd' : 'c';

            // An append reports the left-hand line it follows, not the one it replaces.
            output.Append(Range(edit.LeftStart, edit.LeftCount, command == 'a'))
                .Append(command)
                .Append(Range(edit.RightStart, edit.RightCount, command == 'd'))
                .Append('\n');

            for (var i = 0; i < edit.LeftCount; i++)
            {
                output.Append("< ").Append(left[edit.LeftStart + i]).Append('\n');
            }

            if (command == 'c')
            {
                output.Append("---\n");
            }

            for (var i = 0; i < edit.RightCount; i++)
            {
                output.Append("> ").Append(right[edit.RightStart + i]).Append('\n');
            }
        }

        return output.ToString();
    }

    private static string Range(int start, int count, bool positionOnly)
    {
        if (positionOnly)
        {
            return start.ToString(CultureInfo.InvariantCulture);
        }

        return count <= 1
            ? (start + 1).ToString(CultureInfo.InvariantCulture)
            : $"{start + 1},{start + count}";
    }

    private static string Unified(
        string leftName,
        string rightName,
        List<string> left,
        List<string> right,
        List<Edit> edits,
        int contextLines)
    {
        var output = new StringBuilder()
            .Append("--- ").Append(leftName).Append('\n')
            .Append("+++ ").Append(rightName).Append('\n');

        var index = 0;

        while (index < edits.Count)
        {
            // Merge changes that are close enough for their context windows to touch.
            var last = index;

            while (last + 1 < edits.Count
                   && edits[last + 1].LeftStart - (edits[last].LeftStart + edits[last].LeftCount) <= contextLines * 2)
            {
                last++;
            }

            var first = edits[index];
            var final = edits[last];
            var leftFrom = Math.Max(first.LeftStart - contextLines, 0);
            var rightFrom = Math.Max(first.RightStart - contextLines, 0);
            var leftTo = Math.Min(final.LeftStart + final.LeftCount + contextLines, left.Count);
            var rightTo = Math.Min(final.RightStart + final.RightCount + contextLines, right.Count);

            output.Append("@@ -").Append(Hunk(leftFrom, leftTo - leftFrom))
                .Append(" +").Append(Hunk(rightFrom, rightTo - rightFrom))
                .Append(" @@\n");

            var x = leftFrom;
            var y = rightFrom;

            for (var e = index; e <= last; e++)
            {
                var edit = edits[e];

                while (x < edit.LeftStart)
                {
                    output.Append(' ').Append(left[x++]).Append('\n');
                    y++;
                }

                for (var i = 0; i < edit.LeftCount; i++)
                {
                    output.Append('-').Append(left[x++]).Append('\n');
                }

                for (var i = 0; i < edit.RightCount; i++)
                {
                    output.Append('+').Append(right[y++]).Append('\n');
                }
            }

            while (x < leftTo)
            {
                output.Append(' ').Append(left[x++]).Append('\n');
            }

            index = last + 1;
        }

        return output.ToString();
    }

    private static string Hunk(int start, int count) =>
        count == 1
            ? (start + 1).ToString(CultureInfo.InvariantCulture)
            : $"{(count == 0 ? start : start + 1)},{count}";
}
