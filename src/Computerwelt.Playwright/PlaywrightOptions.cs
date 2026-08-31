namespace Computerwelt.Playwright;

/// <summary>
/// Which browser a <see cref="PlaywrightSession"/> may drive.
/// </summary>
public enum PlaywrightBrowser
{
    /// <summary>Chromium.</summary>
    Chromium,

    /// <summary>Firefox.</summary>
    Firefox,

    /// <summary>WebKit.</summary>
    Webkit,
}

/// <summary>
/// How the host configures the browser behind the sandbox's <c>playwright</c> module.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is the <i>host's</i> decision and none of it can be widened from inside a
/// script. A browser type absent from <see cref="Browsers"/> is not launchable, a host
/// absent from <see cref="AllowedHosts"/> is not reachable, and the caps below are the
/// script's ceiling rather than its default.
/// </para>
/// <para>
/// This is the one place in this repository where a real operating-system process is
/// started, and it is started here — in host code, before any script runs — precisely so
/// that a script never can. See the package README for why the sandbox's other guarantees
/// still hold around it.
/// </para>
/// </remarks>
public sealed record PlaywrightOptions
{
    /// <summary>
    /// The browser types a script may launch, in the order <c>sync_playwright()</c> offers
    /// them.
    /// </summary>
    /// <remarks>
    /// Each is launched lazily, the first time a script asks for it, and shared by every
    /// script afterwards. Asking for one that is not listed raises
    /// <c>playwright.sync_api.Error</c> naming this setting, rather than quietly launching
    /// something else.
    /// </remarks>
    public IReadOnlyList<PlaywrightBrowser> Browsers { get; init; } = [PlaywrightBrowser.Chromium];

    /// <summary>Whether the browser runs without a visible window. Fixed by the host.</summary>
    public bool Headless { get; init; } = true;

    /// <summary>Extra command-line arguments for the browser process.</summary>
    public IReadOnlyList<string> LaunchArguments { get; init; } = [];

    /// <summary>The browser channel — <c>chrome</c>, <c>msedge</c> — or null for the bundled build.</summary>
    public string? Channel { get; init; }

    /// <summary>An explicit browser executable, or null to use the one Playwright installed.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Milliseconds each launch may take.</summary>
    public float LaunchTimeout { get; init; } = 60_000;

    /// <summary>
    /// Milliseconds an action or a navigation may take before Playwright gives up.
    /// </summary>
    /// <remarks>
    /// Applied to every context the sandbox creates. A script may lower it — with
    /// <c>page.set_default_timeout</c> or a per-call <c>timeout=</c> — but not raise it past
    /// this, because a script that waits forever is a script that has escaped the run's
    /// budget.
    /// </remarks>
    public float DefaultTimeout { get; init; } = 30_000;

    /// <summary>
    /// The hosts a page may reach, as patterns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty means <b>nothing is reachable</b> — the same posture the shell takes towards
    /// HTTP — so a sandbox nobody configured cannot browse the internet by accident. A
    /// pattern is a host name, optionally with a leading <c>*.</c> wildcard
    /// (<c>*.example.com</c>) and optionally with a port (<c>localhost:8080</c>); a single
    /// <c>*</c> allows everything, which is a decision worth writing down rather than
    /// stumbling into.
    /// </para>
    /// <para>
    /// The check runs twice, and deliberately: once on the navigation a script asked for, so
    /// the error names the URL it typed, and once on every request the page makes, so a
    /// redirect, an image, an iframe or a <c>fetch</c> the page performs cannot reach
    /// somewhere the script could not have navigated to itself.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>
    /// The URL schemes a page may load. <c>file:</c> is not among the defaults and adding it
    /// hands the browser the host's disk, which no other part of this sandbox can reach.
    /// </summary>
    public IReadOnlyList<string> AllowedSchemes { get; init; } = ["http", "https", "about", "data", "blob"];

    /// <summary>How many browser contexts one session may have open at a time.</summary>
    /// <remarks>
    /// A context is roughly a browser profile, and a script that opens them in a loop and
    /// never closes them would otherwise grow without bound. Reaching this raises rather
    /// than blocking.
    /// </remarks>
    public int MaxContexts { get; init; } = 16;

    /// <summary>How many pages one browser context may have open at a time.</summary>
    public int MaxPagesPerContext { get; init; } = 16;

    /// <summary>The largest screenshot, PDF or response body that may cross into the sandbox.</summary>
    /// <remarks>
    /// The sandbox's filesystem has a quota of its own, but a body is materialised in memory
    /// before it reaches the filesystem at all, so it needs a bound here too.
    /// </remarks>
    public int MaxTransferBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>
    /// A viewport applied to every context the sandbox creates, or null for Playwright's.
    /// </summary>
    /// <remarks>
    /// Set, it wins over a <c>viewport=</c> a script passes to <c>new_context</c> — it is
    /// how a host pins what a screenshot will look like. Left null, which is the default,
    /// the script decides and Playwright's own default applies.
    /// </remarks>
    public (int Width, int Height)? Viewport { get; init; }

    /// <summary>Turns a browser type into the name the Python API spells it with.</summary>
    internal static string NameOf(PlaywrightBrowser browser) => browser switch
    {
        PlaywrightBrowser.Firefox => "firefox",
        PlaywrightBrowser.Webkit => "webkit",
        _ => "chromium",
    };
}
