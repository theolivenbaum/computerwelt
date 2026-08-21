namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// A small POSIX/GNU-style option scanner.
/// </summary>
/// <remarks>
/// <para>
/// The Rust original uses <c>clap</c>; there is no drop-in equivalent here, and pulling in
/// a general CLI framework would be the wrong trade — coreutils option syntax is quirky in
/// ways frameworks resist (<c>head -5</c>, <c>sort -k2,3</c>, <c>tail -n +3</c>), and the
/// port must match those quirks exactly.
/// </para>
/// <para>
/// The scanner therefore handles just what the builtins need: bundled short flags
/// (<c>-la</c>), attached and detached option values, long options with <c>=</c>, and
/// <c>--</c> to stop scanning.
/// </para>
/// </remarks>
public sealed class ArgCursor
{
    private readonly IReadOnlyList<string> _arguments;
    private int _index;
    private int _bundleOffset;
    private bool _stopped;

    /// <summary>Creates a cursor over <paramref name="arguments"/>.</summary>
    public ArgCursor(IReadOnlyList<string> arguments)
    {
        _arguments = arguments;
        Operands = [];
    }

    /// <summary>Non-option arguments collected so far.</summary>
    public List<string> Operands { get; }

    /// <summary>
    /// Stops option parsing at the first operand.
    /// </summary>
    /// <remarks>
    /// A command that takes another command as its operands — <c>xargs</c>, <c>timeout</c>,
    /// <c>env</c> — must not read the inner command's flags as its own: in
    /// <c>xargs -I{} sh -c '...'</c> the <c>-c</c> belongs to <c>sh</c>.
    /// </remarks>
    public bool StopAtFirstOperand { get; init; }

    /// <summary>True when every argument has been consumed.</summary>
    public bool AtEnd => _index >= _arguments.Count;

    /// <summary>
    /// Advances to the next option. Returns <see langword="null"/> when the arguments are
    /// exhausted; non-option arguments are appended to <see cref="Operands"/> along the way.
    /// </summary>
    public string? NextOption()
    {
        while (_index < _arguments.Count)
        {
            var argument = _arguments[_index];

            if (_bundleOffset > 0)
            {
                if (_bundleOffset < argument.Length)
                {
                    var flag = argument[_bundleOffset++];
                    return "-" + flag;
                }

                _bundleOffset = 0;
                _index++;
                continue;
            }

            if (_stopped || argument.Length == 0 || argument[0] != '-' || argument == "-")
            {
                Operands.Add(argument);
                _index++;

                if (StopAtFirstOperand)
                {
                    _stopped = true;
                }

                continue;
            }

            if (argument == "--")
            {
                _stopped = true;
                _index++;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                _index++;
                var equals = argument.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0)
                {
                    return argument;
                }

                PendingValue = argument[(equals + 1)..];
                return argument[..equals];
            }

            // `head -30` is one option, not the cluster `-3 -0`: no builtin has a digit
            // for a short option, and the GNU tools that take a count spell it this way.
            if (char.IsAsciiDigit(argument[1]) && argument.AsSpan(1).ContainsAnyExceptInRange('0', '9') == false)
            {
                _index++;
                return argument;
            }

            _bundleOffset = 1;
        }

        return null;
    }

    /// <summary>A value that was attached to the option with <c>=</c>, if any.</summary>
    public string? PendingValue { get; private set; }

    /// <summary>
    /// Reads the current option's value: the rest of a bundled short option
    /// (<c>-n5</c>), an attached long value (<c>--lines=5</c>), or the next argument.
    /// Returns <see langword="null"/> when no value is available.
    /// </summary>
    public string? TakeValue()
    {
        if (PendingValue is { } pending)
        {
            PendingValue = null;
            return pending;
        }

        if (_bundleOffset > 0 && _index < _arguments.Count && _bundleOffset < _arguments[_index].Length)
        {
            var value = _arguments[_index][_bundleOffset..];
            _bundleOffset = 0;
            _index++;
            return value;
        }

        if (_bundleOffset > 0)
        {
            _bundleOffset = 0;
            _index++;
        }

        if (_index < _arguments.Count)
        {
            return _arguments[_index++];
        }

        return null;
    }

    /// <summary>Collects every remaining argument as an operand and returns the list.</summary>
    public List<string> DrainOperands()
    {
        while (NextOption() is { } option)
        {
            // An unrecognized option in a drain is treated as an operand, which is what
            // `echo -q` does.
            Operands.Add(option);
        }

        return Operands;
    }
}
