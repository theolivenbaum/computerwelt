using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Modules;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// A value the upstream API types as <c>str | Pattern</c>.
/// </summary>
/// <remarks>
/// <para>
/// A great many arguments take either — <c>get_by_text</c>, <c>filter(has_text=…)</c>,
/// <c>to_have_text</c>, <c>to_have_url</c> — and they mean different things: a string is
/// matched loosely, case-insensitively and by substring, while a pattern is matched as
/// written. Accepting only the string form would silently turn one into the other, so both
/// are read and the driver is given whichever it was handed.
/// </para>
/// <para>
/// A pattern arrives as the object <c>re.compile</c> returned, and what is taken from it is
/// its compiled .NET form — the translation from Python's regular-expression syntax has
/// already been done there, once, by the module that owns it.
/// </para>
/// </remarks>
internal readonly record struct Matcher
{
    private Matcher(string? text, Regex? pattern)
    {
        Text = text;
        Pattern = pattern;
    }

    /// <summary>The string form, or null when a pattern was given.</summary>
    public string? Text { get; }

    /// <summary>The pattern form, or null when a string was given.</summary>
    public Regex? Pattern { get; }

    /// <summary>True when the caller passed a compiled pattern.</summary>
    public bool IsPattern => Pattern is not null;

    /// <summary>Reads a <c>str | Pattern</c>, or null when the argument was not given.</summary>
    public static Matcher? From(PyObject? value, string where, string name) => value switch
    {
        null => null,
        PyStr text => new Matcher(text.Value, null),
        ReModule.PyPattern pattern => new Matcher(null, pattern.Compiled),

        // `re.compile` is the only way to make one, so naming it is the useful half of the
        // message: a script that passed a bare string by mistake already works.
        _ => throw new PyRaise(PyErrors.TypeError(
            $"{where}: '{name}' must be a str or a compiled pattern from re.compile(), "
            + $"not '{value.TypeName}'")),
    };

    /// <summary>
    /// Reads a <c>str | Pattern | Sequence[str] | Sequence[Pattern]</c>, as the assertions
    /// that can compare against a whole list of elements take.
    /// </summary>
    /// <remarks>
    /// The driver has an overload for a sequence of strings and one for a sequence of
    /// patterns, and none for a sequence mixing them — so a mixed sequence is refused by
    /// name rather than being flattened into whichever kind came first.
    /// </remarks>
    public static MatcherList Many(PyObject? value, string where, string name)
    {
        if (value is null)
        {
            throw new PyRaise(PyErrors.TypeError($"{where}() missing required argument: '{name}'"));
        }

        if (value is PyStr or ReModule.PyPattern)
        {
            return new MatcherList([From(value, where, name)!.Value], IsSequence: false);
        }

        var items = value.Iterate() ?? throw new PyRaise(PyErrors.TypeError(
            $"{where}: '{name}' must be a str, a compiled pattern, or a sequence of either, "
            + $"not '{value.TypeName}'"));

        var matchers = items.Select(item => From(item, where, name)!.Value).ToList();

        if (matchers.Any(static m => m.IsPattern) && matchers.Any(static m => !m.IsPattern))
        {
            throw Errors.Fail(
                $"{where}: '{name}' must be all strings or all patterns; the driver has no "
                + "way to express a sequence that mixes them.");
        }

        return new MatcherList(matchers, IsSequence: true);
    }
}

/// <summary>
/// What a <c>str | Pattern | Sequence</c> argument turned out to be.
/// </summary>
/// <remarks>
/// Whether it was a sequence is kept, and it is not pedantry: <c>to_have_text('a')</c>
/// asserts about the element a locator resolves to, while <c>to_have_text(['a'])</c> asserts
/// that the locator resolves to exactly one element with that text. Collapsing a
/// one-element list into the scalar form would quietly drop the count.
/// </remarks>
internal readonly record struct MatcherList(IReadOnlyList<Matcher> Items, bool IsSequence)
{
    /// <summary>The one value, when the caller passed a scalar.</summary>
    public Matcher Single => Items[0];

    /// <summary>Whether the values are patterns. False for an empty sequence.</summary>
    public bool ArePatterns => Items.Count > 0 && Items[0].IsPattern;

    /// <summary>The strings, for the overload that takes them.</summary>
    public IEnumerable<string> Strings => Items.Select(static m => m.Text!);

    /// <summary>The patterns, for the overload that takes them.</summary>
    public IEnumerable<Regex> Patterns => Items.Select(static m => m.Pattern!);
}
