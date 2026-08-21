using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// Finding files by pattern, which used to be the shell's job alone.
/// </summary>
/// <remarks>
/// <para>
/// Before <c>glob</c>, <c>fnmatch</c> and <c>os.walk</c> existed here, a Python script that
/// wanted "every <c>.cs</c> under this tree" had to hand-roll a recursion over
/// <c>os.listdir</c> — or give up and let <c>find</c> do it and pipe the result in. Both
/// still work, and both are covered here alongside the direct forms, because a script
/// written for CPython uses whichever its author reached for.
/// </para>
/// <para>
/// The fixtures in <c>tests/monty-extensions/</c> pin what each of these functions
/// promises; this file is about the operation.
/// </para>
/// </remarks>
public sealed class TreeSearchTests
{
    [Fact]
    public async Task Glob_finds_files_one_level_down()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 -c "import glob; print(sorted(glob.glob('src/*.cs')))"
            """);

        Assert.Equal("['src/Gadget.cs', 'src/Widget.cs']\n", output);
    }

    [Fact]
    public async Task Glob_finds_files_across_the_whole_tree()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 -c "import glob; print(glob.glob('**/*.cs', recursive=True))"
            """);

        Assert.Equal("['src/Gadget.cs', 'src/Widget.cs', 'tests/WidgetTests.cs']\n", output);
    }

    [Fact]
    public async Task Os_walk_replaces_the_hand_rolled_recursion()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import os
            found = []
            for directory, directories, files in os.walk('/work'):
                for name in files:
                    if name.endswith('.cs'):
                        found.append(os.path.join(directory, name))
            print(sorted(found))
            PY
            """);

        Assert.Equal(
            "['/work/src/Gadget.cs', '/work/src/Widget.cs', '/work/tests/WidgetTests.cs']\n",
            output);
    }

    [Fact]
    public async Task A_walk_prunes_the_directories_it_should_not_descend_into()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            mkdir -p /work/obj/Debug
            echo 'generated' > /work/obj/Debug/Generated.cs
            python3 - <<'PY'
            import os
            found = []
            for directory, directories, files in os.walk('/work'):
                directories[:] = [d for d in directories if d not in ('obj', 'bin')]
                found += [f for f in files if f.endswith('.cs')]
            print(sorted(found))
            PY
            """);

        // Skipping build output is the reason a walk is used instead of a glob, and it
        // only works because the walk reads the list back after the loop body ran.
        Assert.Equal("['Gadget.cs', 'Widget.cs', 'WidgetTests.cs']\n", output);
    }

    [Fact]
    public async Task Scandir_answers_the_type_from_the_listing()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import os
            with os.scandir('/work') as scan:
                for entry in scan:
                    print(entry.name, 'dir' if entry.is_dir() else entry.stat().st_size)
            PY
            """);

        Assert.Equal(
            """
            README.md 74
            config.json 81
            docs dir
            src dir
            tests dir

            """,
            output);
    }

    [Fact]
    public async Task Fnmatch_filters_a_list_of_names()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import fnmatch, os
            print(fnmatch.filter(sorted(os.listdir('/work/src')), '*.cs'))
            print(fnmatch.filter(sorted(os.listdir('/work/src')), '*.?sproj'))
            PY
            """);

        Assert.Equal("['Gadget.cs', 'Widget.cs']\n['Sample.csproj']\n", output);
    }

    [Fact]
    public async Task Pathlib_globs_and_reads_in_one_pass()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            from pathlib import Path
            for path in Path('/work').rglob('*.cs'):
                first = path.read_text().splitlines()[0]
                print(path.name, first)
            PY
            """);

        Assert.Equal(
            """
            Gadget.cs ﻿namespace Sample;
            Widget.cs namespace Sample;
            WidgetTests.cs namespace Sample.Tests;

            """.Replace("\\ufeff", "﻿", StringComparison.Ordinal),
            output);
    }

    [Fact]
    public async Task Relpath_turns_absolute_results_into_something_printable()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import os
            paths = []
            for directory, _, files in os.walk('/work/src'):
                paths += [os.path.relpath(os.path.join(directory, f), '/work') for f in files]
            print(sorted(paths))
            PY
            """);

        Assert.Equal("['src/Gadget.cs', 'src/Sample.csproj', 'src/Widget.cs']\n", output);
    }

    [Fact]
    public async Task Find_piped_into_python_still_works()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            find /work -name '*.cs' | sort | python3 -c "
            import sys
            for line in sys.stdin:
                print(line.strip().rsplit('/', 1)[-1])
            "
            """);

        // The shell half was the workaround while Python had no glob, and remains the
        // better tool when the filter is one the shell already knows how to express.
        Assert.Equal("Gadget.cs\nWidget.cs\nWidgetTests.cs\n", output);
    }

    [Fact]
    public async Task Listing_a_file_says_so_rather_than_claiming_it_is_missing()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("python3 -c \"import os; os.listdir('/work/README.md')\"");

        // The two are different mistakes and scripts branch on which;
        // `tests/monty-spec/mount_fs__ops.py` pins both messages, and the shell-backed
        // filesystem used to report the wrong one.
        Assert.Contains(
            "NotADirectoryError: [Errno 20] Not a directory: '/work/README.md'",
            result.Stderr.ToString(),
            StringComparison.Ordinal);

        var missing = await session.RunAsync("python3 -c \"import os; os.listdir('/work/nowhere')\"");

        Assert.Contains(
            "FileNotFoundError: [Errno 2] No such file or directory: '/work/nowhere'",
            missing.Stderr.ToString(),
            StringComparison.Ordinal);
    }
}
