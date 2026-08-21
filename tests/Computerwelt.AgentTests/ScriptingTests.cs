using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// The scripting constructs that hold a multi-step operation together.
/// </summary>
/// <remarks>
/// An agent rarely runs one command; it runs a short program. These cover the control
/// flow, status handling and quoting that program depends on — including the failure
/// modes, because a script that keeps going after a failed step does the wrong thing
/// quietly.
/// </remarks>
public sealed class ScriptingTests
{
    [Fact]
    public async Task For_loop_over_a_glob()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            for f in src/*.cs; do
              echo "$(basename "$f"): $(wc -l < "$f")"
            done
            """);

        Assert.Equal("Gadget.cs: 8\nWidget.cs: 8\n", output);
    }

    [Fact]
    public async Task While_read_walks_lines_with_spaces_intact()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            printf 'a  b\nc\td\n' | while IFS= read -r line; do
              echo "[$line]"
            done
            """);

        // Without `IFS=` and `-r` the runs of whitespace collapse — the classic way a
        // loop over filenames silently mangles them.
        Assert.Equal("[a  b]\n[c\td]\n", output);
    }

    [Fact]
    public async Task If_tests_a_file_and_a_string()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            if [ -f README.md ] && [ -d src ]; then echo both; fi
            if [ -z "" ]; then echo empty; fi
            if [ "abc" = "abc" ]; then echo equal; fi
            """);

        Assert.Equal("both\nempty\nequal\n", output);
    }

    [Fact]
    public async Task Case_dispatches_on_a_pattern()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            for f in src/Widget.cs docs/notes.md config.json; do
              case "$f" in
                *.cs) echo "code $f" ;;
                *.md) echo "prose $f" ;;
                *)    echo "other $f" ;;
              esac
            done
            """);

        Assert.Equal("code src/Widget.cs\nprose docs/notes.md\nother config.json\n", output);
    }

    [Fact]
    public async Task Functions_take_arguments_and_return_a_status()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            check() {
              grep -q "$1" "$2" && return 0 || return 1
            }
            check Widget src/Widget.cs && echo found
            check Sprocket src/Widget.cs || echo missing
            """);

        Assert.Equal("found\nmissing\n", output);
    }

    [Fact]
    public async Task Errexit_stops_at_the_first_failure()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("""
            set -e
            echo first
            cat /work/absent.txt
            echo second
            """);

        // The second echo must not run: `set -e` is the only thing standing between a
        // failed step and a script that reports success anyway.
        Assert.Equal("first\n", result.Stdout.ToString());
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Nounset_catches_a_typo_in_a_variable_name()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("set -u; echo \"${nmae:-}\"; echo \"$nmae\"");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unbound variable", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_status_of_the_last_command_is_visible()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("0\n", await session.OutAsync("true; echo $?"));
        Assert.Equal("1\n", await session.OutAsync("false; echo $?"));
        Assert.Equal("127\n", await session.OutAsync("definitely-not-a-command 2>/dev/null; echo $?"));
    }

    [Fact]
    public async Task Exit_status_propagates_out_of_the_session()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal(3, (await session.RunAsync("exit 3")).ExitCode);
    }

    [Fact]
    public async Task Parameter_expansion_supplies_defaults_and_trims()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            path=src/Widget.cs
            echo "${path##*/}"
            echo "${path%.cs}"
            echo "${missing:-fallback}"
            echo "${path/Widget/Gadget}"
            """);

        Assert.Equal("Widget.cs\nsrc/Widget\nfallback\nsrc/Gadget.cs\n", output);
    }

    [Fact]
    public async Task Quoting_keeps_a_path_with_spaces_in_one_piece()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            mkdir -p "/work/two words"
            echo content > "/work/two words/file.txt"
            cat "/work/two words/file.txt"
            find /work -name 'file.txt' | wc -l
            """);

        Assert.Equal("content\n1\n", output);
    }

    [Fact]
    public async Task Environment_variables_reach_a_command()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("value\n", await session.OutAsync("MY_SETTING=value; export MY_SETTING; echo \"$MY_SETTING\""));
        Assert.Equal("once\n", await session.OutAsync("MY_SETTING=once env | grep '^MY_SETTING=' | cut -d= -f2"));
    }

    [Fact]
    public async Task Cd_changes_where_later_commands_look()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("/work/src\nWidget.cs\n", await session.OutAsync("cd src && pwd && ls *.cs | tail -1"));
    }
}
