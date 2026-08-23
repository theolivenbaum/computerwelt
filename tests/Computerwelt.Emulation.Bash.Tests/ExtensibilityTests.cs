using System.Text;
using Computerwelt.Emulation.Bash.Builtins;
using Xunit;

namespace Computerwelt.Emulation.Bash.Tests;

/// <summary>
/// The surface a host uses to give the shell its own vocabulary.
/// </summary>
/// <remarks>
/// These tests are as much about what extending the shell may <i>not</i> do as about what it
/// can: a resolver that cannot shadow an existing command, a withheld command that stays
/// withheld, and a registration that never becomes a route to the host.
/// </remarks>
public sealed class ExtensibilityTests
{
    /// <summary>A command with options and stdin, written the way a host would write one.</summary>
    private sealed class GreetBuiltin : IBuiltin
    {
        public string Name => "greet";

        public string? LlmHint => "greet [--loud] NAME: greets someone.";

        public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
        {
            var loud = context.Arguments.Contains("--loud");
            var name = context.Arguments.FirstOrDefault(argument => !argument.StartsWith('-'))
                ?? context.StdinText.Trim();

            if (name.Length == 0)
            {
                return ValueTask.FromResult(ExecResult.Usage("greet", "no one to greet"));
            }

            var line = $"Hello, {name}!\n";
            return ValueTask.FromResult(ExecResult.Ok(loud ? line.ToUpperInvariant() : line));
        }
    }

    /// <summary>Two commands that only make sense together, so they ship together.</summary>
    private sealed class NotesExtension : IShellExtension
    {
        public string Name => "notes";

        public IEnumerable<IBuiltin> Builtins =>
        [
            new DelegateBuiltin("note-add", async (context, cancellationToken) =>
            {
                var text = string.Join(' ', context.Arguments) + "\n";
                var path = context.ResolvePath("/notes.txt");

                var existing = await context.FileSystem.ExistsAsync(path, cancellationToken)
                    ? Encoding.UTF8.GetString(await context.FileSystem.ReadFileAsync(path, cancellationToken))
                    : string.Empty;

                await context.FileSystem.WriteFileAsync(path, Encoding.UTF8.GetBytes(existing + text), cancellationToken);
                return ExecResult.Success;
            }),

            new DelegateBuiltin("note-count", async (context, cancellationToken) =>
            {
                var path = context.ResolvePath("/notes.txt");

                if (!await context.FileSystem.ExistsAsync(path, cancellationToken))
                {
                    return ExecResult.Ok("0\n");
                }

                var text = Encoding.UTF8.GetString(await context.FileSystem.ReadFileAsync(path, cancellationToken));
                return ExecResult.Ok($"{text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}\n");
            }),
        ];
    }

    /// <summary>An open-ended command space: every <c>tool:*</c> name answers for itself.</summary>
    private sealed class ToolResolver : ICommandResolver
    {
        public IBuiltin? Resolve(string name) =>
            name.StartsWith("tool:", StringComparison.Ordinal)
                ? new DelegateBuiltin(name, context => ExecResult.Ok(
                    $"{context.Name[5..]} {string.Join(' ', context.Arguments)}\n".TrimEnd() + "\n"))
                : null;
    }

    [Fact]
    public async Task Registers_a_custom_command()
    {
        var bash = Bash.CreateBuilder().WithBuiltin(new GreetBuiltin()).Build();

        Assert.Equal("Hello, Ada!\n", (await bash.ExecAsync("greet Ada")).Stdout.ToString());
        Assert.Equal("HELLO, ADA!\n", (await bash.ExecAsync("greet --loud Ada")).Stdout.ToString());
    }

    [Fact]
    public async Task A_custom_command_is_an_ordinary_command()
    {
        var bash = Bash.CreateBuilder().WithBuiltin(new GreetBuiltin()).Build();

        // Pipelines, substitution, redirection and `$?` all work because a registered
        // command is dispatched exactly as a built-in one is.
        var result = await bash.ExecAsync("""
            echo Grace | greet | tr 'a-z' 'A-Z'
            who=$(greet Alan)
            echo "${who%!*}"
            greet
            echo status=$?
            """);

        Assert.Equal("HELLO, GRACE!\nHello, Alan\nstatus=2\n", result.Stdout.ToString());
        Assert.Equal("greet: no one to greet\n", result.Stderr.ToString());
    }

    [Fact]
    public async Task Registers_a_command_from_a_delegate()
    {
        var bash = Bash.CreateBuilder()
            .WithBuiltin("ping", _ => ExecResult.Ok("pong\n"))
            .Build();

        Assert.Equal("pong\n", (await bash.ExecAsync("ping")).Stdout.ToString());
    }

    [Fact]
    public async Task An_extension_contributes_its_commands_as_one_unit()
    {
        var bash = Bash.CreateBuilder().WithExtension(new NotesExtension()).Build();

        var result = await bash.ExecAsync("""
            note-add first
            note-add second
            note-count
            """);

        Assert.Equal("2\n", result.Stdout.ToString());
        Assert.Contains("note-add", bash.BuiltinNames);
        Assert.Contains("note-count", bash.BuiltinNames);
    }

