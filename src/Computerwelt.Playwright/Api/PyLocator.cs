using System.Text.Json;
using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Playwright.Interop;
using Microsoft.Playwright;

namespace Computerwelt.Playwright.Api;

// Playwright is deprecating members that the Python API this port mirrors still exposes —
// `type()`, `is_visible(timeout=…)`, `FrameLocator.first`. Dropping them would make a script
// upstream accepts fail here, so they are used deliberately and the obsolete diagnostic is
// turned off for this file rather than for the project.
#pragma warning disable CS0612


/// <summary>
/// A <c>Locator</c>: a description of an element, resolved afresh on every use.
/// </summary>
/// <remarks>
/// This is the half of the API worth writing scripts against, and the reason is the same
/// here as upstream: a locator is a query, not a handle, so it survives the page re-rendering
/// under it and it waits for what it describes to appear. Everything on it is chainable and
/// nothing on it touches the browser until a method that must.
/// </remarks>
internal sealed class PyLocator : PlaywrightObject
{
    public PyLocator(Bridge bridge, ILocator locator) : base(bridge) => Locator = locator;

    /// <summary>The driver object, for chaining and for the assertions.</summary>
    public ILocator Locator { get; }

    public override string TypeName => "Locator";

    public override string Repr() => $"<Locator {Locator}>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        // Properties upstream, so values here.
        "first" => new PyLocator(Bridge, Locator.First),
        "last" => new PyLocator(Bridge, Locator.Last),
        "content_frame" => new PyFrameLocator(Bridge, Locator.ContentFrame),
        "description" => Values.FromOptional(Locator.Description),

        "nth" => Method("nth", arguments =>
        {
            var index = arguments.RequiredInteger(0, "index");
            arguments.Done(1);
            return new PyLocator(Bridge, Locator.Nth(index));
        }),

        "count" => Method("count", arguments =>
        {
            arguments.Done(0);
            return PyInt.From(Bridge.Block(() => Locator.CountAsync()));
        }),

        "all" => Method("all", arguments =>
        {
            arguments.Done(0);

            var all = Bridge.Block(() => Locator.AllAsync());
            return new PyList([.. all.Select(item => new PyLocator(Bridge, item))]);
        }),

