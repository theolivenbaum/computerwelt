using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// What an agent's script must not be able to do, however it asks.
/// </summary>
/// <remarks>
/// These are the tests that would fail if the sandbox were quietly weakened, so they are
/// written from the attacker's side: each one is a plausible line in an otherwise ordinary
/// script that would reach the host if anything here regressed.
/// </remarks>
public sealed class SandboxBoundaryTests
{
    public static TheoryData<string> ForbiddenModules =>
    [
        "subprocess",
        "socket",
        "urllib",
        "urllib.request",
        "http",
        "ctypes",
        "pickle",
        "importlib",
        "multiprocessing",
        "threading",
        "signal",
        "resource",
        "platform",
        "sysconfig",
        "webbrowser",
    ];

    [Theory]
    [MemberData(nameof(ForbiddenModules))]
    public async Task A_module_that_would_reach_the_host_is_not_importable(string module)
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync($"python3 -c 'import {module}'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No module named", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_is_no_way_to_start_a_process_from_python()
    {
        var session = await AgentSession.NewAsync();

        foreach (var attempt in new[] { "system", "popen", "execv", "execvp", "spawnv", "fork", "posix_spawn" })
        {
            var result = await session.RunAsync($"python3 -c 'import os; os.{attempt}'");

            // Not merely refused at the call — the name is not on the module at all, so
            // there is nothing to reach even by getattr.
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                $"AttributeError: 'module' object has no attribute '{attempt}'",
                result.Stderr.ToString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Eval_and_exec_are_not_available_to_build_code_at_runtime()
    {
        var session = await AgentSession.NewAsync();

        foreach (var attempt in new[] { "eval('1+1')", "exec('x=1')", "compile('1', '<s>', 'eval')", "__import__('os')" })
        {
            var result = await session.RunAsync($"python3 -c \"{attempt}\"");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("NameError", result.Stderr.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_sandbox_filesystem_is_the_only_one_there_is()
    {
        var session = await AgentSession.NewAsync();

        // A host path that certainly exists outside must not exist inside. If this ever
        // passes, the virtual filesystem has been bypassed rather than extended.
        var output = await session.OutAsync("""
            python3 - <<'PY'
            import os
            print(os.path.exists('/etc/passwd'), os.path.exists('/proc/self/environ'))
            PY
            """);

        Assert.Equal("False False\n", output);
    }

    [Fact]
    public async Task The_shell_cannot_reach_the_host_filesystem_either()
    {
        var session = await AgentSession.NewAsync();

        Assert.Equal("absent\n", await session.OutAsync("test -e /etc/passwd && echo present || echo absent"));
    }

    [Fact]
    public async Task The_shell_has_no_way_to_spawn_a_real_command()
    {
        var session = await AgentSession.NewAsync();

        foreach (var attempt in new[] { "/bin/sh -c id", "/usr/bin/env id", "dotnet --version", "curl https://example.com" })
        {
            var result = await session.RunAsync($"{attempt} 2>/dev/null");

            // 127 is "command not found" — the shell looked for a builtin, found none, and
            // did not fall through to anything on the host.
            Assert.Equal(127, result.ExitCode);
        }
    }

    [Fact]
    public async Task Network_access_is_refused_by_default()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("curl -s https://example.com");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Stdout.ToString());
    }

    [Fact]
    public async Task Two_sessions_share_nothing()
    {
        var first = await AgentSession.NewAsync();
        var second = await AgentSession.NewAsync();

        await first.RunAsync("echo leaked > /work/secret.txt; export SECRET=value");

        Assert.Equal("absent\n", await second.OutAsync("test -f /work/secret.txt && echo present || echo absent"));
        Assert.Equal("\n", await second.OutAsync("echo \"$SECRET\""));
    }

    [Fact]
    public async Task A_runaway_loop_is_stopped_rather_than_hanging()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("while true; do :; done");

        // A limit that only advised would make every one of these tests a hang rather than
        // a failure.
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task A_runaway_python_loop_is_stopped_too()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("python3 -c 'x = 0\nwhile True:\n    x += 1'");

        Assert.NotEqual(0, result.ExitCode);
    }
}
