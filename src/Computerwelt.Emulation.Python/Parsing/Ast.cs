namespace Computerwelt.Emulation.Python.Parsing;

/// <summary>A parsed module: the statements of one source file.</summary>
/// <param name="Body">The top-level statements.</param>
public sealed record PyModule(IReadOnlyList<Statement> Body);

/// <summary>Base of every syntax node, carrying the position tracebacks report.</summary>
public abstract record PyNode
{
    /// <summary>1-based line number of the node's first token.</summary>
    public int Line { get; init; }

    /// <summary>0-based column of the node's first token.</summary>
    public int Column { get; init; }
}

/// <summary>Base of every statement.</summary>
public abstract record Statement : PyNode;

/// <summary>An expression evaluated for its effect; the value is discarded.</summary>
/// <param name="Value">The expression.</param>
public sealed record ExpressionStatement(Expression Value) : Statement;

/// <summary>An assignment, possibly to several targets at once.</summary>
/// <param name="Targets">The assignment targets, left to right.</param>
/// <param name="Value">The value assigned.</param>
/// <param name="Annotation">A type annotation, when the statement carried one.</param>
public sealed record Assign(IReadOnlyList<Expression> Targets, Expression? Value, Expression? Annotation = null) : Statement;

/// <summary>An augmented assignment such as <c>x += 1</c>.</summary>
/// <param name="Target">The target, which is both read and written.</param>
/// <param name="Operator">The binary operator, without the <c>=</c>.</param>
/// <param name="Value">The right-hand side.</param>
public sealed record AugmentedAssign(Expression Target, string Operator, Expression Value) : Statement;

/// <summary><c>if</c> with its <c>elif</c> chain flattened into nested else-branches.</summary>
/// <param name="Test">The condition.</param>
/// <param name="Body">The consequent.</param>
/// <param name="OrElse">The alternative, empty when there is none.</param>
public sealed record If(Expression Test, IReadOnlyList<Statement> Body, IReadOnlyList<Statement> OrElse) : Statement;

/// <summary><c>while</c>, with the <c>else</c> clause that runs when no <c>break</c> occurred.</summary>
/// <param name="Test">The condition.</param>
/// <param name="Body">The loop body.</param>
/// <param name="OrElse">The <c>else</c> clause.</param>
public sealed record While(Expression Test, IReadOnlyList<Statement> Body, IReadOnlyList<Statement> OrElse) : Statement;

/// <summary><c>for</c>, with the <c>else</c> clause that runs when no <c>break</c> occurred.</summary>
/// <param name="Target">The loop variable, or a tuple of them.</param>
/// <param name="Iterable">The expression iterated over.</param>
/// <param name="Body">The loop body.</param>
/// <param name="OrElse">The <c>else</c> clause.</param>
public sealed record For(Expression Target, Expression Iterable, IReadOnlyList<Statement> Body, IReadOnlyList<Statement> OrElse) : Statement;

/// <summary><c>try</c> with its handlers, <c>else</c> and <c>finally</c>.</summary>
/// <param name="Body">The guarded block.</param>
/// <param name="Handlers">The <c>except</c> clauses, in order.</param>
/// <param name="OrElse">The <c>else</c> clause, run when the body did not raise.</param>
/// <param name="FinallyBody">The <c>finally</c> clause, run on every path.</param>
public sealed record Try(
    IReadOnlyList<Statement> Body,
    IReadOnlyList<ExceptHandler> Handlers,
    IReadOnlyList<Statement> OrElse,
    IReadOnlyList<Statement> FinallyBody) : Statement;

/// <summary>One <c>except</c> clause.</summary>
/// <param name="ExceptionType">The type or tuple of types caught, or <see langword="null"/> for a bare <c>except</c>.</param>
/// <param name="Name">The name bound to the exception, if any.</param>
/// <param name="Body">The handler body.</param>
public sealed record ExceptHandler(Expression? ExceptionType, string? Name, IReadOnlyList<Statement> Body) : PyNode;

/// <summary><c>with</c>, binding one or more context managers.</summary>
/// <param name="Items">The managed expressions and their optional targets.</param>
/// <param name="Body">The managed block.</param>
public sealed record With(IReadOnlyList<WithItem> Items, IReadOnlyList<Statement> Body) : Statement;

/// <summary>One <c>with</c> item.</summary>
/// <param name="ContextManager">The expression producing the manager.</param>
/// <param name="Target">The name bound by <c>as</c>, if any.</param>
public sealed record WithItem(Expression ContextManager, Expression? Target);

