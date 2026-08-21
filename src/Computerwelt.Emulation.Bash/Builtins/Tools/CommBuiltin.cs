using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>comm</c> — compare two sorted files line by line.
/// </summary>
/// <remarks>
/// The output is three tab-separated columns: lines only in the first file, lines only in
/// the second, and lines in both. Suppressing a column with <c>-1</c>, <c>-2</c> or
/// <c>-3</c> also removes its indentation from the columns that remain, which is what makes
/// <c>comm -12</c> a usable "lines in both" filter.
/// </remarks>
public sealed class CommBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "comm";

    /// <inheritdoc />
    public string? LlmHint => "comm: Compare two sorted files line by line. Supports -1, -2, -3.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: comm [OPTION]... FILE1 FILE2
        Compare sorted files FILE1 and FILE2 line by line.

          -1    suppress lines unique to FILE1
          -2    suppress lines unique to FILE2
          -3    suppress lines that appear in both files
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var suppressed = new bool[3];
        var operands = new List<string>();

        foreach (var argument in context.Arguments)
        {
            if (argument == "--help")
            {
                return ExecResult.Ok(Help + "\n");
            }

            if (argument.Length > 1 && argument[0] == '-' && argument != "-")
            {
                foreach (var flag in argument[1..])
                {
                    if (flag is < '1' or > '3')
                    {
                        return ExecResult.Usage(Name, $"invalid option -- '{flag}'");
                    }

                    suppressed[flag - '1'] = true;
                }

                continue;
            }

            operands.Add(argument);
        }

        if (operands.Count != 2)
        {
            return ExecResult.Usage(Name, "missing operand");
        }

        var files = await context.ReadOperandsAsync(operands, cancellationToken);
        var left = Lines(files[0].Content);
        var right = Lines(files[1].Content);

        // A column's indentation is the number of un-suppressed columns before it.
        var indents = new string[3];

        for (var column = 0; column < 3; column++)
        {
            indents[column] = new string('\t', Enumerable.Range(0, column).Count(c => !suppressed[c]));
        }

        var output = new StringBuilder();
        int i = 0, j = 0;

        while (i < left.Count || j < right.Count)
        {
            context.Budget.ChargeWork(1);

            var order = i >= left.Count ? 1
                : j >= right.Count ? -1
                : string.CompareOrdinal(left[i], right[j]);

            var (column, text) = order switch
            {
                < 0 => (0, left[i++]),
                > 0 => (1, right[j++]),
                _ => (2, Advance(left, right, ref i, ref j)),
            };

            if (!suppressed[column])
            {
                output.Append(indents[column]).Append(text).Append('\n');
            }
        }

        return ExecResult.Ok(output.ToString());
    }

    private static string Advance(List<string> left, List<string> right, ref int i, ref int j)
    {
        var text = left[i];
        i++;
        j++;
        return text;
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
}
