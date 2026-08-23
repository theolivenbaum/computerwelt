using System.Globalization;
using Computerwelt.Emulation.Bash.Parsing;

namespace Computerwelt.Emulation.Bash.Interpreter;

/// <summary>
/// Everything one shell session knows: variables, functions, aliases, options and
/// positional parameters.
/// </summary>
/// <remarks>
/// <para>
/// Scoping is dynamic, not lexical: a <c>local</c> in a caller is visible to every callee
/// until the caller returns. That is why scopes are a stack searched from the top rather
/// than a tree captured at definition time.
/// </para>
/// <para>
/// One instance belongs to one execution and is not shared, so nothing here is
/// synchronized. Subshells get a <see cref="Fork"/>ed copy precisely so their writes cannot
/// be observed by the parent.
/// </para>
/// </remarks>
public sealed class ShellState
{
    private readonly List<Scope> _scopes = [new Scope()];

    /// <summary>Creates a state with only the global scope.</summary>
    public ShellState()
    {
        Options = new ShellOptions();
    }

    /// <summary>The <c>set</c> and <c>shopt</c> flags.</summary>
    public ShellOptions Options { get; private set; }

    /// <summary>Defined shell functions, by name.</summary>
    public Dictionary<string, FunctionDef> Functions { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>Defined aliases, by name.</summary>
    public Dictionary<string, string> Aliases { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>Trap actions, keyed by signal name or pseudo-signal (<c>EXIT</c>, <c>ERR</c>, <c>DEBUG</c>).</summary>
    public Dictionary<string, string> Traps { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>Positional parameters <c>$1</c> onwards.</summary>
    public List<string> Positional { get; set; } = [];

    /// <summary><c>$0</c>.</summary>
    public string ScriptName { get; set; } = "bash";

    /// <summary>The current working directory.</summary>
    public VPath WorkingDirectory { get; set; } = VPath.Parse("/");

    /// <summary>The status of the last foreground command, <c>$?</c>.</summary>
    public int LastExitCode { get; set; }

    /// <summary>Per-stage statuses of the last pipeline, <c>PIPESTATUS</c>.</summary>
    public IReadOnlyList<int> PipeStatus { get; set; } = [];

    /// <summary>The names of functions currently executing, innermost first.</summary>
    public List<string> CallStack { get; } = [];

    /// <summary>
    /// True while a trap handler is running.
    /// </summary>
    /// <remarks>
    /// The flag lives on the state rather than on an interpreter because a handler runs in
    /// a fresh interpreter over the same state: an <c>ERR</c> handler that itself fails
    /// would otherwise re-enter itself until the nesting limit stopped it.
    /// </remarks>
    public bool InTrap { get; set; }

    /// <summary>
    /// Aliases currently being expanded.
    /// </summary>
    /// <remarks>
    /// An alias may name the command it expands to — <c>alias echo='echo foo'</c> is the
    /// usual way to add a default flag — so an alias is not re-expanded inside its own
    /// expansion. The set lives on the state because the expansion runs in a fresh
    /// interpreter over the same shell.
    /// </remarks>
    public HashSet<string> AliasesInProgress { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Descriptors above 2 that <c>exec N&gt;...</c> has opened, by number.
    /// </summary>
    /// <remarks>
    /// The value is the file the descriptor writes to, <see langword="null"/> for
    /// <c>/dev/null</c>, or <c>&amp;1</c>/<c>&amp;2</c> when it duplicates one of the
    /// standard streams. Only these three shapes are reachable without real descriptors,
    /// and together they cover what <c>N&gt;&amp;</c> in a script can ask for.
    /// </remarks>
    public Dictionary<int, string?> Descriptors { get; private set; } = [];

    /// <summary>
    /// Readable descriptors above 2, by number, holding what is left to read from each.
    /// </summary>
    /// <remarks>
    /// A coprocess has no pipe to hold its output, so the output is buffered here and
    /// <c>&lt;&amp;N</c> reads it. The buffer is consumed on read, which is what makes a
    /// second read see end of input rather than the same bytes again.
    /// </remarks>
    public Dictionary<int, string> InputDescriptors { get; private set; } = [];

    /// <summary>
    /// Where a descriptor that duplicates the enclosing standard output collects its
    /// writes.
    /// </summary>
    /// <remarks>
    /// <c>{ ...; } 3&gt;&amp;1 &gt;file</c> is the standard way to keep progress output on
    /// the terminal while the real output goes to a file: fd 3 captures stdout <i>before</i>
    /// the file redirection takes effect, so writes to it must escape that redirection.
    /// Buffering them separately is what lets a value-returning shell express that.
    /// </remarks>
    public Dictionary<int, System.Text.StringBuilder> DescriptorBuffers { get; private set; } = [];

    /// <summary>
    /// Variables the shell seeded at start-up rather than the script exporting them.
    /// </summary>
    /// <remarks>
    /// <c>env</c> and <c>printenv</c> report the environment a script has built, which
    /// starts empty — the shell's own <c>HOME</c>, <c>PATH</c> and friends are inherited by
    /// child shells but are not part of what the script put there. Exporting a name
    /// explicitly removes it from this set, so a re-exported <c>PATH</c> does show up.
    /// </remarks>
    public HashSet<string> ShellDefaults { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>When this shell started, which is what <c>$SECONDS</c> counts from.</summary>
    public DateTimeOffset StartedAt { get; } = TimeProvider.System.GetUtcNow();

    /// <summary>The source line being executed, which <c>$LINENO</c> reports.</summary>
    public int CurrentLine { get; set; } = 1;

    /// <summary>The number of scopes currently pushed beyond the global one.</summary>
    public int ScopeDepth => _scopes.Count - 1;

    /// <summary>Pushes a new local scope for a function call.</summary>
    public void PushScope() => _scopes.Add(new Scope());

    /// <summary>Pops the innermost local scope.</summary>
    public void PopScope()
    {
        if (_scopes.Count > 1)
        {
            _scopes.RemoveAt(_scopes.Count - 1);
        }
    }

    /// <summary>Looks up a variable, searching from the innermost scope outwards.</summary>
    public ShellVariable? Lookup(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGet(name, out var variable))
            {
                // A nameref forwards every read and write to the variable it names, and
                // that name may carry a subscript: `typeset -n ref='a[2]'`.
                if (variable.Attributes.HasFlag(VariableAttributes.NameRef) && variable.Value != name)
                {
                    var target = variable.Value;
                    var bracket = target.IndexOf('[', StringComparison.Ordinal);
                    return Lookup(bracket > 0 ? target[..bracket] : target);
                }

                return variable;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks a variable up for writing: the one returned belongs to this state alone.
    /// </summary>
    /// <remarks>
    /// A fork shares its parent's variables until one of them writes, so a caller that
    /// means to mutate what it finds must ask through here rather than through
    /// <see cref="Lookup"/>. What comes back is an unshared copy where sharing was in
    /// force, and the variable itself as it stands where it was not.
    /// </remarks>
    public ShellVariable? LookupForWrite(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (!_scopes[i].TryGet(name, out var found))
            {
                continue;
            }

            if (found.Attributes.HasFlag(VariableAttributes.NameRef) && found.Value != name)
            {
                var target = found.Value;
                var bracket = target.IndexOf('[', StringComparison.Ordinal);
                return LookupForWrite(bracket > 0 ? target[..bracket] : target);
            }

            return found;
        }

        return null;
    }

    /// <summary>Looks a variable up for writing without following a nameref.</summary>
    public ShellVariable? LookupRawForWrite(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGet(name, out var variable))
            {
                return variable;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a variable without following a nameref.
    /// </summary>
    /// <remarks>
    /// <c>typeset +n ref</c> has to clear the attribute on <c>ref</c> itself, and
    /// <c>${!ref}</c> has to read the name <c>ref</c> holds — both would otherwise operate
    /// on the variable it points at.
    /// </remarks>
    public ShellVariable? LookupRaw(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGet(name, out var variable))
            {
                return variable;
            }
        }

        return null;
    }

    /// <summary>Reads a variable's scalar value, or <see langword="null"/> when unset.</summary>
    public string? Get(string name) => Lookup(name)?.Value;

    /// <summary>Reads a variable's scalar value, or an empty string when unset.</summary>
    public string GetOrEmpty(string name) => Lookup(name)?.Value ?? string.Empty;

    /// <summary>True when the variable exists in any scope.</summary>
    public bool IsSet(string name) => Lookup(name) is { IsUnset: false };

    /// <summary>
    /// True when a variable reference is set, where the reference may name one element:
    /// <c>-v m[foo]</c> asks about the element, not the array.
    /// </summary>
    public bool IsSetReference(string reference)
    {
        var bracket = reference.IndexOf('[', StringComparison.Ordinal);

        if (bracket <= 0 || !reference.EndsWith(']'))
        {
            return IsSet(reference);
        }

        if (Lookup(reference[..bracket]) is not { } variable)
        {
            return false;
        }

        var key = reference[(bracket + 1)..^1].Trim('\'', '"');

        if (key is "@" or "*")
        {
            return variable.Elements.Count > 0;
        }

        return variable.GetElement(key) is not null;
    }

    /// <summary>
    /// Assigns a variable in the scope where it is already defined, or in the global scope
    /// when it is new. This is what makes assignment inside a function visible to the
    /// caller unless <c>local</c> was used.
    /// </summary>
    public void Set(string name, string value)
    {
        var target = FindDefiningScope(name) ?? _scopes[0];

        if (target.TryGet(name, out var existing))
        {
            if (existing.IsReadOnly)
            {
                throw new ShellException(ShellErrorKind.PermissionDenied, $"{name}: readonly variable");
            }

            existing.SetScalar(value);
            return;
        }

        var variable = ShellVariable.Scalar(value);
        if (Options.AllExport)
        {
            variable.Attributes |= VariableAttributes.Exported;
        }

        target.Define(name, variable);
    }

    /// <summary>Assigns a variable, creating it in the innermost scope (<c>local</c>).</summary>
    public ShellVariable SetLocal(string name, string? value)
    {
        var scope = _scopes[^1];
        if (!scope.TryGet(name, out var variable))
        {
            variable = new ShellVariable();
            scope.Define(name, variable);
        }

        if (value is not null)
        {
            variable.SetScalar(value);
        }

        return variable;
    }

    /// <summary>Gets an existing variable or creates it in the appropriate scope.</summary>
    public ShellVariable GetOrCreate(string name)
    {
        // A caller asking to create is a caller about to write, so what comes back is
        // never a variable this state shares with a fork.
        if (LookupForWrite(name) is { } existing)
        {
            return existing;
        }

        var variable = new ShellVariable();
        (FindDefiningScope(name) ?? _scopes[0]).Define(name, variable);
        return variable;
    }

    /// <summary>Removes a variable from whichever scope defines it.</summary>
    /// <remarks>
    /// Unsetting a nameref removes the variable it points at, not the reference — which is
    /// bash's rule and the reason <c>unset ref</c> and <c>unset -n ref</c> differ.
    /// </remarks>
    public bool Unset(string name)
    {
        if (LookupRaw(name) is { } reference
            && reference.Attributes.HasFlag(VariableAttributes.NameRef)
            && reference.Value != name)
        {
            return Unset(reference.Value);
        }

        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (!_scopes[i].TryGet(name, out var variable))
            {
                continue;
            }

            if (variable.IsReadOnly)
            {
                throw new ShellException(ShellErrorKind.PermissionDenied, $"{name}: cannot unset: readonly variable");
            }

            _scopes[i].Remove(name);
            return true;
        }

        return false;
    }

    /// <summary>Every variable name currently visible, innermost definitions winning.</summary>
    public IEnumerable<KeyValuePair<string, ShellVariable>> AllVariables()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            foreach (var pair in _scopes[i].Entries)
            {
                if (seen.Add(pair.Key))
                {
                    yield return pair;
                }
            }
        }
    }

    /// <summary>The environment handed to a command: every exported variable.</summary>
    public Dictionary<string, string> ExportedEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, variable) in AllVariables())
        {
            if (variable.IsExported)
            {
                env[name] = variable.Value;
            }
        }

        return env;
    }

    /// <summary>
    /// Reads a special parameter such as <c>$?</c> or <c>$#</c>, or <see langword="null"/>
    /// when <paramref name="name"/> is not special.
    /// </summary>
    public string? GetSpecial(string name)
    {
        switch (name)
        {
            case "?":
                return LastExitCode.ToString(CultureInfo.InvariantCulture);
            case "#":
                return Positional.Count.ToString(CultureInfo.InvariantCulture);
            case "0":
                return ScriptName;
            case "$":
                return "1"; // A single deterministic pid: there are no real processes here.
            case "!":
                return "0";
            case "*":
                return string.Join(FirstIfsCharacter(), Positional);
            case "@":
                return string.Join(' ', Positional);
            case "-":
                return CurrentFlags();

            // These are computed on every read rather than stored, which is what makes
            // `$RANDOM` differ between two expansions in the same command.
            case "RANDOM":
                return Random.Shared.Next(0, 32768).ToString(CultureInfo.InvariantCulture);

            case "SECONDS":
                return ((long)(TimeProvider.System.GetUtcNow() - StartedAt).TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture);

            case "LINENO":
                return CurrentLine.ToString(CultureInfo.InvariantCulture);

        }

        // `SHOPT_x` reports whether the single-letter `set` option x is on, which is how a
        // script asks about shell state without parsing `set -o` output.
        if (name.StartsWith("SHOPT_", StringComparison.Ordinal) && name.Length == 7)
        {
            return Options.GetByLetter(name[6]) is { } enabled
                ? enabled ? "1" : "0"
                : null;
        }

        if (name.Length > 0 && name.All(char.IsAsciiDigit))
        {
            var index = int.Parse(name, CultureInfo.InvariantCulture);
            return index >= 1 && index <= Positional.Count ? Positional[index - 1] : null;
        }

        return null;
    }

    /// <summary>The first character of <c>IFS</c>, used to join <c>$*</c>. Defaults to a space.</summary>
    public string FirstIfsCharacter()
    {
        var ifs = Get("IFS");
        if (ifs is null)
        {
            return " ";
        }

        return ifs.Length == 0 ? string.Empty : ifs[..1];
    }

    /// <summary>The value of <c>IFS</c>, defaulting to space, tab and newline.</summary>
    public string Ifs => Get("IFS") ?? " \t\n";

    private string CurrentFlags()
    {
        var flags = new System.Text.StringBuilder();
        if (Options.ErrExit)
        {
            flags.Append('e');
        }

        if (Options.NoUnset)
        {
            flags.Append('u');
        }

        if (Options.XTrace)
        {
            flags.Append('x');
        }

        if (Options.NoGlob)
        {
            flags.Append('f');
        }

        return flags.ToString();
    }

    private Scope? FindDefiningScope(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].Contains(name))
            {
                return _scopes[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Produces an independent copy for a subshell. Writes to the copy must not be visible
    /// to the parent, which is exactly what <c>( ... )</c> guarantees.
    /// </summary>
    public ShellState Fork()
    {
        var fork = new ShellState
        {
            Options = Options.Clone(),
            Functions = new Dictionary<string, FunctionDef>(Functions, StringComparer.Ordinal),
            Aliases = new Dictionary<string, string>(Aliases, StringComparer.Ordinal),
            Traps = new Dictionary<string, string>(Traps, StringComparer.Ordinal),
            Positional = [.. Positional],
            ScriptName = ScriptName,
            WorkingDirectory = WorkingDirectory,
            LastExitCode = LastExitCode,
            PipeStatus = [.. PipeStatus],
            ShellDefaults = new HashSet<string>(ShellDefaults, StringComparer.Ordinal),
            Descriptors = new Dictionary<int, string?>(Descriptors),
            InputDescriptors = new Dictionary<int, string>(InputDescriptors),

            // The buffers are shared by reference: a subshell writing to an inherited
            // descriptor writes to the same place the parent will read.
            DescriptorBuffers = new Dictionary<int, System.Text.StringBuilder>(DescriptorBuffers),
        };

        // The scopes are shared, not copied: the two dozen seeded variables that a script
        // never writes are the bulk of them, and a command substitution that only reads is
        // the common case. Whichever side writes first takes its copy then — see
        // <see cref="Scope"/>.
        fork._scopes.Clear();
        foreach (var scope in _scopes)
        {
            fork._scopes.Add(scope.Share());
        }

        fork.CallStack.AddRange(CallStack);
        return fork;
    }

    /// <summary>
    /// One variable scope, which a subshell borrows from its parent rather than copying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Copying every variable on <see cref="Fork"/> was the largest cost a command
    /// substitution paid, and nearly all of it was waste: the two dozen seeded variables
    /// are rarely read and almost never written. A forked scope therefore starts empty and
    /// keeps a link to the scope it came from; a name it has not got is looked up along
    /// that chain and <i>copied in</i> on first touch, so what a fork hands out is always
    /// its own.
    /// </para>
    /// <para>
    /// Copying on read rather than on write is what makes this safe without auditing every
    /// caller: no reference to a parent's variable ever leaves this class, so no path can
    /// write through one. The chain is only ever read — <see cref="TryPeek"/> never
    /// modifies the scope it walks into — and it is as deep as the substitutions are
    /// nested, which the nesting limit bounds.
    /// </para>
    /// <para>
    /// The arrangement rests on a fork running to completion while its parent waits, which
    /// every caller of <see cref="Fork"/> does: there is no concurrency here, and a
    /// parent's later writes cannot reach a fork that has already finished.
    /// </para>
    /// </remarks>
    private sealed class Scope
    {
        private readonly Dictionary<string, ShellVariable> _own = new(StringComparer.Ordinal);
        private readonly Scope? _inherited;

        // Names this scope has unset that the chain behind it still defines.
        private HashSet<string>? _removed;

        public Scope()
        {
        }

        private Scope(Scope inherited) => _inherited = inherited;

        /// <summary>Hands a fork a scope that borrows from this one.</summary>
        public Scope Share() => new(this);

        /// <summary>True when the name is visible here or anywhere along the chain.</summary>
        public bool Contains(string name) => TryPeek(name, out _);

        /// <summary>
        /// Gets a variable this scope owns, copying it in from the chain if that is where
        /// it lives. What comes back may be mutated freely.
        /// </summary>
        public bool TryGet(string name, out ShellVariable variable)
        {
            if (_own.TryGetValue(name, out variable!))
            {
                return true;
            }

            if (_removed?.Contains(name) == true
                || _inherited is null
                || !_inherited.TryPeek(name, out var borrowed))
            {
                variable = null!;
                return false;
            }

            variable = borrowed.Clone();
            _own[name] = variable;
            return true;
        }

        /// <summary>Puts a variable in the scope, replacing any of the same name.</summary>
        public void Define(string name, ShellVariable variable)
        {
            _own[name] = variable;
            _removed?.Remove(name);
        }

        /// <summary>Removes a variable, hiding an inherited one of the same name.</summary>
        public bool Remove(string name)
        {
            var removed = _own.Remove(name);

            if (_inherited is not null && _removed?.Contains(name) != true && _inherited.TryPeek(name, out _))
            {
                (_removed ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
                removed = true;
            }

            return removed;
        }

        /// <summary>
        /// Every variable visible in this scope. A borrowed one is copied in as it is
        /// yielded, so an enumeration never hands out a parent's variable either.
        /// </summary>
        public IEnumerable<KeyValuePair<string, ShellVariable>> Entries =>
            _inherited is null ? _own : Materialised();

        // Reads along the chain without touching anything: the scopes behind this one
        // belong to other states, and a walk must leave them exactly as it found them.
        private bool TryPeek(string name, out ShellVariable variable)
        {
            if (_own.TryGetValue(name, out variable!))
            {
                return true;
            }

            if (_removed?.Contains(name) == true)
            {
                variable = null!;
                return false;
            }

            return _inherited is not null && _inherited.TryPeek(name, out variable);
        }

        private void CollectNames(List<string> names, HashSet<string> seen)
        {
            foreach (var name in _own.Keys)
            {
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }

            if (_removed is { } removed)
            {
                seen.UnionWith(removed);
            }

            _inherited?.CollectNames(names, seen);
        }

        private IEnumerable<KeyValuePair<string, ShellVariable>> Materialised()
        {
            var names = new List<string>(_own.Count);
            CollectNames(names, new HashSet<string>(StringComparer.Ordinal));

            foreach (var name in names)
            {
                if (TryGet(name, out var variable))
                {
                    yield return new KeyValuePair<string, ShellVariable>(name, variable);
                }
            }
        }
    }
}
