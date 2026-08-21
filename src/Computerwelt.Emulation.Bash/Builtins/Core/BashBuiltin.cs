using System.Text;
using Computerwelt.Emulation.Bash.Interpreter;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>bash</c> and <c>sh</c> — run a script in a child shell.
/// </summary>
/// <remarks>
/// <para>
/// No process is created. The script runs in-process against a fresh
/// <see cref="ShellState"/> seeded from the exported environment, which reproduces what
/// scripts actually rely on: <c>bash -c 'FOO=bar'</c> does not leak <c>FOO</c>, a child
/// cannot see the parent's functions or non-exported variables, and <c>bash -e</c> does not
/// turn on <c>errexit</c> in the caller.
/// </para>
/// <para>
/// The option set is bash's own, including <c>-c</c>, the <c>set</c> flags, <c>-o</c> by
/// name, and <c>-n</c> for a syntax check. Unknown options fail with status 2 as bash does,
/// rather than being ignored.
/// </para>
/// </remarks>
public sealed class BashBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under <paramref name="name"/>, either <c>bash</c> or <c>sh</c>.</summary>
    public BashBuiltin(string name = "bash") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint =>
        $"{Name}: Run a script in an isolated child shell. Supports -c, -e, -u, -x, -f, -n, "
        + "-o OPTION, script files and piped input. No host process is created.";

    /// <inheritdoc />
    public string? Help =>
        $"""
        Usage: {Name} [option] ... [file [argument] ...]
        Usage: {Name} [option] ... -c command [name [argument] ...]

          -c COMMAND    run COMMAND, with the remaining arguments as $0, $1, ...
          -e            exit on an unhandled non-zero status
          -u            treat unset variables as an error
          -x            trace commands before running them
          -f            disable pathname expansion
          -n            read commands but do not run them
          -o OPTION     set OPTION by name
          +o OPTION     unset OPTION by name
          --version     output version information and exit
          --help        display this help and exit
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var options = ParseArguments(context.Arguments);

        if (options.Result is { } early)
        {
            return early;
        }

        if (context.Hooks.RunIsolated is not { } run)
        {
            return ExecResult.Error($"{Name}: cannot start a child shell here\n", ExitCodes.Failure);
        }

        // `-c COMMAND [name [args]]`: the first operand after the command is $0.
        if (options.Command is { } command)
        {
            return await run(
                new ChildShell(command)
                {
                    ScriptName = options.Operands.Count > 0 ? options.Operands[0] : Name,
                    Positional = options.Operands.Count > 1 ? options.Operands[1..] : [],
                    Stdin = context.Stdin,
                    Configure = options.Configure,
                },
                cancellationToken);
        }

        if (options.Operands.Count > 0)
        {
            return await RunFileAsync(context, run, options, cancellationToken);
        }

        // With no script and no piped input there is nothing to do; bash would start an
        // interactive shell, which this sandbox has no notion of.
        if (context.Stdin is not { } piped)
        {
            return ExecResult.Success;
        }

        return await run(
            new ChildShell(piped.ToString()) { ScriptName = Name, Configure = options.Configure },
            cancellationToken);
    }

    private async ValueTask<ExecResult> RunFileAsync(
        BuiltinContext context,
        Func<ChildShell, CancellationToken, ValueTask<ExecResult>> run,
        BashOptions options,
        CancellationToken cancellationToken)
    {
        var file = options.Operands[0];
        byte[] bytes;

        try
        {
            bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(file), cancellationToken);
        }
        catch (ShellException)
        {
            return ExecResult.Error($"{Name}: {file}: No such file or directory\n", ExitCodes.NotFound);
        }

        return await run(
            new ChildShell(Encoding.UTF8.GetString(bytes))
            {
                ScriptName = file,
                Positional = options.Operands[1..],
                Stdin = context.Stdin,
                Configure = options.Configure,
            },
            cancellationToken);
    }

    private sealed class BashOptions
    {
        public string? Command { get; set; }

        public List<string> Operands { get; } = [];

        public List<Action<ShellOptions>> Flags { get; } = [];

        public ExecResult? Result { get; set; }

        /// <summary>True when <c>-c</c> was given with nothing after it.</summary>
        public bool PendingCommand { get; set; }

        public Action<ShellOptions> Configure => shell =>
        {
            foreach (var flag in Flags)
            {
                flag(shell);
            }
        };
    }

    /// <summary>Long options real bash accepts and this shell has nothing to do about.</summary>
    private static readonly HashSet<string> AcceptedLongOptions = new(StringComparer.Ordinal)
    {
        "--norc", "--noprofile", "--noediting", "--posix", "--login", "--restricted",
        "--rcfile", "--init-file", "--dump-strings", "--dump-po-strings", "--debugger",
    };

    private BashOptions ParseArguments(IReadOnlyList<string> arguments)
    {
        var options = new BashOptions();
        var i = 0;

        for (; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            if (argument == "--")
            {
                i++;
                break;
            }

            switch (argument)
            {
                case "--version":
                    options.Result = ExecResult.Ok(Version());
                    return options;

                case "--help":
                    options.Result = ExecResult.Ok(Help + "\n");
                    return options;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (AcceptedLongOptions.Contains(argument))
                {
                    continue;
                }

                options.Result = ExecResult.Error(
                    $"{Name}: {argument}: invalid option\n{Help}\n",
                    ExitCodes.Usage);

                return options;
            }

            if (argument.Length > 1 && argument[0] is '-' or '+')
            {
                if (!ApplyFlags(options, argument, arguments, ref i))
                {
                    return options;
                }

                continue;
            }

            break;
        }

        for (; i < arguments.Count; i++)
        {
            options.Operands.Add(arguments[i]);
        }

        if (options.Command is not null || !options.PendingCommand)
        {
            return options;
        }

        options.Result = ExecResult.Error($"{Name}: -c: option requires an argument\n", ExitCodes.Usage);
        return options;
    }

    /// <summary>Applies one bundled flag group, such as <c>-eu</c> or <c>+o pipefail</c>.</summary>
    private bool ApplyFlags(BashOptions options, string argument, IReadOnlyList<string> arguments, ref int index)
    {
        var enable = argument[0] == '-';

        for (var c = 1; c < argument.Length; c++)
        {
            switch (argument[c])
            {
                case 'c':
                {
                    // `-c` takes the rest of the bundle's argument, or the next one.
                    if (c + 1 < argument.Length)
                    {
                        options.Command = argument[(c + 1)..];
                        return true;
                    }

                    if (index + 1 >= arguments.Count)
                    {
                        options.PendingCommand = true;
                        return true;
                    }

                    options.Command = arguments[++index];
                    return true;
                }

                case 'e': options.Flags.Add(shell => shell.ErrExit = enable); break;
                case 'u': options.Flags.Add(shell => shell.NoUnset = enable); break;
                case 'x': options.Flags.Add(shell => shell.XTrace = enable); break;
                case 'v': options.Flags.Add(shell => shell.Verbose = enable); break;
                case 'f': options.Flags.Add(shell => shell.NoGlob = enable); break;
                case 'n': options.Flags.Add(shell => shell.NoExec = enable); break;
                case 'a': options.Flags.Add(shell => shell.AllExport = enable); break;
                case 'C': options.Flags.Add(shell => shell.NoClobber = enable); break;

                // Flags that only matter to a real process or an interactive shell.
                case 'i' or 'l' or 's' or 'm' or 'B' or 'H' or 'p' or 'h' or 'b' or 't':
                    break;

                case 'o':
                {
                    var name = c + 1 < argument.Length
                        ? argument[(c + 1)..]
                        : index + 1 < arguments.Count ? arguments[++index] : null;

                    if (name is null)
                    {
                        // A bare `-o` lists the options, which nothing here needs; treat it
                        // as a no-op rather than an error, as bash does for `set -o`.
                        return true;
                    }

                    if (!ApplyNamedOption(options, name, enable))
                    {
                        options.Result = ExecResult.Error(
                            $"{Name}: {name}: invalid option name\n",
                            ExitCodes.Usage);

                        return false;
                    }

                    return true;
                }

                default:
                    options.Result = ExecResult.Error(
                        $"{Name}: -{argument[c]}: invalid option\n{Help}\n",
                        ExitCodes.Usage);

                    return false;
            }
        }

        return true;
    }

    private static bool ApplyNamedOption(BashOptions options, string name, bool enable)
    {
        switch (name)
        {
            case "errexit": options.Flags.Add(shell => shell.ErrExit = enable); return true;
            case "nounset": options.Flags.Add(shell => shell.NoUnset = enable); return true;
            case "xtrace": options.Flags.Add(shell => shell.XTrace = enable); return true;
            case "verbose": options.Flags.Add(shell => shell.Verbose = enable); return true;
            case "noglob": options.Flags.Add(shell => shell.NoGlob = enable); return true;
            case "noexec": options.Flags.Add(shell => shell.NoExec = enable); return true;
            case "pipefail": options.Flags.Add(shell => shell.PipeFail = enable); return true;
            case "noclobber": options.Flags.Add(shell => shell.NoClobber = enable); return true;
            case "allexport": options.Flags.Add(shell => shell.AllExport = enable); return true;

            // Accepted and inert: nothing here has job control, history or a line editor.
            case "posix" or "monitor" or "history" or "emacs" or "vi" or "ignoreeof"
                or "notify" or "hashall" or "keyword" or "onecmd" or "physical" or "privileged":
                return true;

            default:
                return false;
        }
    }

    private string Version() =>
        Name == "sh"
            // The corpus pins this banner — `bash --version` is greppable behaviour, not
            // branding — so it keeps naming the upstream the port is compatible with.
            ? "Bashkit virtual sh, version 5.2.0(1)-release\n"
            : "GNU bash, version 5.2.0(1)-release (Bashkit virtual shell)\n";
}
