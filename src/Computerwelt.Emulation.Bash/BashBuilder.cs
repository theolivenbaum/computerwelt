using Computerwelt.Emulation.Bash.Builtins;
using Computerwelt.Emulation.Bash.Interpreter;

namespace Computerwelt.Emulation.Bash;

/// <summary>
/// Configures a <see cref="Bash"/> session.
/// </summary>
/// <remarks>
/// Everything a session can reach is decided here and cannot be widened afterwards. A
/// capability that was never registered — a filesystem backend, a network allowlist, a
/// custom command — is simply absent from the running shell rather than present and
/// guarded, which is a much stronger property than a runtime permission check.
/// </remarks>
public sealed class BashBuilder
{
    private readonly Dictionary<string, IBuiltin> _builtins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _withheld = new(StringComparer.Ordinal);
    private readonly List<ICommandResolver> _resolvers = [];
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);
    private IFileSystem? _fileSystem;
    private ExecutionLimits _limits = ExecutionLimits.Default;
    private FsLimits _fileSystemLimits = FsLimits.Default;
    private VPath _workingDirectory = VPath.Parse("/");
    private TimeProvider _timeProvider = TimeProvider.System;
    private string _username = "user";
    private string _hostname = "sandbox";
    private bool _registerDefaults = true;

    /// <summary>Uses <paramref name="fileSystem"/> as the session's storage.</summary>
    public BashBuilder WithFileSystem(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        return this;
    }

    /// <summary>Sets the quota for the default in-memory filesystem.</summary>
    public BashBuilder WithFileSystemLimits(FsLimits limits)
    {
        _fileSystemLimits = limits;
        return this;
    }

    /// <summary>
    /// Sets the clock the session reads. Pin it to make runs reproducible and to keep the
    /// host's wall clock from becoming a fingerprinting channel.
    /// </summary>
    public BashBuilder WithTimeProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        return this;
    }

    /// <summary>Sets the per-execution resource limits.</summary>
    public BashBuilder WithLimits(ExecutionLimits limits)
    {
        _limits = limits;
        return this;
    }

    /// <summary>Sets an exported environment variable.</summary>
    public BashBuilder WithEnvironmentVariable(string name, string value)
    {
        _environment[name] = value;
        return this;
    }

    /// <summary>Sets the initial working directory.</summary>
    public BashBuilder WithWorkingDirectory(string path)
    {
        _workingDirectory = VPath.Parse(path);
        return this;
    }

    /// <summary>Sets the user name reported by <c>whoami</c> and <c>$USER</c>.</summary>
    public BashBuilder WithUsername(string username)
    {
        _username = username;
        return this;
    }

    /// <summary>Sets the host name reported by <c>hostname</c> and <c>$HOSTNAME</c>.</summary>
    public BashBuilder WithHostname(string hostname)
    {
        _hostname = hostname;
        return this;
    }

    /// <summary>Registers a custom command, replacing any builtin of the same name.</summary>
    /// <remarks>
    /// The instance is shared by every execution of the session, so it must be thread-safe
    /// and hold no per-invocation state — see <see cref="IBuiltin"/>.
    /// </remarks>
    public BashBuilder WithBuiltin(IBuiltin builtin)
    {
        ArgumentNullException.ThrowIfNull(builtin);
        _builtins[builtin.Name] = builtin;
        return this;
    }

    /// <summary>Registers a custom command implemented by a delegate.</summary>
    /// <param name="name">The name the command is typed as.</param>
    /// <param name="run">The implementation, which must be thread-safe.</param>
    /// <param name="llmHint">The one-line capability summary, or null to omit it from the prompt.</param>
    /// <param name="help">The text <c>--help</c> shows, if the command answers it.</param>
    public BashBuilder WithBuiltin(
        string name,
        Func<BuiltinContext, CancellationToken, ValueTask<ExecResult>> run,
        string? llmHint = null,
        string? help = null) =>
        WithBuiltin(new DelegateBuiltin(name, run, llmHint, help));

    /// <summary>Registers a custom command implemented by a synchronous delegate.</summary>
    /// <param name="name">The name the command is typed as.</param>
    /// <param name="run">The implementation, which must be thread-safe.</param>
    /// <param name="llmHint">The one-line capability summary, or null to omit it from the prompt.</param>
    /// <param name="help">The text <c>--help</c> shows, if the command answers it.</param>
    public BashBuilder WithBuiltin(
        string name,
        Func<BuiltinContext, ExecResult> run,
        string? llmHint = null,
        string? help = null) =>
        WithBuiltin(new DelegateBuiltin(name, run, llmHint, help));

    /// <summary>Registers several custom commands. A later name replaces an earlier one.</summary>
    public BashBuilder WithBuiltins(params IBuiltin[] builtins) =>
        WithBuiltins((IEnumerable<IBuiltin>)builtins);

    /// <summary>Registers several custom commands. A later name replaces an earlier one.</summary>
    public BashBuilder WithBuiltins(IEnumerable<IBuiltin> builtins)
    {
        ArgumentNullException.ThrowIfNull(builtins);

        foreach (var builtin in builtins)
        {
            WithBuiltin(builtin);
        }

        return this;
    }

    /// <summary>
    /// Registers a related set of commands as one unit.
    /// </summary>
    /// <remarks>
    /// The unit is the point: a host grants a domain vocabulary or withholds it, and the
    /// grant can be read as one line rather than reconstructed from a pile of individual
    /// registrations. Its commands override earlier ones of the same name, exactly as
    /// <see cref="WithBuiltin(IBuiltin)"/> does.
    /// </remarks>
    public BashBuilder WithExtension(IShellExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return WithBuiltins(extension.Builtins);
    }

    /// <summary>
    /// Installs a last-chance resolver for command names nothing else matched.
    /// </summary>
    /// <remarks>
    /// Resolvers are consulted in registration order, and only after shell functions,
    /// registered commands and the search for a script have all missed — so a resolver
    /// extends the vocabulary without being able to shadow any of them. See
    /// <see cref="ICommandResolver"/>.
    /// </remarks>
    public BashBuilder WithCommandResolver(ICommandResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolvers.Add(resolver);
        return this;
    }

    /// <summary>Installs a last-chance resolver implemented by a delegate.</summary>
    public BashBuilder WithCommandResolver(Func<string, IBuiltin?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return WithCommandResolver(new DelegateCommandResolver(resolve));
    }

    /// <summary>
    /// Withholds one command, so the session has no such name at all.
    /// </summary>
    /// <remarks>
    /// Applied after every registration, including this builder's own defaults and anything
    /// an extension contributed, and it cannot be undone by a later call. A withheld name is
    /// absent rather than present and refusing, which is the same posture the rest of the
    /// sandbox takes — and it stays absent to <c>type</c>, <c>command -v</c> and
    /// <see cref="Bash.BuiltinNames"/>, so a script cannot discover a capability it does not
    /// have. It does not bind an <see cref="ICommandResolver"/>: a resolver is the host's own
    /// answer to a name, so a host that withholds a command must not then resolve it.
    /// </remarks>
    public BashBuilder WithoutBuiltin(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _withheld.Add(name);
        return this;
    }

    /// <summary>Withholds several commands.</summary>
    public BashBuilder WithoutBuiltins(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        foreach (var name in names)
        {
            WithoutBuiltin(name);
        }

        return this;
    }

    /// <summary>
    /// Suppresses registration of the standard command set, leaving only explicitly
    /// registered commands. Use for a host that wants a strictly bounded vocabulary.
    /// </summary>
    public BashBuilder WithoutDefaultBuiltins()
    {
        _registerDefaults = false;
        return this;
    }

    /// <summary>Builds the session.</summary>
    public Bash Build()
    {
        var fileSystem = _fileSystem ?? new InMemoryFileSystem(_fileSystemLimits, _timeProvider);
        var state = new ShellState { WorkingDirectory = _workingDirectory };

        SeedEnvironment(state);

        var registry = new Dictionary<string, IBuiltin>(StringComparer.Ordinal);
        var bash = new Bash(state, fileSystem, _limits, new CommandTable(registry, _resolvers));

        if (_registerDefaults)
        {
            RegisterDefaults(registry);
        }

        foreach (var (name, builtin) in _builtins)
        {
            registry[name] = builtin;
        }

        // Withholding is applied last, so it wins over every registration — including one
        // an extension made — rather than depending on the order the host called things in.
        foreach (var name in _withheld)
        {
            registry.Remove(name);
        }

        return bash;
    }

    private void SeedEnvironment(ShellState state)
    {
        var home = $"/home/{_username}";

        var defaults = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["USER"] = _username,
            ["LOGNAME"] = _username,
            ["HOSTNAME"] = _hostname,
            ["PWD"] = _workingDirectory.Value,
            ["SHELL"] = "/bin/bash",
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["IFS"] = " \t\n",
            ["PS1"] = @"\u@\h:\w\$ ",
            ["PS2"] = "> ",
            ["PS4"] = "+ ",
            ["BASH"] = "/bin/bash",
            ["BASH_VERSION"] = "5.2.0(1)-release",
            ["LANG"] = "C.UTF-8",
            ["TERM"] = "dumb",
            ["UID"] = "1000",
            ["EUID"] = "1000",
            ["PPID"] = "1",
            ["BASHPID"] = "1",
            ["SHLVL"] = "1",
            ["OSTYPE"] = "linux-gnu",
            ["MACHTYPE"] = "x86_64-pc-linux-gnu",
            ["HOSTTYPE"] = "x86_64",
        };

        foreach (var (name, value) in defaults)
        {
            state.Set(name, value);
            state.GetOrCreate(name).Attributes |= VariableAttributes.Exported;

            // Seeded, not exported by the script: `env` starts empty even though a child
            // shell still inherits these.
            state.ShellDefaults.Add(name);
        }

        // `BASH_VERSINFO` is an array, so it cannot come from the scalar table above.
        state.GetOrCreate("BASH_VERSINFO").SetArray(["5", "2", "0", "1", "release", "x86_64-pc-linux-gnu"]);
        state.ShellDefaults.Add("BASH_VERSINFO");

        // IFS is deliberately not exported, matching bash.
        state.GetOrCreate("IFS").Attributes &= ~VariableAttributes.Exported;

        foreach (var (name, value) in _environment)
        {
            state.Set(name, value);
            state.GetOrCreate(name).Attributes |= VariableAttributes.Exported;
        }
    }

    private void RegisterDefaults(Dictionary<string, IBuiltin> registry)
    {
        void Register(IBuiltin builtin) => registry[builtin.Name] = builtin;

        Register(new EchoBuiltin());
        Register(new PrintfBuiltin());
        Register(new TrueBuiltin());
        Register(new FalseBuiltin());
        Register(new ColonBuiltin());
        Register(new ExitBuiltin());
        Register(new ReturnBuiltin());
        Register(new CallerBuiltin());
        Register(new WaitBuiltin());
        Register(new BreakBuiltin());
        Register(new ContinueBuiltin());

        Register(new CdBuiltin());
        Register(new PwdBuiltin());

        Register(new ExportBuiltin());
        Register(new UnsetBuiltin());
        Register(new ReadOnlyBuiltin());
        Register(new LocalBuiltin());
        Register(new DeclareBuiltin());
        Register(new DeclareBuiltin("typeset"));
        Register(new ShiftBuiltin());
        Register(new SetBuiltin());
        Register(new ShoptBuiltin());
        Register(new ReadBuiltin());
        Register(new MapfileBuiltin());
        Register(new MapfileBuiltin("readarray"));

        Register(new TestBuiltin());
        Register(new TestBuiltin("["));

        Register(new CatBuiltin());
        Register(new LsBuiltin());
        Register(new MkdirBuiltin());
        Register(new RmBuiltin());
        Register(new CpBuiltin());
        Register(new MvBuiltin());
        Register(new TouchBuiltin());

        Register(new HeadBuiltin());
        Register(new TailBuiltin());
        Register(new WcBuiltin());
        Register(new SortBuiltin());
        Register(new UniqBuiltin());
        Register(new RevBuiltin());
        Register(new TacBuiltin());
        Register(new SeqBuiltin());
        Register(new YesBuiltin());

        Register(new BasenameBuiltin());
        Register(new DirnameBuiltin());
        Register(new EnvBuiltin());
        Register(new EnvBuiltin("printenv"));
        Register(new ExprBuiltin());
        Register(new AliasBuiltin());
        Register(new UnaliasBuiltin());
        Register(new TypeBuiltin());
        Register(new EvalBuiltin());
        Register(new ExecBuiltin());

        Register(new GrepBuiltin());
        Register(new GrepBuiltin("egrep"));
        Register(new GrepBuiltin("fgrep"));
        Register(new SedBuiltin());
        Register(new RipgrepBuiltin());
        Register(new AwkBuiltin());
        Register(new AwkBuiltin("gawk"));
        Register(new AwkBuiltin("mawk"));
        Register(new JqBuiltin());
        Register(new YqBuiltin());
        Register(new CutBuiltin());
        Register(new TrBuiltin());
        Register(new NlBuiltin());
        Register(new PasteBuiltin());
        Register(new TeeBuiltin());
        Register(new XargsBuiltin());
        Register(new FindBuiltin());

        Register(new BcBuiltin());
        Register(new CompgenBuiltin());
        Register(new NumfmtBuiltin());
        Register(new CommBuiltin());
        Register(new OdBuiltin());
        Register(new XxdBuiltin());
        Register(new ShufBuiltin());
        Register(new DfBuiltin());
        Register(new DuBuiltin());
        Register(new FileBuiltin());
        Register(new StringsBuiltin());
        Register(new ColumnBuiltin());
        Register(new TreeBuiltin());
        Register(new PagerBuiltin());
        Register(new PagerBuiltin("more"));
        Register(new WatchBuiltin());
        Register(new HistoryBuiltin());
        Register(new DiffBuiltin());
        Register(new TarBuiltin());
        Register(new ChownBuiltin());
        Register(new ChownBuiltin("chgrp"));
        Register(new KillBuiltin());

        foreach (var checksum in ChecksumBuiltin.All())
        {
            Register(checksum);
        }

        Register(new RealpathBuiltin());
        Register(new ReadlinkBuiltin());
        Register(new LnBuiltin());
        Register(new ChmodBuiltin());
        Register(new StatBuiltin());
        Register(new TruncateBuiltin());
        Register(new MktempBuiltin());
        Register(new RmdirBuiltin());

        Register(new IdentityBuiltin("whoami"));
        Register(new IdentityBuiltin("id"));
        Register(new IdentityBuiltin("hostname"));
        Register(new IdentityBuiltin("uname"));
        Register(new SleepBuiltin());
        Register(new DateBuiltin(_timeProvider));
        Register(new TimeoutBuiltin());
        Register(new DirectoryStackBuiltin("pushd"));
        Register(new DirectoryStackBuiltin("popd"));
        Register(new DirectoryStackBuiltin("dirs"));

        Register(new BashBuiltin());
        Register(new BashBuiltin("sh"));
        Register(new SourceBuiltin());
        Register(new SourceBuiltin("."));
        Register(new CommandBuiltin());
        Register(new WhichBuiltin());
        Register(new HashBuiltin());
        Register(new GetoptsBuiltin());
        Register(new LetBuiltin());
        Register(new TrapBuiltin());
    }
}
