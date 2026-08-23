using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Bash.Builtins;
using Computerwelt.Emulation.Python;
using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Tests;

/// <summary>
/// Extending both halves of one session.
/// </summary>
/// <remarks>
/// The shell and the Python command are extended by different APIs, and this is where the
/// two meet: a host command and a host library over the same virtual filesystem, in one
/// script.
/// </remarks>
public sealed class ExtensibilityTests
{
    /// <summary>A Python library written in Python, shipped by the host.</summary>
    private static PythonLibrary Report() => PythonLibrary.FromSource("report", """
        _lines = []

        def add(line):
            _lines.append(line)

        def render():
            return '\n'.join(f'- {line}' for line in _lines)
        """);

    /// <summary>A shell command that reads the same filesystem Python writes to.</summary>
    private sealed class LineCount : IBuiltin
    {
        public string Name => "linecount";

        public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
        {
            var files = await context.ReadOperandsAsync(context.Arguments, cancellationToken);
            var total = files.Sum(file => file.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

            return ExecResult.Ok($"{total}\n");
        }
    }

    private static Bash NewSession(PythonOptions? options = null) =>
        Bash.CreateBuilder()
            .WithWorkingDirectory("/")
            .WithBuiltin(new LineCount())
            .WithPython(options)
            .Build();

    [Fact]
    public async Task Python_imports_a_library_the_host_registered()
    {
        var bash = NewSession(new PythonOptions { Libraries = [Report()] });

        var result = await bash.ExecAsync("""
            python -c "
            import report
            report.add('first')
            report.add('second')
            print(report.render())
            "
            """);

        Assert.Equal("- first\n- second\n", result.Stdout.ToString());
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_host_command_and_a_host_library_share_the_filesystem()
    {
        var bash = NewSession(new PythonOptions { Libraries = [Report()] });

        var result = await bash.ExecAsync("""
            python - <<'EOF'
            import report
            report.add('alpha')
            report.add('beta')
            open('/report.md', 'w').write(report.render() + '\n')
            EOF
            linecount /report.md
            """);

        Assert.Equal("2\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_library_holds_no_state_between_two_python_commands()
    {
        // Each invocation of `python` is its own run, so a library's module state does not
        // survive it. A script that wants continuity writes to the filesystem, as it would
        // with a real interpreter.
        var bash = NewSession(new PythonOptions { Libraries = [Report()] });

        var result = await bash.ExecAsync("""
            python -c "
            import report
            report.add('gone')
            "
            python -c "
            import report
            print(repr(report.render()))
            "
            """);

        Assert.Equal("''\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_host_library_reaches_the_shell_s_filesystem()
    {
        var options = new PythonOptions
        {
            Libraries =
            [
                PythonLibrary.FromSource("head", """
                    def first_line(path):
                        with open(path) as handle:
                            return handle.readline().rstrip('\n')
                    """),
            ],
        };

        var bash = NewSession(options);

        var result = await bash.ExecAsync("""
            printf 'one\ntwo\n' > /data.txt
            python -c "
            import head
            print(head.first_line('/data.txt'))
            "
            """);

        Assert.Equal("one\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_host_function_reads_a_file_the_shell_wrote()
    {
        var options = new PythonOptions
        {
            HostFunctions = new Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>>
            {
                // Custom functionality implemented in C#, over the shell's own filesystem.
                ["word_count"] = (context, arguments) => new PyInt(
                    context.ReadText(arguments[0].Display())
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length),
            },
        };

        var bash = NewSession(options);

        var result = await bash.ExecAsync("""
            printf 'the quick brown fox\njumps over\n' > /prose.txt
            python -c "print(word_count('/prose.txt'))"
            """);

        Assert.Equal("6\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_host_function_follows_the_shell_s_working_directory()
    {
        var options = new PythonOptions
        {
            HostFunctions = new Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>>
            {
                ["cwd"] = (context, _) => new PyStr(context.WorkingDirectory),
            },
        };

        var bash = NewSession(options);

        // The context reads the environment rather than a copy of it, so a `cd` earlier in
        // the script is where host code finds itself too.
        var result = await bash.ExecAsync("""
            mkdir -p /srv/app
            cd /srv/app
            python -c "print(cwd())"
            """);

        Assert.Equal("/srv/app\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_host_function_sees_what_the_script_exported()
    {
        var options = new PythonOptions
        {
            HostFunctions = new Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>>
            {
                ["tenant"] = (context, _) => new PyStr(context.Environment.GetValueOrDefault("TENANT", "none")),
            },
        };

        var bash = NewSession(options);

        var result = await bash.ExecAsync("""
            export TENANT=acme
            python -c "print(tenant())"
            """);

        Assert.Equal("acme\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_csharp_library_and_the_shell_pass_work_to_each_other()
    {
        // The round trip a host would actually build: a shell command writes, C# transforms,
        // the shell reads the result — all inside one sandbox.
        var options = new PythonOptions
        {
            Libraries =
            [
                PythonLibrary.FromFunctions("csv", new Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>>
                {
                    ["to_markdown"] = (context, arguments) =>
                    {
                        var rows = context.ReadText(arguments[0].Display())
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => "| " + string.Join(" | ", line.Split(',')) + " |");

                        context.WriteText(arguments[1].Display(), string.Join('\n', rows) + "\n");
                        return PyNone.Instance;
                    },
                }),
            ],
        };

        var bash = NewSession(options);

        var result = await bash.ExecAsync("""
            printf 'id,title\n1,first\n' > /rows.csv
            python -c "
            import csv
            csv.to_markdown('/rows.csv', '/rows.md')
            "
            cat /rows.md
            """);

        Assert.Equal("| id | title |\n| 1 | first |\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Host_code_is_charged_against_the_python_run_s_limits()
    {
        var options = new PythonOptions
        {
            MaxInstructions = 2_000,
            HostFunctions = new Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>>
            {
                ["noop"] = (_, _) => PyNone.Instance,
            },
        };

        var bash = NewSession(options);

        var result = await bash.ExecAsync("""
            python -c "
            for i in range(1000000):
                noop()
            "
            """);

        // Registering host code does not buy a way around the caps the run was given.
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task An_external_function_is_the_only_route_out()
    {
        var seen = new List<string>();

        var options = new PythonOptions
        {
            ExternalFunctions = new Dictionary<string, Func<PyObject[], PyObject>>
            {
                ["audit"] = arguments =>
                {
                    seen.Add(arguments[0].Display());
                    return PyNone.Instance;
                },
            },
        };

        var bash = NewSession(options);

        await bash.ExecAsync("""python -c "audit('ran')" """);

        Assert.Equal(["ran"], seen);
    }

    [Fact]
    public async Task An_unregistered_library_is_not_importable()
    {
        var result = await NewSession().ExecAsync("""python -c "import report" """);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ModuleNotFoundError", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_withheld_command_stays_withheld_for_a_script_python_writes()
    {
        // Withholding a name has to hold across the join, or a Python program could write a
        // shell script that reaches what the shell was configured not to have.
        var bash = Bash.CreateBuilder()
            .WithWorkingDirectory("/")
            .WithoutBuiltin("tar")
            .WithPython()
            .Build();

        var result = await bash.ExecAsync("""
            python -c "open('/run.sh', 'w').write('tar --version\n')"
            bash /run.sh
            """);

        Assert.Equal(ExitCodes.NotFound, result.ExitCode);
        Assert.Contains("tar: command not found", result.Stderr.ToString(), StringComparison.Ordinal);
    }
}
