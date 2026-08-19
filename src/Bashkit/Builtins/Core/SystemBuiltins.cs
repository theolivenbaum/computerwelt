using System.Globalization;
using System.Text;

namespace Bashkit.Builtins;

/// <summary><c>whoami</c>, <c>id</c>, <c>hostname</c> and <c>uname</c> — identity, from the sandbox's point of view.</summary>
/// <remarks>
/// These report the configured virtual identity, never the host's. Leaking the real user
/// name, host name or kernel version would be an information disclosure across the sandbox
/// boundary, however harmless it looks.
/// </remarks>
public sealed class IdentityBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under <paramref name="name"/>.</summary>
    public IdentityBuiltin(string name) => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var user = context.State.Get("USER") ?? "user";
        var host = context.State.Get("HOSTNAME") ?? "sandbox";

        return ValueTask.FromResult(Name switch
        {
            "whoami" => ExecResult.Ok(user + "\n"),
            "hostname" => ExecResult.Ok(host + "\n"),
            "id" => ExecResult.Ok(FormatId(context, user)),
            "uname" => ExecResult.Ok(FormatUname(context, host)),
            _ => ExecResult.Success,
        });
    }

    private static string FormatId(BuiltinContext context, string user)
    {
        // Root is uid 0; anyone else is the conventional first non-system uid.
        var uid = user == "root" ? 0 : 1000;
        var group = user == "root" ? "root" : user;

        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case "-u" or "--user":
                    return uid.ToString(CultureInfo.InvariantCulture) + "\n";
                case "-g" or "--group":
                    return uid.ToString(CultureInfo.InvariantCulture) + "\n";
                case "-n" or "--name":
                    return user + "\n";
                case "-un" or "-nu":
                    return user + "\n";
            }
        }

        return $"uid={uid}({user}) gid={uid}({group}) groups={uid}({group})\n";
    }

    private static string FormatUname(BuiltinContext context, string host)
    {
        const string System = "Linux";
        const string Release = "6.0.0-bashkit";
        const string Version = "#1 SMP bashkit";
        const string Machine = "x86_64";

        if (context.Arguments.Count == 0)
        {
            return System + "\n";
        }

        var parts = new List<string>();
        var all = context.Arguments.Any(static a => a is "-a" or "--all");

        if (all)
        {
            return string.Join(' ', [System, host, Release, Version, Machine]) + "\n";
        }

        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case "-s" or "--kernel-name": parts.Add(System); break;
                case "-n" or "--nodename": parts.Add(host); break;
                case "-r" or "--kernel-release": parts.Add(Release); break;
                case "-v" or "--kernel-version": parts.Add(Version); break;
                case "-m" or "--machine" or "-p" or "-i": parts.Add(Machine); break;
                case "-o" or "--operating-system": parts.Add("GNU/Linux"); break;
            }
        }

        return (parts.Count == 0 ? System : string.Join(' ', parts)) + "\n";
    }
}

/// <summary><c>sleep</c> — pauses, bounded by the execution timeout.</summary>
public sealed class SleepBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "sleep";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var total = TimeSpan.Zero;
        var operands = 0;

        foreach (var argument in context.Arguments)
        {
            // A negative interval is not an option: `sleep -1` is an invalid duration, and
            // coreutils reports it as one rather than as an unknown flag.
            if (argument.StartsWith('-') && argument.Length > 1 && !char.IsAsciiDigit(argument[1]) && argument[1] != '.')
            {
                continue;
            }

            operands++;

            if (!TryParseDuration(argument, out var duration) || duration < TimeSpan.Zero)
            {
                return ExecResult.Error($"sleep: invalid time interval '{argument}'\n", ExitCodes.Failure);
            }

            total += duration;
        }

        if (operands == 0)
        {
            return ExecResult.Error("sleep: missing operand\n", ExitCodes.Failure);
        }

        // A sleep longer than the remaining budget would only end in a timeout, so it is
        // clamped: the script still observes elapsed time, and the sandbox still ends.
        var remaining = context.Budget.Limits.Timeout - context.Budget.Elapsed;
        if (total > remaining)
        {
            total = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        if (total > TimeSpan.Zero)
        {
            await Task.Delay(total, cancellationToken);
        }

        return ExecResult.Success;
    }

    /// <summary>Parses a duration with an optional s/m/h/d suffix.</summary>
    internal static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        if (text.Length == 0)
        {
            return false;
        }

        var multiplier = 1.0;
        var numberPart = text;

        switch (text[^1])
        {
            case 's': numberPart = text[..^1]; break;
            case 'm': multiplier = 60; numberPart = text[..^1]; break;
            case 'h': multiplier = 3600; numberPart = text[..^1]; break;
            case 'd': multiplier = 86400; numberPart = text[..^1]; break;
        }

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(value * multiplier);
        return true;
    }
}

