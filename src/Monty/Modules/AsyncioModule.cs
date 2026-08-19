using Monty.Runtime;

namespace Monty.Modules;

/// <summary>The <c>asyncio</c> module.</summary>
/// <remarks>
/// <para>
/// There is no I/O in the sandbox and therefore nothing for a coroutine to wait on: every
/// coroutine runs straight through the moment it is awaited. That makes a scheduler
/// unnecessary rather than absent — <c>gather</c> resolves its arguments in order, which is
/// the same answer a real loop would give for work that never blocks.
/// </para>
/// <para>
/// What is preserved is the shape a program can observe: calling an <c>async def</c> yields
/// a coroutine that has not run, awaiting one twice is an error, and gathering something
/// that is not awaitable is rejected with the message asyncio uses.
/// </para>
/// </remarks>
public static class AsyncioModule
{
    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("asyncio");

        module.Add("run", new PyBuiltinFunction("run", arguments =>
        {
            Arity.Exact("run", arguments, 1);

            return arguments[0] is PyCoroutine or PyFuture
                ? machine.Await(arguments[0])
                : throw new PyRaise(PyErrors.ValueError(
                    $"a coroutine was expected, got {arguments[0].Repr()}"));
        }));

        // `gather` schedules; it does not run. The work happens when the future it returns
        // is awaited, which is what lets two gathers over one coroutine both be built and
        // only the second one fail.
        module.Add("gather", new PyBuiltinFunction("gather", (arguments, keywords) =>
        {
            _ = keywords;
            var awaited = arguments.ToArray();

            return new PyFuture(() =>
            {
                var results = new List<PyObject>(awaited.Length);

                foreach (var awaitable in awaited)
                {
                    if (awaitable is not (PyCoroutine or PyFuture))
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            "An asyncio.Future, a coroutine or an awaitable is required"));
                    }

                    results.Add(machine.Await(awaitable));
                }

                return new PyList(results);
            });
        })
        {
            Kind = "function",
        });

        module.Add("sleep", new PyBuiltinFunction("sleep", arguments =>
            new PyFuture(arguments.Length > 1 ? arguments[1] : PyNone.Instance)));

        module.Add("CancelledError", PyExceptionType.Registry["Exception"]);

        return module;
    }
}
