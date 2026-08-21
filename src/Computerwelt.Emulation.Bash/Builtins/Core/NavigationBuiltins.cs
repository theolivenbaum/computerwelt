namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary><c>cd</c> — changes the working directory.</summary>
public sealed class CdBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "cd";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var operands = new ArgCursor(context.Arguments).DrainOperands();
        var target = operands.Count > 0 ? operands[0] : context.State.Get("HOME") ?? "/";

        // `cd -` returns to the previous directory and prints it, as bash does.
        var announce = false;
        if (target == "-")
        {
            target = context.State.Get("OLDPWD") ?? context.State.WorkingDirectory.Value;
            announce = true;
        }

        var resolved = VPath.Resolve(context.State.WorkingDirectory, target);

        FileMetadata metadata;
        try
        {
            metadata = await context.FileSystem.StatAsync(resolved, cancellationToken);
        }
        catch (FileSystemException)
        {
            return ExecResult.Error($"bash: cd: {target}: No such file or directory\n", ExitCodes.Failure);
        }

        if (!metadata.IsDirectory)
        {
            return ExecResult.Error($"bash: cd: {target}: Not a directory\n", ExitCodes.Failure);
        }

        var previous = context.State.WorkingDirectory;
        context.State.WorkingDirectory = resolved;
        context.State.Set("OLDPWD", previous.Value);
        context.State.Set("PWD", resolved.Value);

        return announce ? ExecResult.Ok(resolved.Value + "\n") : ExecResult.Success;
    }
}

/// <summary><c>pwd</c> — prints the working directory.</summary>
public sealed class PwdBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "pwd";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ExecResult.Ok(context.State.WorkingDirectory.Value + "\n"));
}
