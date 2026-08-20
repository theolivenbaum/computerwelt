using System.Numerics;
using System.Text;
using Monty.Compilation;

namespace Monty.Runtime;

/// <summary>
/// The bytecode interpreter.
/// </summary>
/// <remarks>
/// <para>
/// A stack machine with a separate block stack for <c>try</c>, <c>finally</c> and context
/// managers. Python exceptions travel as <see cref="PyRaise"/>, so a raise inside a nested
/// C# call — an operator, a builtin, a comparison — unwinds to the enclosing frame's
/// handler without any explicit error propagation in the loop.
/// </para>
/// <para>
/// Resource limits are charged inside the loop rather than checked around it: a script that
/// spins in a tight loop must still terminate.
/// </para>
/// </remarks>
public sealed class VirtualMachine
{
    private readonly ExecutionLimits _limits;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private long _instructionCount;
    private int _depth;

    /// <summary>Creates a machine with the given limits and module globals.</summary>
    public VirtualMachine(PyDict globals, PyDict builtins, ExecutionLimits? limits = null)
    {
        Globals = globals;
        Builtins = builtins;
        _limits = limits ?? ExecutionLimits.Default;
    }

    /// <summary>The module namespace.</summary>
    public PyDict Globals { get; }

    /// <summary>The builtin namespace, searched after globals.</summary>
    public PyDict Builtins { get; }

    /// <summary>The resource caps this machine enforces.</summary>
    public ExecutionLimits Limits => _limits;

    /// <summary>
    /// The call depth a program may reach, which <c>sys.setrecursionlimit</c> may lower.
    /// </summary>
    /// <remarks>
    /// It can be lowered but never raised past the configured cap: the limit is a safety
    /// bound on the host stack, and a script must not be able to lift it.
    /// </remarks>
    public int RecursionLimit
    {
        get => _recursionLimit ?? _limits.MaxRecursionDepth;
        set => _recursionLimit = Math.Clamp(value, 1, _limits.MaxRecursionDepth);
    }

    private int? _recursionLimit;

    /// <summary>Everything the program wrote to standard output.</summary>
    public string Stdout => _stdout.ToString();

    /// <summary>Everything the program wrote to standard error.</summary>
    public string Stderr => _stderr.ToString();

    /// <summary>Modules the host has made importable.</summary>
    public Dictionary<string, PyObject> Modules { get; } = new(StringComparer.Ordinal);

    /// <summary>Frames currently executing, innermost last. Used to build tracebacks.</summary>
    internal List<Frame> CallStack { get; } = [];

    /// <summary>Writes to the captured standard output.</summary>
    public void Write(string text) => _stdout.Append(text);

    /// <summary>Writes to the captured standard error.</summary>
    public void WriteError(string text) => _stderr.Append(text);

    /// <summary>Runs a module's code object.</summary>
    public PyObject RunModule(CodeObject code)
    {
        var frame = new Frame(code, Globals, [], []);
        return Execute(frame);
    }

    /// <summary>Calls any callable with positional and keyword arguments.</summary>
    public PyObject Call(PyObject callable, PyObject[] arguments, PyDict? keywords = null)
    {
        switch (callable)
        {
            case PyBuiltinFunction builtin:
                return builtin.Invoke(arguments, keywords);

            case PyBoundMethod method:
                return method.Invoke(arguments, keywords);

            case PyFunction function:
                return CallFunction(function, arguments, keywords);

            case PyType builtinType:
                return builtinType.Construct(arguments, keywords);

            case PyClass type:
                return Instantiate(type, arguments, keywords);

            case PyNamedTupleType namedTuple:
                return namedTuple.Instantiate(arguments, keywords);

            case PyCallable constructible when Monty.Modules.DatetimeModule.TryConstruct(constructible, arguments) is { } built:
                return built;

            case PyExceptionType exceptionType:
            {
                var message = arguments.Length > 0 ? arguments[0].Display() : string.Empty;
                return new PyException(exceptionType, message, arguments);
            }

            default:
                throw new PyRaise(PyErrors.TypeError($"'{callable.TypeName}' object is not callable"));
        }
    }

    private PyObject Instantiate(PyClass type, PyObject[] arguments, PyDict? keywords)
    {
        var instance = new PyInstance(type, this);

        switch (type.GetAttribute("__init__"))
        {
            case PyFunction initializer:
            {
                // An initializer produces the instance by mutating it, so returning
                // anything is a mistake worth reporting rather than quietly dropping.
                var returned = CallFunction(initializer.Bind(instance), arguments, keywords);

                if (returned is not PyNone)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"__init__() should return None, not '{returned.TypeName}'"));
                }

