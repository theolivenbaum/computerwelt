using System.Numerics;
using System.Text;

namespace Monty.Runtime;

/// <summary>A Python exception instance.</summary>
public sealed class PyException : PyObject
{
    /// <summary>Creates an exception of <paramref name="type"/>.</summary>
    public PyException(PyExceptionType type, string message, IReadOnlyList<PyObject>? arguments = null)
    {
        ExceptionType = type;
        Message = message;
        Arguments = arguments ?? (message.Length > 0 ? [new PyStr(message)] : []);
    }

    /// <summary>Which exception class this is.</summary>
    public PyExceptionType ExceptionType { get; }

    /// <summary>The message, as <c>str(e)</c> shows it.</summary>
    public string Message { get; }

    /// <summary><c>e.args</c>.</summary>
    public IReadOnlyList<PyObject> Arguments { get; }

    /// <summary>The exception being handled when this one was raised (<c>__context__</c>).</summary>
    public PyException? Context { get; set; }

    /// <summary>The explicit <c>raise ... from ...</c> cause.</summary>
    public PyException? Cause { get; set; }

    /// <summary>Traceback frames, innermost last.</summary>
    public List<TracebackFrame> Traceback { get; } = [];

    /// <inheritdoc />
    public override string TypeName => ExceptionType.Name;

    /// <inheritdoc />
    public override string Display() => Message;

    /// <inheritdoc />
    public override string Repr() =>
        Arguments.Count == 0
            ? $"{ExceptionType.Name}()"
            : $"{ExceptionType.Name}({string.Join(", ", Arguments.Select(static a => a.Repr()))})";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "args" => new PyTuple([.. Arguments]),
        "__context__" => (PyObject?)Context ?? PyNone.Instance,
        "__cause__" => (PyObject?)Cause ?? PyNone.Instance,
        _ => null,
    };

    /// <summary>True when this exception is an instance of <paramref name="type"/> or a subtype.</summary>
    public bool IsInstanceOf(PyExceptionType type) => ExceptionType.IsSubclassOf(type);

    /// <summary>Formats the traceback the way CPython prints it to stderr.</summary>
    public string FormatTraceback(string fileName)
    {
        var builder = new StringBuilder();

        if (Cause is { } cause)
        {
            builder.Append(cause.FormatTraceback(fileName));
            builder.Append("\nThe above exception was the direct cause of the following exception:\n\n");
        }
        else if (Context is { } context)
        {
            builder.Append(context.FormatTraceback(fileName));
            builder.Append("\nDuring handling of the above exception, another exception occurred:\n\n");
        }

        builder.Append("Traceback (most recent call last):\n");

        foreach (var frame in Traceback)
        {
            builder.Append("  File \"").Append(fileName).Append("\", line ")
                .Append(frame.Line).Append(", in ").Append(frame.FunctionName).Append('\n');

            if (frame.SourceLine is { Length: > 0 } source)
            {
                builder.Append("    ").Append(source.Trim()).Append('\n');
            }
        }

        builder.Append(ExceptionType.Name);

        if (Message.Length > 0)
        {
            builder.Append(": ").Append(Message);
        }

        return builder.ToString();
    }
}

/// <summary>One traceback entry.</summary>
/// <param name="FunctionName">The function the frame was executing, or <c>&lt;module&gt;</c>.</param>
/// <param name="Line">The 1-based source line.</param>
/// <param name="SourceLine">The text of that line, when the source is available.</param>
public readonly record struct TracebackFrame(string FunctionName, int Line, string? SourceLine);

/// <summary>
/// An exception class.
/// </summary>
/// <remarks>
/// Exception classes form the one inheritance hierarchy Monty supports, because
/// <c>except ValueError</c> has to catch <c>ValueError</c> subclasses to be useful at all.
/// User classes remain flat, as upstream documents.
/// </remarks>
public sealed class PyExceptionType : PyObject
{
    private PyExceptionType(string name, PyExceptionType? baseType, PyExceptionType? secondBase = null)
    {
        Name = name;
        BaseType = baseType;
        SecondBase = secondBase;
    }

    /// <summary>The class name.</summary>
    public string Name { get; }

    /// <summary>The base class, or null for <c>BaseException</c>.</summary>
    public PyExceptionType? BaseType { get; }

    /// <summary>
    /// A second base, for the one class in the hierarchy that has two.
    /// </summary>
    /// <remarks>
    /// <c>io.UnsupportedOperation</c> derives from both <c>OSError</c> and
    /// <c>ValueError</c> in CPython, and scripts catch it either way.
    /// </remarks>
    public PyExceptionType? SecondBase { get; }

    /// <inheritdoc />
    public override string TypeName => "type";

    /// <inheritdoc />
    public override string Repr() => $"<class '{Name}'>";

