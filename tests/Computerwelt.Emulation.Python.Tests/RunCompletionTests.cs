using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// A host library being told the run is over.
/// </summary>
/// <remarks>
/// A library is built per run and, without this, is never told the run ended — which is
/// fine for one that only computes and wrong for one holding something that has to be
/// handed back. The point of the hook is the cases the program does not cover: an uncaught
/// exception, a limit reached, or a program that simply did not bother.
/// </remarks>
public sealed class RunCompletionTests
{
    /// <summary>A library that records when its run finished, as a resource holder would.</summary>
    private static PythonLibrary Leases(List<string> log) =>
        PythonLibrary.FromFactory("leases", context =>
        {
            context.Machine.WhenRunCompleted(() => log.Add("released"));

            return context.Module().Add("take", _ =>
            {
                log.Add("taken");
                return PyNone.Instance;
            });
        });

    private static (RunResult Result, List<string> Log) Run(string source, ExecutionLimits? limits = null)
    {
        var log = new List<string>();
        var runner = new PythonRunner(limits);

        runner.Libraries.Add(Leases(log));

        return (runner.Run(source), log);
    }

    [Fact]
    public void A_completion_runs_after_the_program_ends()
    {
        var (result, log) = Run("""
            import leases
            leases.take()
            print('done')
            """);

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal("done\n", result.Stdout);
        Assert.Equal(["taken", "released"], log);
    }

    [Fact]
    public void A_completion_runs_after_an_uncaught_exception()
    {
        var (result, log) = Run("""
            import leases
            leases.take()
            raise ValueError('gave up')
            """);

        Assert.False(result.Succeeded);
        Assert.Equal("ValueError", result.ExceptionType);
        Assert.Equal(["taken", "released"], log);
    }

    [Fact]
    public void A_completion_runs_after_a_limit_is_reached()
    {
        // The case with no program left to run anything: the run is stopped from outside.
        var (result, log) = Run(
            """
            import leases
            leases.take()

            while True:
                pass
            """,
            new ExecutionLimits { MaxInstructions = 100_000 });

        Assert.False(result.Succeeded);
        Assert.Equal(["taken", "released"], log);
    }

    [Fact]
    public void A_completion_runs_after_sys_exit()
    {
        var (result, log) = Run("""
            import leases
            import sys

            leases.take()
            sys.exit(3)
            """);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(["taken", "released"], log);
    }

    [Fact]
    public void A_library_the_program_never_imported_registers_nothing()
    {
        // A library is built on first import, so one nothing imports costs nothing — and
        // that has to include its completion.
        var (result, log) = Run("print('nothing imported')");

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Empty(log);
    }

    [Fact]
    public void Each_run_gets_its_own_completion()
    {
        var log = new List<string>();
        var runner = new PythonRunner();

        runner.Libraries.Add(Leases(log));

        runner.Run("import leases\nleases.take()");
        runner.Run("import leases\nleases.take()");

        Assert.Equal(["taken", "released", "taken", "released"], log);
    }

    [Fact]
    public void A_completion_that_throws_does_not_change_the_result_or_stop_the_others()
    {
        var log = new List<string>();
        var runner = new PythonRunner();

        runner.Libraries.Add(PythonLibrary.FromFactory("first", context =>
        {
            context.Machine.WhenRunCompleted(() => throw new InvalidOperationException("cleanup failed"));
            return context.Module();
        }));

        runner.Libraries.Add(PythonLibrary.FromFactory("second", context =>
        {
            context.Machine.WhenRunCompleted(() => log.Add("second released"));
            return context.Module();
        }));

        var result = runner.Run("""
            import first
            import second
            print('done')
            """);

        // The program had already finished and its result was already decided, so one
        // library's failure to tidy up is not the next one's problem and not the program's.
        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal("done\n", result.Stdout);
        Assert.Equal(["second released"], log);
    }
}
