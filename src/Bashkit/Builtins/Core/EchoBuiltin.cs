using System.Text;

namespace Bashkit.Builtins;

/// <summary>
/// <c>echo</c> — writes its arguments separated by spaces.
/// </summary>
/// <remarks>
/// <c>echo</c> deliberately does not use <see cref="ArgCursor"/>: option parsing must stop
/// at the first argument that is not exactly <c>-n</c>, <c>-e</c> or <c>-E</c>, so
/// <c>echo -n -x</c> prints <c>-x</c> rather than erroring.
/// </remarks>
public sealed class EchoBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "echo";

    /// <inheritdoc />
    public string? LlmHint => "echo: Prints arguments. Supports -n (no newline) and -e (interpret backslash escapes).";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var trailingNewline = true;
        var interpretEscapes = false;
        var index = 0;

        while (index < context.Arguments.Count)
        {
            var argument = context.Arguments[index];
            if (argument.Length < 2 || argument[0] != '-' || !argument[1..].All(static c => c is 'n' or 'e' or 'E'))
            {
                break;
            }

            foreach (var flag in argument[1..])
            {
                switch (flag)
                {
                    case 'n': trailingNewline = false; break;
                    case 'e': interpretEscapes = true; break;
                    case 'E': interpretEscapes = false; break;
                }
            }

            index++;
        }

        var text = string.Join(' ', context.Arguments.Skip(index));

        if (interpretEscapes)
        {
            text = ExpandEscapes(text, out var suppressNewline);
            if (suppressNewline)
            {
                trailingNewline = false;
            }
        }

        if (trailingNewline)
        {
            text += "\n";
        }

        return ValueTask.FromResult(ExecResult.Ok(text));
    }

    /// <summary>Expands the escapes <c>echo -e</c> understands. <c>\c</c> stops output entirely.</summary>
    private static string ExpandEscapes(string text, out bool suppressNewline)
    {
        suppressNewline = false;
        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                builder.Append(text[i]);
                continue;
            }

            var c = text[++i];
            switch (c)
            {
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 'e': builder.Append('\x1b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'v': builder.Append('\v'); break;
                case '\\': builder.Append('\\'); break;

                case 'c':
                    suppressNewline = true;
                    return builder.ToString();

                case '0':
                {
                    var digits = 0;
                    var value = 0;
                    while (digits < 3 && i + 1 < text.Length && text[i + 1] is >= '0' and <= '7')
                    {
                        value = (value * 8) + (text[++i] - '0');
                        digits++;
                    }

                    builder.Append((char)value);
                    break;
                }

                case 'x':
                {
                    var digits = 0;
                    var value = 0;
                    while (digits < 2 && i + 1 < text.Length && Uri.IsHexDigit(text[i + 1]))
                    {
                        value = (value * 16) + Convert.ToInt32(text[++i].ToString(), 16);
                        digits++;
                    }

                    if (digits == 0)
                    {
                        builder.Append("\\x");
                        break;
                    }

                    builder.Append((char)value);
                    break;
                }

                default:
                    builder.Append('\\').Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
