using Xunit;

namespace Bashkit.SpecTests;

public sealed class SpecFileParserTests
{
    [Fact]
    public void Parses_a_simple_case()
    {
        var cases = SpecFileParser.Parse("x.test.sh", """
            ### greet
            # says hello
            echo hello
            ### expect
            hello
            ### end
            """);

        var only = Assert.Single(cases);
        Assert.Equal("greet", only.Name);
        Assert.Equal("says hello", only.Description);
        Assert.Equal("echo hello", only.Script);
        Assert.Equal("hello\n", only.ExpectedStdout);
        Assert.Null(only.ExpectedExitCode);
        Assert.False(only.Skip);
    }

    [Fact]
    public void Parses_directives()
    {
        var cases = SpecFileParser.Parse("x.test.sh", """
            ### failing
            false
            ### exit_code: 1
            ### expect
            ### end

            ### skipped
            ### skip: not implemented
            true
            ### expect
            ### end
            """);

        Assert.Equal(2, cases.Count);
        Assert.Equal(1, cases[0].ExpectedExitCode);
        Assert.Equal(string.Empty, cases[0].ExpectedStdout);
        Assert.True(cases[1].Skip);
        Assert.Equal("not implemented", cases[1].SkipReason);
    }

    [Fact]
    public void Parses_multiple_cases_in_one_file()
    {
        var cases = SpecFileParser.Parse("x.test.sh", """
            ### one
            echo 1
            ### expect
            1
            ### end

            ### two
            echo 2
            ### expect
            2
            ### end
            """);

        Assert.Equal(["one", "two"], cases.Select(static c => c.Name));
    }

    [Fact]
    public void Real_corpus_is_present_and_parses()
    {
        var files = SpecSuite.LoadAll();

        Assert.NotEmpty(files);
        Assert.True(files.Values.Sum(static c => c.Count) > 1000,
            "the conformance corpus should hold thousands of cases");
    }
}
