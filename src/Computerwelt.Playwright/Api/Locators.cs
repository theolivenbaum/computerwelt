using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Playwright.Interop;
using Microsoft.Playwright;

namespace Computerwelt.Playwright.Api;

/// <summary>
/// The argument shapes the page, the locator and the frame locator share.
/// </summary>
/// <remarks>
/// Playwright's .NET API gives each owner its own options class — <c>PageGetByRoleOptions</c>
/// and <c>LocatorGetByRoleOptions</c> are separate types with identical members — so the
/// parsing is factored out here and only the last line, the call itself, is written three
/// times. Parsing is where the behaviour lives; the three tails are transcription.
/// </remarks>
internal static class Locators
{
    /// <summary>What <c>locator()</c> and <c>filter()</c> narrow by.</summary>
    internal readonly record struct Filter(
        ILocator? Has,
        ILocator? HasNot,
        Matcher? HasText,
        Matcher? HasNotText,
        bool? Visible);

    /// <summary>Reads the four narrowing keywords every locator-producing call accepts.</summary>
    public static Filter FilterOptions(Arguments arguments, bool withVisible = false) => new(
        Locator(arguments.Keyword("has"), arguments.Name, "has"),
        Locator(arguments.Keyword("has_not"), arguments.Name, "has_not"),
        arguments.Matcher("has_text"),
        arguments.Matcher("has_not_text"),
        withVisible ? arguments.Bool("visible") : null);

    /// <summary>The string half of a <c>has_text=</c>, or null when a pattern was given.</summary>
    private static string? AsText(Matcher? matcher) => matcher is { IsPattern: false } m ? m.Text : null;

    /// <summary>The pattern half of a <c>has_text=</c>, or null when a string was given.</summary>
    private static System.Text.RegularExpressions.Regex? AsPattern(Matcher? matcher) =>
        matcher is { IsPattern: true } m ? m.Pattern : null;

    /// <summary>
    /// The same filter, in the options type each owner takes.
    /// </summary>
    /// <remarks>
    /// Four types with identical members and no common base, which is why this is written
    /// out four times rather than once. Everything with behaviour in it — reading the
    /// arguments, telling a string from a pattern — happened above.
    /// </remarks>
    public static PageLocatorOptions ForPage(Filter filter) => new()
    {
        Has = filter.Has,
        HasNot = filter.HasNot,
        HasTextString = AsText(filter.HasText),
        HasTextRegex = AsPattern(filter.HasText),
        HasNotTextString = AsText(filter.HasNotText),
        HasNotTextRegex = AsPattern(filter.HasNotText),
    };

    /// <inheritdoc cref="ForPage"/>
    public static LocatorLocatorOptions ForLocator(Filter filter) => new()
    {
        Has = filter.Has,
        HasNot = filter.HasNot,
        HasTextString = AsText(filter.HasText),
        HasTextRegex = AsPattern(filter.HasText),
        HasNotTextString = AsText(filter.HasNotText),
        HasNotTextRegex = AsPattern(filter.HasNotText),
    };

    /// <inheritdoc cref="ForPage"/>
    public static FrameLocatorLocatorOptions ForFrame(Filter filter) => new()
    {
        Has = filter.Has,
        HasNot = filter.HasNot,
        HasTextString = AsText(filter.HasText),
        HasTextRegex = AsPattern(filter.HasText),
        HasNotTextString = AsText(filter.HasNotText),
        HasNotTextRegex = AsPattern(filter.HasNotText),
    };

    /// <inheritdoc cref="ForPage"/>
    public static LocatorFilterOptions ForFilter(Filter filter) => new()
    {
        Has = filter.Has,
        HasNot = filter.HasNot,
        HasTextString = AsText(filter.HasText),
        HasTextRegex = AsPattern(filter.HasText),
        HasNotTextString = AsText(filter.HasNotText),
        HasNotTextRegex = AsPattern(filter.HasNotText),
        Visible = filter.Visible,
    };

    /// <summary>An optional <c>position=</c>, as a driver point.</summary>
    public static Position? Position(Arguments arguments, string name = "position") =>
        arguments.Point(name) is { } point ? new Position { X = point.X, Y = point.Y } : null;

