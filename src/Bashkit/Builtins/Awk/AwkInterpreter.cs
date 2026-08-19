using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bashkit.Builtins.Awk;

/// <summary>How a statement finished, and therefore what the enclosing construct must do.</summary>
internal enum AwkFlow
{
    /// <summary>Fell through normally.</summary>
    Normal,

    /// <summary>Leave the innermost loop.</summary>
    Break,

    /// <summary>Start the innermost loop's next iteration.</summary>
    Continue,

    /// <summary>Abandon this record and read the next.</summary>
    Next,

    /// <summary>Abandon the rest of this input file.</summary>
    NextFile,

    /// <summary>Leave the current function.</summary>
    Return,

    /// <summary>Stop the program, running <c>END</c> first.</summary>
    Exit,
}

/// <summary>A variable scope: function parameters shadow globals, and only those.</summary>
internal sealed class AwkFrame
{
    /// <summary>Scalar parameters, held by value.</summary>
    public Dictionary<string, AwkValue> Scalars { get; } = new(StringComparer.Ordinal);

    /// <summary>Array parameters, held by reference as AWK requires.</summary>
    public Dictionary<string, Dictionary<string, AwkValue>> Arrays { get; } = new(StringComparer.Ordinal);

    /// <summary>Every name this frame owns, including ones still unassigned.</summary>
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Walks a parsed AWK program.
/// </summary>
/// <remarks>
/// <para>
/// A tree walk rather than a bytecode VM: AWK programs are small, run once per record, and
/// the interesting cost is in field splitting and regex matching, not in dispatch. The
/// simpler shape also keeps AWK's many context-sensitive rules — strnum comparison, lvalue
/// arguments to <c>sub</c>, arrays passed by reference — readable.
/// </para>
/// <para>
/// Every record and every loop iteration charges the execution budget, so a runaway program
/// is stopped by the same limits that bound the rest of the shell.
/// </para>
/// </remarks>
internal sealed class AwkInterpreter
{
    private readonly AwkProgram _program;
    private readonly ExecutionBudget _budget;
    private readonly Func<string, string?> _readFile;

    private readonly Dictionary<string, AwkValue> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, AwkValue>> _globalArrays = new(StringComparer.Ordinal);
    private readonly Stack<AwkFrame> _frames = new();

    private readonly List<string> _fields = [];
    private string _record = string.Empty;

    private readonly Dictionary<string, AwkOutput> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AwkInput> _inputs = new(StringComparer.Ordinal);

    private List<AwkSource> _sources = [];
    private int _sourceIndex;
    private int _lineIndex;

    private Random _random = new(0);
    private double _lastSeed;
    private int _loopIterations;

    /// <summary>Creates an interpreter for <paramref name="program"/>.</summary>
    /// <param name="program">The parsed program.</param>
    /// <param name="budget">The budget charged for records and loop iterations.</param>
    /// <param name="readFile">Reads a virtual file, returning null when it cannot be read.</param>
    public AwkInterpreter(AwkProgram program, ExecutionBudget budget, Func<string, string?> readFile)
    {
        _program = program;
        _budget = budget;
        _readFile = readFile;

        _globals["FS"] = AwkValue.Text(" ");
        _globals["OFS"] = AwkValue.Text(" ");
        _globals["ORS"] = AwkValue.Text("\n");
        _globals["RS"] = AwkValue.Text("\n");
        _globals["SUBSEP"] = AwkValue.Text("\u001c");
        _globals["CONVFMT"] = AwkValue.Text("%.6g");
        _globals["OFMT"] = AwkValue.Text("%.6g");
        _globals["FILENAME"] = AwkValue.Text(string.Empty);
        _globals["NR"] = AwkValue.Number(0);
        _globals["FNR"] = AwkValue.Number(0);
        _globals["RSTART"] = AwkValue.Number(0);
        _globals["RLENGTH"] = AwkValue.Number(-1);
    }

    /// <summary>Everything written to standard output.</summary>
    public StringBuilder Stdout { get; } = new();

    /// <summary>Everything written to standard error.</summary>
    public StringBuilder Stderr { get; } = new();

    /// <summary>The status <c>exit</c> asked for, when it was given one.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>The files the program redirected output to, ready to be committed.</summary>
    public IReadOnlyDictionary<string, AwkOutput> Outputs => _outputs;

    /// <summary>A file the program wrote through <c>&gt;</c> or <c>&gt;&gt;</c>.</summary>
    /// <param name="Append">True when the first redirection to it was <c>&gt;&gt;</c>.</param>
    public sealed record AwkOutput(bool Append)
    {
        /// <summary>The accumulated text.</summary>
        public StringBuilder Content { get; } = new();
    }

    /// <summary>One input file, kept separate so <c>FNR</c> and <c>FILENAME</c> can track it.</summary>
    /// <param name="Name">The name to report in <c>FILENAME</c>.</param>
    /// <param name="Lines">Its records.</param>
    public sealed record AwkSource(string Name, string[] Lines);

    private sealed record AwkInput(string[] Lines)
    {
        public int Index { get; set; }
    }

    private AwkValue ReturnValue { get; set; }

    /// <summary>Presets a variable, as <c>-v</c> does.</summary>
    public void Preset(string name, string value) => _globals[name] = AwkValue.Input(Unescape(value));

