namespace Monty.Parsing;

/// <summary>Python's reserved words and soft keywords.</summary>
public static class Keywords
{
    /// <summary>Words that can never be used as identifiers.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await",
        "break", "class", "continue", "def", "del", "elif", "else", "except",
        "finally", "for", "from", "global", "if", "import", "in", "is",
        "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try",
        "while", "with", "yield",
    };

    /// <summary>True when <paramref name="text"/> is a reserved word.</summary>
    public static bool IsReserved(string text) => Reserved.Contains(text);
}
