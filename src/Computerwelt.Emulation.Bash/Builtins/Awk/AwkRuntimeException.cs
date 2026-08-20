namespace Computerwelt.Emulation.Bash.Builtins.Awk;

/// <summary>
/// A fatal error raised by a running AWK program — a division by zero, a bad lvalue, a call
/// to a function that does not exist.
/// </summary>
/// <remarks>
/// Distinct from <see cref="BashkitException"/> because it is not a shell failure: it stops
/// the <c>awk</c> command with a diagnostic and a non-zero status, exactly as the real awk
/// does, while the surrounding script carries on.
/// </remarks>
internal sealed class AwkRuntimeException : Exception
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public AwkRuntimeException(string message)
        : base(message)
    {
    }
}
