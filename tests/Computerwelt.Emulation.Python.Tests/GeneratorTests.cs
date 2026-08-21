using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// Generator functions, which the fixture corpus barely reaches.
/// </summary>
/// <remarks>
/// Upstream's corpus iterates plenty of built-in iterables but defines almost no generator
/// functions of its own, so a generator body with more than one <c>yield</c> in it went
/// untested — and, for a while, took the host process down with a stack underflow rather
/// than raising anything a program could see. These cover the shapes the corpus does not.
/// </remarks>
public sealed class GeneratorTests
{
    private static string Run(string source)
    {
        var result = new PythonRunner().Run(source);
        Assert.Null(result.Traceback);
        return result.Stdout;
    }

    [Fact]
    public void A_generator_with_several_yields_runs_to_the_end()
    {
        Assert.Equal("[1, 2, 3]\n", Run("""
            def three():
                yield 1
                yield 2
                yield 3

            print(list(three()))
            """));
    }

    [Fact]
    public void A_yield_inside_a_loop_produces_every_value()
    {
        Assert.Equal("[0, 1, 2, 3, 4]\n", Run("""
            def counted(n):
                for i in range(n):
                    yield i

            print(list(counted(5)))
            """));
    }

    [Fact]
    public void A_generator_is_consumed_once()
    {
        Assert.Equal("[1, 2]\n[]\n", Run("""
            def pair():
                yield 1
                yield 2

            g = pair()
            print(list(g))
            print(list(g))
            """));
    }

    [Fact]
    public void Yield_from_delegates_to_another_generator()
    {
        Assert.Equal("[1, 2, 3]\n", Run("""
            def inner():
                yield 1
                yield 2

            def outer():
                yield from inner()
                yield 3

            print(list(outer()))
            """));
    }

    [Fact]
    public void Yield_from_recurses()
    {
        // The shape a tree walk takes when `os.walk` is not available.
        Assert.Equal("[0, 1, 2, 3]\n", Run("""
            def down(n):
                if n:
                    yield from down(n - 1)
                yield n

            print(list(down(3)))
            """));
    }

    [Fact]
    public void A_yield_expressions_value_is_none()
    {
        Assert.Equal("[None, None]\n", Run("""
            seen = []

            def watched():
                seen.append((yield 1))
                seen.append((yield 2))

            list(watched())
            print(seen)
            """));
    }

    [Fact]
    public void A_generator_is_lazy()
    {
        Assert.Equal("made\nfirst\n1\n", Run("""
            def noisy():
                print('first')
                yield 1
                print('second')
                yield 2

            g = noisy()
            print('made')
            print(next(g))
            """));
    }

    [Fact]
    public void A_generator_expression_over_a_generator_function()
    {
        Assert.Equal("6\n", Run("""
            def counted(n):
                for i in range(n):
                    yield i

            print(sum(x for x in counted(4)))
            """));
    }
}