    /// <summary>An optional <c>source_position=</c>, which the driver types separately.</summary>
    public static SourcePosition? Source(Arguments arguments) =>
        arguments.Point("source_position") is { } point
            ? new SourcePosition { X = point.X, Y = point.Y }
            : null;

    /// <summary>An optional <c>target_position=</c>, which the driver types separately.</summary>
    public static TargetPosition? Target(Arguments arguments) =>
        arguments.Point("target_position") is { } point
            ? new TargetPosition { X = point.X, Y = point.Y }
            : null;

    /// <summary>An optional <c>modifiers=</c>, as driver enumeration values.</summary>
    public static IEnumerable<KeyboardModifier>? Modifiers(Arguments arguments)
    {
        if (arguments.Strings("modifiers") is not { } names)
        {
            return null;
        }

        return [.. names.Select(name =>
            Enums.Parse<KeyboardModifier>(name, arguments.Name, "modifiers")
            ?? throw new PyRaise(PyErrors.ValueError($"{arguments.Name}: 'modifiers' has an empty entry")))];
    }

    /// <summary>A <c>clip=</c> rectangle for a screenshot.</summary>
    public static Clip Clip(PyObject value, string where)
    {
        if (value is not PyDict dictionary)
        {
            throw new PyRaise(PyErrors.TypeError($"{where}: 'clip' must be a dict, not '{value.TypeName}'"));
        }

        float Side(string key)
        {
            if (!dictionary.TryGetValue(new PyStr(key), out var item))
            {
                throw new PyRaise(PyErrors.TypeError($"{where}: 'clip' is missing '{key}'"));
            }

            return item switch
            {
                PyInt integer => (float)integer.Value,
                PyFloat number => (float)number.Value,
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"{where}: 'clip[{key}]' must be a number, not '{item.TypeName}'")),
            };
        }

        return new Clip { X = Side("x"), Y = Side("y"), Width = Side("width"), Height = Side("height") };
    }

    /// <summary>
    /// What <c>select_option</c> was asked to select.
    /// </summary>
    /// <remarks>
    /// Upstream lets a caller pick by value, by label or by index, and a script written
    /// against it uses whichever suits the markup. All three end up as one list of
    /// <see cref="SelectOptionValue"/>, which is the shape the driver takes.
    /// </remarks>
    public static IEnumerable<SelectOptionValue> SelectValues(Arguments arguments, IReadOnlyList<string>? values)
    {
        var chosen = new List<SelectOptionValue>();

        foreach (var value in values ?? [])
        {
            chosen.Add(new SelectOptionValue { Value = value });
        }

        foreach (var label in arguments.Strings("label") ?? [])
        {
            chosen.Add(new SelectOptionValue { Label = label });
        }

        if (arguments.Keyword("index") is { } index)
        {
            foreach (var item in index is PyInt single ? [single] : index.Iterate() ?? [])
            {
                if (item is not PyInt number)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"{arguments.Name}: 'index' must be an int or a sequence of int"));
                }

                chosen.Add(new SelectOptionValue { Index = number.ToIndex() });
            }
        }

        if (arguments.Keyword("element") is not null)
        {
            throw Errors.Fail(
                "select_option(): 'element' is not supported; select by value, label or index");
        }

        if (chosen.Count == 0)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{arguments.Name}() needs a value, a 'label' or an 'index' to select"));
        }

        return chosen;
    }

    /// <summary>
    /// Turns a <c>files=</c> argument into payloads read from the sandbox's filesystem.
    /// </summary>
    /// <remarks>
    /// Upstream accepts either a path or a <c>{'name', 'mimeType', 'buffer'}</c> dict, and
    /// both are accepted here — but a path is resolved against the <i>sandbox's</i>
    /// filesystem and sent as bytes. The browser is never handed a path, so there is nothing
    /// for a traversal to reach.
    /// </remarks>
    public static IEnumerable<FilePayload> Payloads(Bridge bridge, Arguments arguments, int index)
    {
        var value = arguments.Value(index, "files")
            ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: 'files'"));

        var items = value is PyStr or PyDict
            ? [value]
            : value.Iterate()?.ToList()
              ?? throw new PyRaise(PyErrors.TypeError(
                  $"{arguments.Name}: 'files' must be a path, a dict or a sequence of them"));

        return [.. items.Select(item => Payload(bridge, item, arguments.Name))];
    }

    private static FilePayload Payload(Bridge bridge, PyObject item, string where)
    {
        switch (item)
        {
            case PyStr path:
            {
                var bytes = bridge.Read(path.Value);
                var name = path.Value.Split('/')[^1];

                return new FilePayload
                {
                    Name = name,
                    MimeType = MimeType(name),
                    Buffer = bytes,
                };
            }

            case PyDict dictionary:
            {
                string Field(string key) =>
                    dictionary.TryGetValue(new PyStr(key), out var field) && field is PyStr text
                        ? text.Value
                        : throw new PyRaise(PyErrors.ValueError($"{where}: a file dict needs a '{key}'"));

                var buffer = dictionary.TryGetValue(new PyStr("buffer"), out var raw) && raw is PyBytes bytes
                    ? bytes.Value
                    : throw new PyRaise(PyErrors.TypeError($"{where}: a file dict needs 'buffer' as bytes"));

                return new FilePayload
                {
                    Name = Field("name"),
                    MimeType = Field("mimeType"),
                    Buffer = buffer,
                };
            }

            default:
                throw new PyRaise(PyErrors.TypeError(
                    $"{where}: a file must be a path or a dict, not '{item.TypeName}'"));
        }
    }

    /// <summary>
    /// Guesses a content type from a file name.
    /// </summary>
    /// <remarks>
    /// The browser needs one and the sandbox's filesystem does not store it, so the
    /// extension is all there is to go on. Anything unrecognised is
    /// <c>application/octet-stream</c>, which is what an upload with no better information
    /// should say.
    /// </remarks>
    private static string MimeType(string name)
    {
        var dot = name.LastIndexOf('.');
        var extension = dot >= 0 ? name[(dot + 1)..].ToLowerInvariant() : string.Empty;

        return extension switch
        {
            "txt" or "log" or "md" => "text/plain",
            "csv" => "text/csv",
            "html" or "htm" => "text/html",
            "css" => "text/css",
            "js" => "text/javascript",
            "json" => "application/json",
            "xml" => "application/xml",
            "pdf" => "application/pdf",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "svg" => "image/svg+xml",
            "webp" => "image/webp",
            "zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }

    /// <summary>The parsed form of a <c>get_by_*</c> call, before it reaches an owner.</summary>
    internal readonly record struct GetByRequest(
        string Kind,
        Matcher Text,
        AriaRole Role,
        Matcher? Name,
        bool? Exact,
        bool? Checked,
        bool? Disabled,
        bool? Expanded,
        bool? IncludeHidden,
        int? Level,
        bool? Pressed,
        bool? Selected);

    /// <summary>
    /// Reads a <c>get_by_*</c> call's arguments.
    /// </summary>
    /// <remarks>
    /// The text a locator is built from is <c>str | Pattern</c> upstream and the two mean
    /// different things — a string matches loosely and by substring, a pattern matches as
    /// written — so both are read here and the owner picks the overload.
    /// </remarks>
    public static GetByRequest Parse(string name, Arguments arguments)
    {
        if (name == "get_by_role")
        {
            var role = arguments.String(0, "role");
            var parsed = new GetByRequest(
                name,
                default,
                Enums.Parse<AriaRole>(role, arguments.Name, "role")
                ?? throw new PyRaise(PyErrors.ValueError($"{arguments.Name}: 'role' is required")),
                arguments.Matcher("name"),
                arguments.Bool("exact"),
                arguments.Bool("checked"),
                arguments.Bool("disabled"),
                arguments.Bool("expanded"),
                arguments.Bool("include_hidden"),
                arguments.Integer("level"),
                arguments.Bool("pressed"),
                arguments.Bool("selected"));

            arguments.Done(1);
            return parsed;
        }

        var label = name == "get_by_test_id" ? "test_id" : "text";
        var text = arguments.Matcher(0, label)
            ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: '{label}'"));

        // `get_by_test_id` has no `exact=` upstream: a test id is compared whole either way.
        var exact = name == "get_by_test_id" ? null : arguments.Bool("exact");

        arguments.Done(1);
        return new GetByRequest(name, text, AriaRole.Generic, null, exact, null, null, null, null, null, null, null);
    }

    /// <summary>Applies a parsed <c>get_by_*</c> to a page.</summary>
    public static ILocator GetBy(string name, Arguments arguments, IPage page)
    {
        var request = Parse(name, arguments);

        return name switch
        {
            "get_by_role" => page.GetByRole(request.Role, new PageGetByRoleOptions
            {
                NameString = request.Name is { IsPattern: false } named ? named.Text : null,
                NameRegex = request.Name is { IsPattern: true } pattern ? pattern.Pattern : null,
                Exact = request.Exact,
                Checked = request.Checked,
                Disabled = request.Disabled,
                Expanded = request.Expanded,
                IncludeHidden = request.IncludeHidden,
                Level = request.Level,
                Pressed = request.Pressed,
                Selected = request.Selected,
            }),

            "get_by_text" => request.Text.IsPattern
                ? page.GetByText(request.Text.Pattern!, new PageGetByTextOptions { Exact = request.Exact })
                : page.GetByText(request.Text.Text!, new PageGetByTextOptions { Exact = request.Exact }),

            "get_by_label" => request.Text.IsPattern
                ? page.GetByLabel(request.Text.Pattern!, new PageGetByLabelOptions { Exact = request.Exact })
                : page.GetByLabel(request.Text.Text!, new PageGetByLabelOptions { Exact = request.Exact }),

            "get_by_placeholder" => request.Text.IsPattern
                ? page.GetByPlaceholder(request.Text.Pattern!, new PageGetByPlaceholderOptions { Exact = request.Exact })
                : page.GetByPlaceholder(request.Text.Text!, new PageGetByPlaceholderOptions { Exact = request.Exact }),

            "get_by_alt_text" => request.Text.IsPattern
                ? page.GetByAltText(request.Text.Pattern!, new PageGetByAltTextOptions { Exact = request.Exact })
                : page.GetByAltText(request.Text.Text!, new PageGetByAltTextOptions { Exact = request.Exact }),

            "get_by_title" => request.Text.IsPattern
                ? page.GetByTitle(request.Text.Pattern!, new PageGetByTitleOptions { Exact = request.Exact })
                : page.GetByTitle(request.Text.Text!, new PageGetByTitleOptions { Exact = request.Exact }),

            _ => request.Text.IsPattern
                ? page.GetByTestId(request.Text.Pattern!)
                : page.GetByTestId(request.Text.Text!),
        };
    }

    /// <inheritdoc cref="GetBy(string, Arguments, IPage)"/>
    public static ILocator GetBy(string name, Arguments arguments, ILocator locator)
    {
        var request = Parse(name, arguments);

        return name switch
        {
            "get_by_role" => locator.GetByRole(request.Role, new LocatorGetByRoleOptions
            {
                NameString = request.Name is { IsPattern: false } named ? named.Text : null,
                NameRegex = request.Name is { IsPattern: true } pattern ? pattern.Pattern : null,
                Exact = request.Exact,
                Checked = request.Checked,
                Disabled = request.Disabled,
                Expanded = request.Expanded,
                IncludeHidden = request.IncludeHidden,
                Level = request.Level,
                Pressed = request.Pressed,
                Selected = request.Selected,
            }),

            "get_by_text" => request.Text.IsPattern
                ? locator.GetByText(request.Text.Pattern!, new LocatorGetByTextOptions { Exact = request.Exact })
                : locator.GetByText(request.Text.Text!, new LocatorGetByTextOptions { Exact = request.Exact }),

            "get_by_label" => request.Text.IsPattern
                ? locator.GetByLabel(request.Text.Pattern!, new LocatorGetByLabelOptions { Exact = request.Exact })
                : locator.GetByLabel(request.Text.Text!, new LocatorGetByLabelOptions { Exact = request.Exact }),

            "get_by_placeholder" => request.Text.IsPattern
                ? locator.GetByPlaceholder(request.Text.Pattern!, new LocatorGetByPlaceholderOptions { Exact = request.Exact })
                : locator.GetByPlaceholder(request.Text.Text!, new LocatorGetByPlaceholderOptions { Exact = request.Exact }),

            "get_by_alt_text" => request.Text.IsPattern
                ? locator.GetByAltText(request.Text.Pattern!, new LocatorGetByAltTextOptions { Exact = request.Exact })
                : locator.GetByAltText(request.Text.Text!, new LocatorGetByAltTextOptions { Exact = request.Exact }),

            "get_by_title" => request.Text.IsPattern
                ? locator.GetByTitle(request.Text.Pattern!, new LocatorGetByTitleOptions { Exact = request.Exact })
                : locator.GetByTitle(request.Text.Text!, new LocatorGetByTitleOptions { Exact = request.Exact }),

            _ => request.Text.IsPattern
                ? locator.GetByTestId(request.Text.Pattern!)
                : locator.GetByTestId(request.Text.Text!),
        };
    }

    /// <inheritdoc cref="GetBy(string, Arguments, IPage)"/>
    public static ILocator GetBy(string name, Arguments arguments, IFrameLocator frame)
    {
        var request = Parse(name, arguments);

        return name switch
        {
            "get_by_role" => frame.GetByRole(request.Role, new FrameLocatorGetByRoleOptions
            {
                NameString = request.Name is { IsPattern: false } named ? named.Text : null,
                NameRegex = request.Name is { IsPattern: true } pattern ? pattern.Pattern : null,
                Exact = request.Exact,
                Checked = request.Checked,
                Disabled = request.Disabled,
                Expanded = request.Expanded,
                IncludeHidden = request.IncludeHidden,
                Level = request.Level,
                Pressed = request.Pressed,
                Selected = request.Selected,
            }),

            "get_by_text" => request.Text.IsPattern
                ? frame.GetByText(request.Text.Pattern!, new FrameLocatorGetByTextOptions { Exact = request.Exact })
                : frame.GetByText(request.Text.Text!, new FrameLocatorGetByTextOptions { Exact = request.Exact }),

            "get_by_label" => request.Text.IsPattern
                ? frame.GetByLabel(request.Text.Pattern!, new FrameLocatorGetByLabelOptions { Exact = request.Exact })
                : frame.GetByLabel(request.Text.Text!, new FrameLocatorGetByLabelOptions { Exact = request.Exact }),

            "get_by_placeholder" => request.Text.IsPattern
                ? frame.GetByPlaceholder(request.Text.Pattern!, new FrameLocatorGetByPlaceholderOptions { Exact = request.Exact })
                : frame.GetByPlaceholder(request.Text.Text!, new FrameLocatorGetByPlaceholderOptions { Exact = request.Exact }),

            "get_by_alt_text" => request.Text.IsPattern
                ? frame.GetByAltText(request.Text.Pattern!, new FrameLocatorGetByAltTextOptions { Exact = request.Exact })
                : frame.GetByAltText(request.Text.Text!, new FrameLocatorGetByAltTextOptions { Exact = request.Exact }),

            "get_by_title" => request.Text.IsPattern
                ? frame.GetByTitle(request.Text.Pattern!, new FrameLocatorGetByTitleOptions { Exact = request.Exact })
                : frame.GetByTitle(request.Text.Text!, new FrameLocatorGetByTitleOptions { Exact = request.Exact }),

            _ => request.Text.IsPattern
                ? frame.GetByTestId(request.Text.Pattern!)
                : frame.GetByTestId(request.Text.Text!),
        };
    }

    private static ILocator? Locator(PyObject? value, string where, string name) => value switch
    {
        null => null,
        PyLocator locator => locator.Locator,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"{where}: '{name}' must be a Locator, not '{value.TypeName}'")),
    };
}
