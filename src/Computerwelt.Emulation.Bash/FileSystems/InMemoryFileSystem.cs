using System.Diagnostics.CodeAnalysis;

namespace Computerwelt.Emulation.Bash;

/// <summary>
/// The default backend: a complete POSIX-shaped tree held in memory.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here touches the host. That is the point — a fresh instance is a fresh, empty
/// world, which is what makes multi-tenant isolation trivially true and makes every test
/// hermetic.
/// </para>
/// <para>
/// The tree is a node graph rather than a flat path→bytes dictionary, because symlinks,
/// rename-a-directory and quota accounting all need real parent/child structure. A single
/// lock guards the whole tree: contention is irrelevant at sandbox scale, and coarse
/// locking removes a large class of ordering bugs.
/// </para>
/// </remarks>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Lock _gate = new();
    private readonly DirectoryNode _root;
    private readonly TimeProvider _time;
    private long _totalBytes;
    private int _fileCount;
    private int _directoryCount = 1;

    /// <summary>Creates an empty filesystem containing only <c>/</c>.</summary>
    public InMemoryFileSystem(FsLimits? limits = null, TimeProvider? timeProvider = null)
    {
        Limits = limits ?? FsLimits.Default;
        _time = timeProvider ?? TimeProvider.System;
        _root = new DirectoryNode(_time.GetUtcNow());

        // Every sandbox has somewhere to put a temporary file. A shell without one turns the
        // most ordinary line a script can contain — a redirect into /tmp — into "No such file
        // or directory", and nothing in the sandbox explains why. 1777 is what /tmp carries on
        // a real system: writable by anyone, with the sticky bit.
        CreateDirectoryCore(TempDirectory, recursive: true);
        lock (_gate)
        {
            ResolveExisting(TempDirectory).Mode = (0b111_111_111 | StickyBit) & 0xFFF;
        }
    }

    /// <summary>Where <c>TMPDIR</c> points, and the one directory a fresh filesystem has.</summary>
    public static readonly VPath TempDirectory = VPath.Parse("/tmp");

    /// <summary>The sticky bit, as `ls` reports it on /tmp.</summary>
    private const int StickyBit = 1 << 9;

    /// <inheritdoc />
    public FsLimits Limits { get; }

    /// <inheritdoc />
    public string BackendKind => "memory";

    /// <inheritdoc />
    public FsUsage GetUsage()
    {
        lock (_gate)
        {
            return new FsUsage(_totalBytes, _fileCount, _directoryCount);
        }
    }

    /// <summary>
    /// Seeds the tree from a path→content map, creating parents as needed. Convenience for
    /// tests and for hosts that want a starting world.
    /// </summary>
    public void Seed(IEnumerable<KeyValuePair<string, string>> files)
    {
        foreach (var (path, content) in files)
        {
            var vpath = VPath.Parse(path);
            if (vpath.Parent is { } parent)
            {
                CreateDirectoryCore(parent, recursive: true);
            }

            WriteFileCore(vpath, System.Text.Encoding.UTF8.GetBytes(content), append: false);
        }
    }

    /// <inheritdoc />
    public ValueTask<byte[]> ReadFileAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var node = ResolveExisting(path);
            return node switch
            {
                FileNode file => ValueTask.FromResult((byte[])file.Content.Clone()),
                DirectoryNode => throw FsErrors.IsADirectory(path),
                _ => throw FsErrors.InvalidArgument(path, "Invalid argument"),
            };
        }
    }

    /// <inheritdoc />
    public ValueTask WriteFileAsync(VPath path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteFileCore(path, content.ToArray(), append: false);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AppendFileAsync(VPath path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteFileCore(path, content.ToArray(), append: true);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask CreateDirectoryAsync(VPath path, bool recursive, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateDirectoryCore(path, recursive);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(VPath path, bool recursive, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (path.IsRoot)
            {
                throw FsErrors.PermissionDenied(path);
            }

            var (parent, name) = ResolveParent(path);
            if (!parent.Children.TryGetValue(name, out var node))
            {
                throw FsErrors.NotFound(path);
            }

            if (node is DirectoryNode dir && dir.Children.Count > 0 && !recursive)
            {
                throw FsErrors.DirectoryNotEmpty(path);
            }

            ReleaseAccounting(node);
            parent.Children.Remove(name);
            parent.Touch(_time.GetUtcNow());
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<FileMetadata> StatAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(ResolveExisting(path).ToMetadata());
        }
    }

    /// <inheritdoc />
    public ValueTask<FileMetadata> StatLinkAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (path.IsRoot)
            {
                return ValueTask.FromResult(_root.ToMetadata());
            }

            var (parent, name) = ResolveParent(path);
            if (!parent.Children.TryGetValue(name, out var node))
            {
                throw FsErrors.NotFound(path);
            }

            return ValueTask.FromResult(node.ToMetadata());
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (ResolveExisting(path) is not DirectoryNode dir)
            {
                throw FsErrors.NotADirectory(path);
            }

            var entries = new List<DirectoryEntry>(dir.Children.Count);
            foreach (var (name, node) in dir.Children)
            {
                entries.Add(new DirectoryEntry(name, node.Type));
            }

            entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>(entries);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(TryResolve(path, followFinalLink: true, out _));
        }
    }

    /// <inheritdoc />
    public ValueTask RenameAsync(VPath from, VPath to, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var (fromParent, fromName) = ResolveParent(from);
            if (!fromParent.Children.TryGetValue(fromName, out var node))
            {
                throw FsErrors.NotFound(from);
            }

            // Moving a directory into itself would detach the subtree from the root and
            // leak it, so reject it the way rename(2) does with EINVAL.
            if (node is DirectoryNode && to.IsUnder(from))
            {
                throw FsErrors.InvalidArgument(to, "Invalid argument");
            }

            var (toParent, toName) = ResolveParent(to);
            if (toParent.Children.TryGetValue(toName, out var existing))
            {
                if (existing is DirectoryNode { Children.Count: > 0 })
                {
                    throw FsErrors.DirectoryNotEmpty(to);
                }

                ReleaseAccounting(existing);
            }

            fromParent.Children.Remove(fromName);
            toParent.Children[toName] = node;

            var now = _time.GetUtcNow();
            fromParent.Touch(now);
            toParent.Touch(now);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask CopyAsync(VPath from, VPath to, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (ResolveExisting(from) is not FileNode source)
            {
                throw FsErrors.IsADirectory(from);
            }

            WriteFileLocked(to, (byte[])source.Content.Clone(), append: false, mode: source.Mode);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask CreateSymlinkAsync(string target, VPath link, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var (parent, name) = ResolveParent(link);
            if (parent.Children.ContainsKey(name))
            {
                throw FsErrors.AlreadyExists(link);
            }

            EnsureNameLength(name);
            EnsureDepth(link);
            parent.Children[name] = new SymlinkNode(target, _time.GetUtcNow());
            parent.Touch(_time.GetUtcNow());
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<string> ReadSymlinkAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var (parent, name) = ResolveParent(path);
            if (!parent.Children.TryGetValue(name, out var node))
            {
                throw FsErrors.NotFound(path);
            }

            return node is SymlinkNode link
                ? ValueTask.FromResult(link.Target)
                : throw FsErrors.InvalidArgument(path, "Invalid argument");
        }
    }

    /// <inheritdoc />
    public ValueTask ChangeModeAsync(VPath path, int mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ResolveExisting(path).Mode = mode & 0xFFF;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetModifiedTimeAsync(VPath path, DateTimeOffset time, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ResolveExisting(path).ModifiedAt = time;
        }

        return ValueTask.CompletedTask;
    }

    private void CreateDirectoryCore(VPath path, bool recursive)
    {
        lock (_gate)
        {
            if (path.IsRoot)
            {
                if (!recursive)
                {
                    throw FsErrors.AlreadyExists(path);
                }

                return;
            }

            var segments = path.Segments;
            var current = path.IsAbsolute ? _root : _root;
            var now = _time.GetUtcNow();

            for (var i = 0; i < segments.Length; i++)
            {
                var last = i == segments.Length - 1;
                var name = segments[i];
                EnsureNameLength(name);

                if (current.Children.TryGetValue(name, out var child))
                {
                    child = Dereference(child, path);
                    if (child is not DirectoryNode existingDir)
                    {
                        throw last && !recursive ? FsErrors.AlreadyExists(path) : FsErrors.NotADirectory(path);
                    }

                    if (last && !recursive)
                    {
                        throw FsErrors.AlreadyExists(path);
                    }

                    current = existingDir;
                    continue;
                }

                if (!last && !recursive)
                {
                    throw FsErrors.NotFound(path);
                }

                if (i + 1 > Limits.MaxDepth)
                {
                    throw FsErrors.InvalidArgument(path, "Directory nesting too deep");
                }

                var created = new DirectoryNode(now);
                current.Children[name] = created;
                current.Touch(now);
                _directoryCount++;
                current = created;
            }
        }
    }

    private void WriteFileCore(VPath path, byte[] content, bool append)
    {
        lock (_gate)
        {
            WriteFileLocked(path, content, append, mode: null);
        }
    }

    private void WriteFileLocked(VPath path, byte[] content, bool append, int? mode)
    {
        var (parent, name) = ResolveParent(path);
        EnsureNameLength(name);
        EnsureDepth(path);

        byte[] final;
        long delta;

        if (parent.Children.TryGetValue(name, out var existing))
        {
            existing = Dereference(existing, path);
            if (existing is DirectoryNode)
            {
                throw FsErrors.IsADirectory(path);
            }

            var file = (FileNode)existing;
            if (append)
            {
                final = new byte[file.Content.Length + content.Length];
                file.Content.CopyTo(final, 0);
                content.CopyTo(final, file.Content.Length);
            }
            else
            {
                final = content;
            }

            delta = final.Length - file.Content.Length;
            EnsureCapacity(final.Length, delta, newFile: false);

            file.Content = final;
            file.ModifiedAt = _time.GetUtcNow();
            if (mode is { } m)
            {
                file.Mode = m;
            }

            _totalBytes += delta;
            return;
        }

        final = content;
        EnsureCapacity(final.Length, final.Length, newFile: true);

        var now = _time.GetUtcNow();
        parent.Children[name] = new FileNode(final, now) { Mode = mode ?? 0b110_100_100 };
        parent.Touch(now);
        _totalBytes += final.Length;
        _fileCount++;
    }

    private void EnsureCapacity(long fileSize, long delta, bool newFile)
    {
        if (fileSize > Limits.MaxFileBytes)
        {
            throw FsErrors.QuotaExceeded($"file exceeds {Limits.MaxFileBytes} bytes");
        }

        if (_totalBytes + delta > Limits.MaxTotalBytes)
        {
            throw FsErrors.QuotaExceeded($"filesystem exceeds {Limits.MaxTotalBytes} bytes");
        }

        if (newFile && _fileCount + 1 > Limits.MaxFiles)
        {
            throw FsErrors.QuotaExceeded($"more than {Limits.MaxFiles} files");
        }
    }

    private void EnsureNameLength(string name)
    {
        if (name.Length > Limits.MaxNameLength)
        {
            throw FsErrors.NameTooLong(name);
        }
    }

    /// <summary>
    /// Refuses a path with more components than the filesystem allows.
    /// </summary>
    /// <remarks>
    /// The cap is on the whole path, not just on how deep a <c>mkdir</c> reaches: a file
    /// sits one level below the directory holding it, so bounding directories alone would
    /// leave the deepest thing in the tree one past the limit. Making it a property of the
    /// path means anything that walks this filesystem — <c>find</c>, a glob, Python's
    /// <c>os.walk</c> — has a depth it can count on rather than one it has to guess.
    /// </remarks>
    private void EnsureDepth(VPath path)
    {
        if (path.Segments.Length > Limits.MaxDepth)
        {
            throw FsErrors.InvalidArgument(path, "Directory nesting too deep");
        }
    }

    private void ReleaseAccounting(Node node)
    {
        switch (node)
        {
            case FileNode file:
                _totalBytes -= file.Content.Length;
                _fileCount--;
                break;

            case DirectoryNode dir:
                _directoryCount--;
                foreach (var child in dir.Children.Values)
                {
                    ReleaseAccounting(child);
                }

                break;
        }
    }

    private (DirectoryNode Parent, string Name) ResolveParent(VPath path)
    {
        if (path.IsRoot || path.IsEmpty)
        {
            throw FsErrors.InvalidArgument(path, "Invalid argument");
        }

        var segments = path.Segments;
        var current = _root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!current.Children.TryGetValue(segments[i], out var child))
            {
                throw FsErrors.NotFound(path);
            }

            child = Dereference(child, path);
            current = child as DirectoryNode ?? throw FsErrors.NotADirectory(path);
        }

        return (current, segments[^1]);
    }

    private Node ResolveExisting(VPath path) =>
        TryResolve(path, followFinalLink: true, out var node) ? node : throw FsErrors.NotFound(path);

    private bool TryResolve(VPath path, bool followFinalLink, [NotNullWhen(true)] out Node? result)
    {
        result = null;
        var segments = path.Segments;
        Node current = _root;

        foreach (var segment in segments)
        {
            if (current is not DirectoryNode dir)
            {
                return false;
            }

            if (!dir.Children.TryGetValue(segment, out var child))
            {
                return false;
            }

            current = child is SymlinkNode ? Dereference(child, path) : child;
        }

        if (followFinalLink && current is SymlinkNode)
        {
            current = Dereference(current, path);
        }

        result = current;
        return true;
    }

    private Node Dereference(Node node, VPath origin)
    {
        var hops = 0;
        while (node is SymlinkNode link)
        {
            if (++hops > Limits.MaxSymlinkHops)
            {
                throw FsErrors.TooManySymlinks(origin);
            }

            var target = VPath.Parse(link.Target);
            if (!target.IsAbsolute)
            {
                // Relative link targets resolve against the link's own directory. Without
                // the origin's parent this cannot be answered, so treat it as dangling.
                target = origin.Parent?.Join(link.Target) ?? target;
            }

            if (!TryResolve(target, followFinalLink: false, out var resolved))
            {
                throw FsErrors.NotFound(target);
            }

            node = resolved;
        }

        return node;
    }

    private abstract class Node
    {
        protected Node(DateTimeOffset now)
        {
            CreatedAt = now;
            ModifiedAt = now;
            AccessedAt = now;
        }

        public abstract FileType Type { get; }

        public int Mode { get; set; } = 0b110_100_100;

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset ModifiedAt { get; set; }

        public DateTimeOffset AccessedAt { get; set; }

        public abstract long Size { get; }

        public FileMetadata ToMetadata() => new()
        {
            Type = Type,
            Size = Size,
            Mode = Mode,
            CreatedAt = CreatedAt,
            ModifiedAt = ModifiedAt,
            AccessedAt = AccessedAt,
        };
    }

    private sealed class FileNode(byte[] content, DateTimeOffset now) : Node(now)
    {
        public byte[] Content { get; set; } = content;

        public override FileType Type => FileType.File;

        public override long Size => Content.Length;
    }

    private sealed class DirectoryNode(DateTimeOffset now) : Node(now)
    {
        public DirectoryNode(DateTimeOffset now, int mode) : this(now) => Mode = mode;

        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

        public override FileType Type => FileType.Directory;

        public override long Size => 4096;

        public void Touch(DateTimeOffset now) => ModifiedAt = now;
    }

    private sealed class SymlinkNode(string target, DateTimeOffset now) : Node(now)
    {
        public string Target { get; } = target;

        public override FileType Type => FileType.Symlink;

        public override long Size => Target.Length;
    }
}