        "locator" => Method("locator", arguments =>
        {
            var selector = arguments.Value(0, "selector_or_locator")
                ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: 'selector_or_locator'"));
            var filter = Locators.FilterOptions(arguments);
            arguments.Done(1);

            var options = new LocatorLocatorOptions
            {
                Has = filter.Has,
                HasNot = filter.HasNot,
                HasTextString = filter.HasText,
                HasNotTextString = filter.HasNotText,
            };

            return new PyLocator(Bridge, selector switch
            {
                PyStr text => Locator.Locator(text.Value, options),
                PyLocator inner => Locator.Locator(inner.Locator, options),
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"{arguments.Name}: 'selector_or_locator' must be a str or a Locator, not '{selector.TypeName}'")),
            });
        }),

        "filter" => Method("filter", arguments =>
        {
            var filter = Locators.FilterOptions(arguments, withVisible: true);
            arguments.Done(0);

            return new PyLocator(Bridge, Locator.Filter(new LocatorFilterOptions
            {
                Has = filter.Has,
                HasNot = filter.HasNot,
                HasTextString = filter.HasText,
                HasNotTextString = filter.HasNotText,
                Visible = filter.Visible,
            }));
        }),

        "or_" => Method("or_", arguments => Combine(arguments, Locator.Or)),
        "and_" => Method("and_", arguments => Combine(arguments, Locator.And)),

        "describe" => Method("describe", arguments =>
        {
            var description = arguments.String(0, "description");
            arguments.Done(1);
            return new PyLocator(Bridge, Locator.Describe(description));
        }),

        "get_by_role" or "get_by_text" or "get_by_label" or "get_by_placeholder"
            or "get_by_alt_text" or "get_by_title" or "get_by_test_id" =>
            Method(name, arguments => new PyLocator(Bridge, Locators.GetBy(name, arguments, Locator))),

        "frame_locator" => Method("frame_locator", arguments =>
        {
            var selector = arguments.String(0, "selector");
            arguments.Done(1);
            return new PyFrameLocator(Bridge, Locator.FrameLocator(selector));
        }),

        "click" => Action("click", arguments => Click(arguments, clicks: 1)),
        "dblclick" => Action("dblclick", arguments => Click(arguments, clicks: 2)),

        "fill" => Action("fill", arguments =>
        {
            var value = arguments.String(0, "value");
            var options = new LocatorFillOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
            };

            arguments.Done(1);
            Bridge.Block(() => Locator.FillAsync(value, options));
        }),

        "clear" => Action("clear", arguments =>
        {
            var options = new LocatorClearOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
            };

            arguments.Done(0);
            Bridge.Block(() => Locator.ClearAsync(options));
        }),

        "type" => Action("type", arguments =>
        {
            var text = arguments.String(0, "text");
            var options = new LocatorTypeOptions
            {
                Delay = (float?)arguments.Number("delay"),
                Timeout = Bridge.Timeout(arguments.Timeout()),
            };

            arguments.Done(1);
            Bridge.Block(() => Locator.TypeAsync(text, options));
        }),

        "press_sequentially" => Action("press_sequentially", arguments =>
        {
            var text = arguments.String(0, "text");
            var options = new LocatorPressSequentiallyOptions
            {
                Delay = (float?)arguments.Number("delay"),
                Timeout = Bridge.Timeout(arguments.Timeout()),
            };

            arguments.Done(1);
            Bridge.Block(() => Locator.PressSequentiallyAsync(text, options));
        }),

        "press" => Action("press", arguments =>
        {
            var key = arguments.String(0, "key");
            var options = new LocatorPressOptions
            {
                Delay = (float?)arguments.Number("delay"),
                Timeout = Bridge.Timeout(arguments.Timeout()),
            };

            arguments.Done(1);
            Bridge.Block(() => Locator.PressAsync(key, options));
        }),

        "check" => Action("check", arguments =>
        {
            var options = new LocatorCheckOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Trial = arguments.Bool("trial"),
                Position = Locators.Position(arguments),
            };

            arguments.Done(0);
            Bridge.Block(() => Locator.CheckAsync(options));
        }),

        "uncheck" => Action("uncheck", arguments =>
        {
            var options = new LocatorUncheckOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Trial = arguments.Bool("trial"),
                Position = Locators.Position(arguments),
            };

            arguments.Done(0);
            Bridge.Block(() => Locator.UncheckAsync(options));
        }),

        "set_checked" => Action("set_checked", arguments =>
        {
            var checkedState = arguments.RequiredBool(0, "checked");
            var options = new LocatorSetCheckedOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Trial = arguments.Bool("trial"),
                Position = Locators.Position(arguments),
            };

            arguments.Done(1);
            Bridge.Block(() => Locator.SetCheckedAsync(checkedState, options));
        }),

        "hover" => Action("hover", arguments =>
        {
            var options = new LocatorHoverOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Trial = arguments.Bool("trial"),
                Position = Locators.Position(arguments),
                Modifiers = Locators.Modifiers(arguments),
            };

            arguments.Done(0);
            Bridge.Block(() => Locator.HoverAsync(options));
        }),

        "focus" => Action("focus", arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(0);
            Bridge.Block(() => Locator.FocusAsync(new LocatorFocusOptions { Timeout = timeout }));
        }),

        "blur" => Action("blur", arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(0);
            Bridge.Block(() => Locator.BlurAsync(new LocatorBlurOptions { Timeout = timeout }));
        }),

        "scroll_into_view_if_needed" => Action("scroll_into_view_if_needed", arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(0);

            Bridge.Block(() => Locator.ScrollIntoViewIfNeededAsync(
                new LocatorScrollIntoViewIfNeededOptions { Timeout = timeout }));
        }),

        "select_text" => Action("select_text", arguments =>
        {
            var options = new LocatorSelectTextOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
            };

            arguments.Done(0);
            Bridge.Block(() => Locator.SelectTextAsync(options));
        }),

        "text_content" => Text(name, timeout => Locator.TextContentAsync(
            new LocatorTextContentOptions { Timeout = timeout })),

        "inner_text" => Text(name, timeout => Locator.InnerTextAsync(
            new LocatorInnerTextOptions { Timeout = timeout })!),

        "inner_html" => Text(name, timeout => Locator.InnerHTMLAsync(
            new LocatorInnerHTMLOptions { Timeout = timeout })!),

        "input_value" => Text(name, timeout => Locator.InputValueAsync(
            new LocatorInputValueOptions { Timeout = timeout })!),

        "aria_snapshot" => Text(name, timeout => Locator.AriaSnapshotAsync(
            new LocatorAriaSnapshotOptions { Timeout = timeout })!),

        "get_attribute" => Method("get_attribute", arguments =>
        {
            var attribute = arguments.String(0, "name");
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(1);

            return Values.FromOptional(Bridge.Block(() => Locator.GetAttributeAsync(
                attribute, new LocatorGetAttributeOptions { Timeout = timeout })));
        }),

        "all_text_contents" => Method("all_text_contents", arguments =>
        {
            arguments.Done(0);
            return Values.FromStrings(Bridge.Block(() => Locator.AllTextContentsAsync()));
        }),

        "all_inner_texts" => Method("all_inner_texts", arguments =>
        {
            arguments.Done(0);
            return Values.FromStrings(Bridge.Block(() => Locator.AllInnerTextsAsync()));
        }),

        "is_visible" => Predicate(name, timeout => Locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = timeout })),
        "is_hidden" => Predicate(name, timeout => Locator.IsHiddenAsync(new LocatorIsHiddenOptions { Timeout = timeout })),
        "is_enabled" => Predicate(name, timeout => Locator.IsEnabledAsync(new LocatorIsEnabledOptions { Timeout = timeout })),
        "is_disabled" => Predicate(name, timeout => Locator.IsDisabledAsync(new LocatorIsDisabledOptions { Timeout = timeout })),
        "is_checked" => Predicate(name, timeout => Locator.IsCheckedAsync(new LocatorIsCheckedOptions { Timeout = timeout })),
        "is_editable" => Predicate(name, timeout => Locator.IsEditableAsync(new LocatorIsEditableOptions { Timeout = timeout })),

        "wait_for" => Action("wait_for", arguments =>
        {
            var options = new LocatorWaitForOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                State = Enums.Parse<WaitForSelectorState>(arguments.String("state"), arguments.Name, "state"),
            };

            arguments.Done(0);
            Bridge.Block(() => Locator.WaitForAsync(options));
        }),

        "evaluate" => Method("evaluate", arguments =>
        {
            var expression = arguments.String(0, "expression");
            var argument = Values.ToScript(arguments.Value(1, "arg"), arguments.Name);
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(2);

            return Values.FromScript(Bridge.Block(() => Locator.EvaluateAsync<JsonElement?>(
                expression, argument, new LocatorEvaluateOptions { Timeout = timeout })));
        }),

        "evaluate_all" => Method("evaluate_all", arguments =>
        {
            var expression = arguments.String(0, "expression");
            var argument = Values.ToScript(arguments.Value(1, "arg"), arguments.Name);
            arguments.Done(2);

            return Values.FromScript(Bridge.Block(() => Locator.EvaluateAllAsync<JsonElement?>(expression, argument)));
        }),

        "bounding_box" => Method("bounding_box", arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(0);

            var box = Bridge.Block(() => Locator.BoundingBoxAsync(new LocatorBoundingBoxOptions { Timeout = timeout }));

            if (box is null)
            {
                return PyNone.Instance;
            }

            var result = new PyDict();
            result.Set(new PyStr("x"), new PyFloat(box.X));
            result.Set(new PyStr("y"), new PyFloat(box.Y));
            result.Set(new PyStr("width"), new PyFloat(box.Width));
            result.Set(new PyStr("height"), new PyFloat(box.Height));
            return result;
        }),

        "screenshot" => Method("screenshot", arguments =>
        {
            var path = arguments.String("path");
            var options = new LocatorScreenshotOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                OmitBackground = arguments.Bool("omit_background"),
                Quality = arguments.Integer("quality"),
                Scale = Enums.Parse<ScreenshotScale>(arguments.String("scale"), arguments.Name, "scale"),
                Caret = Enums.Parse<ScreenshotCaret>(arguments.String("caret"), arguments.Name, "caret"),
                Animations = Enums.Parse<ScreenshotAnimations>(arguments.String("animations"), arguments.Name, "animations"),
                Type = Enums.Parse<ScreenshotType>(arguments.String("type"), arguments.Name, "type"),
                Style = arguments.String("style"),
            };

            arguments.Done(0);

            var bytes = Bridge.Block(() => Locator.ScreenshotAsync(options));

            if (path is not null)
            {
                Bridge.Write(path, bytes);
            }

            return new PyBytes(bytes);
        }),

        "select_option" => Method("select_option", arguments =>
        {
            var values = arguments.Strings(0, "value");
            var options = new LocatorSelectOptionOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
            };

            var chosen = Locators.SelectValues(arguments, values);
            arguments.Done(1);

            return Values.FromStrings(Bridge.Block(() => Locator.SelectOptionAsync(chosen, options)));
        }),

        "set_input_files" => Action("set_input_files", arguments =>
        {
            var files = Locators.Payloads(Bridge, arguments, 0);
            var options = new LocatorSetInputFilesOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
            };

            arguments.Done(1);
            Bridge.Block(() => Locator.SetInputFilesAsync(files, options));
        }),

        "drag_to" => Action("drag_to", arguments =>
        {
            var target = arguments.Value(0, "target") is PyLocator locator
                ? locator.Locator
                : throw new PyRaise(PyErrors.TypeError($"{arguments.Name}: 'target' must be a Locator"));

            var options = new LocatorDragToOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Trial = arguments.Bool("trial"),
                SourcePosition = Locators.Source(arguments),
                TargetPosition = Locators.Target(arguments),
            };

            arguments.Done(1);
            Bridge.Block(() => Locator.DragToAsync(target, options));
        }),

        "dispatch_event" => Action("dispatch_event", arguments =>
        {
            var type = arguments.String(0, "type");
            var init = Values.ToScript(arguments.Value(1, "event_init"), arguments.Name);
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(2);

            Bridge.Block(() => Locator.DispatchEventAsync(
                type, init, new LocatorDispatchEventOptions { Timeout = timeout }));
        }),

        "element_handle" => Method("element_handle", arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(0);

            return new PyElementHandle(Bridge, Bridge.Block(() => Locator.ElementHandleAsync(
                new LocatorElementHandleOptions { Timeout = timeout })));
        }),

        "element_handles" => Method("element_handles", arguments =>
        {
            arguments.Done(0);

            var handles = Bridge.Block(() => Locator.ElementHandlesAsync());
            return new PyList([.. handles.Select(handle => new PyElementHandle(Bridge, handle))]);
        }),

        "highlight" or "hide_highlight" => Unsupported(
            name, "highlighting is for a person watching a headed browser"),

        "page" => throw new PyRaise(PyErrors.AttributeError(
            TypeName, "page: a locator does not carry its page back in this port")),

        _ => null,
    };

    private PyObject Combine(Arguments arguments, Func<ILocator, ILocator> combine)
    {
        var other = arguments.Value(0, "locator") is PyLocator locator
            ? locator.Locator
            : throw new PyRaise(PyErrors.TypeError($"{arguments.Name}: 'locator' must be a Locator"));

        arguments.Done(1);
        return new PyLocator(Bridge, combine(other));
    }

    private void Click(Arguments arguments, int clicks)
    {
        var timeout = Bridge.Timeout(arguments.Timeout());
        var button = Enums.Parse<MouseButton>(arguments.String("button"), arguments.Name, "button");
        var delay = (float?)arguments.Number("delay");
        var force = arguments.Bool("force");
        // Deprecated to a no-op in the driver: read so it stays an accepted
        // keyword, and not forwarded, because forwarding it is now an error.
        arguments.Bool("no_wait_after");
        var trial = arguments.Bool("trial");
        var position = Locators.Position(arguments);
        var modifiers = Locators.Modifiers(arguments);
        var count = arguments.Integer("click_count");
        arguments.Done(0);

        if (clicks == 2)
        {
            Bridge.Block(() => Locator.DblClickAsync(new LocatorDblClickOptions
            {
                Timeout = timeout,
                Button = button,
                Delay = delay,
                Force = force,
                Trial = trial,
                Position = position,
                Modifiers = modifiers,
            }));

            return;
        }

        Bridge.Block(() => Locator.ClickAsync(new LocatorClickOptions
        {
            Timeout = timeout,
            Button = button,
            Delay = delay,
            ClickCount = count,
            Force = force,
            Trial = trial,
            Position = position,
            Modifiers = modifiers,
        }));
    }

    private PyObject Text(string name, Func<float?, Task<string?>> work) =>
        Method(name, arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(0);

            return Values.FromOptional(Bridge.Block(() => work(timeout)));
        });

    private PyObject Predicate(string name, Func<float?, Task<bool>> work) =>
        Method(name, arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(0);

            return PyBool.Of(Bridge.Block(() => work(timeout)));
        });
}

