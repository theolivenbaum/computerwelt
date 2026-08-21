namespace Computerwelt.Emulation.Bash;

/// <summary>
/// Exit statuses with meanings fixed by POSIX and bash. Scripts branch on these, so the
/// exact values are part of the observable contract.
/// </summary>
public static class ExitCodes
{
    /// <summary>Success.</summary>
    public const int Success = 0;

    /// <summary>Generic failure.</summary>
    public const int Failure = 1;

    /// <summary>Misuse of a builtin, or a parse error.</summary>
    public const int Usage = 2;

    /// <summary>The command was found but could not be executed.</summary>
    public const int NotExecutable = 126;

    /// <summary>The command could not be found.</summary>
    public const int NotFound = 127;

    /// <summary>Base for signal-terminated statuses; the status is <c>128 + signal</c>.</summary>
    public const int SignalBase = 128;

    /// <summary>Terminated by SIGINT (Ctrl-C).</summary>
    public const int Interrupted = SignalBase + 2;

    /// <summary>Terminated by SIGKILL.</summary>
    public const int Killed = SignalBase + 9;

    /// <summary>Terminated by SIGTERM, which is how the timeout limit reports.</summary>
    public const int Terminated = SignalBase + 15;
}
