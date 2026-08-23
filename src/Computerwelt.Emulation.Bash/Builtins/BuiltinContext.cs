using Computerwelt.Emulation.Bash.Interpreter;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// Everything one invocation of a builtin can see.
/// </summary>
/// <remarks>
/// A builtin never reaches for global state: its filesystem, its arguments and its stdin
/// all arrive here. That is what lets two <see cref="Bash"/> instances run concurrently
/// against completely different worlds while sharing the same builtin objects.
/// </remarks>
public sealed class BuiltinContext
{
    /// <summary>Creates a context for one invocation.</summary>
    public BuiltinContext(
        string name,
        IReadOnlyList<string> arguments,
        ShellState state,
        IFileSystem fileSystem,
        ExecutionBudget budget,
        StreamData? stdin = null,
        ShellHooks? hooks = null)
    {
        Name = name;
        Arguments = arguments;
        State = state;
        FileSystem = fileSystem;
        Budget = budget;
        Stdin = stdin;
        _hooks = hooks;
    }

    private readonly ShellHooks? _hooks;

    /// <summary>
    /// The shell capabilities a builtin may call back into — running a script fragment,
    /// invoking another command, querying the registry.
    /// </summary>
    /// <remarks>
    /// Supplied per invocation by the dispatching interpreter rather than injected at
    /// registration, because a builtin must reach the interpreter that is <i>currently</i>
    /// running it: a subshell has its own state, and a callback captured at build time
    /// would silently address the wrong one.
    /// </remarks>
    public ShellHooks Hooks => _hooks
        ?? throw new InvalidOperationException($"{Name}: shell hooks are not available in this context");

    /// <summary>The name the command was invoked as.</summary>
    public string Name { get; }

    /// <summary>Arguments, excluding the command name.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>The live shell state. Most builtins only read it.</summary>
    public ShellState State { get; }

    /// <summary>The virtual filesystem.</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>The execution budget, for builtins whose work is unbounded in the input.</summary>
    public ExecutionBudget Budget { get; }

    /// <summary>Piped or redirected standard input, or <see langword="null"/> when there is none.</summary>
    public StreamData? Stdin { get; }

    /// <summary>
    /// The same input as a consumable stream, for the commands whose reads advance it.
    /// </summary>
    /// <remarks>
    /// Only <c>read</c> needs this, and it needs it badly: sharing one stream across a
    /// loop's iterations is what makes <c>while read line; do ...; done &lt; file</c>
    /// terminate instead of re-reading the first line forever.
    /// </remarks>
    public InputStream? Input { get; init; }

    /// <summary>The current working directory.</summary>
    public VPath WorkingDirectory => State.WorkingDirectory;

    /// <summary>Resolves an argument into an absolute virtual path.</summary>
    public VPath ResolvePath(string path) => VPath.Resolve(State.WorkingDirectory, path);

    /// <summary>Standard input decoded as text, or an empty string.</summary>
    public string StdinText => Stdin?.ToString() ?? string.Empty;

    /// <summary>The environment a child of this command would inherit.</summary>
    public IReadOnlyDictionary<string, string> Environment => State.ExportedEnvironment();

    /// <summary>Reads a file from the sandbox, decoded as UTF-8, resolving a relative path.</summary>
    /// <exception cref="ShellException">Thrown when there is no such file.</exception>
    public async ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
        System.Text.Encoding.UTF8.GetString(await FileSystem.ReadFileAsync(ResolvePath(path), cancellationToken));

    /// <summary>Writes a file to the sandbox as UTF-8, replacing it if it exists.</summary>
    public ValueTask WriteTextAsync(string path, string text, CancellationToken cancellationToken = default) =>
        FileSystem.WriteFileAsync(ResolvePath(path), System.Text.Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>Reads a file if it is there, or returns null — the shape most commands want.</summary>
    /// <remarks>
    /// A store a command creates on first use does not exist until then, and treating that
    /// as an error rather than as emptiness is where such commands usually go wrong.
    /// </remarks>
    public async ValueTask<string?> ReadTextIfExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);

        return await FileSystem.ExistsAsync(resolved, cancellationToken)
            ? System.Text.Encoding.UTF8.GetString(await FileSystem.ReadFileAsync(resolved, cancellationToken))
            : null;
    }

    /// <summary>
    /// Reads the operands as input: each named file in turn, or standard input when there
    /// are none. This is the <c>cat</c>/<c>wc</c>/<c>grep</c> convention, and centralizing it
    /// keeps the "no operands means stdin" rule from being re-derived in thirty places.
    /// </summary>
    public async ValueTask<List<(string Name, string Content)>> ReadOperandsAsync(
        IReadOnlyList<string> operands,
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string, string)>();

        if (operands.Count == 0)
        {
            results.Add(("-", StdinText));
            return results;
        }

        foreach (var operand in operands)
        {
            if (operand == "-")
            {
                results.Add(("-", StdinText));
                continue;
            }

            var bytes = await FileSystem.ReadFileAsync(ResolvePath(operand), cancellationToken);
            results.Add((operand, System.Text.Encoding.UTF8.GetString(bytes)));
        }

        return results;
    }
}
