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
    internal readonly record struct Filter(ILocator? Has, ILocator? HasNot, string? HasText, string? HasNotText, bool? Visible);

    /// <summary>Reads the four narrowing keywords every locator-producing call accepts.</summary>
    public static Filter FilterOptions(Arguments arguments, bool withVisible = false) => new(
        Locator(arguments.Keyword("has"), arguments.Name, "has"),
        Locator(arguments.Keyword("has_not"), arguments.Name, "has_not"),
        arguments.String("has_text"),
        arguments.String("has_not_text"),
        withVisible ? arguments.Bool("visible") : null);

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
        string Text,
        AriaRole Role,
        bool? Exact,
        bool? Checked,
        bool? Disabled,
        bool? Expanded,
        bool? IncludeHidden,
        int? Level,
        bool? Pressed,
        bool? Selected);

    /// <summary>Reads a <c>get_by_*</c> call's arguments.</summary>
    public static GetByRequest Parse(string name, Arguments arguments)
    {
        if (name == "get_by_role")
        {
            var role = arguments.String(0, "role");
            var parsed = new GetByRequest(
                name,
                arguments.String("name") ?? string.Empty,
                Enums.Parse<AriaRole>(role, arguments.Name, "role")
                ?? throw new PyRaise(PyErrors.ValueError($"{arguments.Name}: 'role' is required")),
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

        var text = arguments.String(0, name == "get_by_test_id" ? "test_id" : "text");
        var exact = name == "get_by_test_id" ? null : arguments.Bool("exact");

        arguments.Done(1);
        return new GetByRequest(name, text, AriaRole.Generic, exact, null, null, null, null, null, null, null);
    }

    /// <summary>Applies a parsed <c>get_by_*</c> to a page.</summary>
    public static ILocator GetBy(string name, Arguments arguments, IPage page)
    {
        var request = Parse(name, arguments);

        return name switch
        {
            "get_by_role" => page.GetByRole(request.Role, new PageGetByRoleOptions
            {
                NameString = request.Text.Length > 0 ? request.Text : null,
                Exact = request.Exact,
                Checked = request.Checked,
                Disabled = request.Disabled,
                Expanded = request.Expanded,
                IncludeHidden = request.IncludeHidden,
                Level = request.Level,
                Pressed = request.Pressed,
                Selected = request.Selected,
            }),
            "get_by_text" => page.GetByText(request.Text, new PageGetByTextOptions { Exact = request.Exact }),
            "get_by_label" => page.GetByLabel(request.Text, new PageGetByLabelOptions { Exact = request.Exact }),
            "get_by_placeholder" => page.GetByPlaceholder(request.Text, new PageGetByPlaceholderOptions { Exact = request.Exact }),
            "get_by_alt_text" => page.GetByAltText(request.Text, new PageGetByAltTextOptions { Exact = request.Exact }),
            "get_by_title" => page.GetByTitle(request.Text, new PageGetByTitleOptions { Exact = request.Exact }),
            _ => page.GetByTestId(request.Text),
        };
    }

    /// <summary>Applies a parsed <c>get_by_*</c> to a locator.</summary>
    public static ILocator GetBy(string name, Arguments arguments, ILocator locator)
    {
        var request = Parse(name, arguments);

        return name switch
        {
            "get_by_role" => locator.GetByRole(request.Role, new LocatorGetByRoleOptions
            {
                NameString = request.Text.Length > 0 ? request.Text : null,
                Exact = request.Exact,
                Checked = request.Checked,
                Disabled = request.Disabled,
                Expanded = request.Expanded,
                IncludeHidden = request.IncludeHidden,
                Level = request.Level,
                Pressed = request.Pressed,
                Selected = request.Selected,
            }),
            "get_by_text" => locator.GetByText(request.Text, new LocatorGetByTextOptions { Exact = request.Exact }),
            "get_by_label" => locator.GetByLabel(request.Text, new LocatorGetByLabelOptions { Exact = request.Exact }),
            "get_by_placeholder" => locator.GetByPlaceholder(request.Text, new LocatorGetByPlaceholderOptions { Exact = request.Exact }),
            "get_by_alt_text" => locator.GetByAltText(request.Text, new LocatorGetByAltTextOptions { Exact = request.Exact }),
            "get_by_title" => locator.GetByTitle(request.Text, new LocatorGetByTitleOptions { Exact = request.Exact }),
            _ => locator.GetByTestId(request.Text),
        };
    }

    /// <summary>Applies a parsed <c>get_by_*</c> to a frame locator.</summary>
    public static ILocator GetBy(string name, Arguments arguments, IFrameLocator frame)
    {
        var request = Parse(name, arguments);

        return name switch
        {
            "get_by_role" => frame.GetByRole(request.Role, new FrameLocatorGetByRoleOptions
            {
                NameString = request.Text.Length > 0 ? request.Text : null,
                Exact = request.Exact,
                Checked = request.Checked,
                Disabled = request.Disabled,
                Expanded = request.Expanded,
                IncludeHidden = request.IncludeHidden,
                Level = request.Level,
                Pressed = request.Pressed,
                Selected = request.Selected,
            }),
            "get_by_text" => frame.GetByText(request.Text, new FrameLocatorGetByTextOptions { Exact = request.Exact }),
            "get_by_label" => frame.GetByLabel(request.Text, new FrameLocatorGetByLabelOptions { Exact = request.Exact }),
            "get_by_placeholder" => frame.GetByPlaceholder(request.Text, new FrameLocatorGetByPlaceholderOptions { Exact = request.Exact }),
            "get_by_alt_text" => frame.GetByAltText(request.Text, new FrameLocatorGetByAltTextOptions { Exact = request.Exact }),
            "get_by_title" => frame.GetByTitle(request.Text, new FrameLocatorGetByTitleOptions { Exact = request.Exact }),
            _ => frame.GetByTestId(request.Text),
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