    /// <summary>Sets the field separator, as <c>-F</c> does.</summary>
    public void SetFieldSeparator(string separator) => _globals["FS"] = AwkValue.Text(Unescape(separator));

    /// <summary>Runs the whole program over <paramref name="sources"/>.</summary>
    public void Run(IReadOnlyList<AwkSource> sources)
    {
        _sources = [.. sources];

        var exited = RunRules(AwkPatternKind.Begin);

        // A program with only a BEGIN block never touches its input, which is what lets
        // `awk 'BEGIN{...}'` run with nothing on standard input.
        if (!exited && _program.Rules.Any(rule => rule.Kind is not AwkPatternKind.Begin))
        {
            RunMain();
        }

        RunRules(AwkPatternKind.End);
    }

    private bool RunRules(AwkPatternKind kind)
    {
        foreach (var rule in _program.Rules)
        {
            if (rule.Kind != kind || rule.Action is null)
            {
                continue;
            }

            if (Execute(rule.Action) == AwkFlow.Exit)
            {
                return true;
            }
        }

        return false;
    }

    private void RunMain()
    {
        while (AdvanceRecord(out var line, out var atFileStart))
        {
            if (atFileStart)
            {
                _globals["FILENAME"] = AwkValue.Text(_sources[_sourceIndex].Name);
            }

            _budget.ChargeWork(1);
            SetRecord(line);
            Bump("NR");
            Bump("FNR");

            var flow = RunRecordRules();

            if (flow == AwkFlow.Exit)
            {
                return;
            }

            if (flow == AwkFlow.NextFile)
            {
                // Jumping to the end of the file leaves the crossing — and the FNR reset —
                // to the single place that does it.
                _lineIndex = _sources[_sourceIndex].Lines.Length;
            }
        }
    }

    private void Bump(string name) => _globals[name] = AwkValue.Number(_globals[name].ToNumber() + 1);

    private AwkFlow RunRecordRules()
    {
        foreach (var rule in _program.Rules)
        {
            if (rule.Kind is AwkPatternKind.Begin or AwkPatternKind.End || !Matches(rule))
            {
                continue;
            }

            // A pattern with no action prints the record.
            if (rule.Action is null)
            {
                Write(GetField(0) + Text("ORS"), null);
                continue;
            }

            var flow = Execute(rule.Action);

            if (flow is AwkFlow.Next)
            {
                return AwkFlow.Normal;
            }

            if (flow is AwkFlow.Exit or AwkFlow.NextFile)
            {
                return flow;
            }
        }

        return AwkFlow.Normal;
    }

