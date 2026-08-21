using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// Making changes: writing files, rewriting them in place, and moving them around.
/// </summary>
public sealed class EditingTests
{
    [Fact]
    public async Task Heredoc_writes_a_file_verbatim()
    {
        var session = await AgentSession.NewAsync();

        await session.RunAsync("""
            cat > /work/new.cs <<'EOF'
            var x = "$notExpanded";
            var y = `backtick`;
            EOF
            """);

        // The quoted delimiter is what makes a heredoc safe for source code: nothing
        // inside it is expanded, so `$` and backticks survive.
        Assert.Equal("var x = \"$notExpanded\";\nvar y = `backtick`;\n", await session.ReadAsync("/work/new.cs"));
    }

    [Fact]
    public async Task Unquoted_heredoc_does_expand()
    {
        var session = await AgentSession.NewAsync();

        await session.RunAsync("""
            name=widget
            cat > /work/expanded.txt <<EOF
            the $name
            EOF
            """);

        Assert.Equal("the widget\n", await session.ReadAsync("/work/expanded.txt"));
    }

    [Fact]
    public async Task Redirection_truncates_and_appends()
    {
        var session = await AgentSession.NewAsync();

        await session.RunAsync("echo one > /work/log.txt; echo two >> /work/log.txt; echo three > /work/log.txt");

        Assert.Equal("three\n", await session.ReadAsync("/work/log.txt"));
    }

    [Fact]
    public async Task Sed_in_place_rewrites_a_file()
    {
        var session = await AgentSession.NewAsync();

        await session.RunAsync("sed -i 's/widget/sprocket/' src/Widget.cs");

        Assert.Contains("sprocket {Count}", await session.ReadAsync("/work/src/Widget.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sed_in_place_over_several_files()
    {
        var session = await AgentSession.NewAsync();

        await session.RunAsync("find src tests -name '*.cs' | xargs sed -i 's/Count/Total/g'");

        Assert.Contains("public int Total", await session.ReadAsync("/work/src/Widget.cs"), StringComparison.Ordinal);
        Assert.Contains("public int Total", await session.ReadAsync("/work/src/Gadget.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mkdir_p_is_idempotent()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("mkdir -p a/b/c && mkdir -p a/b/c && echo ok");

        Assert.Equal("ok\n", result.Stdout.ToString());
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Copy_move_and_remove()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            cp src/Widget.cs /work/backup.cs
            mv /work/backup.cs /work/kept.cs
            rm -f /work/nothing-here.cs
            ls /work/*.cs | sed 's|.*/||'
            """);

        Assert.Equal("kept.cs\n", output);
    }

    [Fact]
    public async Task Removing_a_missing_file_without_f_is_an_error()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("rm /work/absent.txt");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No such file", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recursive_copy_and_delete_of_a_directory()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            cp -r src /work/src-copy
            find /work/src-copy -type f | sort | sed 's|.*/||'
            rm -rf /work/src-copy
            test -d /work/src-copy && echo still-there || echo gone
            """);

        Assert.Equal("Gadget.cs\nSample.csproj\nWidget.cs\ngone\n", output);
    }

    [Fact]
    public async Task Printf_writes_without_a_trailing_newline()
    {
        var session = await AgentSession.NewAsync();

        await session.RunAsync("printf '%s' nonewline > /work/bare.txt");

        Assert.Equal("nonewline", await session.ReadAsync("/work/bare.txt"));
    }

    [Fact]
    public async Task Diff_reports_what_an_edit_changed()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            cp src/Widget.cs /work/before.cs
            sed -i 's/Count/Total/g' src/Widget.cs
            diff /work/before.cs src/Widget.cs | grep -c '^[<>]'
            """);

        Assert.Equal("4\n", output);
    }
}
