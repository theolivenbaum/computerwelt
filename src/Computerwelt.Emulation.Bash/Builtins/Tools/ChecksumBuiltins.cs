using System.Security.Cryptography;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>md5sum</c>, <c>sha1sum</c>, <c>sha256sum</c> and <c>sha512sum</c>.
/// </summary>
/// <remarks>
/// Verification mode (<c>-c</c>) is deliberately rejected rather than ignored. An
/// unrecognised option that silently falls through to hashing standard input would turn
/// <c>sha256sum -c manifest</c> — a check that is supposed to fail loudly — into a command
/// that succeeds while checking nothing.
/// </remarks>
public sealed class ChecksumBuiltin : IBuiltin
{
    private readonly Func<HashAlgorithm> _create;

    /// <summary>Creates the builtin for one digest.</summary>
    /// <param name="name">The command name, such as <c>sha256sum</c>.</param>
    /// <param name="create">Constructs the hash algorithm.</param>
    private ChecksumBuiltin(string name, Func<HashAlgorithm> create)
    {
        Name = name;
        _create = create;
    }

    /// <summary>Every digest command this class provides.</summary>
    public static IEnumerable<ChecksumBuiltin> All() =>
    [
        new("md5sum", MD5.Create),
        new("sha1sum", SHA1.Create),
        new("sha256sum", SHA256.Create),
        new("sha512sum", SHA512.Create),
    ];

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint => $"{Name}: Print the {Name[..^3].ToUpperInvariant()} digest of files or standard input.";

    /// <inheritdoc />
    public string? Help => $"Usage: {Name} [OPTION]... [FILE]...\nPrint the checksum of each FILE.\n";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var operands = new List<string>();

        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case "--help":
                    return ExecResult.Ok(Help!);

                case "--version":
                    return ExecResult.Ok($"{Name} (bashkit) 0.1\n");

                // `-b`, `-t` and `--tag` only choose how the output is laid out, and the
                // default layout is the one scripts parse.
                case "-b" or "--binary" or "-t" or "--text":
                    continue;

                case "-" :
                    operands.Add(argument);
                    continue;
            }

            if (argument.StartsWith('-') && argument.Length > 1)
            {
                return ExecResult.Error($"{Name}: unsupported option '{argument}'\n", ExitCodes.Failure);
            }

            operands.Add(argument);
        }

        var output = new StringBuilder();
        var failed = false;

        if (operands.Count == 0)
        {
            output.Append(Digest(context.Stdin?.ToArray() ?? [])).Append("  -\n");
            return ExecResult.Ok(output.ToString());
        }

        var errors = new StringBuilder();

        foreach (var operand in operands)
        {
            if (operand == "-")
            {
                output.Append(Digest(context.Stdin?.ToArray() ?? [])).Append("  -\n");
                continue;
            }

            byte[] bytes;

            try
            {
                bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(operand), cancellationToken);
            }
            catch (ShellException)
            {
                errors.Append($"{Name}: {operand}: No such file or directory\n");
                failed = true;
                continue;
            }

            context.Budget.ChargeWork(bytes.Length);
            output.Append(Digest(bytes)).Append("  ").Append(operand).Append('\n');
        }

        return new ExecResult
        {
            Stdout = StreamData.FromText(output.ToString()),
            Stderr = StreamData.FromText(errors.ToString()),
            ExitCode = failed ? ExitCodes.Failure : ExitCodes.Success,
        };
    }

    private string Digest(byte[] bytes)
    {
        using var algorithm = _create();
        return Convert.ToHexStringLower(algorithm.ComputeHash(bytes));
    }
}
