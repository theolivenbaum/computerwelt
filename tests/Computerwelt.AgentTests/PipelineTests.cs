using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// Pipelines that summarise output rather than read it.
/// </summary>
/// <remarks>
/// The recurring shape is <c>… | grep -E … | sort -u | head -N</c>: turn a wall of build
/// or test output into the few distinct lines worth looking at. It only works if exit
/// status, ordering and truncation all behave, so these test the whole chain rather than
/// the pieces.
/// </remarks>
public sealed class PipelineTests
{
    [Fact]
    public async Task Filter_deduplicate_and_truncate()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            printf '%s\n' \
              'Widget.cs(3,5): error CS0103: missing' \
              'Widget.cs(3,5): error CS0103: missing' \
              'Gadget.cs(9,1): warning CS8618: null' \
              'Build FAILED.' \
              | grep -E ': error|: warning' | sort -u | head -30
            """);

        Assert.Equal(
            """
            Gadget.cs(9,1): warning CS8618: null
            Widget.cs(3,5): error CS0103: missing

            """,
            output);
    }

    [Fact]
    public async Task A_pipelines_status_is_the_last_commands()
    {
        var session = await AgentSession.NewAsync();

        // This is why `... | grep -q x` reads as a test: the pipeline's status is grep's.
        Assert.Equal("1\n", await session.OutAsync("cat README.md | grep -q Sprocket; echo $?"));
        Assert.Equal("0\n", await session.OutAsync("cat README.md | grep -q Sample; echo $?"));
    }

    [Fact]
    public async Task Pipefail_surfaces_a_failure_from_the_middle()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("0\n", await session.OutAsync("cat missing.txt 2>/dev/null | wc -l >/dev/null; echo $?"));
        Assert.Equal("1\n", await session.OutAsync("set -o pipefail; cat missing.txt 2>/dev/null | wc -l >/dev/null; echo $?"));
    }

    [Fact]
    public async Task Uniq_counts_repeated_lines()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal(
            "      2 error\n      1 warning\n",
            await session.OutAsync("printf 'error\\nerror\\nwarning\\n' | sort | uniq -c"));
    }

    [Fact]
    public async Task Cut_and_tr_reshape_a_line()
    {
        var session = await AgentSession.NewAsync();

        // `cut` splits on the delimiter and nothing else — the trailing colon belongs to
        // the field, so a second pass is what removes it.
        Assert.Equal("CS0103:\n", await session.OutAsync("echo 'Widget.cs(3,5): error CS0103: x' | cut -d' ' -f3"));
        Assert.Equal("CS0103\n", await session.OutAsync("echo 'Widget.cs(3,5): error CS0103: x' | cut -d' ' -f3 | tr -d ':'"));
        Assert.Equal("WIDGET\n", await session.OutAsync("echo widget | tr a-z A-Z"));
    }

    [Fact]
    public async Task Awk_selects_a_field_and_sums()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("6\n", await session.OutAsync("printf '1 a\\n2 b\\n3 c\\n' | awk '{ total += $1 } END { print total }'"));
    }

    [Fact]
    public async Task Xargs_feeds_a_command_from_a_list()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal(
            "src/Gadget.cs\nsrc/Widget.cs\n",
            await session.OutAsync("find src -name '*.cs' | sort | xargs -n1 echo | sed 's|^\\./||'"));
    }

    [Fact]
    public async Task Tee_writes_a_copy_while_the_pipeline_continues()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("1\n", await session.OutAsync("echo saved | tee /work/copy.txt | wc -l"));
        Assert.Equal("saved\n", await session.ReadAsync("/work/copy.txt"));
    }

    [Fact]
    public async Task Command_substitution_captures_output()
    {
        var session = await AgentSession.NewAsync();

        // Trailing newlines are stripped by the substitution itself, which is what makes
        // `count=$(… | wc -l)` usable in arithmetic.
        Assert.Equal("3 cs files\n", await session.OutAsync("n=$(find . -name '*.cs' | wc -l); echo \"$n cs files\""));
    }

    [Fact]
    public async Task Stderr_can_be_kept_apart_or_merged()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal(string.Empty, await session.OutAsync("cat missing.txt 2>/dev/null"));

        // `2>&1 | …` is how a compiler's diagnostics get into the same filter as its
        // ordinary output.
        Assert.Equal("1\n", await session.OutAsync("cat missing.txt 2>&1 | grep -c 'No such file'"));
    }
}
