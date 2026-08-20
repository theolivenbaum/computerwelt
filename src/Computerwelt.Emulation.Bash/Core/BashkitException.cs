namespace Computerwelt.Emulation.Bash;

/// <summary>
/// A fatal condition that aborts the whole execution.
/// </summary>
/// <remarks>
/// Ordinary command failures are <b>not</b> exceptions — they are non-zero
/// <see cref="ExecResult.ExitCode"/> values, because a script is expected to keep running
/// after them. An exception means the sandbox itself refused to continue: a limit was
/// exhausted, the script could not be parsed, or a capability was denied.
/// </remarks>
public class BashkitException : Exception
{
    /// <summary>Creates an exception of the given <paramref name="kind"/>.</summary>
    public BashkitException(BashkitErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException) => Kind = kind;

    /// <summary>What class of failure occurred.</summary>
    public BashkitErrorKind Kind { get; }

    /// <summary>The exit status a host should report for this failure.</summary>
    public virtual int ExitCode => Kind switch
    {
        BashkitErrorKind.Parse => ExitCodes.Usage,
        BashkitErrorKind.Timeout => ExitCodes.Terminated,
        BashkitErrorKind.Cancelled => ExitCodes.Interrupted,
        _ => ExitCodes.Failure,
    };
}

/// <summary>A script could not be parsed.</summary>
public sealed class ParseException : BashkitException
{
    /// <summary>Creates a parse error at <paramref name="position"/>.</summary>
    public ParseException(string message, int position = -1)
        : base(BashkitErrorKind.Parse, message) => Position = position;

    /// <summary>Byte offset into the script where parsing failed, or -1 when unknown.</summary>
    public int Position { get; }
}

/// <summary>A resource limit was exhausted.</summary>
public sealed class LimitExceededException : BashkitException
{
    /// <summary>Creates a limit error naming the limit and its configured value.</summary>
    public LimitExceededException(string limitName, long limit)
        : base(BashkitErrorKind.LimitExceeded, $"resource limit exceeded: {limitName} (limit: {limit})")
    {
        LimitName = limitName;
        Limit = limit;
    }

    /// <summary>The limit that was hit.</summary>
    public string LimitName { get; }

    /// <summary>The configured value of that limit.</summary>
    public long Limit { get; }
}

/// <summary>A filesystem operation failed. Carries the message a shell would print.</summary>
public sealed class FileSystemException : BashkitException
{
    /// <summary>Creates a filesystem error.</summary>
    public FileSystemException(FileSystemErrorKind kind, string message)
        : base(BashkitErrorKind.FileSystem, message) => FsKind = kind;

    /// <summary>The specific filesystem failure.</summary>
    public FileSystemErrorKind FsKind { get; }
}

/// <summary>Broad classes of fatal failure.</summary>
public enum BashkitErrorKind
{
    /// <summary>The script is syntactically invalid.</summary>
    Parse,

    /// <summary>A filesystem operation failed.</summary>
    FileSystem,

    /// <summary>A resource limit was exhausted.</summary>
    LimitExceeded,

    /// <summary>The wall-clock timeout elapsed.</summary>
    Timeout,

    /// <summary>The host cancelled the execution.</summary>
    Cancelled,

    /// <summary>A capability (network, host mount, credential) was denied.</summary>
    PermissionDenied,

    /// <summary>An internal invariant was violated.</summary>
    Internal,
}

/// <summary>Filesystem failures that map onto <c>errno</c> values scripts can observe.</summary>
public enum FileSystemErrorKind
{
    /// <summary>ENOENT.</summary>
    NotFound,

    /// <summary>EEXIST.</summary>
    AlreadyExists,

    /// <summary>EISDIR.</summary>
    IsADirectory,

    /// <summary>ENOTDIR.</summary>
    NotADirectory,

    /// <summary>ENOTEMPTY.</summary>
    DirectoryNotEmpty,

    /// <summary>EACCES.</summary>
    PermissionDenied,

    /// <summary>ELOOP.</summary>
    TooManySymlinks,

    /// <summary>EINVAL.</summary>
    InvalidArgument,

    /// <summary>ENOSPC / EDQUOT — the virtual filesystem quota is full.</summary>
    QuotaExceeded,

    /// <summary>EROFS.</summary>
    ReadOnly,
}
