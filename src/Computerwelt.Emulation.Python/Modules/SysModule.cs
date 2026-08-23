using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>sys</c> module.</summary>
/// <remarks>
/// Everything here reports the sandbox, not the host. <c>sys.platform</c> is
/// <c>"monty"</c> precisely so a script can tell it is sandboxed — upstream's own fixtures
/// branch on it — and no path, environment variable or real file descriptor is exposed.
/// </remarks>
public static class SysModule
{
    /// <summary>Builds the module.</summary>
    /// <param name="machine">The machine whose output buffers the streams write to.</param>
    /// <param name="arguments">
    /// What <c>sys.argv</c> reports, program name first. A host that runs Python as a
    /// command — the shell's <c>python</c> builtin does — passes the real invocation here,
    /// so an ordinary <c>script.py --flag value</c> works.
    /// </param>
    /// <param name="standardInput">
    /// The stream <c>sys.stdin</c> reads, or null when there is no standard input.
    /// </param>
    public static PyModuleObject Create(
        VirtualMachine machine,
        IReadOnlyList<string>? arguments = null,
        PyMemoryStream? standardInput = null)
    {
        var module = new PyModuleObject("sys");

        IReadOnlyList<string> argv = arguments is { Count: > 0 } supplied ? supplied : ["<script>"];

        module.Add("platform", new PyStr("monty"));
        module.Add("maxsize", PyInt.From(long.MaxValue));
        module.Add("argv", new PyList([.. argv.Select(static value => new PyStr(value))]));
        module.Add("path", new PyList());
        module.Add("version", new PyStr("3.14.0 (Monty)"));

        // A structseq, not a namedtuple: named fields but none of the `_`-prefixed helpers.
        module.Add("version_info", new PyNamedTuple(
            new PyNamedTupleType(
                "sys.version_info",
                ["major", "minor", "micro", "releaselevel", "serial"],
                structSeq: true),
            [PyInt.From(3), PyInt.From(14), PyInt.From(0), new PyStr("final"), PyInt.From(0)]));
        module.Add("byteorder", new PyStr(BitConverter.IsLittleEndian ? "little" : "big"));

        module.Add("stdout", new StandardStream("stdout", machine.Write));
        module.Add("stderr", new StandardStream("stderr", machine.WriteError));

        if (standardInput is not null)
        {
            module.Add("stdin", standardInput);
        }

        module.Add("exit", static arguments =>
        {
            var code = arguments.Length > 0 ? arguments[0] : PyNone.Instance;
            throw new PyRaise(new PyException(PyExceptionType.SystemExit, code.Display(), [code]));
        });

        module.Add("getrecursionlimit", _ => PyInt.From(machine.RecursionLimit));

        // A script may lower the limit but not raise it past the sandbox's own cap.
        module.Add("setrecursionlimit", arguments =>
        {
            Arity.Exact("setrecursionlimit", arguments, 1);
            machine.RecursionLimit = arguments[0] is PyInt limit
                ? limit.ToIndex()
                : throw new PyRaise(PyErrors.TypeError(
                    $"'{arguments[0].TypeName}' object cannot be interpreted as an integer"));

            return PyNone.Instance;
        });

        module.Add("intern", static arguments => arguments[0]);

        return module;
    }

    /// <summary>
    /// Builds the <c>input</c> builtin over <paramref name="standardInput"/>.
    /// </summary>
    /// <remarks>
    /// It exists only when the host supplied standard input, so a program that calls it
    /// with nothing piped in gets a <c>NameError</c> naming the problem rather than a hang.
    /// </remarks>
    /// <param name="machine">The machine whose output buffer a prompt is written to.</param>
    /// <param name="standardInput">Where the line comes from.</param>
    public static PyBuiltinFunction CreateInput(VirtualMachine machine, PyMemoryStream standardInput) =>
        new("input", arguments =>
        {
            if (arguments.Length > 0)
            {
                machine.Write(arguments[0].Display());
            }

            var line = standardInput.ReadLine();

            if (line.Length == 0)
            {
                throw new PyRaise(new PyException(PyExceptionType.EOFError, "EOF when reading a line"));
            }

            // `input` hands back the line without its terminator; a last line that had none
            // is returned as it stands.
            return new PyStr(line.TrimEnd('\n'));
        });

    /// <summary>A writable stream backed by the machine's output buffers.</summary>
    private sealed class StandardStream(string name, Action<string> write) : PyObject
    {
        /// <inheritdoc />
        /// <remarks>CPython names it for the module it lives in, and so does `type()`.</remarks>
        public override string TypeName => "_io.TextIOWrapper";

        /// <inheritdoc />
        public override string Repr() => $"<sys.{name}>";

        /// <inheritdoc />
        public override PyObject? GetAttribute(string attribute) => attribute switch
        {
            "write" => new PyBuiltinFunction("write", arguments =>
            {
                var text = arguments.Length > 0 ? arguments[0].Display() : string.Empty;
                write(text);
                return PyInt.From(text.Length);
            }),

            // Output is buffered until the run ends, so flushing is a no-op that succeeds.
            "flush" => new PyBuiltinFunction("flush", static _ => PyNone.Instance),
            "name" => new PyStr($"<{name}>"),
            _ => null,
        };
    }
}
