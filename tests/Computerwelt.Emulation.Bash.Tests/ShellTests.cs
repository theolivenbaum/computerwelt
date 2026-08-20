using Xunit;

namespace Computerwelt.Emulation.Bash.Tests;

/// <summary>End-to-end behaviour of the shell, exercised the way a script would.</summary>
public sealed class ShellTests
{
    private static Bash NewShell() =>
        Bash.CreateBuilder().WithWorkingDirectory("/").Build();

    private static async Task<string> RunAsync(string script)
    {
        var result = await NewShell().ExecAsync(script);
        return result.Stdout.ToString();
    }

    private static async Task<ExecResult> ExecAsync(string script) => await NewShell().ExecAsync(script);

    [Theory]
    [InlineData("echo hello", "hello\n")]
    [InlineData("echo -n hello", "hello")]
    [InlineData("echo 'a  b'", "a  b\n")]
    [InlineData("echo \"a  b\"", "a  b\n")]
    [InlineData("echo a b c", "a b c\n")]
    [InlineData("echo -e 'a\\tb'", "a\tb\n")]
    [InlineData("echo", "\n")]
    public async Task Echoes(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    [InlineData("x=5; echo $x", "5\n")]
    [InlineData("x=5; echo ${x}", "5\n")]
    [InlineData("echo ${undefined:-fallback}", "fallback\n")]
    [InlineData("x=; echo ${x:-fallback}", "fallback\n")]
    [InlineData("x=; echo ${x-fallback}", "\n")]
    [InlineData("x=abc; echo ${#x}", "3\n")]
    [InlineData("x=abcdef; echo ${x:2:3}", "cde\n")]
    [InlineData("x=abcdef; echo ${x: -2}", "ef\n")]
    [InlineData("x=a.b.c; echo ${x#*.}", "b.c\n")]
    [InlineData("x=a.b.c; echo ${x##*.}", "c\n")]
    [InlineData("x=a.b.c; echo ${x%.*}", "a.b\n")]
    [InlineData("x=a.b.c; echo ${x%%.*}", "a\n")]
    [InlineData("x=aaa; echo ${x/a/b}", "baa\n")]
    [InlineData("x=aaa; echo ${x//a/b}", "bbb\n")]
    [InlineData("x=abc; echo ${x^^}", "ABC\n")]
    [InlineData("x=ABC; echo ${x,,}", "abc\n")]
    [InlineData("x=y; y=hello; echo ${!x}", "hello\n")]
    public async Task Expands_parameters(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    [InlineData("echo $((1+2))", "3\n")]
    [InlineData("echo $((2*3+4))", "10\n")]
    [InlineData("echo $((2+3*4))", "14\n")]
    [InlineData("echo $(((2+3)*4))", "20\n")]
    [InlineData("echo $((10/3))", "3\n")]
    [InlineData("echo $((10%3))", "1\n")]
    [InlineData("echo $((2**10))", "1024\n")]
    [InlineData("echo $((1<2))", "1\n")]
    [InlineData("echo $((1>2))", "0\n")]
    [InlineData("echo $((1?2:3))", "2\n")]
    [InlineData("echo $((0x10))", "16\n")]
    [InlineData("echo $((010))", "8\n")]
    [InlineData("echo $((2#101))", "5\n")]
    [InlineData("x=5; echo $((x+1))", "6\n")]
    [InlineData("x=5; echo $((x++)); echo $x", "5\n6\n")]
    [InlineData("x=5; echo $((++x))", "6\n")]
    [InlineData("echo $((-5))", "-5\n")]
    [InlineData("echo $((!0))", "1\n")]
    public async Task Evaluates_arithmetic(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    [InlineData("echo {a,b,c}", "a b c\n")]
    [InlineData("echo {1..5}", "1 2 3 4 5\n")]
    [InlineData("echo {5..1}", "5 4 3 2 1\n")]
    [InlineData("echo {a..e}", "a b c d e\n")]
    [InlineData("echo x{1,2}y", "x1y x2y\n")]
    [InlineData("echo {1..10..3}", "1 4 7 10\n")]
    [InlineData("echo {01..03}", "01 02 03\n")]
    [InlineData("echo {a}", "{a}\n")]
    public async Task Expands_braces(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    [InlineData("if true; then echo yes; fi", "yes\n")]
    [InlineData("if false; then echo yes; else echo no; fi", "no\n")]
    [InlineData("if false; then echo a; elif true; then echo b; else echo c; fi", "b\n")]
    [InlineData("for i in 1 2 3; do echo $i; done", "1\n2\n3\n")]
    [InlineData("i=0; while [ $i -lt 3 ]; do echo $i; i=$((i+1)); done", "0\n1\n2\n")]
    [InlineData("i=0; until [ $i -ge 2 ]; do echo $i; i=$((i+1)); done", "0\n1\n")]
    [InlineData("for ((i=0;i<3;i++)); do echo $i; done", "0\n1\n2\n")]
    [InlineData("case abc in a*) echo matched;; *) echo no;; esac", "matched\n")]
    [InlineData("for i in 1 2 3; do if [ $i = 2 ]; then break; fi; echo $i; done", "1\n")]
    [InlineData("for i in 1 2 3; do if [ $i = 2 ]; then continue; fi; echo $i; done", "1\n3\n")]
    public async Task Runs_control_flow(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    [InlineData("true && echo yes", "yes\n")]
    [InlineData("false && echo yes", "")]
    [InlineData("false || echo yes", "yes\n")]
    [InlineData("true || echo yes", "")]
    [InlineData("echo a; echo b", "a\nb\n")]
    [InlineData("echo hello | cat", "hello\n")]
    [InlineData("printf 'b\\na\\n' | sort", "a\nb\n")]
    [InlineData("echo hello | wc -c", "6\n")]
    [InlineData("printf 'a\\nb\\nc\\n' | head -2", "a\nb\n")]
    [InlineData("printf 'a\\nb\\nc\\n' | tail -1", "c\n")]
    public async Task Runs_lists_and_pipelines(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    [InlineData("f() { echo hi; }; f", "hi\n")]
    [InlineData("f() { echo $1 $2; }; f a b", "a b\n")]
    [InlineData("f() { echo $#; }; f a b c", "3\n")]
    [InlineData("f() { return 3; }; f; echo $?", "3\n")]
    [InlineData("f() { local x=inner; echo $x; }; x=outer; f; echo $x", "inner\nouter\n")]
    [InlineData("function g { echo g; }; g", "g\n")]
    public async Task Defines_and_calls_functions(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    [InlineData("echo $(echo nested)", "nested\n")]
    [InlineData("echo `echo backtick`", "backtick\n")]
    [InlineData("x=$(echo v); echo $x", "v\n")]
    [InlineData("echo \"$(echo a; echo b)\"", "a\nb\n")]
    public async Task Substitutes_commands(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    [InlineData("[ 1 -eq 1 ]; echo $?", "0\n")]
    [InlineData("[ 1 -eq 2 ]; echo $?", "1\n")]
    [InlineData("[ abc = abc ]; echo $?", "0\n")]
    [InlineData("[ -z '' ]; echo $?", "0\n")]
    [InlineData("[ -n x ]; echo $?", "0\n")]
    [InlineData("[[ abc == a* ]]; echo $?", "0\n")]
    [InlineData("[[ abc != a* ]]; echo $?", "1\n")]
    [InlineData("[[ 1 -lt 2 && 3 -gt 2 ]]; echo $?", "0\n")]
    [InlineData("[[ abc =~ ^a.c$ ]]; echo $?", "0\n")]
    public async Task Evaluates_conditions(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    [InlineData("a=(1 2 3); echo ${a[1]}", "2\n")]
    [InlineData("a=(1 2 3); echo ${a[@]}", "1 2 3\n")]
    [InlineData("a=(1 2 3); echo ${#a[@]}", "3\n")]
    [InlineData("a=(1 2 3); a[1]=x; echo ${a[@]}", "1 x 3\n")]
    [InlineData("declare -A m; m[k]=v; echo ${m[k]}", "v\n")]
    public async Task Handles_arrays(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Fact]
    public async Task Writes_and_reads_files_through_redirection()
    {
        var bash = NewShell();

        await bash.ExecAsync("echo hello > /f.txt");
        var result = await bash.ExecAsync("cat /f.txt");

        Assert.Equal("hello\n", result.Stdout.ToString());

        await bash.ExecAsync("echo world >> /f.txt");
        result = await bash.ExecAsync("cat /f.txt");

        Assert.Equal("hello\nworld\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Reads_a_here_document()
    {
        var result = await ExecAsync("""
            cat <<EOF
            line one
            line two
            EOF
            """);

        Assert.Equal("line one\nline two\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_quoted_here_document_delimiter_suppresses_expansion()
    {
        var result = await ExecAsync("""
            x=value
            cat <<'EOF'
            $x
            EOF
            """);

        Assert.Equal("$x\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task An_unquoted_here_document_delimiter_expands()
    {
        var result = await ExecAsync("""
            x=value
            cat <<EOF
            $x
            EOF
            """);

        Assert.Equal("value\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Discards_output_redirected_to_dev_null()
    {
        var result = await ExecAsync("echo noise > /dev/null; echo kept");
        Assert.Equal("kept\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Redirects_stderr_into_stdout()
    {
        var bash = NewShell();
        var result = await bash.ExecAsync("nosuchcommand 2>&1");

        Assert.Contains("command not found", result.Stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(result.Stderr.ToString());
    }

    [Fact]
    public async Task An_unknown_command_exits_127()
    {
        var result = await ExecAsync("definitely_not_a_command");

        Assert.Equal(ExitCodes.NotFound, result.ExitCode);
        Assert.Contains("command not found", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Globs_against_the_virtual_filesystem()
    {
        var bash = NewShell();
        await bash.ExecAsync("mkdir -p /d && touch /d/a.txt /d/b.txt /d/c.log");

        var result = await bash.ExecAsync("echo /d/*.txt");

        Assert.Equal("/d/a.txt /d/b.txt\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_glob_with_no_match_stays_literal_by_default()
    {
        var result = await ExecAsync("echo /nothing/*.zzz");
        Assert.Equal("/nothing/*.zzz\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_subshell_does_not_leak_state()
    {
        var result = await ExecAsync("x=outer; (x=inner; echo $x); echo $x");
        Assert.Equal("inner\nouter\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Variables_persist_between_calls()
    {
        var bash = NewShell();

        await bash.ExecAsync("x=persisted");
        var result = await bash.ExecAsync("echo $x");

        Assert.Equal("persisted\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Two_sessions_are_isolated()
    {
        var a = NewShell();
        var b = NewShell();

        await a.ExecAsync("secret=classified; echo data > /a.txt");

        var variable = await b.ExecAsync("echo ${secret:-none}");
        var file = await b.ExecAsync("cat /a.txt");

        Assert.Equal("none\n", variable.Stdout.ToString());
        Assert.NotEqual(0, file.ExitCode);
    }

    [Fact]
    public async Task Word_splitting_respects_quoting()
    {
        var result = await ExecAsync("""
            x="a b c"
            f() { echo $#; }
            f $x
            f "$x"
            """);

        Assert.Equal("3\n1\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Set_e_stops_on_a_failure()
    {
        var result = await ExecAsync("set -e; false; echo unreachable");

        Assert.Equal("", result.Stdout.ToString());
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Set_e_does_not_fire_for_a_tested_command()
    {
        var result = await ExecAsync("set -e; false || true; echo reached");
        Assert.Equal("reached\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Pipefail_reports_the_failing_stage()
    {
        var passing = await ExecAsync("false | true; echo $?");
        var failing = await ExecAsync("set -o pipefail; false | true; echo $?");

        Assert.Equal("0\n", passing.Stdout.ToString());
        Assert.Equal("1\n", failing.Stdout.ToString());
    }

    [Theory]
    [InlineData("head -2", "a\nb\n")]
    [InlineData("head -10", "a\nb\nc\n")]
    [InlineData("head -30", "a\nb\nc\n")]
    [InlineData("tail -2", "b\nc\n")]
    [InlineData("tail -20", "a\nb\nc\n")]
    public async Task A_multi_digit_count_option_is_one_option(string command, string expected)
    {
        // `head -30` is a count, not the cluster `-3 -0`, which would take the last digit
        // as the count and print nothing.
        var result = await ExecAsync($"printf 'a\\nb\\nc\\n' | {command}");

        Assert.Equal(expected, result.Stdout.ToString());
    }
}
