namespace Computerwelt.Emulation.Bash;

/// <summary>What a path is.</summary>
public enum FileType
{
    /// <summary>A regular file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link.</summary>
    Symlink,

    /// <summary>A named pipe.</summary>
    Fifo,
}

/// <summary>Metadata for one path, as <c>stat</c> and <c>ls -l</c> report it.</summary>
public sealed record FileMetadata
{
    /// <summary>What the path is.</summary>
    public FileType Type { get; init; } = FileType.File;

    /// <summary>Size in bytes. For a directory this is a nominal block size.</summary>
    public long Size { get; init; }

    /// <summary>POSIX permission bits, e.g. 0o644.</summary>
    public int Mode { get; init; } = 0b110_100_100; // 0644

    /// <summary>Last modification time.</summary>
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last access time.</summary>
    public DateTimeOffset AccessedAt { get; init; }

    /// <summary>Owning user id.</summary>
    public int Uid { get; init; }

    /// <summary>Owning group id.</summary>
    public int Gid { get; init; }

    /// <summary>Hard link count.</summary>
    public int LinkCount { get; init; } = 1;

    /// <summary>True for a regular file.</summary>
    public bool IsFile => Type == FileType.File;

    /// <summary>True for a directory.</summary>
    public bool IsDirectory => Type == FileType.Directory;

    /// <summary>True for a symbolic link.</summary>
    public bool IsSymlink => Type == FileType.Symlink;

    /// <summary>True for a named pipe.</summary>
    public bool IsFifo => Type == FileType.Fifo;
}

/// <summary>One entry returned by a directory listing.</summary>
/// <param name="Name">The entry's name, with no path components.</param>
/// <param name="Type">What the entry is.</param>
public readonly record struct DirectoryEntry(string Name, FileType Type)
{
    /// <summary>True for a directory entry.</summary>
    public bool IsDirectory => Type == FileType.Directory;
}

/// <summary>Space and inode consumption of a filesystem.</summary>
/// <param name="TotalBytes">Bytes currently stored.</param>
/// <param name="FileCount">Number of files.</param>
/// <param name="DirectoryCount">Number of directories.</param>
public readonly record struct FsUsage(long TotalBytes, int FileCount, int DirectoryCount);

/// <summary>Caps a filesystem enforces on the data a script may create.</summary>
public sealed record FsLimits
{
    /// <summary>The defaults.</summary>
    public static FsLimits Default { get; } = new();

    /// <summary>Maximum total bytes across all files. Default 100 MB.</summary>
    public long MaxTotalBytes { get; init; } = 100_000_000;

    /// <summary>Maximum size of any one file. Default 50 MB.</summary>
    public long MaxFileBytes { get; init; } = 50_000_000;

    /// <summary>Maximum number of files. Default 10,000.</summary>
    public int MaxFiles { get; init; } = 10_000;

    /// <summary>Maximum directory nesting depth. Default 64.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Maximum symlinks followed while resolving one path. Default 40, as Linux uses.</summary>
    public int MaxSymlinkHops { get; init; } = 40;

    /// <summary>Maximum length of a single path component. Default 255.</summary>
    public int MaxNameLength { get; init; } = 255;
}
