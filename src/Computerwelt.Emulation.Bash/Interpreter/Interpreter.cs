using System.Globalization;
using System.Text;
using Computerwelt.Emulation.Bash.Builtins;
using Computerwelt.Emulation.Bash.Parsing;

namespace Computerwelt.Emulation.Bash.Interpreter;

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
    private readonly CommandTable _commands;
    private readonly StringBuilder _stderr = new();
    private int _loopDepth;

    /// <summary>Creates an interpreter over the given state, filesystem and command table.</summary>
    public Interpreter(
        ShellState state,
        IFileSystem fileSystem,
        ExecutionBudget budget,
        CommandTable commands)
    {
        State = state;
        FileSystem = fileSystem;
        Budget = budget;
        _commands = commands;
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
        BuiltinNames: () => _commands.Names)
    {
        RunIsolated = RunIsolatedAsync,
        SetStandardInput = data => _standardInput = data is { } value ? new InputStream(value.ToString()) : null,
    };

    private Builtins.ShellHooks? _hooks;

    /// <summary>
    /// The shell's standard input, remembered so command substitutions inherit it and
    /// successive reads advance through it.
    /// </summary>
    private InputStream? _standardInput;

    /// <summary>
    /// The exit status of the most recent command substitution, so a command that is
    /// nothing but assignments reports it: <c>x=$(false)</c> leaves <c>$?</c> at 1.
    /// </summary>
    private int? _lastSubstitutionStatus;

    /// <summary>Runs a whole script and returns its combined result.</summary>
    public async ValueTask<ExecResult> RunAsync(Script script, StreamData? stdin = null, CancellationToken cancellationToken = default)
    {
        var stdout = new StringBuilder();
        var result = ExecResult.Success;
        _standardInput ??= stdin is { } data ? new InputStream(data.ToString()) : null;

        foreach (var command in script.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // `$LINENO` reports the line of the command being run.
            State.CurrentLine = script.LineAt(command.Span.Start);

            result = await ExecuteAsync(command, stdin, cancellationToken);
            result = await ApplyErrTrapAsync(result, cancellationToken);
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

        // A pipe into a compound command feeds the whole body from one consuming stream,
        // not each command from a fresh copy: that is what makes `cmd | while read line`
        // advance line by line instead of seeing the first line forever.
        if (stdin is { } piped && SharesInput(node))
        {
            var savedInput = _standardInput;
            _standardInput = new InputStream(piped.ToString());

            try
            {
                return await DispatchNodeAsync(node, null, cancellationToken);
            }
            finally
            {
                _standardInput = savedInput;
            }
        }

        return await DispatchNodeAsync(node, stdin, cancellationToken);
    }

    /// <summary>True for the node kinds whose body shares one input stream.</summary>
    private static bool SharesInput(Node node) =>
        node is WhileCommand or UntilCommand or ForCommand or ArithmeticForCommand
            or SelectCommand or CaseCommand or IfCommand or CompoundCommand or BraceGroup or Subshell;

    private async ValueTask<ExecResult> DispatchNodeAsync(Node node, StreamData? stdin, CancellationToken cancellationToken)
    {
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
            SelectCommand selectCommand => await ExecuteSelectAsync(selectCommand, cancellationToken),
            CoprocessCommand coprocess => await ExecuteCoprocessAsync(coprocess, cancellationToken),
            ArithmeticForCommand arithFor => await ExecuteArithmeticForAsync(arithFor, stdin, cancellationToken),
            CaseCommand caseCommand => await ExecuteCaseAsync(caseCommand, stdin, cancellationToken),
            Subshell subshell => await ExecuteSubshellAsync(subshell, stdin, cancellationToken),
            BraceGroup group => await ExecuteAsync(group.Body, stdin, cancellationToken),
            ArithmeticCommand arithmetic => await ExecuteArithmeticAsync(arithmetic, cancellationToken),
            ConditionalCommand conditional => await ExecuteConditionalAsync(conditional, cancellationToken),
            TimedCommand timed => await ExecuteTimedAsync(timed, stdin, cancellationToken),
            FunctionDef definition => DefineFunction(definition),
            _ => ExecResult.Success,
        };
    }

    /// <summary>
    /// Runs a trap handler, if one is set for <paramref name="signal"/>.
    /// </summary>
    /// <remarks>
    /// Reentrancy is blocked: an <c>ERR</c> handler that itself fails would otherwise
    /// re-trigger itself forever, and bash suppresses the same way.
    /// </remarks>
    public async ValueTask<ExecResult> RunTrapAsync(string signal, CancellationToken cancellationToken)
    {
        if (State.InTrap || !State.Traps.TryGetValue(signal, out var handler) || handler.Length == 0)
        {
            return ExecResult.Success;
        }

        var savedExitCode = State.LastExitCode;
        State.InTrap = true;

        try
        {
            var result = await RunFragmentAsync(handler, null, cancellationToken);
            return result with { ControlFlow = ControlFlow.None };
        }
        finally
        {
            State.InTrap = false;
            State.LastExitCode = savedExitCode;
        }
    }

    /// <summary>
    /// Fires the <c>ERR</c> trap for a failed command. Uses the same suppression rules as
    /// <c>set -e</c>: a command whose failure was explicitly tested does not count.
    /// </summary>
    private async ValueTask<ExecResult> ApplyErrTrapAsync(ExecResult result, CancellationToken cancellationToken)
    {
        if (result.ExitCode == 0 || result.ErrExitSuppressed || result.ErrTrapHandled
            || !State.Traps.ContainsKey("ERR"))
        {
            return result;
        }

        var trap = await RunTrapAsync("ERR", cancellationToken);

        return result with
        {
            Stdout = StreamData.Concat(result.Stdout, trap.Stdout),
            Stderr = StreamData.Concat(result.Stderr, trap.Stderr),
            ErrTrapHandled = true,
        };
    }

    private ExecResult DefineFunction(FunctionDef definition)
    {
        State.Functions[definition.Name] = definition;
        return ExecResult.Success;
    }

    private async ValueTask<ExecResult> ExecuteArithmeticAsync(ArithmeticCommand command, CancellationToken cancellationToken)
    {
        try
        {
            // `(( expr ))` succeeds when the expression is non-zero — the opposite of the
            // usual C convention, and a classic source of off-by-one bugs in ports.
            var value = await Expander.EvaluateArithmeticAsync(command.Expression, cancellationToken);
            return ExecResult.FromExitCode(value != 0 ? 0 : 1);
        }
        catch (ShellArithmeticException e)
        {
            return ExecResult.Error($"bash: {e.Message}\n", ExitCodes.Failure);
        }
    }

    /// <summary>Runs a <c>time</c>-prefixed pipeline and reports how long it took.</summary>
    /// <remarks>
    /// The report goes to standard error, and the timed command's own status and output
    /// pass through untouched — <c>time cmd</c> must be transparent to everything except
    /// the extra three lines.
    /// </remarks>
    private async ValueTask<ExecResult> ExecuteTimedAsync(TimedCommand command, StreamData? stdin, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var result = command.Body is null
            ? ExecResult.Success
            : await ExecuteAsync(command.Body, stdin, cancellationToken);

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        return result with
        {
            Stderr = StreamData.Concat(result.Stderr, StreamData.FromText(FormatTiming(elapsed, command.Posix))),
        };
    }

    private static string FormatTiming(TimeSpan elapsed, bool posix)
    {
        if (posix)
        {
            var seconds = elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);
            return $"real {seconds}\nuser 0.00\nsys 0.00\n";
        }

        // There are no separate user and system times to report without processes, and
        // claiming a number for them would be a fiction; bash's shape is kept, with zeros.
        var minutes = (int)elapsed.TotalMinutes;
        var rest = (elapsed.TotalSeconds - (minutes * 60)).ToString("F3", CultureInfo.InvariantCulture);
        return $"\nreal\t{minutes}m{rest}s\nuser\t0m0.000s\nsys\t0m0.000s\n";
    }

    private async ValueTask<ExecResult> ExecuteListAsync(CommandList list, StreamData? stdin, CancellationToken cancellationToken)
    {
        var left = await ExecuteAsync(list.Left, stdin, cancellationToken);

        if (list.Operator == ListOperator.Sequence)
        {
            left = await ApplyErrTrapAsync(left, cancellationToken);
        }

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

        // `PIPESTATUS` is a real array so `${PIPESTATUS[1]}` indexes it like any other.
        State.GetOrCreate("PIPESTATUS")
            .SetArray(statuses.Select(static status => status.ToString(CultureInfo.InvariantCulture)));

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

        if (redirection.Failure is { } redirectionFailure)
        {
            return redirectionFailure;
        }
        // A redirection on a compound command scopes one input stream to its whole body,
        // which is what lets a `while read` loop advance through the file.
        var saved = _standardInput;

        if (compound.Redirects.Any(static r => r.Kind is RedirectKind.Input or RedirectKind.HereDocument or RedirectKind.HereString))
        {
            _standardInput = new InputStream(redirection.Stdin?.ToString() ?? string.Empty);
        }

        try
        {
            var result = await ExecuteAsync(compound.Body, null, cancellationToken);
            return await redirection.ApplyAsync(result, cancellationToken);
        }
        finally
        {
            _standardInput = saved;
        }
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

    /// <summary>
    /// Runs <c>select</c>: a menu, a prompt and a loop.
    /// </summary>
    /// <remarks>
    /// The menu and the prompt go to standard error, which is what lets the body's own
    /// output be captured on its own. End of input ends the loop with a failing status,
    /// exactly as reaching the end of a here-document or a pipe does in bash.
    /// </remarks>
    private async ValueTask<ExecResult> ExecuteSelectAsync(SelectCommand command, CancellationToken cancellationToken)
    {
        var items = command.Items is null
            ? [.. State.Positional]
            : await Expander.ExpandAllAsync(command.Items, cancellationToken);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = 0;
        var iteration = 0;
        var prompt = State.Get("PS3") ?? "#? ";

        _loopDepth++;
        try
        {
            while (true)
            {
                Budget.ChargeLoopIteration(++iteration);
                cancellationToken.ThrowIfCancellationRequested();

                for (var i = 0; i < items.Count; i++)
                {
                    stderr.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(") ").Append(items[i]).Append('\n');
                }

                stderr.Append(prompt);

                if (_standardInput?.ReadLine('\n') is not { } reply)
                {
                    // End of input ends the menu, and bash closes it with a newline.
                    stdout.Append('\n');
                    exitCode = ExitCodes.Failure;
                    break;
                }

                State.Set("REPLY", reply);

                // A reply that is not a listed number leaves the variable empty and the
                // menu is shown again; only `break` or end of input leaves the loop.
                var chosen = int.TryParse(reply.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 1 && index <= items.Count
                        ? items[index - 1]
                        : string.Empty;

                State.Set(command.Variable, chosen);

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

    private async ValueTask<ExecResult> ExecuteArithmeticForAsync(ArithmeticForCommand command, StreamData? stdin, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = 0;
        var iteration = 0;

        if (command.Init.Length > 0)
        {
            await Expander.EvaluateArithmeticAsync(command.Init, cancellationToken);
        }

        _loopDepth++;
        try
        {
            while (true)
            {
                Budget.ChargeLoopIteration(++iteration);
                cancellationToken.ThrowIfCancellationRequested();

                // An omitted condition means "always true", as in C.
                if (command.Condition.Length > 0
                    && await Expander.EvaluateArithmeticAsync(command.Condition, cancellationToken) == 0)
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
                    await Expander.EvaluateArithmeticAsync(command.Update, cancellationToken);
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
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var result = ExecResult.Success;
        var index = 0;

        while (index < command.Items.Count)
        {
            if (!await MatchesAnyAsync(command.Items[index], subject, cancellationToken))
            {
                index++;
                continue;
            }

            // A matched branch runs, and then its terminator decides what happens next:
            // `;;` stops, `;&` runs the following body unconditionally, and `;;&` resumes
            // pattern matching at the branch after this one.
            var current = index;

            while (true)
            {
                var item = command.Items[current];

                if (item.Body is not null)
                {
                    result = await ExecuteAsync(item.Body, stdin, cancellationToken);
                    stdin = null;
                    stdout.Append(result.Stdout.ToString());
                    stderr.Append(result.Stderr.ToString());
                    State.LastExitCode = result.ExitCode;

                    // `break`, `continue` and `return` inside a branch abandon the whole
                    // `case`, including any pending fall-through.
                    if (!result.ControlFlow.IsNone)
                    {
                        return Build(stdout, stderr, result.ExitCode, result.ControlFlow);
                    }
                }

                if (item.Terminator == CaseTerminator.FallThrough && current + 1 < command.Items.Count)
                {
                    current++;
                    continue;
                }

                break;
            }

            if (command.Items[current].Terminator == CaseTerminator.ContinueMatching)
            {
                index = current + 1;
                continue;
            }

            return Build(stdout, stderr, result.ExitCode, ControlFlow.None);
        }

        return Build(stdout, stderr, stdout.Length > 0 || stderr.Length > 0 ? result.ExitCode : 0, ControlFlow.None);

        static ExecResult Build(StringBuilder stdout, StringBuilder stderr, int exitCode, ControlFlow flow) => new()
        {
            Stdout = StreamData.FromText(stdout.ToString()),
            Stderr = StreamData.FromText(stderr.ToString()),
            ExitCode = exitCode,
            ControlFlow = flow,
        };
    }

    private async ValueTask<bool> MatchesAnyAsync(CaseItem item, string subject, CancellationToken cancellationToken)
    {
        foreach (var pattern in item.Patterns)
        {
            var text = await Expander.ExpandToPatternAsync(pattern, cancellationToken);
            if (PatternMatcher.IsMatch(subject, text, State.Options.NoCaseMatch, State.Options.ExtGlob))
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<ExecResult> ExecuteSubshellAsync(Subshell subshell, StreamData? stdin, CancellationToken cancellationToken)
    {
        // The subshell runs against a forked state, so its assignments, `cd` and option
        // changes are discarded when it finishes. The filesystem is deliberately shared:
        // a subshell writing a file is visible outside, exactly as with a real fork.
        var fork = State.Fork();
        var nested = new Interpreter(fork, FileSystem, Budget, _commands) { _standardInput = _standardInput };
        var result = await nested.ExecuteAsync(subshell.Body, stdin, cancellationToken);

        // The subshell exits when its body ends, so its own EXIT trap fires here — the
        // parent's does not, because the fork replaced it.
        var atExit = await nested.RunTrapAsync("EXIT", cancellationToken);

        result = result with
        {
            Stdout = StreamData.Concat(result.Stdout, atExit.Stdout),
            Stderr = StreamData.Concat(result.Stderr, atExit.Stderr),
        };

        State.LastExitCode = result.ExitCode;

        // `exit` inside a subshell terminates only the subshell.
        if (result.ControlFlow.Kind == ControlFlowKind.Exit)
        {
            return result with { ControlFlow = ControlFlow.None, ExitCode = result.ControlFlow.Level };
        }

        return result with { ControlFlow = ControlFlow.None };
    }

    /// <summary>
    /// Runs a <c>coproc</c>: the command runs to completion and its output is buffered.
    /// </summary>
    /// <remarks>
    /// A real coprocess runs concurrently with the shell, which nothing here can arrange.
    /// Running it eagerly and buffering the result is observably identical for a script
    /// that writes nothing to it, which is what the shape is almost always used for.
    /// </remarks>
    private async ValueTask<ExecResult> ExecuteCoprocessAsync(CoprocessCommand command, CancellationToken cancellationToken)
    {
        var fork = State.Fork();
        var nested = new Interpreter(fork, FileSystem, Budget, _commands);
        var result = await nested.ExecuteAsync(command.Body, null, cancellationToken);

        // bash numbers a coprocess's descriptors from the top of the table downwards.
        var read = 63;

        while (State.InputDescriptors.ContainsKey(read) && read > 3)
        {
            read--;
        }

        State.InputDescriptors[read] = result.Stdout.ToString();
        State.GetOrCreate(command.Name).SetArray(
        [
            read.ToString(CultureInfo.InvariantCulture),
            (read - 1).ToString(CultureInfo.InvariantCulture),
        ]);

        State.Set(command.Name + "_PID", "1");

        return new ExecResult { Stderr = result.Stderr, ExitCode = 0 };
    }

    private async ValueTask<ExecResult> ExecuteConditionalAsync(ConditionalCommand command, CancellationToken cancellationToken)
    {
        var matched = await ConditionalEvaluator.EvaluateAsync(this, command.Expression, cancellationToken);
        return ExecResult.FromExitCode(matched ? 0 : 1);
    }

    /// <summary>
    /// True when a word is plain unquoted text in the script.
    /// </summary>
    /// <remarks>
    /// Aliases are matched against the source word, never against the result of an
    /// expansion: <c>cmd=ll; $cmd</c> runs a command called <c>ll</c>, it does not expand
    /// the <c>ll</c> alias.
    /// </remarks>
    private static bool IsLiteralWord(Word word) =>
        word.Parts.Count > 0 && word.Parts.All(static part => part is WordPart.Literal { Quoted: false });

    private async ValueTask<ExecResult> ExecuteSimpleAsync(SimpleCommand command, StreamData? stdin, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteSimpleCoreAsync(command, stdin, cancellationToken);
        }
        catch (GlobFailureException e)
        {
            // A `failglob` miss fails only the command that used the pattern.
            return ExecResult.Error($"bash: {e.Message}\n", ExitCodes.Failure);
        }
        catch (ShellException e) when (e.Kind is ShellErrorKind.Internal or ShellErrorKind.PermissionDenied)
        {
            // An expansion error — `${x:?}` or `set -u` on an unset name — ends a
            // non-interactive shell rather than the command. A subshell catches the exit
            // and contains it, which is what bash does too.
            return new ExecResult
            {
                Stderr = StreamData.FromText($"bash: {e.Message}\n"),
                ExitCode = ExitCodes.Failure,
                ControlFlow = ControlFlow.Exit(ExitCodes.Failure),
            };
        }
    }

    private async ValueTask<ExecResult> ExecuteSimpleCoreAsync(SimpleCommand command, StreamData? stdin, CancellationToken cancellationToken)
    {
        if (command.Line > 0)
        {
            State.CurrentLine = command.Line;
        }

        // A bare assignment list with no command name assigns in the current shell.
        if (command.Words.Count == 0)
        {
            var redirectionOnly = await Redirection.PrepareAsync(this, command.Redirects, stdin, cancellationToken);

            if (redirectionOnly.Failure is { } assignmentFailure)
            {
                return assignmentFailure;
            }
            _lastSubstitutionStatus = null;

            foreach (var assignment in command.Assignments)
            {
                await ApplyAssignmentAsync(assignment, cancellationToken);
            }

            // `x=$(false)` reports the substitution's status, but a plain `x=1` succeeds.
            var assigned = _lastSubstitutionStatus is { } status
                ? ExecResult.FromExitCode(status)
                : ExecResult.Success;

            return await redirectionOnly.ApplyAsync(assigned, cancellationToken);
        }

        Budget.ChargeCommand();

        _lastSubstitutionStatus = null;
        var words = await Expander.ExpandAllAsync(command.Words, cancellationToken);

        if (words.Count == 0)
        {
            foreach (var assignment in command.Assignments)
            {
                await ApplyAssignmentAsync(assignment, cancellationToken);
            }

            // A command that expanded to nothing still reports what its substitutions did:
            // `$(exit 42)` on its own leaves `$?` at 42.
            return _lastSubstitutionStatus is { } substituted
                ? ExecResult.FromExitCode(substituted)
                : ExecResult.Success;
        }

        var name = words[0];
        var arguments = words[1..];

        // The DEBUG trap runs before each command, and is itself a command, so it must not
        // re-enter — `RunTrapAsync` guards that on the shared state.
        var debug = await RunTrapAsync("DEBUG", cancellationToken);

        // An alias substitutes textually for the command word before dispatch. It is
        // resolved here rather than in the parser because aliases can be defined by the
        // very script being run, so the parser has not seen them yet.
        if (State.Options.ExpandAliases
            && IsLiteralWord(command.Words[0])
            && !State.AliasesInProgress.Contains(name)
            && State.Aliases.TryGetValue(name, out var alias))
        {
            var redirected = await Redirection.PrepareAsync(this, command.Redirects, stdin, cancellationToken);

            if (redirected.Failure is { } aliasFailure)
            {
                return aliasFailure;
            }

            State.AliasesInProgress.Add(name);
            try
            {
                var parts = new List<string> { alias.TrimEnd() };
                var rest = (IReadOnlyList<string>)arguments;

                // An alias whose value ends in a blank asks for the next word to be alias
                // expanded too, which is how `alias sudo='sudo '` makes `sudo ll` work.
                if (alias.Length > 0 && char.IsWhiteSpace(alias[^1])
                    && rest.Count > 0
                    && !State.AliasesInProgress.Contains(rest[0])
                    && State.Aliases.TryGetValue(rest[0], out var chained))
                {
                    parts.Add(chained.TrimEnd());
                    rest = [.. rest.Skip(1)];
                }

                parts.AddRange(rest.Select(Expander_Quote));

                var aliasResult = await RunFragmentAsync(string.Join(' ', parts), redirected.Stdin, cancellationToken);
                return await redirected.ApplyAsync(aliasResult, cancellationToken);
            }
            finally
            {
                State.AliasesInProgress.Remove(name);
            }
        }

        var redirection = await Redirection.PrepareAsync(this, command.Redirects, stdin, cancellationToken);

        if (redirection.Failure is { } failure)
        {
            return failure;
        }

        if (State.Options.XTrace)
        {
            _stderr.Append("+ ").Append(string.Join(' ', words)).Append('\n');
        }

        ExecResult result;
        try
        {
            result = await DispatchAsync(name, arguments, command.Assignments, redirection.Stdin, cancellationToken);
        }
        catch (ShellException e) when (e.Kind is ShellErrorKind.Internal or ShellErrorKind.PermissionDenied)
        {
            result = ExecResult.Error($"bash: {e.Message}\n", ExitCodes.Failure);
        }

        // `$_` is the last argument of the command that just ran.
        State.Set("_", words[^1]);

        if (!debug.Stdout.IsEmpty || !debug.Stderr.IsEmpty)
        {
            result = result with
            {
                Stdout = StreamData.Concat(debug.Stdout, result.Stdout),
                Stderr = StreamData.Concat(debug.Stderr, result.Stderr),
            };
        }

        return await DrainWritersAsync(await redirection.ApplyAsync(result, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Runs the <c>&gt;(...)</c> commands whose input file the finished command wrote.
    /// </summary>
    private async ValueTask<ExecResult> DrainWritersAsync(ExecResult result, CancellationToken cancellationToken)
    {
        var pending = Expander.TakePendingWriters();

        if (pending.Count == 0)
        {
            return result;
        }

        var stdout = result.Stdout;
        var stderr = result.Stderr;

        foreach (var (path, script) in pending)
        {
            var content = await FileSystem.ReadFileAsync(path, cancellationToken);
            var run = await RunFragmentAsync(script, StreamData.FromBytes(content), cancellationToken);
            stdout = StreamData.Concat(stdout, run.Stdout);
            stderr = StreamData.Concat(stderr, run.Stderr);
        }

        return result with { Stdout = stdout, Stderr = stderr };
    }

    private async ValueTask<ExecResult> DispatchAsync(
        string name,
        List<string> arguments,
        IReadOnlyList<Assignment> assignments,
        StreamData? stdin,
        CancellationToken cancellationToken)
    {
        // A command with no input of its own reads the shell's, which a redirection on an
        // enclosing loop or an `exec < file` may have replaced.
        var inheritsShellInput = stdin is null;
        stdin ??= _standardInput?.Remaining;

        if (State.Functions.TryGetValue(name, out var function))
        {
            return await CallFunctionAsync(function, arguments, assignments, stdin, cancellationToken);
        }

        if (!_commands.TryGet(name, out var builtin))
        {
            return await RunScriptFileAsync(name, arguments, assignments, stdin, inheritsShellInput, cancellationToken);
        }

        // Assignments preceding a command apply only for that command's duration.
        var saved = await ApplyTemporaryAssignmentsAsync(assignments, cancellationToken);
        try
        {
            // Reads share the shell's stream when the input came from it, and get a
            // private one otherwise — a pipe feeds exactly one command.
            var input = inheritsShellInput
                ? _standardInput
                : stdin is { } data ? new InputStream(data.ToString()) : null;

            var context = new BuiltinContext(name, arguments, State, FileSystem, Budget, stdin, Hooks) { Input = input };
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
        UpdateFunctionName();
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
            UpdateFunctionName();
            State.Positional = savedPositional;
            RestoreTemporaryAssignments(savedAssignments);
        }
    }

    // Rebuilt on entry to and exit from every function call, so it is one buffer rather
    // than a list per call. An interpreter belongs to one execution, and `SetArray` copies
    // what it is given, so nothing outlives the call that filled it.
    private readonly List<string> _functionNames = [];

    /// <summary>Republishes <c>FUNCNAME</c> from the call stack, innermost first.</summary>
    private void UpdateFunctionName()
    {
        _functionNames.Clear();

        for (var i = State.CallStack.Count - 1; i >= 0; i--)
        {
            _functionNames.Add(State.CallStack[i]);
        }

        State.GetOrCreate("FUNCNAME").SetArray(_functionNames);
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
            throw new ShellException(ShellErrorKind.PermissionDenied, $"{assignment.Name}: readonly variable");
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
                        var elementKey = Expander.ExpandSubscriptText(key);

                        if (variable.IsAssociative)
                        {
                            variable.SetAssociative(StripQuotes(elementKey), elementValue);
                        }
                        else
                        {
                            variable.SetIndexed(ArithmeticEvaluator.Evaluate(State, elementKey), elementValue);
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

                if (assignment.Index is { } rawIndex)
                {
                    // An assignment's subscript gets the full expansion, including command
                    // substitution: `m["$(f)"]=v` keys on what `f` printed.
                    var index = await Expander.ExpandToStringAsync(
                        Parsing.WordParser.Parse(rawIndex), cancellationToken);

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

                // `x+=z` on an array appends to element 0, not as a new element: bash
                // reads a scalar assignment as one about `${x[0]}` whatever the variable
                // holds, so `x=(a b c); x+=z` leaves `az b c`. Appending an element is
                // what `x+=(z)` is for, and that is the array case above.
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
    /// <summary>
    /// Parses a substitution's body, or hands back the parse the node already has.
    /// </summary>
    /// <remarks>
    /// The body is the same text on every execution of the node, so a function that
    /// substitutes parses once rather than once per call. What that first parse cost is
    /// charged again on every reuse: the work is skipped, the accounting is not, so
    /// <c>max_parser_fuel</c> bounds a loop exactly as it did before.
    /// </remarks>
    private Script ParseSubstitution(WordPart part)
    {
        switch (part)
        {
            case WordPart.CommandSubstitution command:
                if (command.Parsed is { } parsedCommand)
                {
                    Budget.ChargeParserFuel(command.ParsedFuel);
                    return parsedCommand;
                }

                (command.Parsed, command.ParsedFuel) = ParseAndMeasure(command.Script);
                return command.Parsed;

            case WordPart.ProcessSubstitution process:
                if (process.Parsed is { } parsedProcess)
                {
                    Budget.ChargeParserFuel(process.ParsedFuel);
                    return parsedProcess;
                }

                (process.Parsed, process.ParsedFuel) = ParseAndMeasure(process.Script);
                return process.Parsed;

            default:
                throw new ArgumentException($"{part.GetType().Name} carries no script", nameof(part));
        }
    }

    private (Script Parsed, long Fuel) ParseAndMeasure(string script)
    {
        var before = Budget.ParserFuel;
        var parsed = Parser.Parse(script, Budget);
        return (parsed, Budget.ParserFuel - before);
    }

    private async ValueTask<ExecResult> RunSubstitutionAsync(WordPart part, CancellationToken cancellationToken)
    {
        Budget.ThrowIfExpired();
        using var nesting = Budget.EnterNesting();

        var parsed = ParseSubstitution(part);

        // A substitution is a subshell: assignments, function definitions and traps made
        // inside it are discarded, which is what keeps `x=$(myvar=inside; ...)` from
        // rewriting the caller's variable.
        var fork = State.Fork();

        // It does inherit the shell's standard input, so `x=$(cat)` in a script fed from a
        // pipe reads that pipe — the file descriptor a real shell would have handed down.
        var nested = new Interpreter(fork, FileSystem, Budget, _commands) { _standardInput = _standardInput };
        var result = await nested.RunAsync(parsed, _standardInput?.Remaining, cancellationToken);

        // The subshell exits when the substitution ends, so its EXIT trap fires here and
        // its output belongs to the substituted value.
        var atExit = await nested.RunTrapAsync("EXIT", cancellationToken);

        _lastSubstitutionStatus = result.ControlFlow.Kind == ControlFlowKind.Exit
            ? result.ControlFlow.Level
            : result.ExitCode;

        AppendStderr(result.Stderr);
        AppendStderr(atExit.Stderr);

        return result with
        {
            Stdout = StreamData.Concat(result.Stdout, atExit.Stdout),
            Stderr = StreamData.Empty,
            ControlFlow = ControlFlow.None,
        };
    }

    /// <summary>Runs a script fragment in the current shell, as <c>eval</c> and <c>source</c> do.</summary>
    /// <summary>
    /// Runs a name that is not a builtin or a function as a script from the filesystem,
    /// falling back to a host <see cref="ICommandResolver"/> when there is no such script.
    /// </summary>
    /// <remarks>
    /// This is the closest the sandbox comes to <c>exec</c>, and it deliberately stops well
    /// short of it: the file must live in the virtual filesystem and must be a shell script,
    /// there is no interpreter dispatch on the shebang line, and it runs in-process as an
    /// isolated child shell. Nothing here can reach a host binary.
    /// </remarks>
    private async ValueTask<ExecResult> RunScriptFileAsync(
        string name,
        List<string> arguments,
        IReadOnlyList<Assignment> assignments,
        StreamData? stdin,
        bool inheritsShellInput,
        CancellationToken cancellationToken)
    {
        var saved = await ApplyTemporaryAssignmentsAsync(assignments, cancellationToken);

        try
        {
            var path = await ResolveExecutableAsync(name, cancellationToken);

            if (path is null)
            {
                // Nothing else answered for the name, so a host resolver gets the last
                // word. It is asked here rather than alongside the registered commands
                // precisely so that it cannot shadow one, or a script on `PATH`.
                if (_commands.ResolveUnknown(name) is { } resolved)
                {
                    Budget.ChargeCommand();

                    var input = inheritsShellInput
                        ? _standardInput
                        : stdin is { } data ? new InputStream(data.ToString()) : null;

                    var resolvedContext =
                        new BuiltinContext(name, arguments, State, FileSystem, Budget, stdin, Hooks) { Input = input };

                    return await resolved.ExecuteAsync(resolvedContext, cancellationToken);
                }

                return ExecResult.Error($"bash: {name}: command not found\n", ExitCodes.NotFound);
            }

            FileMetadata metadata;

            try
            {
                metadata = await FileSystem.StatAsync(path.Value, cancellationToken);
            }
            catch (ShellException)
            {
                return ExecResult.Error($"bash: {name}: command not found\n", ExitCodes.NotFound);
            }

            if (metadata.IsDirectory)
            {
                return ExecResult.Error($"bash: {name}: Is a directory\n", ExitCodes.NotExecutable);
            }

            if ((metadata.Mode & 0b001_001_001) == 0)
            {
                return ExecResult.Error($"bash: {name}: Permission denied\n", ExitCodes.NotExecutable);
            }

            var source = System.Text.Encoding.UTF8.GetString(await FileSystem.ReadFileAsync(path.Value, cancellationToken));

            return await RunIsolatedAsync(
                new Builtins.ChildShell(source)
                {
                    ScriptName = name.Contains('/', StringComparison.Ordinal) ? name : path.Value.Value,
                    Positional = arguments,
                    Stdin = stdin,
                },
                cancellationToken);
        }
        finally
        {
            RestoreTemporaryAssignments(saved);
        }
    }

    /// <summary>Finds an executable by path or by searching <c>PATH</c>.</summary>
    private async ValueTask<VPath?> ResolveExecutableAsync(string name, CancellationToken cancellationToken)
    {
        if (name.Contains('/', StringComparison.Ordinal))
        {
            var direct = VPath.Resolve(State.WorkingDirectory, name);
            return await FileSystem.ExistsAsync(direct, cancellationToken) ? direct : (VPath?)null;
        }

        foreach (var directory in (State.Get("PATH") ?? string.Empty)
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = VPath.Parse(directory).Join(name);

            if (await FileSystem.ExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs a script in a fresh shell that shares only the filesystem.
    /// </summary>
    /// <remarks>
    /// This is what <c>bash -c</c> and running a script file need, and it is not the same as
    /// a subshell: a subshell forks the whole state, whereas a child shell starts from the
    /// exported environment alone. Non-exported variables, functions and aliases do not
    /// cross, and nothing the child does comes back.
    /// </remarks>
    public async ValueTask<ExecResult> RunIsolatedAsync(
        Builtins.ChildShell request,
        CancellationToken cancellationToken)
    {
        using var nesting = Budget.EnterNesting();

        var child = new ShellState
        {
            WorkingDirectory = State.WorkingDirectory,
            ScriptName = request.ScriptName,
            Positional = [.. request.Positional],
        };

        foreach (var (name, value) in State.ExportedEnvironment())
        {
            child.Set(name, value);
            child.GetOrCreate(name).Attributes |= VariableAttributes.Exported;
        }

        // `${BASH_SOURCE[0]}` is how a script finds its own path, and it is set for a
        // script run as a command just as `source` sets it.
        child.GetOrCreate("BASH_SOURCE").SetArray([request.ScriptName]);

        request.Configure?.Invoke(child.Options);

        Script parsed;

        try
        {
            parsed = Parser.Parse(request.Script, Budget);
        }
        catch (ShellException exception) when (exception.Kind == ShellErrorKind.Parse)
        {
            return ExecResult.Error($"{request.ScriptName}: {exception.Message}\n", ExitCodes.Usage);
        }

        // `-n` asks for a syntax check only, which the parse above has just performed.
        if (child.Options.NoExec)
        {
            return ExecResult.Success;
        }

        var nested = new Interpreter(child, FileSystem, Budget, _commands);
        var result = await nested.RunAsync(parsed, request.Stdin, cancellationToken);
        return result with { ControlFlow = ControlFlow.None };
    }

    /// <summary>Parses and runs a script fragment in this shell, sharing all of its state.</summary>
    public async ValueTask<ExecResult> RunFragmentAsync(string script, StreamData? stdin, CancellationToken cancellationToken)
    {
        using var nesting = Budget.EnterNesting();
        var parsed = Parser.Parse(script, Budget);
        var nested = new Interpreter(State, FileSystem, Budget, _commands) { _standardInput = _standardInput };
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
        if (words.Count == 0 || !_commands.TryGet(words[0], out var builtin))
        {
            return ExecResult.Error($"bash: {(words.Count > 0 ? words[0] : string.Empty)}: command not found\n", ExitCodes.NotFound);
        }

        Budget.ChargeCommand();
        var context = new BuiltinContext(words[0], [.. words.Skip(1)], State, FileSystem, Budget, stdin, Hooks);
        return await builtin.ExecuteAsync(context, cancellationToken);
    }

    /// <summary>True when <paramref name="name"/> resolves to a registered builtin.</summary>
    public bool HasBuiltin(string name) => _commands.Contains(name);

    /// <summary>The names of every registered builtin.</summary>
    public IEnumerable<string> BuiltinNames => _commands.Names;
}