    [Fact]
    public async Task A_custom_command_replaces_a_default_of_the_same_name()
    {
        var bash = Bash.CreateBuilder()
            .WithBuiltin("echo", _ => ExecResult.Ok("intercepted\n"))
            .Build();

        Assert.Equal("intercepted\n", (await bash.ExecAsync("echo anything")).Stdout.ToString());
    }

    [Fact]
    public async Task A_withheld_command_is_absent_rather_than_refusing()
    {
        var bash = Bash.CreateBuilder().WithoutBuiltins("tar", "curl").Build();

        var result = await bash.ExecAsync("tar --version");

        Assert.Equal(ExitCodes.NotFound, result.ExitCode);
        Assert.Equal("bash: tar: command not found\n", result.Stderr.ToString());

        // Absent to the shell's own introspection too, so a script cannot discover a
        // capability it does not have.
        Assert.DoesNotContain("tar", bash.BuiltinNames);
        Assert.Equal("", (await bash.ExecAsync("command -v tar")).Stdout.ToString());
        Assert.Equal(ExitCodes.Failure, (await bash.ExecAsync("type tar")).ExitCode);
    }

    [Fact]
    public async Task Withholding_wins_over_every_registration()
    {
        // Order of the calls must not matter: the withheld name loses to nothing.
        var bash = Bash.CreateBuilder()
            .WithoutBuiltin("note-add")
            .WithExtension(new NotesExtension())
            .WithBuiltin("note-add", _ => ExecResult.Ok("registered anyway\n"))
            .Build();

        Assert.Equal(ExitCodes.NotFound, (await bash.ExecAsync("note-add x")).ExitCode);
        Assert.Contains("note-count", bash.BuiltinNames);
    }

    [Fact]
    public async Task Without_defaults_only_registered_commands_exist()
    {
        var bash = Bash.CreateBuilder()
            .WithoutDefaultBuiltins()
            .WithBuiltin(new GreetBuiltin())
            .Build();

        Assert.Equal(["greet"], bash.BuiltinNames);
        Assert.Equal("Hello, Ada!\n", (await bash.ExecAsync("greet Ada")).Stdout.ToString());
        Assert.Equal(ExitCodes.NotFound, (await bash.ExecAsync("cat /etc/passwd")).ExitCode);
    }

    [Fact]
    public async Task A_resolver_answers_for_a_name_nothing_else_matched()
    {
        var bash = Bash.CreateBuilder().WithCommandResolver(new ToolResolver()).Build();

        Assert.Equal("deploy --dry-run\n", (await bash.ExecAsync("tool:deploy --dry-run")).Stdout.ToString());

        // A name it does not own is still not found.
        Assert.Equal(ExitCodes.NotFound, (await bash.ExecAsync("deploy")).ExitCode);
    }

    [Fact]
    public async Task A_resolver_cannot_shadow_a_registered_command()
    {
        var bash = Bash.CreateBuilder()
            .WithCommandResolver(_ => new DelegateBuiltin("anything", _ => ExecResult.Ok("from resolver\n")))
            .Build();

        Assert.Equal("real\n", (await bash.ExecAsync("echo real")).Stdout.ToString());
    }