/// <summary>A function definition.</summary>
/// <param name="Name">The function's name.</param>
/// <param name="Parameters">Its parameter list.</param>
/// <param name="Body">Its body.</param>
/// <param name="Decorators">Decorator expressions, outermost first.</param>
/// <param name="IsAsync">True for <c>async def</c>.</param>
/// <param name="ReturnAnnotation">The <c>-&gt;</c> annotation, if any.</param>
public sealed record FunctionDef(
    string Name,
    ParameterList Parameters,
    IReadOnlyList<Statement> Body,
    IReadOnlyList<Expression> Decorators,
    bool IsAsync,
    Expression? ReturnAnnotation) : Statement;

/// <summary>A class definition.</summary>
/// <param name="Name">The class's name.</param>
/// <param name="Bases">Base-class expressions. Non-empty bases are rejected at runtime.</param>
/// <param name="Body">The class body.</param>
/// <param name="Decorators">Decorator expressions, outermost first.</param>
public sealed record ClassDef(
    string Name,
    IReadOnlyList<Expression> Bases,
    IReadOnlyList<Statement> Body,
    IReadOnlyList<Expression> Decorators) : Statement;

/// <summary>A formal parameter list.</summary>
/// <param name="Parameters">Positional and keyword parameters, in order.</param>
/// <param name="VarArgs">The <c>*args</c> name, if present.</param>
/// <param name="KeywordOnly">Parameters after a <c>*</c> separator.</param>
/// <param name="KeywordArgs">The <c>**kwargs</c> name, if present.</param>
/// <param name="PositionalOnlyCount">How many leading parameters precede a <c>/</c>.</param>
public sealed record ParameterList(
    IReadOnlyList<Parameter> Parameters,
    string? VarArgs,
    IReadOnlyList<Parameter> KeywordOnly,
    string? KeywordArgs,
    int PositionalOnlyCount = 0)
{
    /// <summary>An empty parameter list.</summary>
    public static ParameterList Empty { get; } = new([], null, [], null);
}

/// <summary>One formal parameter.</summary>
/// <param name="Name">The parameter's name.</param>
/// <param name="Default">Its default value expression, if any.</param>
/// <param name="Annotation">Its type annotation, if any.</param>
public sealed record Parameter(string Name, Expression? Default, Expression? Annotation);

/// <summary><c>return</c>.</summary>
/// <param name="Value">The returned expression, or <see langword="null"/> for a bare <c>return</c>.</param>
public sealed record Return(Expression? Value) : Statement;

/// <summary><c>raise</c>.</summary>
/// <param name="Exception">The raised expression, or <see langword="null"/> for a bare re-raise.</param>
/// <param name="Cause">The <c>from</c> expression, if any.</param>
public sealed record Raise(Expression? Exception, Expression? Cause) : Statement;

/// <summary><c>assert</c>.</summary>
/// <param name="Test">The condition.</param>
/// <param name="Message">The message expression, if any.</param>
public sealed record Assert(Expression Test, Expression? Message) : Statement;

/// <summary><c>del</c>.</summary>
/// <param name="Targets">The deleted targets.</param>
public sealed record Delete(IReadOnlyList<Expression> Targets) : Statement;

/// <summary><c>pass</c>.</summary>
public sealed record Pass : Statement;

/// <summary><c>break</c>.</summary>
public sealed record Break : Statement;

/// <summary><c>continue</c>.</summary>
public sealed record Continue : Statement;

/// <summary><c>global</c>.</summary>
/// <param name="Names">The declared names.</param>
public sealed record Global(IReadOnlyList<string> Names) : Statement;

/// <summary><c>nonlocal</c>.</summary>
/// <param name="Names">The declared names.</param>
public sealed record Nonlocal(IReadOnlyList<string> Names) : Statement;

/// <summary><c>import</c>.</summary>
/// <param name="Names">The imported module names and their aliases.</param>
public sealed record Import(IReadOnlyList<ImportAlias> Names) : Statement;

/// <summary><c>from ... import ...</c>.</summary>
/// <param name="Module">The module imported from.</param>
/// <param name="Names">The imported names and their aliases; a single <c>*</c> means everything.</param>
public sealed record ImportFrom(string Module, IReadOnlyList<ImportAlias> Names) : Statement;

/// <summary>One imported name.</summary>
/// <param name="Name">The name in the module.</param>
/// <param name="Alias">The local name, when renamed with <c>as</c>.</param>
public readonly record struct ImportAlias(string Name, string? Alias);
