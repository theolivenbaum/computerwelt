using System.Globalization;
using System.Text;

namespace Bashkit.Builtins;

/// <summary>
/// <c>od</c> — dump bytes in octal, hex, decimal or as characters.
/// </summary>
/// <remarks>
/// An unknown option is a usage error rather than a silent no-op: <c>od</c> is reached for
/// when something is already wrong with a file, and a flag that was quietly ignored would
/// produce a dump in the wrong format that looks perfectly plausible.
/// </remarks>
public sealed class OdBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "od";

    /// <inheritdoc />
    public string? LlmHint =>
        "od: Dump bytes. Supports -A (address format), -t (output type), -j (skip), "
        + "-N (count), -w (bytes per line), -c, -x.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: od [OPTION]... [FILE]...
        Write an unambiguous representation of FILE to standard output.

          -A RADIX   address radix: o, x, d or n for none
          -t TYPE    output type: x1, o1, d1, u1, c
          -j BYTES   skip BYTES of input first
          -N BYTES   dump at most BYTES
          -w BYTES   bytes per output line (default 16)
          -c         same as -t c
          -x         same as -t x2
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var address = 'o';
        var type = "o2";
        long skip = 0;
        long? limit = null;
        var width = 16;
        var operands = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            if (argument == "--help")
            {
                return ExecResult.Ok(Help + "\n");
            }

            if (argument.Length < 2 || argument[0] != '-' || argument == "-")
            {
                operands.Add(argument);
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                return ExecResult.Usage(Name, $"unrecognized option '{argument}'");
            }

            // Short options take their value attached or as the next argument.
            var flag = argument[1];
            var attached = argument[2..];

            string? Take() => attached.Length > 0 ? attached
                : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;

            switch (flag)
            {
                case 'A': address = Take() is { Length: > 0 } radix ? radix[0] : 'o'; break;
                case 't': type = Take() ?? "o2"; break;
                case 'j': skip = ParseSize(Take()); break;
                case 'N': limit = ParseSize(Take()); break;
                case 'w': width = (int)Math.Max(ParseSize(Take()), 1); break;
                case 'c': type = "c"; break;
                case 'x': type = "x2"; break;
                case 'b': type = "o1"; break;
                case 'd': type = "u2"; break;
                case 'v': break;

                default:
                    return ExecResult.Usage(Name, $"invalid option -- '{flag}'");
            }
        }

        var bytes = new List<byte>();

        foreach (var (_, content) in await context.ReadOperandsAsync(operands, cancellationToken))
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(content));
        }

        var data = bytes.Skip((int)skip).Take((int)(limit ?? int.MaxValue)).ToArray();
        context.Budget.ChargeWork(data.Length);

        return ExecResult.Ok(Dump(data, address, type, width, skip));
    }

    private static long ParseSize(string? text)
    {
        if (text is null or "")
        {
            return 0;
        }

        var multiplier = text[^1] switch
        {
            'b' => 512L,
            'k' or 'K' => 1024L,
            'm' or 'M' => 1024L * 1024,
            _ => 1L,
        };

        var digits = multiplier == 1 ? text : text[..^1];

        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToInt64(digits[2..], 16) * multiplier;
        }

        return long.TryParse(digits, CultureInfo.InvariantCulture, out var value) ? value * multiplier : 0;
    }

    private static string Dump(byte[] data, char address, string type, int width, long start)
    {
        var output = new StringBuilder();
        var size = type == "c" ? 1 : type.Length > 1 && char.IsAsciiDigit(type[1]) ? type[1] - '0' : 2;

        for (var offset = 0; offset < data.Length; offset += width)
        {
            AppendAddress(output, address, start + offset);
            var count = Math.Min(width, data.Length - offset);

            for (var i = 0; i < count; i += size)
            {
                output.Append(' ').Append(Render(data, offset + i, Math.Min(size, count - i), type));
            }

            output.Append('\n');
        }

        // od closes with the address one past the last byte, which is how a reader knows
        // the length without counting.
        if (address != 'n')
        {
            AppendAddress(output, address, start + data.Length);
            output.Append('\n');
        }

        return output.ToString();
    }

    private static void AppendAddress(StringBuilder output, char address, long offset)
    {
        switch (address)
        {
            case 'n':
                return;

            case 'x':
                output.Append(offset.ToString("x7", CultureInfo.InvariantCulture));
                return;

            case 'd':
                output.Append(offset.ToString("D7", CultureInfo.InvariantCulture));
                return;

            default:
                output.Append(Convert.ToString(offset, 8).PadLeft(7, '0'));
                return;
        }
    }

    private static string Render(byte[] data, int offset, int size, string type)
    {
        if (type == "c")
        {
            return Character(data[offset]).PadLeft(3);
        }

        // Multi-byte units are little-endian, as they are on every platform od runs on.
        long value = 0;

        for (var i = size - 1; i >= 0; i--)
        {
            value = (value << 8) | data[offset + i];
        }

        var digits = size * 2;

        return type[0] switch
        {
            'x' => value.ToString("x" + digits, CultureInfo.InvariantCulture),
            'd' or 'u' => value.ToString(CultureInfo.InvariantCulture).PadLeft(size * 3),
            _ => Convert.ToString(value, 8).PadLeft(size * 3, '0'),
        };
    }

    private static string Character(byte value) => value switch
    {
        0 => "\\0",
        (byte)'\n' => "\\n",
        (byte)'\t' => "\\t",
        (byte)'\r' => "\\r",
        (byte)'\a' => "\\a",
        (byte)'\b' => "\\b",
        (byte)'\f' => "\\f",
        (byte)'\v' => "\\v",
        >= 0x20 and < 0x7f => ((char)value).ToString(),
        _ => Convert.ToString(value, 8).PadLeft(3, '0'),
    };
}

