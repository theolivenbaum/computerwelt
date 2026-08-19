using System.Text;

namespace Bashkit.Parsing;

/// <summary>
/// A shell word: a sequence of parts, each either literal text or something to expand.
/// </summary>
/// <remarks>
/// The word is kept as parts rather than as raw text because expansion is not a string
/// rewrite — whether a part was quoted decides whether its result is word-split and
/// globbed. <c>echo "$a"</c> and <c>echo $a</c> differ only in that flag, and flattening to
/// text loses it.
/// </remarks>
public sealed record Word(IReadOnlyList<WordPart> Parts)
{
    /// <summary>An empty word.</summary>
    public static Word Empty { get; } = new([]);

    /// <summary>A word consisting of one literal string.</summary>
    public static Word Literal(string text) => new([new WordPart.Literal(text)]);

    /// <summary>True when every part is literal, so no expansion is needed.</summary>
    public bool IsLiteral => Parts.All(static p => p is WordPart.Literal);

    /// <summary>
    /// The concatenated literal text, valid only when <see cref="IsLiteral"/> is true.
    /// Used for command names and here-document delimiters.
    /// </summary>
    public string LiteralText
    {
        get
        {
            if (Parts.Count == 1 && Parts[0] is WordPart.Literal single)
            {
                return single.Text;
            }

            var builder = new StringBuilder();
            foreach (var part in Parts)
            {
                if (part is WordPart.Literal literal)
                {
                    builder.Append(literal.Text);
                }
            }

            return builder.ToString();
        }
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat(Parts.Select(static p => p.ToString()));
}

/// <summary>One component of a <see cref="Word"/>.</summary>
public abstract record WordPart
{
    /// <summary>
    /// True when this part came from inside quotes, and so must not be word-split or
    /// globbed after expansion.
    /// </summary>
    public bool Quoted { get; init; }

    /// <summary>Literal text with no expansion.</summary>
    /// <param name="Text">The literal characters.</param>
    public sealed record Literal(string Text) : WordPart
    {
        /// <inheritdoc />
        public override string ToString() => Text;
    }

    /// <summary>A parameter expansion such as <c>$name</c> or <c>${name:-default}</c>.</summary>
    /// <param name="Name">The parameter name, or a special parameter such as <c>@</c> or <c>?</c>.</param>
    /// <param name="Operation">The modifier applied, if any.</param>
    /// <param name="Argument">The operator's argument word, if any.</param>
    /// <param name="Index">An array subscript, unparsed, if any.</param>
    /// <param name="IndirectRef">True for <c>${!name}</c> indirection.</param>
    /// <param name="LengthOf">True for <c>${#name}</c>.</param>
    public sealed record Parameter(
        string Name,
        ParameterOp Operation = ParameterOp.None,
        Word? Argument = null,
        string? Index = null,
        bool IndirectRef = false,
        bool LengthOf = false) : WordPart
    {
        /// <summary>For pattern-substitution operators, the replacement word.</summary>
        public Word? Replacement { get; init; }

        /// <inheritdoc />
        public override string ToString() => $"${{{Name}}}";
    }

    /// <summary>A command substitution, <c>$(...)</c> or backticks.</summary>
    /// <param name="Script">The unparsed inner script.</param>
    /// <param name="Backticks">True when written with backticks.</param>
    public sealed record CommandSubstitution(string Script, bool Backticks = false) : WordPart
    {
        /// <inheritdoc />
        public override string ToString() => $"$({Script})";
    }

    /// <summary>An arithmetic expansion, <c>$((...))</c>.</summary>
    /// <param name="Expression">The unparsed arithmetic text.</param>
    public sealed record Arithmetic(string Expression) : WordPart
    {
        /// <inheritdoc />
        public override string ToString() => $"$(({Expression}))";
    }

    /// <summary>A tilde prefix, <c>~</c> or <c>~user</c>.</summary>
    /// <param name="User">The user name, or an empty string for the current user.</param>
    public sealed record Tilde(string User) : WordPart
    {
        /// <inheritdoc />
        public override string ToString() => $"~{User}";
    }

    /// <summary>A process substitution, <c>&lt;(...)</c> or <c>&gt;(...)</c>.</summary>
    /// <param name="Script">The unparsed inner script.</param>
    /// <param name="Output">True for <c>&gt;(...)</c>.</param>
    public sealed record ProcessSubstitution(string Script, bool Output) : WordPart
    {
        /// <inheritdoc />
        public override string ToString() => Output ? $">({Script})" : $"<({Script})";
    }
}

/// <summary>The modifier applied inside <c>${...}</c>.</summary>
public enum ParameterOp
{
    /// <summary>No modifier: plain <c>${name}</c>.</summary>
    None,

    /// <summary>
    /// A <c>${...}</c> whose body names no parameter at all.
    /// </summary>
    /// <remarks>
    /// This is a runtime error rather than a syntax error, because that is where bash
    /// reports it: the script parses, and <c>${%}</c> fails when it is expanded.
    /// </remarks>
    BadSubstitution,

    /// <summary><c>${name:-word}</c> — substitute when unset or empty.</summary>
    UseDefault,

    /// <summary><c>${name-word}</c> — substitute when unset only.</summary>
    UseDefaultUnsetOnly,

    /// <summary><c>${name:=word}</c> — assign a default when unset or empty.</summary>
    AssignDefault,

    /// <summary><c>${name=word}</c> — assign a default when unset only.</summary>
    AssignDefaultUnsetOnly,

    /// <summary><c>${name:?word}</c> — error when unset or empty.</summary>
    ErrorIfUnset,

    /// <summary><c>${name?word}</c> — error when unset only.</summary>
    ErrorIfUnsetOnly,

    /// <summary><c>${name:+word}</c> — substitute when set and non-empty.</summary>
    UseAlternate,

    /// <summary><c>${name+word}</c> — substitute when set, even if empty.</summary>
    UseAlternateSetOnly,

    /// <summary><c>${name#pattern}</c> — strip the shortest leading match.</summary>
    RemoveSmallestPrefix,

    /// <summary><c>${name##pattern}</c> — strip the longest leading match.</summary>
    RemoveLargestPrefix,

    /// <summary><c>${name%pattern}</c> — strip the shortest trailing match.</summary>
    RemoveSmallestSuffix,

    /// <summary><c>${name%%pattern}</c> — strip the longest trailing match.</summary>
    RemoveLargestSuffix,

    /// <summary><c>${name/pattern/replacement}</c> — replace the first match.</summary>
    ReplaceFirst,

    /// <summary><c>${name//pattern/replacement}</c> — replace every match.</summary>
    ReplaceAll,

    /// <summary><c>${name/#pattern/replacement}</c> — replace an anchored prefix.</summary>
    ReplacePrefix,

    /// <summary><c>${name/%pattern/replacement}</c> — replace an anchored suffix.</summary>
    ReplaceSuffix,

    /// <summary><c>${name:offset:length}</c> — substring.</summary>
    Substring,

    /// <summary><c>${name^pattern}</c> — upper-case the first match.</summary>
    UpperFirst,

    /// <summary><c>${name^^pattern}</c> — upper-case every match.</summary>
    UpperAll,

    /// <summary><c>${name,pattern}</c> — lower-case the first match.</summary>
    LowerFirst,

    /// <summary><c>${name,,pattern}</c> — lower-case every match.</summary>
    LowerAll,

    /// <summary><c>${name@op}</c> — the transformation operators Q, E, P, A, a, U, L, u.</summary>
    Transform,
}
