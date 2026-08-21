using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// The cap on how deep a tree walk will go.
/// </summary>
/// <remarks>
/// Over the shell's own filesystem the cap can never be reached, because that filesystem
/// refuses to create a path deeper than it in the first place. What it is for is a host
/// that supplies storage this sandbox did not build — where a tree can be arbitrarily
/// deep, or, as here, has no bottom at all.
/// </remarks>
public sealed class DepthLimitTests
{
    /// <summary>A filesystem where every directory contains one more directory, forever.</summary>
    /// <remarks>
    /// Not a contrivance: a real filesystem reached through a host adapter can present the
    /// same shape through a symbolic link that points at one of its own ancestors, and an
    /// unguarded walk over it never returns.
    /// </remarks>
    private sealed class BottomlessFileSystem : IPyFileSystem
    {
        public string WorkingDirectory => "/";

        public IReadOnlyDictionary<string, string> Environment { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public bool Exists(string path) => true;

        public bool IsFile(string path) => path.EndsWith("leaf.txt", StringComparison.Ordinal);

        public bool IsDirectory(string path) => !IsFile(path);

        public IReadOnlyList<string> List(string path) => ["down", "leaf.txt"];

        public byte[] Read(string path) => [];

        public void Write(string path, byte[] content) { }

        public void Append(string path, byte[] content) { }

        public void Remove(string path) { }

        public void CreateDirectory(string path, bool parents, bool existsOk) { }

        public void RemoveDirectory(string path) { }

        public long Size(string path) => 0;

        public void Rename(string from, string to) { }

        public int Mode(string path) => IsFile(path) ? 0b1000_000_110_100_100 : 0b100_000_111_101_101;

        public double ModifiedAt(string path) => 0;
    }

    private static RunResult Run(string source, int? maxDepth = null)
    {
        var limits = maxDepth is { } depth
            ? ExecutionLimits.Default with { MaxDirectoryDepth = depth }
            : ExecutionLimits.Default;

        return new PythonRunner(limits) { FileSystem = new BottomlessFileSystem() }.Run(source);
    }

    [Fact]
    public void A_walk_of_a_bottomless_tree_stops_at_the_cap()
    {
        var result = Run("""
            import os
            visited = 0
            for directory, directories, files in os.walk('/'):
                visited += 1
            """, maxDepth: 8);

        // It raises rather than returning what it managed to see: a walk that quietly
        // stopped part-way would report a subset of the tree as though it were all of it.
        Assert.False(result.Succeeded);
        Assert.Equal("OSError", result.ExceptionType);
        Assert.Contains("exceeds the depth limit of 8", result.Exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cap_is_catchable_and_names_the_directory_it_gave_up_on()
    {
        var result = Run("""
            import os
            reached = None
            try:
                for directory, _, _ in os.walk('/'):
                    reached = directory
            except OSError as e:
                print(reached)
                print('caught')
            """, maxDepth: 4);

        Assert.True(result.Succeeded);

        // Four levels below the root, then the refusal — and it is an ordinary OSError, so
        // a program that wants to carry on can.
        Assert.Equal("/down/down/down/down\ncaught\n", result.Stdout);
    }

    [Fact]
    public void A_recursive_glob_is_bounded_the_same_way()
    {
        var result = Run("""
            import glob
            glob.glob('/**/leaf.txt', recursive=True)
            """, maxDepth: 6);

        Assert.False(result.Succeeded);
        Assert.Equal("OSError", result.ExceptionType);
        Assert.Contains("depth limit of 6", result.Exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_glob_and_walk_inherit_the_runs_cap()
    {
        var glob = Run("from pathlib import Path\nlist(Path('/').rglob('leaf.txt'))\n", maxDepth: 5);
        Assert.Equal("OSError", glob.ExceptionType);

        var walk = Run("from pathlib import Path\nlist(Path('/').walk())\n", maxDepth: 5);
        Assert.Equal("OSError", walk.ExceptionType);
    }

    [Fact]
    public void The_callers_own_max_depth_stops_short_of_the_cap_without_raising()
    {
        var result = Run("""
            import os
            print(len([d for d, _, _ in os.walk('/', max_depth=3)]))
            """, maxDepth: 64);

        // A bound the caller asked for is not a limit being hit, so it ends the walk
        // cleanly: the root plus three levels.
        Assert.True(result.Succeeded);
        Assert.Equal("4\n", result.Stdout);
    }

    [Fact]
    public void Max_depth_counts_from_the_top_not_from_the_root()
    {
        var result = Run("""
            import os
            print([d for d, _, _ in os.walk('/down/down', max_depth=1)])
            """);

        Assert.Equal("['/down/down', '/down/down/down']\n", result.Stdout);
    }

    [Fact]
    public void Max_depth_zero_visits_only_the_top()
    {
        var result = Run("""
            import os
            for directory, directories, files in os.walk('/', max_depth=0):
                print(directory, directories, files)
            """);

        // The directory list still names what is below, as CPython's does; the walk simply
        // does not go there.
        Assert.Equal("/ ['down'] ['leaf.txt']\n", result.Stdout);
    }

    [Fact]
    public void A_negative_or_non_integer_max_depth_is_refused()
    {
        Assert.Equal("ValueError", Run("import os\nlist(os.walk('/', max_depth=-1))\n").ExceptionType);
        Assert.Equal("TypeError", Run("import os\nlist(os.walk('/', max_depth='2'))\n").ExceptionType);
        Assert.Equal("ValueError", Run(
            "from pathlib import Path\nlist(Path('/').walk(max_depth=-1))\n").ExceptionType);
    }

    [Fact]
    public void A_bottom_up_walk_is_bounded_too()
    {
        var result = Run("""
            import os
            print([d for d, _, _ in os.walk('/', topdown=False, max_depth=2)])
            """);

        // Deepest first, and it never descends past the bound — which matters more here
        // than top-down, since a bottom-up walk cannot be pruned by editing the list.
        Assert.Equal("['/down/down', '/down', '/']\n", result.Stdout);
    }
}