    [Fact]
    public async Task A_resolver_cannot_shadow_a_script_on_the_path()
    {
        var bash = Bash.CreateBuilder()
            .WithCommandResolver(_ => new DelegateBuiltin("anything", _ => ExecResult.Ok("from resolver\n")))
            .Build();

        var result = await bash.ExecAsync("""
            mkdir -p /usr/local/bin
            printf 'echo from script\n' > /usr/local/bin/report
            chmod +x /usr/local/bin/report
            report
            """);

        Assert.Equal("from script\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_resolver_cannot_shadow_a_shell_function()
    {
        var bash = Bash.CreateBuilder()
            .WithCommandResolver(new ToolResolver())
            .Build();

        var result = await bash.ExecAsync("""
            function tool:deploy { echo from function; }
            tool:deploy
            """);

        Assert.Equal("from function\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Resolved_names_are_not_enumerable()
    {
        var bash = Bash.CreateBuilder().WithCommandResolver(new ToolResolver()).Build();

        // The resolver is asked about one name at a time and never listed, so a name it
        // would answer for is invisible to introspection — which is the honest report:
        // the host cannot enumerate the space either.
        Assert.DoesNotContain("tool:deploy", bash.BuiltinNames);
        Assert.Equal(ExitCodes.Failure, (await bash.ExecAsync("type tool:deploy")).ExitCode);
    }

    [Fact]
    public async Task Resolvers_are_consulted_in_registration_order()
    {
        var bash = Bash.CreateBuilder()
            .WithCommandResolver(name => name == "x" ? new DelegateBuiltin("x", _ => ExecResult.Ok("first\n")) : null)
            .WithCommandResolver(name => name == "x" ? new DelegateBuiltin("x", _ => ExecResult.Ok("second\n")) : null)
            .Build();

        Assert.Equal("first\n", (await bash.ExecAsync("x")).Stdout.ToString());
    }

    [Fact]
    public async Task A_resolved_command_reads_the_pipeline()
    {
        var bash = Bash.CreateBuilder()
            .WithCommandResolver(name => name == "shout"
                ? new DelegateBuiltin("shout", context => ExecResult.Ok(context.StdinText.ToUpperInvariant()))
                : null)
            .Build();

        Assert.Equal("QUIET\n", (await bash.ExecAsync("echo quiet | shout")).Stdout.ToString());
    }

    [Fact]
    public async Task A_custom_command_is_charged_against_the_execution_budget()
    {
        var bash = Bash.CreateBuilder()
            .WithLimits(ExecutionLimits.Default with { MaxCommands = 3 })
            .WithCommandResolver(name => name == "noop" ? new DelegateBuiltin("noop", _ => ExecResult.Success) : null)
            .Build();

        var result = await bash.ExecAsync("noop; noop; noop; noop");

        // A host command extends the vocabulary; it does not buy a way around the limits.
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("max_commands", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_custom_command_works_through_the_context_helpers()
    {
        // What "implement it in C#" looks like at its shortest: the context carries the
        // sandbox's filesystem, so a command is a few lines and reaches nothing else.
        var bash = Bash.CreateBuilder()
            .WithBuiltin("upper", async (context, cancellationToken) =>
            {
                var text = await context.ReadTextAsync(context.Arguments[0], cancellationToken);
                await context.WriteTextAsync(context.Arguments[1], text.ToUpperInvariant(), cancellationToken);

                return ExecResult.Success;
            })
            .Build();

        var result = await bash.ExecAsync("""
            printf 'quiet\n' > /in.txt
            upper /in.txt /out.txt
            cat /out.txt
            """);

        Assert.Equal("QUIET\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task A_custom_command_reads_a_store_that_is_not_there_yet()
    {
        var bash = Bash.CreateBuilder()
            .WithBuiltin("count", async (context, cancellationToken) =>
            {
                var text = await context.ReadTextIfExistsAsync("/store.txt", cancellationToken) ?? string.Empty;
                return ExecResult.Ok($"{text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}\n");
            })
            .Build();

        Assert.Equal("0\n", (await bash.ExecAsync("count")).Stdout.ToString());
        Assert.Equal("1\n", (await bash.ExecAsync("echo one > /store.txt; count")).Stdout.ToString());
    }

    [Fact]
    public async Task A_custom_command_sees_the_exported_environment()
    {
        static Bash NewShell() => Bash.CreateBuilder()
            .WithBuiltin("tenant", context => ExecResult.Ok(
                $"{context.Environment.GetValueOrDefault("TENANT", "none")}\n"))
            .Build();

        Assert.Equal("none\n", (await NewShell().ExecAsync("tenant")).Stdout.ToString());
        Assert.Equal("acme\n", (await NewShell().ExecAsync("export TENANT=acme; tenant")).Stdout.ToString());

        // A variable that was never exported is not part of the environment, exactly as it
        // would not be for a real child process.
        Assert.Equal("none\n", (await NewShell().ExecAsync("TENANT=local; tenant")).Stdout.ToString());
    }

    [Fact]
    public async Task A_custom_command_resolves_a_relative_path_against_the_working_directory()
    {
        var bash = Bash.CreateBuilder()
            .WithBuiltin("cat2", async (context, cancellationToken) =>
                ExecResult.Ok(await context.ReadTextAsync(context.Arguments[0], cancellationToken)))
            .Build();

        var result = await bash.ExecAsync("""
            mkdir -p /srv
            echo inside > /srv/note.txt
            cd /srv
            cat2 note.txt
            """);

        Assert.Equal("inside\n", result.Stdout.ToString());
    }

    [Fact]
    public async Task Two_sessions_sharing_one_command_share_no_state()
    {
        // The builtin instance is shared by construction, so this is the test that says the
        // sharing is safe: the data lives in each session's own filesystem.
        var extension = new NotesExtension();

        var first = Bash.CreateBuilder().WithExtension(extension).Build();
        var second = Bash.CreateBuilder().WithExtension(extension).Build();

        await first.ExecAsync("note-add one; note-add two");
        await second.ExecAsync("note-add only");

        Assert.Equal("2\n", (await first.ExecAsync("note-count")).Stdout.ToString());
        Assert.Equal("1\n", (await second.ExecAsync("note-count")).Stdout.ToString());
    }

    [Fact]
    public async Task A_custom_command_can_run_a_script_fragment_through_the_shell()
    {
        var bash = Bash.CreateBuilder()
            .WithBuiltin("twice", async (context, cancellationToken) =>
            {
                var script = string.Join(' ', context.Arguments);
                var first = await context.Hooks.RunFragment(script, null, cancellationToken);
                var second = await context.Hooks.RunFragment(script, null, cancellationToken);

                return ExecResult.Ok(StreamData.Concat(first.Stdout, second.Stdout));
            })
            .Build();

        Assert.Equal("hi\nhi\n", (await bash.ExecAsync("twice echo hi")).Stdout.ToString());
    }
}
