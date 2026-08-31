using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Playwright.Interop;
using Microsoft.Playwright;

namespace Computerwelt.Playwright.Api;

/// <summary>
/// <c>expect()</c>: the web-first assertions, and the reason a test written against a live
/// page is not a race.
/// </summary>
/// <remarks>
/// <para>
/// Each of these retries until it holds or the timeout expires, which is the whole
/// difference between <c>expect(locator).to_have_text('Done')</c> and reading the text once
/// and comparing it. A failure arrives as an ordinary <c>AssertionError</c>, so
/// <c>assert</c>-shaped scripts and these two read the same way in a traceback.
/// </para>
/// <para>
/// Upstream's <c>expect.set_options(timeout=…)</c> is deliberately absent: the driver holds
/// that default in a process-wide static, so a script setting it would be changing another
/// tenant's assertions. The per-call <c>timeout=</c> does the same job for one caller, and
/// the host's ceiling still applies to it.
/// </para>
/// </remarks>
internal static class PyExpect
{
    /// <summary>The callable a program is handed as <c>expect</c>.</summary>
    public static PyObject Callable(Bridge bridge) =>
        new PyBuiltinFunction("expect", (positional, keywords) =>
        {
            var arguments = new Arguments("expect", positional, keywords);
            var subject = arguments.Value(0, "actual")
                ?? throw new PyRaise(PyErrors.TypeError("expect() missing required argument: 'actual'"));

            // Upstream takes a message for the failure and ignores it unless the assertion
            // fails; the driver has no place to put one, so it is accepted and dropped
            // rather than becoming an unexpected-keyword error.
            arguments.String("message");
            arguments.Done(1);

            return subject switch
            {
                PyLocator locator => new PyAssertions(bridge, Assertions.Expect(locator.Locator)),
                PyPage page => new PyAssertions(bridge, Assertions.Expect(page.Page)),
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"expect() takes a Locator or a Page, not '{subject.TypeName}'")),
            };
        });
}

/// <summary>
/// The assertions themselves, for a locator or for a page.
/// </summary>
/// <remarks>
/// One class covers both because the negated form is a prefix rather than a separate object:
/// every <c>to_*</c> has a <c>not_to_*</c>, and reading the prefix off the name here is what
/// keeps that from being written twice.
/// </remarks>
internal sealed class PyAssertions : PlaywrightObject
{
    private readonly ILocatorAssertions? _locator;
    private readonly IPageAssertions? _page;

    public PyAssertions(Bridge bridge, ILocatorAssertions assertions) : base(bridge) => _locator = assertions;

    public PyAssertions(Bridge bridge, IPageAssertions assertions) : base(bridge) => _page = assertions;

    public override string TypeName => _locator is not null ? "LocatorAssertions" : "PageAssertions";

    public override PyObject? GetAttribute(string name)
    {
        var negated = name.StartsWith("not_", StringComparison.Ordinal);
        var assertion = negated ? name[4..] : name;

        if (!assertion.StartsWith("to_", StringComparison.Ordinal))
        {
            return null;
        }

        return _locator is not null
            ? LocatorAssertion(name, assertion, negated ? _locator.Not : _locator)
            : PageAssertion(name, assertion, negated ? _page!.Not : _page!);
    }

