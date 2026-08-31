using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// Turns the string literals the Python API uses into the driver's enumerations.
/// </summary>
/// <remarks>
/// Upstream types these as <c>Literal["load", "domcontentloaded", …]</c>, so a script passes
/// a plain string and a misspelling is a mistake that should be caught here rather than
/// travel to the browser as a value it will ignore. The mapping drops punctuation and
/// ignores case, which is exactly the difference between the two spellings —
/// <c>no-preference</c> and <c>NoPreference</c> — and nothing else.
/// </remarks>
internal static class Enums
{
    /// <summary>Parses a literal, or reports what the accepted ones are.</summary>
    public static T? Parse<T>(string? value, string where, string name)
        where T : struct, Enum
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        if (Enum.TryParse<T>(normalized, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new PyRaise(PyErrors.ValueError(
            $"{where}: '{name}' must be one of {Spelling<T>()}, not '{value}'"));
    }

    private static string Spelling<T>()
        where T : struct, Enum =>
        string.Join(", ", Enum.GetNames<T>().Select(static name => $"'{name.ToLowerInvariant()}'"));
}