/// <summary>A <c>FrameLocator</c>: the way into an <c>iframe</c>.</summary>
internal sealed class PyFrameLocator : PlaywrightObject
{
    private readonly IFrameLocator _frame;

    public PyFrameLocator(Bridge bridge, IFrameLocator frame) : base(bridge) => _frame = frame;

    public override string TypeName => "FrameLocator";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "first" => new PyFrameLocator(Bridge, _frame.First),
        "last" => new PyFrameLocator(Bridge, _frame.Last),
        "owner" => new PyLocator(Bridge, _frame.Owner),

        "nth" => Method("nth", arguments =>
        {
            var index = arguments.RequiredInteger(0, "index");
            arguments.Done(1);
            return new PyFrameLocator(Bridge, _frame.Nth(index));
        }),

        "locator" => Method("locator", arguments =>
        {
            var selector = arguments.String(0, "selector_or_locator");
            var filter = Locators.FilterOptions(arguments);
            arguments.Done(1);

            return new PyLocator(Bridge, _frame.Locator(selector, new FrameLocatorLocatorOptions
            {
                Has = filter.Has,
                HasNot = filter.HasNot,
                HasTextString = filter.HasText,
                HasNotTextString = filter.HasNotText,
            }));
        }),

        "frame_locator" => Method("frame_locator", arguments =>
        {
            var selector = arguments.String(0, "selector");
            arguments.Done(1);
            return new PyFrameLocator(Bridge, _frame.FrameLocator(selector));
        }),

