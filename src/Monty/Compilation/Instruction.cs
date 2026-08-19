namespace Monty.Compilation;

/// <summary>
/// The virtual machine's instruction set.
/// </summary>
/// <remarks>
/// A stack machine, like CPython's. The operand stack removes the need for register
/// allocation, and the block stack makes <c>try</c>/<c>finally</c> and loop-with-<c>else</c>
/// expressible without any control-flow analysis in the compiler.
/// </remarks>
public enum OpCode
{
    /// <summary>Do nothing. Used as a jump landing pad during assembly.</summary>
    Nop,

    /// <summary>Push a constant from the code object's pool.</summary>
    LoadConst,

    /// <summary>Push a local variable's value; raises if unbound.</summary>
    LoadLocal,

    /// <summary>Pop and store into a local slot.</summary>
    StoreLocal,

    /// <summary>Unbind a local slot.</summary>
    DeleteLocal,

    /// <summary>Push a global or builtin by name.</summary>
    LoadGlobal,

    /// <summary>Pop and store into the module globals.</summary>
    StoreGlobal,

    /// <summary>Remove a global binding.</summary>
    DeleteGlobal,

    /// <summary>Push a closure cell's value.</summary>
    LoadCell,

    /// <summary>Pop and store into a closure cell.</summary>
    StoreCell,

    /// <summary>Push an attribute of the value on top of the stack.</summary>
    LoadAttr,

    /// <summary>Pop a value and an object; set the object's attribute.</summary>
    StoreAttr,

    /// <summary>Pop an index and an object; push the indexed value.</summary>
    LoadSubscript,

    /// <summary>Pop an index, an object and a value; store it.</summary>
    StoreSubscript,

    /// <summary>Pop an index and an object; delete the entry.</summary>
    DeleteSubscript,

    /// <summary>Apply a binary operator named by the operand.</summary>
    BinaryOp,

    /// <summary>Apply a unary operator named by the operand.</summary>
    UnaryOp,

    /// <summary>Apply a comparison named by the operand.</summary>
    CompareOp,

    /// <summary>Build a list from the top N values.</summary>
    BuildList,

    /// <summary>Build a tuple from the top N values.</summary>
    BuildTuple,

    /// <summary>Build a set from the top N values.</summary>
    BuildSet,

    /// <summary>Build a dict from the top 2N values, keys and values interleaved.</summary>
    BuildMap,

    /// <summary>Build a slice from the top 3 values.</summary>
    BuildSlice,

    /// <summary>Build a string by concatenating the top N values.</summary>
    BuildString,

    /// <summary>Format the value on top of the stack, with a spec and conversion.</summary>
    FormatValue,

    /// <summary>Extend the list N slots down with an iterable popped from the top.</summary>
    ListExtend,

    /// <summary>Append the top value to the list N slots down.</summary>
    ListAppend,

    /// <summary>Add the top value to the set N slots down.</summary>
    SetAdd,

    /// <summary>Merge the top mapping into the set N slots down.</summary>
    SetUpdate,

    /// <summary>Store the top key and value into the dict N slots down.</summary>
    MapPut,

    /// <summary>Merge the top mapping into the dict N slots down.</summary>
    MapUpdate,

    /// <summary>Discard the top value.</summary>
    Pop,

    /// <summary>Duplicate the top value.</summary>
    Duplicate,

    /// <summary>Swap the top two values.</summary>
    Swap,

    /// <summary>Rotate the top three values so the top moves down two.</summary>
    RotateThree,

    /// <summary>Jump unconditionally.</summary>
    Jump,

    /// <summary>Pop and jump when falsy.</summary>
    JumpIfFalse,

    /// <summary>Pop and jump when truthy.</summary>
    JumpIfTrue,

    /// <summary>Jump when falsy, leaving the value; otherwise pop. For <c>and</c>.</summary>
    JumpIfFalseOrPop,

    /// <summary>Jump when truthy, leaving the value; otherwise pop. For <c>or</c>.</summary>
    JumpIfTrueOrPop,

    /// <summary>Replace the top value with its iterator.</summary>
    GetIterator,

    /// <summary>Advance the iterator on top; push the next value, or jump when exhausted.</summary>
    ForIterate,

    /// <summary>Unpack the top iterable into N values, pushed in reverse.</summary>
    UnpackSequence,

    /// <summary>Unpack with a starred target at the operand's position.</summary>
    UnpackStarred,

    /// <summary>Call with N positional arguments.</summary>
    Call,

    /// <summary>Call with the argument spec in the operand and a keyword-name tuple on top.</summary>
    CallKeyword,

    /// <summary>Return the top value from the current frame.</summary>
    Return,

    /// <summary>Yield the top value from a generator frame.</summary>
    Yield,

    /// <summary>Delegate to a sub-iterator, for <c>yield from</c>.</summary>
    YieldFrom,

    /// <summary>Build a function from the code object named by the operand.</summary>
    MakeFunction,

    /// <summary>Build a class from a name and a body function.</summary>
    MakeClass,

    /// <summary>Raise; the operand says how many values were supplied.</summary>
    Raise,

    /// <summary>Push an exception handler targeting the operand.</summary>
    SetupExcept,

    /// <summary>Push a finally handler targeting the operand.</summary>
    SetupFinally,

    /// <summary>Push a loop block whose break target is the operand.</summary>
    SetupLoop,

    /// <summary>Pop the innermost block.</summary>
    PopBlock,

    /// <summary>Push the exception currently being handled.</summary>
    PushCurrentException,

    /// <summary>Compare the top value against the handled exception's type.</summary>
    MatchException,

    /// <summary>Mark the handled exception as dealt with.</summary>
    EndHandler,

    /// <summary>Re-raise the exception being handled.</summary>
    ReRaise,

    /// <summary>Enter a context manager, pushing its exit callable.</summary>
    SetupWith,

    /// <summary>Leave a context manager.</summary>
    ExitWith,

    /// <summary>Import a module named by the operand.</summary>
    ImportName,

    /// <summary>Push an attribute of the module on top of the stack.</summary>
    ImportFrom,

    /// <summary>Assert: pop the condition and an optional message.</summary>
    Assert,
}

/// <summary>One instruction: an opcode, an integer operand and the source line it came from.</summary>
/// <param name="OpCode">The operation.</param>
/// <param name="Operand">Its integer operand; the meaning depends on the opcode.</param>
/// <param name="Line">The 1-based source line, for tracebacks.</param>
public readonly record struct Instruction(OpCode OpCode, int Operand, int Line)
{
    /// <inheritdoc />
    public override string ToString() => $"{OpCode} {Operand}";
}