    private PyObject? LocatorAssertion(string name, string assertion, ILocatorAssertions target) => assertion switch
    {
        "to_be_visible" => Assert(name, (arguments, timeout) =>
        {
            var visible = arguments.Bool("visible");
            arguments.Done(0);
            return target.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = timeout, Visible = visible });
        }),

        "to_be_hidden" => Assert(name, (arguments, timeout) =>
        {
            arguments.Done(0);
            return target.ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = timeout });
        }),

        "to_be_attached" => Assert(name, (arguments, timeout) =>
        {
            var attached = arguments.Bool("attached");
            arguments.Done(0);
            return target.ToBeAttachedAsync(new LocatorAssertionsToBeAttachedOptions { Timeout = timeout, Attached = attached });
        }),

        "to_be_checked" => Assert(name, (arguments, timeout) =>
        {
            var state = arguments.Bool("checked");
            arguments.Done(0);
            return target.ToBeCheckedAsync(new LocatorAssertionsToBeCheckedOptions { Timeout = timeout, Checked = state });
        }),

        "to_be_enabled" => Assert(name, (arguments, timeout) =>
        {
            var enabled = arguments.Bool("enabled");
            arguments.Done(0);
            return target.ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = timeout, Enabled = enabled });
        }),

        "to_be_disabled" => Assert(name, (arguments, timeout) =>
        {
            arguments.Done(0);
            return target.ToBeDisabledAsync(new LocatorAssertionsToBeDisabledOptions { Timeout = timeout });
        }),

        "to_be_editable" => Assert(name, (arguments, timeout) =>
        {
            var editable = arguments.Bool("editable");
            arguments.Done(0);
            return target.ToBeEditableAsync(new LocatorAssertionsToBeEditableOptions { Timeout = timeout, Editable = editable });
        }),

        "to_be_empty" => Assert(name, (arguments, timeout) =>
        {
            arguments.Done(0);
            return target.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = timeout });
        }),

        "to_be_focused" => Assert(name, (arguments, timeout) =>
        {
            arguments.Done(0);
            return target.ToBeFocusedAsync(new LocatorAssertionsToBeFocusedOptions { Timeout = timeout });
        }),

        "to_be_in_viewport" => Assert(name, (arguments, timeout) =>
        {
            var ratio = (float?)arguments.Number("ratio");
            arguments.Done(0);
            return target.ToBeInViewportAsync(new LocatorAssertionsToBeInViewportOptions { Timeout = timeout, Ratio = ratio });
        }),

        "to_have_count" => Assert(name, (arguments, timeout) =>
        {
            var count = arguments.RequiredInteger(0, "count");
            arguments.Done(1);
            return target.ToHaveCountAsync(count, new LocatorAssertionsToHaveCountOptions { Timeout = timeout });
        }),

        "to_have_text" => Assert(name, (arguments, timeout) =>
        {
            var expected = arguments.Matchers(0, "expected");
            var options = new LocatorAssertionsToHaveTextOptions
            {
                Timeout = timeout,
                IgnoreCase = arguments.Bool("ignore_case"),
                UseInnerText = arguments.Bool("use_inner_text"),
            };

            arguments.Done(1);

            // A scalar asserts about the element the locator resolves to; a sequence
            // asserts about the whole list, including how many there are. A one-element
            // list is therefore still the sequence form.
            return (expected.IsSequence, expected.ArePatterns) switch
            {
                (false, true) => target.ToHaveTextAsync(expected.Single.Pattern!, options),
                (false, false) => target.ToHaveTextAsync(expected.Single.Text!, options),
                (true, true) => target.ToHaveTextAsync(expected.Patterns, options),
                (true, false) => target.ToHaveTextAsync(expected.Strings, options),
            };
        }),

        "to_contain_text" => Assert(name, (arguments, timeout) =>
        {
            var expected = arguments.Matchers(0, "expected");
            var options = new LocatorAssertionsToContainTextOptions
            {
                Timeout = timeout,
                IgnoreCase = arguments.Bool("ignore_case"),
                UseInnerText = arguments.Bool("use_inner_text"),
            };

            arguments.Done(1);

            // A scalar asserts about the element the locator resolves to; a sequence
            // asserts about the whole list, including how many there are. A one-element
            // list is therefore still the sequence form.
            return (expected.IsSequence, expected.ArePatterns) switch
            {
                (false, true) => target.ToContainTextAsync(expected.Single.Pattern!, options),
                (false, false) => target.ToContainTextAsync(expected.Single.Text!, options),
                (true, true) => target.ToContainTextAsync(expected.Patterns, options),
                (true, false) => target.ToContainTextAsync(expected.Strings, options),
            };
        }),

        "to_have_value" => Assert(name, (arguments, timeout) =>
        {
            var value = Required(arguments, 0, "value");
            arguments.Done(1);

            var options = new LocatorAssertionsToHaveValueOptions { Timeout = timeout };

            return value.IsPattern
                ? target.ToHaveValueAsync(value.Pattern!, options)
                : target.ToHaveValueAsync(value.Text!, options);
        }),

        "to_have_values" => Assert(name, (arguments, timeout) =>
        {
            var values = arguments.Matchers(0, "values");
            arguments.Done(1);

            var options = new LocatorAssertionsToHaveValuesOptions { Timeout = timeout };

            // Always the sequence form: `to_have_values` compares against a `<select>`'s
            // selected options, which is a list even when it holds one.
            return values.ArePatterns
                ? target.ToHaveValuesAsync(values.Patterns, options)
                : target.ToHaveValuesAsync(values.Strings, options);
        }),

        "to_have_attribute" => Assert(name, (arguments, timeout) =>
        {
            var attribute = arguments.String(0, "name");
            var value = arguments.Matcher(1, "value");
            var options = new LocatorAssertionsToHaveAttributeOptions
            {
                Timeout = timeout,
                IgnoreCase = arguments.Bool("ignore_case"),
            };

            arguments.Done(2);

            // Upstream's value is optional, and leaving it out asserts that the attribute is
            // *present* whatever it holds. The driver has no overload for that, so it is
            // expressed as a pattern that matches any value — an attribute that is absent
            // has no value to match, so it still fails.
            return value switch
            {
                null => target.ToHaveAttributeAsync(attribute, AnyValue, options),
                { IsPattern: true } pattern => target.ToHaveAttributeAsync(attribute, pattern.Pattern!, options),
                { } text => target.ToHaveAttributeAsync(attribute, text.Text!, options),
            };
        }),

        "to_have_class" => Assert(name, (arguments, timeout) =>
        {
            var expected = arguments.Matchers(0, "expected");
            var options = new LocatorAssertionsToHaveClassOptions { Timeout = timeout };
            arguments.Done(1);

            return (expected.IsSequence, expected.ArePatterns) switch
            {
                (false, true) => target.ToHaveClassAsync(expected.Single.Pattern!, options),
                (false, false) => target.ToHaveClassAsync(expected.Single.Text!, options),
                (true, true) => target.ToHaveClassAsync(expected.Patterns, options),
                (true, false) => target.ToHaveClassAsync(expected.Strings, options),
            };
        }),

        // Strings only, and that is upstream's signature rather than a gap here: a class
        // list is compared token by token, which a pattern has nothing to say about.
        "to_contain_class" => Assert(name, (arguments, timeout) =>
        {
            var expected = Expected(arguments, "expected");
            var options = new LocatorAssertionsToContainClassOptions { Timeout = timeout };
            arguments.Done(1);

            return expected.Count == 1
                ? target.ToContainClassAsync(expected[0], options)
                : target.ToContainClassAsync(expected, options);
        }),

        "to_have_css" => Assert(name, (arguments, timeout) =>
        {
            var property = arguments.String(0, "name");
            var value = Required(arguments, 1, "value");
            arguments.Done(2);

            var options = new LocatorAssertionsToHaveCSSOptions { Timeout = timeout };

            return value.IsPattern
                ? target.ToHaveCSSAsync(property, value.Pattern!, options)
                : target.ToHaveCSSAsync(property, value.Text!, options);
        }),

        "to_have_id" => Assert(name, (arguments, timeout) =>
        {
            var id = Required(arguments, 0, "id");
            arguments.Done(1);

            var options = new LocatorAssertionsToHaveIdOptions { Timeout = timeout };

            return id.IsPattern
                ? target.ToHaveIdAsync(id.Pattern!, options)
                : target.ToHaveIdAsync(id.Text!, options);
        }),

        "to_have_js_property" => Assert(name, (arguments, timeout) =>
        {
            var property = arguments.String(0, "name");
            var value = Values.ToScript(arguments.Value(1, "value"), name);
            arguments.Done(2);

            // `None` is a value a JavaScript property can genuinely hold, so it is passed
            // through rather than treated as "no argument".
            return target.ToHaveJSPropertyAsync(
                property, value!, new LocatorAssertionsToHaveJSPropertyOptions { Timeout = timeout });
        }),

        "to_have_role" => Assert(name, (arguments, timeout) =>
        {
            var role = arguments.String(0, "role");
            arguments.Done(1);

            return target.ToHaveRoleAsync(
                Enums.Parse<AriaRole>(role, name, "role")!.Value,
                new LocatorAssertionsToHaveRoleOptions { Timeout = timeout });
        }),

        "to_have_accessible_name" => Assert(name, (arguments, timeout) =>
        {
            var accessible = Required(arguments, 0, "name");
            var options = new LocatorAssertionsToHaveAccessibleNameOptions
            {
                Timeout = timeout,
                IgnoreCase = arguments.Bool("ignore_case"),
            };

            arguments.Done(1);

            return accessible.IsPattern
                ? target.ToHaveAccessibleNameAsync(accessible.Pattern!, options)
                : target.ToHaveAccessibleNameAsync(accessible.Text!, options);
        }),

        "to_have_accessible_description" => Assert(name, (arguments, timeout) =>
        {
            var description = Required(arguments, 0, "description");
            var options = new LocatorAssertionsToHaveAccessibleDescriptionOptions
            {
                Timeout = timeout,
                IgnoreCase = arguments.Bool("ignore_case"),
            };

            arguments.Done(1);

            return description.IsPattern
                ? target.ToHaveAccessibleDescriptionAsync(description.Pattern!, options)
                : target.ToHaveAccessibleDescriptionAsync(description.Text!, options);
        }),

        "to_match_aria_snapshot" => Assert(name, (arguments, timeout) =>
        {
            var expected = arguments.String(0, "expected");
            arguments.Done(1);

            return target.ToMatchAriaSnapshotAsync(
                expected, new LocatorAssertionsToMatchAriaSnapshotOptions { Timeout = timeout });
        }),

        _ => null,
    };

    private PyObject? PageAssertion(string name, string assertion, IPageAssertions target) => assertion switch
    {
        "to_have_title" => Assert(name, (arguments, timeout) =>
        {
            var title = Required(arguments, 0, "title_or_reg_exp");
            arguments.Done(1);

            var options = new PageAssertionsToHaveTitleOptions { Timeout = timeout };

            return title.IsPattern
                ? target.ToHaveTitleAsync(title.Pattern!, options)
                : target.ToHaveTitleAsync(title.Text!, options);
        }),

        "to_have_url" => Assert(name, (arguments, timeout) =>
        {
            var url = Required(arguments, 0, "url_or_reg_exp");
            var options = new PageAssertionsToHaveURLOptions
            {
                Timeout = timeout,
                IgnoreCase = arguments.Bool("ignore_case"),
            };

            arguments.Done(1);

            return url.IsPattern
                ? target.ToHaveURLAsync(url.Pattern!, options)
                : target.ToHaveURLAsync(url.Text!, options);
        }),

        "to_match_aria_snapshot" => Assert(name, (arguments, timeout) =>
        {
            var expected = arguments.String(0, "expected");
            arguments.Done(1);

            return target.ToMatchAriaSnapshotAsync(
                expected, new PageAssertionsToMatchAriaSnapshotOptions { Timeout = timeout });
        }),

        _ => null,
    };

    /// <summary>
    /// Runs one assertion, reporting a failure the way a Python test expects it.
    /// </summary>
    /// <remarks>
    /// The driver reports both an assertion that did not hold and the timeout it waited out
    /// as the same kind of failure, and so does upstream: both mean the page never reached
    /// the state that was asserted, so both become <c>AssertionError</c> rather than one of
    /// them becoming a <c>TimeoutError</c> a test would have to catch separately.
    /// </remarks>
    private PyObject Assert(string name, Func<Arguments, float?, Task> body) =>
        Action(name, arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());

            try
            {
                Task.Run(() => body(arguments, timeout)).GetAwaiter().GetResult();
            }
            catch (Exception failure) when (failure is PlaywrightException or System.TimeoutException)
            {
                // Both mean the same thing to a test: the page never reached the state that
                // was asserted. Upstream raises AssertionError for either, so a timed-out
                // assertion is not something a test has to catch separately.
                throw new PyRaise(PyErrors.AssertionError(failure.Message.Trim()));
            }
            catch (Exception exception)
            {
                throw Errors.Translate(exception);
            }
        });

    private static IReadOnlyList<string> Expected(Arguments arguments, string name) =>
        arguments.Strings(0, name)
        ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: '{name}'"));

    /// <summary>Matches any attribute value, for <c>to_have_attribute</c> with no value.</summary>
    private static readonly System.Text.RegularExpressions.Regex AnyValue =
        new("[\\s\\S]*", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>A required <c>str | Pattern</c> argument.</summary>
    private static Matcher Required(Arguments arguments, int index, string name) =>
        arguments.Matcher(index, name)
        ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: '{name}'"));
}