        "get_by_role" or "get_by_text" or "get_by_label" or "get_by_placeholder"
            or "get_by_alt_text" or "get_by_title" or "get_by_test_id" =>
            Method(name, arguments => new PyLocator(Bridge, Locators.GetBy(name, arguments, _frame))),

        _ => null,
    };
}

/// <summary>
/// An <c>ElementHandle</c>: a reference to one element as it was when it was found.
/// </summary>
/// <remarks>
/// Kept small on purpose. Upstream marks handles as discouraged for the same reason they are
/// small here: a handle goes stale the moment the page re-renders, and a locator does not.
/// What is here is what <c>query_selector</c> is worth having for — reading a match rather
/// than driving it.
/// </remarks>
internal sealed class PyElementHandle : PlaywrightObject
{
    private readonly IElementHandle _handle;

    public PyElementHandle(Bridge bridge, IElementHandle handle) : base(bridge) => _handle = handle;

    public override string TypeName => "ElementHandle";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "text_content" => Method("text_content", arguments =>
        {
            arguments.Done(0);
            return Values.FromOptional(Bridge.Block(() => _handle.TextContentAsync()));
        }),

        "inner_text" => Method("inner_text", arguments =>
        {
            arguments.Done(0);
            return new PyStr(Bridge.Block(() => _handle.InnerTextAsync()));
        }),

