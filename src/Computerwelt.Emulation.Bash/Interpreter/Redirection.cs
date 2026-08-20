using System.Text;
using Computerwelt.Emulation.Bash.Parsing;

namespace Computerwelt.Emulation.Bash.Interpreter;

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
    private readonly List<StringBuilder> _escaping = [];
    private StringBuilder? _stdoutEscape;
    private bool _stdoutIsRedirected;
    private bool _discardStdout;
    private bool _discardStderr;
    private bool _stderrToStdout;
    private bool _stdoutToStderr;

    private Redirection(IFileSystem fileSystem) => FileSystem = fileSystem;

    private IFileSystem FileSystem { get; }

    /// <summary>Standard input for the command after input redirections are resolved.</summary>
    public StreamData? Stdin { get; private set; }

    /// <summary>
    /// The error a redirection failed with, or <see langword="null"/> when they all
    /// resolved.
    /// </summary>
    /// <remarks>
    /// A redirection that cannot be set up — a missing input file, an output path that is a
    /// directory — stops the command without running it and reports status 1, which is what
    /// bash does. Letting the failure escape as an exception would instead abort the whole
    /// script.
    /// </remarks>
    public ExecResult? Failure { get; private set; }

    /// <summary>Resolves the redirection list, reading any input sources.</summary>
    public static async ValueTask<Redirection> PrepareAsync(
        Interpreter interpreter,
        IReadOnlyList<Redirect> redirects,
        StreamData? stdin,
        CancellationToken cancellationToken)
    {
        var redirection = new Redirection(interpreter.FileSystem) { Stdin = stdin };

        // A descriptor that duplicates stdout only needs a buffer of its own when this same
        // list goes on to re-point stdout; otherwise it still is stdout.
        redirection._stdoutIsRedirected = redirects.Any(static r =>
            r.Kind is RedirectKind.Output or RedirectKind.Append or RedirectKind.Clobber
                or RedirectKind.OutputBoth or RedirectKind.AppendBoth
            && (r.Fd == 1 || r.Kind is RedirectKind.OutputBoth or RedirectKind.AppendBoth));

        foreach (var redirect in redirects)
        {
            try
            {
                await redirection.AddAsync(interpreter, redirect, cancellationToken);
            }
            catch (BashkitException exception)
                when (exception.Kind is BashkitErrorKind.FileSystem or BashkitErrorKind.PermissionDenied)
            {
                redirection.Failure = ExecResult.Error($"bash: {exception.Message}\n", ExitCodes.Failure);
                return redirection;
            }
        }

        return redirection;
    }

    private async ValueTask AddAsync(Interpreter interpreter, Redirect redirect, CancellationToken cancellationToken)
    {
        switch (redirect.Kind)
        {
            case RedirectKind.Input:
            {
                var target = await interpreter.Expander.ExpandToStringAsync(redirect.Target, cancellationToken);

                // The sandbox has no device nodes, so `/dev/null` is recognised by name.
                if (target == "/dev/null")
                {
                    Stdin = StreamData.Empty;
                    return;
                }

                var path = interpreter.State.WorkingDirectory.Join(target);
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
                ApplyDuplicate(interpreter.State, redirect.Fd, target);
                return;
            }

            case RedirectKind.DuplicateInput:
            {
                var target = await interpreter.Expander.ExpandToStringAsync(redirect.Target, cancellationToken);

                if (target == "-")
                {
                    Stdin = null;
                    return;
                }

                // `<&N` reads a descriptor a coprocess opened; anything else has no real
                // descriptor behind it and is a no-op.
                if (int.TryParse(target, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var source)
                    && interpreter.State.InputDescriptors.TryGetValue(source, out var buffered))
                {
                    Stdin = StreamData.FromText(buffered);
                    interpreter.State.InputDescriptors[source] = string.Empty;
                }

                return;
            }

            default:
            {
                var path = await interpreter.Expander.ExpandToStringAsync(redirect.Target, cancellationToken);
                await AddOutputAsync(interpreter, redirect, path, cancellationToken);
                return;
            }
        }
    }

    private void ApplyDuplicate(ShellState state, int fd, string target)
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

        // `N>&-` closes an extra descriptor.
        if (fd >= 3 && target == "-")
        {
            state.Descriptors.Remove(fd);
            return;
        }

        // `N>&1` and `N>&2` make an extra descriptor an alias of a standard stream. For
        // stdout that alias is taken now, before any later redirection re-points it, so the
        // descriptor gets a buffer of its own that this command's output is joined with.
        if (fd >= 3 && target is "1" or "2")
        {
            state.Descriptors[fd] = "&" + target;

            if (target == "1" && _stdoutIsRedirected)
            {
                var buffer = new StringBuilder();
                state.DescriptorBuffers[fd] = buffer;
                _escaping.Add(buffer);
            }
            else if (target == "1")
            {
                state.DescriptorBuffers.Remove(fd);
            }

            return;
        }

        // `1>&N` and `2>&N` send a standard stream wherever N was opened. An unopened
        // descriptor is left alone rather than guessed at.
        if (fd is 1 or 2
            && int.TryParse(target, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var source)
            && source >= 3
            && state.Descriptors.TryGetValue(source, out var opened))
        {
            if (opened is null)
            {
                if (fd == 1)
                {
                    _discardStdout = true;
                }
                else
                {
                    _discardStderr = true;
                }

                return;
            }

            if (opened.StartsWith('&'))
            {
                if (opened == "&1" && state.DescriptorBuffers.TryGetValue(source, out var escape))
                {
                    // The descriptor was captured before the enclosing redirection, so what
                    // is written to it bypasses that redirection.
                    if (fd == 1)
                    {
                        _stdoutEscape = escape;
                    }
                    else
                    {
                        _stderrToStdout = true;
                        _stdoutEscape = escape;
                    }

                    return;
                }

                if (fd == 2 && opened == "&1")
                {
                    _stderrToStdout = true;
                }
                else if (fd == 1 && opened == "&2")
                {
                    _stdoutToStderr = true;
                }

                return;
            }

            // The descriptor stays open across commands, so each write appends to it.
            _outputs.Add(new Target(VPath.Parse(opened), Append: true, fd == 1, fd == 2));
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
            if (redirect.Fd >= 3 && !both)
            {
                interpreter.State.Descriptors[redirect.Fd] = null;
                return;
            }

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

        // A descriptor above 2 has no stream of its own to divert; opening it records
        // where later `>&N` writes should land.
        if (redirect.Fd >= 3 && !both)
        {
            interpreter.State.Descriptors[redirect.Fd] = resolved.Value;

            if (!append)
            {
                await FileSystem.WriteFileAsync(resolved, ReadOnlyMemory<byte>.Empty, cancellationToken);
            }

            return;
        }

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

        // Output bound for a descriptor that duplicated the outer stdout is set aside
        // before any file redirection can claim it.
        if (_stdoutEscape is not null)
        {
            _stdoutEscape.Append(stdout.ToString());
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

            try
            {
                if (target.Append)
                {
                    await FileSystem.AppendFileAsync(target.Path, payload.Memory, cancellationToken);
                }
                else
                {
                    await FileSystem.WriteFileAsync(target.Path, payload.Memory, cancellationToken);
                }
            }
            catch (BashkitException e) when (e.Kind is BashkitErrorKind.FileSystem or BashkitErrorKind.PermissionDenied)
            {
                // Writing to a directory or a read-only file fails the command; it is not
                // an error in the shell itself.
                return result with
                {
                    Stdout = StreamData.Empty,
                    Stderr = StreamData.Concat(stderr, StreamData.FromText($"bash: {e.Message}\n")),
                    ExitCode = ExitCodes.Failure,
                };
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

        // Whatever escaped through such a descriptor is this command's output.
        foreach (var buffer in _escaping)
        {
            if (buffer.Length > 0)
            {
                stdout = StreamData.Concat(StreamData.FromText(buffer.ToString()), stdout);
                buffer.Clear();
            }
        }

        return result with { Stdout = stdout, Stderr = stderr };
    }

    private readonly record struct Target(VPath Path, bool Append, bool CapturesStdout, bool CapturesStderr);

    /// <summary>Formats a builder's contents as stream data.</summary>
    internal static StreamData ToStream(StringBuilder builder) => StreamData.FromText(builder.ToString());
}
