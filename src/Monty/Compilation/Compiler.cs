using System.Numerics;
using Monty.Parsing;
using Monty.Runtime;

namespace Monty.Compilation;

/// <summary>
/// Lowers a parsed module into bytecode.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="Compiler"/> instance compiles one code object; nested functions get their
/// own, chained by <see cref="_parent"/> so that closure capture can be resolved.
/// </para>
/// <para>
/// Scope is decided in a pre-pass over each function body: a name assigned anywhere in a
/// function is local <i>throughout</i> it, even before the assignment. That is why
/// <c>x = 1; def f(): print(x); x = 2</c> raises <c>UnboundLocalError</c> rather than
/// printing — and why the pre-pass cannot be skipped.
/// </para>
/// </remarks>
public sealed class Compiler
{
    private readonly CodeObject _code;
    private readonly Compiler? _parent;
    private readonly HashSet<string> _locals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalDeclarations = new(StringComparer.Ordinal);

    // What this scope has assigned or read so far, in statement order, so a later `global`
    // declaration can be reported as coming too late. Imports are excluded: CPython treats
    // an import binding as a soft one that a following `global` may still override.
    private readonly HashSet<string> _assignedHere = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readHere = new(StringComparer.Ordinal);
    private bool _bindingImport;
    private readonly HashSet<string> _nonlocalDeclarations = new(StringComparer.Ordinal);
    private readonly List<LoopContext> _loops = [];
    private readonly List<FinallyContext> _finallies = [];
    private readonly bool _isFunctionScope;

    /// <summary>The name a class body records its annotations under.</summary>
    private const string AnnotationsName = "__annotations__";

    private bool _isClassBody;

    /// <summary>
    /// Whether this scope is a comprehension's implicit function.
    /// </summary>
    /// <remarks>
    /// A walrus inside a comprehension binds in the *enclosing* scope, not the
    /// comprehension's, so this scope must not claim the target as a local.
    /// </remarks>
    private bool _isComprehension;

    private Compiler(string name, string fileName, Compiler? parent, bool isFunctionScope)
    {
        _code = new CodeObject(name, fileName);
        _parent = parent;
        _isFunctionScope = isFunctionScope;
    }

    /// <summary>Compiles a module into its top-level code object.</summary>
    public static CodeObject CompileModule(PyModule module, string fileName)
    {
        var compiler = new Compiler("<module>", fileName, null, isFunctionScope: false);
        compiler.CompileStatements(module.Body);
        compiler.Emit(OpCode.LoadConst, compiler._code.AddConstant(PyNone.Instance), 0);
        compiler.Emit(OpCode.Return, 0, 0);
        return compiler._code;
    }

    /// <summary>
    /// Cleanup owed to an enclosing <c>try</c>/<c>finally</c> or <c>with</c>, which a jump
    /// out of it has to run first.
    /// </summary>
    /// <param name="Body">The finally body, or null for a <c>with</c>.</param>
    /// <param name="LoopDepth">How many loops enclosed the block when it was opened.</param>
    private sealed record FinallyContext(IReadOnlyList<Statement>? Body, int LoopDepth);

    private sealed record LoopContext(List<int> BreakJumps, List<int> ContinueJumps, int ContinueTarget)
    {
        public int ContinueTarget { get; set; } = ContinueTarget;

        /// <summary>True for a <c>for</c>, which keeps its iterator on the value stack.</summary>
        public bool HasIterator { get; init; }
    }

    private int Emit(OpCode opCode, int operand, int line)
    {
        _code.Instructions.Add(new Instruction(opCode, operand, line));
        return _code.Instructions.Count - 1;
    }

    private int Here => _code.Instructions.Count;

    private void Patch(int index, int target) =>
        _code.Instructions[index] = _code.Instructions[index] with { Operand = target };

    private void PatchAll(IEnumerable<int> indices, int target)
    {
        foreach (var index in indices)
        {
            Patch(index, target);
        }
    }

    // ---- scope ----

    /// <summary>
    /// Collects every name a function body binds, so that reads of those names compile to
    /// local access even when they appear before the assignment.
    /// </summary>
    private void CollectBindings(IReadOnlyList<Statement> body)
    {
        foreach (var statement in body)
        {
            CollectBindings(statement);
        }
    }

    private void CollectBindings(Statement statement)
    {
        switch (statement)
        {
            case Assign assign:
                foreach (var target in assign.Targets)
                {
                    CollectTargetNames(target);
                }

                break;

            case AugmentedAssign augmented:
                CollectTargetNames(augmented.Target);
                break;

            case FunctionDef function:
                Declare(function.Name);
                break;

            case ClassDef classDef:
                Declare(classDef.Name);
                break;

            case For loop:
                CollectTargetNames(loop.Target);
                CollectBindings(loop.Body);
                CollectBindings(loop.OrElse);
                break;

            case While loop:
                CollectBindings(loop.Body);
                CollectBindings(loop.OrElse);
                break;

            case If branch:
                CollectBindings(branch.Body);
                CollectBindings(branch.OrElse);
                break;

            case Try tryStatement:
                CollectBindings(tryStatement.Body);
                CollectBindings(tryStatement.OrElse);
                CollectBindings(tryStatement.FinallyBody);
                foreach (var handler in tryStatement.Handlers)
                {
                    if (handler.Name is { } name)
                    {
                        Declare(name);
                    }

                    CollectBindings(handler.Body);
                }

                break;

            case With with:
                foreach (var item in with.Items)
                {
                    if (item.Target is { } target)
                    {
                        CollectTargetNames(target);
                    }
                }

                CollectBindings(with.Body);
                break;

            case Import import:
                foreach (var alias in import.Names)
                {
                    Declare(alias.Alias ?? alias.Name.Split('.')[0]);
                }

                break;

            case ImportFrom importFrom:
                foreach (var alias in importFrom.Names)
                {
                    Declare(alias.Alias ?? alias.Name);
                }

                break;

            case Global global:
                foreach (var name in global.Names)
                {
                    _globalDeclarations.Add(name);
                }

                break;

            case Nonlocal nonlocal:
                // There is no enclosing function to bind to at module level, so the
                // declaration cannot mean anything there.
                if (!_isFunctionScope)
                {
                    throw new PythonSyntaxError("nonlocal declaration not allowed at module level", nonlocal.Line, nonlocal.Column);
                }

                foreach (var name in nonlocal.Names)
                {
                    _nonlocalDeclarations.Add(name);
                }

                break;
        }
    }

