namespace Computerwelt.Emulation.Bash;

/// <summary>
/// The live accounting of one execution against its <see cref="ExecutionLimits"/>.
/// </summary>
/// <remarks>
/// One budget instance is threaded through the parser, interpreter and every builtin, so
/// that work anywhere in the tree draws from the same pool. Charging throws
/// <see cref="LimitExceededException"/> at the moment of exhaustion rather than returning
/// a status, because a script that has blown its budget has no valid continuation.
/// </remarks>
public sealed class ExecutionBudget
{
    private readonly long _deadlineTicks;
    private int _commands;
    private int _totalLoopIterations;
    private long _workUnits;
    private long _parserFuel;

    /// <summary>Creates a budget for <paramref name="limits"/>.</summary>
    public ExecutionBudget(ExecutionLimits limits, CancellationToken cancellationToken = default)
    {
        Limits = limits;
        CancellationToken = cancellationToken;
        StartedAt = TimeProvider.System.GetTimestamp();
        _deadlineTicks = StartedAt + (long)(limits.Timeout.TotalSeconds * TimeProvider.System.TimestampFrequency);
    }

    /// <summary>The limits this budget enforces.</summary>
    public ExecutionLimits Limits { get; }

    /// <summary>Host cancellation for the execution.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Timestamp captured when the execution began.</summary>
    public long StartedAt { get; }

    /// <summary>Elapsed wall-clock time since the execution began.</summary>
    public TimeSpan Elapsed => TimeProvider.System.GetElapsedTime(StartedAt);

    /// <summary>Commands executed so far.</summary>
    public int CommandCount => _commands;

    /// <summary>Current shell function call depth.</summary>
    public int FunctionDepth { get; private set; }

    /// <summary>Current compound-command / expansion nesting depth.</summary>
    public int NestingDepth { get; private set; }

    /// <summary>Charges one command against the budget.</summary>
    public void ChargeCommand()
    {
        ThrowIfExpired();

        if (++_commands > Limits.MaxCommands)
        {
            throw new LimitExceededException("max_commands", Limits.MaxCommands);
        }
    }

    /// <summary>
    /// Charges one iteration of a loop. <paramref name="loopIteration"/> is the iteration
    /// index within the current loop, checked against the per-loop cap; the global cap is
    /// tracked internally.
    /// </summary>
    public void ChargeLoopIteration(int loopIteration)
    {
        ThrowIfExpired();

        if (loopIteration > Limits.MaxLoopIterations)
        {
            throw new LimitExceededException("max_loop_iterations", Limits.MaxLoopIterations);
        }

        if (++_totalLoopIterations > Limits.MaxTotalLoopIterations)
        {
            throw new LimitExceededException("max_total_loop_iterations", Limits.MaxTotalLoopIterations);
        }
    }

    /// <summary>Charges abstract work units, for operations whose cost is not one command.</summary>
    public void ChargeWork(long units)
    {
        _workUnits += units;
        if (_workUnits > Limits.MaxWorkUnits)
        {
            throw new LimitExceededException("max_work_units", Limits.MaxWorkUnits);
        }
    }

    /// <summary>Charges parser fuel. Called per token and per production.</summary>
    public void ChargeParserFuel(long units = 1)
    {
        _parserFuel += units;
        if (_parserFuel > Limits.MaxParserFuel)
        {
            throw new LimitExceededException("max_parser_fuel", Limits.MaxParserFuel);
        }
    }

    /// <summary>Enters a shell function frame; the returned scope restores the depth.</summary>
    public DepthScope EnterFunction()
    {
        if (++FunctionDepth > Limits.MaxFunctionDepth)
        {
            FunctionDepth--;
            throw new LimitExceededException("max_function_depth", Limits.MaxFunctionDepth);
        }

        return new DepthScope(this, isFunction: true);
    }

    /// <summary>Enters a nested construct; the returned scope restores the depth.</summary>
    public DepthScope EnterNesting()
    {
        if (++NestingDepth > Limits.MaxNestingDepth)
        {
            NestingDepth--;
            throw new LimitExceededException("max_nesting_depth", Limits.MaxNestingDepth);
        }

        return new DepthScope(this, isFunction: false);
    }

    /// <summary>Throws if the execution has timed out or been cancelled.</summary>
    public void ThrowIfExpired()
    {
        if (CancellationToken.IsCancellationRequested)
        {
            throw new BashkitException(BashkitErrorKind.Cancelled, "execution cancelled");
        }

        if (TimeProvider.System.GetTimestamp() > _deadlineTicks)
        {
            throw new BashkitException(
                BashkitErrorKind.Timeout,
                $"execution timed out after {Limits.Timeout.TotalSeconds:0.###}s");
        }
    }

    /// <summary>Restores a depth counter when disposed.</summary>
    public readonly struct DepthScope : IDisposable
    {
        private readonly ExecutionBudget? _budget;
        private readonly bool _isFunction;

        internal DepthScope(ExecutionBudget budget, bool isFunction)
        {
            _budget = budget;
            _isFunction = isFunction;
        }

        /// <summary>Leaves the frame.</summary>
        public void Dispose()
        {
            if (_budget is null)
            {
                return;
            }

            if (_isFunction)
            {
                _budget.FunctionDepth--;
            }
            else
            {
                _budget.NestingDepth--;
            }
        }
    }
}
