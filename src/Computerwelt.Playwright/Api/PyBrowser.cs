using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Playwright.Interop;
using Microsoft.Playwright;

namespace Computerwelt.Playwright.Api;

/// <summary>
/// A <c>Browser</c>: the process the host launched, seen from inside the sandbox.
/// </summary>
/// <remarks>
/// <c>close()</c> closes what this run opened and leaves the browser running, because the
/// browser is shared and closing it would end every other tenant's session too. That is the
/// one place where this object deliberately does less than upstream's, and it does it
/// loudly rather than by omission — see the package README.
/// </remarks>
internal sealed class PyBrowser : PlaywrightObject
{
    private readonly IBrowser _browser;

    public PyBrowser(Bridge bridge, IBrowser browser) : base(bridge) => _browser = browser;

    public override string TypeName => "Browser";

    public override string Repr() => $"<Browser type={_browser.BrowserType.Name} version={_browser.Version}>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "version" => new PyStr(_browser.Version),
        "browser_type" => new PyBrowserType(Bridge, _browser.BrowserType.Name),

        // The run's contexts, not the browser's: another tenant's contexts are on the same
        // browser object and are none of this script's business.
        "contexts" => new PyList([.. Bridge.Contexts]),

        "is_connected" => Method("is_connected", arguments =>
        {
            arguments.Done(0);
            return PyBool.Of(_browser.IsConnected);
        }),

        "new_context" => Method("new_context", arguments => NewContext(arguments)),

        "new_page" => Method("new_page", arguments =>
        {
            var context = NewContext(arguments);
            return context.NewPage();
        }),

        "close" => Action("close", arguments =>
        {
            arguments.String("reason");
            arguments.Done(0);
            Bridge.ReleaseAll();
        }),

        "new_browser_cdp_session" or "start_tracing" or "stop_tracing" or "bind" or "unbind" =>
            Unsupported(name, "it reaches past the page into the browser the host owns"),

