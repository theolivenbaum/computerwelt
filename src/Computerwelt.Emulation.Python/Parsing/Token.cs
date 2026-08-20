namespace Computerwelt.Emulation.Python.Parsing;

/// <summary>The lexical categories of Python source.</summary>
public enum TokenKind
{
    /// <summary>End of input.</summary>
    EndOfInput,

    /// <summary>A logical line break.</summary>
    Newline,

    /// <summary>An increase in indentation, opening a block.</summary>
    Indent,

    /// <summary>A decrease in indentation, closing a block.</summary>
    Dedent,

    /// <summary>An identifier.</summary>
    Name,

    /// <summary>A reserved word.</summary>
    Keyword,

    /// <summary>An integer literal.</summary>
    Integer,

    /// <summary>A floating-point literal.</summary>
    Float,

    /// <summary>A string literal, already decoded.</summary>
    String,

    /// <summary>A bytes literal, already decoded.</summary>
    Bytes,

    /// <summary>An f-string literal, kept raw for the parser to expand.</summary>
    FString,

    /// <summary>An operator or delimiter.</summary>
    Operator,
}

/// <summary>One token with the source range it covers.</summary>
/// <param name="Kind">The token's category.</param>
/// <param name="Text">The token's text; for literals, the decoded value.</param>
/// <param name="Start">Offset of the first character.</param>
/// <param name="Length">Length in source characters.</param>
/// <param name="Line">1-based line number, used for tracebacks.</param>
/// <param name="Column">0-based column of the first character.</param>
public readonly record struct Token(
    TokenKind Kind,
    string Text,
    int Start,
    int Length,
    int Line,
    int Column)
{
    /// <summary>The string prefix that introduced a literal, such as <c>r</c> or <c>rb</c>.</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>True when this token is the operator or keyword <paramref name="text"/>.</summary>
    public bool Is(string text) =>
        Kind is TokenKind.Operator or TokenKind.Keyword && string.Equals(Text, text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => $"{Kind}({Text}) @{Line}:{Column}";
}
