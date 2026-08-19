using System.Globalization;
using System.Text;
using Bashkit.Builtins;
using Bashkit.Parsing;

namespace Bashkit.Interpreter;

/// <summary>
/// Evaluates a parsed script.
/// </summary>
/// <remarks>
/// <para>
/// The interpreter is a tree walker that threads an <see cref="ExecResult"/> through every
/// node. Output is returned rather than written, so a pipeline is just feeding one node's
/// result into the next node's stdin, and command substitution is just running a nested
/// script and taking its stdout.
/// </para>
/// <para>
/// One instance handles one execution and is not reentrant across threads.
/// </para>
/// </remarks>
public sealed class Interpreter
{
    private readonly IReadOnlyDictionary<string, IBuiltin> _builtins;
    private readonly StringBuilder _stderr = new();
    private readonly HashSet<string> _aliasesInProgress = new(StringComparer.Ordinal);
    private int _loopDepth;

    /// <summary>Creates an interpreter over the given state, filesystem and builtin table.</summary>
    public Interpreter(
        ShellState state,
        IFileSystem fileSystem,
        ExecutionBudget budget,
        IReadOnlyDictionary<string, IBuiltin> builtins)
    {
        State = state;
        FileSystem = fileSystem;
        Budget = budget;
        _builtins = builtins;
        Expander = new Expander(state, fileSystem, budget, RunSubstitutionAsync);
    }

    /// <summary>The shell state this interpreter mutates.</summary>
    public ShellState State { get; }

    /// <summary>The virtual filesystem.</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>The execution budget.</summary>
    public ExecutionBudget Budget { get; }

    /// <summary>The word expander bound to this interpreter.</summary>
    public Expander Expander { get; }

    /// <summary>The shell capabilities handed to builtins that call back into the shell.</summary>
    public Builtins.ShellHooks Hooks => _hooks ??= new Builtins.ShellHooks(
        RunFragment: RunFragmentAsync,
        RunCommand: RunBuiltinDirectlyAsync,
        IsBuiltin: HasBuiltin,
        BuiltinNames: () => _builtins.Keys);

    private Builtins.ShellHooks? _hooks;

    /// <summary>Runs a whole script and returns its combined result.</summary>
    public async ValueTask<ExecResult> RunAsync(Script script, StreamData? stdin = null, CancellationToken cancellationToken = default)
    {
        var stdout = new StringBuilder();
        var result = ExecResult.Success;

        foreach (var command in script.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await ExecuteAsync(command, stdin, cancellationToken);
            stdout.Append(result.Stdout.ToString());
            AppendStderr(result.Stderr);
            State.LastExitCode = result.ExitCode;

            if (result.ControlFlow.Kind == ControlFlowKind.Exit)
            {
                return Finish(stdout, result.ControlFlow.Level);
            }

            if (State.Options.ErrExit && result.ExitCode != 0 && !result.ErrExitSuppressed)
            {
                return Finish(stdout, result.ExitCode);
            }

            // Only the first command's stdin is the script's stdin.
            stdin = null;
        }

        return Finish(stdout, result.ExitCode);
    }

    private ExecResult Finish(StringBuilder stdout, int exitCode)
    {
        var output = Truncate(stdout.ToString(), out var stdoutTruncated);
        var errors = Truncate(_stderr.ToString(), out var stderrTruncated);

        return new ExecResult
        {
            Stdout = output,
            Stderr = errors,
            ExitCode = exitCode,
            StdoutTruncated = stdoutTruncated,
            StderrTruncated = stderrTruncated,
        };
    }

    private StreamData Truncate(string text, out bool truncated)
    {
        var data = StreamData.FromText(text);
        truncated = data.Length > Budget.Limits.MaxOutputBytes;
        return truncated ? data.Truncate((int)Budget.Limits.MaxOutputBytes) : data;
    }

    private void AppendStderr(StreamData data)
    {
        if (!data.IsEmpty)
        {
            _stderr.Append(data.ToString());
        }
    }