    private bool Matches(AwkRule rule)
    {
        switch (rule.Kind)
        {
            case AwkPatternKind.Always:
                return true;

            case AwkPatternKind.Expression:
                return Evaluate(rule.Pattern!).ToBoolean();

            case AwkPatternKind.Range:
            {
                if (!rule.RangeActive)
                {
                    if (!Evaluate(rule.Pattern!).ToBoolean())
                    {
                        return false;
                    }

                    // A range that ends on its first record closes at once, so the end
                    // pattern is tested against the same line.
                    rule.RangeActive = !Evaluate(rule.RangeEnd!).ToBoolean();
                    return true;
                }

                if (Evaluate(rule.RangeEnd!).ToBoolean())
                {
                    rule.RangeActive = false;
                }

                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Reads the next main-input record, crossing into the next file when needed.</summary>
    private bool AdvanceRecord(out string line, out bool atFileStart)
    {
        atFileStart = false;

        while (_sourceIndex < _sources.Count)
        {
            var source = _sources[_sourceIndex];

            if (_lineIndex < source.Lines.Length)
            {
                // `_sourceIndex` keeps pointing at the file the record came from, so the
                // caller can read FILENAME from it.
                atFileStart = _lineIndex == 0;
                line = source.Lines[_lineIndex++];
                return true;
            }

            _sourceIndex++;
            _lineIndex = 0;
            _globals["FNR"] = AwkValue.Number(0);
        }

        line = string.Empty;
        return false;
    }

    // ---- fields ----

    private string Text(string name) => GetVariable(name).ToText(ConvFmt);

    private string ConvFmt => _globals.TryGetValue("CONVFMT", out var value) ? value.ToText() : "%.6g";

    private string OutFmt => _globals.TryGetValue("OFMT", out var value) ? value.ToText() : "%.6g";

    private void SetRecord(string line)
    {
        _record = line;
        _fields.Clear();
        _fields.AddRange(SplitRecord(line, Text("FS")));
    }

    /// <summary>
    /// Splits a record into fields.
    /// </summary>
    /// <remarks>
    /// The default separator is not a literal space: it means "runs of blanks, with leading
    /// and trailing blanks discarded", which is why <c>"  a  b  "</c> has two fields rather
    /// than six. A single other character is literal even when it is a regex metacharacter,
    /// and anything longer is an ERE.
    /// </remarks>
    private static List<string> SplitRecord(string line, string separator)
    {
        if (separator == " ")
        {
            return [.. line.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)];
        }

        if (line.Length == 0)
        {
            return [];
        }

        if (separator.Length == 0)
        {
            return [.. line.Select(c => c.ToString())];
        }

        if (separator.Length == 1)
        {
            return [.. line.Split(separator[0])];
        }

        try
        {
            return [.. AwkRegexes.Get(separator).Split(line)];
        }
        catch (RegexMatchTimeoutException)
        {
            return [line];
        }
    }

    private string GetField(int index)
    {
        if (index == 0)
        {
            return _record;
        }

        return index >= 1 && index <= _fields.Count ? _fields[index - 1] : string.Empty;
    }

    private void SetField(int index, string value)
    {
        if (index == 0)
        {
            SetRecord(value);
            return;
        }

        if (index < 0)
        {
            throw new AwkRuntimeException($"awk: attempt to access field {index}");
        }

        while (_fields.Count < index)
        {
            _fields.Add(string.Empty);
        }

        _fields[index - 1] = value;
        _record = string.Join(Text("OFS"), _fields);
    }

    private void SetFieldCount(int count)
    {
        count = Math.Max(count, 0);

        while (_fields.Count > count)
        {
            _fields.RemoveAt(_fields.Count - 1);
        }

        while (_fields.Count < count)
        {
            _fields.Add(string.Empty);
        }

        _record = string.Join(Text("OFS"), _fields);
    }

    // ---- variables ----

    private AwkFrame? CurrentFrame => _frames.Count > 0 ? _frames.Peek() : null;

    private AwkValue GetVariable(string name)
    {
        if (name == "NF")
        {
            return AwkValue.Number(_fields.Count);
        }

        if (CurrentFrame is { } frame && frame.Names.Contains(name))
        {
            return frame.Scalars.TryGetValue(name, out var local) ? local : AwkValue.Uninitialized;
        }

        return _globals.TryGetValue(name, out var value) ? value : AwkValue.Uninitialized;
    }

    private void SetVariable(string name, AwkValue value)
    {
        if (name == "NF")
        {
            SetFieldCount((int)value.ToNumber());
            return;
        }

        if (CurrentFrame is { } frame && frame.Names.Contains(name))
        {
            frame.Scalars[name] = value;
            return;
        }

        _globals[name] = value;
    }

    private Dictionary<string, AwkValue> GetArray(string name)
    {
        if (CurrentFrame is { } frame && frame.Names.Contains(name))
        {
            if (!frame.Arrays.TryGetValue(name, out var local))
            {
                local = new Dictionary<string, AwkValue>(StringComparer.Ordinal);
                frame.Arrays[name] = local;
            }

            return local;
        }

        if (!_globalArrays.TryGetValue(name, out var array))
        {
            array = new Dictionary<string, AwkValue>(StringComparer.Ordinal);
            _globalArrays[name] = array;
        }

        return array;
    }

    private bool IsArray(string name) =>
        CurrentFrame is { } frame && frame.Names.Contains(name)
            ? frame.Arrays.ContainsKey(name)
            : _globalArrays.ContainsKey(name);

    private string Subscript(IReadOnlyList<AwkExpression> parts)
    {
        if (parts.Count == 1)
        {
            return Evaluate(parts[0]).ToText(ConvFmt);
        }

        return string.Join(Text("SUBSEP"), parts.Select(part => Evaluate(part).ToText(ConvFmt)));
    }

    // ---- statements ----

    private AwkFlow Execute(AwkStatement statement)
    {
        _budget.ThrowIfExpired();

        switch (statement)
        {
            case AwkBlock block:
            {
                foreach (var inner in block.Statements)
                {
                    var flow = Execute(inner);

                    if (flow != AwkFlow.Normal)
                    {
                        return flow;
                    }
                }

                return AwkFlow.Normal;
            }

            case AwkExpressionStatement expression:
                Evaluate(expression.Value);
                return AwkFlow.Normal;

            case AwkPrint print:
                return ExecutePrint(print);

            case AwkPrintf printf:
                return ExecutePrintf(printf);

            case AwkIf conditional:
                return Evaluate(conditional.Condition).ToBoolean()
                    ? Execute(conditional.Then)
                    : conditional.Else is { } alternative ? Execute(alternative) : AwkFlow.Normal;

            case AwkWhile loop:
                return ExecuteLoop(() => Evaluate(loop.Condition).ToBoolean(), loop.Body, null, testFirst: true);

            case AwkDoWhile loop:
                return ExecuteLoop(() => Evaluate(loop.Condition).ToBoolean(), loop.Body, null, testFirst: false);

            case AwkFor loop:
            {
                if (loop.Init is { } init)
                {
                    Execute(init);
                }

                return ExecuteLoop(
                    () => loop.Condition is null || Evaluate(loop.Condition).ToBoolean(),
                    loop.Body,
                    loop.Update,
                    testFirst: true);
            }

            case AwkForIn loop:
                return ExecuteForIn(loop);

            case AwkNext:
                return AwkFlow.Next;

            case AwkNextFile:
                return AwkFlow.NextFile;

            case AwkBreak:
                return AwkFlow.Break;

            case AwkContinue:
                return AwkFlow.Continue;

            case AwkExit exit:
                ExitCode = exit.Code is { } code ? (int)Evaluate(code).ToNumber() : ExitCode;
                return AwkFlow.Exit;

            case AwkReturn ret:
                ReturnValue = ret.Value is { } value ? Evaluate(value) : AwkValue.Uninitialized;
                return AwkFlow.Return;

            case AwkDelete delete:
            {
                var array = GetArray(delete.ArrayName);

                if (delete.Subscript is null)
                {
                    array.Clear();
                }
                else
                {
                    array.Remove(Subscript(delete.Subscript));
                }

                return AwkFlow.Normal;
            }

            case AwkGetlineStatement getline:
                Getline(getline.Target, getline.Source, getline.FromPipe);
                return AwkFlow.Normal;

            default:
                return AwkFlow.Normal;
        }
    }

    private AwkFlow ExecuteLoop(Func<bool> condition, AwkStatement body, AwkStatement? update, bool testFirst)
    {
        var first = true;

        while (true)
        {
            if ((testFirst || !first) && !condition())
            {
                return AwkFlow.Normal;
            }

            first = false;
            ChargeIteration();
            var flow = Execute(body);

            if (flow == AwkFlow.Break)
            {
                return AwkFlow.Normal;
            }

            if (flow is not (AwkFlow.Normal or AwkFlow.Continue))
            {
                return flow;
            }

            if (update is not null)
            {
                Execute(update);
            }
        }
    }

    private AwkFlow ExecuteForIn(AwkForIn loop)
    {
        foreach (var key in OrderedKeys(GetArray(loop.ArrayName)))
        {
            ChargeIteration();
            SetVariable(loop.Variable, AwkValue.Input(key));
            var flow = Execute(loop.Body);

            if (flow == AwkFlow.Break)
            {
                break;
            }

            if (flow is not (AwkFlow.Normal or AwkFlow.Continue))
            {
                return flow;
            }
        }

        return AwkFlow.Normal;
    }

    private void ChargeIteration()
    {
        _budget.ChargeLoopIteration(++_loopIterations);
        _budget.ThrowIfExpired();
    }

    /// <summary>
    /// Orders array keys for <c>for (k in a)</c>. AWK leaves the order unspecified; a
    /// deterministic one — numeric when every key is a number, lexical otherwise — makes
    /// scripts reproducible, which is what a sandbox handed to an agent needs.
    /// </summary>
    private static List<string> OrderedKeys(Dictionary<string, AwkValue> array)
    {
        var keys = array.Keys.ToList();
        var numeric = keys.Count > 0 && keys.TrueForAll(static key =>
            double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

        if (numeric)
        {
            keys.Sort(static (left, right) =>
                double.Parse(left, CultureInfo.InvariantCulture)
                    .CompareTo(double.Parse(right, CultureInfo.InvariantCulture)));
        }
        else
        {
            keys.Sort(StringComparer.Ordinal);
        }

        return keys;
    }

    private AwkFlow ExecutePrint(AwkPrint print)
    {
        var text = print.Arguments.Count == 0
            ? GetField(0)
            : string.Join(Text("OFS"), print.Arguments.Select(argument => Output(Evaluate(argument))));

        Write(text + Text("ORS"), print.Redirect);
        return AwkFlow.Normal;
    }

    private AwkFlow ExecutePrintf(AwkPrintf printf)
    {
        if (printf.Arguments.Count == 0)
        {
            return AwkFlow.Normal;
        }

        var format = Evaluate(printf.Arguments[0]).ToText(ConvFmt);
        var operands = printf.Arguments.Skip(1).Select(Evaluate).ToList();

        Write(AwkFormatter.Format(format, operands, ConvFmt), printf.Redirect);
        return AwkFlow.Normal;
    }

    /// <summary><c>print</c> renders numbers through OFMT; everything else uses CONVFMT.</summary>
    private string Output(AwkValue value) =>
        value.Kind == AwkValueKind.Number
            ? AwkValue.FormatNumber(value.ToNumber(), OutFmt)
            : value.ToText(ConvFmt);

    private void Write(string text, AwkRedirect? redirect)
    {
        if (redirect is null)
        {
            Stdout.Append(text);
            return;
        }

        var target = Evaluate(redirect.Target).ToText(ConvFmt);

        switch (target)
        {
            case "/dev/stdout" or "-":
                Stdout.Append(text);
                return;

            case "/dev/stderr":
                Stderr.Append(text);
                return;
        }

        if (redirect.Kind == AwkRedirectKind.Pipe)
        {
            // A pipe would need a process, and the sandbox has none. Saying so beats
            // dropping the output silently.
            Stderr.Append("awk: piping output to a command is not supported\n");
            return;
        }

        if (!_outputs.TryGetValue(target, out var output))
        {
            output = new AwkOutput(redirect.Kind == AwkRedirectKind.Append);
            _outputs[target] = output;
        }

        output.Content.Append(text);
    }

    // ---- expressions ----

    private AwkValue Evaluate(AwkExpression expression)
    {
        _budget.ThrowIfExpired();

        switch (expression)
        {
            case AwkNumber number:
                return AwkValue.Number(number.Value);

            case AwkString text:
                return AwkValue.Text(text.Value);

            case AwkRegex regex:
                // A bare regex in a value position tests the current record.
                return AwkValue.Bool(AwkRegexes.IsMatch(GetField(0), regex.Pattern));

            case AwkVariable variable:
                return GetVariable(variable.Name);

            case AwkField field:
                return AwkValue.Input(GetField((int)Evaluate(field.Index).ToNumber()));

            case AwkIndex index:
            {
                var array = GetArray(index.Name);
                var key = Subscript(index.Subscript);

                // Referencing an element creates it, which `in` can then observe — that is
                // AWK's behaviour, surprising as it is.
                if (!array.TryGetValue(key, out var value))
                {
                    array[key] = AwkValue.Uninitialized;
                    return AwkValue.Uninitialized;
                }

                return value;
            }

            case AwkGroup group:
                return Evaluate(group.Items[^1]);

            case AwkBinary binary:
                return EvaluateBinary(binary);

            case AwkUnary unary:
                return unary.Operator switch
                {
                    "!" => AwkValue.Bool(!Evaluate(unary.Operand).ToBoolean()),
                    "-" => AwkValue.Number(-Evaluate(unary.Operand).ToNumber()),
                    _ => AwkValue.Number(Evaluate(unary.Operand).ToNumber()),
                };

            case AwkConcat concat:
                return AwkValue.Text(
                    Evaluate(concat.Left).ToText(ConvFmt) + Evaluate(concat.Right).ToText(ConvFmt));

            case AwkAssign assign:
                return EvaluateAssign(assign);

            case AwkConditional conditional:
                return Evaluate(conditional.Condition).ToBoolean()
                    ? Evaluate(conditional.Then)
                    : Evaluate(conditional.Else);

            case AwkMatch match:
            {
                var subject = Evaluate(match.Subject).ToText(ConvFmt);
                var matched = AwkRegexes.IsMatch(subject, PatternOf(match.Pattern));
                return AwkValue.Bool(match.Negated ? !matched : matched);
            }

            case AwkIn membership:
                return AwkValue.Bool(GetArray(membership.ArrayName).ContainsKey(Subscript(membership.Subscript)));

            case AwkIncrement increment:
            {
                var before = Evaluate(increment.Target).ToNumber();
                var after = before + increment.Delta;
                Assign(increment.Target, AwkValue.Number(after));
                return AwkValue.Number(increment.Prefix ? after : before);
            }

            case AwkCall call:
                return EvaluateCall(call);

            case AwkGetline getline:
                return AwkValue.Number(Getline(getline.Target, getline.Source, getline.FromPipe));

            default:
                return AwkValue.Uninitialized;
        }
    }

    /// <summary>
    /// A regex operand may be a literal or any expression producing a pattern string, and
    /// the two are spelled identically at the call site.
    /// </summary>
    private string PatternOf(AwkExpression expression) =>
        expression is AwkRegex regex ? regex.Pattern : Evaluate(expression).ToText(ConvFmt);

    private AwkValue EvaluateBinary(AwkBinary binary)
    {
        switch (binary.Operator)
        {
            case "&&":
                return AwkValue.Bool(Evaluate(binary.Left).ToBoolean() && Evaluate(binary.Right).ToBoolean());

            case "||":
                return AwkValue.Bool(Evaluate(binary.Left).ToBoolean() || Evaluate(binary.Right).ToBoolean());
        }

        var left = Evaluate(binary.Left);
        var right = Evaluate(binary.Right);

        switch (binary.Operator)
        {
            case "+": return AwkValue.Number(left.ToNumber() + right.ToNumber());
            case "-": return AwkValue.Number(left.ToNumber() - right.ToNumber());
            case "*": return AwkValue.Number(left.ToNumber() * right.ToNumber());
            case "/": return AwkValue.Number(Divide(left.ToNumber(), right.ToNumber(), "/"));
            case "%": return AwkValue.Number(Modulo(left.ToNumber(), right.ToNumber(), "%"));
            case "^": return AwkValue.Number(Math.Pow(left.ToNumber(), right.ToNumber()));
        }

        var comparison = AwkValue.Compare(left, right, ConvFmt);

        return AwkValue.Bool(binary.Operator switch
        {
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            "==" => comparison == 0,
            "!=" => comparison != 0,
            _ => false,
        });
    }

    private static double Divide(double left, double right, string op) =>
        right == 0
            ? throw new AwkRuntimeException($"awk: division by zero in {op}")
            : left / right;

    /// <summary>
    /// AWK's <c>%</c> is C's <c>fmod</c>: the result takes the dividend's sign, unlike
    /// <see cref="Math.IEEERemainder"/>.
    /// </summary>
    private static double Modulo(double left, double right, string op) =>
        right == 0
            ? throw new AwkRuntimeException($"awk: division by zero in {op}")
            : left - (right * Math.Truncate(left / right));

    private AwkValue EvaluateAssign(AwkAssign assign)
    {
        if (assign.Operator == "=")
        {
            var assigned = Evaluate(assign.Value);

            // The assigned value keeps its own kind, so `x = $1` still compares numerically.
            Assign(assign.Target, assigned);
            return assigned;
        }

        var current = Evaluate(assign.Target).ToNumber();
        var operand = Evaluate(assign.Value).ToNumber();

        var result = assign.Operator switch
        {
            "+=" => current + operand,
            "-=" => current - operand,
            "*=" => current * operand,
            "/=" => Divide(current, operand, "/="),
            "%=" => Modulo(current, operand, "%="),
            "^=" => Math.Pow(current, operand),
            _ => operand,
        };

        var value = AwkValue.Number(result);
        Assign(assign.Target, value);
        return value;
    }

    private void Assign(AwkExpression target, AwkValue value)
    {
        switch (target)
        {
            case AwkVariable variable:
                SetVariable(variable.Name, value);
                break;

            case AwkField field:
                SetField((int)Evaluate(field.Index).ToNumber(), value.ToText(ConvFmt));
                break;

            case AwkIndex index:
                GetArray(index.Name)[Subscript(index.Subscript)] = value;
                break;

            default:
                throw new AwkRuntimeException("awk: assignment to a non-lvalue");
        }
    }

    // ---- functions ----

    private AwkValue EvaluateCall(AwkCall call) =>
        _program.Functions.TryGetValue(call.Name, out var function)
            ? CallUser(function, call.Arguments)
            : CallBuiltin(call);

    private AwkValue CallUser(AwkFunction function, IReadOnlyList<AwkExpression> arguments)
    {
        using var depth = _budget.EnterFunction();
        var frame = new AwkFrame();

        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var name = function.Parameters[i];

            if (i >= arguments.Count)
            {
                // Extra parameters are AWK's only way to declare a local, so they are named
                // but left unset.
                frame.Names.Add(name);
                continue;
            }

            // An array argument passes by reference and a scalar by value. A bare name that
            // is neither yet becomes an array, which is what makes `split(s, out)` work
            // through a wrapper function.
            if (arguments[i] is AwkVariable variable && !IsScalar(variable.Name))
            {
                var array = GetArray(variable.Name);
                frame.Names.Add(name);
                frame.Arrays[name] = array;
                continue;
            }

            var value = Evaluate(arguments[i]);
            frame.Names.Add(name);
            frame.Scalars[name] = value;
        }

        _frames.Push(frame);
        var saved = ReturnValue;
        ReturnValue = AwkValue.Uninitialized;

        try
        {
            Execute(function.Body);
            return ReturnValue;
        }
        finally
        {
            ReturnValue = saved;
            _frames.Pop();
        }
    }

    private bool IsScalar(string name)
    {
        if (IsArray(name))
        {
            return false;
        }

        if (CurrentFrame is { } frame && frame.Names.Contains(name))
        {
            return frame.Scalars.ContainsKey(name);
        }

        return _globals.ContainsKey(name);
    }

    private AwkValue CallBuiltin(AwkCall call)
    {
        var arguments = call.Arguments;

        switch (call.Name)
        {
            case "length":
            {
                if (arguments.Count == 0)
                {
                    return AwkValue.Number(GetField(0).Length);
                }

                if (arguments[0] is AwkVariable variable && IsArray(variable.Name))
                {
                    return AwkValue.Number(GetArray(variable.Name).Count);
                }

                return AwkValue.Number(Arg(arguments, 0).Length);
            }

            case "substr":
                return Substr(arguments);

            case "index":
                return AwkValue.Number(Arg(arguments, 0).IndexOf(Arg(arguments, 1), StringComparison.Ordinal) + 1);

            case "split":
                return Split(arguments);

            case "sub" or "gsub":
                return Substitute(call.Name == "gsub", arguments);

            case "gensub":
                return Gensub(arguments);

            case "match":
                return Match(arguments);

            case "sprintf":
                return AwkValue.Text(AwkFormatter.Format(
                    Arg(arguments, 0),
                    arguments.Skip(1).Select(Evaluate).ToList(),
                    ConvFmt));

            case "printf":
            {
                // `printf("...", x)` reaches here when the parser read it as a call, which
                // it does whenever the name abuts its parenthesis.
                if (arguments.Count > 0)
                {
                    Stdout.Append(AwkFormatter.Format(
                        Arg(arguments, 0),
                        arguments.Skip(1).Select(Evaluate).ToList(),
                        ConvFmt));
                }

                return AwkValue.Uninitialized;
            }

            case "toupper":
                return AwkValue.Text(Arg(arguments, 0).ToUpperInvariant());

            case "tolower":
                return AwkValue.Text(Arg(arguments, 0).ToLowerInvariant());

            case "sin": return AwkValue.Number(Math.Sin(Number(arguments, 0)));
            case "cos": return AwkValue.Number(Math.Cos(Number(arguments, 0)));
            case "atan2": return AwkValue.Number(Math.Atan2(Number(arguments, 0), Number(arguments, 1)));
            case "exp": return AwkValue.Number(Math.Exp(Number(arguments, 0)));
            case "log": return AwkValue.Number(Math.Log(Number(arguments, 0)));
            case "sqrt": return AwkValue.Number(Math.Sqrt(Number(arguments, 0)));
            case "int": return AwkValue.Number(Math.Truncate(Number(arguments, 0)));

            case "rand":
                return AwkValue.Number(_random.NextDouble());

            case "srand":
            {
                var previous = _lastSeed;
                _lastSeed = arguments.Count > 0 ? Number(arguments, 0) : 0;
                _random = new Random((int)_lastSeed);
                return AwkValue.Number(previous);
            }

            case "system":
                // Running a command would mean spawning a process, which the sandbox never
                // does. Reporting failure is truthful; pretending success would not be.
                Stderr.Append("awk: system() is not available in this sandbox\n");
                return AwkValue.Number(-1);

            case "close":
            {
                var name = Arg(arguments, 0);
                var closed = _outputs.Remove(name) | _inputs.Remove(name);
                return AwkValue.Number(closed ? 0 : -1);
            }

            case "fflush":
                return AwkValue.Number(0);

            default:
                throw new AwkRuntimeException($"awk: calling undefined function {call.Name}");
        }
    }

    private string Arg(IReadOnlyList<AwkExpression> arguments, int index) =>
        index < arguments.Count ? Evaluate(arguments[index]).ToText(ConvFmt) : string.Empty;

    private double Number(IReadOnlyList<AwkExpression> arguments, int index) =>
        index < arguments.Count ? Evaluate(arguments[index]).ToNumber() : 0;

    /// <summary>
    /// <c>substr(s, m, n)</c>, whose out-of-range behaviour is specified as a character
    /// range that may begin before the string: <c>substr("hello", 0, 2)</c> is <c>"h"</c>,
    /// not <c>"he"</c>.
    /// </summary>
    private AwkValue Substr(IReadOnlyList<AwkExpression> arguments)
    {
        var text = Arg(arguments, 0);
        var start = Math.Round(Number(arguments, 1), MidpointRounding.AwayFromZero);

        var end = arguments.Count >= 3
            ? start + Math.Round(Number(arguments, 2), MidpointRounding.AwayFromZero)
            : text.Length + 1;

        var from = (int)Math.Clamp(start, 1, text.Length + 1);
        var to = (int)Math.Clamp(end, 1, text.Length + 1);

        return AwkValue.Text(from >= to ? string.Empty : text[(from - 1)..(to - 1)]);
    }

    private AwkValue Split(IReadOnlyList<AwkExpression> arguments)
    {
        if (arguments.Count < 2 || arguments[1] is not AwkVariable target)
        {
            throw new AwkRuntimeException("awk: split: second argument must be an array");
        }

        var text = Arg(arguments, 0);
        var separator = arguments.Count >= 3 ? PatternOf(arguments[2]) : Text("FS");

        var array = GetArray(target.Name);
        array.Clear();

        var parts = SplitRecord(text, separator);

        for (var i = 0; i < parts.Count; i++)
        {
            array[(i + 1).ToString(CultureInfo.InvariantCulture)] = AwkValue.Input(parts[i]);
        }

        return AwkValue.Number(parts.Count);
    }

    private AwkValue Substitute(bool global, IReadOnlyList<AwkExpression> arguments)
    {
        var pattern = arguments.Count > 0 ? PatternOf(arguments[0]) : string.Empty;
        var replacement = Arg(arguments, 1);
        var target = arguments.Count >= 3 ? arguments[2] : new AwkField(new AwkNumber(0));
        var subject = Evaluate(target).ToText(ConvFmt);

        var count = 0;
        var result = ReplaceAll(subject, pattern, replacement, global, ref count);

        if (count > 0)
        {
            Assign(target, AwkValue.Text(result));
        }

        return AwkValue.Number(count);
    }

    /// <summary>
    /// GNU's <c>gensub</c>, which unlike <c>sub</c> returns the result instead of assigning
    /// it and supports <c>\1</c>–<c>\9</c> backreferences in the replacement.
    /// </summary>
    private AwkValue Gensub(IReadOnlyList<AwkExpression> arguments)
    {
        var pattern = arguments.Count > 0 ? PatternOf(arguments[0]) : string.Empty;
        var replacement = Arg(arguments, 1);
        var how = Arg(arguments, 2);
        var subject = arguments.Count >= 4 ? Arg(arguments, 3) : GetField(0);

        var global = how.Equals("g", StringComparison.OrdinalIgnoreCase);
        var which = global ? 0 : Math.Max((int)AwkValue.ParsePrefix(how), 1);

        var builder = new StringBuilder();
        var position = 0;
        var occurrence = 0;

        foreach (var match in Matches(subject, pattern))
        {
            occurrence++;

            if (!global && occurrence != which)
            {
                continue;
            }

            builder.Append(subject, position, match.Index - position);
            builder.Append(ExpandGensub(replacement, match));
            position = match.Index + match.Length;

            if (!global)
            {
                break;
            }
        }

        builder.Append(subject, position, subject.Length - position);
        return AwkValue.Text(builder.ToString());
    }

    /// <summary>
    /// <c>match(s, r)</c>, plus gawk's third argument: an array that receives the whole
    /// match at index 0 and each capture group at its own index.
    /// </summary>
    private AwkValue Match(IReadOnlyList<AwkExpression> arguments)
    {
        var subject = Arg(arguments, 0);
        var pattern = arguments.Count > 1 ? PatternOf(arguments[1]) : string.Empty;
        var captures = arguments.Count > 2 && arguments[2] is AwkVariable target ? GetArray(target.Name) : null;
        captures?.Clear();

        System.Text.RegularExpressions.Match match;

        try
        {
            match = AwkRegexes.Get(pattern).Match(subject);
        }
        catch (RegexMatchTimeoutException)
        {
            match = System.Text.RegularExpressions.Match.Empty;
        }

        if (!match.Success)
        {
            _globals["RSTART"] = AwkValue.Number(0);
            _globals["RLENGTH"] = AwkValue.Number(-1);
            return AwkValue.Number(0);
        }

        if (captures is not null)
        {
            for (var group = 0; group < match.Groups.Count; group++)
            {
                captures[group.ToString(CultureInfo.InvariantCulture)] =
                    AwkValue.Input(match.Groups[group].Value);
            }
        }

        _globals["RSTART"] = AwkValue.Number(match.Index + 1);
        _globals["RLENGTH"] = AwkValue.Number(match.Length);
        return AwkValue.Number(match.Index + 1);
    }

    private static List<System.Text.RegularExpressions.Match> Matches(string subject, string pattern)
    {
        List<System.Text.RegularExpressions.Match> matches = [];

        try
        {
            var regex = AwkRegexes.Get(pattern);
            var position = 0;

            while (position <= subject.Length)
            {
                var match = regex.Match(subject, position);

                if (!match.Success)
                {
                    break;
                }

                matches.Add(match);

                // An empty match must still advance, or the scan never terminates.
                position = match.Length == 0 ? match.Index + 1 : match.Index + match.Length;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }

        return matches;
    }

    /// <summary>
    /// Applies a <c>sub</c>/<c>gsub</c> replacement, in which <c>&amp;</c> stands for the
    /// matched text and <c>\&amp;</c> for a literal ampersand.
    /// </summary>
    private static string ReplaceAll(
        string subject,
        string pattern,
        string replacement,
        bool global,
        ref int count)
    {
        var builder = new StringBuilder();
        var position = 0;

        foreach (var match in Matches(subject, pattern))
        {
            if (match.Index < position)
            {
                continue;
            }

            builder.Append(subject, position, match.Index - position);
            builder.Append(ExpandAmpersand(replacement, match.Value));
            position = match.Index + match.Length;
            count++;

            if (!global)
            {
                break;
            }
        }

        builder.Append(subject, position, subject.Length - position);
        return builder.ToString();
    }

    private static string ExpandAmpersand(string replacement, string matched)
    {
        var builder = new StringBuilder(replacement.Length);

        for (var i = 0; i < replacement.Length; i++)
        {
            var c = replacement[i];

            if (c == '\\' && i + 1 < replacement.Length && replacement[i + 1] is '&' or '\\')
            {
                builder.Append(replacement[++i]);
                continue;
            }

            if (c == '&')
            {
                builder.Append(matched);
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string ExpandGensub(string replacement, System.Text.RegularExpressions.Match match)
    {
        var builder = new StringBuilder(replacement.Length);

        for (var i = 0; i < replacement.Length; i++)
        {
            var c = replacement[i];

            if (c == '\\' && i + 1 < replacement.Length)
            {
                var next = replacement[++i];

                if (next is >= '0' and <= '9')
                {
                    var group = next - '0';
                    builder.Append(group < match.Groups.Count ? match.Groups[group].Value : string.Empty);
                    continue;
                }

                builder.Append(next);
                continue;
            }

            if (c == '&')
            {
                builder.Append(match.Value);
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    // ---- getline ----

    /// <summary>Runs one <c>getline</c>, returning 1 for a record, 0 at end of input, -1 on error.</summary>
    private double Getline(AwkExpression? target, AwkExpression? source, bool fromPipe)
    {
        if (fromPipe)
        {
            // Reading from a command needs a process, which the sandbox does not have.
            Stderr.Append("awk: reading from a command is not supported\n");
            return -1;
        }

        string line;

        if (source is null)
        {
            if (!AdvanceRecord(out line, out _))
            {
                return 0;
            }

            Bump("NR");
            Bump("FNR");
        }
        else
        {
            var name = Evaluate(source).ToText(ConvFmt);

            if (!_inputs.TryGetValue(name, out var input))
            {
                var content = _readFile(name);

                if (content is null)
                {
                    return -1;
                }

                input = new AwkInput(SplitLines(content));
                _inputs[name] = input;
            }

            if (input.Index >= input.Lines.Length)
            {
                return 0;
            }

            line = input.Lines[input.Index++];
        }

        if (target is null)
        {
            SetRecord(line);
        }
        else
        {
            Assign(target, AwkValue.Input(line));
        }

        return 1;
    }

    /// <summary>Splits input into records, dropping a single trailing newline.</summary>
    public static string[] SplitLines(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var trimmed = content.EndsWith('\n') ? content[..^1] : content;
        return trimmed.Split('\n');
    }

    /// <summary>
    /// Expands C escapes in text AWK processes at use time rather than at parse time: a
    /// <c>-v</c> assignment and a <c>-F</c> separator both arrive as raw shell words.
    /// </summary>
    public static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                builder.Append(text[i]);
                continue;
            }

            var escape = text[++i];

            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'v': builder.Append('\v'); break;
                case '\\': builder.Append('\\'); break;
                case '"': builder.Append('"'); break;
                case '/': builder.Append('/'); break;

                default:
                    builder.Append('\\').Append(escape);
                    break;
            }
        }

        return builder.ToString();
    }
}
