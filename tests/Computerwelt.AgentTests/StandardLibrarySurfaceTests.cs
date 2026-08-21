using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// The importable module set, written down.
/// </summary>
/// <remarks>
/// <para>
/// A script written against CPython assumes a stdlib that is not here, and finds out at
/// the import — after the shell commands around it have already run. So the set is pinned
/// in both directions: what is importable, and what is not. Adding a module means editing
/// <see cref="Available"/>, which is the point — the list is the documentation.
/// </para>
/// <para>
/// <see cref="Absent"/> is not a to-do list. Some of its entries are refusals
/// (<c>subprocess</c>, <c>socket</c>) covered by <see cref="SandboxBoundaryTests"/>; the
/// rest are simply not ported yet, and a script that needs one has to be written a
/// different way.
/// </para>
/// </remarks>
public sealed class StandardLibrarySurfaceTests
{
    /// <summary>Every module a sandboxed script can import.</summary>
    public static readonly string[] Available =
    [
        "__future__",
        "asyncio",
        "collections",
        "dataclasses",
        "datetime",
        "gc",
        "io",
        "itertools",
        "json",
        "math",
        "os",
        "pathlib",
        "re",
        "sys",
        "typing",
        "unicodedata",
    ];

    /// <summary>
    /// Modules an agent's script is likely to reach for that are not here.
    /// </summary>
    /// <remarks>
    /// Kept as a list rather than a blanket "anything else fails" so that adding one is a
    /// deliberate edit in two places, and so the failure a script would hit is written
    /// down where someone looking for it will find it.
    /// </remarks>
    public static readonly string[] Absent =
    [
        "argparse",
        "base64",
        "bisect",
        "contextlib",
        "copy",
        "csv",
        "difflib",
        "fnmatch",
        "functools",
        "glob",
        "hashlib",
        "heapq",
        "operator",
        "posixpath",
        "random",
        "shlex",
        "shutil",
        "statistics",
        "string",
        "struct",
        "tempfile",
        "textwrap",
        "time",
        "uuid",
    ];

    public static TheoryData<string> AvailableModules => [.. Available];

    public static TheoryData<string> AbsentModules => [.. Absent];

    [Theory]
    [MemberData(nameof(AvailableModules))]
    public async Task Module_can_be_imported(string module)
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync($"python3 -c 'import {module}'");

        Assert.Equal(string.Empty, result.Stderr.ToString());
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [MemberData(nameof(AbsentModules))]
    public async Task Module_is_not_importable(string module)
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync($"python3 -c 'import {module}'");

        // The failure is a plain ModuleNotFoundError naming the module, not a stub that
        // imports and then misbehaves.
        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"No module named '{module}'", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Os_offers_the_operations_it_claims_and_no_others()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import os
            names = ['getenv', 'listdir', 'stat', 'mkdir', 'makedirs', 'remove',
                     'unlink', 'rmdir', 'rename', 'replace', 'getcwd', 'path']
            print(' '.join(n for n in names if hasattr(os, n)))
            print('walk' , hasattr(os, 'walk'))
            print('system', hasattr(os, 'system'))
            PY
            """);

        Assert.Equal(
            """
            getenv listdir stat mkdir makedirs remove unlink rmdir rename replace getcwd path
            walk False
            system False

            """,
            output);
    }

    [Fact]
    public async Task Walking_a_tree_is_done_with_listdir_since_os_walk_is_absent()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import os

            def walk(root):
                for name in sorted(os.listdir(root)):
                    p = root + '/' + name
                    if os.path.isdir(p):
                        yield from walk(p)
                    else:
                        yield p

            print(len(list(walk('/work'))))
            PY
            """);

        // The workaround is short enough to inline, which is why the absence is recorded
        // rather than worked around in the port.
        Assert.Equal("7\n", output);
    }
}
