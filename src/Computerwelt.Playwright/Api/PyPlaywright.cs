using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Playwright.Interop;

namespace Computerwelt.Playwright.Api;

/// <summary>
/// What <c>sync_playwright()</c> returns: the context manager a program enters.
/// </summary>
/// <remarks>
/// <para>
/// Upstream this object starts the driver on <c>start()</c> and stops it on <c>stop()</c>.
/// Here the driver is the host's and was running before the program was parsed, so what is
/// started and stopped is the program's own claim on it: <c>stop()</c> closes the browser
/// contexts this run opened and leaves the browser itself alone, ready for the next run.
/// </para>
/// <para>
/// That is the difference worth knowing about this port, and it is also why the idiomatic
/// <c>with sync_playwright() as p:</c> is worth keeping: it is what returns a context to the
/// pool at the end of a script, instead of leaving it to the session's disposal.
/// </para>
/// </remarks>
internal sealed class PyPlaywrightContextManager : PlaywrightObject
{
    private PyPlaywright? _started;

    public PyPlaywrightContextManager(Bridge bridge) : base(bridge)
    {
    }

    public override string TypeName => "PlaywrightContextManager";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "start" or "__enter__" => Method(name, arguments =>
        {
            arguments.Done(0);
            return _started ??= new PyPlaywright(Bridge);
        }),

        "stop" or "__exit__" => Action(name, _ =>
        {
            // `__exit__` is handed the exception triple, and a `with` block that raised must
            // still get its contexts closed, so the arguments are accepted and ignored.
            _started?.Stop();
            _started = null;
        }),

        _ => null,
    };
}

/// <summary>The <c>Playwright</c> object: the browser types this sandbox may drive.</summary>
internal sealed class PyPlaywright : PlaywrightObject
{
    public PyPlaywright(Bridge bridge) : base(bridge)
    {
    }

    public override string TypeName => "Playwright";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "chromium" or "firefox" or "webkit" => new PyBrowserType(Bridge, name),

        // A device profile is a plain dict upstream, and the reason it is one is that
        // `browser.new_context(**p.devices['iPhone 13'])` is how it gets used.
        "devices" => Devices(),

        "stop" => Action("stop", arguments =>
        {
            arguments.Done(0);
            Stop();
        }),

        "selectors" => throw new PyRaise(PyErrors.AttributeError(
            TypeName,
            "selectors: a custom selector engine is host code, and this sandbox registers "
            + "host code before a script runs rather than from inside one")),

        "request" => throw new PyRaise(PyErrors.AttributeError(
            TypeName,
            "request: the sandbox's HTTP surface is the shell's allowlist, not Playwright's "
            + "APIRequestContext")),

        _ => null,
    };

    /// <summary>
    /// Closes what this run opened.
    /// </summary>
    /// <remarks>
    /// The browsers stay up: they belong to the <see cref="PlaywrightSession"/> and to every
    /// other tenant using it. A context is the thing this run created and the thing that
    /// holds its cookies, so it is the thing that goes.
    /// </remarks>
    public void Stop()
    {
        foreach (var context in Bridge.Contexts.ToArray())
        {
            context.CloseQuietly();
        }

        Bridge.Contexts.Clear();
    }

    private PyDict Devices()
    {
        var devices = new PyDict();

        foreach (var (deviceName, options) in Bridge.Session.Devices)
        {
            var entry = new PyDict();

            if (options.UserAgent is { } agent)
            {
                entry.Set(new PyStr("user_agent"), new PyStr(agent));
            }

            if (options.ViewportSize is { } viewport)
            {
                var size = new PyDict();
                size.Set(new PyStr("width"), PyInt.From(viewport.Width));
                size.Set(new PyStr("height"), PyInt.From(viewport.Height));
                entry.Set(new PyStr("viewport"), size);
            }

            if (options.DeviceScaleFactor is { } scale)
            {
                entry.Set(new PyStr("device_scale_factor"), new PyFloat(scale));
            }

            if (options.IsMobile is { } mobile)
            {
                entry.Set(new PyStr("is_mobile"), PyBool.Of(mobile));
            }

            if (options.HasTouch is { } touch)
            {
                entry.Set(new PyStr("has_touch"), PyBool.Of(touch));
            }

            devices.Set(new PyStr(deviceName), entry);
        }

        return devices;
    }
}

/// <summary>
/// A <c>BrowserType</c>: <c>p.chromium</c> and its two siblings.
/// </summary>
/// <remarks>
/// <c>launch()</c> does not launch anything. The browser was started by the host, before the
/// script existed, and this hands back a handle to it — which is the whole reason a script
/// running in this sandbox cannot start a process. Launch options a script passes are
/// therefore refused rather than ignored: they are decisions the host already made in
/// <see cref="PlaywrightOptions"/>, and pretending to honour them would be worse than
/// saying so.
/// </remarks>
internal sealed class PyBrowserType : PlaywrightObject
{
    private readonly string _name;

    public PyBrowserType(Bridge bridge, string name) : base(bridge) => _name = name;

    public override string TypeName => "BrowserType";

    public override string Repr() => $"<BrowserType name={_name}>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "name" => new PyStr(_name),

        "executable_path" => new PyStr(Bridge.Block(async () =>
        {
            var browser = await Bridge.Session.GetBrowserAsync(_name).ConfigureAwait(false);
            return browser.BrowserType.ExecutablePath;
        })),

        "launch" => Method("launch", Launch),

        "launch_persistent_context" => Unsupported(
            "launch_persistent_context",
            "it would write a browser profile to the host's disk, which nothing in this "
            + "sandbox is allowed to do"),

        "connect" or "connect_over_cdp" => Unsupported(
            name,
            "the browser this sandbox uses is the one the host launched; it does not dial out "
            + "to another"),

        _ => null,
    };

    private PyObject Launch(Arguments arguments)
    {
        // Read every option upstream takes, so that the refusal below can name the one the
        // script actually passed rather than reporting an unknown keyword.
        foreach (var fixedOption in new[]
                 {
                     "headless", "channel", "executable_path", "args", "ignore_default_args",
                     "handle_sigint", "handle_sigterm", "handle_sighup", "env", "proxy",
                     "downloads_path", "traces_dir", "chromium_sandbox", "firefox_user_prefs",
                     "devtools", "slow_mo", "timeout",
                 })
        {
            if (arguments.Keyword(fixedOption) is not null)
            {
                throw Errors.Fail(
                    $"launch(): '{fixedOption}' is fixed by the host. The browser is launched "
                    + "outside the sandbox from PlaywrightOptions, so a script cannot change how "
                    + "it starts.");
            }
        }

        arguments.Done(0);

        if (!Bridge.Session.Allows(_name))
        {
            throw Errors.Fail(
                $"'{_name}' is not available: this sandbox was configured with "
                + $"[{string.Join(", ", Bridge.Session.BrowserNames)}]. "
                + "Add it to PlaywrightOptions.Browsers.");
        }

        var browser = Bridge.Block(() => Bridge.Session.GetBrowserAsync(_name));

        return new PyBrowser(Bridge, browser);
    }
}
