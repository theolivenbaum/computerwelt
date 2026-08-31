using System.Text;
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
/// A <c>Page</c>: one tab, and the object almost every script spends its time on.
/// </summary>
/// <remarks>
/// <para>
/// The selector-taking shorthands (<c>page.click('#go')</c>) and the locator API
/// (<c>page.locator('#go').click()</c>) are both here, because both are what people write.
/// </para>
/// <para>
/// Everything that would touch a file — a screenshot, a PDF, an upload, an init script —
/// goes through the sandbox's filesystem rather than the driver's own path handling. The
/// bytes make the round trip through this process, which costs a copy and buys the property
/// that a script cannot name a path outside the sandbox and have the browser write to it.
/// </para>
/// </remarks>
internal sealed class PyPage : PlaywrightObject
{
    private readonly IPage _page;
    private readonly PyBrowserContext _context;

    public PyPage(Bridge bridge, IPage page, PyBrowserContext context) : base(bridge)
    {
        _page = page;
        _context = context;
    }

    public override string TypeName => "Page";

    public override string Repr() => $"<Page url={_page.Url}>";

    /// <summary>The driver object, for the assertions and the locators built from it.</summary>
    public IPage Page => _page;

    public override PyObject? GetAttribute(string name) => name switch
    {
        // Properties upstream, so values here rather than methods.
        "url" => new PyStr(_page.Url),
        "context" => _context,
        "keyboard" => new PyKeyboard(Bridge, _page.Keyboard),
        "mouse" => new PyMouse(Bridge, _page.Mouse),
        "viewport_size" => Viewport(),
        "frames" or "workers" or "main_frame" => throw new PyRaise(PyErrors.AttributeError(
            TypeName, $"{name}: frames and workers are not modelled in this port; "
            + "use page.frame_locator(selector) to reach into an iframe")),

        "title" => Method("title", arguments =>
        {
            arguments.Done(0);
            return new PyStr(Bridge.Block(() => _page.TitleAsync()));
        }),

        "content" => Method("content", arguments =>
        {
            arguments.Done(0);
            return new PyStr(Bridge.Block(() => _page.ContentAsync()));
        }),

        "goto" => Method("goto", Goto),

        "set_content" => Action("set_content", arguments =>
        {
            var html = arguments.String(0, "html");
            var timeout = Bridge.Timeout(arguments.Timeout());
            var waitUntil = Enums.Parse<WaitUntilState>(arguments.String("wait_until"), arguments.Name, "wait_until");
            arguments.Done(1);

            Bridge.Block(() => _page.SetContentAsync(html, new PageSetContentOptions
            {
                Timeout = timeout,
                WaitUntil = waitUntil,
            }));
        }),

        "reload" => Method("reload", arguments => Navigate(
            arguments,
            (timeout, waitUntil) => _page.ReloadAsync(new PageReloadOptions { Timeout = timeout, WaitUntil = waitUntil }))),

        "go_back" => Method("go_back", arguments => Navigate(
            arguments,
            (timeout, waitUntil) => _page.GoBackAsync(new PageGoBackOptions { Timeout = timeout, WaitUntil = waitUntil }))),

        "go_forward" => Method("go_forward", arguments => Navigate(
            arguments,
            (timeout, waitUntil) => _page.GoForwardAsync(new PageGoForwardOptions { Timeout = timeout, WaitUntil = waitUntil }))),

        "close" => Action("close", arguments =>
        {
            arguments.Bool("run_before_unload");
            arguments.String("reason");
            arguments.Done(0);

            Bridge.Block(() => _page.CloseAsync());
            _context.Release(this);
        }),

        "is_closed" => Method("is_closed", arguments =>
        {
            arguments.Done(0);
            return PyBool.Of(_page.IsClosed);
        }),

        "wait_for_timeout" => Action("wait_for_timeout", arguments =>
        {
            var milliseconds = arguments.Value(0, "timeout") is { } value
                ? value switch
                {
                    PyInt integer => (float)integer.Value,
                    PyFloat number => (float)number.Value,
                    _ => throw new PyRaise(PyErrors.TypeError(
                        $"{arguments.Name}: 'timeout' must be a number, not '{value.TypeName}'")),
                }
                : throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: 'timeout'"));

            arguments.Done(1);

            // Clamped to the host's ceiling: a sleep is the cheapest way to hold a browser
            // context open past the run's budget.
            Bridge.Block(() => _page.WaitForTimeoutAsync(
                Math.Min(milliseconds, Bridge.Options.DefaultTimeout)));
        }),

        "wait_for_load_state" => Action("wait_for_load_state", arguments =>
        {
            var state = Enums.Parse<LoadState>(arguments.OptionalString(0, "state"), arguments.Name, "state");
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(1);

            Bridge.Block(() => _page.WaitForLoadStateAsync(state, new PageWaitForLoadStateOptions { Timeout = timeout }));
        }),

        "wait_for_url" => Action("wait_for_url", arguments =>
        {
            var url = arguments.Matcher(0, "url")
                ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: 'url'"));
            var timeout = Bridge.Timeout(arguments.Timeout());
            var waitUntil = Enums.Parse<WaitUntilState>(arguments.String("wait_until"), arguments.Name, "wait_until");
            arguments.Done(1);

            var options = new PageWaitForURLOptions { Timeout = timeout, WaitUntil = waitUntil };

            Bridge.Block(() => url.IsPattern
                ? _page.WaitForURLAsync(url.Pattern!, options)
                : _page.WaitForURLAsync(url.Text!, options));
        }),

        "wait_for_selector" => Method("wait_for_selector", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var timeout = Bridge.Timeout(arguments.Timeout());
            var state = Enums.Parse<WaitForSelectorState>(arguments.String("state"), arguments.Name, "state");
            var strict = arguments.Bool("strict");
            arguments.Done(1);

            var handle = Bridge.Block(() => _page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
            {
                Timeout = timeout,
                State = state,
                Strict = strict,
            }));

            return handle is null ? PyNone.Instance : new PyElementHandle(Bridge, handle);
        }),

        "query_selector" => Method("query_selector", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var strict = arguments.Bool("strict");
            arguments.Done(1);

            var handle = Bridge.Block(() => _page.QuerySelectorAsync(
                selector, new PageQuerySelectorOptions { Strict = strict }));

            return handle is null ? PyNone.Instance : new PyElementHandle(Bridge, handle);
        }),

        "query_selector_all" => Method("query_selector_all", arguments =>
        {
            var selector = arguments.String(0, "selector");
            arguments.Done(1);

            var handles = Bridge.Block(() => _page.QuerySelectorAllAsync(selector));
            return new PyList([.. handles.Select(handle => new PyElementHandle(Bridge, handle))]);
        }),

        "locator" => Method("locator", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var options = Locators.FilterOptions(arguments);
            arguments.Done(1);

            return new PyLocator(Bridge, _page.Locator(selector, Locators.ForPage(options)));
        }),

        "get_by_role" or "get_by_text" or "get_by_label" or "get_by_placeholder"
            or "get_by_alt_text" or "get_by_title" or "get_by_test_id" =>
            Method(name, arguments => new PyLocator(Bridge, Locators.GetBy(name, arguments, _page))),

        "frame_locator" => Method("frame_locator", arguments =>
        {
            var selector = arguments.String(0, "selector");
            arguments.Done(1);
            return new PyFrameLocator(Bridge, _page.FrameLocator(selector));
        }),

        "click" => Action("click", arguments => Click(arguments, clicks: 1)),
        "dblclick" => Action("dblclick", arguments => Click(arguments, clicks: 2)),

        "fill" => Action("fill", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var value = arguments.String(1, "value");
            var options = new PageFillOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(2);
            Bridge.Block(() => _page.FillAsync(selector, value, options));
        }),

        "type" => Action("type", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var text = arguments.String(1, "text");
            var options = new PageTypeOptions
            {
                Delay = (float?)arguments.Number("delay"),
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(2);
            Bridge.Block(() => _page.TypeAsync(selector, text, options));
        }),

        "press" => Action("press", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var key = arguments.String(1, "key");
            var options = new PagePressOptions
            {
                Delay = (float?)arguments.Number("delay"),
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(2);
            Bridge.Block(() => _page.PressAsync(selector, key, options));
        }),

        "check" => Action("check", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var options = new PageCheckOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Strict = arguments.Bool("strict"),
                Trial = arguments.Bool("trial"),
                Position = Locators.Position(arguments),
            };

            arguments.Done(1);
            Bridge.Block(() => _page.CheckAsync(selector, options));
        }),

        "uncheck" => Action("uncheck", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var options = new PageUncheckOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Strict = arguments.Bool("strict"),
                Trial = arguments.Bool("trial"),
                Position = Locators.Position(arguments),
            };

            arguments.Done(1);
            Bridge.Block(() => _page.UncheckAsync(selector, options));
        }),

        "hover" => Action("hover", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var options = new PageHoverOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Strict = arguments.Bool("strict"),
                Trial = arguments.Bool("trial"),
                Position = Locators.Position(arguments),
                Modifiers = Locators.Modifiers(arguments),
            };

            arguments.Done(1);
            Bridge.Block(() => _page.HoverAsync(selector, options));
        }),

        "focus" => Action("focus", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var options = new PageFocusOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(1);
            Bridge.Block(() => _page.FocusAsync(selector, options));
        }),

        "select_option" => Method("select_option", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var values = arguments.Strings(1, "value");
            var options = new PageSelectOptionOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Strict = arguments.Bool("strict"),
            };

            var chosen = Locators.SelectValues(arguments, values);
            arguments.Done(2);

            var selected = Bridge.Block(() => _page.SelectOptionAsync(selector, chosen, options));
            return Values.FromStrings(selected);
        }),

        "set_input_files" => Action("set_input_files", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var files = Locators.Payloads(Bridge, arguments, 1);
            var options = new PageSetInputFilesOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(2);
            Bridge.Block(() => _page.SetInputFilesAsync(selector, files, options));
        }),

        "text_content" => Method("text_content", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var options = new PageTextContentOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(1);
            return Values.FromOptional(Bridge.Block(() => _page.TextContentAsync(selector, options)));
        }),

        "inner_text" => Method("inner_text", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var options = new PageInnerTextOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(1);
            return new PyStr(Bridge.Block(() => _page.InnerTextAsync(selector, options)));
        }),

        "inner_html" => Method("inner_html", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var options = new PageInnerHTMLOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(1);
            return new PyStr(Bridge.Block(() => _page.InnerHTMLAsync(selector, options)));
        }),

        "input_value" => Method("input_value", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var options = new PageInputValueOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(1);
            return new PyStr(Bridge.Block(() => _page.InputValueAsync(selector, options)));
        }),

        "get_attribute" => Method("get_attribute", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var attribute = arguments.String(1, "name");
            var options = new PageGetAttributeOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(2);
            return Values.FromOptional(Bridge.Block(() => _page.GetAttributeAsync(selector, attribute, options)));
        }),

        "is_visible" => Predicate(name, (selector, timeout, strict) =>
            _page.IsVisibleAsync(selector, new PageIsVisibleOptions { Timeout = timeout, Strict = strict })),

        "is_hidden" => Predicate(name, (selector, timeout, strict) =>
            _page.IsHiddenAsync(selector, new PageIsHiddenOptions { Timeout = timeout, Strict = strict })),

        "is_enabled" => Predicate(name, (selector, timeout, strict) =>
            _page.IsEnabledAsync(selector, new PageIsEnabledOptions { Timeout = timeout, Strict = strict })),

        "is_disabled" => Predicate(name, (selector, timeout, strict) =>
            _page.IsDisabledAsync(selector, new PageIsDisabledOptions { Timeout = timeout, Strict = strict })),

        "is_checked" => Predicate(name, (selector, timeout, strict) =>
            _page.IsCheckedAsync(selector, new PageIsCheckedOptions { Timeout = timeout, Strict = strict })),

        "is_editable" => Predicate(name, (selector, timeout, strict) =>
            _page.IsEditableAsync(selector, new PageIsEditableOptions { Timeout = timeout, Strict = strict })),

        "evaluate" => Method("evaluate", arguments =>
        {
            var expression = arguments.String(0, "expression");
            var argument = Values.ToScript(arguments.Value(1, "arg"), arguments.Name);
            arguments.Done(2);

            return Values.FromScript(Bridge.Block(() => _page.EvaluateAsync<JsonElement?>(expression, argument)));
        }),

        "eval_on_selector" => Method("eval_on_selector", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var expression = arguments.String(1, "expression");
            var argument = Values.ToScript(arguments.Value(2, "arg"), arguments.Name);
            var strict = arguments.Bool("strict");
            arguments.Done(3);

            return Values.FromScript(Bridge.Block(() => _page.EvalOnSelectorAsync<JsonElement?>(
                selector, expression, argument, new PageEvalOnSelectorOptions { Strict = strict })));
        }),

        "eval_on_selector_all" => Method("eval_on_selector_all", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var expression = arguments.String(1, "expression");
            var argument = Values.ToScript(arguments.Value(2, "arg"), arguments.Name);
            arguments.Done(3);

            return Values.FromScript(Bridge.Block(() => _page.EvalOnSelectorAllAsync<JsonElement?>(
                selector, expression, argument)));
        }),

        "screenshot" => Method("screenshot", Screenshot),
        "pdf" => Method("pdf", Pdf),

        "aria_snapshot" => Method("aria_snapshot", arguments =>
        {
            var timeout = Bridge.Timeout(arguments.Timeout());
            arguments.Done(0);

            return new PyStr(Bridge.Block(() => _page.AriaSnapshotAsync(
                new PageAriaSnapshotOptions { Timeout = timeout })));
        }),

        "set_viewport_size" => Action("set_viewport_size", arguments =>
        {
            var size = arguments.Value(0, "viewport_size")
                ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: 'viewport_size'"));

            arguments.Done(1);

            if (size is not PyDict dictionary
                || !dictionary.TryGetValue(new PyStr("width"), out var width)
                || !dictionary.TryGetValue(new PyStr("height"), out var height)
                || width is not PyInt w || height is not PyInt h)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{arguments.Name}: 'viewport_size' must be a dict with int 'width' and 'height'"));
            }

            Bridge.Block(() => _page.SetViewportSizeAsync(w.ToIndex(), h.ToIndex()));
        }),

        "set_default_timeout" => Action("set_default_timeout", arguments =>
        {
            var timeout = arguments.Value(0, "timeout");
            arguments.Done(1);
            _page.SetDefaultTimeout(Ceiling(timeout, arguments.Name));
        }),

        "set_default_navigation_timeout" => Action("set_default_navigation_timeout", arguments =>
        {
            var timeout = arguments.Value(0, "timeout");
            arguments.Done(1);
            _page.SetDefaultNavigationTimeout(Ceiling(timeout, arguments.Name));
        }),

        "set_extra_http_headers" => Action("set_extra_http_headers", arguments =>
        {
            var headers = arguments.Headers("headers")
                ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: 'headers'"));

            arguments.Done(1);
            Bridge.Block(() => _page.SetExtraHTTPHeadersAsync(headers));
        }),

        "add_init_script" => Action("add_init_script", arguments =>
        {
            var script = arguments.OptionalString(0, "script");
            var path = arguments.String("path");
            arguments.Done(1);

            var source = path is not null
                ? Bridge.ReadText(path)
                : script ?? throw new PyRaise(PyErrors.TypeError(
                    $"{arguments.Name}() needs either 'script' or 'path'"));

            Bridge.Block(() => _page.AddInitScriptAsync(source));
        }),

        "add_script_tag" => Method("add_script_tag", arguments =>
        {
            var url = arguments.String("url");
            var path = arguments.String("path");
            var content = arguments.String("content");
            var type = arguments.String("type");
            arguments.Done(0);

            if (url is not null)
            {
                Bridge.RequireAllowed(url);
            }

            var handle = Bridge.Block(() => _page.AddScriptTagAsync(new PageAddScriptTagOptions
            {
                Url = url,
                Content = path is not null ? Bridge.ReadText(path) : content,
                Type = type,
            }));

            return new PyElementHandle(Bridge, handle);
        }),

        "add_style_tag" => Method("add_style_tag", arguments =>
        {
            var url = arguments.String("url");
            var path = arguments.String("path");
            var content = arguments.String("content");
            arguments.Done(0);

            if (url is not null)
            {
                Bridge.RequireAllowed(url);
            }

            var handle = Bridge.Block(() => _page.AddStyleTagAsync(new PageAddStyleTagOptions
            {
                Url = url,
                Content = path is not null ? Bridge.ReadText(path) : content,
            }));

            return new PyElementHandle(Bridge, handle);
        }),

        "emulate_media" => Action("emulate_media", arguments =>
        {
            var options = new PageEmulateMediaOptions
            {
                Media = Enums.Parse<Media>(arguments.String("media"), arguments.Name, "media"),
                ColorScheme = Enums.Parse<ColorScheme>(arguments.String("color_scheme"), arguments.Name, "color_scheme"),
                ReducedMotion = Enums.Parse<ReducedMotion>(arguments.String("reduced_motion"), arguments.Name, "reduced_motion"),
                ForcedColors = Enums.Parse<ForcedColors>(arguments.String("forced_colors"), arguments.Name, "forced_colors"),
            };

            arguments.Done(0);
            Bridge.Block(() => _page.EmulateMediaAsync(options));
        }),

        "dispatch_event" => Action("dispatch_event", arguments =>
        {
            var selector = arguments.String(0, "selector");
            var type = arguments.String(1, "type");
            var init = Values.ToScript(arguments.Value(2, "event_init"), arguments.Name);
            var options = new PageDispatchEventOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Strict = arguments.Bool("strict"),
            };

            arguments.Done(3);
            Bridge.Block(() => _page.DispatchEventAsync(selector, type, init, options));
        }),

        "drag_and_drop" => Action("drag_and_drop", arguments =>
        {
            var source = arguments.String(0, "source");
            var target = arguments.String(1, "target");
            var options = new PageDragAndDropOptions
            {
                Timeout = Bridge.Timeout(arguments.Timeout()),
                Force = arguments.Bool("force"),
                Strict = arguments.Bool("strict"),
                Trial = arguments.Bool("trial"),
                SourcePosition = Locators.Source(arguments),
                TargetPosition = Locators.Target(arguments),
            };

            arguments.Done(2);
            Bridge.Block(() => _page.DragAndDropAsync(source, target, options));
        }),

        "bring_to_front" => Action("bring_to_front", arguments =>
        {
            arguments.Done(0);
            Bridge.Block(() => _page.BringToFrontAsync());
        }),

        // The pull-based half of Playwright's event API, and the half a synchronous
        // interpreter can honour: the browser records these and the script asks for them,
        // rather than the browser calling into a VM that is busy.
        "console_messages" => Method("console_messages", arguments =>
        {
            var filter = arguments.String("filter");
            arguments.Done(0);

            var messages = Bridge.Block(() => _page.ConsoleMessagesAsync(new PageConsoleMessagesOptions
            {
                Filter = Enums.Parse<ConsoleMessagesFilter>(filter, arguments.Name, "filter"),
            }));

            return new PyList([.. messages.Select(message => new PyConsoleMessage(Bridge, message))]);
        }),

        "page_errors" => Method("page_errors", arguments =>
        {
            arguments.Done(0);

            var errors = Bridge.Block(() => _page.PageErrorsAsync());
            return new PyList([.. errors.Select(static error => new PyException(Errors.Error, error))]);
        }),

        "requests" => Method("requests", arguments =>
        {
            arguments.Done(0);

            var requests = Bridge.Block(() => _page.RequestsAsync());
            return new PyList([.. requests.Select(request => new PyRequest(Bridge, request))]);
        }),

        "clear_console_messages" => Action("clear_console_messages", arguments =>
        {
            arguments.Done(0);
            Bridge.Block(() => _page.ClearConsoleMessagesAsync());
        }),

        "clear_page_errors" => Action("clear_page_errors", arguments =>
        {
            arguments.Done(0);
            Bridge.Block(() => _page.ClearPageErrorsAsync());
        }),

        "on" or "once" or "remove_listener" or "expect_event" or "wait_for_event" =>
            Unsupported(
                name,
                "the interpreter is synchronous, so nothing can call into it while a script "
                + "is blocked. Use page.console_messages(), page.requests() or page.page_errors(), "
                + "which the browser records for you"),

        "route" or "unroute" or "unroute_all" or "route_from_har" or "route_web_socket" =>
            Unsupported(name, "request filtering is the host's, and it is already installed"),

        "expose_function" or "expose_binding" or "pause" or "add_locator_handler" =>
            Unsupported(name, "it would call back into the interpreter from the browser's thread"),

        _ => null,
    };

    private PyObject Goto(Arguments arguments)
    {
        var url = arguments.String(0, "url");
        var timeout = Bridge.Timeout(arguments.Timeout());
        var waitUntil = Enums.Parse<WaitUntilState>(arguments.String("wait_until"), arguments.Name, "wait_until");
        var referer = arguments.String("referer");
        arguments.Done(1);

        // Checked here for the error message — the request filter on the context is what
        // actually enforces it, and it cannot tell a script which line to look at.
        Bridge.RequireAllowed(url);

        var response = Bridge.Block(() => _page.GotoAsync(url, new PageGotoOptions
        {
            Timeout = timeout,
            WaitUntil = waitUntil,
            Referer = referer,
        }));

        return response is null ? PyNone.Instance : new PyResponse(Bridge, response);
    }

    private PyObject Navigate(Arguments arguments, Func<float?, WaitUntilState?, Task<IResponse?>> work)
    {
        var timeout = Bridge.Timeout(arguments.Timeout());
        var waitUntil = Enums.Parse<WaitUntilState>(arguments.String("wait_until"), arguments.Name, "wait_until");
        arguments.Done(0);

        var response = Bridge.Block(() => work(timeout, waitUntil));
        return response is null ? PyNone.Instance : new PyResponse(Bridge, response);
    }

    private void Click(Arguments arguments, int clicks)
    {
        var selector = arguments.String(0, "selector");
        var timeout = Bridge.Timeout(arguments.Timeout());
        var button = Enums.Parse<MouseButton>(arguments.String("button"), arguments.Name, "button");
        var delay = (float?)arguments.Number("delay");
        var force = arguments.Bool("force");
        // Deprecated to a no-op in the driver: read so it stays an accepted
        // keyword, and not forwarded, because forwarding it is now an error.
        arguments.Bool("no_wait_after");
        var strict = arguments.Bool("strict");
        var trial = arguments.Bool("trial");
        var position = Locators.Position(arguments);
        var modifiers = Locators.Modifiers(arguments);
        var count = arguments.Integer("click_count");
        arguments.Done(1);

        if (clicks == 2)
        {
            Bridge.Block(() => _page.DblClickAsync(selector, new PageDblClickOptions
            {
                Timeout = timeout,
                Button = button,
                Delay = delay,
                Force = force,
                Strict = strict,
                Trial = trial,
                Position = position,
                Modifiers = modifiers,
            }));

            return;
        }

        Bridge.Block(() => _page.ClickAsync(selector, new PageClickOptions
        {
            Timeout = timeout,
            Button = button,
            Delay = delay,
            ClickCount = count,
            Force = force,
            Strict = strict,
            Trial = trial,
            Position = position,
            Modifiers = modifiers,
        }));
    }

    private PyObject Predicate(string name, Func<string, float?, bool?, Task<bool>> work) =>
        Method(name, arguments =>
        {
            var selector = arguments.String(0, "selector");
            var timeout = Bridge.Timeout(arguments.Timeout());
            var strict = arguments.Bool("strict");
            arguments.Done(1);

            return PyBool.Of(Bridge.Block(() => work(selector, timeout, strict)));
        });

    private PyObject Screenshot(Arguments arguments)
    {
        var path = arguments.String("path");
        var options = new PageScreenshotOptions
        {
            Timeout = Bridge.Timeout(arguments.Timeout()),
            FullPage = arguments.Bool("full_page"),
            OmitBackground = arguments.Bool("omit_background"),
            Quality = arguments.Integer("quality"),
            Scale = Enums.Parse<ScreenshotScale>(arguments.String("scale"), arguments.Name, "scale"),
            Caret = Enums.Parse<ScreenshotCaret>(arguments.String("caret"), arguments.Name, "caret"),
            Animations = Enums.Parse<ScreenshotAnimations>(arguments.String("animations"), arguments.Name, "animations"),
            Type = Enums.Parse<ScreenshotType>(arguments.String("type"), arguments.Name, "type"),
            Style = arguments.String("style"),
        };

        if (arguments.Keyword("clip") is { } clip)
        {
            options.Clip = Locators.Clip(clip, arguments.Name);
        }

        arguments.Done(0);

        // Never `options.Path`: the driver would write it with the host's own file API, to
        // a path the sandbox has no say over. The bytes come back and go out through the
        // run's filesystem instead.
        var bytes = Bridge.Block(() => _page.ScreenshotAsync(options));

        if (path is not null)
        {
            Bridge.Write(path, bytes);
        }

        return new PyBytes(bytes);
    }

    private PyObject Pdf(Arguments arguments)
    {
        var path = arguments.String("path");
        var options = new PagePdfOptions
        {
            Scale = (float?)arguments.Number("scale"),
            DisplayHeaderFooter = arguments.Bool("display_header_footer"),
            HeaderTemplate = arguments.String("header_template"),
            FooterTemplate = arguments.String("footer_template"),
            PrintBackground = arguments.Bool("print_background"),
            Landscape = arguments.Bool("landscape"),
            PageRanges = arguments.String("page_ranges"),
            Format = arguments.String("format"),
            Width = arguments.String("width"),
            Height = arguments.String("height"),
            PreferCSSPageSize = arguments.Bool("prefer_css_page_size"),
            Outline = arguments.Bool("outline"),
            Tagged = arguments.Bool("tagged"),
        };

        arguments.Done(0);

        var bytes = Bridge.Block(() => _page.PdfAsync(options));

        if (path is not null)
        {
            Bridge.Write(path, bytes);
        }

        return new PyBytes(bytes);
    }

    private PyObject Viewport()
    {
        if (_page.ViewportSize is not { } size)
        {
            return PyNone.Instance;
        }

        var viewport = new PyDict();
        viewport.Set(new PyStr("width"), PyInt.From(size.Width));
        viewport.Set(new PyStr("height"), PyInt.From(size.Height));
        return viewport;
    }

    private float Ceiling(PyObject? value, string where)
    {
        var milliseconds = value switch
        {
            PyInt integer => (float)integer.Value,
            PyFloat number => (float)number.Value,
            null => throw new PyRaise(PyErrors.TypeError($"{where}() missing required argument: 'timeout'")),
            _ => throw new PyRaise(PyErrors.TypeError(
                $"{where}: 'timeout' must be a number, not '{value.TypeName}'")),
        };

        // A script may shorten its own patience and never lengthen it past the host's.
        return Math.Min(milliseconds, Bridge.Options.DefaultTimeout);
    }
}