    /// <summary>Executes one node.</summary>
    public async ValueTask<ExecResult> ExecuteAsync(Node node, StreamData? stdin, CancellationToken cancellationToken)
    {
        Budget.ThrowIfExpired();

        return node switch
        {
            SimpleCommand simple => await ExecuteSimpleAsync(simple, stdin, cancellationToken),
            Pipeline pipeline => await ExecutePipelineAsync(pipeline, stdin, cancellationToken),
            CommandList list => await ExecuteListAsync(list, stdin, cancellationToken),
            CompoundCommand compound => await ExecuteCompoundAsync(compound, stdin, cancellationToken),
            IfCommand ifCommand => await ExecuteIfAsync(ifCommand, stdin, cancellationToken),
            WhileCommand whileCommand => await ExecuteWhileAsync(whileCommand, stdin, negate: false, cancellationToken),
            UntilCommand untilCommand => await ExecuteWhileAsync(
                new WhileCommand(untilCommand.Condition, untilCommand.Body), stdin, negate: true, cancellationToken),
            ForCommand forCommand => await ExecuteForAsync(forCommand, stdin, cancellationToken),
            ArithmeticForCommand arithFor => await ExecuteArithmeticForAsync(arithFor, stdin, cancellationToken),
            CaseCommand caseCommand => await ExecuteCaseAsync(caseCommand, stdin, cancellationToken),
            Subshell subshell => await ExecuteSubshellAsync(subshell, stdin, cancellationToken),
            BraceGroup group => await ExecuteAsync(group.Body, stdin, cancellationToken),
            ArithmeticCommand arithmetic => ExecuteArithmetic(arithmetic),
            ConditionalCommand conditional => await ExecuteConditionalAsync(conditional, cancellationToken),
            FunctionDef definition => DefineFunction(definition),
            _ => ExecResult.Success,
        };
    }

    private ExecResult DefineFunction(FunctionDef definition)
    {
        State.Functions[definition.Name] = definition;
        return ExecResult.Success;
    }

    private ExecResult ExecuteArithmetic(ArithmeticCommand command)
    {
        try
        {
            // `(( expr ))` succeeds when the expression is non-zero — the opposite of the
            // usual C convention, and a classic source of off-by-one bugs in ports.
            var value = ArithmeticEvaluator.Evaluate(State, command.Expression);
            return ExecResult.FromExitCode(value != 0 ? 0 : 1);
        }
        catch (ShellArithmeticException e)
        {
            return ExecResult.Error($"bash: {e.Message}\n", ExitCodes.Failure);
        }
    }

    private async ValueTask<ExecResult> ExecuteListAsync(CommandList list, StreamData? stdin, CancellationToken cancellationToken)
    {
        var left = await ExecuteAsync(list.Left, stdin, cancellationToken);
        State.LastExitCode = left.ExitCode;

        if (!left.ControlFlow.IsNone)
        {
            return left;
        }

        if (list.Right is null)
        {
            return left;
        }

        // `set -e` aborts the rest of a `;` sequence, not just the rest of the script.
        if (list.Operator == ListOperator.Sequence
            && State.Options.ErrExit
            && left.ExitCode != 0
            && !left.ErrExitSuppressed)
        {
            return left;
        }

        var shouldRun = list.Operator switch
        {
            ListOperator.And => left.ExitCode == 0,
            ListOperator.Or => left.ExitCode != 0,
            _ => true,
        };

        if (!shouldRun)
        {
            // A short-circuited list is not an errexit trigger: `false && x` is a
            // deliberate test, not a failure.
            return left with { ErrExitSuppressed = list.Operator is ListOperator.And or ListOperator.Or };
        }

        var right = await ExecuteAsync(list.Right, list.Operator == ListOperator.Sequence ? null : null, cancellationToken);
        State.LastExitCode = right.ExitCode;

        var suppressed = list.Operator is ListOperator.And or ListOperator.Or
            ? right.ErrExitSuppressed
            : right.ErrExitSuppressed;

        return right with
        {
            Stdout = StreamData.Concat(left.Stdout, right.Stdout),
            Stderr = StreamData.Concat(left.Stderr, right.Stderr),
            ErrExitSuppressed = suppressed,
        };
    }