        _ => null,
    };

    /// <summary>
    /// Opens a context, translating every option a script may set.
    /// </summary>
    /// <remarks>
    /// The options that would put a file on the host's disk — a video directory, a HAR
    /// path — are refused by name rather than dropped, and so is a proxy, which would route
    /// around the host's allowlist entirely.
    /// </remarks>
    private PyBrowserContext NewContext(Arguments arguments)
    {
        var options = new BrowserNewContextOptions
        {
            UserAgent = arguments.String("user_agent"),
            Locale = arguments.String("locale"),
            TimezoneId = arguments.String("timezone_id"),
            BaseURL = arguments.String("base_url"),
            IsMobile = arguments.Bool("is_mobile"),
            HasTouch = arguments.Bool("has_touch"),
            JavaScriptEnabled = arguments.Bool("java_script_enabled"),
            BypassCSP = arguments.Bool("bypass_csp"),
            IgnoreHTTPSErrors = arguments.Bool("ignore_https_errors"),
            Offline = arguments.Bool("offline"),
            StrictSelectors = arguments.Bool("strict_selectors"),
            DeviceScaleFactor = (float?)arguments.Number("device_scale_factor"),
            ColorScheme = Enums.Parse<ColorScheme>(arguments.String("color_scheme"), arguments.Name, "color_scheme"),
            ReducedMotion = Enums.Parse<ReducedMotion>(arguments.String("reduced_motion"), arguments.Name, "reduced_motion"),
            ForcedColors = Enums.Parse<ForcedColors>(arguments.String("forced_colors"), arguments.Name, "forced_colors"),
            ServiceWorkers = Enums.Parse<ServiceWorkerPolicy>(arguments.String("service_workers"), arguments.Name, "service_workers"),
        };

        if (arguments.Strings("permissions") is { } permissions)
        {
            options.Permissions = permissions;
        }

        if (arguments.Headers("extra_http_headers") is { } headers)
        {
            options.ExtraHTTPHeaders = headers;
        }

        if (arguments.Keyword("viewport") is { } viewport)
        {
            options.ViewportSize = Size(viewport, arguments.Name, "viewport");
        }

        if (arguments.Keyword("screen") is { } screen)
        {
            var size = Size(screen, arguments.Name, "screen");
            options.ScreenSize = new ScreenSize { Width = size.Width, Height = size.Height };
        }

        if (arguments.Bool("no_viewport") is true)
        {
            options.ViewportSize = ViewportSize.NoViewport;
        }

        if (arguments.Keyword("storage_state") is { } state)
        {
            // A path here names a file in the sandbox, so it is read through the run's
            // filesystem and handed over as text — the driver never sees a path it could
            // open on the host.
            options.StorageState = state switch
            {
                PyStr path => Bridge.ReadText(path.Value),
                PyDict => Serialize(state),
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"{arguments.Name}: 'storage_state' must be a dict or a path, not '{state.TypeName}'")),
            };
        }

        // Accepted and ignored, exactly as upstream does, so that
        // `new_context(**p.devices['iPhone 13'])` works unchanged.
        arguments.Keyword("default_browser_type");

        foreach (var refused in new[]
                 {
                     "record_video_dir", "record_video_size", "record_har_path", "record_har_content",
                     "record_har_mode", "record_har_omit_content", "record_har_url_filter",
                     "downloads_path", "traces_dir",
                 })
        {
            if (arguments.Keyword(refused) is not null)
            {
                throw Errors.Fail(
                    $"new_context(): '{refused}' writes to the host's filesystem, which this "
                    + "sandbox has no route to. Capture what you need through the page instead.");
            }
        }

        if (arguments.Keyword("proxy") is not null)
        {
            throw Errors.Fail(
                "new_context(): 'proxy' would route requests around PlaywrightOptions.AllowedHosts, "
                + "so the host sets it or nobody does.");
        }

        if (arguments.Keyword("accept_downloads") is not null)
        {
            throw Errors.Fail(
                "new_context(): 'accept_downloads' saves files to the host's filesystem. "
                + "Read the response body instead.");
        }

        arguments.Done(0);

        var context = Bridge.Block(() => Bridge.Session.NewContextAsync(_browser, options));
        var wrapper = new PyBrowserContext(Bridge, context);

        Bridge.Contexts.Add(wrapper);
        return wrapper;
    }

    private static ViewportSize Size(PyObject value, string where, string name)
    {
        if (value is not PyDict dictionary
            || !dictionary.TryGetValue(new PyStr("width"), out var width)
            || !dictionary.TryGetValue(new PyStr("height"), out var height)
            || width is not PyInt w
            || height is not PyInt h)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{where}: '{name}' must be a dict with int 'width' and 'height'"));
        }

        return new ViewportSize { Width = w.ToIndex(), Height = h.ToIndex() };
    }

    private static string Serialize(PyObject value) =>
        System.Text.Json.JsonSerializer.Serialize(Values.ToScript(value, "storage_state"));
}

/// <summary>
/// A <c>BrowserContext</c>: one isolated profile, and the unit two tenants are separated by.
/// </summary>
internal sealed class PyBrowserContext : PlaywrightObject
{
    private readonly IBrowserContext _context;
    private readonly List<PyPage> _pages = [];
    private bool _closed;

    public PyBrowserContext(Bridge bridge, IBrowserContext context) : base(bridge) => _context = context;

    public override string TypeName => "BrowserContext";

    /// <summary>The driver object, for the page that needs to reach its own context.</summary>
    public IBrowserContext Context => _context;

