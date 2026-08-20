namespace Computerwelt.Emulation.Bash.Parsing;

/// <summary>The lexical categories the shell grammar distinguishes.</summary>
public enum TokenKind
{
    /// <summary>End of input.</summary>
    EndOfInput,

    /// <summary>
    /// A word, carried as its raw source text with quotes intact.
    /// <see cref="WordParser"/> turns it into a structured <see cref="Word"/>; keeping the
    /// quotes until then is what preserves the split/glob distinction.
    /// </summary>
    Word,

    /// <summary>An IO number immediately preceding a redirection operator, e.g. the <c>2</c> in <c>2&gt;</c>.</summary>
    IoNumber,

    /// <summary>A line break, which terminates a command like <c>;</c>.</summary>
    Newline,

    /// <summary><c>;</c></summary>
    Semicolon,

    /// <summary><c>;;</c> — end of a case branch.</summary>
    DoubleSemicolon,

    /// <summary><c>;&amp;</c> — fall through to the next case branch.</summary>
    SemiAmpersand,

    /// <summary><c>;;&amp;</c> — keep testing subsequent case patterns.</summary>
    DoubleSemiAmpersand,

    /// <summary><c>|</c></summary>
    Pipe,

    /// <summary><c>|&amp;</c> — pipe stdout and stderr.</summary>
    PipeAmpersand,

    /// <summary><c>&amp;&amp;</c></summary>
    AndIf,

    /// <summary><c>||</c></summary>
    OrIf,

    /// <summary><c>&amp;</c> — run in background.</summary>
    Ampersand,

    /// <summary><c>&gt;</c></summary>
    Great,

    /// <summary><c>&gt;&gt;</c></summary>
    DoubleGreat,

    /// <summary><c>&lt;</c></summary>
    Less,

    /// <summary><c>&lt;&lt;</c> — here-document.</summary>
    DoubleLess,

    /// <summary><c>&lt;&lt;-</c> — here-document with leading tabs stripped.</summary>
    DoubleLessDash,

    /// <summary><c>&lt;&lt;&lt;</c> — here-string.</summary>
    TripleLess,

    /// <summary><c>&lt;&gt;</c> — open for reading and writing.</summary>
    LessGreat,

    /// <summary><c>&gt;&amp;</c> — duplicate or redirect an output descriptor.</summary>
    GreatAmpersand,

    /// <summary><c>&lt;&amp;</c> — duplicate or redirect an input descriptor.</summary>
    LessAmpersand,

    /// <summary><c>&gt;|</c> — clobber even under <c>set -o noclobber</c>.</summary>
    Clobber,

    /// <summary><c>&amp;&gt;</c> — redirect both stdout and stderr.</summary>
    AmpersandGreat,

    /// <summary><c>&amp;&gt;&gt;</c> — append both stdout and stderr.</summary>
    AmpersandDoubleGreat,

    /// <summary><c>(</c></summary>
    LeftParen,

    /// <summary><c>)</c></summary>
    RightParen,

    /// <summary><c>{</c> as a reserved word.</summary>
    LeftBrace,

    /// <summary><c>}</c> as a reserved word.</summary>
    RightBrace,

    /// <summary><c>[[</c></summary>
    DoubleLeftBracket,

    /// <summary><c>]]</c></summary>
    DoubleRightBracket,

    /// <summary><c>((</c> — arithmetic evaluation.</summary>
    DoubleLeftParen,

    /// <summary>A reserved word such as <c>if</c> or <c>while</c>.</summary>
    Keyword,
}

/// <summary>
/// One lexical token with the source range it covers.
/// </summary>
/// <param name="Kind">The token's category.</param>
/// <param name="Text">The token's text. For words this is the raw text with quotes removed at the outer level only.</param>
/// <param name="Start">Byte offset of the token's first character.</param>
/// <param name="Length">Length of the token in the source.</param>
public readonly record struct Token(TokenKind Kind, string Text, int Start, int Length)
{
    /// <summary>The source span this token covers.</summary>
    public Span Span => new(Start, Length);

    /// <summary>True when this token can begin or continue a command word.</summary>
    public bool IsWordLike => Kind is TokenKind.Word or TokenKind.IoNumber;

    /// <summary>True when this token terminates a command.</summary>
    public bool IsCommandTerminator => Kind is TokenKind.Newline or TokenKind.Semicolon
        or TokenKind.Ampersand or TokenKind.EndOfInput;

    /// <summary>True when this token starts a redirection.</summary>
    public bool IsRedirectOperator => Kind is TokenKind.Great or TokenKind.DoubleGreat
        or TokenKind.Less or TokenKind.DoubleLess or TokenKind.DoubleLessDash
        or TokenKind.TripleLess or TokenKind.LessGreat or TokenKind.GreatAmpersand
        or TokenKind.LessAmpersand or TokenKind.Clobber or TokenKind.AmpersandGreat
        or TokenKind.AmpersandDoubleGreat;

    /// <inheritdoc />
    public override string ToString() => $"{Kind}({Text})";
}

/// <summary>A half-open range of source text.</summary>
/// <param name="Start">Byte offset of the first character.</param>
/// <param name="Length">Number of characters covered.</param>
public readonly record struct Span(int Start, int Length)
{
    /// <summary>Offset one past the last character.</summary>
    public int End => Start + Length;

    /// <summary>An empty span at the origin.</summary>
    public static Span Empty => default;

    /// <summary>The smallest span covering both operands.</summary>
    public Span Union(Span other)
    {
        var start = Math.Min(Start, other.Start);
        return new Span(start, Math.Max(End, other.End) - start);
    }
}