                break;
            }

            // A decorator such as `@dataclass` installs a builtin `__init__`, which is
            // unbound and therefore takes the receiver as its first argument.
            case PyBuiltinFunction builtin:
                builtin.Invoke([instance, .. arguments], keywords);
                break;

            case null when arguments.Length > 0 || keywords is { Count: > 0 }:
                throw new PyRaise(PyErrors.TypeError($"{type.Name}() takes no arguments"));
        }

        return instance;
    }

    /// <summary>
    /// Counts one level of host-side recursion against the call-depth limit.
    /// </summary>
    /// <remarks>
    /// A generated method — a dataclass's <c>__eq__</c> or <c>__repr__</c> — recurses in C#
    /// rather than in bytecode, so it has to charge the same budget or a cyclic structure
    /// would overflow the host stack instead of raising <c>RecursionError</c>.
    /// </remarks>
    public IDisposable EnterRecursion()
    {
        if (++_depth > RecursionLimit)
        {
            _depth--;
            throw new PyRaise(new PyException(
                PyExceptionType.RecursionError, "maximum recursion depth exceeded"));
        }

        return new RecursionLevel(this);
    }

    private sealed class RecursionLevel(VirtualMachine machine) : IDisposable
    {
        public void Dispose() => machine._depth--;
    }

    /// <summary>
    /// Resolves an awaited value.
    /// </summary>
    /// <remarks>
    /// Only a coroutine or a settled future can be awaited. Anything else is a mistake in
    /// the program rather than something to pass through, and saying so is the difference
    /// between a typo being caught and it silently doing nothing.
    /// </remarks>
    public PyObject Await(PyObject value) => value switch
    {
        PyCoroutine coroutine => coroutine.Resolve(),
        PyFuture future => future.Resolve(),
        _ => throw new PyRaise(PyErrors.TypeError($"'{value.TypeName}' object can't be awaited")),
    };

    private PyObject CallFunction(PyFunction function, PyObject[] arguments, PyDict? keywords)
    {
        if (++_depth > RecursionLimit)
        {
            _depth--;
            throw new PyRaise(new PyException(
                PyExceptionType.RecursionError, "maximum recursion depth exceeded"));
        }

        try
        {
            var locals = BindArguments(function, arguments, keywords);
            var cells = BuildCells(function, locals);
            var frame = new Frame(function.Code, function.Globals, locals, cells);

            if (function.Code.IsGenerator)
            {
                // A generator call runs nothing yet; it materializes lazily on iteration.
                return new PyGenerator(this, frame);
            }

            // Calling an `async def` produces a coroutine; the body runs when it is awaited.
            if (function.Code.IsCoroutine)
            {
                return new PyCoroutine(function.Name, () => Execute(frame));
            }

            return Execute(frame);
        }
        finally
        {
            _depth--;
        }
    }

    private Dictionary<string, PyCell> BuildCells(PyFunction function, PyObject?[] locals)
    {
        var cells = new Dictionary<string, PyCell>(function.Closure, StringComparer.Ordinal);

        // A local this function's body reads through a cell must be shared, not copied,
        // so nested closures observe later assignments.
        foreach (var name in function.Code.CellNames)
        {
            if (cells.ContainsKey(name))
            {
                continue;
            }

            var cell = new PyCell();
            var slot = function.Code.LocalNames.IndexOf(name);

            if (slot >= 0 && slot < locals.Length)
            {
                cell.Value = locals[slot];
            }

            cells[name] = cell;
        }

        return cells;
    }

    /// <summary>
    /// Reports an unknown or duplicated keyword, which CPython does before it complains
    /// about surplus positional arguments.
    /// </summary>
    private static void RejectBadKeywords(PyFunction function, PyDict? keywords, HashSet<string> bound)
    {
        var parameters = function.Code.Parameters;

        foreach (var (key, _) in keywords?.Entries ?? [])
        {
            var name = key.Display();

            if (bound.Contains(name))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{function.Code.Name}() got multiple values for argument '{name}'"));
            }

            var known = parameters.Parameters.Any(parameter => parameter.Name == name)
                || parameters.KeywordOnly.Any(parameter => parameter.Name == name);

            if (!known && parameters.KeywordArgs is null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{function.Code.Name}() got an unexpected keyword argument '{name}'"));
            }
        }
    }

    /// <summary>Formats a list of parameter names the way CPython's arity errors do.</summary>
    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => $"'{names[0]}'",
        2 => $"'{names[0]}' and '{names[1]}'",
        _ => string.Join(", ", names.Take(names.Count - 1).Select(name => $"'{name}'"))
            + $", and '{names[^1]}'",
    };

    /// <summary>Binds call arguments to parameter slots, applying defaults and packing varargs.</summary>
    private PyObject?[] BindArguments(PyFunction function, PyObject[] arguments, PyDict? keywords)
    {
        var code = function.Code;
        var parameters = code.Parameters;
        var locals = new PyObject?[Math.Max(code.LocalNames.Count, 1)];

        var positional = new List<PyObject>(arguments);

        if (function.BoundSelf is { } instance)
        {
            positional.Insert(0, instance);
        }

        var bound = new HashSet<string>(StringComparer.Ordinal);
        var declared = parameters.Parameters;

        var supplied = Math.Min(positional.Count, declared.Count);

        for (var i = 0; i < supplied; i++)
        {
            locals[code.LocalNames.IndexOf(declared[i].Name)] = positional[i];
            bound.Add(declared[i].Name);
        }

        var extra = positional.Skip(declared.Count).ToList();

        if (parameters.VarArgs is { } varArgs)
        {
            locals[code.LocalNames.IndexOf(varArgs)] = new PyTuple(extra);
        }
        else if (extra.Count > 0)
        {
            // A keyword error is reported first, because CPython binds the keywords before
            // it counts the positionals.
            RejectBadKeywords(function, keywords, bound);

            // With defaults the arity is a range, and CPython says so.
            var required = declared.Count(parameter =>
                !function.Defaults.TryGetValue(new PyStr(parameter.Name), out _));

            // Keyword-only arguments that bound are counted in a suffix, so the numbers in
            // the message add up to what the caller actually passed.
            var keywordOnly = parameters.KeywordOnly
                .Count(parameter => keywords?.TryGetValue(new PyStr(parameter.Name), out _) == true);

            throw new PyRaise(PyErrors.TypeError(
                $"{code.Name}() takes "
                + (required == declared.Count
                    ? $"{declared.Count} positional argument{(declared.Count == 1 ? string.Empty : "s")}"
                    : $"from {required} to {declared.Count} positional arguments")
                + " but "
                + (keywordOnly == 0
                    ? $"{positional.Count} were given"
                    : $"{positional.Count} positional arguments "
                        + $"(and {keywordOnly} keyword-only argument{(keywordOnly == 1 ? string.Empty : "s")}) "
                        + "were given")));
        }

        var leftoverKeywords = new PyDict();

        if (keywords is not null)
        {
            foreach (var (key, value) in keywords.Entries)
            {
                var name = key.Display();
                var slot = code.LocalNames.IndexOf(name);
                var positionalOnly = declared.Take(parameters.PositionalOnlyCount).Any(p => p.Name == name);

                // A parameter before `/` can only be filled positionally; naming it is an
                // error even though the name exists.
                if (positionalOnly)
                {
                    if (parameters.KeywordArgs is null)
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            $"{code.Name}() got some positional-only arguments passed as keyword arguments: '{name}'"));
                    }

                    leftoverKeywords.Set(key, value);
                    continue;
                }

                var isParameter = declared.Any(p => p.Name == name) || parameters.KeywordOnly.Any(p => p.Name == name);

                if (isParameter && slot >= 0)
                {
                    if (!bound.Add(name))
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            $"{code.Name}() got multiple values for argument '{name}'"));
                    }

                    locals[slot] = value;
                    continue;
                }

                if (parameters.KeywordArgs is null)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"{code.Name}() got an unexpected keyword argument '{name}'"));
                }

                leftoverKeywords.Set(key, value);
            }
        }

        if (parameters.KeywordArgs is { } keywordArgs)
        {
            locals[code.LocalNames.IndexOf(keywordArgs)] = leftoverKeywords;
        }

        // Every missing parameter is collected before reporting, because CPython names
        // them all in one message.
        var missing = new List<string>();

        foreach (var parameter in declared.Concat(parameters.KeywordOnly))
        {
            if (bound.Contains(parameter.Name))
            {
                continue;
            }

            var slot = code.LocalNames.IndexOf(parameter.Name);

            if (function.Defaults.TryGetValue(new PyStr(parameter.Name), out var defaultValue))
            {
                locals[slot] = defaultValue;
                continue;
            }

            missing.Add(parameter.Name);
        }

        if (missing.Count > 0)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{code.Name}() missing {missing.Count} required positional "
                + $"argument{(missing.Count == 1 ? string.Empty : "s")}: {Join(missing)}"));
        }

        return locals;
    }

    /// <summary>One activation record.</summary>
    internal sealed class Frame
    {
        public Frame(CodeObject code, PyDict globals, PyObject?[] locals, Dictionary<string, PyCell> cells)
        {
            Code = code;
            Globals = globals;
            Locals = locals.Length >= code.LocalNames.Count
                ? locals
                : [.. locals, .. new PyObject?[code.LocalNames.Count - locals.Length]];
            Cells = cells;
        }

        public CodeObject Code { get; }

        public PyDict Globals { get; }

        public PyObject?[] Locals { get; }

        public Dictionary<string, PyCell> Cells { get; }

        public List<PyObject> Stack { get; } = [];

        public List<Block> Blocks { get; } = [];

        public int InstructionPointer { get; set; }

        public PyException? CurrentException { get; set; }

        public int CurrentLine { get; set; }

        public void Push(PyObject value) => Stack.Add(value);

        public PyObject Pop()
        {
            var value = Stack[^1];
            Stack.RemoveAt(Stack.Count - 1);
            return value;
        }

        public PyObject Peek(int depth = 0) => Stack[^(depth + 1)];
    }

    /// <summary>An entry on the block stack.</summary>
    internal readonly record struct Block(BlockKind Kind, int Handler, int StackDepth);

    /// <summary>What a block protects.</summary>
    internal enum BlockKind
    {
        Except,
        Finally,
        With,
    }

    /// <summary>A control transfer that is not an exception: a return out of a frame.</summary>
    private sealed class ReturnSignal(PyObject value) : Exception
    {
        public PyObject Value { get; } = value;
    }

    /// <summary>
    /// Runs a frame until it reaches a <c>yield</c> or finishes. Returns true when a yield
    /// was reached, leaving the frame suspended for the generator to resume.
    /// </summary>
    internal bool RunUntilYield(Frame frame)
    {
        while (frame.InstructionPointer < frame.Code.Instructions.Count)
        {
            var instruction = frame.Code.Instructions[frame.InstructionPointer];

            if (instruction.OpCode is OpCode.Yield or OpCode.YieldFrom)
            {
                return true;
            }

            frame.InstructionPointer++;
            frame.CurrentLine = instruction.Line;

            if (++_instructionCount > _limits.MaxInstructions)
            {
                throw new PyRaise(PyErrors.RuntimeError("instruction limit exceeded"));
            }

            try
            {
                if (Step(frame, instruction, out _))
                {
                    return false;
                }
            }
            catch (PyRaise raise)
            {
                if (!Unwind(frame, raise.Exception))
                {
                    RecordTraceback(raise.Exception, frame);
                    throw;
                }
            }
        }

        return false;
    }

    /// <summary>Runs a frame to completion and returns its result.</summary>
    internal PyObject Execute(Frame frame)
    {
        CallStack.Add(frame);

        try
        {
            while (true)
            {
                if (frame.InstructionPointer >= frame.Code.Instructions.Count)
                {
                    return PyNone.Instance;
                }

                if (++_instructionCount > _limits.MaxInstructions)
                {
                    throw new PyRaise(PyErrors.RuntimeError("instruction limit exceeded"));
                }

                var instruction = frame.Code.Instructions[frame.InstructionPointer++];
                frame.CurrentLine = instruction.Line;

                try
                {
                    if (Step(frame, instruction, out var result))
                    {
                        return result;
                    }
                }
                catch (PyRaise raise)
                {
                    if (!Unwind(frame, raise.Exception))
                    {
                        RecordTraceback(raise.Exception, frame);
                        throw;
                    }
                }
            }
        }
        finally
        {
            CallStack.RemoveAt(CallStack.Count - 1);
        }
    }

    /// <summary>
    /// The instruction after the <c>ReRaise</c> that closes a <c>with</c>'s handler chain.
    /// </summary>
    /// <remarks>
    /// A manager that swallows the exception must skip the outer managers' handlers as
    /// well: they are only reached while an exception is in flight. The chain holds nothing
    /// but handler calls and that final re-raise, so the first one found ends it.
    /// </remarks>
    private static int SkipHandlers(CodeObject code, int from)
    {
        for (var i = from; i < code.Instructions.Count; i++)
        {
            if (code.Instructions[i].OpCode == OpCode.ReRaise)
            {
                return i + 1;
            }
        }

        return code.Instructions.Count;
    }

    /// <summary>
    /// Looks up a special method the way a protocol does: on the type, never on the
    /// instance.
    /// </summary>
    private PyObject Protocol(PyObject value, string name) =>
        (value is PyInstance instance ? instance.Dunder(name) : Attributes.TryGet(this, value, name))
        ?? throw new PyRaise(PyErrors.TypeError(
            $"'{value.TypeName}' object does not support the context manager protocol "
            + $"(missed {name} method)"));

    private void RecordTraceback(PyException exception, Frame frame) =>
        exception.Traceback.Insert(0, new TracebackFrame(frame.Code.Name, frame.CurrentLine, null));

    /// <summary>
    /// Finds a handler for <paramref name="exception"/> in the frame's block stack and
    /// transfers control to it. Returns false when the frame has no handler.
    /// </summary>
    private static bool Unwind(Frame frame, PyException exception)
    {
        while (frame.Blocks.Count > 0)
        {
            var block = frame.Blocks[^1];

            // A `with` block stays registered: its handler pops it, the same way the
            // normal path's ExitWith does, so both find the manager's exit the same way.
            if (block.Kind != BlockKind.With)
            {
                frame.Blocks.RemoveAt(frame.Blocks.Count - 1);
            }

            if (frame.Stack.Count > block.StackDepth)
            {
                frame.Stack.RemoveRange(block.StackDepth, frame.Stack.Count - block.StackDepth);
            }

            // An exception raised while handling another records the first as its context.
            if (frame.CurrentException is { } previous && !ReferenceEquals(previous, exception))
            {
                exception.Context ??= previous;
            }

            frame.CurrentException = exception;
            frame.InstructionPointer = block.Handler;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Executes one instruction. Returns true when the frame is finished, with
    /// <paramref name="result"/> set to its return value.
    /// </summary>
    private bool Step(Frame frame, Instruction instruction, out PyObject result)
    {
        result = PyNone.Instance;
        var code = frame.Code;

        switch (instruction.OpCode)
        {
            case OpCode.Nop:
                return false;

            case OpCode.LoadConst:
                frame.Push(code.Constants[instruction.Operand]);
                return false;

            case OpCode.LoadLocal:
            {
                var name = code.LocalNames[instruction.Operand];

                // A captured local lives in its cell, which a nested function may have
                // written since the slot was last set. The cell is therefore always at
                // least as fresh as the slot, so it wins whenever one exists.
                if (frame.Cells.TryGetValue(name, out var cell))
                {
                    if (cell.Value is { } cellValue)
                    {
                        frame.Push(cellValue);
                        return false;
                    }

                    throw new PyRaise(PyErrors.UnboundLocalError(name));
                }

                var value = frame.Locals[instruction.Operand]
                    ?? throw new PyRaise(PyErrors.UnboundLocalError(name));

                frame.Push(value);
                return false;
            }

            case OpCode.LoadName:
            {
                var name = code.LocalNames[instruction.Operand];

                if (frame.Locals[instruction.Operand] is { } bound)
                {
                    frame.Push(bound);
                    return false;
                }

                var key = new PyStr(name);

                if (frame.Globals.TryGetValue(key, out var global))
                {
                    frame.Push(global);
                    return false;
                }

                frame.Push(Builtins.TryGetValue(key, out var builtin)
                    ? builtin
                    : throw new PyRaise(PyErrors.NameError(name)));

                return false;
            }

            case OpCode.StoreLocal:
            {
                var value = frame.Pop();
                frame.Locals[instruction.Operand] = value;

                // Keep a captured local's cell in step, so closures see the new value.
                var name = code.LocalNames[instruction.Operand];
                if (frame.Cells.TryGetValue(name, out var cell))
                {
                    cell.Value = value;
                }

                return false;
            }

            case OpCode.DeleteLocal:
                frame.Locals[instruction.Operand] = null;
                return false;

            case OpCode.LoadGlobal:
            {
                var name = code.Names[instruction.Operand];
                var key = new PyStr(name);

                if (frame.Globals.TryGetValue(key, out var value))
                {
                    frame.Push(value);
                    return false;
                }

                if (Builtins.TryGetValue(key, out var builtin))
                {
                    frame.Push(builtin);
                    return false;
                }

                throw new PyRaise(PyErrors.NameError(name));
            }

            case OpCode.StoreGlobal:
                frame.Globals.Set(new PyStr(code.Names[instruction.Operand]), frame.Pop());
                return false;

            case OpCode.DeleteGlobal:
                if (!frame.Globals.Remove(new PyStr(code.Names[instruction.Operand])))
                {
                    throw new PyRaise(PyErrors.NameError(code.Names[instruction.Operand]));
                }

                return false;

            case OpCode.LoadCell:
            {
                var name = code.CellNames[instruction.Operand];

                if (!frame.Cells.TryGetValue(name, out var cell) || cell.Value is null)
                {
                    throw new PyRaise(PyErrors.UnboundLocalError(name));
                }

                frame.Push(cell.Value);
                return false;
            }

            case OpCode.StoreCell:
            {
                var name = code.CellNames[instruction.Operand];

                if (!frame.Cells.TryGetValue(name, out var cell))
                {
                    cell = new PyCell();
                    frame.Cells[name] = cell;
                }

                cell.Value = frame.Pop();
                return false;
            }

            case OpCode.LoadAttr:
            {
                var target = frame.Pop();
                var name = code.Names[instruction.Operand];
                frame.Push(Attributes.Get(this, target, name));
                return false;
            }

            case OpCode.StoreAttr:
            {
                var target = frame.Pop();
                var value = frame.Pop();
                var name = code.Names[instruction.Operand];

                if (!target.SetAttribute(name, value))
                {
                    throw new PyRaise(PyErrors.AttributeError(target.TypeName, name));
                }

                return false;
            }

            case OpCode.LoadSubscript:
            {
                var index = frame.Pop();
                var target = frame.Pop();
                frame.Push(target.GetItem(index));
                return false;
            }

            case OpCode.StoreSubscript:
            {
                var index = frame.Pop();
                var target = frame.Pop();
                var value = frame.Pop();
                target.SetItem(index, value);
                return false;
            }

            case OpCode.DeleteSubscript:
            {
                var index = frame.Pop();
                frame.Pop().DeleteItem(index);
                return false;
            }

            case OpCode.BinaryOp:
            {
                var right = frame.Pop();
                var left = frame.Pop();
                frame.Push(Operators.Binary(code.Names[instruction.Operand], left, right));
                return false;
            }

            case OpCode.UnaryOp:
                frame.Push(Operators.Unary(code.Names[instruction.Operand], frame.Pop()));
                return false;

            case OpCode.CompareOp:
            {
                var right = frame.Pop();
                var left = frame.Pop();
                frame.Push(Operators.Compare(code.Names[instruction.Operand], left, right));
                return false;
            }

            case OpCode.BuildList:
                frame.Push(new PyList(PopMany(frame, instruction.Operand)));
                return false;

            case OpCode.BuildTuple:
            {
                // A -1 operand means "convert the list on top", used after unpacking.
                if (instruction.Operand < 0)
                {
                    var list = (PyList)frame.Pop();
                    frame.Push(new PyTuple(list.Items));
                    return false;
                }

                frame.Push(new PyTuple(PopMany(frame, instruction.Operand)));
                return false;
            }

            case OpCode.BuildSet:
                frame.Push(new PySet(PopMany(frame, instruction.Operand)));
                return false;

            case OpCode.BuildMap:
            {
                var pairs = PopMany(frame, instruction.Operand * 2);
                var dict = new PyDict();

                for (var i = 0; i < pairs.Count; i += 2)
                {
                    dict.Set(pairs[i], pairs[i + 1]);
                }

                frame.Push(dict);
                return false;
            }

            case OpCode.BuildSlice:
            {
                var step = frame.Pop();
                var stop = frame.Pop();
                var start = frame.Pop();
                frame.Push(new PySlice(start, stop, step));
                return false;
            }

            case OpCode.BuildString:
            {
                var pieces = PopMany(frame, instruction.Operand);
                var builder = new StringBuilder();

                foreach (var piece in pieces)
                {
                    builder.Append(piece.Display());
                }

                frame.Push(new PyStr(builder.ToString()));
                return false;
            }

            case OpCode.FormatValue:
            {
                var spec = frame.Pop();
                var value = frame.Pop();

                var converted = (char)instruction.Operand switch
                {
                    'r' => new PyStr(value.Repr()),
                    's' => new PyStr(value.Display()),
                    'a' => new PyStr(PyStr.Ascii(value.Repr())),
                    _ => value,
                };

                var specText = spec is PyNone ? string.Empty : spec.Display();
                frame.Push(new PyStr(StringFormatter.Format(converted, specText)));
                return false;
            }

            case OpCode.ListAppend:
            {
                var value = frame.Pop();
                ((PyList)frame.Peek(instruction.Operand - 1)).Items.Add(value);
                return false;
            }

            case OpCode.ListExtend:
            {
                var iterable = frame.Pop();
                var target = (PyList)frame.Peek(instruction.Operand - 1);
                target.Items.AddRange(RequireIterable(iterable));
                return false;
            }

            case OpCode.SetAdd:
            {
                var value = frame.Pop();
                ((PySet)frame.Peek(instruction.Operand - 1)).Add(value);
                return false;
            }

            case OpCode.SetUpdate:
            {
                var iterable = frame.Pop();
                var target = (PySet)frame.Peek(instruction.Operand - 1);

                foreach (var item in RequireIterable(iterable))
                {
                    target.Add(item);
                }

                return false;
            }

            case OpCode.MapPut:
            {
                var value = frame.Pop();
                var key = frame.Pop();
                ((PyDict)frame.Peek(instruction.Operand - 2)).Set(key, value);
                return false;
            }

            case OpCode.MapMerge:
            {
                var incoming = frame.Pop();
                var keywordTarget = (PyDict)frame.Peek(instruction.Operand - 1);

                // The callable sits under the positional list, which sits under the map.
                var name = frame.Peek(instruction.Operand + 1) switch
                {
                    PyCallable callee => callee.Name,
                    PyExceptionType type => type.Name,
                    _ => "function",
                };

                if (incoming is not PyDict merged)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"{name}() argument after ** must be a mapping, not {incoming.TypeName}"));
                }

                foreach (var (key, value) in merged.Entries)
                {
                    if (keywordTarget.TryGetValue(key, out _))
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            $"{name}() got multiple values for keyword argument '{key.Display()}'"));
                    }

                    keywordTarget.Set(key, value);
                }

                return false;
            }

            case OpCode.MapUpdate:
            {
                var mapping = frame.Pop();
                var target = (PyDict)frame.Peek(instruction.Operand - 1);

                // A dict display words this differently from a call, which names the
                // callable and the `**`.
                if (mapping is not PyDict source)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"'{mapping.TypeName}' object is not a mapping"));
                }

                foreach (var (key, value) in source.Entries)
                {
                    target.Set(key, value);
                }

                return false;
            }

            case OpCode.Pop:
                frame.Pop();
                return false;

            case OpCode.Duplicate:
            {
                // A non-zero operand duplicates the top N values as a group.
                var count = Math.Max(1, instruction.Operand);
                var values = new PyObject[count];

                for (var i = 0; i < count; i++)
                {
                    values[i] = frame.Peek(count - 1 - i);
                }

                foreach (var value in values)
                {
                    frame.Push(value);
                }

                return false;
            }

            case OpCode.Swap:
            {
                var top = frame.Pop();
                var second = frame.Pop();
                frame.Push(top);
                frame.Push(second);
                return false;
            }

            case OpCode.RotateThree:
            {
                var top = frame.Pop();
                var second = frame.Pop();
                var third = frame.Pop();
                frame.Push(top);
                frame.Push(third);
                frame.Push(second);
                return false;
            }

            case OpCode.Jump:
                frame.InstructionPointer = instruction.Operand;
                return false;

            case OpCode.JumpIfFalse:
                if (!frame.Pop().IsTruthy())
                {
                    frame.InstructionPointer = instruction.Operand;
                }

                return false;

            case OpCode.JumpIfTrue:
                if (frame.Pop().IsTruthy())
                {
                    frame.InstructionPointer = instruction.Operand;
                }

                return false;

            case OpCode.JumpIfFalseOrPop:
                if (!frame.Peek().IsTruthy())
                {
                    frame.InstructionPointer = instruction.Operand;
                }
                else
                {
                    frame.Pop();
                }

                return false;

            case OpCode.JumpIfTrueOrPop:
                if (frame.Peek().IsTruthy())
                {
                    frame.InstructionPointer = instruction.Operand;
                }
                else
                {
                    frame.Pop();
                }

                return false;

            case OpCode.GetIterator:
            {
                var iterable = frame.Pop();
                frame.Push(iterable is PyIterator or PyGenerator ? iterable : new PyIterator(RequireIterable(iterable)));
                return false;
            }

            case OpCode.ForIterate:
            {
                var iterator = frame.Peek();
                var next = iterator switch
                {
                    PyIterator sequence => sequence.Next(),
                    PyGenerator generator => generator.Next(),
                    _ => throw new PyRaise(PyErrors.TypeError($"'{iterator.TypeName}' object is not an iterator")),
                };

                if (next is null)
                {
                    frame.Pop();
                    frame.InstructionPointer = instruction.Operand;
                    return false;
                }

                frame.Push(next);
                return false;
            }

            case OpCode.UnpackSequence:
            {
                var source = frame.Pop();
                var wanted = instruction.Operand;

                // One item past the target count is enough to detect a surplus, and stopping
                // there leaves the rest of an iterator available — which is what CPython does.
                var values = new List<PyObject>(wanted);

                using (var items = Unpackable(source).GetEnumerator())
                {
                    while (values.Count <= wanted && items.MoveNext())
                    {
                        values.Add(items.Current);
                    }
                }

                if (values.Count != wanted)
                {
                    throw new PyRaise(PyErrors.ValueError(values.Count < wanted
                        ? $"not enough values to unpack (expected {wanted}, got {values.Count})"
                        : $"too many values to unpack (expected {wanted})"));
                }

                // Pushed in reverse so the first target pops first.
                for (var i = values.Count - 1; i >= 0; i--)
                {
                    frame.Push(values[i]);
                }

                return false;
            }

            case OpCode.UnpackStarred:
            {
                var starIndex = instruction.Operand >> 16;
                var total = instruction.Operand & 0xFFFF;
                var values = RequireIterable(frame.Pop()).ToList();
                var after = total - starIndex - 1;

                if (values.Count < total - 1)
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"not enough values to unpack (expected at least {total - 1}, got {values.Count})"));
                }

                var unpacked = new List<PyObject>(total);
                unpacked.AddRange(values.Take(starIndex));
                unpacked.Add(new PyList(values.Skip(starIndex).Take(values.Count - starIndex - after).ToList()));
                unpacked.AddRange(values.Skip(values.Count - after));

                for (var i = unpacked.Count - 1; i >= 0; i--)
                {
                    frame.Push(unpacked[i]);
                }

                return false;
            }

            case OpCode.Call:
            {
                var arguments = PopMany(frame, instruction.Operand).ToArray();
                var callable = frame.Pop();
                frame.Push(Call(callable, arguments));
                return false;
            }

            case OpCode.CallKeyword:
            {
                var keywords = (PyDict)frame.Pop();
                var positional = (PyList)frame.Pop();
                var callable = frame.Pop();

                // Every key is checked before the call, so `f(**{1: 2})` reports the bad
                // key rather than whatever the callee makes of it.
                foreach (var (key, _) in keywords.Entries)
                {
                    if (key is not PyStr)
                    {
                        throw new PyRaise(PyErrors.TypeError("keywords must be strings"));
                    }
                }

                frame.Push(Call(callable, [.. positional.Items], keywords.Count > 0 ? keywords : null));
                return false;
            }

            case OpCode.Await:
                frame.Push(Await(frame.Pop()));
                return false;

            case OpCode.Return:
                result = frame.Pop();
                return true;

            case OpCode.Yield:
                throw new PyRaise(PyErrors.RuntimeError("yield outside a generator"));

            case OpCode.YieldFrom:
                throw new PyRaise(PyErrors.RuntimeError("yield from outside a generator"));

            case OpCode.MakeFunction:
            {
                var defaults = (PyDict)frame.Pop();
                var nested = code.NestedCode[instruction.Operand];
                frame.Push(new PyFunction(nested, frame.Cells, frame.Globals, defaults));
                return false;
            }

            case OpCode.MakeClass:
            {
                var body = (PyFunction)frame.Pop();
                var name = frame.Pop().Display();

                // The class body runs in its own frame; its locals become the namespace.
                var members = RunClassBody(body);
                frame.Push(new PyClass(name, members));
                return false;
            }

            case OpCode.Raise:
                DoRaise(frame, instruction.Operand);
                return false;

            case OpCode.SetupExcept:
                frame.Blocks.Add(new Block(BlockKind.Except, instruction.Operand, frame.Stack.Count));
                return false;

            case OpCode.SetupFinally:
                frame.Blocks.Add(new Block(BlockKind.Finally, instruction.Operand, frame.Stack.Count));
                return false;

            case OpCode.PopBlock:
                if (frame.Blocks.Count > 0)
                {
                    frame.Blocks.RemoveAt(frame.Blocks.Count - 1);
                }

                return false;

            case OpCode.PushCurrentException:
                frame.Push(frame.CurrentException ?? (PyObject)PyNone.Instance);
                return false;

            case OpCode.MatchException:
            {
                var expected = frame.Pop();
                var actual = frame.CurrentException;

                if (actual is null)
                {
                    frame.Push(PyBool.False);
                    return false;
                }

                frame.Push(PyBool.Of(MatchesExceptionType(actual, expected)));
                return false;
            }

            case OpCode.EndHandler:
                frame.CurrentException = null;
                return false;

            case OpCode.ReRaise:
                if (frame.CurrentException is { } pending)
                {
                    frame.CurrentException = null;
                    throw new PyRaise(pending);
                }

                return false;

            case OpCode.SetupWith:
            {
                var manager = frame.Pop();

                // The protocol reads through the type, not the instance, so an instance
                // attribute of the same name does not shadow the method. `__exit__` is
                // checked first, so an object with neither is reported against it — which
                // is the half CPython names.
                var exit = Protocol(manager, "__exit__");
                var enter = Protocol(manager, "__enter__");

                // `__enter__` runs before the block is registered: a manager whose entry
                // raises was never entered, so its `__exit__` must not run.
                var entered = Call(enter, []);

                frame.Push(exit);
                frame.Blocks.Add(new Block(BlockKind.With, instruction.Operand, frame.Stack.Count));
                frame.Push(entered);
                return false;
            }

            case OpCode.ExitWithException:
            {
                // The exception is in flight and the manager's `__exit__` sits in the slot
                // its block recorded; a truthy return means the manager handled it.
                var index = frame.Blocks.FindLastIndex(static b => b.Kind == BlockKind.With);

                if (index < 0)
                {
                    return false;
                }

                var slot = frame.Blocks[index].StackDepth - 1;
                var exit = frame.Stack[slot];
                frame.Stack.RemoveAt(slot);
                frame.Blocks.RemoveAt(index);

                var raised = frame.CurrentException
                    ?? throw new PyRaise(PyErrors.RuntimeError("no active exception to exit"));

                var handled = Call(exit, [raised.ExceptionType, raised, PyNone.Instance]);

                if (handled.IsTruthy())
                {
                    frame.CurrentException = null;
                    frame.InstructionPointer = SkipHandlers(code, frame.InstructionPointer);
                }

                return false;
            }

            case OpCode.ExitWith:
            {
                // The exit is taken from the slot the block recorded, not from the top of
                // the stack: a `return` out of the body leaves its value above it.
                var index = frame.Blocks.FindLastIndex(static b => b.Kind == BlockKind.With);

                if (index < 0)
                {
                    return false;
                }

                var slot = frame.Blocks[index].StackDepth - 1;
                var exit = frame.Stack[slot];
                frame.Stack.RemoveAt(slot);
                frame.Blocks.RemoveAt(index);

                Call(exit, [PyNone.Instance, PyNone.Instance, PyNone.Instance]);
                return false;
            }

            case OpCode.ImportName:
            {
                var name = code.Names[instruction.Operand];

                if (!Modules.TryGetValue(name, out var module))
                {
                    throw new PyRaise(PyErrors.ModuleNotFound(name));
                }

                frame.Push(module);
                return false;
            }

            case OpCode.ImportFrom:
            {
                var module = frame.Pop();
                var name = code.Names[instruction.Operand];

                frame.Push(module.GetAttribute(name)
                    ?? throw new PyRaise(new PyException(
                        PyExceptionType.ImportError, $"cannot import name '{name}'")));

                return false;
            }

            case OpCode.Assert:
            {
                var message = instruction.Operand == 1 ? frame.Pop().Display() : string.Empty;
                throw new PyRaise(PyErrors.AssertionError(message));
            }

            default:
                throw new PyRaise(PyErrors.RuntimeError($"unimplemented opcode {instruction.OpCode}"));
        }
    }

    private PyDict RunClassBody(PyFunction body)
    {
        var locals = new PyObject?[body.Code.LocalNames.Count];
        var frame = new Frame(body.Code, body.Globals, locals, new Dictionary<string, PyCell>(body.Closure, StringComparer.Ordinal));

        Execute(frame);

        var members = new PyDict();

        for (var i = 0; i < frame.Code.LocalNames.Count; i++)
        {
            if (frame.Locals[i] is { } value)
            {
                members.Set(new PyStr(frame.Code.LocalNames[i]), value);
            }
        }

        // A class body at module scope stores into globals, so pick those up too.
        foreach (var name in frame.Code.Names)
        {
            if (frame.Globals.TryGetValue(new PyStr(name), out var value) && !members.Contains(new PyStr(name)))
            {
                _ = value;
            }
        }

        return members;
    }

    private static bool MatchesExceptionType(PyException actual, PyObject expected) => expected switch
    {
        PyExceptionType type => actual.IsInstanceOf(type),
        PyTuple tuple => tuple.Items.Any(item => MatchesExceptionType(actual, item)),
        PyClass => false,
        _ => false,
    };

    private void DoRaise(Frame frame, int argumentCount)
    {
        if (argumentCount == 0)
        {
            throw new PyRaise(frame.CurrentException
                ?? PyErrors.RuntimeError("No active exception to reraise"));
        }

        PyException? cause = null;

        if (argumentCount == 2)
        {
            cause = ToException(frame.Pop());
        }

        var exception = ToException(frame.Pop());
        exception.Cause = cause;
        exception.Context ??= frame.CurrentException;

        throw new PyRaise(exception);
    }

    private PyException ToException(PyObject value) => value switch
    {
        PyException exception => exception,
        PyExceptionType type => new PyException(type, string.Empty),
        _ => throw new PyRaise(PyErrors.TypeError("exceptions must derive from BaseException")),
    };

    private static List<PyObject> PopMany(Frame frame, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var start = frame.Stack.Count - count;
        var values = frame.Stack.GetRange(start, count);
        frame.Stack.RemoveRange(start, count);
        return values;
    }

    /// <summary>
    /// The items of an unpacking source, wording a non-iterable's refusal the way
    /// unpacking does rather than the way <c>iter()</c> does.
    /// </summary>
    private static IEnumerable<PyObject> Unpackable(PyObject source)
    {
        IEnumerator<PyObject> items;

        try
        {
            items = RequireIterable(source).GetEnumerator();
        }
        catch (PyRaise raise) when (raise.Exception.Message == $"'{source.TypeName}' object is not iterable")
        {
            throw new PyRaise(PyErrors.TypeError($"cannot unpack non-iterable {source.TypeName} object"));
        }

        using (items)
        {
            while (items.MoveNext())
            {
                yield return items.Current;
            }
        }
    }

    /// <summary>
    /// Iterates <paramref name="value"/>, raising a <c>TypeError</c> when it is not
    /// iterable.
    /// </summary>
    public static IEnumerable<PyObject> RequireIterable(PyObject value) =>
        value.Iterate()
        ?? (value as PyGenerator)?.Iterate()
        ?? throw new PyRaise(PyErrors.TypeError($"'{value.TypeName}' object is not iterable"));

    /// <summary>
    /// A pull function over an iterable, returning null at the end.
    /// </summary>
    /// <remarks>
    /// This is not <c>RequireIterable().GetEnumerator()</c>: a C# iterator block that
    /// throws reports itself finished afterwards, so a Python source whose
    /// <c>__next__</c> raises would silently be skipped rather than raising again on the
    /// next call. Pulling from the object itself keeps the source in place.
    /// </remarks>
    public static Func<PyObject?> Puller(PyObject value)
    {
        switch (value)
        {
            case PyIterator iterator:
                return iterator.Next;

            case PyGenerator generator:
                return generator.Next;

            case PyInstance instance when instance.Dunder("__next__") is not null:
                return () =>
                {
                    try
                    {
                        return instance.Invoke(instance.Dunder("__next__")!, []);
                    }
                    catch (PyRaise raise)
                        when (raise.Exception.ExceptionType == PyExceptionType.StopIteration)
                    {
                        return null;
                    }
                };

            case PyInstance outer when outer.Dunder("__iter__") is { } start:
                return Puller(outer.Invoke(start, []));

            default:
            {
                var items = RequireIterable(value).GetEnumerator();
                return () => items.MoveNext() ? items.Current : null;
            }
        }
    }
}

/// <summary>Per-run resource caps.</summary>
public sealed record ExecutionLimits
{
    /// <summary>The defaults.</summary>
    public static ExecutionLimits Default { get; } = new();

    /// <summary>Maximum bytecode instructions executed. Default 50 million.</summary>
    public long MaxInstructions { get; init; } = 50_000_000;

    /// <summary>Maximum Python call depth. Default 200.</summary>
    public int MaxRecursionDepth { get; init; } = 200;

    /// <summary>Maximum characters written to stdout and stderr. Default 10 MB.</summary>
    public int MaxOutputCharacters { get; init; } = 10_000_000;
}
