using Monty.Compilation;

namespace Monty.Runtime;

/// <summary>Anything that can be called.</summary>
public abstract class PyCallable : PyObject
{
    /// <summary>The name reported in error messages and by <c>__name__</c>.</summary>
    public abstract string Name { get; }

    /// <inheritdoc />
    /// <remarks>
    /// A callable is hashable by identity, which is what lets a function be a dict key —
    /// a dispatch table keyed by function is an ordinary thing to write.
    /// </remarks>
    public override System.Numerics.BigInteger PyHash() =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) =>
        name is "__name__" or "__qualname__" ? new PyStr(Name) : null;
}

/// <summary>A function defined in Python.</summary>
public sealed class PyFunction : PyCallable
{
    /// <summary>Creates a function from compiled code, its captured cells and its defaults.</summary>
    public PyFunction(CodeObject code, Dictionary<string, PyCell> closure, PyDict globals, PyDict? defaults = null)
    {
        Code = code;
        Closure = closure;
        Globals = globals;
        Defaults = defaults ?? new PyDict();
    }

    /// <summary>
    /// Default values, keyed by parameter name, evaluated once when the function was
    /// defined — which is why a mutable default is shared across calls.
    /// </summary>
    public PyDict Defaults { get; }

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
        new(Code, Closure, Globals, Defaults) { BoundSelf = instance };
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

    /// <summary>
    /// Creates a builtin that takes only positional arguments.
    /// </summary>
    /// <remarks>
    /// Passing a keyword to one of these is an error, not something to ignore: CPython's
    /// builtins written in C accept no keywords at all, and a script that spells an
    /// argument as a keyword needs to hear about it.
    /// </remarks>
    public PyBuiltinFunction(string name, Func<PyObject[], PyObject> implementation)
        : this(name, (arguments, keywords) =>
        {
            RejectKeywords(name, keywords);
            return implementation(arguments);
        })
    {
    }

    /// <summary>Raises when a positional-only builtin was given keyword arguments.</summary>
    internal static void RejectKeywords(string name, PyDict? keywords)
    {
        if (keywords is { Count: > 0 })
        {
            throw new PyRaise(PyErrors.TypeError($"{name}() takes no keyword arguments"));
        }
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

    /// <summary>Creates a bound method taking only positional arguments.</summary>
    public PyBoundMethod(string name, PyObject receiver, Func<PyObject, PyObject[], PyObject> implementation)
        : this(name, receiver, (self, arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords(name, keywords);
            return implementation(self, arguments);
        })
    {
    }

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

/// <summary>
/// An instance of a Python class.
/// </summary>
/// <remarks>
/// Every protocol the interpreter uses — truthiness, equality, hashing, iteration,
/// indexing, comparison — is answered by looking for the matching dunder on the class and
/// calling it. That is what makes a user class behave like a built-in one, and it is why
/// an instance holds a reference to the machine that will run those methods.
/// </remarks>
public sealed class PyInstance : PyObject
{
    private readonly VirtualMachine? _machine;

    /// <summary>Creates an instance of <paramref name="type"/>.</summary>
    public PyInstance(PyClass type, VirtualMachine? machine = null)
    {
        Class = type;
        _machine = machine;
    }

    /// <summary>The class this is an instance of.</summary>
    public PyClass Class { get; }

    /// <summary>The instance's own attributes.</summary>
    public PyDict Fields { get; } = new();

    /// <inheritdoc />
    public override string TypeName => Class.Name;

    /// <inheritdoc />
    public override string Repr() =>
        Dunder("__repr__") is { } repr
            ? Invoke(repr, []).Display()
            // The default repr names the class and the identity, as CPython's does; the
            // "address" is the same identity `id()` reports.
            : $"<{Class.Name} object at 0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):x8}>";

    /// <inheritdoc />
    public override string Display() =>
        Dunder("__str__") is { } str ? Invoke(str, []).Display() : Repr();

    /// <inheritdoc />
    public override bool IsTruthy()
    {
        if (Dunder("__bool__") is { } boolean)
        {
            return Invoke(boolean, []).IsTruthy();
        }

        // Without `__bool__`, a container is falsy when empty; anything else is true.
        return Dunder("__len__") is not { } length || Invoke(length, []).IsTruthy();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A <c>__eq__</c> that returns <c>NotImplemented</c> is declining to answer, not
    /// answering "true": the comparison then falls back to identity, which is what makes
    /// the usual <c>if not isinstance(other, C): return NotImplemented</c> guard work.
    /// </remarks>
    public override bool PyEquals(PyObject other)
    {
        if (Dunder("__eq__") is not { } equals)
        {
            return ReferenceEquals(this, other);
        }

        var result = Invoke(equals, [other]);

        return result is Builtins.NotImplementedSingleton ? ReferenceEquals(this, other) : result.IsTruthy();
    }

    /// <inheritdoc />
    public override System.Numerics.BigInteger PyHash()
    {
        // `__hash__ = None` is the explicit way to opt out, and a class defining `__eq__`
        // without `__hash__` opts out implicitly: equal objects would otherwise hash apart.
        if (Class.GetAttribute("__hash__") is PyNone
            || (Dunder("__hash__") is null && Dunder("__eq__") is not null))
        {
            throw new PyRaise(PyErrors.TypeError($"unhashable type: '{Class.Name}'"));
        }

        return Dunder("__hash__") is { } hash
            ? ((PyInt)Invoke(hash, [])).Value
            : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    /// <inheritdoc />
    public override int? PyCompare(PyObject other)
    {
        if (Dunder("__lt__") is { } less)
        {
            if (Invoke(less, [other]).IsTruthy())
            {
                return -1;
            }

            return PyEquals(other) ? 0 : 1;
        }

        return null;
    }

    /// <inheritdoc />
    public override int? Length() =>
        Dunder("__len__") is { } length ? ((PyInt)Invoke(length, [])).ToIndex() : null;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate()
    {
        if (Dunder("__iter__") is { } iterator)
        {
            return Drain(Invoke(iterator, []));
        }

        // The legacy protocol: `__getitem__` from 0 until IndexError.
        return Dunder("__getitem__") is null ? null : IterateByIndex();
    }

    /// <inheritdoc />
    public override bool Contains(PyObject item) =>
        Dunder("__contains__") is { } contains
            ? Invoke(contains, [item]).IsTruthy()
            : base.Contains(item);

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) =>
        Dunder("__getitem__") is { } getter
            ? Invoke(getter, [index])
            : throw new PyRaise(PyErrors.TypeError($"'{Class.Name}' object is not subscriptable"));

    /// <inheritdoc />
    public override void SetItem(PyObject index, PyObject value)
    {
        if (Dunder("__setitem__") is not { } setter)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"'{Class.Name}' object does not support item assignment"));
        }

        Invoke(setter, [index, value]);
    }

