namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// A filter closed over the scope it was defined in.
/// </summary>
/// <remarks>
/// <see cref="Scope"/> is settable because a definition must be able to see itself: the
/// closure is created first, then the scope containing it is built, then the closure is
/// pointed at that scope. Without the cycle, <c>def r: ., (.[]? | r); r</c> could not
/// recurse.
/// </remarks>
internal sealed class JqClosure
{
    /// <summary>Creates a closure over <paramref name="body"/>.</summary>
    /// <param name="parameters">The parameter names; a <c>$</c> prefix means a value parameter.</param>
    /// <param name="body">The filter.</param>
    public JqClosure(IReadOnlyList<string> parameters, JqNode body)
    {
        Parameters = parameters;
        Body = body;
        Scope = JqScope.Empty;
    }

    /// <summary>The parameter names.</summary>
    public IReadOnlyList<string> Parameters { get; }

    /// <summary>The filter's body.</summary>
    public JqNode Body { get; }

    /// <summary>The scope the body is evaluated in.</summary>
    public JqScope Scope { get; set; }
}

/// <summary>
/// An immutable chain of variable and function bindings.
/// </summary>
/// <remarks>
/// Immutable and linked rather than a mutable dictionary because closures capture it:
/// <c>reduce</c> rebinding its accumulator must not be visible to a filter that captured
/// the scope before the fold started.
/// </remarks>
internal sealed class JqScope
{
    private readonly JqScope? _parent;
    private readonly string? _name;
    private readonly JsonValue? _value;
    private readonly JqClosure? _closure;

    private JqScope()
    {
    }

    private JqScope(JqScope parent, string name, JsonValue value)
    {
        _parent = parent;
        _name = name;
        _value = value;
    }

    private JqScope(JqScope parent, string name, JqClosure closure)
    {
        _parent = parent;
        _name = name;
        _closure = closure;
    }

    /// <summary>The empty scope.</summary>
    public static JqScope Empty { get; } = new();

    /// <summary>Returns a scope with <c>$name</c> bound to <paramref name="value"/>.</summary>
    public JqScope WithVariable(string name, JsonValue value) => new(this, "$" + name, value);

    /// <summary>Returns a scope with a filter bound under <paramref name="name"/>.</summary>
    public JqScope WithFunction(string name, int arity, JqClosure closure) =>
        new(this, Key(name, arity), closure);

    /// <summary>Looks up <c>$name</c>, returning null when it is unbound.</summary>
    public JsonValue? Variable(string name)
    {
        var key = "$" + name;

        for (var scope = this; scope is not null; scope = scope._parent)
        {
            if (scope._value is not null && string.Equals(scope._name, key, StringComparison.Ordinal))
            {
                return scope._value;
            }
        }

        return null;
    }

    /// <summary>Looks up a filter by name and arity, returning null when it is unbound.</summary>
    public JqClosure? Function(string name, int arity)
    {
        var key = Key(name, arity);

        for (var scope = this; scope is not null; scope = scope._parent)
        {
            if (scope._closure is not null && string.Equals(scope._name, key, StringComparison.Ordinal))
            {
                return scope._closure;
            }
        }

        return null;
    }

    private static string Key(string name, int arity) => $"{name}/{arity}";
}
