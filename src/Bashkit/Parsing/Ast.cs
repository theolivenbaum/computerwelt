namespace Bashkit.Parsing;

/// <summary>A parsed script: a sequence of top-level commands.</summary>
/// <param name="Commands">The commands, in source order.</param>
public sealed record Script(IReadOnlyList<Node> Commands)
{
    /// <summary>An empty script.</summary>
    public static Script Empty { get; } = new([]);

    /// <summary>
    /// The text this script was parsed from, kept so a node's offset can be turned into a
    /// line number for <c>$LINENO</c>.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The 1-based line <paramref name="offset"/> falls on.</summary>
    public int LineAt(int offset)
    {
        var line = 1;

        for (var i = 0; i < offset && i < Source.Length; i++)
        {
            if (Source[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}

/// <summary>
/// Base of the syntax tree.
/// </summary>
/// <remarks>
/// Rust models the grammar with payload-carrying enums; C# has no native discriminated
/// union, so the port uses a sealed record hierarchy and relies on <c>switch</c> pattern
/// matching for exhaustive dispatch. Records give structural equality for free, which the
/// parser tests lean on.
/// </remarks>
public abstract record Node
{
    /// <summary>The source range this node covers.</summary>
    public Span Span { get; init; }

    /// <summary>
    /// The 1-based source line this node starts on, or zero when unknown.
    /// </summary>
    /// <remarks>
    /// Recorded at parse time rather than derived later, because a function's body outlives
    /// the script text it came from: by the time the function is called, the running script
    /// may be a different file entirely.
    /// </remarks>
    public int Line { get; init; }
}

/// <summary>A command name with arguments, assignments and redirections.</summary>
/// <param name="Assignments">Variable assignments preceding the command name.</param>
/// <param name="Words">The command name followed by its arguments, before expansion.</param>
/// <param name="Redirects">Redirections attached to this command.</param>
public sealed record SimpleCommand(
    IReadOnlyList<Assignment> Assignments,
    IReadOnlyList<Word> Words,
    IReadOnlyList<Redirect> Redirects) : Node;

/// <summary>A sequence of commands joined by <c>|</c>.</summary>
/// <param name="Stages">The pipeline stages, at least one.</param>
/// <param name="Negated">True when the pipeline was prefixed with <c>!</c>.</param>
/// <param name="PipeStderr">Per-stage flags: true where the stage was joined with <c>|&amp;</c>.</param>
public sealed record Pipeline(
    IReadOnlyList<Node> Stages,
    bool Negated,
    IReadOnlyList<bool> PipeStderr) : Node;

/// <summary>How two list elements are joined.</summary>
public enum ListOperator
{
    /// <summary><c>;</c> or a newline: run unconditionally.</summary>
    Sequence,

    /// <summary><c>&amp;&amp;</c>: run only if the left side succeeded.</summary>
    And,

    /// <summary><c>||</c>: run only if the left side failed.</summary>
    Or,

    /// <summary><c>&amp;</c>: run the left side in the background.</summary>
    Background,
}

/// <summary>A list of commands joined by <c>;</c>, <c>&amp;</c>, <c>&amp;&amp;</c> or <c>||</c>.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Operator">How the operands are joined.</param>
/// <param name="Right">The right operand, or <see langword="null"/> for a trailing operator.</param>
public sealed record CommandList(Node Left, ListOperator Operator, Node? Right) : Node;

/// <summary>A compound command with attached redirections.</summary>
/// <param name="Body">The compound construct.</param>
/// <param name="Redirects">Redirections applied to the whole construct.</param>
public sealed record CompoundCommand(Node Body, IReadOnlyList<Redirect> Redirects) : Node;

/// <summary><c>if ... then ... elif ... else ... fi</c>.</summary>
/// <param name="Condition">The condition list.</param>
/// <param name="Then">The consequent.</param>
/// <param name="Else">The alternative, which may itself be another <see cref="IfCommand"/> for <c>elif</c>.</param>
public sealed record IfCommand(Node Condition, Node Then, Node? Else) : Node;

/// <summary><c>for NAME in WORDS; do ...; done</c>.</summary>
/// <param name="Variable">The loop variable's name.</param>
/// <param name="Items">The word list, or <see langword="null"/> when the <c>in</c> clause was omitted (implying <c>"$@"</c>).</param>
/// <param name="Body">The loop body.</param>
public sealed record ForCommand(string Variable, IReadOnlyList<Word>? Items, Node Body) : Node;

/// <summary><c>coproc [NAME] command</c> — runs a command with a readable output.</summary>
/// <param name="Name">The array the descriptors are published in, <c>COPROC</c> by default.</param>
/// <param name="Body">The command to run.</param>
public sealed record CoprocessCommand(string Name, Node Body) : Node;

/// <summary><c>select NAME [in words]; do ...; done</c>.</summary>
/// <param name="Variable">The variable each chosen item is assigned to.</param>
/// <param name="Items">The menu items, or <see langword="null"/> for the positional parameters.</param>
/// <param name="Body">The loop body.</param>
public sealed record SelectCommand(string Variable, IReadOnlyList<Word>? Items, Node Body) : Node;

/// <summary><c>for ((init; condition; update)); do ...; done</c>.</summary>
/// <param name="Init">The initializer expression, unparsed.</param>
/// <param name="Condition">The condition expression, unparsed. An empty string means "always true".</param>
/// <param name="Update">The update expression, unparsed.</param>
/// <param name="Body">The loop body.</param>
public sealed record ArithmeticForCommand(string Init, string Condition, string Update, Node Body) : Node;

/// <summary><c>while COND; do ...; done</c>.</summary>
/// <param name="Condition">The condition list.</param>
/// <param name="Body">The loop body.</param>
public sealed record WhileCommand(Node Condition, Node Body) : Node;

/// <summary><c>until COND; do ...; done</c>.</summary>
/// <param name="Condition">The condition list.</param>
/// <param name="Body">The loop body.</param>
public sealed record UntilCommand(Node Condition, Node Body) : Node;

/// <summary><c>case WORD in PATTERN) ...;; esac</c>.</summary>
/// <param name="Subject">The word matched against each pattern.</param>
/// <param name="Items">The branches, in source order.</param>
public sealed record CaseCommand(Word Subject, IReadOnlyList<CaseItem> Items) : Node;

/// <summary>How a case branch ends, which decides what happens after it runs.</summary>
public enum CaseTerminator
{
    /// <summary><c>;;</c> — stop.</summary>
    Break,

    /// <summary><c>;&amp;</c> — run the next branch's body unconditionally.</summary>
    FallThrough,

    /// <summary><c>;;&amp;</c> — keep testing subsequent patterns.</summary>
    ContinueMatching,
}

/// <summary>One <c>case</c> branch.</summary>
/// <param name="Patterns">The alternative patterns, joined by <c>|</c> in source.</param>
/// <param name="Body">The branch body, or <see langword="null"/> when empty.</param>
/// <param name="Terminator">How the branch ends.</param>
public sealed record CaseItem(IReadOnlyList<Word> Patterns, Node? Body, CaseTerminator Terminator);

/// <summary><c>( ... )</c> — runs in a subshell, so its state changes do not escape.</summary>
/// <param name="Body">The subshell body.</param>
public sealed record Subshell(Node Body) : Node;

/// <summary><c>{ ...; }</c> — grouped, but runs in the current shell.</summary>
/// <param name="Body">The group body.</param>
public sealed record BraceGroup(Node Body) : Node;

/// <summary><c>(( expression ))</c> — arithmetic evaluation as a command.</summary>
/// <param name="Expression">The unparsed arithmetic text.</param>
public sealed record ArithmeticCommand(string Expression) : Node;

/// <summary><c>[[ expression ]]</c> — the extended conditional.</summary>
/// <param name="Expression">The parsed condition.</param>
public sealed record ConditionalCommand(ConditionalExpression Expression) : Node;

/// <summary><c>NAME() { ... }</c>.</summary>
/// <param name="Name">The function's name.</param>
/// <param name="Body">The function body.</param>
public sealed record FunctionDef(string Name, Node Body) : Node
{
    /// <summary>The definition's source text, for <c>declare -f</c> and <c>type</c>.</summary>
    public string? Source { get; init; }
}

/// <summary>A pipeline prefixed with the <c>time</c> keyword.</summary>
/// <param name="Body">What to time, or <see langword="null"/> for a bare <c>time</c>.</param>
/// <param name="Posix">True for <c>time -p</c>, which uses the plainer POSIX format.</param>
public sealed record TimedCommand(Node? Body, bool Posix) : Node;

/// <summary>A variable assignment.</summary>
/// <param name="Name">The variable name.</param>
/// <param name="Value">The assigned value.</param>
/// <param name="Append">True for <c>NAME+=value</c>.</param>
/// <param name="Index">For <c>NAME[expr]=value</c>, the unparsed subscript.</param>
public sealed record Assignment(string Name, AssignmentValue Value, bool Append, string? Index);

/// <summary>The right-hand side of an assignment.</summary>
public abstract record AssignmentValue
{
    /// <summary>A scalar value.</summary>
    /// <param name="Word">The word to expand.</param>
    public sealed record Scalar(Word Word) : AssignmentValue;

    /// <summary>An array literal, <c>NAME=(a b c)</c> or <c>NAME=([k]=v)</c>.</summary>
    /// <param name="Elements">The element words. Keyed elements carry their key.</param>
    public sealed record Array(IReadOnlyList<ArrayElement> Elements) : AssignmentValue;
}

/// <summary>One element of an array literal.</summary>
/// <param name="Key">The explicit subscript, unparsed, or <see langword="null"/> for positional elements.</param>
/// <param name="Value">The element's value.</param>
public readonly record struct ArrayElement(string? Key, Word Value);

/// <summary>Which descriptor a redirection affects and how.</summary>
public enum RedirectKind
{
    /// <summary><c>&gt;</c></summary>
    Output,

    /// <summary><c>&gt;&gt;</c></summary>
    Append,

    /// <summary><c>&lt;</c></summary>
    Input,

    /// <summary><c>&lt;&lt;</c> and <c>&lt;&lt;-</c></summary>
    HereDocument,

    /// <summary><c>&lt;&lt;&lt;</c></summary>
    HereString,

    /// <summary><c>&lt;&gt;</c></summary>
    ReadWrite,

    /// <summary><c>&gt;&amp;</c></summary>
    DuplicateOutput,

    /// <summary><c>&lt;&amp;</c></summary>
    DuplicateInput,

    /// <summary><c>&gt;|</c></summary>
    Clobber,

    /// <summary><c>&amp;&gt;</c></summary>
    OutputBoth,

    /// <summary><c>&amp;&gt;&gt;</c></summary>
    AppendBoth,
}

/// <summary>One redirection.</summary>
/// <param name="Kind">What the redirection does.</param>
/// <param name="Fd">The affected descriptor, defaulting to 1 for output kinds and 0 for input kinds.</param>
/// <param name="Target">The filename, descriptor or here-string word.</param>
/// <param name="HereDocumentBody">For a here-document, the collected body.</param>
/// <param name="HereDocumentExpands">False when the here-document delimiter was quoted, which suppresses expansion.</param>
public sealed record Redirect(
    RedirectKind Kind,
    int Fd,
    Word Target,
    string? HereDocumentBody = null,
    bool HereDocumentExpands = true);