    public override PyObject? GetAttribute(string name) => name switch
    {
        "pages" => new PyList([.. _pages]),

        "new_page" => Method("new_page", arguments =>
        {
            arguments.Done(0);
            return NewPage();
        }),

        "close" => Action("close", arguments =>
        {
            arguments.String("reason");
            arguments.Done(0);
            Close();
        }),

        "is_closed" => Method("is_closed", arguments =>
        {
            arguments.Done(0);
            return PyBool.Of(_closed);
        }),

        "set_default_timeout" => Action("set_default_timeout", arguments =>
        {
            var timeout = arguments.Value(0, "timeout");
            arguments.Done(1);
            _context.SetDefaultTimeout(Milliseconds(timeout, arguments.Name));
        }),

        "set_default_navigation_timeout" => Action("set_default_navigation_timeout", arguments =>
        {
            var timeout = arguments.Value(0, "timeout");
            arguments.Done(1);
            _context.SetDefaultNavigationTimeout(Milliseconds(timeout, arguments.Name));
        }),

        "set_extra_http_headers" => Action("set_extra_http_headers", arguments =>
        {
            var headers = arguments.Headers("headers")
                ?? HeadersAt(arguments)
                ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: 'headers'"));

            arguments.Done(1);
            Bridge.Block(() => _context.SetExtraHTTPHeadersAsync(headers));
        }),

        "set_offline" => Action("set_offline", arguments =>
        {
            var offline = arguments.RequiredBool(0, "offline");
            arguments.Done(1);
            Bridge.Block(() => _context.SetOfflineAsync(offline));
        }),

        "cookies" => Method("cookies", arguments =>
        {
            var urls = arguments.Strings(0, "urls");
            arguments.Done(1);

            var cookies = Bridge.Block(() => urls is null
                ? _context.CookiesAsync()
                : _context.CookiesAsync(urls));
            return new PyList([.. cookies.Select(FromCookie)]);
        }),

        "add_cookies" => Action("add_cookies", arguments =>
        {
            var value = arguments.Value(0, "cookies")
                ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: 'cookies'"));

            arguments.Done(1);

            var cookies = (value.Iterate() ?? throw new PyRaise(PyErrors.TypeError(
                    $"{arguments.Name}: 'cookies' must be a sequence of dicts")))
                .Select(item => ToCookie(item, arguments.Name))
                .ToList();

            Bridge.Block(() => _context.AddCookiesAsync(cookies));
        }),

        "clear_cookies" => Action("clear_cookies", arguments =>
        {
            arguments.Done(0);
            Bridge.Block(() => _context.ClearCookiesAsync());
        }),

        "grant_permissions" => Action("grant_permissions", arguments =>
        {
            var permissions = arguments.Strings(0, "permissions")
                ?? throw new PyRaise(PyErrors.TypeError($"{arguments.Name}() missing required argument: 'permissions'"));
            var origin = arguments.String("origin");
            arguments.Done(1);

            Bridge.Block(() => _context.GrantPermissionsAsync(
                permissions,
                origin is null ? null : new BrowserContextGrantPermissionsOptions { Origin = origin }));
        }),

        "clear_permissions" => Action("clear_permissions", arguments =>
        {
            arguments.Done(0);
            Bridge.Block(() => _context.ClearPermissionsAsync());
        }),

        "add_init_script" => Action("add_init_script", arguments =>
        {
            var script = Script(arguments);
            arguments.Done(1);
            Bridge.Block(() => _context.AddInitScriptAsync(script));
        }),

        "storage_state" => Method("storage_state", arguments =>
        {
            var path = arguments.String("path");
            arguments.Done(0);

            // Asked for without a path, so the driver hands the JSON back here and the file,
            // if one was asked for, is written through the sandbox's filesystem.
            var state = Bridge.Block(() => _context.StorageStateAsync());

            if (path is not null)
            {
                Bridge.Write(path, System.Text.Encoding.UTF8.GetBytes(state));
            }

            return Values.FromJsonText(state, arguments.Name);
        }),

        "route" or "unroute" or "unroute_all" or "route_from_har" or "route_web_socket" =>
            Unsupported(name, "request filtering is the host's, and it is already installed"),

        "expose_function" or "expose_binding" =>
            Unsupported(name, "it would call back into the interpreter from the browser's thread"),

        "browser" => new PyBrowser(Bridge, _context.Browser
            ?? throw Errors.Fail("this context has no browser")),

        _ => null,
    };

    /// <summary>Opens a page under the context's page cap.</summary>
    public PyPage NewPage()
    {
        if (_closed)
        {
            throw Errors.Fail("this browser context has been closed");
        }

        if (_pages.Count >= Bridge.Options.MaxPagesPerContext)
        {
            throw Errors.Fail(
                $"this context already has {_pages.Count} pages open, which is "
                + "PlaywrightOptions.MaxPagesPerContext. Close one, or raise the cap.");
        }

        var page = Bridge.Block(() => _context.NewPageAsync());
        var wrapper = new PyPage(Bridge, page, this);

        _pages.Add(wrapper);
        return wrapper;
    }