    /// <inheritdoc />
    public override void DeleteItem(PyObject index)
    {
        if (Dunder("__delitem__") is not { } deleter)
        {
            throw new PyRaise(PyErrors.TypeError($"'{Class.Name}' object doesn't support item deletion"));
        }

        Invoke(deleter, [index]);
    }

    /// <summary>Looks up a dunder method on the class, bound to this instance.</summary>
    public PyObject? Dunder(string name)
    {
        if (_machine is null)
        {
            return null;
        }

        var attribute = Class.GetAttribute(name);

        return attribute switch
        {
            PyFunction function => function.Bind(this),
            null => null,
            _ => attribute,
        };
    }

    /// <summary>Calls a bound dunder.</summary>
    public PyObject Invoke(PyObject callable, PyObject[] arguments) =>
        _machine is null
            ? throw new PyRaise(PyErrors.RuntimeError("no interpreter available"))
            // A builtin `__init__` installed by a decorator is unbound, so it needs the
            // receiver passed explicitly.
            : _machine.Call(callable, callable is PyBuiltinFunction ? [this, .. arguments] : arguments);

    private IEnumerable<PyObject> Drain(PyObject iterator)
    {
        if (iterator is PyIterator sequence)
        {
            while (sequence.Next() is { } value)
            {
                yield return value;
            }

            yield break;
        }

        if (iterator is PyGenerator generator)
        {
            while (generator.Next() is { } value)
            {
                yield return value;
            }

            yield break;
        }

        if (iterator is not PyInstance instance || instance.Dunder("__next__") is not { } next)
        {
            throw new PyRaise(PyErrors.TypeError("__iter__ returned a non-iterator"));
        }

        while (true)
        {
            PyObject value;

            try
            {
                value = instance.Invoke(next, []);
            }
            catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.StopIteration))
            {
                yield break;
            }

            yield return value;
        }
    }

    private IEnumerable<PyObject> IterateByIndex()
    {
        for (var i = 0; ; i++)
        {
            PyObject value;

            try
            {
                value = GetItem(new PyInt(i));
            }
            catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.IndexError))
            {
                yield break;
            }

            yield return value;
        }
    }

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
    /// <param name="values">What to iterate.</param>
    /// <param name="typeName">
    /// The Python type name, which differs per source: <c>iter([])</c> is a
    /// <c>list_iterator</c> and <c>iter("")</c> a <c>str_iterator</c>. Scripts do check.
    /// </param>
    public PyIterator(IEnumerable<PyObject> values, string typeName = "iterator")
    {
        _enumerator = values.GetEnumerator();
        TypeName = typeName;
    }

    /// <inheritdoc />
    public override string TypeName { get; }

    /// <inheritdoc />
    public override string Repr() => $"<{TypeName} object>";

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