/// <summary>The <c>Keyboard</c>: whole-page key input, unattached to any element.</summary>
internal sealed class PyKeyboard : PlaywrightObject
{
    private readonly IKeyboard _keyboard;

    public PyKeyboard(Bridge bridge, IKeyboard keyboard) : base(bridge) => _keyboard = keyboard;

    public override string TypeName => "Keyboard";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "press" => Action("press", arguments =>
        {
            var key = arguments.String(0, "key");
            var delay = (float?)arguments.Number("delay");
            arguments.Done(1);

            Bridge.Block(() => _keyboard.PressAsync(key, new KeyboardPressOptions { Delay = delay }));
        }),

        "type" => Action("type", arguments =>
        {
            var text = arguments.String(0, "text");
            var delay = (float?)arguments.Number("delay");
            arguments.Done(1);

            Bridge.Block(() => _keyboard.TypeAsync(text, new KeyboardTypeOptions { Delay = delay }));
        }),

        "insert_text" => Action("insert_text", arguments =>
        {
            var text = arguments.String(0, "text");
            arguments.Done(1);
            Bridge.Block(() => _keyboard.InsertTextAsync(text));
        }),

        "down" => Action("down", arguments =>
        {
            var key = arguments.String(0, "key");
            arguments.Done(1);
            Bridge.Block(() => _keyboard.DownAsync(key));
        }),

