using Bashkit;
using Xunit;

namespace Computerwelt.Tests;

/// <summary>
/// The two interpreters over one sandbox.
/// </summary>
/// <remarks>
/// These are the tests that justify porting both halves into one library: a shell script
/// and a Python script have to see the same filesystem, and neither may reach the host.
/// </remarks>
public sealed class IntegrationTests
{
    private static Bash NewSession() =>
        Bash.CreateBuilder().WithWorkingDirectory("/").WithPython().Build();

    [Fact]
    public async Task Runs_python_from_the_shell()
    {
        var result = await NewSession().ExecAsync("python -c 'print(1 + 1)'");

        Assert.Equal("2\n", result.Stdout.ToString());
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Python_reads_a_file_the_shell_wrote()
    {
        var bash = NewSession();

        var result = await bash.ExecAsync("""
            echo hello > /note.txt
            python -c "print(open('/note.txt').read().strip().upper())"
            """);

        Assert.Equal("HELLO\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Shell_reads_a_file_python_wrote()
    {
        var bash = NewSession();

        var result = await bash.ExecAsync("""
            python -c "
            f = open('/out.txt', 'w')
            f.write('from python\n')
            f.close()
            "
            cat /out.txt
            """);

        Assert.Equal("from python\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Python_sees_the_shells_working_directory()
    {
        var bash = NewSession();

        var result = await bash.ExecAsync("""
            mkdir -p /work
            cd /work
            python -c "import os; print(os.getcwd())"
            """);

        Assert.Equal("/work\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Python_lists_the_shared_directory()
    {
        var bash = NewSession();

        var result = await bash.ExecAsync("""
            mkdir -p /d && touch /d/a /d/b
            python -c "import os; print(sorted(os.listdir('/d')))"
            """);

        Assert.Equal("['a', 'b']\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Runs_a_script_file_from_the_virtual_filesystem()
    {
        var bash = NewSession();

        var result = await bash.ExecAsync("""
            cat > /s.py <<'EOF'
            total = sum(x * x for x in range(5))
            print(f"total={total}")
            EOF
            python /s.py
            """);

        Assert.Equal("total=30\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_python_exception_becomes_a_non_zero_exit_and_a_traceback()
    {
        var bash = NewSession();
        var result = await bash.ExecAsync("python -c 'raise ValueError(\"boom\")'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ValueError: boom", result.Stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Traceback", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Python_output_flows_through_a_shell_pipeline()
    {
        var bash = NewSession();

        var result = await bash.ExecAsync("""
            python -c "
            for i in range(3):
                print(3 - i)
            " | sort
            """);

        Assert.Equal("1\n2\n3\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Two_sessions_share_no_filesystem()
    {
        var a = NewSession();
        var b = NewSession();

        await a.ExecAsync("python -c \"open('/secret.txt', 'w').write('x')\"");

        var result = await b.ExecAsync("python -c \"import os; print(os.path.exists('/secret.txt'))\"");

        Assert.Equal("False\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Python_cannot_reach_the_host_filesystem()
    {
        var bash = NewSession();

        // /etc/passwd exists on the host but must not exist in the sandbox.
        var result = await bash.ExecAsync("python -c \"import os; print(os.path.exists('/etc/passwd'))\"");

        Assert.Equal("False\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Python_cannot_import_a_module_outside_the_permitted_set()
    {
        var bash = NewSession();
        var result = await bash.ExecAsync("python -c 'import subprocess'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ModuleNotFoundError", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_script_reports_the_conventional_error()
    {
        var bash = NewSession();
        var result = await bash.ExecAsync("python /nope.py");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("can't open file", result.Stderr.ToString(), StringComparison.Ordinal);
    }
}