    /// <inheritdoc />
    /// <remarks>A class is a first-class value, hashable by identity like any other.</remarks>
    public override System.Numerics.BigInteger PyHash() =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) =>
        name == "__name__" ? new PyStr(Name) : null;

    /// <summary>True when this class is <paramref name="other"/> or derives from it.</summary>
    public bool IsSubclassOf(PyExceptionType other)
    {
        for (var current = this; current is not null; current = current.BaseType)
        {
            if (ReferenceEquals(current, other) || current.SecondBase?.IsSubclassOf(other) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static PyExceptionType Define(string name, PyExceptionType? baseType, PyExceptionType? secondBase = null)
    {
        var type = new PyExceptionType(name, baseType, secondBase);
        Registry[name] = type;
        return type;
    }

    /// <summary>Every built-in exception class, by name.</summary>
    public static Dictionary<string, PyExceptionType> Registry { get; } = new(StringComparer.Ordinal);

    /// <summary><c>BaseException</c>.</summary>
    public static PyExceptionType BaseException { get; } = Define("BaseException", null);

    /// <summary><c>Exception</c>.</summary>
    public static PyExceptionType Exception { get; } = Define("Exception", BaseException);

    /// <summary><c>ArithmeticError</c>.</summary>
    public static PyExceptionType ArithmeticError { get; } = Define("ArithmeticError", Exception);

    /// <summary><c>LookupError</c>.</summary>
    public static PyExceptionType LookupError { get; } = Define("LookupError", Exception);

    /// <summary><c>ValueError</c>.</summary>
    public static PyExceptionType ValueError { get; } = Define("ValueError", Exception);

    /// <summary><c>TypeError</c>.</summary>
    public static PyExceptionType TypeError { get; } = Define("TypeError", Exception);

    /// <summary><c>json.JSONDecodeError</c>, which is a <c>ValueError</c>.</summary>
    public static PyExceptionType JsonDecodeError { get; } = Define("JSONDecodeError", ValueError);

    /// <summary><c>NameError</c>.</summary>
    public static PyExceptionType NameError { get; } = Define("NameError", Exception);

    /// <summary><c>UnboundLocalError</c>.</summary>
    public static PyExceptionType UnboundLocalError { get; } = Define("UnboundLocalError", NameError);

    /// <summary><c>AttributeError</c>.</summary>
    public static PyExceptionType AttributeError { get; } = Define("AttributeError", Exception);

    /// <summary><c>IndexError</c>.</summary>
    public static PyExceptionType IndexError { get; } = Define("IndexError", LookupError);

    /// <summary><c>KeyError</c>.</summary>
    public static PyExceptionType KeyError { get; } = Define("KeyError", LookupError);

    /// <summary><c>ZeroDivisionError</c>.</summary>
    public static PyExceptionType ZeroDivisionError { get; } = Define("ZeroDivisionError", ArithmeticError);

    /// <summary><c>OverflowError</c>.</summary>
    public static PyExceptionType OverflowError { get; } = Define("OverflowError", ArithmeticError);

    /// <summary><c>StopIteration</c>.</summary>
    public static PyExceptionType StopIteration { get; } = Define("StopIteration", Exception);

    /// <summary><c>AssertionError</c>.</summary>
    public static PyExceptionType AssertionError { get; } = Define("AssertionError", Exception);

    /// <summary><c>RuntimeError</c>.</summary>
    public static PyExceptionType RuntimeError { get; } = Define("RuntimeError", Exception);

    /// <summary><c>RecursionError</c>.</summary>
    public static PyExceptionType RecursionError { get; } = Define("RecursionError", RuntimeError);

    /// <summary><c>NotImplementedError</c>.</summary>
    public static PyExceptionType NotImplementedError { get; } = Define("NotImplementedError", RuntimeError);

    /// <summary><c>ImportError</c>.</summary>
    public static PyExceptionType ImportError { get; } = Define("ImportError", Exception);

    /// <summary><c>ModuleNotFoundError</c>.</summary>
    public static PyExceptionType ModuleNotFoundError { get; } = Define("ModuleNotFoundError", ImportError);

    /// <summary><c>OSError</c>.</summary>
    public static PyExceptionType OSError { get; } = Define("OSError", Exception);

    /// <summary><c>FileNotFoundError</c>.</summary>
    public static PyExceptionType FileNotFoundError { get; } = Define("FileNotFoundError", OSError);

    /// <summary><c>FileExistsError</c>.</summary>
    public static PyExceptionType FileExistsError { get; } = Define("FileExistsError", OSError);

    /// <summary><c>IsADirectoryError</c>.</summary>
    public static PyExceptionType IsADirectoryError { get; } = Define("IsADirectoryError", OSError);

    /// <summary><c>NotADirectoryError</c>.</summary>
    public static PyExceptionType NotADirectoryError { get; } = Define("NotADirectoryError", OSError);

    /// <summary>
    /// <c>io.UnsupportedOperation</c>, raised by an operation the file's mode forbids.
    /// </summary>
    public static PyExceptionType UnsupportedOperation { get; } =
        Define("io.UnsupportedOperation", OSError, ValueError);

    /// <summary><c>PermissionError</c>.</summary>
    public static PyExceptionType PermissionError { get; } = Define("PermissionError", OSError);

    /// <summary><c>re.PatternError</c>, also spelled <c>re.error</c>.</summary>
    public static PyExceptionType PatternError { get; } = Define("PatternError", ValueError);

    /// <summary><c>UnicodeError</c>.</summary>
    public static PyExceptionType UnicodeError { get; } = Define("UnicodeError", ValueError);

    /// <summary><c>UnicodeDecodeError</c>.</summary>
    public static PyExceptionType UnicodeDecodeError { get; } = Define("UnicodeDecodeError", UnicodeError);

    /// <summary><c>UnicodeEncodeError</c>.</summary>
    public static PyExceptionType UnicodeEncodeError { get; } = Define("UnicodeEncodeError", UnicodeError);

    /// <summary><c>KeyboardInterrupt</c>.</summary>
    public static PyExceptionType KeyboardInterrupt { get; } = Define("KeyboardInterrupt", BaseException);

    /// <summary><c>SystemExit</c>.</summary>
    public static PyExceptionType SystemExit { get; } = Define("SystemExit", BaseException);

    /// <summary><c>MemoryError</c>.</summary>
    public static PyExceptionType MemoryError { get; } = Define("MemoryError", Exception);
}

/// <summary>
/// The .NET exception that carries a Python exception up the call stack.
/// </summary>
/// <remarks>
/// Using a .NET exception for Python's <c>raise</c> means <c>finally</c> blocks in the
/// interpreter's own C# unwind correctly, and it keeps the VM loop from threading an error
/// flag through every instruction.
/// </remarks>
public sealed class PyRaise : Exception
{
    /// <summary>Wraps a Python exception.</summary>
    public PyRaise(PyException exception) : base(exception.Message) => Exception = exception;

    /// <summary>The Python exception being raised.</summary>
    public PyException Exception { get; }
}

/// <summary>Factories for the built-in exceptions, with CPython's message wording.</summary>
public static class PyErrors
{
    /// <summary>Creates a <c>TypeError</c>.</summary>
    public static PyException TypeError(string message) => new(PyExceptionType.TypeError, message);

    /// <summary>Creates a <c>ValueError</c>.</summary>
    public static PyException ValueError(string message) => new(PyExceptionType.ValueError, message);

    /// <summary>Creates a <c>NameError</c> with CPython's wording.</summary>
    public static PyException NameError(string name) =>
        new(PyExceptionType.NameError, $"name '{name}' is not defined");

    /// <summary>Creates an <c>UnboundLocalError</c>.</summary>
    public static PyException UnboundLocalError(string name) =>
        new(PyExceptionType.UnboundLocalError, $"cannot access local variable '{name}' where it is not associated with a value");

    /// <summary>Creates an <c>AttributeError</c>.</summary>
    public static PyException AttributeError(string typeName, string attribute) =>
        new(PyExceptionType.AttributeError, $"'{typeName}' object has no attribute '{attribute}'");

    /// <summary>Creates an <c>IndexError</c>.</summary>
    public static PyException IndexError(string message) => new(PyExceptionType.IndexError, message);

    /// <summary>
    /// Creates a <c>KeyError</c>. Its message is the key's <c>repr</c>, not a sentence —
    /// scripts print it and expect exactly that.
    /// </summary>
    public static PyException KeyError(PyObject key)
    {
        string text;

        try
        {
            text = key.Repr();
        }
        catch (PyRaise)
        {
            // A key too large to render — a huge integer — still names a missing key, and
            // reporting that is more useful than replacing it with the rendering failure.
            text = $"<{key.TypeName}>";
        }

        return new PyException(PyExceptionType.KeyError, text, [key]);
    }

    /// <summary>Creates a <c>ZeroDivisionError</c>.</summary>
    public static PyException ZeroDivisionError(string message) =>
        new(PyExceptionType.ZeroDivisionError, message);

    /// <summary>Creates a <c>StopIteration</c>.</summary>
    public static PyException StopIteration() => new(PyExceptionType.StopIteration, string.Empty);

    /// <summary>Creates an <c>AssertionError</c>.</summary>
    public static PyException AssertionError(string message) =>
        new(PyExceptionType.AssertionError, message);

    /// <summary>Creates a <c>RuntimeError</c>.</summary>
    public static PyException RuntimeError(string message) => new(PyExceptionType.RuntimeError, message);

    /// <summary>Creates an <c>ImportError</c>.</summary>
    public static PyException ModuleNotFound(string module) =>
        new(PyExceptionType.ModuleNotFoundError, $"No module named '{module}'");

    /// <summary>Creates an exception by class name, for <c>raise</c> of a builtin.</summary>
    public static PyException Create(string typeName, string message) =>
        new(PyExceptionType.Registry.TryGetValue(typeName, out var type) ? type : PyExceptionType.Exception, message);

    /// <summary>The hash of a value, or a <c>TypeError</c> when it is unhashable.</summary>
    public static BigInteger RequireHash(PyObject value) => value.PyHash();
}
