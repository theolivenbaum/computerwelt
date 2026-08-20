using System.Text;
using Xunit;

namespace Computerwelt.Emulation.Bash.Tests;

public sealed class InMemoryFileSystemTests
{
    private static readonly byte[] Hello = "hello"u8.ToArray();

    [Fact]
    public async Task Writes_and_reads_a_file()
    {
        var fs = new InMemoryFileSystem();
        await fs.WriteFileAsync("/a.txt", Hello);

        Assert.Equal(Hello, await fs.ReadFileAsync("/a.txt"));
        Assert.True(await fs.ExistsAsync("/a.txt"));
    }

    [Fact]
    public async Task Appends_to_a_file()
    {
        var fs = new InMemoryFileSystem();
        await fs.WriteFileAsync("/a.txt", "a"u8.ToArray());
        await fs.AppendFileAsync("/a.txt", "b"u8.ToArray());

        Assert.Equal("ab", Encoding.UTF8.GetString(await fs.ReadFileAsync("/a.txt")));
    }

    [Fact]
    public async Task Reading_a_missing_file_reports_not_found()
    {
        var fs = new InMemoryFileSystem();
        var error = await Assert.ThrowsAsync<FileSystemException>(async () => await fs.ReadFileAsync("/missing"));

        Assert.Equal(FileSystemErrorKind.NotFound, error.FsKind);
    }

    [Fact]
    public async Task Creates_nested_directories_only_when_recursive()
    {
        var fs = new InMemoryFileSystem();

        await Assert.ThrowsAsync<FileSystemException>(
            async () => await fs.CreateDirectoryAsync("/a/b/c", recursive: false));

        await fs.CreateDirectoryAsync("/a/b/c", recursive: true);
        Assert.True((await fs.StatAsync("/a/b/c")).IsDirectory);
    }

    [Fact]
    public async Task Refuses_to_remove_a_non_empty_directory_without_recursion()
    {
        var fs = new InMemoryFileSystem();
        await fs.CreateDirectoryAsync("/a", recursive: true);
        await fs.WriteFileAsync("/a/f", Hello);

        var error = await Assert.ThrowsAsync<FileSystemException>(
            async () => await fs.RemoveAsync("/a", recursive: false));

        Assert.Equal(FileSystemErrorKind.DirectoryNotEmpty, error.FsKind);

        await fs.RemoveAsync("/a", recursive: true);
        Assert.False(await fs.ExistsAsync("/a"));
    }

    [Fact]
    public async Task Lists_a_directory_in_name_order()
    {
        var fs = new InMemoryFileSystem();
        await fs.CreateDirectoryAsync("/d", recursive: true);
        await fs.WriteFileAsync("/d/c", Hello);
        await fs.WriteFileAsync("/d/a", Hello);
        await fs.CreateDirectoryAsync("/d/b", recursive: true);

        var entries = await fs.ReadDirectoryAsync("/d");

        Assert.Equal(["a", "b", "c"], entries.Select(static e => e.Name));
        Assert.Equal(FileType.Directory, entries[1].Type);
    }

    [Fact]
    public async Task Follows_symlinks_on_stat_but_not_on_stat_link()
    {
        var fs = new InMemoryFileSystem();
        await fs.WriteFileAsync("/target", Hello);
        await fs.CreateSymlinkAsync("/target", "/link");

        Assert.Equal(FileType.File, (await fs.StatAsync("/link")).Type);
        Assert.Equal(FileType.Symlink, (await fs.StatLinkAsync("/link")).Type);
        Assert.Equal(Hello, await fs.ReadFileAsync("/link"));
    }

    [Fact]
    public async Task Enforces_the_total_size_quota()
    {
        var fs = new InMemoryFileSystem(new FsLimits { MaxTotalBytes = 16, MaxFileBytes = 16 });
        await fs.WriteFileAsync("/a", new byte[10]);

        var error = await Assert.ThrowsAsync<FileSystemException>(
            async () => await fs.WriteFileAsync("/b", new byte[10]));

        Assert.Equal(FileSystemErrorKind.QuotaExceeded, error.FsKind);
    }

    [Fact]
    public async Task Enforces_the_file_count_quota()
    {
        var fs = new InMemoryFileSystem(new FsLimits { MaxFiles = 2 });
        await fs.WriteFileAsync("/a", Hello);
        await fs.WriteFileAsync("/b", Hello);

        await Assert.ThrowsAsync<FileSystemException>(async () => await fs.WriteFileAsync("/c", Hello));
    }

    [Fact]
    public async Task Renaming_a_directory_into_itself_is_rejected()
    {
        // Allowing this would detach the subtree from the root and leak it.
        var fs = new InMemoryFileSystem();
        await fs.CreateDirectoryAsync("/a/b", recursive: true);

        await Assert.ThrowsAsync<FileSystemException>(async () => await fs.RenameAsync("/a", "/a/b/c"));
    }

    [Fact]
    public async Task Reports_usage()
    {
        var fs = new InMemoryFileSystem();
        await fs.CreateDirectoryAsync("/d", recursive: true);
        await fs.WriteFileAsync("/d/f", new byte[100]);

        var usage = fs.GetUsage();

        Assert.Equal(100, usage.TotalBytes);
        Assert.Equal(1, usage.FileCount);
        Assert.Equal(2, usage.DirectoryCount);
    }

    [Fact]
    public async Task Two_instances_share_nothing()
    {
        var a = new InMemoryFileSystem();
        var b = new InMemoryFileSystem();

        await a.WriteFileAsync("/secret", Hello);

        Assert.False(await b.ExistsAsync("/secret"));
    }
}
