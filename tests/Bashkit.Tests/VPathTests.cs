using Xunit;

namespace Bashkit.Tests;

public sealed class VPathTests
{
    [Theory]
    [InlineData("/a/b/c", "/a/b/c")]
    [InlineData("/a//b///c", "/a/b/c")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/b/..", "/a")]
    [InlineData("/a/b/../..", "/")]
    [InlineData("/..", "/")]
    [InlineData("/../..", "/")]
    [InlineData("a/b/../c", "a/c")]
    [InlineData("../a", "../a")]
    [InlineData("./a", "a")]
    [InlineData("/", "/")]
    [InlineData(".", ".")]
    [InlineData("/a/b/", "/a/b")]
    public void Normalizes(string input, string expected) =>
        Assert.Equal(expected, VPath.Parse(input).Value);

    [Fact]
    public void Escaping_the_root_with_dotdot_is_impossible()
    {
        // The sandbox depends on this: no sequence of `..` may produce a path above `/`.
        Assert.Equal("/", VPath.Parse("/../../../../etc").Parent?.Value ?? "/");
        Assert.Equal("/etc", VPath.Parse("/../../../../etc").Value);
    }

    [Fact]
    public void Joining_an_absolute_path_replaces_the_base() =>
        Assert.Equal("/etc/passwd", VPath.Parse("/home/user").Join("/etc/passwd").Value);

    [Fact]
    public void Windows_style_input_is_not_treated_as_absolute()
    {
        // On a Windows host, System.IO.Path would treat this as a drive-rooted path and
        // escape the sandbox. VPath must see it as an ordinary relative name.
        var joined = VPath.Parse("/home/user").Join(@"C:\Windows");
        Assert.Equal(@"/home/user/C:\Windows", joined.Value);
    }

    [Theory]
    [InlineData("/a/b/c.txt", "c.txt")]
    [InlineData("/a/b/", "b")]
    [InlineData("/", "")]
    [InlineData("file", "file")]
    public void Extracts_the_file_name(string input, string expected) =>
        Assert.Equal(expected, VPath.Parse(input).FileName);

    [Theory]
    [InlineData("/a/b/c", "/a/b")]
    [InlineData("/a", "/")]
    [InlineData("/", null)]
    public void Extracts_the_parent(string input, string? expected) =>
        Assert.Equal(expected, VPath.Parse(input).Parent?.Value);

    [Theory]
    [InlineData("/a/b/c", "/a", true)]
    [InlineData("/a/b/c", "/a/b/c", true)]
    [InlineData("/ab", "/a", false)]
    [InlineData("/a", "/a/b", false)]
    [InlineData("/anything", "/", true)]
    public void Detects_containment(string path, string ancestor, bool expected) =>
        Assert.Equal(expected, VPath.Parse(path).IsUnder(VPath.Parse(ancestor)));

    [Fact]
    public void Resolves_relative_paths_against_a_working_directory()
    {
        var cwd = VPath.Parse("/home/user");
        Assert.Equal("/home/user/file", VPath.Resolve(cwd, "file").Value);
        Assert.Equal("/home", VPath.Resolve(cwd, "..").Value);
        Assert.Equal("/etc", VPath.Resolve(cwd, "/etc").Value);
    }
}
