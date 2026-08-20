namespace Computerwelt.Emulation.Bash.Interpreter;

/// <summary>
/// A pattern that matched nothing while <c>failglob</c> was set.
/// </summary>
/// <remarks>
/// This is a failure of the command, not of the shell: unlike an unset variable under
/// <c>set -u</c>, a <c>failglob</c> miss leaves the shell running and only the command that
/// used the pattern reports an error.
/// </remarks>
public sealed class GlobFailureException(string message)
    : BashkitException(BashkitErrorKind.Internal, message);