    private async ValueTask<ExecResult> ExecutePipelineAsync(Pipeline pipeline, StreamData? stdin, CancellationToken cancellationToken)
    {
        var current = stdin;
        var stderr = new StringBuilder();
        var statuses = new List<int>(pipeline.Stages.Count);
        ExecResult result = ExecResult.Success;

        for (var i = 0; i < pipeline.Stages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await ExecuteAsync(pipeline.Stages[i], current, cancellationToken);
            statuses.Add(result.ExitCode);

            var last = i == pipeline.Stages.Count - 1;

            if (last)
            {
                stderr.Append(result.Stderr.ToString());
                break;
            }

            // `|&` folds the stage's stderr into the next stage's stdin; otherwise stderr
            // bypasses the pipe entirely, exactly as with real processes.
            if (i < pipeline.PipeStderr.Count && pipeline.PipeStderr[i])
            {
                current = StreamData.Concat(result.Stdout, result.Stderr);
            }
            else
            {
                stderr.Append(result.Stderr.ToString());
                current = result.Stdout;
            }

            if (!result.ControlFlow.IsNone)
            {
                break;
            }
        }

        State.PipeStatus = statuses;

        var exitCode = State.Options.PipeFail
            ? statuses.LastOrDefault(static status => status != 0, 0)
            : statuses.Count > 0 ? statuses[^1] : 0;

        if (pipeline.Negated)
        {
            exitCode = exitCode == 0 ? 1 : 0;
        }

        return result with
        {
            Stderr = StreamData.FromText(stderr.ToString()),
            ExitCode = exitCode,
            // `! cmd` is an explicit test of failure, so it must not trip errexit.
            ErrExitSuppressed = pipeline.Negated,
        };
    }

    private async ValueTask<ExecResult> ExecuteCompoundAsync(CompoundCommand compound, StreamData? stdin, CancellationToken cancellationToken)
    {
        var redirection = await Redirection.PrepareAsync(this, compound.Redirects, stdin, cancellationToken);
        var result = await ExecuteAsync(compound.Body, redirection.Stdin, cancellationToken);
        return await redirection.ApplyAsync(result, cancellationToken);
    }

    private async ValueTask<ExecResult> ExecuteIfAsync(IfCommand command, StreamData? stdin, CancellationToken cancellationToken)
    {
        var condition = await ExecuteAsync(command.Condition, stdin, cancellationToken);
        State.LastExitCode = condition.ExitCode;

        if (!condition.ControlFlow.IsNone)
        {
            return condition;
        }

        var branch = condition.ExitCode == 0 ? command.Then : command.Else;

        if (branch is null)
        {
            // An `if` with no matching branch succeeds, whatever the condition returned.
            return new ExecResult { Stdout = condition.Stdout, Stderr = condition.Stderr };
        }

        var result = await ExecuteAsync(branch, null, cancellationToken);

        return result with
        {
            Stdout = StreamData.Concat(condition.Stdout, result.Stdout),
            Stderr = StreamData.Concat(condition.Stderr, result.Stderr),
        };
    }

    private async ValueTask<ExecResult> ExecuteWhileAsync(WhileCommand command, StreamData? stdin, bool negate, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = 0;
        var iteration = 0;

        _loopDepth++;
        try
        {
            while (true)
            {
                Budget.ChargeLoopIteration(++iteration);
                cancellationToken.ThrowIfCancellationRequested();

                var condition = await ExecuteAsync(command.Condition, stdin, cancellationToken);
                stdout.Append(condition.Stdout.ToString());
                stderr.Append(condition.Stderr.ToString());
                State.LastExitCode = condition.ExitCode;
                stdin = null;

                var passed = negate ? condition.ExitCode != 0 : condition.ExitCode == 0;
                if (!passed)
                {
                    break;
                }

                var body = await ExecuteAsync(command.Body, null, cancellationToken);
                stdout.Append(body.Stdout.ToString());
                stderr.Append(body.Stderr.ToString());
                exitCode = body.ExitCode;
                State.LastExitCode = exitCode;

                if (HandleLoopControlFlow(body, out var propagate))
                {
                    if (propagate is { } signal)
                    {
                        return BuildLoopResult(stdout, stderr, exitCode, signal);
                    }

                    break;
                }
            }
        }
        finally
        {
            _loopDepth--;
        }

        return BuildLoopResult(stdout, stderr, exitCode, ControlFlow.None);
    }

