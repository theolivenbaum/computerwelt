using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// Python treated as a command: how it is invoked, what it sees, and how it reports back.
/// </summary>
/// <remarks>
/// A script that runs under <c>python</c> here should behave the way it would under
/// CPython in the same position in a pipeline — read standard input, see its own
/// arguments, and exit with the status it asked for. Where it cannot, the divergence is
/// pinned rather than left to be discovered.
/// </remarks>
public sealed class PythonToolTests
{
    [Fact]
    public async Task Dash_c_runs_a_one_liner()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("4\n", await session.OutAsync("python3 -c 'print(2 + 2)'"));
        Assert.Equal("4\n", await session.OutAsync("python -c 'print(2 + 2)'"));
    }

    [Fact]
    public async Task A_script_file_runs_with_its_own_path_as_argv_zero()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            cat > /work/tool.py <<'PY'
            import sys
            print(sys.argv[0], sys.argv[1:])
            PY
            python3 /work/tool.py --flag value
            """);

        Assert.Equal("/work/tool.py ['--flag', 'value']\n", output);
    }

    [Fact]
    public async Task Dash_c_reports_its_arguments_the_way_cpython_does()
    {
        var session = await AgentSession.NewAsync();

        // CPython puts the literal "-c" in argv[0] and the code's own arguments after it,
        // which is what lets one script be invoked either way.
        Assert.Equal(
            "['-c', 'alpha', 'beta']\n",
            await session.OutAsync("python3 -c 'import sys; print(sys.argv)' alpha beta"));
    }

    [Fact]
    public async Task A_heredoc_script_is_named_dash_in_argv()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - one two <<'PY'
            import sys
            print(sys.argv)
            PY
            """);

        Assert.Equal("['-', 'one', 'two']\n", output);
    }

    [Fact]
    public async Task Standard_input_can_be_piped_in()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("HELLO\n", await session.OutAsync("echo hello | python3 -c \"print(input().upper())\""));
    }

    [Fact]
    public async Task Sys_exit_sets_the_shells_status()
    {
        var session = await AgentSession.NewAsync();

        // Without this, `python check.py || handle-it` never fires: a script that means to
        // report failure would look like a crash instead, or like success.
        Assert.Equal("3\n", await session.OutAsync("python3 -c 'import sys; sys.exit(3)'; echo $?"));
        Assert.Equal("0\n", await session.OutAsync("python3 -c 'import sys; sys.exit(0)'; echo $?"));
        Assert.Equal("0\n", await session.OutAsync("python3 -c 'import sys; sys.exit()'; echo $?"));
        Assert.Equal("3\n", await session.OutAsync("python3 -c 'raise SystemExit(3)'; echo $?"));
    }

    [Fact]
    public async Task Sys_exit_with_a_message_prints_it_and_exits_one()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("python3 -c 'import sys; sys.exit(\"no config found\")'");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("no config found\n", result.Stderr.ToString());

        // A requested exit is not a crash, so there is no traceback with it.
        Assert.DoesNotContain("Traceback", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_uncaught_exception_exits_one_with_a_traceback()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("python3 -c 'raise ValueError(\"bad input\")'");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Traceback (most recent call last):", result.Stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("ValueError: bad input", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_script_is_a_usage_error()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("python3 /work/not-here.py");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("can't open file", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Python_output_flows_through_a_pipeline()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync(
            "python3 -c \"print('b'); print('a'); print('a')\" | sort -u | tr '\\n' ','");

        Assert.Equal("a,b,", output);
    }

    [Fact]
    public async Task Python_reads_the_shells_environment_and_working_directory()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            export BUILD_ID=42
            cd src
            python3 -c "import os; print(os.getenv('BUILD_ID'), os.getcwd())"
            """);

        Assert.Equal("42 /work/src\n", output);
    }

    [Fact]
    public async Task Version_is_reported_without_running_anything()
    {
        var session = await AgentSession.NewAsync();

        Assert.StartsWith("Python 3.", await session.OutAsync("python3 --version"), StringComparison.Ordinal);
    }
}
