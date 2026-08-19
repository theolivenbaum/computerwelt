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
