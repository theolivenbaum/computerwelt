using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>The <c>io</c> module's in-memory streams.</summary>
public sealed class MemoryStreamTests
{
    private static string Run(string source)
    {
        var result = new PythonRunner().Run(source);
        Assert.Null(result.Traceback);
        return result.Stdout;
    }

    [Fact]
    public void StringIO_accumulates_writes()
    {
        Assert.Equal("one two\n", Run("""
            import io
            buffer = io.StringIO()
            buffer.write('one')
            buffer.write(' two')
            print(buffer.getvalue())
            """));
    }

    [Fact]
    public void StringIO_starts_from_a_value_and_reads_back()
    {
        Assert.Equal("hello\nhel\nlo\n", Run("""
            import io
            buffer = io.StringIO('hello')
            print(buffer.getvalue())
            print(buffer.read(3))
            print(buffer.read())
            """));
    }

    [Fact]
    public void StringIO_reads_lines()
    {
        Assert.Equal("['a\\n', 'b\\n']\n['A', 'B']\n", Run("""
            import io
            print(io.StringIO('a\nb\n').readlines())
            print([line.strip().upper() for line in io.StringIO('a\nb\n')])
            """));
    }

    [Fact]
    public void Seeking_rewinds_and_overwrites()
    {
        Assert.Equal("Xbcd\n", Run("""
            import io
            buffer = io.StringIO('abcd')
            buffer.seek(0)
            buffer.write('X')
            print(buffer.getvalue())
            """));
    }

    [Fact]
    public void BytesIO_works_in_bytes()
    {
        Assert.Equal("b'ab'\n2\n", Run("""
            import io
            buffer = io.BytesIO()
            print(buffer.write(b'ab') and b'' or buffer.getvalue())
            print(buffer.tell())
            """));
    }

    [Fact]
    public void A_stream_refuses_the_other_types_data()
    {
        var text = new PythonRunner().Run("import io\nio.StringIO().write(b'x')\n");
        Assert.Equal("TypeError", text.ExceptionType);

        var binary = new PythonRunner().Run("import io\nio.BytesIO().write('x')\n");
        Assert.Equal("TypeError", binary.ExceptionType);
    }

    [Fact]
    public void A_closed_stream_refuses_everything()
    {
        var result = new PythonRunner().Run("""
            import io
            buffer = io.StringIO('x')
            buffer.close()
            print(buffer.closed)
            buffer.read()
            """);

        Assert.Equal("ValueError", result.ExceptionType);
        Assert.Equal("True\n", result.Stdout);
    }

    [Fact]
    public void A_stream_is_a_context_manager()
    {
        Assert.Equal("done\nTrue\n", Run("""
            import io
            with io.StringIO() as buffer:
                buffer.write('done')
                print(buffer.getvalue())
            print(buffer.closed)
            """));
    }
}