        "up" => Action("up", arguments =>
        {
            var key = arguments.String(0, "key");
            arguments.Done(1);
            Bridge.Block(() => _keyboard.UpAsync(key));
        }),

        _ => null,
    };
}

/// <summary>The <c>Mouse</c>: input by coordinate, for what no selector can express.</summary>
internal sealed class PyMouse : PlaywrightObject
{
    private readonly IMouse _mouse;

    public PyMouse(Bridge bridge, IMouse mouse) : base(bridge) => _mouse = mouse;

    public override string TypeName => "Mouse";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "move" => Action("move", arguments =>
        {
            var (x, y) = Coordinates(arguments);
            var steps = arguments.Integer("steps");
            arguments.Done(2);

            Bridge.Block(() => _mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = steps }));
        }),

        "click" => Action("click", arguments =>
        {
            var (x, y) = Coordinates(arguments);
            var options = new MouseClickOptions
            {
                Button = Enums.Parse<MouseButton>(arguments.String("button"), arguments.Name, "button"),
                Delay = (float?)arguments.Number("delay"),
                ClickCount = arguments.Integer("click_count"),
            };

            arguments.Done(2);
            Bridge.Block(() => _mouse.ClickAsync(x, y, options));
        }),

        "dblclick" => Action("dblclick", arguments =>
        {
            var (x, y) = Coordinates(arguments);
            var options = new MouseDblClickOptions
            {
                Button = Enums.Parse<MouseButton>(arguments.String("button"), arguments.Name, "button"),
                Delay = (float?)arguments.Number("delay"),
            };

            arguments.Done(2);
            Bridge.Block(() => _mouse.DblClickAsync(x, y, options));
        }),

        "down" => Action("down", arguments =>
        {
            var options = new MouseDownOptions
            {
                Button = Enums.Parse<MouseButton>(arguments.String("button"), arguments.Name, "button"),
                ClickCount = arguments.Integer("click_count"),
            };

            arguments.Done(0);
            Bridge.Block(() => _mouse.DownAsync(options));
        }),

        "up" => Action("up", arguments =>
        {
            var options = new MouseUpOptions
            {
                Button = Enums.Parse<MouseButton>(arguments.String("button"), arguments.Name, "button"),
                ClickCount = arguments.Integer("click_count"),
            };

            arguments.Done(0);
            Bridge.Block(() => _mouse.UpAsync(options));
        }),

        "wheel" => Action("wheel", arguments =>
        {
            var deltaX = Number(arguments.Value(0, "delta_x"), arguments.Name, "delta_x");
            var deltaY = Number(arguments.Value(1, "delta_y"), arguments.Name, "delta_y");
            arguments.Done(2);

            Bridge.Block(() => _mouse.WheelAsync(deltaX, deltaY));
        }),

        _ => null,
    };

    private static (float X, float Y) Coordinates(Arguments arguments) =>
        (Number(arguments.Value(0, "x"), arguments.Name, "x"), Number(arguments.Value(1, "y"), arguments.Name, "y"));

    private static float Number(PyObject? value, string where, string name) => value switch
    {
        PyInt integer => (float)integer.Value,
        PyFloat number => (float)number.Value,
        null => throw new PyRaise(PyErrors.TypeError($"{where}() missing required argument: '{name}'")),
        _ => throw new PyRaise(PyErrors.TypeError($"{where}: '{name}' must be a number, not '{value.TypeName}'")),
    };
}