    /// <summary>Forgets a page the script closed.</summary>
    public void Release(PyPage page) => _pages.Remove(page);

    /// <summary>Closes the context and gives its slot back to the session.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _pages.Clear();
        Bridge.Contexts.Remove(this);
        Bridge.Block(() => _context.CloseAsync());
        Bridge.Session.Release(_context);
    }

    /// <summary>
    /// Closes the context on the way out of a <c>with</c> block or a <c>stop()</c>.
    /// </summary>
    /// <remarks>
    /// Failures are swallowed here and only here: this runs while a script is already
    /// finishing, and a browser that went away first must not turn an orderly exit into a
    /// traceback about the tidying up.
    /// </remarks>
    public void CloseQuietly()
    {
        try
        {
            Close();
        }
        catch (PyRaise)
        {
        }
    }

    private static float Milliseconds(PyObject? value, string where) => value switch
    {
        PyInt integer => (float)integer.Value,
        PyFloat number => (float)number.Value,
        null => throw new PyRaise(PyErrors.TypeError($"{where}() missing required argument: 'timeout'")),
        _ => throw new PyRaise(PyErrors.TypeError($"{where}: 'timeout' must be a number, not '{value.TypeName}'")),
    };

    private static IReadOnlyDictionary<string, string>? HeadersAt(Arguments arguments)
    {
        if (arguments.Value(0, "headers") is not { } value)
        {
            return null;
        }

        if (value is not PyDict dictionary)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{arguments.Name}: 'headers' must be a dict, not '{value.TypeName}'"));
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, item) in dictionary.Entries)
        {
            headers[key.Display()] = item.Display();
        }

        return headers;
    }

    private string Script(Arguments arguments)
    {
        var source = arguments.OptionalString(0, "script");
        var path = arguments.String("path");

        if (path is not null)
        {
            // The path is the sandbox's, so an init script is a file the surrounding shell
            // wrote — not one the host happens to have.
            return Bridge.ReadText(path);
        }

        return source ?? throw new PyRaise(PyErrors.TypeError(
            $"{arguments.Name}() needs either 'script' or 'path'"));
    }

    private static PyDict FromCookie(BrowserContextCookiesResult cookie)
    {
        var entry = new PyDict();
        entry.Set(new PyStr("name"), new PyStr(cookie.Name));
        entry.Set(new PyStr("value"), new PyStr(cookie.Value));
        entry.Set(new PyStr("domain"), new PyStr(cookie.Domain));
        entry.Set(new PyStr("path"), new PyStr(cookie.Path));
        entry.Set(new PyStr("expires"), new PyFloat(cookie.Expires));
        entry.Set(new PyStr("http_only"), PyBool.Of(cookie.HttpOnly));
        entry.Set(new PyStr("secure"), PyBool.Of(cookie.Secure));
        entry.Set(new PyStr("same_site"), new PyStr(cookie.SameSite.ToString()));
        return entry;
    }

    private static Cookie ToCookie(PyObject value, string where)
    {
        if (value is not PyDict dictionary)
        {
            throw new PyRaise(PyErrors.TypeError($"{where}: a cookie must be a dict, not '{value.TypeName}'"));
        }

        string? Field(string key) =>
            dictionary.TryGetValue(new PyStr(key), out var item) && item is PyStr text ? text.Value : null;

        return new Cookie
        {
            Name = Field("name") ?? throw new PyRaise(PyErrors.ValueError($"{where}: a cookie needs a 'name'")),
            Value = Field("value") ?? throw new PyRaise(PyErrors.ValueError($"{where}: a cookie needs a 'value'")),
            Url = Field("url"),
            Domain = Field("domain"),
            Path = Field("path"),
            Expires = dictionary.TryGetValue(new PyStr("expires"), out var expires) && expires is PyFloat seconds
                ? (float)seconds.Value
                : null,
            HttpOnly = dictionary.TryGetValue(new PyStr("http_only"), out var httpOnly) ? httpOnly.IsTruthy() : null,
            Secure = dictionary.TryGetValue(new PyStr("secure"), out var secure) ? secure.IsTruthy() : null,
            SameSite = Enums.Parse<SameSiteAttribute>(Field("same_site"), where, "same_site"),
        };
    }
}
