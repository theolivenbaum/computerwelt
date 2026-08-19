using Monty.Runtime;

namespace Monty.Modules;

/// <summary>The <c>sys</c> module.</summary>
/// <remarks>
/// Everything here reports the sandbox, not the host. <c>sys.platform</c> is
/// <c>"monty"</c> precisely so a script can tell it is sandboxed — upstream's own fixtures
/// branch on it — and no path, environment variable or real file descriptor is exposed.
/// </remarks>
public static class SysModule
{
    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("sys");

        module.Add("platform", new PyStr("monty"));
        module.Add("maxsize", new PyInt(long.MaxValue));
        module.Add("argv", new PyList([new PyStr("<script>")]));
        module.Add("path", new PyList());
        module.Add("version", new PyStr("3.12.0 (monty)"));
        module.Add("version_info", new PyTuple([new PyInt(3), new PyInt(12), new PyInt(0)]));
        module.Add("byteorder", new PyStr(BitConverter.IsLittleEndian ? "little" : "big"));

        module.Add("stdout", new StandardStream("stdout", machine.Write));
        module.Add("stderr", new StandardStream("stderr", machine.WriteError));

        module.Add("exit", static arguments =>
        {
            var code = arguments.Length > 0 ? arguments[0] : PyNone.Instance;
            throw new PyRaise(new PyException(PyExceptionType.SystemExit, code.Display(), [code]));
        });

        module.Add("getrecursionlimit", _ => new PyInt(machine.Limits.MaxRecursionDepth));

        // The limit is fixed for the run; accepting the call and ignoring it lets fixtures
        // that set it defensively still run.
        module.Add("setrecursionlimit", static _ => PyNone.Instance);

        module.Add("intern", static arguments => arguments[0]);

        return module;
    }

    /// <summary>A writable stream backed by the machine's output buffers.</summary>
    private sealed class StandardStream(string name, Action<string> write) : PyObject
    {
        /// <inheritdoc />
        public override string TypeName => "TextIOWrapper";

        /// <inheritdoc />
        public override string Repr() => $"<sys.{name}>";

        /// <inheritdoc />
        public override PyObject? GetAttribute(string attribute) => attribute switch
        {
            "write" => new PyBuiltinFunction("write", arguments =>
            {
                var text = arguments.Length > 0 ? arguments[0].Display() : string.Empty;
                write(text);
                return new PyInt(text.Length);
            }),

            // Output is buffered until the run ends, so flushing is a no-op that succeeds.
            "flush" => new PyBuiltinFunction("flush", static _ => PyNone.Instance),
            "name" => new PyStr($"<{name}>"),
            _ => null,
        };
    }
}
