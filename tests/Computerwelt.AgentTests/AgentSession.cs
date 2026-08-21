using Computerwelt.Emulation.Bash;

namespace Computerwelt.AgentTests;

/// <summary>
/// A sandbox holding a small source tree, and the means to run a script against it.
/// </summary>
/// <remarks>
/// <para>
/// The tree is deliberately shaped like the repository an agent is asked to work in — a
/// couple of C# files with a BOM on one of them, a project file, a JSON document, a README
/// and a nested directory — because the operations these tests cover only make sense over
/// something with that shape. <c>grep -rn</c> needs more than one file to be worth
/// running; a patch script needs a file whose encoding it has to notice.
/// </para>
/// <para>
/// Every call to <see cref="NewAsync"/> builds a fresh session: nothing here may depend on
/// what a previous test left behind.
/// </para>
/// </remarks>
public sealed class AgentSession
{
    private AgentSession(Bash bash) => Bash = bash;

    /// <summary>The shell, with <c>python</c> registered over the same filesystem.</summary>
    public Bash Bash { get; }

    /// <summary>Builds a session over a fresh copy of the sample tree.</summary>
    public static async Task<AgentSession> NewAsync(CancellationToken cancellationToken = default)
    {
        var bash = Bash.CreateBuilder()
            .WithWorkingDirectory("/work")
            .WithPython()
            .Build();

        var session = new AgentSession(bash);
        await session.SeedAsync(cancellationToken);
        return session;
    }

    /// <summary>Runs a script and returns its result.</summary>
    public Task<ExecResult> RunAsync(string script, CancellationToken cancellationToken = default) =>
        Bash.ExecAsync(script, cancellationToken).AsTask();

    /// <summary>Runs a script and returns its standard output.</summary>
    public async Task<string> OutAsync(string script, CancellationToken cancellationToken = default) =>
        (await RunAsync(script, cancellationToken)).Stdout.ToString();

    /// <summary>Reads a file back out of the sandbox.</summary>
    public async Task<string> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        System.Text.Encoding.UTF8.GetString(await Bash.FileSystem.ReadFileAsync(path, cancellationToken));

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await Bash.ExecAsync("mkdir -p /work/src /work/tests /work/docs", cancellationToken);

        await WriteAsync("/work/src/Widget.cs", """
            namespace Sample;

            public sealed class Widget
            {
                public int Count { get; set; }

                public string Describe() => $"widget {Count}";
            }

            """, cancellationToken);

        // One file carries a byte-order mark, which is the case a patch script has to
        // handle: decoding it as plain UTF-8 leaves a stray U+FEFF at the front and the
        // first `assert old in text` fails for a reason that is hard to see.
        await WriteAsync("/work/src/Gadget.cs", "﻿" + """
            namespace Sample;

            public sealed class Gadget
            {
                public int Count { get; set; }

                public string Describe() => $"gadget {Count}";
            }

            """, cancellationToken);

        await WriteAsync("/work/src/Sample.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>

            """, cancellationToken);

        await WriteAsync("/work/tests/WidgetTests.cs", """
            namespace Sample.Tests;

            public sealed class WidgetTests
            {
                public void Describes() { }
            }

            """, cancellationToken);

        await WriteAsync("/work/docs/notes.md", """
            # Notes

            - widget counts start at zero
            - gadget counts start at zero
            - TODO: describe the encoding rules

            """, cancellationToken);

        await WriteAsync("/work/config.json", """
            {
              "name": "sample",
              "version": "1.0.0",
              "features": ["widget", "gadget"]
            }

            """, cancellationToken);

        await WriteAsync("/work/README.md", """
            # Sample

            A tree small enough to reason about and large enough to search.

            """, cancellationToken);
    }

    private Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
        Bash.FileSystem.WriteFileAsync(path, System.Text.Encoding.UTF8.GetBytes(content), cancellationToken).AsTask();
}
