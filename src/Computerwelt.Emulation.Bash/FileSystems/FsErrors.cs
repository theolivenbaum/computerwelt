namespace Computerwelt.Emulation.Bash;

/// <summary>
/// Factories for filesystem errors whose messages match what a POSIX shell prints, since
/// scripts routinely grep stderr.
/// </summary>
public static class FsErrors
{
    /// <summary>ENOENT.</summary>
    public static FileSystemException NotFound(VPath path) =>
        new(FileSystemErrorKind.NotFound, $"{path}: No such file or directory");

    /// <summary>EEXIST.</summary>
    public static FileSystemException AlreadyExists(VPath path) =>
        new(FileSystemErrorKind.AlreadyExists, $"{path}: File exists");

    /// <summary>EISDIR.</summary>
    public static FileSystemException IsADirectory(VPath path) =>
        new(FileSystemErrorKind.IsADirectory, $"{path}: Is a directory");

    /// <summary>ENOTDIR.</summary>
    public static FileSystemException NotADirectory(VPath path) =>
        new(FileSystemErrorKind.NotADirectory, $"{path}: Not a directory");

    /// <summary>ENOTEMPTY.</summary>
    public static FileSystemException DirectoryNotEmpty(VPath path) =>
        new(FileSystemErrorKind.DirectoryNotEmpty, $"{path}: Directory not empty");

    /// <summary>EACCES.</summary>
    public static FileSystemException PermissionDenied(VPath path) =>
        new(FileSystemErrorKind.PermissionDenied, $"{path}: Permission denied");

    /// <summary>ELOOP.</summary>
    public static FileSystemException TooManySymlinks(VPath path) =>
        new(FileSystemErrorKind.TooManySymlinks, $"{path}: Too many levels of symbolic links");

    /// <summary>EINVAL.</summary>
    public static FileSystemException InvalidArgument(VPath path, string detail) =>
        new(FileSystemErrorKind.InvalidArgument, $"{path}: {detail}");

    /// <summary>ENOSPC — the virtual quota, not the host disk.</summary>
    public static FileSystemException QuotaExceeded(string detail) =>
        new(FileSystemErrorKind.QuotaExceeded, $"No space left on device: {detail}");

    /// <summary>EROFS.</summary>
    public static FileSystemException ReadOnly(VPath path) =>
        new(FileSystemErrorKind.ReadOnly, $"{path}: Read-only file system");

    /// <summary>ENAMETOOLONG.</summary>
    public static FileSystemException NameTooLong(string name) =>
        new(FileSystemErrorKind.InvalidArgument, $"{name}: File name too long");
}
