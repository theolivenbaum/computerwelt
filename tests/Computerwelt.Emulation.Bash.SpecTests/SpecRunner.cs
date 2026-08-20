using System.Diagnostics;

namespace Computerwelt.Emulation.Bash.SpecTests;

/// <summary>Runs one spec case against a fresh sandbox.</summary>
public static class SpecRunner
{
    /// <summary>Runs a case and reports what happened.</summary>
    public static async Task<SpecOutcome> RunAsync(SpecCase specCase, CancellationToken cancellationToken = default)
    {
        // Each case gets its own session: cases are not allowed to depend on state left
        // behind by whichever case happened to run before them.
        var bash = Bash.CreateBuilder()
            .WithWorkingDirectory("/tmp")
            .WithLimits(ExecutionLimits.Default with { Timeout = TimeSpan.FromSeconds(10) })
            .Build();

        await bash.FileSystem.CreateDirectoryAsync("/tmp", recursive: true, cancellationToken);
        await bash.FileSystem.CreateDirectoryAsync("/home/user", recursive: true, cancellationToken);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await bash.ExecAsync(specCase.Script, cancellationToken);
            stopwatch.Stop();

            var stdoutMatches = result.Stdout.ToString() == specCase.ExpectedStdout;
            var exitMatches = specCase.ExpectedExitCode is not { } expected || result.ExitCode == expected;

            return new SpecOutcome
            {
                Case = specCase,
                Passed = stdoutMatches && exitMatches,
                ActualStdout = result.Stdout.ToString(),
                ActualStderr = result.Stderr.ToString(),
                ActualExitCode = result.ExitCode,
                Duration = stopwatch.Elapsed,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            stopwatch.Stop();

            return new SpecOutcome
            {
                Case = specCase,
                Passed = false,
                ActualStdout = string.Empty,
                ActualStderr = e.Message,
                ActualExitCode = -1,
                Error = e,
                Duration = stopwatch.Elapsed,
            };
        }
    }
}

/// <summary>The result of running one spec case.</summary>
public sealed record SpecOutcome
{
    /// <summary>The case that ran.</summary>
    public required SpecCase Case { get; init; }

    /// <summary>True when stdout and exit status both matched.</summary>
    public required bool Passed { get; init; }

    /// <summary>What the port actually printed.</summary>
    public required string ActualStdout { get; init; }

    /// <summary>What the port wrote to stderr.</summary>
    public required string ActualStderr { get; init; }

    /// <summary>The status the port returned, or -1 when it threw.</summary>
    public required int ActualExitCode { get; init; }

    /// <summary>The exception thrown, when the case failed by throwing.</summary>
    public Exception? Error { get; init; }

    /// <summary>How long the case took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>A diff-style description suitable for a failure message.</summary>
    public string Describe() =>
        $"""
         {Case.File}::{Case.Name}
         script:
         {Indent(Case.Script)}
         expected stdout:
         {Indent(Case.ExpectedStdout)}
         actual stdout:
         {Indent(ActualStdout)}
         stderr:
         {Indent(ActualStderr)}
         exit: expected {Case.ExpectedExitCode?.ToString() ?? "any"}, got {ActualExitCode}
         """;

    private static string Indent(string text) =>
        text.Length == 0 ? "  <empty>" : string.Join('\n', text.TrimEnd('\n').Split('\n').Select(static l => "  " + l));
}
