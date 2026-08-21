using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// Codec behaviour the upstream fixture corpus does not reach.
/// </summary>
/// <remarks>
/// The corpus covers utf-8, ascii, latin-1 and the utf-16/32 families; <c>utf-8-sig</c> is
/// absent from it but is what a script reads a BOM-prefixed file with, so it is pinned
/// here.
/// </remarks>
public sealed class CodecTests
{
    private static string Run(string source)
    {
        var result = new PythonRunner().Run(source);

        Assert.True(result.Succeeded, result.Exception?.Message ?? result.SyntaxError?.Message);
        return result.Stdout;
    }

    [Fact]
    public void Utf8_sig_encodes_with_a_signature()
    {
        Assert.Equal("b'\\xef\\xbb\\xbfhi'\n", Run("print(repr('hi'.encode('utf-8-sig')))"));
    }

    [Fact]
    public void Utf8_sig_strips_a_leading_signature()
    {
        Assert.Equal("'hi'\n", Run("print(repr(b'\\xef\\xbb\\xbfhi'.decode('utf-8-sig')))"));
    }

    [Fact]
    public void Utf8_sig_leaves_unsigned_input_alone()
    {
        Assert.Equal("'hi'\n", Run("print(repr(b'hi'.decode('utf-8-sig')))"));
    }

    [Fact]
    public void Utf8_sig_strips_only_the_first_signature()
    {
        // A second signature is content — U+FEFF, a zero-width no-break space.
        Assert.Equal("'\\ufeffhi'\n", Run(
            "print(repr(b'\\xef\\xbb\\xbf\\xef\\xbb\\xbfhi'.decode('utf-8-sig')))"));
    }

    [Fact]
    public void Utf8_sig_round_trips()
    {
        Assert.Equal("True\n", Run("print('héllo'.encode('utf-8-sig').decode('utf-8-sig') == 'héllo')"));
    }
}
