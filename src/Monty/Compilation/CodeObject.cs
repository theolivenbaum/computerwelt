using Monty.Runtime;

namespace Monty.Compilation;

/// <summary>
/// A compiled function, module or class body.
/// </summary>
/// <remarks>
/// Everything a frame needs to run is here and nothing that varies per call: the same code
/// object serves every invocation, which is what makes recursion and closures cheap.
/// </remarks>
public sealed class CodeObject
{
    /// <summary>Creates a code object.</summary>
    public CodeObject(string name, string fileName)
    {
        Name = name;
        FileName = fileName;
    }

    /// <summary>The function or module name, as tracebacks report it.</summary>
    public string Name { get; }

    /// <summary>The source file name.</summary>
    public string FileName { get; }

    /// <summary>The instruction stream.</summary>
    public List<Instruction> Instructions { get; } = [];

    /// <summary>The constant pool.</summary>
    public List<PyObject> Constants { get; } = [];

    /// <summary>Names referenced as globals, attributes, or operators.</summary>
    public List<string> Names { get; } = [];

    /// <summary>Local variable names, indexed by slot.</summary>
    public List<string> LocalNames { get; } = [];

    /// <summary>Names of the closure cells this code reads or writes.</summary>
    public List<string> CellNames { get; } = [];

    /// <summary>Nested code objects, referenced by <see cref="OpCode.MakeFunction"/>.</summary>
    public List<CodeObject> NestedCode { get; } = [];

    /// <summary>The declared parameters.</summary>
    public Parsing.ParameterList Parameters { get; set; } = Parsing.ParameterList.Empty;

    /// <summary>True when the body contains a <c>yield</c>, making calls produce a generator.</summary>
    public bool IsGenerator { get; set; }

    /// <summary>
    /// True for an <c>async def</c> body.
    /// </summary>
    /// <remarks>
    /// Calling one produces a coroutine rather than running the body: the deferral is the
    /// only observable difference in a runtime with no I/O to wait on, and it is what makes
    /// awaiting the same coroutine twice an error.
    /// </remarks>
    public bool IsCoroutine { get; set; }

    /// <summary>Adds a constant, reusing an existing slot when the value is already present.</summary>
    public int AddConstant(PyObject value)
    {
        for (var i = 0; i < Constants.Count; i++)
        {
            if (Constants[i].GetType() == value.GetType() && Constants[i].PyEquals(value))
            {
                return i;
            }
        }

        Constants.Add(value);
        return Constants.Count - 1;
    }

    /// <summary>Adds a name, reusing an existing slot.</summary>
    public int AddName(string name)
    {
        var index = Names.IndexOf(name);

        if (index >= 0)
        {
            return index;
        }

        Names.Add(name);
        return Names.Count - 1;
    }

    /// <summary>Returns a local's slot, allocating one if needed.</summary>
    public int LocalSlot(string name)
    {
        var index = LocalNames.IndexOf(name);

        if (index >= 0)
        {
            return index;
        }

        LocalNames.Add(name);
        return LocalNames.Count - 1;
    }

    /// <summary>A human-readable disassembly, for debugging the compiler.</summary>
    public string Disassemble()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("code ").Append(Name).Append('\n');

        for (var i = 0; i < Instructions.Count; i++)
        {
            var instruction = Instructions[i];
            builder.Append(i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))
                .Append("  ").Append(instruction.OpCode.ToString().PadRight(20))
                .Append(instruction.Operand);

            switch (instruction.OpCode)
            {
                case OpCode.LoadConst when instruction.Operand < Constants.Count:
                    builder.Append("  (").Append(Constants[instruction.Operand].Repr()).Append(')');
                    break;

                case OpCode.LoadGlobal or OpCode.StoreGlobal or OpCode.LoadAttr or OpCode.StoreAttr
                    when instruction.Operand < Names.Count:
                    builder.Append("  (").Append(Names[instruction.Operand]).Append(')');
                    break;

                case OpCode.LoadLocal or OpCode.StoreLocal when instruction.Operand < LocalNames.Count:
                    builder.Append("  (").Append(LocalNames[instruction.Operand]).Append(')');
                    break;
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
