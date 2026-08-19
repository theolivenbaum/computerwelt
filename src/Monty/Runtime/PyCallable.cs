using Monty.Compilation;

namespace Monty.Runtime;

/// <summary>Anything that can be called.</summary>
public abstract class PyCallable : PyObject
{
    /// <summary>The name reported in error messages and by <c>__name__</c>.</summary>
    public abstract string Name { get; }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) =>
        name is "__name__" or "__qualname__" ? new PyStr(Name) : null;
}

/// <summary>A function defined in Python.</summary>
public sealed class PyFunction : PyCallable
{
    /// <summary>Creates a function from compiled code and its captured cells.</summary>
    public PyFunction(CodeObject code, Dictionary<string, PyCell> closure, PyDict globals)
    {
        Code = code;
        Closure = closure;
        Globals = globals;
    }

    /// <summary>The compiled body.</summary>
    public CodeObject Code { get; }

    /// <summary>The cells captured from enclosing scopes.</summary>
    public Dictionary<string, PyCell> Closure { get; }

    /// <summary>The module globals this function was defined in.</summary>
    public PyDict Globals { get; }

    /// <summary>The instance bound to this function, when accessed through one.</summary>
    public PyObject? BoundSelf { get; init; }

    /// <inheritdoc />
    public override string Name => Code.Name;

    /// <inheritdoc />
    public override string TypeName => "function";

    /// <inheritdoc />
    public override string Repr() => $"<function {Code.Name}>";

    /// <summary>Returns a copy of this function bound to <paramref name="instance"/>.</summary>
    public PyFunction Bind(PyObject instance) =>
        new(Code, Closure, Globals) { BoundSelf = instance };
}

/// <summary>A function implemented in C#.</summary>
public sealed class PyBuiltinFunction : PyCallable
{
    private readonly Func<PyObject[], PyDict?, PyObject> _implementation;

    /// <summary>Creates a builtin from a delegate taking positional and keyword arguments.</summary>
    public PyBuiltinFunction(string name, Func<PyObject[], PyDict?, PyObject> implementation)
    {
        Name = name;
        _implementation = implementation;
    }

    /// <summary>Creates a builtin that takes only positional arguments.</summary>
    public PyBuiltinFunction(string name, Func<PyObject[], PyObject> implementation)
        : this(name, (arguments, _) => implementation(arguments))
    {
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string TypeName => "builtin_function_or_method";

    /// <inheritdoc />
    public override string Repr() => $"<built-in function {Name}>";

    /// <summary>Invokes the builtin.</summary>
    public PyObject Invoke(PyObject[] arguments, PyDict? keywords = null) =>
        _implementation(arguments, keywords);
}

/// <summary>A builtin bound to a receiver, as a method of a built-in type.</summary>
public sealed class PyBoundMethod : PyCallable
{
    private readonly Func<PyObject, PyObject[], PyDict?, PyObject> _implementation;

    /// <summary>Creates a bound method over <paramref name="receiver"/>.</summary>
    public PyBoundMethod(string name, PyObject receiver, Func<PyObject, PyObject[], PyDict?, PyObject> implementation)
    {
        Name = name;
        Receiver = receiver;
        _implementation = implementation;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>The object the method was reached through.</summary>
    public PyObject Receiver { get; }

    /// <inheritdoc />
    public override string TypeName => "builtin_function_or_method";

    /// <inheritdoc />
    public override string Repr() => $"<built-in method {Name} of {Receiver.TypeName} object>";

    /// <summary>Invokes the method.</summary>
    public PyObject Invoke(PyObject[] arguments, PyDict? keywords = null) =>
        _implementation(Receiver, arguments, keywords);
}

/// <summary>
/// A mutable box holding a variable captured by a closure.
/// </summary>
/// <remarks>
/// Closures capture the variable, not its value: an inner function sees a later assignment
/// by the outer one. A cell is the indirection that makes that true.
/// </remarks>
public sealed class PyCell
{
    /// <summary>The current value, or null while unbound.</summary>
    public PyObject? Value { get; set; }
}

/// <summary>A class defined in Python. Monty supports plain classes only.</summary>
public sealed class PyClass : PyCallable
{
    /// <summary>Creates a class from its namespace.</summary>
    public PyClass(string name, PyDict members)
    {
        Name = name;
        Members = members;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>The class-level namespace: methods and class attributes.</summary>
    public PyDict Members { get; }

    /// <inheritdoc />
    public override string TypeName => "type";

    /// <inheritdoc />
    public override string Repr() => $"<class '{Name}'>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        if (name == "__name__")
        {
            return new PyStr(Name);
        }

        return Members.TryGetValue(new PyStr(name), out var value) ? value : null;
    }

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        Members.Set(new PyStr(name), value);
        return true;
    }
}

/// <summary>An instance of a Python class.</summary>
public sealed class PyInstance : PyObject
{
    /// <summary>Creates an instance of <paramref name="type"/>.</summary>
    public PyInstance(PyClass type) => Class = type;

    /// <summary>The class this is an instance of.</summary>
    public PyClass Class { get; }

    /// <summary>The instance's own attributes.</summary>
    public PyDict Fields { get; } = new();

    /// <inheritdoc />
    public override string TypeName => Class.Name;

    /// <inheritdoc />
    public override string Repr() => $"<{Class.Name} object>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        if (Fields.TryGetValue(new PyStr(name), out var field))
        {
            return field;
        }

        var classAttribute = Class.GetAttribute(name);

        // A function found on the class becomes a bound method when reached through an
        // instance; that binding is what supplies `self`.
        return classAttribute is PyFunction method ? method.Bind(this) : classAttribute;
    }

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        Fields.Set(new PyStr(name), value);
        return true;
    }
}

/// <summary>An iterator over a materialized sequence of values.</summary>
public sealed class PyIterator : PyObject
{
    private readonly IEnumerator<PyObject> _enumerator;

    /// <summary>Creates an iterator over <paramref name="values"/>.</summary>
    public PyIterator(IEnumerable<PyObject> values) => _enumerator = values.GetEnumerator();

    /// <inheritdoc />
    public override string TypeName => "iterator";

    /// <inheritdoc />
    public override string Repr() => "<iterator>";

    /// <summary>Advances the iterator. Returns null when exhausted.</summary>
    public PyObject? Next() => _enumerator.MoveNext() ? _enumerator.Current : null;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate()
    {
        while (Next() is { } value)
        {
            yield return value;
        }
    }
}
