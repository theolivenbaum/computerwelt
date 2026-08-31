using Microsoft.Playwright;

namespace Computerwelt.Playwright;

/// <summary>
/// The browser backend a sandbox borrows.
/// </summary>
/// <remarks>
/// <para>
/// The session is host-owned and lives outside the sandbox: it starts the Playwright driver,
/// launches browsers and holds them, and every script that imports <c>playwright</c> is
/// handed a view onto what is already running. Nothing a script does starts a process, and
/// nothing a script does can outlive <see cref="DisposeAsync"/>.
/// </para>
/// <para>
/// One session is meant to be shared. Browsers are expensive and a browser <i>context</i> is
/// the unit Playwright isolates on — separate cookies, storage and cache — so two tenants
/// sharing this session still share nothing observable, exactly as two <c>Bash</c> instances
/// over one <see cref="PlaywrightSession"/> would.
/// </para>
/// </remarks>
public sealed class PlaywrightSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IBrowser> _browsers = new(StringComparer.Ordinal);
    private readonly List<IBrowserContext> _contexts = [];
    private IPlaywright? _playwright;
    private bool _disposed;

    private PlaywrightSession(IPlaywright playwright, PlaywrightOptions options)
    {
        _playwright = playwright;
        Options = options;
        Policy = new NavigationPolicy(options);
    }

    /// <summary>The host's configuration, which a script cannot widen.</summary>
    public PlaywrightOptions Options { get; }

    /// <summary>What the browser is allowed to reach.</summary>
    public NavigationPolicy Policy { get; }

    /// <summary>How many browser contexts are open across every script using this session.</summary>
    public int OpenContexts
    {
        get
        {
            lock (_contexts)
            {
                return _contexts.Count;
            }
        }
    }

    /// <summary>
    /// Starts the Playwright driver and returns a session over it.
    /// </summary>
    /// <remarks>
    /// No browser is launched here. The first script that asks for one pays for it, and a
    /// session nobody uses costs a driver process and nothing more.
    /// </remarks>
    /// <param name="options">The host's configuration, or null for the defaults — which reach no hosts at all.</param>
    /// <param name="cancellationToken">Abandons the wait for the driver to come up.</param>
    public static async Task<PlaywrightSession> CreateAsync(
        PlaywrightOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);

        return new PlaywrightSession(playwright, options ?? new PlaywrightOptions());
    }

    /// <summary>
    /// The device profiles Playwright ships, for <c>p.devices['iPhone 13']</c>.
    /// </summary>
    /// <remarks>
    /// A table of constants the driver already has, so it costs nothing and needs no browser.
    /// </remarks>
    public IReadOnlyDictionary<string, BrowserNewContextOptions> Devices =>
        _playwright?.Devices ?? throw new ObjectDisposedException(nameof(PlaywrightSession));

    /// <summary>The names a script may pass to <c>sync_playwright()</c>'s browser types.</summary>
    public IEnumerable<string> BrowserNames => Options.Browsers.Select(PlaywrightOptions.NameOf);

    /// <summary>Whether <paramref name="name"/> is a browser this session may launch.</summary>
    public bool Allows(string name) => BrowserNames.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// The browser of that name, launched on first use and shared thereafter.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The host did not list this browser, so it is not launchable at all — which is
    /// reported rather than silently substituted.
    /// </exception>
    public async Task<IBrowser> GetBrowserAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Allows(name))
        {
            throw new InvalidOperationException(
                $"'{name}' is not available: this sandbox was configured with "
                + $"[{string.Join(", ", BrowserNames)}]. Add it to PlaywrightOptions.Browsers.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_browsers.TryGetValue(name, out var existing) && existing.IsConnected)
            {
                return existing;
            }

            var playwright = _playwright ?? throw new ObjectDisposedException(nameof(PlaywrightSession));

            var type = name switch
            {
                "firefox" => playwright.Firefox,
                "webkit" => playwright.Webkit,
                _ => playwright.Chromium,
            };

            var browser = await type.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = Options.Headless,
                Args = Options.LaunchArguments.Count > 0 ? Options.LaunchArguments : null,
                Channel = Options.Channel,
                ExecutablePath = Options.ExecutablePath,
                Timeout = Options.LaunchTimeout,
            }).ConfigureAwait(false);

            _browsers[name] = browser;
            return browser;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens a browser context under the session's caps and its navigation policy.
    /// </summary>
    /// <remarks>
    /// Every context the sandbox uses comes through here, which is what makes the two
    /// guarantees hold for all of them: the cap on how many may exist, and the request
    /// filter that refuses anything the host did not allow — including the requests a page
    /// makes on its own, which no check on <c>page.goto</c> would ever see.
    /// </remarks>
    public async Task<IBrowserContext> NewContextAsync(
        IBrowser browser,
        BrowserNewContextOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_contexts)
        {
            // Checked before the context exists, so the cap is a bound on what is open
            // rather than on what was asked for.
            if (_contexts.Count >= Options.MaxContexts)
            {
                throw new InvalidOperationException(
                    $"this sandbox already has {_contexts.Count} browser contexts open, which is "
                    + "PlaywrightOptions.MaxContexts. Close one, or raise the cap.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Options.Viewport is { } viewport)
        {
            options.ViewportSize = new ViewportSize { Width = viewport.Width, Height = viewport.Height };
        }

        var context = await browser.NewContextAsync(options).ConfigureAwait(false);

        context.SetDefaultTimeout(Options.DefaultTimeout);
        context.SetDefaultNavigationTimeout(Options.DefaultTimeout);

        // The backstop the whole policy rests on: `page.goto` is checked for the sake of a
        // good error message, but *this* is what a redirect, an iframe, an image or a
        // `fetch()` the page issues has to get past.
        await context.RouteAsync("**/*", route =>
        {
            if (Policy.IsAllowed(route.Request.Url))
            {
                _ = route.ContinueAsync();
            }
            else
            {
                _ = route.AbortAsync("blockedbyclient");
            }
        }).ConfigureAwait(false);

        context.Close += OnContextClosed;

        lock (_contexts)
        {
            _contexts.Add(context);
        }

        return context;
    }

    /// <summary>Forgets a context the sandbox closed, so it stops counting against the cap.</summary>
    public void Release(IBrowserContext context)
    {
        lock (_contexts)
        {
            _contexts.Remove(context);
        }
    }

    private void OnContextClosed(object? sender, IBrowserContext context) => Release(context);

    /// <summary>
    /// Closes every context and browser this session opened, then stops the driver.
    /// </summary>
    /// <remarks>
    /// A script has no way to leave anything running past this point, which is the reason
    /// the session — and not the script — owns all of it.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        IBrowserContext[] contexts;

        lock (_contexts)
        {
            contexts = [.. _contexts];
            _contexts.Clear();
        }

        foreach (var context in contexts)
        {
            // A context whose browser already went away throws on close, and a failure to
            // tidy up must not hide the reason we are tidying up.
            try
            {
                await context.CloseAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
            }
        }

        foreach (var browser in _browsers.Values)
        {
            try
            {
                await browser.CloseAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
            }
        }

        _browsers.Clear();
        _playwright?.Dispose();
        _playwright = null;
        _gate.Dispose();
    }
}