    private async ValueTask<ExecResult> ExecuteForAsync(ForCommand command, StreamData? stdin, CancellationToken cancellationToken)
    {
        var items = command.Items is null
            ? [.. State.Positional]
            : await Expander.ExpandAllAsync(command.Items, cancellationToken);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = 0;
        var iteration = 0;

        _loopDepth++;
        try
        {
            foreach (var item in items)
            {
                Budget.ChargeLoopIteration(++iteration);
                cancellationToken.ThrowIfCancellationRequested();

                State.Set(command.Variable, item);
                var body = await ExecuteAsync(command.Body, stdin, cancellationToken);
                stdin = null;

                stdout.Append(body.Stdout.ToString());
                stderr.Append(body.Stderr.ToString());
                exitCode = body.ExitCode;
                State.LastExitCode = exitCode;

                if (HandleLoopControlFlow(body, out var propagate))
                {
                    if (propagate is { } signal)
                    {
                        return BuildLoopResult(stdout, stderr, exitCode, signal);
                    }

                    break;
                }
            }
        }
        finally
        {
            _loopDepth--;
        }

        return BuildLoopResult(stdout, stderr, exitCode, ControlFlow.None);
    }

    private async ValueTask<ExecResult> ExecuteArithmeticForAsync(ArithmeticForCommand command, StreamData? stdin, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = 0;
        var iteration = 0;

        if (command.Init.Length > 0)
        {
            ArithmeticEvaluator.Evaluate(State, command.Init);
        }

        _loopDepth++;
        try
        {
            while (true)
            {
                Budget.ChargeLoopIteration(++iteration);
                cancellationToken.ThrowIfCancellationRequested();

                // An omitted condition means "always true", as in C.
                if (command.Condition.Length > 0 && ArithmeticEvaluator.Evaluate(State, command.Condition) == 0)
                {
                    break;
                }

                var body = await ExecuteAsync(command.Body, stdin, cancellationToken);
                stdin = null;

                stdout.Append(body.Stdout.ToString());
                stderr.Append(body.Stderr.ToString());
                exitCode = body.ExitCode;
                State.LastExitCode = exitCode;

                if (HandleLoopControlFlow(body, out var propagate))
                {
                    if (propagate is { } signal)
                    {
                        return BuildLoopResult(stdout, stderr, exitCode, signal);
                    }

                    break;
                }

                if (command.Update.Length > 0)
                {
                    ArithmeticEvaluator.Evaluate(State, command.Update);
                }
            }
        }
        finally
        {
            _loopDepth--;
        }

        return BuildLoopResult(stdout, stderr, exitCode, ControlFlow.None);
    }

    /// <summary>
    /// Interprets a loop body's control flow. Returns true when the loop must stop, with
    /// <paramref name="propagate"/> set when the signal targets an outer loop or the caller.
    /// </summary>
    private static bool HandleLoopControlFlow(ExecResult body, out ControlFlow? propagate)
    {
        propagate = null;
        var flow = body.ControlFlow;

        switch (flow.Kind)
        {
            case ControlFlowKind.None:
                return false;

            case ControlFlowKind.Break:
            {
                var remaining = flow.Unwind();
                if (!remaining.IsNone)
                {
                    propagate = remaining;
                }

                return true;
            }

            case ControlFlowKind.Continue:
            {
                var remaining = flow.Unwind();
                if (!remaining.IsNone)
                {
                    propagate = remaining;
                    return true;
                }

                return false;
            }

            default:
                propagate = flow;
                return true;
        }
    }

    private static ExecResult BuildLoopResult(StringBuilder stdout, StringBuilder stderr, int exitCode, ControlFlow flow) =>
        new()
        {
            Stdout = StreamData.FromText(stdout.ToString()),
            Stderr = StreamData.FromText(stderr.ToString()),
            ExitCode = exitCode,
            ControlFlow = flow,
        };