/// <summary><c>pushd</c>, <c>popd</c> and <c>dirs</c> — the directory stack.</summary>
public sealed class DirectoryStackBuiltin : IBuiltin
{
    private const string StackVariable = "DIRSTACK";

    /// <summary>Creates the builtin under <paramref name="name"/>.</summary>
    public DirectoryStackBuiltin(string name) => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        // The stack is kept in DIRSTACK, so it survives across calls and is visible to
        // scripts exactly as bash exposes it.
        var stack = context.State.Lookup(StackVariable)?.Elements.ToList() ?? [];

        switch (Name)
        {
            case "dirs":
            {
                if (context.Arguments.Contains("-c"))
                {
                    context.State.GetOrCreate(StackVariable).SetArray([]);
                    return ExecResult.Success;
                }

                return ExecResult.Ok(Render(context, stack, context.Arguments.Contains("-p") || context.Arguments.Contains("-v")));
            }

            case "pushd":
            {
                var operands = new ArgCursor(context.Arguments).DrainOperands();

                if (operands.Count == 0)
                {
                    // Bare `pushd` swaps the top two entries.
                    if (stack.Count == 0)
                    {
                        return ExecResult.Error("bash: pushd: no other directory\n", ExitCodes.Failure);
                    }

                    var swap = stack[0];
                    stack[0] = context.State.WorkingDirectory.Value;
                    var moved = await ChangeDirectoryAsync(context, swap, cancellationToken);
                    if (moved is { } error)
                    {
                        return error;
                    }

                    context.State.GetOrCreate(StackVariable).SetArray(stack);
                    return ExecResult.Ok(Render(context, stack, verbose: false));
                }

                var previous = context.State.WorkingDirectory.Value;
                var failure = await ChangeDirectoryAsync(context, operands[0], cancellationToken);
                if (failure is { } changeError)
                {
                    return changeError;
                }

                stack.Insert(0, previous);
                context.State.GetOrCreate(StackVariable).SetArray(stack);
                return ExecResult.Ok(Render(context, stack, verbose: false));
            }

            case "popd":
            {
                if (stack.Count == 0)
                {
                    return ExecResult.Error("bash: popd: directory stack empty\n", ExitCodes.Failure);
                }

                var target = stack[0];
                stack.RemoveAt(0);

                var failure = await ChangeDirectoryAsync(context, target, cancellationToken);
                if (failure is { } popError)
                {
                    return popError;
                }

                context.State.GetOrCreate(StackVariable).SetArray(stack);
                return ExecResult.Ok(Render(context, stack, verbose: false));
            }

            default:
                return ExecResult.Success;
        }
    }

    private static async ValueTask<ExecResult?> ChangeDirectoryAsync(BuiltinContext context, string target, CancellationToken cancellationToken)
    {
        var path = context.ResolvePath(target);

        try
        {
            if (!(await context.FileSystem.StatAsync(path, cancellationToken)).IsDirectory)
            {
                return ExecResult.Error($"bash: cd: {target}: Not a directory\n", ExitCodes.Failure);
            }
        }
        catch (FileSystemException)
        {
            return ExecResult.Error($"bash: cd: {target}: No such file or directory\n", ExitCodes.Failure);
        }

        context.State.Set("OLDPWD", context.State.WorkingDirectory.Value);
        context.State.WorkingDirectory = path;
        context.State.Set("PWD", path.Value);
        return null;
    }

    private static string Render(BuiltinContext context, List<string> stack, bool verbose)
    {
        var entries = new List<string> { Abbreviate(context, context.State.WorkingDirectory.Value) };
        entries.AddRange(stack.Select(entry => Abbreviate(context, entry)));

        if (!verbose)
        {
            return string.Join(' ', entries) + "\n";
        }

        var builder = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append("  ").Append(entries[i]).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Replaces the home directory prefix with <c>~</c>, as bash prints it.</summary>
    private static string Abbreviate(BuiltinContext context, string path)
    {
        var home = context.State.Get("HOME");

        if (string.IsNullOrEmpty(home) || !path.StartsWith(home, StringComparison.Ordinal))
        {
            return path;
        }

        return path.Length == home.Length ? "~" : "~" + path[home.Length..];
    }
}