        "inner_html" => Method("inner_html", arguments =>
        {
            arguments.Done(0);
            return new PyStr(Bridge.Block(() => _handle.InnerHTMLAsync()));
        }),

        "input_value" => Method("input_value", arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(0);

            return new PyStr(Bridge.Block(() => _handle.InputValueAsync(
                new ElementHandleInputValueOptions { Timeout = timeout })));
        }),

        "get_attribute" => Method("get_attribute", arguments =>
        {
            var attribute = arguments.String(0, "name");
            arguments.Done(1);

            return Values.FromOptional(Bridge.Block(() => _handle.GetAttributeAsync(attribute)));
        }),

        "is_visible" => Method("is_visible", arguments =>
        {
            arguments.Done(0);
            return PyBool.Of(Bridge.Block(() => _handle.IsVisibleAsync()));
        }),

        "is_enabled" => Method("is_enabled", arguments =>
        {
            arguments.Done(0);
            return PyBool.Of(Bridge.Block(() => _handle.IsEnabledAsync()));
        }),

        "is_checked" => Method("is_checked", arguments =>
        {
            arguments.Done(0);
            return PyBool.Of(Bridge.Block(() => _handle.IsCheckedAsync()));
        }),

        "click" => Action("click", arguments =>
        {
            var options = new ElementHandleClickOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Button = Enums.Parse<MouseButton>(arguments.String("button"), arguments.Name, "button"),
                Delay = (float?)arguments.Number("delay"),
                ClickCount = arguments.Integer("click_count"),
                Force = arguments.Bool("force"),
                Trial = arguments.Bool("trial"),
                Position = Locators.Position(arguments),
                Modifiers = Locators.Modifiers(arguments),
            };

            arguments.Done(0);
            Bridge.Block(() => _handle.ClickAsync(options));
        }),

        "fill" => Action("fill", arguments =>
        {
            var value = arguments.String(0, "value");
            var options = new ElementHandleFillOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
            };

            arguments.Done(1);
            Bridge.Block(() => _handle.FillAsync(value, options));
        }),

        "query_selector" => Method("query_selector", arguments =>
        {
            var selector = arguments.String(0, "selector");
            arguments.Done(1);

            var found = Bridge.Block(() => _handle.QuerySelectorAsync(selector));
            return found is null ? PyNone.Instance : new PyElementHandle(Bridge, found);
        }),

        "query_selector_all" => Method("query_selector_all", arguments =>
        {
            var selector = arguments.String(0, "selector");
            arguments.Done(1);

            var found = Bridge.Block(() => _handle.QuerySelectorAllAsync(selector));
            return new PyList([.. found.Select(item => new PyElementHandle(Bridge, item))]);
        }),

        "evaluate" => Method("evaluate", arguments =>
        {
            var expression = arguments.String(0, "expression");
            var argument = Values.ToScript(arguments.Value(1, "arg"), arguments.Name);
            arguments.Done(2);

            return Values.FromScript(Bridge.Block(() => _handle.EvaluateAsync<JsonElement?>(expression, argument)));
        }),

        "json_value" => Method("json_value", arguments =>
        {
            arguments.Done(0);
            return Values.FromScript(Bridge.Block(() => _handle.JsonValueAsync<JsonElement?>()));
        }),

        "dispose" => Action("dispose", arguments =>
        {
            arguments.Done(0);
            Bridge.Block(() => _handle.DisposeAsync().AsTask());
        }),

        _ => null,
    };
}