    private async ValueTask<ExecResult> ExecuteCaseAsync(CaseCommand command, StreamData? stdin, CancellationToken cancellationToken)
    {
        var subject = await Expander.ExpandToStringAsync(command.Subject, cancellationToken);

        for (var i = 0; i < command.Items.Count; i++)
        {
            var item = command.Items[i];
            var matched = false;

            foreach (var pattern in item.Patterns)
            {
                var text = await Expander.ExpandToPatternAsync(pattern, cancellationToken);
                if (PatternMatcher.IsMatch(subject, text, State.Options.NoCaseMatch, State.Options.ExtGlob))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            var result = item.Body is null
                ? ExecResult.Success
                : await ExecuteAsync(item.Body, stdin, cancellationToken);

            switch (item.Terminator)
            {
                case CaseTerminator.FallThrough when i + 1 < command.Items.Count:
                {
                    var next = command.Items[i + 1];
                    if (next.Body is not null)
                    {
                        var extra = await ExecuteAsync(next.Body, null, cancellationToken);
                        result = extra with
                        {
                            Stdout = StreamData.Concat(result.Stdout, extra.Stdout),
                            Stderr = StreamData.Concat(result.Stderr, extra.Stderr),
                        };
                    }

                    return result;
                }

                case CaseTerminator.ContinueMatching:
                    continue;

                default:
                    return result;
            }
        }

        return ExecResult.Success;
    }

    private async ValueTask<ExecResult> ExecuteSubshellAsync(Subshell subshell, StreamData? stdin, CancellationToken cancellationToken)
    {
        // The subshell runs against a forked state, so its assignments, `cd` and option
        // changes are discarded when it finishes. The filesystem is deliberately shared:
        // a subshell writing a file is visible outside, exactly as with a real fork.
        var fork = State.Fork();
        var nested = new Interpreter(fork, FileSystem, Budget, _builtins);
        var result = await nested.ExecuteAsync(subshell.Body, stdin, cancellationToken);

        State.LastExitCode = result.ExitCode;

        // `exit` inside a subshell terminates only the subshell.
        if (result.ControlFlow.Kind == ControlFlowKind.Exit)
        {
            return result with { ControlFlow = ControlFlow.None, ExitCode = result.ControlFlow.Level };
        }

        return result with { ControlFlow = ControlFlow.None };
    }

    private async ValueTask<ExecResult> ExecuteConditionalAsync(ConditionalCommand command, CancellationToken cancellationToken)
    {
        var matched = await ConditionalEvaluator.EvaluateAsync(this, command.Expression, cancellationToken);
        return ExecResult.FromExitCode(matched ? 0 : 1);
    }

    private async ValueTask<ExecResult> ExecuteSimpleAsync(SimpleCommand command, StreamData? stdin, CancellationToken cancellationToken)
    {
        // A bare assignment list with no command name assigns in the current shell.
        if (command.Words.Count == 0)
        {
            var redirectionOnly = await Redirection.PrepareAsync(this, command.Redirects, stdin, cancellationToken);
            foreach (var assignment in command.Assignments)
            {
                await ApplyAssignmentAsync(assignment, cancellationToken);
            }

            return await redirectionOnly.ApplyAsync(ExecResult.Success, cancellationToken);
        }

        Budget.ChargeCommand();

        var words = await Expander.ExpandAllAsync(command.Words, cancellationToken);
        if (words.Count == 0)
        {
            foreach (var assignment in command.Assignments)
            {
                await ApplyAssignmentAsync(assignment, cancellationToken);
            }

            return ExecResult.Success;
        }

        var name = words[0];
        var arguments = words[1..];

        // An alias substitutes textually for the command word before dispatch. It is
        // resolved here rather than in the parser because aliases can be defined by the
        // very script being run, so the parser has not seen them yet.
        if (State.Options.ExpandAliases
            && !_aliasesInProgress.Contains(name)
            && State.Aliases.TryGetValue(name, out var alias))
        {
            var redirected = await Redirection.PrepareAsync(this, command.Redirects, stdin, cancellationToken);
            _aliasesInProgress.Add(name);
            try
            {
                var expandedLine = alias + (arguments.Count > 0 ? " " + string.Join(' ', arguments.Select(Expander_Quote)) : string.Empty);
                var aliasResult = await RunFragmentAsync(expandedLine, redirected.Stdin, cancellationToken);
                return await redirected.ApplyAsync(aliasResult, cancellationToken);
            }
            finally
            {
                _aliasesInProgress.Remove(name);
            }
        }

        var redirection = await Redirection.PrepareAsync(this, command.Redirects, stdin, cancellationToken);

        if (State.Options.XTrace)
        {
            _stderr.Append("+ ").Append(string.Join(' ', words)).Append('\n');
        }

        ExecResult result;
        try
        {
            result = await DispatchAsync(name, arguments, command.Assignments, redirection.Stdin, cancellationToken);
        }
        catch (BashkitException e) when (e.Kind is BashkitErrorKind.Internal or BashkitErrorKind.PermissionDenied)
        {
            result = ExecResult.Error($"bash: {e.Message}\n", ExitCodes.Failure);
        }

        return await redirection.ApplyAsync(result, cancellationToken);
    }

    private async ValueTask<ExecResult> DispatchAsync(
        string name,
        List<string> arguments,
        IReadOnlyList<Assignment> assignments,
        StreamData? stdin,
        CancellationToken cancellationToken)
    {
        if (State.Functions.TryGetValue(name, out var function))
        {
            return await CallFunctionAsync(function, arguments, assignments, stdin, cancellationToken);
        }

        if (!_builtins.TryGetValue(name, out var builtin))
        {
            return ExecResult.Error($"bash: {name}: command not found\n", ExitCodes.NotFound);
        }

        // Assignments preceding a command apply only for that command's duration.
        var saved = await ApplyTemporaryAssignmentsAsync(assignments, cancellationToken);
        try
        {
            var context = new BuiltinContext(name, arguments, State, FileSystem, Budget, stdin, Hooks);
            return await builtin.ExecuteAsync(context, cancellationToken);
        }
        finally
        {
            RestoreTemporaryAssignments(saved);
        }
    }

    private async ValueTask<ExecResult> CallFunctionAsync(
        FunctionDef function,
        List<string> arguments,
        IReadOnlyList<Assignment> assignments,
        StreamData? stdin,
        CancellationToken cancellationToken)
    {
        using var frame = Budget.EnterFunction();

        var savedPositional = State.Positional;
        var savedAssignments = await ApplyTemporaryAssignmentsAsync(assignments, cancellationToken);

        State.Positional = arguments;
        State.CallStack.Add(function.Name);
        State.PushScope();

        try
        {
            var result = await ExecuteAsync(function.Body, stdin, cancellationToken);

            // `return` unwinds only to the function boundary.
            if (result.ControlFlow.Kind == ControlFlowKind.Return)
            {
                return result with
                {
                    ControlFlow = ControlFlow.None,
                    ExitCode = result.ControlFlow.Level,
                };
            }

            return result;
        }
        finally
        {
            State.PopScope();
            State.CallStack.RemoveAt(State.CallStack.Count - 1);
            State.Positional = savedPositional;
            RestoreTemporaryAssignments(savedAssignments);
        }
    }

    private async ValueTask<List<(string Name, string? Value, bool Existed)>> ApplyTemporaryAssignmentsAsync(
        IReadOnlyList<Assignment> assignments,
        CancellationToken cancellationToken)
    {
        if (assignments.Count == 0)
        {
            return [];
        }

        var saved = new List<(string, string?, bool)>(assignments.Count);

        foreach (var assignment in assignments)
        {
            var existing = State.Lookup(assignment.Name);
            saved.Add((assignment.Name, existing?.Value, existing is not null));
            await ApplyAssignmentAsync(assignment, cancellationToken);

            // A prefix assignment is exported to the command it prefixes.
            State.GetOrCreate(assignment.Name).Attributes |= VariableAttributes.Exported;
        }

        return saved;
    }

    private void RestoreTemporaryAssignments(List<(string Name, string? Value, bool Existed)> saved)
    {
        foreach (var (name, value, existed) in saved)
        {
            if (existed && value is not null)
            {
                State.Set(name, value);
            }
            else
            {
                State.Unset(name);
            }
        }
    }

    /// <summary>Applies one assignment to the shell state.</summary>
    public async ValueTask ApplyAssignmentAsync(Assignment assignment, CancellationToken cancellationToken)
    {
        var variable = State.GetOrCreate(assignment.Name);

        if (variable.IsReadOnly)
        {
            throw new BashkitException(BashkitErrorKind.PermissionDenied, $"{assignment.Name}: readonly variable");
        }

        switch (assignment.Value)
        {
            case AssignmentValue.Array array:
            {
                var values = new List<string>();
                foreach (var element in array.Elements)
                {
                    if (element.Key is { } key)
                    {
                        var elementValue = await Expander.ExpandToStringAsync(element.Value, cancellationToken);
                        if (variable.IsAssociative)
                        {
                            variable.SetAssociative(StripQuotes(key), elementValue);
                        }
                        else
                        {
                            variable.SetIndexed(ArithmeticEvaluator.Evaluate(State, key), elementValue);
                        }

                        continue;
                    }

                    values.AddRange(await Expander.ExpandAsync(element.Value, cancellationToken));
                }

                if (values.Count > 0 || array.Elements.Count == 0)
                {
                    if (assignment.Append)
                    {
                        variable.AppendArray(values);
                    }
                    else
                    {
                        variable.SetArray(values);
                    }
                }

                return;
            }

            case AssignmentValue.Scalar scalar:
            {
                var value = await Expander.ExpandToStringAsync(scalar.Word, cancellationToken);

                if (assignment.Index is { } index)
                {
                    if (variable.IsAssociative)
                    {
                        var key = StripQuotes(index);
                        variable.SetAssociative(key, assignment.Append ? (variable.GetElement(key) ?? string.Empty) + value : value);
                    }
                    else
                    {
                        var key = ArithmeticEvaluator.Evaluate(State, index);
                        var previous = assignment.Append
                            ? variable.GetElement(key.ToString(CultureInfo.InvariantCulture)) ?? string.Empty
                            : string.Empty;
                        variable.SetIndexed(key, previous + value);
                    }

                    return;
                }

                if (variable.Attributes.HasFlag(VariableAttributes.Integer))
                {
                    var numeric = ArithmeticEvaluator.Evaluate(State, assignment.Append ? variable.Value + "+" + value : value);
                    variable.SetScalar(numeric.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                if (assignment.Append && variable.IsArray)
                {
                    variable.AppendArray([value]);
                    return;
                }

                if (assignment.Append)
                {
                    variable.AppendScalar(value);
                    return;
                }

                variable.SetScalar(value);

                if (State.Options.AllExport)
                {
                    variable.Attributes |= VariableAttributes.Exported;
                }

                return;
            }
        }
    }

    /// <summary>
    /// Re-quotes an already-expanded argument so that re-parsing an alias expansion does
    /// not split or glob it a second time.
    /// </summary>
    private static string Expander_Quote(string value) => Expander.Quote(value);

    private static string StripQuotes(string text) =>
        text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''))
            ? text[1..^1]
            : text;

    /// <summary>
    /// Runs a nested script for command substitution. It shares the parent's variables so
    /// that <c>$(echo $x)</c> sees <c>x</c>, but its own <c>exit</c> does not end the caller.
    /// </summary>
    private async ValueTask<ExecResult> RunSubstitutionAsync(string script, CancellationToken cancellationToken)
    {
        Budget.ThrowIfExpired();
        using var nesting = Budget.EnterNesting();

        var parsed = Parser.Parse(script, Budget);
        var nested = new Interpreter(State, FileSystem, Budget, _builtins);
        var result = await nested.RunAsync(parsed, null, cancellationToken);

        AppendStderr(result.Stderr);
        return result with { Stderr = StreamData.Empty, ControlFlow = ControlFlow.None };
    }

    /// <summary>Runs a script fragment in the current shell, as <c>eval</c> and <c>source</c> do.</summary>
    public async ValueTask<ExecResult> RunFragmentAsync(string script, StreamData? stdin, CancellationToken cancellationToken)
    {
        using var nesting = Budget.EnterNesting();
        var parsed = Parser.Parse(script, Budget);
        var nested = new Interpreter(State, FileSystem, Budget, _builtins);
        return await nested.RunAsync(parsed, stdin, cancellationToken);
    }

    /// <summary>
    /// Runs one already-expanded command line, bypassing function lookup. This is what
    /// <c>command</c> needs: the words are final, and a shell function of the same name
    /// must not shadow the builtin.
    /// </summary>
    public async ValueTask<ExecResult> RunBuiltinDirectlyAsync(
        IReadOnlyList<string> words,
        StreamData? stdin,
        CancellationToken cancellationToken)
    {
        if (words.Count == 0 || !_builtins.TryGetValue(words[0], out var builtin))
        {
            return ExecResult.Error($"bash: {(words.Count > 0 ? words[0] : string.Empty)}: command not found\n", ExitCodes.NotFound);
        }

        Budget.ChargeCommand();
        var context = new BuiltinContext(words[0], [.. words.Skip(1)], State, FileSystem, Budget, stdin, Hooks);
        return await builtin.ExecuteAsync(context, cancellationToken);
    }

    /// <summary>True when <paramref name="name"/> resolves to a registered builtin.</summary>
    public bool HasBuiltin(string name) => _builtins.ContainsKey(name);

    /// <summary>The names of every registered builtin.</summary>
    public IEnumerable<string> BuiltinNames => _builtins.Keys;
}
