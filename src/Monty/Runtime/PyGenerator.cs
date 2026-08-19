using Monty.Compilation;

namespace Monty.Runtime;

/// <summary>
/// A generator.
/// </summary>
/// <remarks>
/// <para>
/// A suspended frame is what a generator needs, and the VM's <see cref="OpCode.Yield"/>
/// would have to unwind and resume the C# stack to provide one. Rather than write a second
/// interpreter loop, this runs the frame on its own thread of control via C#'s own
/// iterator machinery: the body executes lazily, one <c>yield</c> at a time, driven by
/// <see cref="Next"/>.
/// </para>
/// <para>
/// The consequence is that a generator holds a live enumerator rather than a serializable
/// frame, so generators are the one thing a snapshot cannot yet capture. That is recorded
/// in the ledger rather than hidden.
/// </para>
/// </remarks>
public sealed class PyGenerator : PyObject
{
    private readonly IEnumerator<PyObject> _enumerator;
    private bool _finished;

    /// <summary>Creates a generator over a prepared frame.</summary>
    internal PyGenerator(VirtualMachine machine, VirtualMachine.Frame frame) =>
        _enumerator = GeneratorRunner.Run(machine, frame).GetEnumerator();

    /// <inheritdoc />
    public override string TypeName => "generator";

    /// <inheritdoc />
    public override string Repr() => "<generator object>";

    /// <summary>Advances the generator. Returns null when it is exhausted.</summary>
    public PyObject? Next()
    {
        if (_finished)
        {
            return null;
        }

        if (_enumerator.MoveNext())
        {
            return _enumerator.Current;
        }

        _finished = true;
        return null;
    }

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate()
    {
        while (Next() is { } value)
        {
            yield return value;
        }
    }
}

/// <summary>
/// A coroutine: an <c>async def</c> body that has been called but not yet awaited.
/// </summary>
/// <remarks>
/// With no I/O to wait on, nothing a coroutine does can actually block, so awaiting one
/// runs it straight through. What the object buys is the deferral — the body does not run
/// at the call — and the record of having been awaited, which is what makes awaiting the
/// same coroutine twice the error CPython reports.
/// </remarks>
public sealed class PyCoroutine : PyObject
{
    private readonly Func<PyObject> _run;
    private bool _awaited;

    internal PyCoroutine(string name, Func<PyObject> run)
    {
        Name = name;
        _run = run;
    }

    /// <summary>The name of the function that produced it.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string TypeName => "coroutine";

    /// <inheritdoc />
    public override string Repr() => $"<coroutine object {Name}>";

    /// <summary>Runs the body and returns its result.</summary>
    public PyObject Resolve()
    {
        if (_awaited)
        {
            throw new PyRaise(PyErrors.RuntimeError("cannot reuse already awaited coroutine"));
        }

        _awaited = true;
        return _run();
    }
}

/// <summary>
/// A value that is already settled, standing in for a future.
/// </summary>
/// <remarks>
/// <c>gather</c> has to return something awaitable, and with every coroutine completing
/// synchronously there is nothing left to wait for by the time it returns — so the result
/// is simply carried until the <c>await</c> unwraps it.
/// </remarks>
public sealed class PyFuture : PyObject
{
    private Func<PyObject>? _pending;
    private PyObject? _value;

    /// <summary>Creates a future that has already settled.</summary>
    public PyFuture(PyObject value) => _value = value;

    /// <summary>Creates a future whose work runs when it is first awaited.</summary>
    public PyFuture(Func<PyObject> pending) => _pending = pending;

    /// <inheritdoc />
    public override string TypeName => "Future";

    /// <inheritdoc />
    public override string Repr() =>
        _pending is null ? $"<Future finished result={_value!.Repr()}>" : "<Future pending>";

    /// <summary>
    /// Settles the future, running its work the first time and caching the result.
    /// </summary>
    /// <remarks>
    /// Unlike a coroutine, a future may be awaited any number of times: the second await
    /// gets the same object back rather than an error.
    /// </remarks>
    public PyObject Resolve()
    {
        if (_pending is { } work)
        {
            _pending = null;
            _value = work();
        }

        return _value!;
    }
}

/// <summary>
/// Runs a generator frame, yielding each value the body produces.
/// </summary>
/// <remarks>
/// This is a second, simplified interpreter loop that handles only what a generator body
/// needs and can suspend at a <c>yield</c>, which the main loop cannot. The duplication is
/// deliberate: threading suspension through every instruction of the main loop would slow
/// down the common case for the sake of the rare one.
/// </remarks>
internal static class GeneratorRunner
{
    public static IEnumerable<PyObject> Run(VirtualMachine machine, VirtualMachine.Frame frame)
    {
        while (frame.InstructionPointer < frame.Code.Instructions.Count)
        {
            var instruction = frame.Code.Instructions[frame.InstructionPointer];

            if (instruction.OpCode == OpCode.Yield)
            {
                frame.InstructionPointer++;
                yield return frame.Pop();
                continue;
            }

            if (instruction.OpCode == OpCode.YieldFrom)
            {
                frame.InstructionPointer++;
                var source = frame.Pop();

                foreach (var value in VirtualMachine.RequireIterable(source))
                {
                    yield return value;
                }

                frame.Push(PyNone.Instance);
                continue;
            }

            // Everything up to the next yield runs on the main loop, which stops as soon
            // as it reaches one.
            var reachedYield = machine.RunUntilYield(frame);

            if (!reachedYield)
            {
                yield break;
            }
        }
    }
}