    private void CollectTargetNames(Expression target)
    {
        switch (target)
        {
            case Parsing.Name name:
                Declare(name.Id);
                break;

            case TupleExpr tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectTargetNames(element);
                }

                break;

            case ListExpr list:
                foreach (var element in list.Elements)
                {
                    CollectTargetNames(element);
                }

                break;

            case Starred starred:
                CollectTargetNames(starred.Value);
                break;
        }
    }

    private void Declare(string name)
    {
        if (_globalDeclarations.Contains(name) || _nonlocalDeclarations.Contains(name))
        {
            return;
        }

        _locals.Add(name);
    }

    /// <summary>Where a name lives, which decides which load and store opcodes to emit.</summary>
    private enum Binding
    {
        Local,
        Cell,
        Global,
    }

    private Binding Resolve(string name)
    {
        if (_globalDeclarations.Contains(name))
        {
            return Binding.Global;
        }

        if (_nonlocalDeclarations.Contains(name))
        {
            RegisterCellChain(name);
            return Binding.Cell;
        }

        if (!_isFunctionScope)
        {
            // At module level, locals and globals are the same namespace.
            return Binding.Global;
        }

        if (_locals.Contains(name))
        {
            return Binding.Local;
        }

        // A name bound in an enclosing function is captured; otherwise it is a global. A
        // class body is not such a scope: a method's bare name reaches past its class to
        // the module, which is why `helper` in a method is never the class attribute.
        for (var scope = _parent; scope is not null; scope = scope._parent)
        {
            if (!scope._isFunctionScope || scope._isClassBody || !scope._locals.Contains(name))
            {
                continue;
            }

            RegisterCellChain(name);
            return Binding.Cell;
        }

        return Binding.Global;
    }

    /// <summary>
    /// Marks <paramref name="name"/> as a cell in every scope from the one that defines it
    /// down to this one.
    /// </summary>
    /// <remarks>
    /// Without this the defining scope would store only to its local slot, and the
    /// capturing scope would read a cell nobody ever wrote. Registering the whole chain is
    /// what makes the variable shared rather than copied.
    /// </remarks>
    private void RegisterCellChain(string name)
    {
        Compiler? definer = null;

        for (var scope = _parent; scope is not null; scope = scope._parent)
        {
            if (scope._isFunctionScope && !scope._isClassBody
                && (scope._locals.Contains(name) || scope._code.CellNames.Contains(name)))
            {
                definer = scope;
                break;
            }
        }

        for (var link = this; link is not null; link = link._parent)
        {
            link.CellSlot(name);

            if (ReferenceEquals(link, definer))
            {
                break;
            }
        }
    }

    private void EmitLoad(string name, int line)
    {
        _readHere.Add(name);

        switch (Resolve(name))
        {
            case Binding.Local:
                // A class body's members are dynamic: a name read before it is bound falls
                // through to the module rather than raising.
                Emit(_isClassBody ? OpCode.LoadName : OpCode.LoadLocal, _code.LocalSlot(name), line);
                break;

            case Binding.Cell:
                Emit(OpCode.LoadCell, CellSlot(name), line);
                break;

            default:
                Emit(OpCode.LoadGlobal, _code.AddName(name), line);
                break;
        }
    }

    private void EmitStore(string name, int line)
    {
        if (!_bindingImport)
        {
            _assignedHere.Add(name);
        }

        switch (Resolve(name))
        {
            case Binding.Local:
                Emit(OpCode.StoreLocal, _code.LocalSlot(name), line);
                break;

            case Binding.Cell:
                Emit(OpCode.StoreCell, CellSlot(name), line);
                break;

            default:
                Emit(OpCode.StoreGlobal, _code.AddName(name), line);
                break;
        }
    }

    private int CellSlot(string name)
    {
        var index = _code.CellNames.IndexOf(name);

        if (index >= 0)
        {
            return index;
        }

        _code.CellNames.Add(name);
        return _code.CellNames.Count - 1;
    }

    // ---- statements ----

    private void CompileStatements(IReadOnlyList<Statement> statements)
    {
        foreach (var statement in statements)
        {
            CompileStatement(statement);
        }
    }

    private void CompileStatement(Statement statement)
    {
        switch (statement)
        {
            case ExpressionStatement expression:
                CompileExpression(expression.Value);
                Emit(OpCode.Pop, 0, expression.Line);
                break;

            case Assign assign:
                CompileAssign(assign);
                break;

            case AugmentedAssign augmented:
                CompileAugmentedAssign(augmented);
                break;

            case If branch:
                CompileIf(branch);
                break;

            case While loop:
                CompileWhile(loop);
                break;

            case For loop:
                CompileFor(loop);
                break;

            case Try tryStatement:
                CompileTry(tryStatement);
                break;

            case With with:
                CompileWith(with);
                break;

            case FunctionDef function:
                CompileFunctionDef(function);
                break;

            case ClassDef classDef:
                CompileClassDef(classDef);
                break;

            case Return returnStatement:
                if (returnStatement.Value is { } value)
                {
                    CompileExpression(value);
                }
                else
                {
                    Emit(OpCode.LoadConst, _code.AddConstant(PyNone.Instance), returnStatement.Line);
                }

                // The return value is computed before the `finally` bodies run, and waits
                // on the stack while they do — which is why `return f()` inside a `try`
                // calls `f` first and the cleanup second.
                UnwindFinallies(returnStatement.Line, all: true);
                Emit(OpCode.Return, 0, returnStatement.Line);
                break;

            case Raise raise:
                CompileRaise(raise);
                break;

            case Assert assertion:
                CompileAssert(assertion);
                break;

            case Delete delete:
                foreach (var target in delete.Targets)
                {
                    CompileDelete(target);
                }

                break;

            case Pass:
                break;

            case Break breakStatement:
                if (_loops.Count == 0)
                {
                    throw new PythonSyntaxError("'break' outside loop", breakStatement.Line, breakStatement.Column);
                }

                UnwindFinallies(breakStatement.Line);

                // A `for` leaves its iterator on the stack for the whole loop, and only the
                // exhaustion path pops it — so a `break` has to pop it itself.
                if (_loops[^1].HasIterator)
                {
                    Emit(OpCode.Pop, 0, breakStatement.Line);
                }

                _loops[^1].BreakJumps.Add(Emit(OpCode.Jump, 0, breakStatement.Line));
                break;

            case Continue continueStatement:
                if (_loops.Count == 0)
                {
                    throw new PythonSyntaxError("'continue' not properly in loop", continueStatement.Line, continueStatement.Column);
                }

                UnwindFinallies(continueStatement.Line);
                _loops[^1].ContinueJumps.Add(Emit(OpCode.Jump, 0, continueStatement.Line));
                break;

            case Global global:
                // A declaration emits nothing; it only takes part in the pre-pass. What it
                // can do here is complain, because ordering matters: the name must not have
                // been used in this scope before the declaration was seen.
                foreach (var name in global.Names)
                {
                    if (_assignedHere.Contains(name))
                    {
                        throw new PythonSyntaxError(
                            $"name '{name}' is assigned to before global declaration",
                            global.Line,
                            global.Column);
                    }

                    if (_readHere.Contains(name))
                    {
                        throw new PythonSyntaxError(
                            $"name '{name}' is used prior to global declaration",
                            global.Line,
                            global.Column);
                    }
                }

                break;

            case Nonlocal moduleNonlocal when !_isFunctionScope:
                // There is no enclosing function to bind to at module level, so the
                // declaration cannot mean anything there.
                throw new PythonSyntaxError(
                    "nonlocal declaration not allowed at module level",
                    moduleNonlocal.Line,
                    moduleNonlocal.Column);

            case Nonlocal:
                break;

            case Import import:
                _bindingImport = true;

                foreach (var alias in import.Names)
                {
                    Emit(OpCode.ImportName, _code.AddName(alias.Name), import.Line);
                    EmitStore(alias.Alias ?? alias.Name.Split('.')[0], import.Line);
                }

                _bindingImport = false;
                break;

            case ImportFrom importFrom:
                Emit(OpCode.ImportName, _code.AddName(importFrom.Module), importFrom.Line);
                _bindingImport = true;

                foreach (var alias in importFrom.Names)
                {
                    Emit(OpCode.Duplicate, 0, importFrom.Line);
                    Emit(OpCode.ImportFrom, _code.AddName(alias.Name), importFrom.Line);
                    EmitStore(alias.Alias ?? alias.Name, importFrom.Line);
                }

                _bindingImport = false;
                Emit(OpCode.Pop, 0, importFrom.Line);
                break;

            default:
                throw new PythonSyntaxError($"unsupported statement {statement.GetType().Name}", statement.Line, statement.Column);
        }
    }

    private void CompileAssign(Assign assign)
    {
        // In a class body an annotation is data: it names a field, whether or not the
        // statement also assigns a default.
        if (_isClassBody && assign.Annotation is { } annotation
            && assign.Targets is [Parsing.Name annotated])
        {
            CompileExpression(annotation);
            EmitLoad(AnnotationsName, assign.Line);
            Emit(OpCode.LoadConst, _code.AddConstant(new PyStr(annotated.Id)), assign.Line);
            Emit(OpCode.StoreSubscript, 0, assign.Line);
        }

        // A bare annotation (`x: int`) declares without assigning.
        if (assign.Value is null)
        {
            return;
        }

        CompileExpression(assign.Value);

        for (var i = 0; i < assign.Targets.Count; i++)
        {
            // Every target but the last needs its own copy of the value.
            if (i < assign.Targets.Count - 1)
            {
                Emit(OpCode.Duplicate, 0, assign.Line);
            }

            CompileStoreTarget(assign.Targets[i], assign.Line);
        }
    }

    private void CompileStoreTarget(Expression target, int line)
    {
        switch (target)
        {
            case Parsing.Name name:
                EmitStore(name.Id, line);
                break;

            case Parsing.Attribute attribute:
                CompileExpression(attribute.Value);
                Emit(OpCode.StoreAttr, _code.AddName(attribute.AttributeName), line);
                break;

            case Subscript subscript:
                CompileExpression(subscript.Value);
                CompileExpression(subscript.Index);
                Emit(OpCode.StoreSubscript, 0, line);
                break;

            case TupleExpr tuple:
                CompileUnpack(tuple.Elements, line);
                break;

            case ListExpr list:
                CompileUnpack(list.Elements, line);
                break;

            default:
                throw new PythonSyntaxError("cannot assign to expression", line, 0);
        }
    }

    private void CompileUnpack(IReadOnlyList<Expression> targets, int line)
    {
        var starIndex = -1;

        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i] is Starred)
            {
                starIndex = i;
                break;
            }
        }

        if (starIndex < 0)
        {
            Emit(OpCode.UnpackSequence, targets.Count, line);
        }
        else
        {
            // The operand packs the star's position and the total count, so the VM knows
            // how many trailing targets must be left over for it.
            Emit(OpCode.UnpackStarred, (starIndex << 16) | targets.Count, line);
        }

        foreach (var target in targets)
        {
            CompileStoreTarget(target is Starred starred ? starred.Value : target, line);
        }
    }

    private void CompileAugmentedAssign(AugmentedAssign augmented)
    {
        // The target is read, combined and written back; a subscript target must not have
        // its index expression evaluated twice, so it is duplicated on the stack instead.
        switch (augmented.Target)
        {
            case Parsing.Name name:
                EmitLoad(name.Id, augmented.Line);
                CompileExpression(augmented.Value);

                // The `=` is kept on the operator name so the runtime can mutate a list or
                // a set in place rather than rebinding the name to a new object.
                Emit(OpCode.BinaryOp, _code.AddName(augmented.Operator + "="), augmented.Line);
                EmitStore(name.Id, augmented.Line);
                break;

            case Parsing.Attribute attribute:
                CompileExpression(attribute.Value);
                Emit(OpCode.Duplicate, 0, augmented.Line);
                Emit(OpCode.LoadAttr, _code.AddName(attribute.AttributeName), augmented.Line);
                CompileExpression(augmented.Value);
                Emit(OpCode.BinaryOp, _code.AddName(augmented.Operator + "="), augmented.Line);
                Emit(OpCode.Swap, 0, augmented.Line);
                Emit(OpCode.StoreAttr, _code.AddName(attribute.AttributeName), augmented.Line);
                break;

            case Subscript subscript:
                CompileExpression(subscript.Value);
                CompileExpression(subscript.Index);
                Emit(OpCode.Duplicate, 2, augmented.Line);
                Emit(OpCode.LoadSubscript, 0, augmented.Line);
                CompileExpression(augmented.Value);
                Emit(OpCode.BinaryOp, _code.AddName(augmented.Operator + "="), augmented.Line);
                Emit(OpCode.RotateThree, 0, augmented.Line);
                Emit(OpCode.StoreSubscript, 0, augmented.Line);
                break;

            default:
                throw new PythonSyntaxError("illegal target for augmented assignment", augmented.Line, augmented.Column);
        }
    }

    private void CompileDelete(Expression target)
    {
        switch (target)
        {
            case Parsing.Name name:
                if (Resolve(name.Id) == Binding.Local)
                {
                    Emit(OpCode.DeleteLocal, _code.LocalSlot(name.Id), name.Line);
                }
                else
                {
                    Emit(OpCode.DeleteGlobal, _code.AddName(name.Id), name.Line);
                }

                break;

            case Subscript subscript:
                CompileExpression(subscript.Value);
                CompileExpression(subscript.Index);
                Emit(OpCode.DeleteSubscript, 0, subscript.Line);
                break;

            default:
                throw new PythonSyntaxError("cannot delete expression", target.Line, target.Column);
        }
    }

    private void CompileIf(If branch)
    {
        CompileExpression(branch.Test);
        var jumpOverBody = Emit(OpCode.JumpIfFalse, 0, branch.Line);
        CompileStatements(branch.Body);

        if (branch.OrElse.Count == 0)
        {
            Patch(jumpOverBody, Here);
            return;
        }

        var jumpOverElse = Emit(OpCode.Jump, 0, branch.Line);
        Patch(jumpOverBody, Here);
        CompileStatements(branch.OrElse);
        Patch(jumpOverElse, Here);
    }

    private void CompileWhile(While loop)
    {
        var top = Here;
        CompileExpression(loop.Test);
        var exit = Emit(OpCode.JumpIfFalse, 0, loop.Line);

        var context = new LoopContext([], [], top);
        _loops.Add(context);
        CompileStatements(loop.Body);
        _loops.RemoveAt(_loops.Count - 1);

        PatchAll(context.ContinueJumps, top);
        Emit(OpCode.Jump, top, loop.Line);
        Patch(exit, Here);

        // The `else` clause runs only when the loop ended without a `break`.
        CompileStatements(loop.OrElse);
        PatchAll(context.BreakJumps, Here);
    }

    private void CompileFor(For loop)
    {
        CompileExpression(loop.Iterable);
        Emit(OpCode.GetIterator, 0, loop.Line);

        var top = Here;
        var exit = Emit(OpCode.ForIterate, 0, loop.Line);

        CompileStoreTarget(loop.Target, loop.Line);

        var context = new LoopContext([], [], top) { HasIterator = true };
        _loops.Add(context);
        CompileStatements(loop.Body);
        _loops.RemoveAt(_loops.Count - 1);

        PatchAll(context.ContinueJumps, top);
        Emit(OpCode.Jump, top, loop.Line);
        Patch(exit, Here);

        CompileStatements(loop.OrElse);

        // `break` skips the `else` and lands after it, having popped the iterator itself —
        // the exhaustion path through `ForIterate` pops it on the other route.
        var afterElse = Here;
        PatchAll(context.BreakJumps, afterElse);
    }

    private void CompileTry(Try tryStatement)
    {
        if (tryStatement.FinallyBody.Count > 0)
        {
            CompileTryFinally(tryStatement);
            return;
        }

        CompileTryExcept(tryStatement);
    }

    /// <summary>
    /// Runs the cleanup owed to every <c>finally</c> the jump is leaving.
    /// </summary>
    /// <remarks>
    /// <c>break</c> and <c>continue</c> leave a <c>try</c> block just as <c>return</c>
    /// does, so their finallys have to run before the jump — otherwise the cleanup a script
    /// wrote is silently skipped, which is exactly the bug <c>finally</c> exists to prevent.
    /// </remarks>
    /// <summary>
    /// Runs the <c>finally</c> bodies a jump out of them would skip.
    /// </summary>
    /// <param name="line">The line to attribute the emitted code to.</param>
    /// <param name="all">
    /// True to unwind every enclosing <c>finally</c>, as a <c>return</c> does; false to
    /// stop at the current loop, which is as far as a <c>break</c> or <c>continue</c> goes.
    /// </param>
    private void UnwindFinallies(int line, bool all = false)
    {
        var pending = new List<FinallyContext>(_finallies);

        try
        {
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                if (!all && pending[i].LoopDepth != _loops.Count)
                {
                    break;
                }

                // A `with` has no body to re-emit: its cleanup is the manager's exit,
                // which is already on the stack, and which ExitWith finds for itself.
                if (pending[i].Body is not { } body)
                {
                    Emit(OpCode.ExitWith, 0, line);
                    continue;
                }

                Emit(OpCode.PopBlock, 0, line);

                // A `return` inside the body being emitted must not unwind this same body
                // again, so it is dropped from the pending set while it is compiled.
                _finallies.RemoveRange(i, _finallies.Count - i);
                CompileStatements(body);
            }
        }
        finally
        {
            _finallies.Clear();
            _finallies.AddRange(pending);
        }
    }

    private void CompileTryFinally(Try tryStatement)
    {
        var setup = Emit(OpCode.SetupFinally, 0, tryStatement.Line);
        _finallies.Add(new FinallyContext(tryStatement.FinallyBody, _loops.Count));

        try
        {
            if (tryStatement.Handlers.Count > 0 || tryStatement.OrElse.Count > 0)
            {
                CompileTryExcept(tryStatement);
            }
            else
            {
                CompileStatements(tryStatement.Body);
            }
        }
        finally
        {
            _finallies.RemoveAt(_finallies.Count - 1);
        }

        Emit(OpCode.PopBlock, 0, tryStatement.Line);

        // The finally body is emitted twice: once on the normal path and once as the
        // handler the VM jumps to while unwinding. Duplicating the code is what lets a
        // `return` inside the try run the cleanup without a second mechanism.
        CompileStatements(tryStatement.FinallyBody);
        var skip = Emit(OpCode.Jump, 0, tryStatement.Line);

        Patch(setup, Here);
        CompileStatements(tryStatement.FinallyBody);
        Emit(OpCode.ReRaise, 0, tryStatement.Line);
        Patch(skip, Here);
    }

    private void CompileTryExcept(Try tryStatement)
    {
        if (tryStatement.Handlers.Count == 0)
        {
            CompileStatements(tryStatement.Body);
            CompileStatements(tryStatement.OrElse);
            return;
        }

        var setup = Emit(OpCode.SetupExcept, 0, tryStatement.Line);
        CompileStatements(tryStatement.Body);
        Emit(OpCode.PopBlock, 0, tryStatement.Line);

        CompileStatements(tryStatement.OrElse);
        var skipHandlers = Emit(OpCode.Jump, 0, tryStatement.Line);

        Patch(setup, Here);

        var handlerExits = new List<int>();

        foreach (var handler in tryStatement.Handlers)
        {
            int? nextHandler = null;

            if (handler.ExceptionType is { } type)
            {
                CompileExpression(type);
                Emit(OpCode.MatchException, 0, handler.Line);
                nextHandler = Emit(OpCode.JumpIfFalse, 0, handler.Line);
            }

            if (handler.Name is { } name)
            {
                Emit(OpCode.PushCurrentException, 0, handler.Line);
                EmitStore(name, handler.Line);
            }

            // The exception stays current for the whole handler, so a bare `raise` inside
            // it re-raises what was caught; it is cleared only on the way out.
            CompileStatements(handler.Body);
            Emit(OpCode.EndHandler, 0, handler.Line);
            handlerExits.Add(Emit(OpCode.Jump, 0, handler.Line));

            if (nextHandler is { } jump)
            {
                Patch(jump, Here);
            }
        }

        // No handler matched, so the exception continues outward.
        Emit(OpCode.ReRaise, 0, tryStatement.Line);

        Patch(skipHandlers, Here);
        PatchAll(handlerExits, Here);
    }

    /// <summary>
    /// Compiles a <c>with</c>, including the exception path.
    /// </summary>
    /// <remarks>
    /// Several managers on one <c>with</c> are compiled as nested single-manager ones,
    /// which is how the language defines them — and what makes an exception swallowed by an
    /// inner manager still run the outer one's exit.
    /// </remarks>
    private void CompileWith(With with) => CompileWithItem(with, 0);

    private void CompileWithItem(With with, int index)
    {
        var item = with.Items[index];
        CompileExpression(item.ContextManager);
        var setup = Emit(OpCode.SetupWith, 0, with.Line);

        if (item.Target is { } target)
        {
            CompileStoreTarget(target, with.Line);
        }
        else
        {
            Emit(OpCode.Pop, 0, with.Line);
        }

        // A `return`, `break` or `continue` leaving the body has to run this exit, the same
        // way it has to run an enclosing `finally`.
        _finallies.Add(new FinallyContext(null, _loops.Count));

        try
        {
            if (index + 1 < with.Items.Count)
            {
                CompileWithItem(with, index + 1);
            }
            else
            {
                CompileStatements(with.Body);
            }
        }
        finally
        {
            _finallies.RemoveAt(_finallies.Count - 1);
        }

        Emit(OpCode.ExitWith, 0, with.Line);
        var done = Emit(OpCode.Jump, 0, with.Line);

        Patch(setup, Here);
        Emit(OpCode.ExitWithException, 0, with.Line);
        Emit(OpCode.ReRaise, 0, with.Line);
        Patch(done, Here);
    }

    private void CompileRaise(Raise raise)
    {
        if (raise.Exception is null)
        {
            Emit(OpCode.Raise, 0, raise.Line);
            return;
        }

        CompileExpression(raise.Exception);

        if (raise.Cause is { } cause)
        {
            CompileExpression(cause);
            Emit(OpCode.Raise, 2, raise.Line);
            return;
        }

        Emit(OpCode.Raise, 1, raise.Line);
    }

    private void CompileAssert(Assert assertion)
    {
        CompileExpression(assertion.Test);
        var skip = Emit(OpCode.JumpIfTrue, 0, assertion.Line);

        if (assertion.Message is { } message)
        {
            CompileExpression(message);
            Emit(OpCode.Assert, 1, assertion.Line);
        }
        else
        {
            Emit(OpCode.Assert, 0, assertion.Line);
        }

        Patch(skip, Here);
    }

    private void CompileFunctionDef(FunctionDef function)
    {
        var code = CompileFunctionBody(
            function.Name, function.Parameters, function.Body, function.Line, isGeneratorHint: null);

        code.IsCoroutine = function.IsAsync;

        // Decorator *expressions* evaluate top to bottom, but the decorators *apply*
        // bottom up. Pushing every expression first, then the function, leaves the stack
        // as [d0 … dn-1, f] — so each Call(1) naturally consumes the innermost pair.
        foreach (var decorator in function.Decorators)
        {
            CompileExpression(decorator);
        }

        EmitDefaults(function.Parameters, function.Line);
        _code.NestedCode.Add(code);
        Emit(OpCode.MakeFunction, _code.NestedCode.Count - 1, function.Line);

        for (var i = 0; i < function.Decorators.Count; i++)
        {
            Emit(OpCode.Call, 1, function.Line);
        }

        EmitStore(function.Name, function.Line);
    }

    private CodeObject CompileFunctionBody(
        string name,
        ParameterList parameters,
        IReadOnlyList<Statement> body,
        int line,
        bool? isGeneratorHint,
        bool isClassBody = false)
    {
        var compiler = new Compiler(name, _code.FileName, this, isFunctionScope: true)
        {
            _isClassBody = isClassBody,
        };

        // Two parameters of one name would share a slot, so the second would silently win.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in parameters.Parameters
            .Concat(parameters.KeywordOnly)
            .Select(static p => p.Name)
            .Concat(parameters.VarArgs is null ? [] : [parameters.VarArgs])
            .Concat(parameters.KeywordArgs is null ? [] : [parameters.KeywordArgs]))
        {
            if (!seen.Add(parameter))
            {
                throw new PythonSyntaxError(
                    $"duplicate argument '{parameter}' in function definition", line, 0);
            }
        }

        // Parameters occupy the first local slots, in declaration order.
        foreach (var parameter in parameters.Parameters)
        {
            compiler._locals.Add(parameter.Name);
            compiler._code.LocalSlot(parameter.Name);
        }

        if (parameters.VarArgs is { } varArgs)
        {
            compiler._locals.Add(varArgs);
            compiler._code.LocalSlot(varArgs);
        }

        foreach (var parameter in parameters.KeywordOnly)
        {
            compiler._locals.Add(parameter.Name);
            compiler._code.LocalSlot(parameter.Name);
        }

        if (parameters.KeywordArgs is { } keywordArgs)
        {
            compiler._locals.Add(keywordArgs);
            compiler._code.LocalSlot(keywordArgs);
        }

        compiler.CollectBindings(body);
        compiler._code.Parameters = parameters;
        compiler._code.IsGenerator = isGeneratorHint ?? ContainsYield(body);

        // A class that annotates any of its names gets an `__annotations__` mapping, which
        // is where `@dataclass` reads its fields from — the annotations are the fields.
        if (isClassBody && body.Any(static statement => statement is Assign { Annotation: not null }))
        {
            compiler.Declare(AnnotationsName);
            compiler.Emit(OpCode.BuildMap, 0, line);
            compiler.EmitStore(AnnotationsName, line);
        }

        // A class's leading string literal is its docstring, which becomes `__doc__` in
        // the namespace — unless the body assigns one itself, which then wins.
        if (isClassBody && body is [ExpressionStatement { Value: Literal { Value: string docstring } }, ..])
        {
            compiler.Declare("__doc__");
            compiler.Emit(OpCode.LoadConst, compiler._code.AddConstant(new PyStr(docstring)), line);
            compiler.EmitStore("__doc__", line);
        }

        compiler.CompileStatements(body);
        compiler.Emit(OpCode.LoadConst, compiler._code.AddConstant(PyNone.Instance), line);
        compiler.Emit(OpCode.Return, 0, line);

        return compiler._code;
    }

    /// <summary>
    /// Emits the default-value map a function definition captures.
    /// </summary>
    /// <remarks>
    /// Defaults are evaluated once, at definition time, in the <i>enclosing</i> scope —
    /// which is why <c>def f(x=[])</c> shares one list across calls. Folding them at
    /// compile time would both change that and reject every non-literal default.
    /// </remarks>
    private void EmitDefaults(ParameterList parameters, int line)
    {
        var withDefaults = parameters.Parameters
            .Concat(parameters.KeywordOnly)
            .Where(static parameter => parameter.Default is not null)
            .ToList();

        foreach (var parameter in withDefaults)
        {
            Emit(OpCode.LoadConst, _code.AddConstant(new PyStr(parameter.Name)), line);
            CompileExpression(parameter.Default!);
        }

        Emit(OpCode.BuildMap, withDefaults.Count, line);
    }

    private static bool ContainsYield(IReadOnlyList<Statement> body) =>
        body.Any(ContainsYield);

    private static bool ContainsYield(Statement statement) => statement switch
    {
        ExpressionStatement expression => ContainsYield(expression.Value),
        Assign assign => assign.Value is not null && ContainsYield(assign.Value),
        AugmentedAssign augmented => ContainsYield(augmented.Value),
        Return returnStatement => returnStatement.Value is not null && ContainsYield(returnStatement.Value),
        If branch => ContainsYield(branch.Body) || ContainsYield(branch.OrElse),
        While loop => ContainsYield(loop.Body) || ContainsYield(loop.OrElse),
        For loop => ContainsYield(loop.Body) || ContainsYield(loop.OrElse),
        With with => ContainsYield(with.Body),
        Try tryStatement => ContainsYield(tryStatement.Body)
            || ContainsYield(tryStatement.OrElse)
            || ContainsYield(tryStatement.FinallyBody)
            || tryStatement.Handlers.Any(handler => ContainsYield(handler.Body)),
        _ => false,
    };

    private static bool ContainsYield(Expression expression) => expression switch
    {
        Yield => true,
        BinaryOp binary => ContainsYield(binary.Left) || ContainsYield(binary.Right),
        UnaryOp unary => ContainsYield(unary.Operand),
        Call call => ContainsYield(call.Function) || call.Arguments.Any(ContainsYield),
        Conditional conditional => ContainsYield(conditional.Test)
            || ContainsYield(conditional.Body)
            || ContainsYield(conditional.OrElse),
        _ => false,
    };

    private void CompileClassDef(ClassDef classDef)
    {
        var body = CompileFunctionBody(
            classDef.Name, ParameterList.Empty, classDef.Body, classDef.Line, isGeneratorHint: false, isClassBody: true);

        foreach (var decorator in classDef.Decorators)
        {
            CompileExpression(decorator);
        }

        Emit(OpCode.LoadConst, _code.AddConstant(new PyStr(classDef.Name)), classDef.Line);
        Emit(OpCode.BuildMap, 0, classDef.Line);
        _code.NestedCode.Add(body);
        Emit(OpCode.MakeFunction, _code.NestedCode.Count - 1, classDef.Line);
        Emit(OpCode.MakeClass, 0, classDef.Line);

        for (var i = 0; i < classDef.Decorators.Count; i++)
        {
            Emit(OpCode.Call, 1, classDef.Line);
        }

        EmitStore(classDef.Name, classDef.Line);
    }

    // ---- expressions ----

    private void CompileExpression(Expression expression)
    {
        switch (expression)
        {
            case Literal literal:
                Emit(OpCode.LoadConst, _code.AddConstant(Literals.ToPyObject(literal.Value)), literal.Line);
                break;

            case Parsing.Name name:
                EmitLoad(name.Id, name.Line);
                break;

            case BinaryOp binary:
                CompileExpression(binary.Left);
                CompileExpression(binary.Right);
                Emit(OpCode.BinaryOp, _code.AddName(binary.Operator), binary.Line);
                break;

            case UnaryOp unary:
                CompileExpression(unary.Operand);
                Emit(OpCode.UnaryOp, _code.AddName(unary.Operator), unary.Line);
                break;

            case BoolOp boolOp:
                CompileBoolOp(boolOp);
                break;

            case Compare compare:
                CompileCompare(compare);
                break;

            case Conditional conditional:
                CompileExpression(conditional.Test);
                var elseJump = Emit(OpCode.JumpIfFalse, 0, conditional.Line);
                CompileExpression(conditional.Body);
                var endJump = Emit(OpCode.Jump, 0, conditional.Line);
                Patch(elseJump, Here);
                CompileExpression(conditional.OrElse);
                Patch(endJump, Here);
                break;

            case Call call:
                CompileCall(call);
                break;

            case Parsing.Attribute attribute:
                CompileExpression(attribute.Value);
                Emit(OpCode.LoadAttr, _code.AddName(attribute.AttributeName), attribute.Line);
                break;

            case Subscript subscript:
                CompileExpression(subscript.Value);
                CompileExpression(subscript.Index);
                Emit(OpCode.LoadSubscript, 0, subscript.Line);
                break;

            case Slice slice:
                CompileSliceOperand(slice.Lower, slice.Line);
                CompileSliceOperand(slice.Upper, slice.Line);
                CompileSliceOperand(slice.Step, slice.Line);
                Emit(OpCode.BuildSlice, 3, slice.Line);
                break;

            case ListExpr list:
                CompileSequence(list.Elements, OpCode.BuildList, OpCode.ListExtend, list.Line);
                break;

            case TupleExpr tuple:
                CompileSequence(tuple.Elements, OpCode.BuildTuple, OpCode.ListExtend, tuple.Line);
                break;

            case SetExpr set:
                CompileSequence(set.Elements, OpCode.BuildSet, OpCode.SetUpdate, set.Line);
                break;

            case DictExpr dict:
                CompileDict(dict);
                break;

            case Comprehension comprehension:
                CompileComprehension(comprehension);
                break;

            case FormattedString formatted:
                CompileFormattedString(formatted);
                break;

            case Lambda lambda:
                CompileLambda(lambda);
                break;

            case NamedExpr named:
                CompileExpression(named.Value);
                Emit(OpCode.Duplicate, 0, named.Line);

                // A walrus binds in the scope it appears in, which the declaration pre-pass
                // does not reach: it walks statements, and this is an expression. Inside a
                // comprehension the binding belongs to the enclosing scope instead.
                if (!_isComprehension)
                {
                    Declare(named.Target.Id);
                }
                EmitStore(named.Target.Id, named.Line);
                break;

            case Yield yield:
                if (yield.Value is { } yielded)
                {
                    CompileExpression(yielded);
                }
                else
                {
                    Emit(OpCode.LoadConst, _code.AddConstant(PyNone.Instance), yield.Line);
                }

                Emit(yield.IsDelegating ? OpCode.YieldFrom : OpCode.Yield, 0, yield.Line);
                break;

            case Await await:
                CompileExpression(await.Value);
                Emit(OpCode.Await, 0, await.Line);
                break;

            case Starred starred:
                CompileExpression(starred.Value);
                break;

            default:
                throw new PythonSyntaxError(
                    $"unsupported expression {expression.GetType().Name}", expression.Line, expression.Column);
        }
    }

    private void CompileSliceOperand(Expression? operand, int line)
    {
        if (operand is null)
        {
            Emit(OpCode.LoadConst, _code.AddConstant(PyNone.Instance), line);
            return;
        }

        CompileExpression(operand);
    }

    private void CompileBoolOp(BoolOp boolOp)
    {
        var jumps = new List<int>();
        var shortCircuit = boolOp.Operator == "and" ? OpCode.JumpIfFalseOrPop : OpCode.JumpIfTrueOrPop;

        for (var i = 0; i < boolOp.Values.Count; i++)
        {
            CompileExpression(boolOp.Values[i]);

            if (i < boolOp.Values.Count - 1)
            {
                jumps.Add(Emit(shortCircuit, 0, boolOp.Line));
            }
        }

        PatchAll(jumps, Here);
    }

    private void CompileCompare(Compare compare)
    {
        CompileExpression(compare.Left);

        if (compare.Operators.Count == 1)
        {
            CompileExpression(compare.Comparators[0]);
            Emit(OpCode.CompareOp, _code.AddName(compare.Operators[0]), compare.Line);
            return;
        }

        // A chain evaluates each operand once and stops at the first false comparison, so
        // the intermediate value is kept on the stack and rotated into place.
        var exits = new List<int>();

        for (var i = 0; i < compare.Operators.Count; i++)
        {
            CompileExpression(compare.Comparators[i]);

            if (i < compare.Operators.Count - 1)
            {
                Emit(OpCode.Duplicate, 0, compare.Line);
                Emit(OpCode.RotateThree, 0, compare.Line);
            }

            Emit(OpCode.CompareOp, _code.AddName(compare.Operators[i]), compare.Line);

            if (i < compare.Operators.Count - 1)
            {
                exits.Add(Emit(OpCode.JumpIfFalseOrPop, 0, compare.Line));
            }
        }

        if (exits.Count == 0)
        {
            return;
        }

        var end = Emit(OpCode.Jump, 0, compare.Line);
        PatchAll(exits, Here);

        // On the short-circuit path the leftover operand must be discarded.
        Emit(OpCode.Swap, 0, compare.Line);
        Emit(OpCode.Pop, 0, compare.Line);
        Patch(end, Here);
    }

    private void CompileSequence(IReadOnlyList<Expression> elements, OpCode build, OpCode extend, int line)
    {
        var hasStar = elements.Any(static e => e is Starred);

        if (!hasStar)
        {
            foreach (var element in elements)
            {
                CompileExpression(element);
            }

            Emit(build, elements.Count, line);
            return;
        }

        // With unpacking, the container is built empty and filled, since the element count
        // is not known until run time.
        Emit(build == OpCode.BuildTuple ? OpCode.BuildList : build, 0, line);

        foreach (var element in elements)
        {
            if (element is Starred starred)
            {
                CompileExpression(starred.Value);
                Emit(extend, 1, line);
                continue;
            }

            CompileExpression(element);
            Emit(build == OpCode.BuildSet ? OpCode.SetAdd : OpCode.ListAppend, 1, line);
        }

        if (build == OpCode.BuildTuple)
        {
            Emit(OpCode.BuildTuple, -1, line);
        }
    }

    private void CompileDict(DictExpr dict)
    {
        var hasUnpacking = dict.Keys.Any(static k => k is null);

        if (!hasUnpacking)
        {
            for (var i = 0; i < dict.Keys.Count; i++)
            {
                CompileExpression(dict.Keys[i]!);
                CompileExpression(dict.Values[i]);
            }

            Emit(OpCode.BuildMap, dict.Keys.Count, dict.Line);
            return;
        }

        Emit(OpCode.BuildMap, 0, dict.Line);

        for (var i = 0; i < dict.Keys.Count; i++)
        {
            if (dict.Keys[i] is null)
            {
                CompileExpression(dict.Values[i]);
                Emit(OpCode.MapUpdate, 1, dict.Line);
                continue;
            }

            CompileExpression(dict.Keys[i]!);
            CompileExpression(dict.Values[i]);
            Emit(OpCode.MapPut, 2, dict.Line);
        }
    }

    private void CompileCall(Call call)
    {
        CompileExpression(call.Function);

        var hasUnpacking = call.Arguments.Any(static a => a is Starred)
            || call.Keywords.Any(static k => k.Name is null);

        if (call.Keywords.Count == 0 && !hasUnpacking)
        {
            foreach (var argument in call.Arguments)
            {
                CompileExpression(argument);
            }

            Emit(OpCode.Call, call.Arguments.Count, call.Line);
            return;
        }

        // The general path packs positionals into a list and keywords into a dict, so one
        // instruction covers every calling convention.
        Emit(OpCode.BuildList, 0, call.Line);

        foreach (var argument in call.Arguments)
        {
            if (argument is Starred starred)
            {
                CompileExpression(starred.Value);
                Emit(OpCode.ListExtend, 1, call.Line);
                continue;
            }

            CompileExpression(argument);
            Emit(OpCode.ListAppend, 1, call.Line);
        }

        Emit(OpCode.BuildMap, 0, call.Line);

        foreach (var keyword in call.Keywords)
        {
            if (keyword.Name is null)
            {
                CompileExpression(keyword.Value);
                Emit(OpCode.MapMerge, 1, call.Line);
                continue;
            }

            Emit(OpCode.LoadConst, _code.AddConstant(new PyStr(keyword.Name)), call.Line);
            CompileExpression(keyword.Value);
            Emit(OpCode.MapPut, 2, call.Line);
        }

        Emit(OpCode.CallKeyword, 0, call.Line);
    }

    private void CompileLambda(Lambda lambda)
    {
        var body = new List<Statement> { new Return(lambda.Body) { Line = lambda.Line } };
        var code = CompileFunctionBody("<lambda>", lambda.Parameters, body, lambda.Line, isGeneratorHint: false);

        EmitDefaults(lambda.Parameters, lambda.Line);
        _code.NestedCode.Add(code);
        Emit(OpCode.MakeFunction, _code.NestedCode.Count - 1, lambda.Line);
    }

    /// <summary>
    /// Compiles a comprehension into a nested function, as CPython does.
    /// </summary>
    /// <remarks>
    /// The implicit function is what gives a comprehension its own scope: the loop variable
    /// must not leak, and in Python 3 it does not.
    /// </remarks>
    private void CompileComprehension(Comprehension comprehension)
    {
        const string IterableParameter = ".0";

        var compiler = new Compiler("<comprehension>", _code.FileName, this, isFunctionScope: true)
        {
            _isComprehension = true,
        };

        compiler._locals.Add(IterableParameter);
        compiler._code.LocalSlot(IterableParameter);
        compiler._code.Parameters = new ParameterList([new Parameter(IterableParameter, null, null)], null, [], null);

        foreach (var clause in comprehension.Clauses)
        {
            compiler.CollectTargetNames(clause.Target);
        }

        var accumulator = comprehension.Kind switch
        {
            ComprehensionKind.List => OpCode.BuildList,
            ComprehensionKind.Set => OpCode.BuildSet,
            ComprehensionKind.Dictionary => OpCode.BuildMap,
            _ => OpCode.BuildList,
        };

        compiler.Emit(accumulator, 0, comprehension.Line);
        compiler.CompileComprehensionClauses(comprehension, 0, isOutermost: true);
        compiler.Emit(OpCode.Return, 0, comprehension.Line);

        Emit(OpCode.BuildMap, 0, comprehension.Line);
        _code.NestedCode.Add(compiler._code);
        Emit(OpCode.MakeFunction, _code.NestedCode.Count - 1, comprehension.Line);

        // The outermost iterable is evaluated in the enclosing scope and passed in.
        CompileExpression(comprehension.Clauses[0].Iterable);
        Emit(OpCode.GetIterator, 0, comprehension.Line);
        Emit(OpCode.Call, 1, comprehension.Line);

        // A generator comprehension yields lazily; materializing then iterating keeps the
        // observable result right for everything except infinite sources.
        if (comprehension.Kind == ComprehensionKind.Generator)
        {
            Emit(OpCode.GetIterator, 0, comprehension.Line);
        }
    }

    private void CompileComprehensionClauses(Comprehension comprehension, int clauseIndex, bool isOutermost)
    {
        if (clauseIndex >= comprehension.Clauses.Count)
        {
            EmitComprehensionElement(comprehension);
            return;
        }

        var clause = comprehension.Clauses[clauseIndex];

        if (isOutermost)
        {
            Emit(OpCode.LoadLocal, _code.LocalSlot(".0"), comprehension.Line);
        }
        else
        {
            CompileExpression(clause.Iterable);
            Emit(OpCode.GetIterator, 0, comprehension.Line);
        }

        var top = Here;
        var exit = Emit(OpCode.ForIterate, 0, comprehension.Line);
        CompileStoreTarget(clause.Target, comprehension.Line);

        var conditionJumps = new List<int>();

        foreach (var condition in clause.Conditions)
        {
            CompileExpression(condition);
            conditionJumps.Add(Emit(OpCode.JumpIfFalse, 0, comprehension.Line));
        }

        CompileComprehensionClauses(comprehension, clauseIndex + 1, isOutermost: false);

        PatchAll(conditionJumps, Here);
        Emit(OpCode.Jump, top, comprehension.Line);
        Patch(exit, Here);
    }

    private void EmitComprehensionElement(Comprehension comprehension)
    {
        // The accumulator sits below every active iterator, so the append depth is one per
        // clause plus one for the accumulator itself.
        var depth = comprehension.Clauses.Count + 1;

        switch (comprehension.Kind)
        {
            case ComprehensionKind.Dictionary:
                CompileExpression(comprehension.Key!);
                CompileExpression(comprehension.Element);
                Emit(OpCode.MapPut, depth + 1, comprehension.Line);
                break;

            case ComprehensionKind.Set:
                CompileExpression(comprehension.Element);
                Emit(OpCode.SetAdd, depth, comprehension.Line);
                break;

            default:
                CompileExpression(comprehension.Element);
                Emit(OpCode.ListAppend, depth, comprehension.Line);
                break;
        }
    }

    private void CompileFormattedString(FormattedString formatted)
    {
        var pieces = 0;

        foreach (var part in formatted.Parts)
        {
            if (part.Value is null)
            {
                Emit(OpCode.LoadConst, _code.AddConstant(new PyStr(part.Literal ?? string.Empty)), formatted.Line);
                pieces++;
                continue;
            }

            // The `f'{x=}'` debug form prints the source text before the value.
            if (part.Literal is { } prefix)
            {
                Emit(OpCode.LoadConst, _code.AddConstant(new PyStr(prefix)), formatted.Line);
                pieces++;
            }

            CompileExpression(part.Value);

            if (part.FormatSpec is { } spec)
            {
                CompileExpression(spec);
            }
            else
            {
                Emit(OpCode.LoadConst, _code.AddConstant(PyNone.Instance), formatted.Line);
            }

            Emit(OpCode.FormatValue, part.Conversion, formatted.Line);
            pieces++;
        }

        Emit(OpCode.BuildString, pieces, formatted.Line);
    }
}

/// <summary>Converts parser literal values into runtime objects.</summary>
internal static class Literals
{
    public static PyObject ToPyObject(object? value) => value switch
    {
        null => PyNone.Instance,
        bool flag => PyBool.Of(flag),
        long integer => new PyInt(integer),
        BigInteger integer => new PyInt(integer),
        double number => new PyFloat(number),
        string text => new PyStr(text),
        byte[] bytes => new PyBytes(bytes),
        Parsing.Ellipsis => PyEllipsis.Instance,
        _ => throw new InvalidOperationException($"unsupported literal type {value.GetType()}"),
    };
}
