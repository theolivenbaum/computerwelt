using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>compgen</c> — list the completions bash would offer.
/// </summary>
/// <remarks>
/// Useful in a non-interactive shell despite the name: <c>compgen -c</c> is how a script
/// asks "what commands exist here", which in this shell means the registered builtins plus
/// the executable scripts on <c>PATH</c>.
/// </remarks>
public sealed class CompgenBuiltin : IBuiltin
{
    private static readonly string[] Keywords =
    [
        "if", "then", "else", "elif", "fi", "case", "esac", "for", "select", "while",
        "until", "do", "done", "in", "function", "time", "coproc", "{", "}", "!", "[[", "]]",
    ];

    /// <inheritdoc />
    public string Name => "compgen";

    /// <inheritdoc />
    public string? LlmHint =>
        "compgen: List completions. -c commands, -b builtins, -k keywords, -f files, "
        + "-d directories, -v variables, -A function, -W wordlist.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: compgen [-abcdfkv] [-A ACTION] [-W WORDLIST] [WORD]
        Display the possible completions for WORD.
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var actions = new List<string>();
        string? wordlist = null;
        string? prefix = null;

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            if (argument == "--help")
            {
                return ExecResult.Ok(Help + "\n");
            }

            if (argument == "-A" && i + 1 < context.Arguments.Count)
            {
                actions.Add(context.Arguments[++i]);
                continue;
            }

            if (argument == "-W" && i + 1 < context.Arguments.Count)
            {
                wordlist = context.Arguments[++i];
                actions.Add("wordlist");
                continue;
            }

            if (argument.Length > 1 && argument[0] == '-')
            {
                foreach (var flag in argument[1..])
                {
                    actions.Add(flag switch
                    {
                        'a' => "alias",
                        'b' => "builtin",
                        'c' => "command",
                        'd' => "directory",
                        'e' => "export",
                        'f' => "file",
                        'k' => "keyword",
                        'v' => "variable",
                        _ => "unknown",
                    });
                }

                continue;
            }

            prefix = argument;
        }

        var results = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var action in actions)
        {
            switch (action)
            {
                case "builtin":
                    results.UnionWith(context.Hooks.BuiltinNames());
                    break;

                case "command":
                    results.UnionWith(context.Hooks.BuiltinNames());
                    results.UnionWith(context.State.Functions.Keys);
                    results.UnionWith(await PathCommandsAsync(context, cancellationToken));
                    break;

                case "keyword":
                    results.UnionWith(Keywords);
                    break;

                case "alias":
                    results.UnionWith(context.State.Aliases.Keys);
                    break;

                case "function":
                    results.UnionWith(context.State.Functions.Keys);
                    break;

                case "variable":
                    results.UnionWith(context.State.AllVariables().Select(static v => v.Key));
                    break;

                case "export":
                    results.UnionWith(context.State.ExportedEnvironment().Keys);
                    break;

                case "file" or "directory":
                    results.UnionWith(await EntriesAsync(context, action == "directory", cancellationToken));
                    break;

                case "wordlist":
                    results.UnionWith((wordlist ?? string.Empty)
                        .Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries));

                    break;
            }
        }

        var matches = prefix is null
            ? results
            : new SortedSet<string>(results.Where(r => r.StartsWith(prefix, StringComparison.Ordinal)), StringComparer.Ordinal);

        var output = new StringBuilder();

        foreach (var match in matches)
        {
            output.Append(match).Append('\n');
        }

        return matches.Count == 0
            ? ExecResult.FromExitCode(ExitCodes.Failure)
            : ExecResult.Ok(output.ToString());
    }

    private static async ValueTask<List<string>> PathCommandsAsync(
        BuiltinContext context,
        CancellationToken cancellationToken)
    {
        var commands = new List<string>();

        foreach (var directory in (context.State.Get("PATH") ?? string.Empty)
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = VPath.Parse(directory);

            if (!await context.FileSystem.ExistsAsync(path, cancellationToken))
            {
                continue;
            }

            foreach (var entry in await context.FileSystem.ReadDirectoryAsync(path, cancellationToken))
            {
                var metadata = await context.FileSystem.StatAsync(path.Join(entry.Name), cancellationToken);

                // Only an executable file is a command, which is what makes a data file
                // dropped into a PATH directory invisible here.
                if (!metadata.IsDirectory && (metadata.Mode & 0b001_001_001) != 0)
                {
                    commands.Add(entry.Name);
                }
            }
        }

        return commands;
    }

    private static async ValueTask<List<string>> EntriesAsync(
        BuiltinContext context,
        bool directoriesOnly,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();

        foreach (var entry in await context.FileSystem.ReadDirectoryAsync(context.WorkingDirectory, cancellationToken))
        {
            var metadata = await context.FileSystem.StatAsync(
                context.WorkingDirectory.Join(entry.Name),
                cancellationToken);

            if (!directoriesOnly || metadata.IsDirectory)
            {
                names.Add(entry.Name);
            }
        }

        return names;
    }
}
