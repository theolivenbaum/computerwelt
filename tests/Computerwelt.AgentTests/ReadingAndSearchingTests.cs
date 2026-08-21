using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// The commands an agent reaches for first: look at a file, find a string, count matches.
/// </summary>
/// <remarks>
/// Every script here was taken from a real working session against this repository rather
/// than invented, which is the point of the file — the conformance corpus proves the
/// builtins are individually right, and this proves the handful of shapes that actually
/// get typed still work when composed.
/// </remarks>
public sealed class ReadingAndSearchingTests
{
    [Fact]
    public async Task Cat_shows_a_whole_file()
    {
        var session = await AgentSession.NewAsync();

        Assert.StartsWith("# Sample", await session.OutAsync("cat README.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Head_and_tail_take_a_slice()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("namespace Sample;\n", await session.OutAsync("head -1 src/Widget.cs"));
        Assert.Equal("}\n", await session.OutAsync("tail -1 src/Widget.cs"));
        Assert.Equal(
            "    public string Describe() => $\"widget {Count}\";\n",
            await session.OutAsync("tail -2 src/Widget.cs | head -1"));
    }

    [Fact]
    public async Task Sed_prints_a_line_range()
    {
        var session = await AgentSession.NewAsync();

        // `sed -n 'a,bp'` is how a large file gets read a window at a time.
        Assert.Equal(
            "public sealed class Widget\n{\n",
            await session.OutAsync("sed -n '3,4p' src/Widget.cs"));
    }

    [Fact]
    public async Task Wc_counts_lines()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("8\n", await session.OutAsync("wc -l < src/Widget.cs"));
    }

    [Fact]
    public async Task Ls_and_find_enumerate_the_tree()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("Gadget.cs\nSample.csproj\nWidget.cs\n", await session.OutAsync("ls src | sort"));

        Assert.Equal(
            "./src/Gadget.cs\n./src/Widget.cs\n./tests/WidgetTests.cs\n",
            await session.OutAsync("find . -name '*.cs' | sort"));
    }

    [Fact]
    public async Task Find_filters_by_type_and_prunes()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal(
            "./docs\n./src\n./tests\n",
            await session.OutAsync("find . -mindepth 1 -type d | sort"));

        // Skipping a directory wholesale is the difference between a usable listing and
        // one buried in build output.
        Assert.Equal(
            "./config.json\n./README.md\n",
            await session.OutAsync("find . -maxdepth 1 -type f | sort -r"));
    }

    [Fact]
    public async Task Grep_reports_file_and_line()
    {
        var session = await AgentSession.NewAsync();

        // One file named on the command line means no filename prefix; the line number is
        // still there, which is what makes the output a usable jump target.
        Assert.Equal(
            "5:    public int Count { get; set; }\n",
            await session.OutAsync("grep -n 'int Count' src/Widget.cs"));

        // Two or more files, and grep prefixes each match with the file it came from.
        Assert.Equal(
            "src/Gadget.cs:5:    public int Count { get; set; }\n"
            + "src/Widget.cs:5:    public int Count { get; set; }\n",
            await session.OutAsync("grep -n 'int Count' src/Gadget.cs src/Widget.cs"));
    }

    [Fact]
    public async Task Grep_recurses_with_a_glob_filter()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal(
            "src/Gadget.cs\nsrc/Widget.cs\n",
            await session.OutAsync("grep -rl --include='*.cs' 'Count' . | sed 's|^\\./||' | sort"));
    }

    [Fact]
    public async Task Grep_counts_and_inverts()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("2\n", await session.OutAsync("grep -c 'Count' src/Widget.cs"));
        Assert.Equal("0\n", await session.OutAsync("grep -vc '' src/Widget.cs"));
    }

    [Fact]
    public async Task Grep_shows_context_around_a_match()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal(
            """
            public sealed class Widget
            {
                public int Count { get; set; }

            """,
            await session.OutAsync("grep -B2 'int Count' src/Widget.cs"));
    }

    [Fact]
    public async Task Grep_extended_regex_alternation()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal(
            "config.json\ndocs/notes.md\nsrc/Gadget.cs\nsrc/Widget.cs\n",
            await session.OutAsync("grep -rlE 'widget|gadget' . | sed 's|^\\./||' | sort"));
    }

    [Fact]
    public async Task Grep_only_matching_extracts_the_hit()
    {
        var session = await AgentSession.NewAsync();

        // `-o` is how a version or an identifier gets pulled out of a line without a
        // separate parser.
        Assert.Equal("net10.0\n", await session.OutAsync("grep -oE 'net[0-9.]+' src/Sample.csproj"));
    }

    [Fact]
    public async Task Grep_finds_nothing_and_says_so_with_status_1()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("grep -r 'Sprocket' .");

        // A search that matched nothing exits 1, which is what makes `grep -q ... && ...`
        // usable as a condition — it must not be mistaken for an error.
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout.ToString());
        Assert.Equal(string.Empty, result.Stderr.ToString());
    }

    [Fact]
    public async Task Grep_quiet_gates_a_command()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal(
            "present\n",
            await session.OutAsync("grep -rq 'TargetFramework' src && echo present || echo absent"));
    }
}