/// <summary>
/// <c>xxd</c> — a hex dump in the layout <c>xxd</c> uses.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="OdBuiltin"/> because the two disagree about everything
/// that matters: xxd groups bytes in pairs, prints an ASCII gutter, and its <c>-p</c> mode
/// emits a bare hex stream that <c>xxd -r -p</c> can read straight back.
/// </remarks>
public sealed class XxdBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "xxd";

    /// <inheritdoc />
    public string? LlmHint => "xxd: Hex dump. Supports -p (plain), -r (reverse), -l (length), -c (columns).";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: xxd [OPTION]... [FILE]
        Make a hex dump.

          -p         output a plain hex stream
          -r         reverse: turn a hex dump back into binary
          -l LEN     dump at most LEN bytes
          -c COLS    bytes per line (default 16)
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var plain = false;
        var reverse = false;
        var columns = 16;
        int? length = null;
        var operands = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            if (argument == "--help")
            {
                return ExecResult.Ok(Help + "\n");
            }

            if (argument.Length < 2 || argument[0] != '-')
            {
                operands.Add(argument);
                continue;
            }

            foreach (var flag in argument[1..])
            {
                switch (flag)
                {
                    case 'p': plain = true; break;
                    case 'r': reverse = true; break;

                    case 'l' when i + 1 < context.Arguments.Count:
                        length = int.TryParse(context.Arguments[++i], CultureInfo.InvariantCulture, out var l) ? l : null;
                        break;

                    case 'c' when i + 1 < context.Arguments.Count:
                        columns = int.TryParse(context.Arguments[++i], CultureInfo.InvariantCulture, out var c) ? c : 16;
                        break;

                    default:
                        return ExecResult.Usage(Name, $"invalid option -- '{flag}'");
                }
            }
        }

        var text = string.Concat((await context.ReadOperandsAsync(operands, cancellationToken))
            .Select(static file => file.Content));

        if (reverse)
        {
            var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
            return ExecResult.Ok(Encoding.UTF8.GetString(Convert.FromHexString(hex[..(hex.Length / 2 * 2)])));
        }

        var data = Encoding.UTF8.GetBytes(text);

        if (length is { } limit)
        {
            data = data.Take(limit).ToArray();
        }

        context.Budget.ChargeWork(data.Length);
        return ExecResult.Ok(plain ? Plain(data, columns) : Grouped(data, columns));
    }

    private static string Plain(byte[] data, int columns)
    {
        var output = new StringBuilder();
        var perLine = columns == 16 ? 30 : columns;

        for (var offset = 0; offset < data.Length; offset += perLine)
        {
            output.Append(Convert.ToHexStringLower(data, offset, Math.Min(perLine, data.Length - offset)))
                .Append('\n');
        }

        return output.ToString();
    }

    private static string Grouped(byte[] data, int columns)
    {
        var output = new StringBuilder();

        for (var offset = 0; offset < data.Length; offset += columns)
        {
            var count = Math.Min(columns, data.Length - offset);
            output.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append(": ");

            for (var i = 0; i < columns; i++)
            {
                output.Append(i < count ? Convert.ToHexStringLower(data, offset + i, 1) : "  ");

                if (i % 2 == 1)
                {
                    output.Append(' ');
                }
            }

            output.Append(' ');

            for (var i = 0; i < count; i++)
            {
                var b = data[offset + i];
                output.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
            }

            output.Append('\n');
        }

        return output.ToString();
    }
}
