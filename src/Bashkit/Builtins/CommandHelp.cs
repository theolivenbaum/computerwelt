namespace Bashkit.Builtins;

/// <summary>
/// The <c>--help</c> and <c>--version</c> handling shared by the coreutils-style builtins.
/// </summary>
/// <remarks>
/// Handled per command rather than centrally in the dispatcher, because <c>--help</c> is
/// not universally an option: <c>echo --help</c> prints the word, and <c>grep --help</c>
/// after a pattern is a search term. Only a command that has decided <c>--help</c> is an
/// option for it calls this.
/// </remarks>
internal static class CommandHelp
{
    /// <summary>
    /// Returns the response to <c>--help</c> or <c>--version</c>, or null when neither was
    /// asked for.
    /// </summary>
    /// <param name="arguments">The command's arguments.</param>
    /// <param name="usage">The full help text, whose first line is the usage summary.</param>
    /// <param name="version">The version line, without its newline.</param>
    public static ExecResult? Handle(IReadOnlyList<string> arguments, string usage, string version)
    {
        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case "--help":
                    return ExecResult.Ok(usage.EndsWith('\n') ? usage : usage + "\n");

                case "--version":
                    return ExecResult.Ok(version + "\n");

                // Everything after `--` is an operand, help text included.
                case "--":
                    return null;
            }
        }

        return null;
    }
}
