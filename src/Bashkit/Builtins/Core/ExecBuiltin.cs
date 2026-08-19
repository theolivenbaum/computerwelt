namespace Bashkit.Builtins;

/// <summary>
/// <c>exec</c> — replace the shell with a command, or redirect the shell itself.
/// </summary>
/// <remarks>
/// <para>
/// With a command, the command runs and the shell exits with its status: nothing after the
/// <c>exec</c> is reached. There is no process to replace, but the observable effect —
/// "this is the last thing this shell does" — is exactly reproducible.
/// </para>
/// <para>
/// With no command, <c>exec</c> exists for its redirections: <c>exec &lt; file</c> makes
/// the file the shell's standard input for everything that follows, which is the idiom
/// scripts use to read from a file without a loop redirection.
/// </para>
/// </remarks>
public sealed class ExecBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "exec";

    /// <inheritdoc />
    public string? LlmHint =>
        "exec: Run a command as the shell's last act, or with no command apply its "
        + "redirections to the shell itself.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: exec [COMMAND [ARGUMENT]...]
        Replace the shell with COMMAND, or apply the redirections to the shell.
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var words = new List<string>();

        foreach (var argument in context.Arguments)
        {
            // `-c`, `-l` and `-a` shape the replacement process's environment, which there
            // is no process to shape.
            if (words.Count == 0 && argument is "-c" or "-l" or "--")
            {
                continue;
            }

            // `exec {fd}>file` asks bash to allocate a descriptor into a variable. The
            // redirection itself is already parsed; the `{fd}` word is not a command.
            if (words.Count == 0 && IsDescriptorName(argument))
            {
                continue;
            }

            words.Add(argument);
        }

        if (words.Count == 0)
        {
            // A bare `exec` keeps whatever its own redirections resolved to, so a
            // `exec < file` becomes the shell's input from here on.
            context.Hooks.SetStandardInput?.Invoke(context.Stdin);
            return ExecResult.Success;
        }

        var line = string.Join(' ', words.Select(Quote));
        var result = await context.Hooks.RunFragment(line, context.Stdin, cancellationToken);

        // Nothing after an `exec` runs, which is the whole point of it.
        return result with { ControlFlow = ControlFlow.Exit(result.ExitCode) };
    }

    /// <summary>True for the <c>{name}</c> spelling bash uses to allocate a descriptor.</summary>
    private static bool IsDescriptorName(string word) =>
        word.Length > 2
        && word[0] == '{'
        && word[^1] == '}'
        && (char.IsAsciiLetter(word[1]) || word[1] == '_')
        && word[1..^1].All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Quotes a word so re-parsing the line yields the same words back.</summary>
    private static string Quote(string word) =>
        "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
