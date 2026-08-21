using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// <c>input</c> and <c>sys.stdin</c>, which exist only when the host supplies input.
/// </summary>
public sealed class StandardInputTests
{
    private static PythonRunner Runner(string? standardInput)
    {
        var runner = new PythonRunner();
        runner.StandardInput = standardInput;
        return runner;
    }

    [Fact]
    public void Input_reads_a_line_without_its_terminator()
    {
        var result = Runner("first\nsecond\n").Run("print(input())\nprint(input())\n");

        Assert.Null(result.Traceback);
        Assert.Equal("first\nsecond\n", result.Stdout);
    }

    [Fact]
    public void Input_writes_its_prompt_to_standard_output()
    {
        var result = Runner("yes\n").Run("answer = input('proceed? ')\nprint(answer)\n");

        Assert.Equal("proceed? yes\n", result.Stdout);
    }

    [Fact]
    public void Input_past_the_end_raises_eof_error()
    {
        var result = Runner("only\n").Run("input()\ninput()\n");

        Assert.False(result.Succeeded);
        Assert.Equal("EOFError", result.ExceptionType);
    }

    [Fact]
    public void Sys_stdin_reads_the_same_stream_input_does()
    {
        var result = Runner("a\nb\nc\n").Run("""
            import sys
            print(input())
            print(sys.stdin.read())
            """);

        // One stream, one position: what `input` consumed is not handed out again.
        Assert.Equal("a\nb\nc\n\n", result.Stdout);
    }

    [Fact]
    public void Sys_stdin_iterates_line_by_line()
    {
        var result = Runner("one\ntwo\n").Run("""
            import sys
            for line in sys.stdin:
                print(line.strip().upper())
            """);

        Assert.Equal("ONE\nTWO\n", result.Stdout);
    }

    [Fact]
    public void With_no_input_neither_name_exists()
    {
        var withoutInput = Runner(null).Run("input()");

        Assert.False(withoutInput.Succeeded);
        Assert.Equal("NameError", withoutInput.ExceptionType);

        var noStdin = Runner(null).Run("import sys\nsys.stdin\n");

        // Absent rather than an empty stream, so a program cannot mistake "nothing was
        // piped in" for "the input was empty".
        Assert.False(noStdin.Succeeded);
        Assert.Equal("AttributeError", noStdin.ExceptionType);
    }
}
