namespace Bashkit;

/// <summary>
/// The only route by which sandboxed code touches storage.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the interpreter or in a builtin may call <see cref="System.IO.File"/> or
/// <see cref="System.IO.Directory"/>. All I/O flows through an implementation of this
/// interface, which is what lets a host hand a script an in-memory tree, an overlay, or a
/// tightly jailed slice of the real disk without the script being able to tell or escape.
/// </para>
/// <para>Implementations must be safe for concurrent use: one instance is shared across a
/// whole execution, and pipelines evaluate stages concurrently.</para>
/// </remarks>
public interface IFileSystem
{
    /// <summary>Reads a file's full contents.</summary>
    ValueTask<byte[]> ReadFileAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Writes a file, creating or truncating it.</summary>
    ValueTask WriteFileAsync(VPath path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Appends to a file, creating it when absent.</summary>
    ValueTask AppendFileAsync(VPath path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Creates a directory; <paramref name="recursive"/> creates missing parents.</summary>
    ValueTask CreateDirectoryAsync(VPath path, bool recursive, CancellationToken cancellationToken = default);

    /// <summary>Removes a file or directory; <paramref name="recursive"/> removes a non-empty directory.</summary>
    ValueTask RemoveAsync(VPath path, bool recursive, CancellationToken cancellationToken = default);

    /// <summary>Stats a path, following symlinks.</summary>
    ValueTask<FileMetadata> StatAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Stats a path without following a final symlink.</summary>
    ValueTask<FileMetadata> StatLinkAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Lists a directory's immediate children.</summary>
    ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Tests whether a path exists.</summary>
    ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Renames or moves a path.</summary>
    ValueTask RenameAsync(VPath from, VPath to, CancellationToken cancellationToken = default);

    /// <summary>Copies a file.</summary>
    ValueTask CopyAsync(VPath from, VPath to, CancellationToken cancellationToken = default);

    /// <summary>Creates a symbolic link at <paramref name="link"/> pointing at <paramref name="target"/>.</summary>
    ValueTask CreateSymlinkAsync(string target, VPath link, CancellationToken cancellationToken = default);

    /// <summary>Reads a symbolic link's target.</summary>
    ValueTask<string> ReadSymlinkAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Changes a path's permission bits.</summary>
    ValueTask ChangeModeAsync(VPath path, int mode, CancellationToken cancellationToken = default);

    /// <summary>Sets a path's modification time.</summary>
    ValueTask SetModifiedTimeAsync(VPath path, DateTimeOffset time, CancellationToken cancellationToken = default);

    /// <summary>Current space and inode usage.</summary>
    FsUsage GetUsage();

    /// <summary>The size and count caps this filesystem enforces.</summary>
    FsLimits Limits { get; }

    /// <summary>A short identifier for the backend, reported by <c>df</c> and diagnostics.</summary>
    string BackendKind { get; }
}
