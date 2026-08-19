using System.Text;
using Bashkit.Parsing;

namespace Bashkit.Interpreter;

/// <summary>
/// Applies a command's redirections around its execution.
/// </summary>
/// <remarks>
/// <para>
/// Without real file descriptors, redirection is modelled as a transformation of the
/// command's inputs and outputs: input redirections are resolved <i>before</i> the command
/// runs and become its stdin, and output redirections are applied <i>after</i>, diverting
/// the returned stdout or stderr into the filesystem instead of the caller.
/// </para>
/// <para>
/// This preserves observable behaviour for everything a script can detect, and it is the
/// only design that works when commands return their output as values.
/// </para>
/// </remarks>
public sealed class Redirection
{
    private readonly List<Target> _outputs = [];
    private bool _discardStdout;
    private bool _discardStderr;
    private bool _stderrToStdout;
    private bool _stdoutToStderr;

    private Redirection(IFileSystem fileSystem) => FileSystem = fileSystem;

    private IFileSystem FileSystem { get; }

    /// <summary>Standard input for the command after input redirections are resolved.</summary>
    public StreamData? Stdin { get; private set; }

    /// <summary>Resolves the redirection list, reading any input sources.</summary>
    public static async ValueTask<Redirection> PrepareAsync(
        Interpreter interpreter,
        IReadOnlyList<Redirect> redirects,
        StreamData? stdin,
        CancellationToken cancellationToken)
    {
        var redirection = new Redirection(interpreter.FileSystem) { Stdin = stdin };

        foreach (var redirect in redirects)
        {
            await redirection.AddAsync(interpreter, redirect, cancellationToken);
        }

        return redirection;
    }

    private async ValueTask AddAsync(Interpreter interpreter, Redirect redirect, CancellationToken cancellationToken)
    {
        switch (redirect.Kind)
        {
            case RedirectKind.Input:
            {
                var path = interpreter.State.WorkingDirectory.Join(
                    await interpreter.Expander.ExpandToStringAsync(redirect.Target, cancellationToken));
                var bytes = await FileSystem.ReadFileAsync(path, cancellationToken);
                Stdin = StreamData.FromBytes(bytes);
                return;
            }

            case RedirectKind.HereString:
            {
                var text = await interpreter.Expander.ExpandToStringAsync(redirect.Target, cancellationToken);
                Stdin = StreamData.FromText(text + "\n");
                return;
            }

            case RedirectKind.HereDocument:
            {
                var body = redirect.HereDocumentBody ?? string.Empty;

                // An unquoted delimiter means the body is expanded like a double-quoted
                // string; a quoted one means it is taken literally.
                if (redirect.HereDocumentExpands && body.Length > 0)
                {
                    body = await interpreter.Expander.ExpandToStringAsync(
                        WordParser.Parse("\"" + body.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""),
                        cancellationToken);
                }

                Stdin = StreamData.FromText(body);
                return;
            }

            case RedirectKind.DuplicateOutput:
            {
                var target = await interpreter.Expander.ExpandToStringAsync(redirect.Target, cancellationToken);
                ApplyDuplicate(redirect.Fd, target);
                return;
            }

            case RedirectKind.DuplicateInput:
                // `<&N` with no real descriptors is a no-op unless it closes the input.
                if ((await interpreter.Expander.ExpandToStringAsync(redirect.Target, cancellationToken)) == "-")
                {
                    Stdin = null;
                }

                return;

            default:
            {
                var path = await interpreter.Expander.ExpandToStringAsync(redirect.Target, cancellationToken);
                await AddOutputAsync(interpreter, redirect, path, cancellationToken);
                return;
            }
        }
    }

    private void ApplyDuplicate(int fd, string target)
    {
        switch (target)
        {
            case "1" when fd == 2:
                _stderrToStdout = true;
                return;
            case "2" when fd == 1:
                _stdoutToStderr = true;
                return;
            case "-" when fd == 1:
                _discardStdout = true;
                return;
            case "-" when fd == 2:
                _discardStderr = true;
                return;
        }
    }

    private async ValueTask AddOutputAsync(Interpreter interpreter, Redirect redirect, string path, CancellationToken cancellationToken)
    {
        var append = redirect.Kind is RedirectKind.Append or RedirectKind.AppendBoth;
        var both = redirect.Kind is RedirectKind.OutputBoth or RedirectKind.AppendBoth;

        // `>/dev/null` is the canonical way to discard output, and the sandbox has no
        // device nodes, so it is recognized by name.
        if (path is "/dev/null")
        {
            if (both || redirect.Fd == 1)
            {
                _discardStdout = true;
            }

            if (both || redirect.Fd == 2)
            {
                _discardStderr = true;
            }

            return;
        }

        var resolved = interpreter.State.WorkingDirectory.Join(path);

        if (!append && interpreter.State.Options.NoClobber && redirect.Kind == RedirectKind.Output
            && await FileSystem.ExistsAsync(resolved, cancellationToken))
        {
            throw new BashkitException(BashkitErrorKind.PermissionDenied, $"{path}: cannot overwrite existing file");
        }

        // Truncate now, so that `> f` with no output still empties the file.
        if (!append)
        {
            await FileSystem.WriteFileAsync(resolved, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }

        _outputs.Add(new Target(resolved, append, both || redirect.Fd == 1, both || redirect.Fd == 2));
    }

    /// <summary>Diverts the command's output according to the collected redirections.</summary>
    public async ValueTask<ExecResult> ApplyAsync(ExecResult result, CancellationToken cancellationToken)
    {
        var stdout = result.Stdout;
        var stderr = result.Stderr;

        if (_stderrToStdout)
        {
            stdout = StreamData.Concat(stdout, stderr);
            stderr = StreamData.Empty;
        }
        else if (_stdoutToStderr)
        {
            stderr = StreamData.Concat(stderr, stdout);
            stdout = StreamData.Empty;
        }

        foreach (var target in _outputs)
        {
            var payload = StreamData.Empty;

            if (target.CapturesStdout)
            {
                payload = StreamData.Concat(payload, stdout);
            }

            if (target.CapturesStderr)
            {
                payload = StreamData.Concat(payload, stderr);
            }

            if (target.Append)
            {
                await FileSystem.AppendFileAsync(target.Path, payload.Memory, cancellationToken);
            }
            else
            {
                await FileSystem.WriteFileAsync(target.Path, payload.Memory, cancellationToken);
            }

            if (target.CapturesStdout)
            {
                stdout = StreamData.Empty;
            }

            if (target.CapturesStderr)
            {
                stderr = StreamData.Empty;
            }
        }

        if (_discardStdout)
        {
            stdout = StreamData.Empty;
        }

        if (_discardStderr)
        {
            stderr = StreamData.Empty;
        }

        return result with { Stdout = stdout, Stderr = stderr };
    }

    private readonly record struct Target(VPath Path, bool Append, bool CapturesStdout, bool CapturesStderr);

    /// <summary>Formats a builder's contents as stream data.</summary>
    internal static StreamData ToStream(StringBuilder builder) => StreamData.FromText(builder.ToString());
}
